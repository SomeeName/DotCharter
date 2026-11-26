using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #953: the chord Technique page's "Chord techniques" rows set a technique on EVERY string of every chord the
///     "Apply to" ladder reaches in one click and one undo step, read mixed (-1) when the strings differ, and clear
///     the technique with "none".
/// </summary>
public sealed class ChordAllStringsTechniqueTests
{
    [Fact]
    public void OneClick_MutesEveryStringOfEverySelectedChord_InOneUndoStep_AndUndoRestores()
    {
        var f = new ChordLadderFixture();
        using var pages = InspectorStagedPagesTests.Pages(f, applyStep: false);
        f.Choose(ChordApplyRung.Selected);
        var chords = new[] { f.First, f.Second };
        var fingerprints = f.Fingerprints();

        pages.ChordTechnique.SetAllMuteCommand.Execute("1");

        Assert.All(chords, c => Assert.All(c.ChordNotes, n => Assert.Equal(MuteType.Full, n.Mute)));
        Assert.Equal(chords.ToHashSet(), f.ChangedSince(fingerprints));
        Assert.Single(f.History.GetUndoSteps());
        Assert.Equal((int)MuteType.Full, pages.ChordTechnique.AllMute);

        f.History.Undo();

        Assert.Empty(f.ChangedSince(fingerprints));
        Assert.All(chords, c => Assert.All(c.ChordNotes, n => Assert.Equal(MuteType.None, n.Mute)));
    }

    [Fact]
    public void StringsThatDiffer_ReadMixed_AndNoneClearsEveryString()
    {
        var f = new ChordLadderFixture();
        using var pages = InspectorStagedPagesTests.Pages(f, applyStep: false);
        f.Choose(ChordApplyRung.Selected);
        f.First.ChordNotes[1].Mute = MuteType.Palm;
        f.Second.ChordNotes[0].Tremolo = true;
        pages.Refresh();

        Assert.Equal(-1, pages.ChordTechnique.AllMute);
        Assert.Null(pages.ChordTechnique.AllTremolo);
        Assert.Equal(0, pages.ChordTechnique.AllHopo);

        pages.ChordTechnique.SetAllMuteCommand.Execute("0");
        pages.ChordTechnique.ToggleAllTremoloCommand.Execute(null);

        Assert.All(new[] { f.First, f.Second }, c => Assert.All(c.ChordNotes, n => Assert.Equal(MuteType.None, n.Mute)));
        Assert.All(new[] { f.First, f.Second }, c => Assert.All(c.ChordNotes, n => Assert.True(n.Tremolo)));
        Assert.Equal(0, pages.ChordTechnique.AllMute);
        Assert.True(pages.ChordTechnique.AllTremolo);
        Assert.Equal(2, f.History.GetUndoSteps().Count);
    }

    [Fact]
    public void TheMask_KeepsTheRowsOnTheStringsItIncludes()
    {
        var f = new ChordLadderFixture();
        using var pages = InspectorStagedPagesTests.Pages(f, applyStep: false);
        f.Choose(ChordApplyRung.Selected);
        f.State.ToggleStringEditMask(0);

        pages.ChordTechnique.SetAllHarmonicCommand.Execute("1");

        Assert.All(new[] { f.First, f.Second }, c =>
        {
            Assert.Equal(HarmonicType.Normal, c.ChordNotes.Single(n => n.StringIndex == 0).Harmonic);
            Assert.All(c.ChordNotes.Where(n => n.StringIndex != 0), n => Assert.Equal(HarmonicType.None, n.Harmonic));
        });
    }

    [Fact]
    public void WithTheApplyStep_EveryStringStages_AndTheFooterWritesThemAsOneNode()
    {
        var f = new ChordLadderFixture();
        using var pages = InspectorStagedPagesTests.Pages(f);
        f.Choose(ChordApplyRung.ThisChord);
        var chord = pages.Engine.Source as Chord ?? throw new InvalidOperationException("no chord on the page");

        pages.ChordTechnique.SetAllMuteCommand.Execute("2");

        Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.None, n.Mute));
        Assert.Equal((int)MuteType.Palm, pages.ChordTechnique.AllMute);
        pages.Footer.ApplyCommand.Execute(null);

        Assert.All(chord.ChordNotes, n => Assert.Equal(MuteType.Palm, n.Mute));
        Assert.Single(f.History.GetUndoSteps());
    }
}
