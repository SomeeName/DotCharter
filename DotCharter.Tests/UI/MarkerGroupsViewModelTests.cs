using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     #246: the Song tab's Sections and Tones work like the Phrases panel — one row per name
///     folding open to its places, each place labelled with its bar and time and a jump there,
///     renamed in place (Esc puts it back) and deleted as one undo step the rows follow.
/// </summary>
public sealed class MarkerGroupsViewModelTests
{
    /// <summary>120 BPM 4/4: a bar is 2 s, beat i at i·500 ms.</summary>
    private static List<BeatMarker> Beats(int count = 64) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120 })];

    private static ChartProject Song()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        foreach (var name in new[] { "Lead", "Rhythm" })
        {
            var arrangement = new Arrangement { Name = name };
            foreach (var (beat, section) in new[] { (0, "intro"), (8, "verse"), (16, "chorus"), (32, "verse") })
                arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Section = section });
            project.Arrangements.Add(arrangement);
        }

        // A section only Rhythm has.
        project.Arrangements[1].EventPoints.Add(new EventPoint { Position = new FractionalPosition(40), Section = "riff" });
        return project;
    }

    private sealed record Pane(MarkerGroupsViewModel Vm, HistoryService History, List<double> Seeks);

    private static Pane Open(ChartProject project, MarkerKind kind)
    {
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var seeks = new List<double>();
        var vm = new MarkerGroupsViewModel(kind, () => project, seeks.Add, history, FakeLocalization.English(), logger);
        history.HistoryChanged += vm.Refresh;
        vm.Refresh();
        return new Pane(vm, history, seeks);
    }

    [Fact]
    public void Sections_AreListedInSongOrder_OnePlacePerMoment_WithBarTimeAndArrangements()
    {
        var pane = Open(Song(), MarkerKind.Section);

        Assert.Equal(["intro", "verse", "chorus", "riff"], pane.Vm.Groups.Select(g => g.Name));
        var verse = pane.Vm.Groups[1];
        Assert.Equal("2 places", verse.CountLabel);
        Assert.Equal(["#1 · bar 3 · 0:04.0 · Lead, Rhythm", "#2 · bar 9 · 0:16.0 · Lead, Rhythm"],
            verse.Occurrences.Select(o => o.Label));
        Assert.Equal("#1 · bar 11 · 0:20.0 · Rhythm", Assert.Single(pane.Vm.Groups[3].Occurrences).Label);
        Assert.Equal("Delete “verse”: removes all 2 of its places. Ctrl+Z undoes it.", verse.DeleteTip);
    }

    [Fact]
    public void APlace_JumpsTheEditorThere()
    {
        var pane = Open(Song(), MarkerKind.Section);

        pane.Vm.JumpToCommand.Execute(pane.Vm.Groups[1].Occurrences[1]);

        Assert.Equal([16000.0], pane.Seeks);
    }

    [Fact]
    public void ARename_RenamesEveryArrangement_AsOneUndoNode_AndTheRowsFollowTheUndo()
    {
        var project = Song();
        var pane = Open(project, MarkerKind.Section);
        var verse = pane.Vm.Groups[1];

        verse.EditName = "verse ";
        Assert.False(verse.IsRenameDirty);
        verse.EditName = "A";
        Assert.True(verse.IsRenameDirty);
        pane.Vm.RenameCommand.Execute(verse);

        Assert.Equal(["intro", "A", "chorus", "riff"], pane.Vm.Groups.Select(g => g.Name));
        Assert.Equal(4, project.Arrangements.SelectMany(a => a.EventPoints).Count(e => e.Section == "A"));
        Assert.Equal("Renamed “verse” to “A”.", pane.Vm.StatusLine);

        pane.History.Undo();

        Assert.False(pane.History.CanUndo, "the rename must be ONE undo step");
        Assert.Equal(["intro", "verse", "chorus", "riff"], pane.Vm.Groups.Select(g => g.Name));
    }

    [Fact]
    public void Escape_PutsTheNameBack()
    {
        var pane = Open(Song(), MarkerKind.Section);
        var chorus = pane.Vm.Groups[2];
        chorus.EditName = "hook";

        pane.Vm.CancelRenameCommand.Execute(chorus);

        Assert.Equal("chorus", chorus.EditName);
        Assert.False(chorus.IsRenameDirty);
    }

    /// <summary>A marker that also carries a phrase keeps the phrase; one that only named the section goes.</summary>
    [Fact]
    public void ADelete_RemovesTheSectionEverywhere_KeepsAPhraseOnTheSameMarker_AndUndoBringsItBack()
    {
        var project = Song();
        project.Arrangements[0].EventPoints[1].Phrase = "verse riff";
        var pane = Open(project, MarkerKind.Section);

        pane.Vm.DeleteCommand.Execute(pane.Vm.Groups[1]);

        Assert.Equal(["intro", "chorus", "riff"], pane.Vm.Groups.Select(g => g.Name));
        Assert.DoesNotContain(project.Arrangements.SelectMany(a => a.EventPoints), e => e.Section == "verse");
        Assert.Contains(project.Arrangements[0].EventPoints, e => e.Phrase == "verse riff" && e.Section == "");
        Assert.Equal("Deleted “verse”. Ctrl+Z brings it back.", pane.Vm.StatusLine);

        pane.History.Undo();

        Assert.False(pane.History.CanUndo, "the delete must be ONE undo step");
        Assert.Equal(["intro", "verse", "chorus", "riff"], pane.Vm.Groups.Select(g => g.Name));
    }

    /// <summary>A starting tone is a place of its own, listed first; a defined, unplaced tone comes last.</summary>
    [Fact]
    public void Tones_ListTheStartingToneFirst_AndAnUnplacedToneLast()
    {
        var project = Song();
        project.ToneDefinitions.Add(new ToneDefinition { Key = "spare", Name = "spare" });
        foreach (var arrangement in project.Arrangements)
        {
            arrangement.StartingTone = "clean";
            arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(16), ToneName = "dist" });
        }

        project.Arrangements[0].ToneChanges.Add(new ToneChange { Position = new FractionalPosition(32), ToneName = "clean" });
        var pane = Open(project, MarkerKind.Tone);

        Assert.Equal(["clean", "dist", "spare"], pane.Vm.Groups.Select(g => g.Name));
        var clean = pane.Vm.Groups[0];
        Assert.Equal(["Starting tone · Lead, Rhythm", "#1 · bar 9 · 0:16.0 · Lead"], clean.Occurrences.Select(o => o.Label));
        Assert.Equal("2 uses", clean.CountLabel);
        Assert.Equal("#1 · bar 5 · 0:08.0 · Lead, Rhythm", Assert.Single(pane.Vm.Groups[1].Occurrences).Label);
        var spare = pane.Vm.Groups[2];
        Assert.Empty(spare.Occurrences);
        Assert.Equal("Delete “spare”: removes its definition. Ctrl+Z undoes it.", spare.DeleteTip);
        Assert.True(clean.HasWarning, "clean is placed but has no definition");
        Assert.False(spare.HasWarning);
    }

    /// <summary>One arrangement: the places do not name it.</summary>
    [Fact]
    public void ASingleArrangementSong_DoesNotNameTheArrangement()
    {
        var project = Song();
        project.Arrangements.RemoveAt(1);

        var pane = Open(project, MarkerKind.Section);

        Assert.Equal("#1 · bar 1 · 0:00.0", Assert.Single(pane.Vm.Groups[0].Occurrences).Label);
    }
}
