using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Tone Designer's dependent controls, against the dialog grammar's
///     rule that <b>a greyed control owes the user its reason, bound to the
///     INVERSE of the predicate that greyed it</b> — and against the rule that
///     an outcome is printed where the button that caused it lives.
/// </summary>
public sealed class ToneDesignerReasonMarkupTests
{
    private const string Ns = "https://github.com/avaloniaui";

    /// <summary>
    ///     Preview is gated on the WHOLE predicate now. Its second clause
    ///     (`HasSelectedTone`) used to be enforced inside the property setter,
    ///     so an ENABLED toggle flipped straight back with nothing printed
    ///     anywhere — worse than a greyed one, because there is not even a
    ///     disabled state to ask about.
    /// </summary>
    [Fact]
    public void ThePreviewToggle_IsGatedOnEverythingItNeeds() => Assert.Equal("{Binding CanPreviewNow}", Attr(Named("PreviewToggle"), "IsEnabled"));

    /// <summary>…and the reason is the inverse of that same predicate, covering
    /// BOTH clauses rather than only the missing-rig one.</summary>
    [Fact]
    public void TheReason_CoversBothClauses_AndBindsToTheirInverse()
    {
        var reason = Named("ToneActionReason");
        Assert.Equal("{Binding ToneActionUnavailableReason}", Attr(reason, "Text"));
        Assert.Equal("{Binding HasToneActionUnavailableReason}", Attr(reason, "IsVisible"));
    }

    /// <summary>
    ///     What Generate / Import / Add just did is printed BESIDE them. It used
    ///     to appear only inside the IMPORTED TONES card, in the far right
    ///     column and inside a scroller, so pressing Generate in the left rail
    ///     looked like it had done nothing at all.
    /// </summary>
    [Fact]
    public void TheRailPrintsTheOutcomeOfItsOwnButtons()
    {
        var status = Named("ToneRailStatus");
        Assert.Equal("{Binding ImportStatus}", Attr(status, "Text"));

        // It really is in the rail — the same DockPanel as the tone list.
        Assert.True(status.Ancestors()
                .Any(a => a.Descendants().Any(d => Attr(d, "Name") == "ToneListFrame")),
            "the status line is not in the left rail");
    }

    /// <summary>
    ///     The middle column's empty state said "No tones yet — add one or
    ///     import from a PSARC", which is the LIST's sentence: with a full rail
    ///     and the selection merely cleared it asserted the project had no tones
    ///     while they were listed beside it.
    /// </summary>
    [Fact]
    public void TheEditorsEmptyState_DoesNotClaimTheProjectHasNoTones()
    {
        var empty = Doc.Descendants(XName.Get("Border", Ns))
            .Single(e => (Attr(e, "Classes") ?? "") == "DlgEmpty");
        Assert.Equal("{Binding !HasSelectedTone}", Attr(empty, "IsVisible"));

        var text = empty.Descendants(XName.Get("TextBlock", Ns)).Single();
        Assert.Equal("{localization:Loc Dialog_Tones_PickAToneFirst}", Attr(text, "Text"));
    }

    /// <summary>
    ///     The two label columns of the editor's rows share ONE literal — that
    ///     shared column is what gives them one left edge — and it is wide
    ///     enough for the German captions. `DlgLabel` WRAPS rather than trims,
    ///     so a label a couple of pixels over becomes two lines beside a slider.
    /// </summary>
    [Fact]
    public void TheEditorsLabelColumn_IsOneLiteral_WideEnoughForGerman()
    {
        var rows = Doc.Descendants(XName.Get("Grid", Ns))
            .Select(e => Attr(e, "ColumnDefinitions"))
            .Where(d => d != null && (d.StartsWith("92,", StringComparison.Ordinal) ||
                                      d.StartsWith("118,", StringComparison.Ordinal)))
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, d => Assert.StartsWith("118,", d!, StringComparison.Ordinal));
    }

    // ---- helpers -----------------------------------------------------------

    private static XDocument? _doc;

    private static XDocument Doc => _doc ??= XDocument.Load(UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs", "ToneDesignerDialogView.axaml")));

    private static XElement Named(string name)
    {
        var found = Doc.Descendants()
            .Where(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name))
            .ToList();
        Assert.True(found.Count == 1, $"expected exactly one x:Name=\"{name}\", found {found.Count}");
        return found[0];
    }

    /// <summary>Attribute by LOCAL name. A dotted attached property carries no
    /// XML prefix, so its local name is the whole dotted string.</summary>
    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
}
