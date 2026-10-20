using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Pattern Builder's dependent controls, against the dialog grammar's
///     rule: <b>a greyed control owes the user its reason, and the reason binds
///     to the INVERSE of the predicate that greyed it.</b>
///     <para>
///         Five of them had neither half. Duplicate, Delete and the three
///         try-bar verbs were plain <c>[RelayCommand]</c>s opening with a bare
///         <c>if (SelectedPattern == null) return;</c>, so on an empty rail five
///         enabled-looking buttons no-opped with no message and no log line.
///     </para>
///     <para>
///         The gate itself is behaviour, pinned by
///         <c>PatternBuilderViewModelTests.With_no_pattern_picked_the_five_verbs_are_greyed_and_say_why</c>;
///         this class pins the markup half.
///     </para>
/// </summary>
public sealed class PatternBuilderDisabledReasonTests
{
    private const string Ns = "https://github.com/avaloniaui";

    /// <summary>
    ///     The reason shows on the INVERSE of the same predicate, so it can never
    ///     stand beside a live control — a hint that shows while the button works
    ///     is worse than none.
    /// </summary>
    [Fact]
    public void TheReasonBindsToTheInverseOfTheGate()
    {
        var hint = Named("RailNoSelectionHint");
        Assert.Equal("{Binding NoPatternSelectedReason}", Attr(hint, "Text"));
        Assert.Equal("{Binding !HasSelectedPattern}", Attr(hint, "IsVisible"));
    }

    /// <summary>
    ///     The try bar has THREE states, not two. Markup cannot say "a seed AND
    ///     it is usable", so the OK tint goes through one view-model predicate —
    ///     a seed from another arrangement used to render in the same green as a
    ///     working one while every count under it was 0.
    /// </summary>
    [Fact]
    public void TheTryBar_HasAStateForAForeignSeed()
    {
        var strip = Doc.Descendants(XName.Get("Border", Ns))
            .Single(e => (Attr(e, "Classes") ?? "") == "DlgStrip" &&
                         e.Attributes().Any(a => a.Name.LocalName == "Classes.Ok"));

        Assert.Equal("{Binding SeedIsUsable}", Attr(strip, "Classes.Ok"));
        Assert.Equal("{Binding !HasSeed}", Attr(strip, "Classes.Notice"));
        Assert.Equal("{Binding SeedIsFromAnotherArrangement}", Attr(strip, "Classes.Error"));
    }

    /// <summary>
    ///     The three try-bar verbs explained themselves in a `ToolTip.Tip` and
    ///     nowhere else, which is the one gesture a keyboard or screen-reader
    ///     user does not have.
    /// </summary>
    [Theory]
    [InlineData("Previous")]
    [InlineData("Next")]
    [InlineData("Select")]
    public void EveryTryBarVerb_IsAnnouncedAndNotOnlyHovered(string verb)
    {
        var button = Doc.Descendants(XName.Get("Button", Ns))
            .Single(e => Attr(e, "Content") == $"{{localization:Loc Pattern_Run_{verb}}}");

        Assert.Equal($"{{localization:Loc Pattern_Verb_{verb}}}", Attr(button, "ToolTip.Tip"));
        Assert.Equal($"{{localization:Loc Pattern_Verb_{verb}}}", Attr(button, "AutomationProperties.Name"));
    }

    // ---- helpers -----------------------------------------------------------

    private static XDocument? _doc;

    private static XDocument Doc => _doc ??= XDocument.Load(UiSourceTree.Resolve(Path.Combine(
        TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs", "PatternBuilderDialogView.axaml")));

    private static XElement Named(string name)
    {
        var found = Doc.Descendants()
            .Where(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name))
            .ToList();
        Assert.True(found.Count == 1, $"expected exactly one x:Name=\"{name}\", found {found.Count}");
        return found[0];
    }

    /// <summary>Attribute by LOCAL name. Note that a DOTTED attached property
    /// (`ToolTip.Tip`, `Classes.Ok`) carries no XML prefix, so its local name is
    /// the whole dotted string — only a real prefix like `x:Name` splits.</summary>
    private static string? Attr(XElement e, string name) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
}
