using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace DotCharter.Tests.Perf;

/// <summary>One thing a screen reader would trip over on one screen.</summary>
/// <param name="Screen">The swept view, or <c>View/HiddenRoot</c> for a gated screen inside it.</param>
/// <param name="Kind">One of <see cref="AccessibilityAudit.Unnamed" />, <see cref="AccessibilityAudit.Unreachable" />,
///     <see cref="AccessibilityAudit.UnnamedImage" />.</param>
/// <param name="ControlType">The UIA control type the peer reports.</param>
/// <param name="Where">The control's type and <c>x:Name</c>, or its nearest named ancestor, and its tooltip.</param>
public sealed record AccessibilityFinding(string Screen, string Kind, string ControlType, string Where);

/// <summary>What the automation walk of one swept surface saw.</summary>
public sealed record AccessibilityAuditResult(
    string Surface,
    IReadOnlyDictionary<string, int> InteractiveByScreen,
    IReadOnlyList<AccessibilityFinding> Findings)
{
    public int Interactive => InteractiveByScreen.Values.Sum();

    public int Count(string kind) => Findings.Count(f => f.Kind == kind);
}

/// <summary>
///     <b>What UIA — and so Narrator and NVDA — sees of a laid-out view (#311).</b> The runtime twin of
///     <c>UI/AccessibleNameTests</c>: that lint reads the MARKUP of each control, this walks the
///     AUTOMATION TREE Avalonia hands the platform (<see cref="ControlAutomationPeer.CreatePeerForElement" />,
///     <see cref="AutomationPeer.GetChildren" />), so it also sees rows realized from an <c>ItemTemplate</c>,
///     controls built in code and names that come out of a panel (<c>"Avalonia.Controls.StackPanel"</c>,
///     <c>Perf/ButtonAutomationNameUiPerfSmokeTests</c>). <see cref="UiSweepRunner" /> calls it once per surface,
///     after the hidden screens are realized; nothing here writes to the tree.
///     <para>
///         Three findings. An INTERACTIVE peer (button, toggle, slider, combo, list item, menu item, text box…)
///         whose name is empty or a CLR type name; an enabled interactive peer that is neither keyboard-focusable
///         nor inside a focusable ancestor; an <c>Image</c> peer with no name that is not marked decorative
///         (<c>AutomationProperties.AccessibilityView="Raw"</c>). A Raw interactive peer is out of the control
///         view a reader browses, so it is not graded either; a scroll bar's own buttons are skipped outright.
///     </para>
/// </summary>
public static class AccessibilityAudit
{
    public const string Unnamed = "unnamed";
    public const string Unreachable = "unreachable";
    public const string UnnamedImage = "unnamed-image";

    /// <summary>The peer types a user OPERATES. Text, groups, panes and images are announced, not used.</summary>
    private static readonly HashSet<AutomationControlType> InteractiveTypes =
    [
        AutomationControlType.Button, AutomationControlType.CheckBox, AutomationControlType.ComboBox,
        AutomationControlType.Edit, AutomationControlType.Hyperlink, AutomationControlType.ListItem,
        AutomationControlType.MenuItem, AutomationControlType.RadioButton, AutomationControlType.Slider,
        AutomationControlType.Spinner, AutomationControlType.SplitButton, AutomationControlType.TabItem,
        AutomationControlType.TreeItem
    ];

    /// <summary>A peer named after a type is as anonymous as one with no name: the reader says the type.</summary>
    private static readonly Regex TypeNameShape = new(@"^(Avalonia|DotCharter|System)\.[\w.`+\[\], ]+$",
        RegexOptions.CultureInvariant);

    /// <summary>A record's ToString — <c>"Row { Title = …, Artist = … }"</c> — is every member, read aloud.</summary>
    private static readonly Regex RecordShape = new(@"^\w+ \{ .* \}$", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static bool IsMeaningfulName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && !TypeNameShape.IsMatch(name.Trim()) && !RecordShape.IsMatch(name.Trim());

    /// <summary>
    ///     Walks <paramref name="view" /> and each realized hidden root. The roots go FIRST so a control inside a
    ///     gated screen is attributed to that screen (outermost root wins, as in the sweep's own inspection);
    ///     a control is reported once.
    /// </summary>
    public static AccessibilityAuditResult Audit(string surface, Control view, IReadOnlyList<Control> hiddenRoots)
    {
        var seen = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var interactive = new Dictionary<string, int>(StringComparer.Ordinal);
        var findings = new List<AccessibilityFinding>();

        foreach (var root in hiddenRoots)
        {
            var screen = $"{surface}/{(string.IsNullOrEmpty(root.Name) ? root.GetType().Name : root.Name)}";
            if (!seen.Add(root)) continue;
            Walk(ControlAutomationPeer.CreatePeerForElement(root), screen, root.Focusable, seen, interactive,
                findings);
        }

        seen.Add(view);
        Walk(ControlAutomationPeer.CreatePeerForElement(view), surface, view.Focusable, seen, interactive, findings);
        return new AccessibilityAuditResult(surface, interactive, findings);
    }

    private static void Walk(AutomationPeer parent, string screen, bool insideFocusable, HashSet<Control> seen,
        Dictionary<string, int> interactive, List<AccessibilityFinding> findings)
    {
        foreach (var peer in parent.GetChildren())
        {
            var owner = (peer as ControlAutomationPeer)?.Owner;
            if (owner != null && (!owner.IsVisible || !seen.Add(owner))) continue;

            // A scroll bar's line and page buttons are pointer affordances by design: the keyboard scrolls the
            // ScrollViewer itself. Every list in the app would otherwise report four "unreachable" buttons.
            if (owner?.TemplatedParent is ScrollBar) continue;

            var type = peer.GetAutomationControlType();
            var focusable = peer.IsKeyboardFocusable() || owner is { Focusable: true };

            // AccessibilityView="Raw" takes a peer out of UIA's CONTROL view, the one Narrator and NVDA
            // browse — the explicit "a reader never lands here". Its children are still walked.
            if (InteractiveTypes.Contains(type) && peer.IsControlElement())
            {
                interactive[screen] = interactive.GetValueOrDefault(screen) + 1;
                if (!IsMeaningfulName(peer.GetName()))
                    findings.Add(new AccessibilityFinding(screen, Unnamed, type.ToString(), Describe(owner, peer)));
                if (!focusable && !insideFocusable && owner is { IsEffectivelyEnabled: true })
                {
                    findings.Add(new AccessibilityFinding(screen, Unreachable, type.ToString(),
                        Describe(owner, peer)));
                }
            }
            else if (type == AutomationControlType.Image && peer.IsControlElement()
                     && (owner is null || AutomationProperties.GetAccessibilityView(owner) != AccessibilityView.Raw)
                     && !IsMeaningfulName(peer.GetName()))
            {
                findings.Add(new AccessibilityFinding(screen, UnnamedImage, type.ToString(), Describe(owner, peer)));
            }

            Walk(peer, screen, insideFocusable || focusable, seen, interactive, findings);
        }
    }

    /// <summary>
    ///     Enough to find the control in the markup: its type and name, the nearest ancestor named in MARKUP
    ///     (template parts, <c>PART_*</c>, are skipped — they name the theme, not the view), the templated
    ///     parent of a template part, the row's data type, the announced name and the tooltip.
    /// </summary>
    private static string Describe(Control? owner, AutomationPeer peer)
    {
        if (owner is null) return "(no owner)";
        var sb = new StringBuilder(owner.GetType().Name);
        if (!string.IsNullOrEmpty(owner.Name)) sb.Append('#').Append(owner.Name);
        if (owner.TemplatedParent is Control parent) sb.Append(" of ").Append(parent.GetType().Name);

        var named = owner.GetVisualAncestors().OfType<Control>()
            .FirstOrDefault(a => !string.IsNullOrEmpty(a.Name) && !a.Name.StartsWith("PART_", StringComparison.Ordinal));
        if (named != null) sb.Append(" in ").Append(named.GetType().Name).Append('#').Append(named.Name);
        if (owner.DataContext is { } dc && !ReferenceEquals(dc, named?.DataContext))
            sb.Append(" dc=").Append(dc.GetType().Name);
        if (peer.GetName() is { Length: > 0 } announced) sb.Append(" name=\"").Append(announced).Append('"');

        if (owner.Classes.Count > 0) sb.Append(" .").Append(string.Join(".", owner.Classes.Where(c => !c.StartsWith(':'))));
        if (ToolTip.GetTip(owner) is string tip && tip.Length > 0) sb.Append(" tip=\"").Append(tip).Append('"');
        return sb.ToString();
    }
}
