// PlayModeViewModel: starting a run - BeginGameplay, the metronome count-in, and the end-of-song watch that ends it.

using DotCharter.Services.Gameplay;
using DotCharter.Core.Graphics;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     Starting and ending a RUN: <c>BeginGameplay</c> (the one place a run's
///     transport, judging session, highway and tone state are all armed), the
///     metronome count-in that precedes it, and the end-watch tick that notices
///     the song has finished.
///     <para>
///         Part of <see cref="PlayModeViewModel" />. Injected dependencies and
///         plain private fields live in the ROOT file; see
///         <c>PlayModeViewModel.cs</c> for the full field rule and
///         why the split exists.
///     </para>
/// </summary>
public sealed partial class PlayModeViewModel
{
    private void BeginGameplay()
    {
        if (_livePlay == null || _transport == null) return;

        // ── The pre-run baselines, captured ONCE per run CHAIN ────────────────
        // Retry (results), Restart (pause), retry-from-section and a REFUSED run
        // all start a new run without ever reaching EndRunSideEffects, so the
        // previous run's transport speed, drop-tuner shift and highway scroll
        // speed are still APPLIED when we get here. Re-snapshotting them would
        // record the run's own values as the "before" state, and the eventual
        // EndRunSideEffects would then restore the run values — silently turning
        // "just for this playthrough" into a permanent setting for the editor and
        // every later song. Capture only when nothing is applied; the flag is
        // cleared by EndRunSideEffects, which is the one place they are undone.
        if (!_runSideEffectsApplied)
        {
            _preRunPitchShift = _transport.InputPitchShiftSemis;
            _preRunSongPitchShift = _transport.PitchShift;
            _preRunSpeed = _transport.PlaybackSpeed;
            _preRunScrollSpeed = _projectState.HighwayScrollSpeed;
            _runSideEffectsApplied = true;
        }

        ListenBack.Reset();

        // Full-difficulty run: the matcher already defaults to fully-leveled
        // charts; point the highway at the top level so what the player sees
        // matches what is judged.
        if (_projectState.ActiveArrangement is { Levels.Count: > 0 } arr)
            _projectState.ActiveLevel = arr.Levels[^1];

        // Down only (RigDropTuneViewModel): a song tuned ABOVE standard is left
        // alone rather than "applied" as a shift the engine silently ignores.
        // DropTunerViewModel only answers a step for a path it can honour.
        if (DropTuner.EffectiveSemitones is var drop and < 0)
        {
            _transport.InputPitchShiftSemis = drop;
            // What the RUN applied, which is what EndRunSideEffects restores off
            // — see _runAppliedPitchShift. Sticky for the whole chain.
            _runAppliedPitchShift = true;
        }

        // Tuning-match transpose: the SONG comes to the player's tuning instead of
        // the player retuning. The engine's own Rubber Band stretcher shifts it (the master
        // source's, or the stem group's shared one) — the same native path the practice
        // speed rides, replayed on an engine rebuild — and judging expects the charted frets
        // that much higher. The drop tuner is the other answer to the same question, so the
        // two never stack. Sticky for the chain, like the drop-tuner shift: a refused run
        // lands back on Get Ready, where the switch can be turned off before the next Start.
        var transpose = DropTuner.IsOn ? 0 : TuningMatch.EffectiveSemitones;
        _livePlay.TransposeSemitones = transpose;
        if (transpose != 0)
        {
            _transport.PitchShift = transpose;
            _runAppliedSongTranspose = true;
        }
        else if (_runAppliedSongTranspose)
        {
            _transport.PitchShift = _preRunSongPitchShift;
        }

        // Practice speed (single-player pick, or the LAN host's choice).
        // Baseline captured at the top of this method — see the run-chain note.
        _transport.PlaybackSpeed = Math.Clamp(PracticeSpeedPercent, 25, 100) / 100.0;

        // Highway scroll speed for THIS run. RunScrollSpeed was seeded from the
        // resolver on the pre-song card and may have been typed over, so it is the
        // per-run value and wins outright. Restored in EndRunSideEffects; AppConfig
        // is never written, which is what keeps it to this playthrough. Baseline
        // captured at the top of this method — see the run-chain note.
        _projectState.HighwayScrollSpeed = HighwaySpeedPolicy.ClampScrollSpeed((float)RunScrollSpeed);

        // Arm the gameplay metronome for the whole run (persisted setting).
        // AudioPlaybackCoordinator ORs this with the editor toggle, so it ticks
        // every beat during playback and clears in EndRunSideEffects.
        _transport.GameplayMetronomeActive = _configService.LoadAppConfig().GameplayMetronomeEnabled;
        // A click on every charted note. Scheduled sample-accurately on the RAW
        // transport axis (see AudioPlaybackCoordinator), so a clap lands ON its note
        // rather than one output latency after it — which is what makes it usable as
        // a reference for judging your own timing.
        _transport.GameplayClapsActive = _configService.LoadAppConfig().GameplayClapsEnabled;

        ApplyBackingTrackMutes();

        // Tone source for the run: the coordinator consults the per-song
        // preference itself (My tone → apply the chosen user preset once and
        // don't drive; In-game tones → force driving even with the global
        // toggle off; Global → follow UseInGameTones). Skips are cheap and
        // logged, so BeginRun is always called. Snapshots + restores the
        // manual chain itself when it drives.
        var driving = _rsToneCoordinator?.BeginRun() ?? false;
        if (!driving && _rsToneCoordinator?.IsPrepareInFlight == true)
            InGameTonesLoading = true; // surface the skip — badge shows when the player returns
        _logger.Info(AppLogCategory.Audio,
            $"Play Mode: tone coordinator BeginRun → driving={driving} (UseInGameTones={UseInGameTones}, " +
            $"HasInGameTones={HasInGameTones}, songPref={SongTonePreferenceIndex}, " +
            $"prepareInFlight={_rsToneCoordinator?.IsPrepareInFlight == true}).");

        // Start just before the first thing that is actually ON the highway, not at 0.
        //
        // An authored chart's beat grid starts at LeadingSilenceSeconds (10 s by default):
        // PadSilenceOnNewProject bakes that leading silence into the working audio and puts
        // beat 0 at the boundary, so the music begins there and every note is anchored to a
        // beat (the ffmpeg-less fallback shifts only the grid, but the beat-0 offset is the
        // same either way). The highway draws only chart content (there is no scrolling
        // ground plane) and a beat line is only visible within ~1.75 s of the playhead, so
        // seeking to 0 left the player staring at a pixel-identical frozen frame for ~8 s
        // while the transport was in fact running. That is the "it hangs for ten seconds
        // before the highway moves" report. This start logic reads beat[0].Time and backs
        // off one lead-in — it does not double-count the baked silence.
        //
        // PSARC imports, whose grid starts near zero, are unaffected: ResolveStartMs
        // returns 0 whenever the content already falls inside the lead-in.
        // The lead-in has to cover the VISIBLE horizon, which the player now
        // controls: at a slow scroll speed / long draw distance the board holds
        // up to 12 s of chart, and a fixed 3 s lead-in would leave the first note
        // already a quarter of the way down the highway when the run begins
        // instead of flying in from the far edge.
        var startGrid = _projectState.Beats;
        var leadInMs = GameplayStartPlan.LeadInForHorizon(
            HighwaySpeedPolicy.HorizonMs(
                (float)_projectState.HighwayScrollSpeed,
                (float)_projectState.HighwayDrawDistanceZ));
        var runStartMs = GameplayStartPlan.ResolveStartMs(
            startGrid is { Count: > 0 } ? startGrid[0].Time : null, leadInMs);

        // Retry-from-section (rock-meter fail-out): same lead-in policy,
        // anchored at the failed section instead of the first chart content.
        // Never EARLIER than the ordinary run start, so a section at the top
        // of the song behaves exactly like a full restart. Consumed once.
        if (_pendingRunStartOverrideMs is { } overrideStartMs)
        {
            _pendingRunStartOverrideMs = null;
            runStartMs = Math.Max(runStartMs,
                GameplayStartPlan.ResolveStartMs(overrideStartMs, leadInMs));
        }

        _transport.SeekTo(runStartMs);
        // TransportViewModel.SeekTo does NOT write ProjectState.CurrentTimeMs, and
        // `_livePlay.IsActive = true` below syncs the matcher to CurrentTimeMs. Without
        // this the matcher would anchor at 0 while the transport played from runStartMs,
        // and every note before the real start would be judged a miss.
        _projectState.CurrentTimeMs = runStartMs;

        CurrentScreen = PlayModeScreen.Playing;
        ShowEditorSurfaceRequested?.Invoke();

        _livePlay.IsActive = true;
        if (!_livePlay.IsActive)
        {
            // No input device / no chart — Live Play refused to start. Land on
            // GET READY, not Song Select: the user sat through Get Ready and the
            // count-in to reach this, and Get Ready is the screen that DIAGNOSES
            // the refusal — the input chip and the live signal probe are on it —
            // while their whole setup (path, scroll speed, stem mutes, drop
            // tuner) is still held here. Song Select threw all of that off screen
            // and put the reason in the browse bar, at the far corner from where
            // they were looking. Enter re-runs from here.
            ShowPlayModeSurfaceRequested?.Invoke();
            CurrentScreen = PlayModeScreen.TuningCheck;
            FlowError = _livePlay.StatusHint;
            return;
        }

        // Performance stakes (opt-in rock meter, 0.24). HARD exclusions live
        // right here: the Riff Repeater disarms mid-run (practice, not a gig);
        // editor practice / free play / listen-back never reach this path at
        // all. The disarm branch is belt-and-braces — Stop() already
        // disarms on every run end.
        if (_configService.LoadAppConfig().GameplayRockMeterEnabled)
            _livePlay.ArmRockMeter();
        else
            _livePlay.DisarmRockMeter();

        // Crowd meter HUD (0.28 Band night): purely cosmetic, so every Play
        // Mode gameplay run arms it. Disarmed with
        // the run (LivePlayViewModel.Stop) and by the Riff Repeater.
        _livePlay.ArmCrowdMeter();

        // A run may lead in with an audible count-in (off/4/8 beats).
        var countInBeats = CountInPlan.Sanitize(_configService.LoadAppConfig().GameplayCountInBeats);

        // Pre-first-note count-in: on a long noteless intro the run-start pre-roll
        // ends, then the player hears silence until notes arrive unannounced. When
        // enabled AND the first note sits well past the run start, additionally
        // click the N beats immediately before it — scheduled sample-accurately by
        // LivePlayViewModel as the playhead reaches them. Skipped when the first
        // note is already within the run-start count-in's reach.
        if (countInBeats > 0)
        {
            var beatTimes = new double[startGrid.Count];
            for (var i = 0; i < startGrid.Count; i++) beatTimes[i] = startGrid[i].Time;
            // Meter from the beat the run actually starts at, not grid[0]: a CDLC's
            // parked-at-zero first beat is a one-beat "measure" spanning the intro
            // silence (BeatsInMeasure=1), which would accent every count-in click.
            var (_, beatsPerMeasure) = CountInPlan.ResolveGridTempo(startGrid, runStartMs, _transport.Bpm);
            _livePlay.ArmFirstNoteCountIn(
                FirstExpectedSoundMs() is { } firstNoteMs
                    ? FirstNoteCountInPlan.Build(beatTimes, firstNoteMs, runStartMs, countInBeats, beatsPerMeasure)
                    : []);
        }
        else
        {
            _livePlay.ArmFirstNoteCountIn([]);
        }

        // Per-run identity for the count-in. CurrentScreen is a GLOBAL enum, so a
        // count-in abandoned by a quit would see Playing again the moment the
        // player re-enters the song and resume clicking + start the transport for
        // the NEW run (song starts early, doubled clicks). Cancel any predecessor
        // and hand this run its own token.
        _countInCts?.Cancel(); // not disposed: the abandoned task still holds this token
        var countIn = _countInCts = new CancellationTokenSource();

        if (countInBeats > 0)
        {
            _ = RunCountInThenStartAsync(countInBeats, runStartMs, countIn.Token);
        }
        else
        {
            StartGameplayTransport();
        }
    }

    /// <summary>The count-in of the current run (see <see cref="RunCountInThenStartAsync"/>).
    /// Cancelled on every run exit; never disposed, because an abandoned count-in
    /// may still be parked in a <c>Task.Delay</c> on its token.</summary>
    private CancellationTokenSource? _countInCts;

    private void CancelCountIn()
    {
        var cts = _countInCts;
        _countInCts = null;
        cts?.Cancel();
    }

    /// <summary>Actually starts the transport + end-watch once any count-in has
    /// finished (or immediately when there is none).</summary>
    private void StartGameplayTransport()
    {
        if (_transport == null) return;
        _transport.PlayCommand.Execute(null);
        _endWatchTimer.Start();
        _logger.Info(AppLogCategory.Application,
            $"Play Mode gameplay started. Song='{FlowSongTitle}', arrangement='{FlowArrangement}'.");
    }

    /// <summary>Plays the pre-roll count-in clicks on the beat grid at the start
    /// tempo (scaled by practice speed, downbeats accented), then starts the
    /// transport. Bails without starting when its run ended (<paramref name="token"/>
    /// — quit/restart) or the player merely left the Playing screen (pause). The
    /// schedule is the pure <see cref="CountInPlan"/>; only the awaited spacing
    /// lives here. The tempo comes from <see cref="CountInPlan.ResolveGridTempo"/>
    /// at <paramref name="runStartMs"/> — NEVER raw <c>grid[0].Bpm</c>: on many
    /// CDLC imports beat 0 is parked at t=0 with its Bpm derived from the gap to
    /// the real grid seconds later, which read as e.g. 11 "bpm" and stretched a
    /// 4-beat count-in to ~21 s of silence between session start and transport
    /// start (worst library case 65 s).</summary>
    private async Task RunCountInThenStartAsync(int beats, double runStartMs, CancellationToken token)
    {
        if (_transport == null) return;

        var grid = _projectState.Beats;
        var (bpm, beatsPerMeasure) = CountInPlan.ResolveGridTempo(grid, runStartMs, _transport.Bpm);
        var speed = _transport.PlaybackSpeed;

        try
        {
            foreach (var click in CountInPlan.Build(beats, beatsPerMeasure, bpm, speed))
            {
                if (click.DelayBeforeMs > 0.5)
                    await Task.Delay((int)Math.Round(click.DelayBeforeMs), token);
                if (token.IsCancellationRequested) return;          // this run is over
                if (CurrentScreen != PlayModeScreen.Playing) return; // paused
                _transport.PlayMetronomeClick(click.Accent);
            }

            // One more beat leads the song's downbeat in after the last click.
            await Task.Delay((int)Math.Round(CountInPlan.MsPerClick(bpm, speed)), token);
            if (token.IsCancellationRequested) return;
            if (CurrentScreen != PlayModeScreen.Playing) return;
            StartGameplayTransport();
        }
        catch (OperationCanceledException)
        {
            // The run this count-in belongs to ended (quit / restart). Whatever is
            // on screen now is someone else's run — never start its transport.
        }
    }

    /// <summary>Chart time of the first expected sound (note or chord) at the run's
    /// active level — the anchor the pre-first-note count-in leads into. Null when
    /// there is no chart content. Matches BeginGameplay's top-level selection.</summary>
    private double? FirstExpectedSoundMs()
    {
        var level = _projectState.ActiveLevel;
        if (level == null) return null;
        var beats = _projectState.Beats;
        if (beats.Count == 0) return null;

        var earliest = double.PositiveInfinity;
        foreach (var note in level.Notes)
        {
            var ms = note.GetTimeMs(beats);
            if (ms < earliest) earliest = ms;
        }
        foreach (var chord in level.Chords)
        {
            var ms = chord.GetTimeMs(beats);
            if (ms < earliest) earliest = ms;
        }
        return double.IsFinite(earliest) ? earliest : null;
    }

    private void EndWatchTick()
    {
        if (CurrentScreen != PlayModeScreen.Playing)
        {
            _endWatchTimer.Stop();
            return;
        }

        var total = _projectState.TotalAudioLengthMs;

        if (total <= 0) return;
        if (_projectState.CurrentTimeMs < total - SongEndGraceMs) return;

        // Song ran out: stop the session; SessionCompleted drives the results.
        _endWatchTimer.Stop();
        _livePlay!.IsActive = false;
        _transport!.StopCommand.Execute(null);
    }
}
