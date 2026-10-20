using System.Text;
using LyricPhrase = DotCharter.Core.Domain.Vocals.LyricPhrase;
using DotCharter.Services.Editing;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Diagnostics;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.Services.Audio.Coordination;

public class AudioPlaybackCoordinator
{
    private readonly IAudioPlaybackService _audioService;
    private readonly IChartEditorSurface _editor;
    private readonly DotCharter.Services.Editing.Playback.IEditorLoopService _loop;
    private readonly ProjectState _projectState;
    private readonly IEditorTransport _transport;
    private readonly IUiDispatcher _ui;
    private readonly ChartSynthFeeder _chartSynthFeeder = new();

    // Click-scan cursors. Timer-thread state, touched ONLY from
    // OnAudioPositionChanged / ScheduleClicks.
    private int _beatScanIndex;
    private int _chordClapScanIndex;
    private int _clapScanIndex;
    private double _lastPlayheadMs;

    // Playback-context cursors. UI-thread state, touched ONLY from
    // UpdatePlaybackContext (which the coalescing dispatcher Post runs). Nothing
    // resets them cross-thread: each scan clamps and walks both directions, so it
    // re-finds its place after any seek on its own.
    private LyricPhrase? _currentActiveLyric;
    private int _lyricScanIndex;
    private readonly PlaybackMarkerCursor _markers = new();

    // UI-binding throttle: the audio engine can post position updates at up to
    // 250 Hz (every 4 ms). Each update propagates through Avalonia's compiled
    // bindings → AvaloniaProperty.SetLocalValueAndRaise → compositor render →
    // SkiaSharp DrawingContextImpl + ConcurrentDictionary alloc, costing
    // ~10 KB per binding-update on the compositor thread (verified via
    // dotnet-trace AllocationSampling). At 250 Hz that's ~2.4 MB/s of GC
    // pressure that exhausts the playback NoGCRegion budget within ~5 minutes
    // and produces audible audio dropouts. 60 Hz is well above the human
    // perception threshold for playhead motion and matches the typical
    // monitor refresh — there is no visible benefit to faster updates, and
    // every chart-render path (Preview3DComposedCanvas, ChartRenderer) already polls
    // the audio service directly each render frame for sample-accurate time.
    private const long UiUpdateIntervalTicks = 16; // ms via Environment.TickCount64
    private long _lastUiUpdateTicks;

    // PositionChanged fires on the audio service's timer thread. Avalonia's
    // property system is single-threaded, so the binding-visible state below
    // must be mutated on the UI thread. A single coalescing post (guarded by
    // _uiUpdateQueued) keeps at most one dispatcher item in flight no matter
    // how fast the audio clock ticks — the post always publishes the latest
    // _pendingUiTimeMs, never a stale queue of intermediate values.
    private int _uiUpdateQueued;
    private double _pendingUiTimeMs;

    // Cached once: Roslyn does not cache instance method-group conversions,
    // so passing PublishUiTime directly to Post would allocate a fresh
    // Action (plus DispatcherOperation) per 60 Hz publish.
    private readonly Action _publishUiTime;

    // --- Metronome / clap scheduling ----------------------------------------
    //
    // These are NOT TRIGGERED on a crossing of the playhead this handler is
    // given. That playhead is LATENCY-COMPENSATED — it reports what the user is
    // HEARING right now — so by the time a note crossed it, the note's audio had
    // already left the engine one output latency ago. The click fired in response
    // then had to traverse that very same output buffer, arriving a further output
    // latency later. Every clap therefore landed ~EstimatedOutputLatencyMs (2 ms on
    // a tight ASIO buffer, 50 ms on a Steam-mirror device) BEHIND the note it was
    // marking. That is a systematic bias, not jitter: use those claps as a timing
    // reference to measure your own playing latency and you measure the engine's
    // output latency instead. On top of it, the trigger landed on whichever audio
    // block came next, adding the timer quantum + one block of jitter.
    //
    // So: schedule AHEAD, on the RAW transport axis, and let the engine place the
    // voice on the exact sample.
    //
    //   RawPlayheadMs  = the song time the engine is currently WRITING INTO the
    //                    output buffer. It runs one output latency AHEAD of the
    //                    audible playhead, so everything past it is un-rendered —
    //                    which is precisely what makes it schedulable.
    //
    // Each tick we scan (previous horizon, RawPlayheadMs + ClickLookaheadMs] and
    // hand every beat/note in that window to the engine WITH ITS OWN CHART TIME.
    // The engine converts that to an absolute output frame and starts the voice at
    // that sample — so the click is written into the SAME output samples that carry
    // the note's audio. Both then travel through the identical device buffer, driver
    // and hardware delay, and reach the ear TOGETHER.
    //
    // Sign check (the whole point — get this backwards and the bug doubles):
    // a note the user has not heard yet lies AHEAD of the raw playhead, so
    // (noteMs - RawPlayheadMs) > 0 and we schedule into the future. Anchoring on the
    // compensated playhead instead would put the note BEHIND us — a negative delta —
    // which is exactly the "always one output latency late" behaviour we are killing.
    // Scheduling on the raw axis also means output latency never enters the
    // arithmetic at all, so a WRONG latency estimate cannot bias the result.
    //
    // Lookahead must comfortably exceed the position tick (4 ms) + one audio block
    // (up to ~11 ms) + scheduling overhead. 120 ms is far past that while staying
    // short enough that a pause/seek (which clears the engine's queue) never leaves
    // an audible straggler.
    private const double ClickLookaheadMs = 120.0;
    // A chord and its constituent notes share an onset; without a merge window the
    // same strum would clap twice.
    private const double ClapMergeMs = 1.0;
    private double _clickScanToMs = double.NegativeInfinity;
    private double _lastClapMs = double.NegativeInfinity;

    // The speed the horizon below was built at. A click is handed to the engine
    // as a CHART time and converted to an output FRAME with the tempo factor of
    // the moment, so changing the speed invalidates every frame already queued —
    // which is why JuceAudioPlaybackService.PlaybackSpeed drops the whole queue.
    // Nothing tells THIS scanner, and its horizon still claims those clicks are
    // scheduled, so without re-anchoring every beat and clap inside the
    // remaining lookahead is silently lost on the pass where the user changed
    // speed. Read on the timer thread beside _clickScanToMs, which is the same
    // thread that owns it.
    private double _clickScanSpeed = double.NaN;

    public AudioPlaybackCoordinator(
        IAudioPlaybackService audioService,
        IEditorTransport transport,
        IChartEditorSurface editor,
        ProjectState projectState,
        DotCharter.Services.Editing.Playback.IEditorLoopService loop,
        IUiDispatcher ui)
    {
        _ui = ui;
        _audioService = audioService;
        _transport = transport;
        _editor = editor;
        _projectState = projectState;
        _loop = loop;
        _publishUiTime = PublishUiTime;
        _audioService.PositionChanged += OnAudioPositionChanged;
    }

    // Re-entrancy latch for the loop wrap. IAudioPlaybackService.SeekTo raises
    // PositionChanged SYNCHRONOUSLY, and ApplyLoopStep calls it from inside this
    // handler — so the wrap re-enters this method on the same thread with the
    // playhead already moved to the loop start. Timer-thread state, like the
    // click cursors: the only writer is the frame that is doing the seeking.
    private bool _inLoopSeek;

    public void OnAudioPositionChanged(object? sender, double newTime)
    {
        var discontinuity = newTime < _lastPlayheadMs || newTime > _lastPlayheadMs + 2000;
        if (discontinuity)
        {
            // Only the click cursors — they are timer-thread state. The
            // section/phrase/lyric cursors belong to UpdatePlaybackContext on the
            // UI thread and are deliberately NOT touched here: resetting them from
            // this thread was a cross-thread write for no benefit, since every one
            // of those scans clamps its index and walks in BOTH directions, so it
            // self-corrects from any starting point after a seek (second half of the
            // review finding).
            _beatScanIndex = 0;
            _clapScanIndex = 0;
            _chordClapScanIndex = 0;
            // Re-anchor the click cursor at the new position and drop anything
            // already queued in the engine: those frames belong to the position
            // we just left. (The service clears on seek/stop too; this covers a
            // discontinuity we observe without having caused it.)
            _clickScanToMs = double.NegativeInfinity;
            _lastClapMs = double.NegativeInfinity;
            _audioService.ClearScheduledClicks();
        }

        // Everything below indexes chart collections that the UI thread owns and
        // mutates (ObservableCollection). We are on the audio service's timer
        // thread: an edit landing mid-scan can invalidate a Count/indexer pair,
        // and System.Timers.Timer SWALLOWS the resulting exception — which would
        // abort the whole tick, so the click was dropped AND _lastPlayheadMs never
        // advanced. Contain the damage to the scan that
        // hit the edit: the playhead bookkeeping and the UI publish below still run.
        try
        {
            ScheduleClicks(_audioService.RawPlayheadMs);
        }
        catch (ArgumentOutOfRangeException) { /* chart edited mid-scan; next tick re-reads */ }
        catch (IndexOutOfRangeException) { }
        catch (InvalidOperationException) { }

        // Written BEFORE anything below can re-enter this method. ApplyLoopStep's
        // seek does exactly that, and the inner frame — which is the one that
        // saw the real discontinuity and re-scheduled from the loop start —
        // records the new playhead here. Assigning at the END of the outer frame
        // put the PRE-wrap value straight back, so the next ordinary tick read as
        // a two-second jump backwards: the engine's queue was dropped a second
        // time and the loop start's own downbeat, just scheduled, went with it.
        _lastPlayheadMs = newTime;

        // The editor's A/B loop and the one-shot selection audition. RAW again,
        // and for the same reason as ScheduleClicks: the loop END is a point of
        // the SONG. Wrapping when the AUDIBLE playhead reaches it means the
        // engine has already rendered one output latency of material past the
        // loop end, and the user hears it — inaudible on a tight ASIO buffer,
        // obvious on the ~50 ms Steam-mirror device the compensation exists for.
        //
        // Skipped while a wrap's own seek is in flight. That seek re-enters this
        // handler with the playhead already at the loop start, and the decision
        // reads a playhead inside the region as "the wrap has landed" and clears
        // its one-seek-per-pass latch — disarming the guard with the very event
        // it exists to absorb. The next tick that still reported a position past
        // the loop end (the seek takes several ticks to show on the playhead,
        // which is the latch's whole premise) then fired a second seek, and a
        // third.
        if (!_inLoopSeek)
            ApplyLoopStep(_loop.Tick(_audioService.RawPlayheadMs, _audioService.IsPlaying));

        try
        {
            if ((_transport.PreviewsMidiToExternalDevice || _transport.VstEnabled || _audioService.IsVstInstrumentEnabled) &&
                _editor.ActiveArrangement != null)
            {
                // RAW, for the same reason as ScheduleClicks above — a MIDI NoteOn is a
                // trigger-NOW sink, so it lands in the block the engine is WRITING, and
                // that block carries the song audio for RawPlayheadMs. Handing it
                // `newTime` (compensated) fired every previewed note only once raw had
                // reached chartTime + latency, so the VSTi was heard one full output
                // latency behind its own song — inaudible on a tight ASIO buffer,
                // grotesque on the ~50 ms Steam-mirror device the compensation exists
                // for. This was the last scheduling path still on the compensated axis
                // after v0.18 converted the metronome; MidiOffsetMs let users dial the
                // bias out by hand, which is how it survived.
                _audioService.UpdateMidiNotes(_audioService.RawPlayheadMs, _editor.PlacedNotes, _editor.PlacedChords,
                    _editor.ActiveArrangement, _transport.Bpm, _editor.Beats, _editor.HandShapes);
            }
        }
        catch (ArgumentOutOfRangeException) { }
        catch (IndexOutOfRangeException) { }
        catch (InvalidOperationException) { }

        // The built-in chart synth (ABI v38). NOT a per-tick note feed like the
        // MIDI preview above: the feeder uploads the whole arrangement once per
        // change (fingerprinted at most every 250 ms) and the engine schedules
        // every note natively against the song clock.
        if (_audioService is IChartSynthEngine chartSynth && chartSynth.IsChartSynthEnabled)
        {
            try
            {
                _chartSynthFeeder.Tick(Environment.TickCount64, chartSynth, _editor.ActiveArrangement,
                    _editor.PlacedNotes, _editor.PlacedChords, _editor.Beats, _editor.HandShapes, _transport.Bpm,
                    _projectState.ActiveProject?.SynthPlayStyle);
            }
            catch (ArgumentOutOfRangeException) { /* chart edited mid-scan; the next check re-reads */ }
            catch (IndexOutOfRangeException) { }
            catch (InvalidOperationException) { }
        }

        // Throttle UI-binding updates to ~60 Hz. The audio engine is allowed
        // to fire PositionChanged at up to 250 Hz so the click scan above keeps
        // a tight scheduling horizon; only the binding-visible playhead
        // throttles. See _lastUiUpdateTicks comment for rationale.
        var nowTicks = Environment.TickCount64;
        var jumped = Math.Abs(newTime - _projectState.CurrentTimeMs) > 50;
        if (jumped || nowTicks - _lastUiUpdateTicks >= UiUpdateIntervalTicks)
        {
            _lastUiUpdateTicks = nowTicks;
            Volatile.Write(ref _pendingUiTimeMs, newTime);
            if (Interlocked.Exchange(ref _uiUpdateQueued, 1) == 0)
                _ui.PostNormal(_publishUiTime);
        }
    }

    /// <summary>
    /// Applies what <c>IEditorLoopService.Tick</c> decided. We are on the audio
    /// timer thread: <c>SeekTo</c> is engine-safe there (it snapshots sources and
    /// catches disposal races — the same call Live Play's loop wrap makes off the
    /// UI thread) and doing it inline is what keeps the loop point tight, but the
    /// PAUSE goes through the transport view model on the UI thread because it
    /// writes bound state.
    /// </summary>
    private void ApplyLoopStep(DotCharter.Services.Editing.Playback.EditorLoopStep step)
    {
        if (step.Action == DotCharter.Services.Editing.Playback.EditorLoopAction.None) return;

        _inLoopSeek = true;
        try { _audioService.SeekTo(step.SeekToMs); }
        finally { _inLoopSeek = false; }
        if (step.Action != DotCharter.Services.Editing.Playback.EditorLoopAction.StopAtStart) return;

        _ui.PostNormal(() =>
        {
            _transport.Pause();
            _loop.EndAudition();
        });
    }

    /// <summary>
    /// Hands the engine every beat / note onset in
    /// <c>(previous horizon, rawNowMs + ClickLookaheadMs]</c> with its chart time,
    /// so each click is rendered into the same output samples as the audio it marks.
    /// See the ClickLookaheadMs comment for why the RAW playhead is the right anchor.
    /// </summary>
    private void ScheduleClicks(double rawNowMs)
    {
        // The editor toolbar toggle OR the Play Mode gameplay metronome (persisted
        // AppConfig.GameplayMetronomeEnabled, armed by the shell for the run) both
        // drive the same beat scan — one click per beat, measure downbeats accented.
        // No extra timer.
        var metronome = _transport.MetronomeEnabled || _transport.GameplayMetronomeActive;
        // Same shape as the metronome: the editor toolbar toggle OR the Play Mode
        // run flag (AppConfig.GameplayClapsEnabled, armed by the shell for the run).
        var claps = _transport.ClapsEnabled || _transport.GameplayClapsActive;

        // Clicks/claps accompany PLAYBACK only. While paused, PositionChanged still
        // fires on every scrub/seek, and the engine's render clock keeps running even
        // though the transport does not — so scheduling here would drop claps into
        // the output and they would audibly fire as the user drags the playhead
        // (owner-reported). Glue the cursor to the playhead and schedule
        // nothing until playback actually resumes; also keeps enabling a toggle
        // mid-song from replaying the horizon.
        // Re-anchor at the PLAYHEAD whenever the speed moved: the engine dropped
        // its queue, and anything between here and the stale horizon would
        // otherwise never be offered again. Anchoring on rawNowMs rather than
        // rewinding to the old scan start is what keeps this from replaying
        // clicks the engine has already rendered.
        var speed = _audioService.PlaybackSpeed;
        var speedChanged = !_clickScanSpeed.Equals(speed);
        _clickScanSpeed = speed;

        if ((!metronome && !claps) || !_audioService.IsPlaying)
        {
            _clickScanToMs = rawNowMs;
            return;
        }

        if (speedChanged)
        {
            _clickScanToMs = rawNowMs;
            _lastClapMs = double.NegativeInfinity;
            _beatScanIndex = 0;
            _clapScanIndex = 0;
            _chordClapScanIndex = 0;
        }

        if (double.IsNegativeInfinity(_clickScanToMs)) _clickScanToMs = rawNowMs;

        var horizon = rawNowMs + ClickLookaheadMs;
        if (horizon <= _clickScanToMs) return;
        var from = _clickScanToMs;   // exclusive: already scheduled up to here
        _clickScanToMs = horizon;

        var beats = _editor.Beats;

        if (metronome)
        {
            while (_beatScanIndex < beats.Count && beats[_beatScanIndex].Time <= from)
                _beatScanIndex++;

            while (_beatScanIndex < beats.Count && beats[_beatScanIndex].Time <= horizon)
            {
                var beat = beats[_beatScanIndex];
                _audioService.TryScheduleClick(beat.IsMeasure ? 1 : 0, beat.Time);
                _beatScanIndex++;
            }
        }

        if (!claps) return;

        var notes = _editor.PlacedNotes;
        var chords = _editor.PlacedChords;

        while (_clapScanIndex < notes.Count &&
               notes[_clapScanIndex].Position.GetPositionMs(beats) <= from)
        {
            _clapScanIndex++;
        }

        while (_chordClapScanIndex < chords.Count &&
               chords[_chordClapScanIndex].Position.GetPositionMs(beats) <= from)
        {
            _chordClapScanIndex++;
        }

        // Merge the two sorted onset streams so a chord and the notes that make it
        // up produce ONE clap, not two 0 ms apart.
        while (true)
        {
            var noteMs = _clapScanIndex < notes.Count
                ? notes[_clapScanIndex].Position.GetPositionMs(beats)
                : double.MaxValue;
            var chordMs = _chordClapScanIndex < chords.Count
                ? chords[_chordClapScanIndex].Position.GetPositionMs(beats)
                : double.MaxValue;

            var nextMs = Math.Min(noteMs, chordMs);
            if (nextMs > horizon) break;   // also terminates when both streams are exhausted

            if (noteMs <= chordMs) _clapScanIndex++;
            else _chordClapScanIndex++;

            if (nextMs - _lastClapMs < ClapMergeMs) continue;
            _lastClapMs = nextMs;
            _audioService.TryScheduleClick(2, nextMs);
        }
    }

    private void PublishUiTime()
    {
        Interlocked.Exchange(ref _uiUpdateQueued, 0);
        // The engine can stop the transport on its own — the position tick pauses
        // at the end of the audio — and the transport view model's bound flag is
        // otherwise only ever written by its own Play/Pause/Stop commands. This is
        // the UI-thread publish, so it is the right place to put the two back in
        // step; see TransportViewModel.SyncPlayingStateFromEngine.
        _transport.SyncPlayingStateFromEngine();
        var timeMs = Volatile.Read(ref _pendingUiTimeMs);
        _projectState.CurrentTimeMs = timeMs;
        UpdatePlaybackContext(timeMs);
    }

    private void UpdatePlaybackContext(double timeMs)
    {
        // One cursor for both, the owners latched: walking back to the
        // nearest section and phrase on every publish would repeat a walk each time.
        var (section, phrase) =
            _markers.Resolve(_editor.EventPoints, _editor.Beats, timeMs, Environment.TickCount64);
        if (_projectState.CurrentSectionDisplay != section)
            _projectState.CurrentSectionDisplay = section;
        if (_projectState.CurrentPhraseDisplay != phrase)
            _projectState.CurrentPhraseDisplay = phrase;

        var lyrics = _editor.LyricPhrases;
        LyricPhrase? activeLyric = null;

        if (lyrics.Count > 0)
        {
            if (_lyricScanIndex >= lyrics.Count) _lyricScanIndex = lyrics.Count - 1;
            while (_lyricScanIndex + 1 < lyrics.Count &&
                   lyrics[_lyricScanIndex + 1].Position.GetPositionMs(_editor.Beats) <= timeMs)
            {
                _lyricScanIndex++;
            }

            while (_lyricScanIndex > 0 && lyrics[_lyricScanIndex].Position.GetPositionMs(_editor.Beats) > timeMs)
                _lyricScanIndex--;

            var lp = lyrics[_lyricScanIndex];
            if (lp.Position.GetPositionMs(_editor.Beats) <= timeMs &&
                lp.EndPosition.GetPositionMs(_editor.Beats) >= timeMs)
            {
                activeLyric = lp;
            }
        }

        if (activeLyric != _currentActiveLyric)
        {
            _currentActiveLyric = activeLyric;
            if (activeLyric != null)
            {
                var sb = new StringBuilder();
                foreach (var l in activeLyric.Lyrics)
                {
                    sb.Append(l.Text);
                    if (!l.IsWordPart) sb.Append(" ");
                }

                _projectState.CurrentLyricDisplay = sb.ToString().Trim();
            }
            else
            {
                _projectState.CurrentLyricDisplay = "";
            }
        }
    }
}
