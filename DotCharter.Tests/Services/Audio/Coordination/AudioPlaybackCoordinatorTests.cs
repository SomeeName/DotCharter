using DotCharter.Services.Editing.Selection;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Playback;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.Tests.Services.Audio.Coordination;

/// <summary>
///     The metronome/clap scheduler. These tests exist for ONE reason: the sign.
///     <para>
///     Clicks used to be TRIGGERED when the latency-COMPENSATED playhead (what the
///     user is hearing) crossed a note. By then the note's audio had already left
///     the engine, and the click fired in response had to cross the output buffer
///     all over again — so every clap landed one full output latency BEHIND the
///     note it marked. A systematic bias, which is fatal for the thing claps are
///     for: judging your own timing against the chart.
///     </para>
///     <para>
///     The scheduler now scans AHEAD on the RAW playhead — the song time the engine
///     is currently WRITING INTO the output buffer — and hands the engine each
///     event's own chart time. So a click must be scheduled BEFORE the audible
///     playhead reaches it, and always AT the event's chart time.
///     </para>
/// </summary>
public sealed class AudioPlaybackCoordinatorTests
{
    private const int Tick = 0;      // click type: beat
    private const int Measure = 1;   // click type: downbeat
    private const int Clap = 2;      // click type: clap

    [Fact]
    public void Metronome_SchedulesBeatsAhead_AtTheirOwnChartTime_NotWhenTheyAreHeard()
    {
        var h = new Harness();
        h.Transport.MetronomeEnabled = true;

        // The user is HEARING 380 ms; the engine is RENDERING 400 ms (a 20 ms
        // output latency). The beat at 500 ms has not been heard OR rendered yet.
        h.Audio.RawPlayheadMs = 400;
        h.Coordinator.OnAudioPositionChanged(null, 380);

        // It is scheduled now — 120 ms of audible time before the user hears it —
        // and it carries the beat's chart time, not "now".
        var beat = Assert.Single(h.Audio.ScheduledClicks);
        Assert.Equal(Tick, beat.ClickType);
        Assert.Equal(500, beat.TransportMs);

        // Nothing was fired through the immediate (always-late) path.
        Assert.Empty(h.Audio.ImmediateClicks);
    }

    [Fact]
    public void Metronome_MeasureDownbeats_AreAccented()
    {
        var h = new Harness();
        h.Transport.MetronomeEnabled = true;

        // Beats sit every 500 ms, every 4th a measure: 0, 500, 1000, 1500, [2000].
        h.Audio.RawPlayheadMs = 1900;
        h.Coordinator.OnAudioPositionChanged(null, 1900);

        var beat = Assert.Single(h.Audio.ScheduledClicks);
        Assert.Equal(Measure, beat.ClickType);
        Assert.Equal(2000, beat.TransportMs);
    }

    [Fact]
    public void Metronome_DoesNotRescheduleTheSameBeatOnEveryTick()
    {
        var h = new Harness();
        h.Transport.MetronomeEnabled = true;

        // 4 ms position ticks across the beat at 500 ms. The horizon only advances,
        // so the beat must be handed to the engine exactly once.
        for (var raw = 400.0; raw <= 600.0; raw += 4.0)
            h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = raw);

        Assert.Equal(1, h.Audio.ScheduledClicks.Count(c => c.TransportMs == 500));
    }

    [Fact]
    public void Claps_ChordAndItsNotesShareAnOnset_ProduceOneClap()
    {
        var h = new Harness();
        h.Transport.ClapsEnabled = true;

        // A strum: a chord plus its constituent notes, all on beat 1 (= 500 ms).
        h.AddChord(beatIndex: 1);
        h.AddNote(beatIndex: 1);
        h.AddNote(beatIndex: 1);

        h.Audio.RawPlayheadMs = 450;
        h.Coordinator.OnAudioPositionChanged(null, 450);

        var clap = Assert.Single(h.Audio.ScheduledClicks);
        Assert.Equal(Clap, clap.ClickType);
        Assert.Equal(500, clap.TransportMs);
    }

    [Fact]
    public void Claps_DistinctOnsets_EachGetTheirOwnClap()
    {
        var h = new Harness();
        h.Transport.ClapsEnabled = true;
        h.AddNote(beatIndex: 1);   //  500 ms
        h.AddChord(beatIndex: 2);  // 1000 ms

        // A 120 ms lookahead needs two ticks to cover 500 and 1000.
        h.Audio.RawPlayheadMs = 450;
        h.Coordinator.OnAudioPositionChanged(null, 450);
        h.Audio.RawPlayheadMs = 950;
        h.Coordinator.OnAudioPositionChanged(null, 950);

        Assert.Equal([(Clap, 500.0), (Clap, 1000.0)], h.Audio.ScheduledClicks);
    }

    [Fact]
    public void EventsAlreadyRenderedAreNotScheduled()
    {
        var h = new Harness();
        h.Transport.ClapsEnabled = true;
        h.AddNote(beatIndex: 1); // 500 ms

        // The engine has already rendered past 500 ms — that audio is gone, and a
        // clap for it could only ever be late. Skip it rather than lie.
        h.Audio.RawPlayheadMs = 700;
        h.Coordinator.OnAudioPositionChanged(null, 700);

        Assert.Empty(h.Audio.ScheduledClicks);
    }

    [Fact]
    public void Seek_ClearsTheEngineQueue_AndReanchorsAtTheNewPosition()
    {
        var h = new Harness();
        h.Transport.MetronomeEnabled = true;

        h.Audio.RawPlayheadMs = 400;
        h.Coordinator.OnAudioPositionChanged(null, 400);
        Assert.NotEmpty(h.Audio.ScheduledClicks);   // beat 500 is queued in the engine
        h.Audio.ScheduledClicks.Clear();

        // Jump backwards. Everything queued belongs to the position we left: it
        // must be dropped, or those frames fire against the wrong song time.
        h.Audio.RawPlayheadMs = 100;
        h.Coordinator.OnAudioPositionChanged(null, 100);

        Assert.Equal(1, h.Audio.ClearScheduledClicksCount);
        // ...and the scan re-anchors: beat 0 is behind us, beat 500 is beyond the
        // horizon (100 + 120), so this tick schedules nothing.
        Assert.Empty(h.Audio.ScheduledClicks);
    }

    [Fact]
    public void DisabledToggles_KeepTheCursorAtThePlayhead_SoEnablingMidSongDoesNotBurst()
    {
        var h = new Harness();

        // Play most of the song with both toggles off.
        for (var raw = 0.0; raw <= 3000.0; raw += 100.0)
            h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = raw);
        Assert.Empty(h.Audio.ScheduledClicks);

        // Turn the metronome on: it must pick up from HERE, not replay the six
        // beats we already played past.
        h.Transport.MetronomeEnabled = true;
        h.Audio.RawPlayheadMs = 3010;
        h.Coordinator.OnAudioPositionChanged(null, 3010);
        Assert.Empty(h.Audio.ScheduledClicks);   // 3500 is still beyond the 120 ms horizon

        // ...and it resumes normally at the next beat that enters the horizon.
        h.Audio.RawPlayheadMs = 3400;
        h.Coordinator.OnAudioPositionChanged(null, 3400);

        var beat = Assert.Single(h.Audio.ScheduledClicks);
        Assert.Equal(3500, beat.TransportMs);
    }

    [Fact]
    public void ScrubbingWhilePaused_SchedulesNoClaps_ButKeepsTheCursorAtThePlayhead()
    {
        var h = new Harness();
        h.Transport.ClapsEnabled = true;
        h.AddNote(beatIndex: 1);   // 500 ms
        h.Audio.IsPlaying = false; // paused

        // Drag the playhead across the note. PositionChanged still fires on every
        // seek, and the engine's render clock is still running — so without the
        // is-playing guard, a clap for the 500 ms note would be dropped into the
        // output and actually sound while the user scrubs.
        for (var raw = 400.0; raw <= 600.0; raw += 20.0)
            h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = raw);

        Assert.Empty(h.Audio.ScheduledClicks);

        // And when playback resumes, scheduling picks up from the playhead (no
        // burst of everything we scrubbed past).
        h.Audio.IsPlaying = true;
        h.Audio.RawPlayheadMs = 950;
        h.AddChord(beatIndex: 2); // 1000 ms, now within the lookahead horizon
        h.Coordinator.OnAudioPositionChanged(null, 950);

        var clap = Assert.Single(h.Audio.ScheduledClicks);
        Assert.Equal(1000, clap.TransportMs);
    }

    // === Playback speed changes mid-play =====================================

    [Fact]
    public void SpeedChangeMidPlay_ReschedulesTheClicksTheEngineJustDropped()
    {
        var h = new Harness();
        h.Transport.MetronomeEnabled = true;

        // Beat 500 is inside the 120 ms horizon and gets handed to the engine.
        h.Audio.RawPlayheadMs = 400;
        h.Coordinator.OnAudioPositionChanged(null, 400);
        Assert.Contains((Tick, 500.0), h.Audio.ScheduledClicks);

        // The user drops to 0.75x. The engine converts a scheduled click's CHART
        // time into an output FRAME using the tempo factor, so every frame it is
        // holding is now wrong — JuceAudioPlaybackService.PlaybackSpeed therefore
        // drops the whole queue. The coordinator's own horizon knows nothing
        // about that, so unless it re-anchors, beat 500 is never re-offered and
        // the click is silently lost.
        h.Transport.PlaybackSpeed = 0.75;

        h.Audio.RawPlayheadMs = 404;
        h.Coordinator.OnAudioPositionChanged(null, 404);

        Assert.Contains((Tick, 500.0), h.Audio.ScheduledClicks);
    }

    [Fact]
    public void SpeedChangeMidPlay_DoesNotReplayClicksAlreadyBehindThePlayhead()
    {
        var h = new Harness();
        h.Transport.MetronomeEnabled = true;

        // Play past beats 0 and 500 normally.
        for (var raw = 0.0; raw <= 600.0; raw += 4.0)
            h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = raw);

        h.Transport.PlaybackSpeed = 0.5;
        h.Audio.RawPlayheadMs = 604;
        h.Coordinator.OnAudioPositionChanged(null, 604);

        // Re-anchoring must start at the PLAYHEAD, not at the top of the beat
        // map: 0 and 500 are already rendered and a clap for them could only be
        // late (the same rule EventsAlreadyRenderedAreNotScheduled pins).
        Assert.DoesNotContain(h.Audio.ScheduledClicks, c => c.TransportMs < 604);
    }

    [Fact]
    public void SpeedChangeWhileNothingSounds_DoesNotBurstOnTheNextTick()
    {
        var h = new Harness();

        for (var raw = 0.0; raw <= 3000.0; raw += 100.0)
            h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = raw);

        h.Transport.PlaybackSpeed = 0.5;
        h.Transport.MetronomeEnabled = true;
        h.Audio.RawPlayheadMs = 3010;
        h.Coordinator.OnAudioPositionChanged(null, 3010);

        Assert.Empty(h.Audio.ScheduledClicks);
    }

    [Fact]
    public void MidiPreview_IsDrivenByTheRawPlayhead_NotTheCompensatedOne()
    {
        var h = new Harness();
        // A loaded VST instrument: the sink the per-tick MIDI preview feeds. MIDI Out
        // with no plugin plays the built-in synth, which needs no per-tick feed (#138).
        h.Audio.IsVstInstrumentEnabled = true;

        // The user is HEARING 450 ms; the engine is WRITING 500 ms (a 50 ms output
        // latency — the Steam-mirror case the compensation exists for).
        h.Audio.RawPlayheadMs = 500;
        h.Coordinator.OnAudioPositionChanged(null, 450);

        // A MIDI NoteOn is trigger-NOW: it rides the block the engine is writing,
        // which carries the song audio for 500 ms. Feeding the scheduler 450 would
        // hold the note at 500 ms back until raw reached 550 — one full output
        // latency behind its own song, every note, systematically.
        Assert.Equal(500, Assert.Single(h.Audio.MidiUpdateTimes));
    }

    [Fact]
    public void MidiPreview_IsNotDriven_WhenEveryPreviewSinkIsOff()
    {
        var h = new Harness();

        h.Audio.RawPlayheadMs = 500;
        h.Coordinator.OnAudioPositionChanged(null, 450);

        Assert.Empty(h.Audio.MidiUpdateTimes);
    }

    // === The editor loop region (v0.35 stage 3) ==============================

    [Fact]
    public void EditorLoop_WrapsOnTheRawPlayhead_NotTheCompensatedOne()
    {
        var h = new Harness();
        h.Loop.SetStart(1000);
        h.Loop.SetEnd(3000);

        // The user is HEARING 2950 ms; the engine is already WRITING 3000 — the
        // loop end. Wrapping on the audible axis instead would let the engine
        // render one whole output latency of material PAST the loop end before
        // the seek lands, and the user hears it.
        h.Audio.RawPlayheadMs = 3000;
        h.Coordinator.OnAudioPositionChanged(null, 2950);

        Assert.Equal(1000, h.Audio.LastSeekToMs);
    }

    [Fact]
    public void EditorLoop_DoesNotWrapBeforeTheRawPlayheadReachesTheEnd()
    {
        var h = new Harness();
        h.Loop.SetStart(1000);
        h.Loop.SetEnd(3000);

        h.Audio.RawPlayheadMs = 2900;
        h.Coordinator.OnAudioPositionChanged(null, 2850);

        Assert.Equal(0, h.Audio.LastSeekToMs);
    }

    [Fact]
    public void EditorLoop_WithNoRegion_LeavesTheTransportAlone()
    {
        var h = new Harness();

        h.Audio.RawPlayheadMs = 5000;
        h.Coordinator.OnAudioPositionChanged(null, 5000);

        Assert.Equal(0, h.Audio.LastSeekToMs);
    }

    [Fact]
    public void EditorLoop_IssuesOneSeekPerPass_NotOnePerTick()
    {
        // The position timer runs at up to 250 Hz and the seek takes several
        // ticks to show on the playhead.
        var h = new Harness();
        h.Loop.SetStart(1000);
        h.Loop.SetEnd(3000);

        for (var raw = 3000.0; raw <= 3040.0; raw += 4.0)
            h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = raw);

        Assert.Equal([1000.0], h.Audio.SeekHistory);
    }

    /// <summary>
    ///     The wrap calls <c>SeekTo</c> from INSIDE this handler and the service
    ///     raises <c>PositionChanged</c> synchronously, so the handler re-enters
    ///     itself on the audio timer thread with the playhead already moved to the
    ///     loop start. The INNER frame is the one that does the right thing — it
    ///     sees the discontinuity, drops the engine's queue and re-schedules from
    ///     the new position, the loop start's own downbeat included. The outer
    ///     frame must not then write its pre-wrap playhead back over that.
    /// </summary>
    [Fact]
    public void LoopWrap_DoesNotWriteTheStalePlayheadBackOverTheSeek()
    {
        var h = new Harness();
        h.Transport.MetronomeEnabled = true;
        h.Loop.SetStart(900);
        h.Loop.SetEnd(3000);

        // Run up to the loop end.
        for (var raw = 2800.0; raw < 3000.0; raw += 4.0)
            h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = raw);

        var clearsBeforeTheWrap = h.Audio.ClearScheduledClicksCount;

        // The wrap itself: one discontinuity, one queue drop, and the beat at
        // 1000 ms — inside the 120 ms horizon from the loop start — handed to the
        // engine by the re-entrant frame.
        h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = 3000);
        Assert.Equal(900, h.Audio.LastSeekToMs);
        Assert.Equal(clearsBeforeTheWrap + 1, h.Audio.ClearScheduledClicksCount);
        Assert.Contains((Tick, 1000.0), h.Audio.ScheduledClicks);

        // The next ordinary tick, four milliseconds past the loop start. It is
        // NOT a discontinuity — unless the outer frame put the pre-wrap playhead
        // back, in which case this reads as a two-second jump backwards, drops
        // the engine's queue a second time and takes that click with it.
        h.Coordinator.OnAudioPositionChanged(null, h.Audio.RawPlayheadMs = 904);

        Assert.Equal(clearsBeforeTheWrap + 1, h.Audio.ClearScheduledClicksCount);
        // ...and it is not re-offered, because the scanner never lost it.
        Assert.Equal(1, h.Audio.ScheduledClicks.Count(c => c.TransportMs == 1000));
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();

            var project = new ChartProject();
            for (var i = 0; i < 16; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
            var arr = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];
            State.TotalAudioLengthMs = 8000;

            var scope = new EditingScope(State);
            Loop = new EditorLoopService(State, new EditTargetService(Selection, scope, config), scope, logger);
            Transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly(), null, logger, Loop);
            var trackContexts = new TrackContextRegistry(State, Selection);
            Editor = new ChartEditorViewModel(State, Transport, history, Selection, tempo, trackContexts, FakeLocalization.KeysOnly());
            Coordinator = new AudioPlaybackCoordinator(Audio, Transport, Editor, State, Loop, Ui);

            // Every scheduling test below models the transport PLAYING; the scheduler
            // only accompanies playback. The paused-scrub case flips this off.
            Audio.IsPlaying = true;
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public TransportViewModel Transport { get; }
        public ChartEditorViewModel Editor { get; }
        public AudioPlaybackCoordinator Coordinator { get; }
        public EditorLoopService Loop { get; }

        /// <summary>Holds every post, never runs it: these tests drive the
        /// timer-thread half of the coordinator only, as they did when the
        /// posts went to an Avalonia dispatcher no loop was pumping.</summary>
        public HeldUiDispatcher Ui { get; } = new();

        /// <summary>Beats are 500 ms apart, so beatIndex 1 == 500 ms.</summary>
        public void AddNote(int beatIndex) =>
            State.PlacedNotes.Add(new GuitarNote
            {
                Position = new FractionalPosition(beatIndex), StringIndex = 0, Fret = 3
            });

        public void AddChord(int beatIndex) =>
            State.PlacedChords.Add(new Chord { Position = new FractionalPosition(beatIndex) });
    }

    private sealed class HeldUiDispatcher : IUiDispatcher
    {
        public List<Action> Held { get; } = [];
        public bool CheckAccess() => false;
        public void Post(Action action) => Held.Add(action);
        public void PostNormal(Action action) => Held.Add(action);
        public Task InvokeAsync(Action action) => new TaskCompletionSource().Task;
        public Task<T> InvokeAsync<T>(Func<T> func) => new TaskCompletionSource<T>().Task;
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => new TaskCompletionSource<T>().Task;
    }
}
