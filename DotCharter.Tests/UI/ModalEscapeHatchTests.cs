using System.Xml;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>A modal with no way out that a keyboard can reach is a trap.</b> Every
///     scrimmed surface in <c>DotCharter.UI/Views</c> carries a focusable control
///     that dismisses it — a close/cancel/back button, or one marked
///     <c>IsCancel="True"</c> so Escape fires it.
///     <para>
///         The Fire vocabulary already states the rule for its own overlays
///         ("<c>Button.FireOverlayClose</c> in the card's top-right. Esc is not a
///         substitute: this menu is controller-navigable, and a layer with no
///         visible way out is a trap") but nothing checked it, and the rule is not
///         Fire-specific: the 30-odd <c>Border.DialogScrim</c> dialogs are the same
///         shape and the same trap.
///     </para>
///     <para>
///         <b>Clicking the scrim does NOT count.</b> Six dialogs dismiss on
///         <c>PointerPressed="OnScrimPressed"</c>, which is the one gesture a
///         keyboard-only or controller user does not have — so a scrim handler is
///         what makes a surface a modal here, never what discharges it.
///     </para>
/// </summary>
public sealed class ModalEscapeHatchTests
{
    /// <summary>A class name containing "Scrim" is how every modal in this app
    /// declares itself: <c>DialogScrim</c> for the dialog grammar,
    /// <c>FireOverlayScrim</c> for the Fire one.</summary>
    private const string ScrimMarker = "Scrim";

    /// <summary>What a dismissing command is CALLED. Deliberately short: the two
    /// dialogs whose passive action is named neither (Recovery's "Ask later",
    /// the updater's "Later") are caught by <c>IsCancel="True"</c> instead, which
    /// is the stronger signal anyway — it is what makes Escape fire them.</summary>
    private static readonly string[] DismissVerbs = ["Close", "Cancel", "Dismiss", "Back", "Done"];

    private static readonly string[] PressableControls =
        ["Button", "ToggleButton", "RadioButton", "SplitButton", "MenuItem"];

    [Fact]
    public void EveryScrimmedSurface_OffersAFocusableWayOut()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        Assert.True(Directory.Exists(views), $"Views not found at {views}");

        var roots = 0;
        var traps = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (var modal in ModalRootsIn(XDocument.Load(file, LoadOptions.SetLineInfo)))
            {
                roots++;
                if (HasFocusableDismiss(modal)) continue;

                var info = (IXmlLineInfo)modal;
                var line = info.HasLineInfo() ? info.LineNumber : 0;
                traps.Add($"{Path.GetFileName(file)}:{line}  <{modal.Name.LocalName} " +
                          $"x:Name=\"{modal.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value ?? "-"}\">");
            }
        }

        // The scan must not pass by finding no modals.
        Assert.True(roots > 30, $"only {roots} scrimmed surfaces were found — the walk is broken");

        Assert.True(traps.Count == 0,
            "These scrimmed surfaces have no focusable control that dismisses them. Clicking the scrim " +
            "does not count — that is the one gesture a keyboard or controller user does not have. Give " +
            "the card a close button, or mark its passive action IsCancel=\"True\" so Escape fires it:\n  " +
            string.Join("\n  ", traps));
    }

    /// <summary>
    ///     The non-vacuity proof. The middle two cases are the ones that decide
    ///     whether this guard means anything: a scrim that only closes on a
    ///     pointer press is exactly the trap, and <c>IsCancel</c> is exactly the
    ///     shape the two dialogs that have no close BUTTON use instead.
    /// </summary>
    [Theory]
    [InlineData("""<Panel><Border Classes="DialogScrim" /><Button Command="{Binding CloseCommand}" /></Panel>""", true)]
    [InlineData("""<Panel PointerPressed="OnScrimPressed"><Border Classes="DialogScrim" /><Button Command="{Binding SaveCommand}" /></Panel>""", false)]
    [InlineData("""<Panel><Border Classes="DialogScrim" /><Button IsCancel="True" Command="{Binding LaterCommand}" /></Panel>""", true)]
    [InlineData("""<Panel><Border Classes="FireOverlayScrim" /><Button Classes="FireOverlayClose" /></Panel>""", true)]
    [InlineData("""<Panel><Border Classes="DialogScrim" /><TextBlock Text="Cancel" /></Panel>""", false)]
    public void APointerOnlyScrim_IsNotAWayOut(string markup, bool hasWayOut)
    {
        var modal = XElement.Parse(markup);
        Assert.Equal(hasWayOut, HasFocusableDismiss(modal));
    }

    /// <summary>A modal is identified by its SCRIM, and the root the check walks
    /// is the scrim's parent — in the Fire grammar the card is the scrim's
    /// child, in the dialog grammar its sibling.</summary>
    [Fact]
    public void TheModalRoot_IsTheScrimsParent()
    {
        var doc = XDocument.Parse("""<Root><Panel><Border Classes="DialogScrim" /></Panel></Root>""");
        var modal = Assert.Single(ModalRootsIn(doc));
        Assert.Equal("Panel", modal.Name.LocalName);
    }

    // ── the predicates ────────────────────────────────────────────────────

    /// <summary>A dialog on <c>Views/Shell/DialogHost</c> has no scrim of its own — the host draws one under it — so its
    /// <c>Grid.DialogFrame</c> root marks it as a modal just the same.</summary>
    private const string HostedFrameMarker = "DialogFrame";

    private static IEnumerable<XElement> ModalRootsIn(XDocument doc) =>
        doc.Descendants()
            .Where(e => e.Attribute("Classes")?.Value is { } classes
                        && (classes.Contains(ScrimMarker, StringComparison.Ordinal)
                            || classes.Contains(HostedFrameMarker, StringComparison.Ordinal)))
            .Select(e => e.Parent)
            .OfType<XElement>()
            .Distinct();

    private static bool HasFocusableDismiss(XElement modal) =>
        modal.Descendants().Any(d =>
            PressableControls.Contains(d.Name.LocalName, StringComparer.Ordinal)
            && (string.Equals(d.Attribute("IsCancel")?.Value, "True", StringComparison.OrdinalIgnoreCase)
                || NamesADismiss(d.Attribute("Command")?.Value)
                || NamesADismiss(d.Attribute("Classes")?.Value)
                || NamesADismiss(d.Attribute("Click")?.Value)));

    private static bool NamesADismiss(string? value) =>
        value != null && DismissVerbs.Any(v => value.Contains(v, StringComparison.Ordinal));
}
