using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     W4 (2026-09-16) — THE RIG'S TONE SURFACES CARRY THREE TYPE ROLES AND NO
///     OTHERS.
///     <para>
///         The screen had reached SEVEN faces on these five files —
///         <c>FirePanelTitle</c>, <c>FireTitle</c>, <c>FireRowTitle</c>,
///         <c>FireRowDesc</c>, <c>FireRowDesc.Wide</c>, <c>FireMono.Faint</c>
///         and <c>FireStatusText</c> — so a section label, a status line and a
///         load state were three different sizes with no rule saying which was
///         which. They are <c>RigRoleTitle</c> (panel title),
///         <c>RigRoleLabel</c> (section label, and the FLOOR for a status or
///         load line) and <c>RigRoleBody</c> (running text) now, declared once
///         in <c>Styles/Fire/FireRigRolesAndTones.axaml</c>.
///     </para>
///     <para>
///         Source-level on purpose: a class that matches no selector renders in
///         the default look with no warning (<c>UI/FireClassReachabilityTests</c>
///         is the half that catches THAT), and a fourth face added at a call
///         site renders perfectly. Only reading the markup finds either.
///     </para>
/// </summary>
public sealed class RigTypographyRoleTests
{
    /// <summary>The files W4 owns the typography of. Adding a rig view here is
    /// how the rule spreads; removing one is how it rots.</summary>
    private static readonly string[] Surfaces =
    [
        Path.Combine("Views", "PlayMode", "ToneDesignPanel.axaml"),
        Path.Combine("Views", "PlayMode", "ToneDesignCorePanel.axaml"),
        Path.Combine("Views", "PlayMode", "ToneSectionsPanel.axaml"),
        // The Tones view (its own file since 2026-09-27): the library's cards,
        // the rail and the TONE3000 capture rows.
        Path.Combine("Views", "PlayMode", "RigTonesView.axaml"),
        // …whose TONE3000 / library capture rows are their own dictionary (#242)
        Path.Combine("Views", "PlayMode", "RigNamRowTemplates.axaml"),
        Path.Combine("Views", "PlayMode", "RigControlAssignmentView.axaml"),
        Path.Combine("Views", "PlayMode", "RigPerformanceControlsView.axaml")
    ];

    /// <summary>The three roles, plus the STATE modifiers each may carry. A
    /// weight is not a face, which is why <c>.Strong</c> is a modifier and not a
    /// fourth role: Space Grotesk Bold and Regular are one family, and a card's
    /// own name has to separate from its description.</summary>
    private static readonly string[] Roles = ["RigRoleTitle", "RigRoleLabel", "RigRoleBody"];

    private static readonly string[] Modifiers = ["Strong", "Accent", "Warn", "Danger"];

    private static string Read(string rel) =>
        UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", rel)));

    [Fact]
    public void EveryTextBlockOnTheToneSurfaces_WearsExactlyOneOfTheThreeRoles()
    {
        var offenders = new List<string>();
        var counted = 0;

        foreach (var rel in Surfaces)
        {
            var file = Path.GetFileName(rel);
            foreach (var t in XDocument.Load(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", rel)))
                         .Descendants().Where(e => e.Name.LocalName == "TextBlock"))
            {
                // A TextBlock inside a ComboBox/ListBox ItemTemplate draws a
                // single data value in the control's own face; it is the
                // control's type, not this screen's.
                if (t.Ancestors().Any(a => a.Name.LocalName is "ComboBox.ItemTemplate")) continue;
                // A tag's or a pill's word and a toggle's caption are the
                // CONTROL's face (`Border.FireTag > TextBlock`, the ghost
                // toggle's own font), not running text on this screen.
                if (IsControlFace(t)) continue;

                counted++;
                var classes = (t.Attribute("Classes")?.Value ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var roles = classes.Where(c => Roles.Contains(c)).ToArray();
                var strays = classes.Where(c => !Roles.Contains(c) && !Modifiers.Contains(c)).ToArray();

                if (roles.Length != 1 || strays.Length > 0)
                    offenders.Add($"{file}: Classes=\"{string.Join(' ', classes)}\" on \"{Text(t)}\"");
            }
        }

        Assert.True(counted >= 30, $"only {counted} TextBlocks walked — the parse stopped matching");
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} TextBlock(s) on the rig's tone surfaces do not wear exactly one of "
            + $"{string.Join(" / ", Roles)} (state modifiers {string.Join(" / ", Modifiers)} may be "
            + $"added):{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    ///     …and no call site may set a size or a tracking of its own. That is
    ///     what turns a role into a look nobody can move: a `FontSize="12"` on
    ///     one row is invisible in review and permanent in the file.
    ///     <para>
    ///         Proven non-vacuous by putting <c>FontSize="11"</c> back on the
    ///         match block's subtitle: the case fails naming the file and the
    ///         attribute.
    ///     </para>
    /// </summary>
    [Fact]
    public void NoToneSurfaceSetsAFontSizeOrALetterSpacingAtTheCallSite()
    {
        var offenders = new List<string>();

        foreach (var rel in Surfaces)
        {
            // Comments are MASKED rather than stripped: this file's own prose
            // names the properties it bans, and an XComment is not an attribute
            // but a regex over raw text cannot tell.
            var text = Regex.Replace(Read(rel), "<!--.*?-->", "", RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(text, @"\b(FontSize|LetterSpacing|FontFamily)\s*="))
                offenders.Add($"{Path.GetFileName(rel)}: {m.Groups[1].Value}=");
        }

        Assert.True(offenders.Count == 0,
            "a type ROLE is the whole vocabulary on these surfaces — set the size, the face and the "
            + $"tracking once in Styles/Fire/FireRigRolesAndTones.axaml, never here:{Environment.NewLine}  "
            + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    ///     The three roles exist as <c>TextBlock</c> ControlThemes (#242: a role is
    ///     <c>RigRoleTitleText</c> &amp;c., gated on its class), and the
    ///     modifiers exist too. Without this the two cases above would be
    ///     satisfied by a class nothing styles — the silent no-op
    ///     <c>UI/FireClassReachabilityTests</c> exists for, restated here so
    ///     THIS rule cannot pass vacuously on its own.
    /// </summary>
    [Fact]
    public void TheThreeRoles_AreDeclaredForTextBlock()
    {
        var styles = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI",
            "Styles", "Fire", "FireRigRolesAndTones.axaml"));

        foreach (var role in Roles)
        {
            Assert.Contains($"<ControlTheme x:Key=\"{role}Text\" TargetType=\"TextBlock\">", styles, StringComparison.Ordinal);
            Assert.Contains($"Selector=\"^.{role}\"", styles, StringComparison.Ordinal);
        }

        // …and at least one state modifier is declared, so `.Danger` on a role
        // is a colour rather than a typo.
        Assert.Contains("Selector=\"^.RigRoleLabel.Danger\"", styles, StringComparison.Ordinal);
        Assert.Contains("Selector=\"^.RigRoleBody.Strong\"", styles, StringComparison.Ordinal);
    }

    private static readonly string[] FaceBorders = ["FireTag", "FireStatusPill"];

    /// <summary>True for a TextBlock that is the direct word of a tag or pill,
    /// or the caption of a toggle (the Tones head's "AI designer"). NOT a
    /// Button's content: a tone CARD is a Button, and its name and description
    /// are exactly the running text this rule is about.</summary>
    private static bool IsControlFace(XElement t)
    {
        var parent = t.Parent;
        if (parent?.Name.LocalName == "Border"
            && (parent.Attribute("Classes")?.Value ?? "").Split(' ').Any(c => FaceBorders.Contains(c)))
        {
            return true;
        }

        return t.Ancestors().TakeWhile(a => a.Name.LocalName is not "DataTemplate")
            .Any(a => a.Name.LocalName is "ToggleButton");
    }

    private static string Text(XElement t)
    {
        var v = t.Attribute("Text")?.Value ?? t.Value;
        return v.Length > 60 ? v[..60] + "…" : v;
    }
}
