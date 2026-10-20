using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Playback;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;

namespace DotCharter.Tests.Services.Editing.Playback;

/// <summary>
///     The editor's loop region and the per-selection audition, including the
///     bit that matters most: it REUSES <see cref="LivePlayLoop" />'s
///     normalization and wrap test rather than carrying a second copy.
/// </summary>
public sealed class EditorLoopServiceTests
{
    // === Punching a region ===================================================

    [Fact]
    public void PunchingBothPointsArmsAndEnablesTheLoop()
    {
        var h = new Harness();

        h.Loop.SetStart(1000);
        Assert.Null(h.Loop.Region);
        Assert.Equal(1000, h.State.LoopPendingStartMs);

        Assert.True(h.Loop.SetEnd(3000));

        Assert.Equal(new LivePlayLoop(1000, 3000), h.Loop.Region);
        Assert.True(h.Loop.IsEnabled);
        Assert.Null(h.State.LoopPendingStartMs);
    }

    [Fact]
    public void PunchingTheEndBeforeTheStartStillMakesTheRegion()
    {
        // Normalization is LivePlayLoop.TryCreate's, shared with Live Play.
        var h = new Harness();
        h.Loop.SetStart(3000);

        Assert.True(h.Loop.SetEnd(1000));

        Assert.Equal(new LivePlayLoop(1000, 3000), h.Loop.Region);
    }

    [Fact]
    public void ADegenerateRegionIsRefusedRatherThanSilentlyAccepted()
    {
        var h = new Harness();
        h.Loop.SetStart(1000);

        Assert.False(h.Loop.SetEnd(1000 + LivePlayLoop.MinLengthMs - 1));

        Assert.Null(h.Loop.Region);
        Assert.False(h.Loop.IsEnabled);
    }

    [Fact]
    public void PunchingTheEndWithNoArmedStartIsRefused()
    {
        var h = new Harness();

        Assert.False(h.Loop.SetEnd(3000));

        Assert.Null(h.Loop.Region);
    }

    [Fact]
    public void PunchingTheEndDoesNotSeek()
    {
        // An editor loop takes effect on the next pass instead of yanking a
        // paused playhead away from what the user was looking at. The service
        // never touches the transport at all — SetEnd returns nothing to seek to.
        var h = new Harness();
        h.State.CurrentTimeMs = 5000;
        h.Loop.SetStart(1000);

        h.Loop.SetEnd(3000);

        Assert.Equal(5000, h.State.CurrentTimeMs);
    }

    // === Toggling / clearing =================================================

    [Fact]
    public void TogglingOffKeepsTheRegion()
    {
        var h = new Harness();
        h.Mark(1000, 3000);

        Assert.True(h.Loop.Toggle());

        Assert.False(h.Loop.IsEnabled);
        Assert.Equal(new LivePlayLoop(1000, 3000), h.Loop.Region);

        Assert.True(h.Loop.Toggle());
        Assert.True(h.Loop.IsEnabled);
    }

    [Fact]
    public void TogglingWithNoRegionIsRefused()
    {
        var h = new Harness();

        Assert.False(h.Loop.Toggle());
    }

    [Fact]
    public void ClearingDropsEverything()
    {
        var h = new Harness();
        h.Mark(1000, 3000);
        h.Loop.SetStart(4000);

        h.Loop.Clear();

        Assert.Null(h.Loop.Region);
        Assert.False(h.Loop.IsEnabled);
        Assert.Null(h.State.LoopPendingStartMs);
        Assert.False(h.State.LoopIsAudition);
    }

    // === The tick ============================================================

    [Fact]
    public void TheTickWrapsAtTheEndOfARegion()
    {
        var h = new Harness();
        h.Mark(1000, 3000);

        Assert.Equal(EditorLoopAction.None, h.Loop.Tick(2000, true).Action);

        var step = h.Loop.Tick(3000, true);
        Assert.Equal(EditorLoopAction.Wrap, step.Action);
        Assert.Equal(1000, step.SeekToMs);
    }

    [Fact]
    public void ADisabledRegionDoesNotWrap()
    {
        var h = new Harness();
        h.Mark(1000, 3000);
        h.Loop.Toggle();

        Assert.Equal(EditorLoopAction.None, h.Loop.Tick(5000, true).Action);
    }

    // === Auditioning the resolved target =====================================

    [Fact]
    public void AuditioningPlaysTheResolvedTargetOnce()
    {
        var h = new Harness();
        var note = h.AddNote(beat: 2, sustainBeats: 1);
        h.Selection.SelectedNotes.Add(note);

        var seekTo = h.Loop.BeginAuditionOfTarget();

        // Beats are 500 ms apart: onset 1000 ms, end 1500 ms + the ring-out tail.
        Assert.Equal(1000, seekTo);
        Assert.Equal(new LivePlayLoop(1000, 1500 + AuditionSpan.TailMs), h.Loop.Region);
        Assert.True(h.State.LoopIsAudition);
        Assert.True(h.Loop.IsEnabled);
    }

    [Fact]
    public void AnAuditionPassStopsInsteadOfWrapping()
    {
        var h = new Harness();
        h.Selection.SelectedNotes.Add(h.AddNote(beat: 2, sustainBeats: 1));
        h.Loop.BeginAuditionOfTarget();

        var step = h.Loop.Tick(9999, true);

        Assert.Equal(EditorLoopAction.StopAtStart, step.Action);
        Assert.Equal(1000, step.SeekToMs);
    }

    [Fact]
    public void EndingAnAuditionLeavesNoLoopBehind()
    {
        var h = new Harness();
        h.Selection.SelectedNotes.Add(h.AddNote(beat: 2, sustainBeats: 1));
        h.Loop.BeginAuditionOfTarget();

        h.Loop.EndAudition();

        Assert.False(h.State.LoopIsAudition);
        Assert.False(h.Loop.IsEnabled);
    }

    [Fact]
    public void AuditioningUsesTheSharedTargetLadderNotOnlyTheSelection()
    {
        // Nothing selected: the pen rung resolves the sound under the playhead,
        // so "hear this" needs no separate notion of what "this" is.
        var h = new Harness();
        var note = h.AddNote(beat: 2, sustainBeats: 1);
        h.Targets.SetPenPositionProvider(() => new FractionalPosition(2));

        var seekTo = h.Loop.BeginAuditionOfTarget();

        Assert.Equal(1000, seekTo);
        Assert.Same(note, Assert.Single(h.Selection.SelectedNotes));
    }

    [Fact]
    public void AuditioningWithNothingResolvedIsRefused()
    {
        var h = new Harness();

        Assert.Null(h.Loop.BeginAuditionOfTarget());
        Assert.Null(h.Loop.Region);
    }

    [Fact]
    public void AZeroSustainNoteStillGetsAPlayableSpan()
    {
        // Every placement produces Sustain = Zero, so without the minimum the
        // most common audition target would be refused as degenerate.
        var h = new Harness();
        h.Selection.SelectedNotes.Add(h.AddNote(beat: 2, sustainBeats: 0));

        Assert.NotNull(h.Loop.BeginAuditionOfTarget());
        Assert.True(h.Loop.Region!.Value.LengthMs >= LivePlayLoop.MinLengthMs);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Config.AppConfig.UseFastChartingInput = true;

            var logger = new FakeAppLogger();
            var scope = new EditingScope(State);
            Targets = new EditTargetService(Selection, scope, Config);
            Loop = new EditorLoopService(State, Targets, scope, logger);

            var project = new ChartProject();
            for (var i = 0; i < 32; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];
            State.TotalAudioLengthMs = 16000;
        }

        public FakeConfigurationService Config { get; } = new();
        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public EditTargetService Targets { get; }
        public EditorLoopService Loop { get; }

        public void Mark(double startMs, double endMs)
        {
            Loop.SetStart(startMs);
            Loop.SetEnd(endMs);
        }

        public GuitarNote AddNote(int beat, int sustainBeats)
        {
            var n = new GuitarNote
            {
                Position = new FractionalPosition(beat),
                StringIndex = 0,
                Fret = 3,
                Sustain = new FractionalPosition(sustainBeats)
            };
            State.PlacedNotes.Add(n);
            return n;
        }
    }
}
