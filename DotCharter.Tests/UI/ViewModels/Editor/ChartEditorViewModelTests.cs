using DotCharter.Core.Domain.Vocals;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class ChartEditorViewModelTests
{
    [Fact]
    public void ApplyProject_SetsActiveArrangement_AndVocalTrack()
    {
        var h = new Harness();
        var project = new ChartProject();
        project.Arrangements.Add(new Arrangement { Name = "Rhythm" });
        project.VocalTracks.Add(new VocalTrack { Name = "Lead Vocal" });

        h.Editor.ApplyProject(project);

        Assert.Same(project, h.State.ActiveProject);
        Assert.Equal("Rhythm", h.State.ActiveArrangement?.Name);
        Assert.Equal("Lead Vocal", h.Editor.ActiveVocalTrack?.Name);
    }

    [Fact]
    public void ExecuteSeek_ClampsAtZero()
    {
        var h = new Harness();
        h.Editor.ExecuteSeek(-100);
        Assert.Equal(0, h.State.CurrentTimeMs);
        Assert.Equal(0, h.Audio.LastSeekToMs);
    }

    [Fact]
    public void ExecuteSeek_ClampsAtAudioLength()
    {
        var h = new Harness();
        h.Editor.ExecuteSeek(99_999);
        Assert.Equal(8000, h.State.CurrentTimeMs);
        Assert.Equal(8000, h.Audio.LastSeekToMs);
    }

    [Fact]
    public void ExecuteSeek_ForwardsToTransport()
    {
        var h = new Harness();
        h.Editor.ExecuteSeek(1234);
        Assert.Equal(1234, h.State.CurrentTimeMs);
        Assert.Equal(1234, h.Audio.LastSeekToMs);
    }

    [Fact]
    public void SeekToNextElement_NoShift_AdvancesToNextBeat()
    {
        var h = new Harness();
        h.State.CurrentTimeMs = 0;
        h.Editor.SeekToNextElement(false);
        Assert.Equal(500, h.State.CurrentTimeMs);
    }

    [Fact]
    public void SeekToNextElement_ShiftPressed_AdvancesToNextNote()
    {
        var h = new Harness();
        var n = new GuitarNote { Position = new FractionalPosition(2), Fret = 5 };
        h.State.PlacedNotes.Add(n);
        h.State.CurrentTimeMs = 0;
        h.Editor.SeekToNextElement(true);
        // Beat 2 at 120 BPM = 1000 ms.
        Assert.Equal(1000, h.State.CurrentTimeMs);
    }

    [Fact]
    public void Notes_Chords_Beats_ProxyToProjectState()
    {
        var h = new Harness();
        h.State.PlacedNotes.Add(new GuitarNote { Position = new FractionalPosition(1), Fret = 3 });
        Assert.Single(h.Editor.Notes);
        Assert.Single(h.Editor.PlacedNotes);
        Assert.Equal(16, h.Editor.Beats.Count);
    }

    [Fact]
    public void RequestRedraw_RaisesChartDataChanged()
    {
        var h = new Harness();
        var fired = false;
        h.Editor.ChartDataChanged += () => fired = true;
        h.Editor.RequestRedraw();
        Assert.True(fired);
    }

    /// <summary>Every String order writer (Settings, View menu, first-run setup) re-renders the timeline.</summary>
    [Fact]
    public void ChangingTheStringOrder_RedrawsTheTimeline()
    {
        var h = new Harness();
        var redraws = 0;
        h.Editor.ChartDataChanged += () => redraws++;

        h.State.InvertStrings = true;
        h.State.PlayModeInvertStrings = true;

        Assert.Equal(1, redraws);
    }

    [Fact]
    public void UndoCommand_DelegatesToHistoryService()
    {
        var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(1), Fret = 5 };
        h.History.Do(new LambdaAction(
            () => h.State.PlacedNotes.Add(note),
            () => h.State.PlacedNotes.Remove(note)));
        Assert.Single(h.State.PlacedNotes);

        h.Editor.UndoCommand.Execute(null);
        Assert.Empty(h.State.PlacedNotes);

        h.Editor.RedoCommand.Execute(null);
        Assert.Single(h.State.PlacedNotes);
    }

    [Fact]
    public void SelectNearestToTime_SelectsClosestNote()
    {
        var h = new Harness();
        var n1 = new GuitarNote { Position = new FractionalPosition(1), Fret = 3 }; // 500 ms
        var n2 = new GuitarNote { Position = new FractionalPosition(4), Fret = 5 }; // 2000 ms
        h.State.PlacedNotes.Add(n1);
        h.State.PlacedNotes.Add(n2);

        h.Editor.SelectNearestToTime(1800); // closer to n2

        Assert.Same(n2, Assert.Single(h.Editor.Selection.SelectedNotes));
        Assert.Empty(h.Editor.Selection.SelectedChords);
    }

    [Fact]
    public void SelectNearestToTime_PrefersCloserChordOverNote()
    {
        var h = new Harness();
        var note = new GuitarNote { Position = new FractionalPosition(1), Fret = 3 }; // 500 ms
        var chord = new Chord { Position = new FractionalPosition(4) };              // 2000 ms
        h.State.PlacedNotes.Add(note);
        h.State.PlacedChords.Add(chord);

        h.Editor.SelectNearestToTime(1900); // closer to chord

        Assert.Empty(h.Editor.Selection.SelectedNotes);
        Assert.Same(chord, Assert.Single(h.Editor.Selection.SelectedChords));
    }

    [Fact]
    public void SelectNearestToTime_ReplacesPreviousSelection()
    {
        var h = new Harness();
        var n1 = new GuitarNote { Position = new FractionalPosition(1) };
        var n2 = new GuitarNote { Position = new FractionalPosition(6) }; // 3000 ms
        h.State.PlacedNotes.Add(n1);
        h.State.PlacedNotes.Add(n2);
        h.Editor.Selection.SelectedNotes.Add(n1);

        h.Editor.SelectNearestToTime(3000);

        Assert.Same(n2, Assert.Single(h.Editor.Selection.SelectedNotes));
    }

    [Fact]
    public void SnapLyricStartMs_SnapsToWaveformOnset_WhenCloserThanGrid()
    {
        var h = new Harness();
        h.Transport.GridDivisor = 1; // 500 ms grid -> lines at ...,1000,1500
        // Onset at block 123 => 1230 ms (between the 1000 and 1500 grid lines).
        var env = new float[300];
        env[123] = 1f;
        for (var i = 124; i < env.Length; i++) env[i] = Math.Max(0f, env[i - 1] - 0.05f);
        h.State.WaveformData = new WaveformSamples([env], 10);

        var pos = h.Editor.SnapLyricStartMs(1235);
        var ms = TimelineMath.CalculateTimeMs(pos, h.Editor.Beats);

        Assert.True(Math.Abs(ms - 1230) < 5, $"expected onset snap ~1230ms, got {ms}");
    }

    [Fact]
    public void SnapLyricStartMs_FallsBackToGrid_WhenNoWaveform()
    {
        var h = new Harness();
        h.Transport.GridDivisor = 1;
        Assert.Null(h.State.WaveformData);

        var lyricSnap = h.Editor.SnapLyricStartMs(1235);
        var gridSnap = h.Editor.SnapToGrid(1235);
        Assert.Equal(gridSnap, lyricSnap);
    }

    /// <summary>
    ///     Focus is NOT <c>ActiveArrangement</c>, and it moves on its own:
    ///     <c>ToolCoordinator.HandlePointerPressed</c> focuses whatever stacked
    ///     panel was clicked and <c>GlobalEditingCoordinator</c> focuses a
    ///     send-selection target, both deliberately leaving
    ///     <c>ActiveArrangement</c> alone.
    ///     <para>
    ///         <c>ChartEditorViewModel</c> projects the registry's focus as
    ///         <c>FocusedTrack</c> / <c>ActiveRibbon</c>, and View ▸ Timeline Rows
    ///         binds five <c>ChartEditor.ActiveRibbon.*</c> flags TWO-WAY. Until
    ///         <c>ITrackContextRegistry.FocusedTrackChanged</c> was subscribed
    ///         (it had zero subscribers solution-wide), those re-raised only on an
    ///         <c>ActiveArrangement</c> change — so after either gesture the menu
    ///         went on writing the PREVIOUS track's ribbon object: checkboxes
    ///         drawn over a model they do not write.
    ///     </para>
    /// </summary>
    [Fact]
    public void FocusMovingWithoutActiveArrangement_RepublishesFocusedTrackAndActiveRibbon()
    {
        var h = new Harness();
        var second = new Arrangement { Name = "Rhythm" };
        second.Levels.Add(new ChartLevel());
        h.State.ActiveProject!.Arrangements.Add(second);

        var activeBefore = h.State.ActiveArrangement;
        var ribbonBefore = h.Editor.ActiveRibbon;

        var raised = new List<string?>();
        h.Editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.TrackContexts.Focus(second);

        // The gesture must not have promoted anything — that is the whole point.
        Assert.Same(activeBefore, h.State.ActiveArrangement);

        Assert.Contains(nameof(ChartEditorViewModel.FocusedTrack), raised);
        Assert.Contains(nameof(ChartEditorViewModel.ActiveRibbon), raised);

        Assert.Same(second, h.Editor.FocusedTrack?.Arrangement);
        Assert.NotSame(ribbonBefore, h.Editor.ActiveRibbon);
        Assert.Same(h.TrackContexts.Get(second)!.Ribbon, h.Editor.ActiveRibbon);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            Tempo = new TempoService(logger);
            var config = new FakeConfigurationService();

            var project = new ChartProject();
            for (var i = 0; i < 16; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];
            State.TotalAudioLengthMs = 8000;

            Transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly());
            TrackContexts = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, Transport, History, Selection, Tempo, TrackContexts, FakeLocalization.KeysOnly());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public TempoService Tempo { get; }
        public FakeAudioPlaybackService Audio { get; } = new();
        public TransportViewModel Transport { get; }
        public TrackContextRegistry TrackContexts { get; }
        public ChartEditorViewModel Editor { get; }
    }

    private sealed class LambdaAction(Action execute, Action undo) : IChartAction
    {
        public void Execute() => execute();

        public void Undo() => undo();
    }
}
