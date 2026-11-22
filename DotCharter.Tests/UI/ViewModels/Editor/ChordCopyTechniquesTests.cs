using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Panels;
using static DotCharter.Tests.UI.ViewModels.Editor.InspectorStagedPagesTests;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 bug (c), re-checked through the staged Technique page (P4): "Copy to strings" copied every per-string
///     technique but the Ignore flag. It stages the edited string's Ignore too, onto the played strings the
///     string-edit mask includes (an empty mask includes all), and Apply writes it.
/// </summary>
public sealed class ChordCopyTechniquesTests
{
    [Fact]
    public void Ignore_IsCopiedOntoEveryString_WithNoMask()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var page = pages.ChordTechnique;
        var chord = f.Selection.SelectedChord!;

        page.SelectStringCommand.Execute(1);
        page.StringIgnore = true;
        page.CopyToStringsCommand.Execute(null);
        pages.Footer.ApplyCommand.Execute(null);

        Assert.All(chord.ChordNotes, n => Assert.True(n.Ignore));
    }

    [Fact]
    public void Ignore_IsCopiedOnlyOntoTheMaskedStrings()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        f.State.ToggleStringEditMask(0);
        f.State.ToggleStringEditMask(2);
        var page = pages.ChordTechnique;
        var chord = f.Selection.SelectedChord!;

        page.SelectStringCommand.Execute(0);
        page.StringIgnore = true;
        page.CopyToStringsCommand.Execute(null);
        pages.Footer.ApplyCommand.Execute(null);

        Assert.Equal([true, false, true], chord.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Ignore));
    }

    [Fact]
    public void TheSlide_IsCopiedAsAnInterval_FromEachStringsOwnFret()
    {
        var f = new ChordLadderFixture();
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var page = pages.ChordTechnique;
        var chord = f.Selection.SelectedChord!;
        var frets = chord.ChordNotes.ToDictionary(n => n.StringIndex, n => n.Fret);

        page.SelectStringCommand.Execute(0);
        page.SlideTo = frets[0] + 2;
        page.CopyToStringsCommand.Execute(null);
        pages.Footer.ApplyCommand.Execute(null);

        Assert.All(chord.ChordNotes, n => Assert.Equal(frets[n.StringIndex] + 2, n.SlideTo));
    }

    /// <summary>#751 P4 review: an open string cannot slide (<c>ChordSlideScope.Movers</c>): the copy puts the
    /// interval on the fretted strings only and leaves the open string's own slide alone.</summary>
    [Fact]
    public void TheSlide_IsCopiedOntoTheFrettedStringsOnly_NeverAnOpenString()
    {
        var f = new ChordLadderFixture();
        var chord = f.Selection.SelectedChord!;
        chord.ChordNotes.Single(n => n.StringIndex == 2).Fret = 0;
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var page = pages.ChordTechnique;
        var frets = chord.ChordNotes.ToDictionary(n => n.StringIndex, n => n.Fret);

        page.SelectStringCommand.Execute(0);
        page.SlideTo = frets[0] + 2;
        page.CopyToStringsCommand.Execute(null);
        pages.Footer.ApplyCommand.Execute(null);

        Assert.Equal(frets[0] + 2, chord.ChordNotes.Single(n => n.StringIndex == 0).SlideTo);
        Assert.Equal(frets[1] + 2, chord.ChordNotes.Single(n => n.StringIndex == 1).SlideTo);
        Assert.Null(chord.ChordNotes.Single(n => n.StringIndex == 2).SlideTo);
    }

    /// <summary>#751 P4 review: the copy carries the vibrato's depth with the vibrato, as the old copy did.</summary>
    [Fact]
    public void TheVibratoDepth_IsCopiedWithTheVibrato()
    {
        var f = new ChordLadderFixture();
        var chord = f.Selection.SelectedChord!;
        var source = chord.ChordNotes.Single(n => n.StringIndex == 0);
        source.Vibrato = true;
        source.VibratoStrength = 40;
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var page = pages.ChordTechnique;

        page.SelectStringCommand.Execute(0);
        page.CopyToStringsCommand.Execute(null);
        pages.Footer.ApplyCommand.Execute(null);

        Assert.All(chord.ChordNotes, n => Assert.Equal((true, 40), (n.Vibrato, n.VibratoStrength)));
    }

    [Fact]
    public void AnUnignoredString_ClearsIgnoreOnTheOtherStrings()
    {
        var f = new ChordLadderFixture();
        var chord = f.Selection.SelectedChord!;
        foreach (var n in chord.ChordNotes) n.Ignore = n.StringIndex != 0;
        using var pages = Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var page = pages.ChordTechnique;

        page.SelectStringCommand.Execute(0);
        page.CopyToStringsCommand.Execute(null);
        pages.Footer.ApplyCommand.Execute(null);

        Assert.All(chord.ChordNotes, n => Assert.False(n.Ignore));
    }
}
