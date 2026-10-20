// LivePlayViewModel: the WORKER loop - the capture read and tick, the input silence/starvation watches, and the worker-side latency calibration drive.

using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     The WORKER loop and everything it drives off the captured samples: the
///     latency calibration pump, the input silence and starvation watches, the
///     peak probe, and <c>TickCore</c> itself - which runs OFF the UI thread and
///     may therefore only publish through the coalescing channel.
///     <para>
///         Part of <see cref="LivePlayViewModel" />. The type's fields, its
///         whole bound surface and its constructor stay in the ROOT file, so
///         "where does this come from" is one look rather than a search.
///     </para>
/// </summary>
public partial class LivePlayViewModel
{
    // ---- latency calibration --------------------------------------------------

    [RelayCommand]
    private void CalibrateLatency()
    {
        if (IsCalibrating)
        {
            lock (_sessionGate)
            {
                _calibration?.Cancel();
                _calibration = null;
                IsCalibrating = false;
                CalibrationStatus = string.Empty;
            }

            // Outside the gate: TearDownAnalysis joins the worker.
            if (!IsActive && !ShowDetectionMonitor) TearDownAnalysis();
            return;
        }

        if (!EnsureAnalyzer()) return;
        lock (_sessionGate)
        {
            _calibration = new LatencyCalibrationSession();
            _calibration.Start(Stopwatch.GetTimestamp());
        }

        IsCalibrating = true;
        CalibrationStatus = _localization["LivePlay_Calibrating"];
    }

    /// <summary>Worker-side calibration drive. Audio emits are engine-safe off
    /// the UI thread; the completion (config save, status text, teardown
    /// decision) is UI work and rides the snapshot as a calibration outcome.</summary>
    private void PumpCalibration(TickUiState ui)
    {
        if (_calibration is not { } calibration) return;
        var now = Stopwatch.GetTimestamp();

        if (calibration.ShouldEmitClick(now))
        {
            // Emit the reference click on the sample-accurate SCHEDULED path and time
            // the round trip from when it is actually AUDIBLE. PlayClap fires one
            // output latency after the call, yet we would have stamped the emit at the
            // call instant — inflating every measured round trip by that latency and
            // baking an output-latency bias into the judging offset. Fall back to
            // trigger-now only until the render clock is up.
            if (_audio.TryScheduleCalibrationClap(out var emitTicks))
            {
                calibration.NotifyClickEmitted(emitTicks);
            }
            else
            {
                _audio.PlayClap();
                calibration.NotifyClickEmitted(Stopwatch.GetTimestamp());
            }
        }

        calibration.Advance(now);

        switch (calibration.State)
        {
            case LatencyCalibrationState.Complete when calibration.ResultMs is { } resultMs:
                // The judging offset takes effect immediately (worker state);
                // persisting it is UI-side, in the posted outcome.
                _latencyOffsetMs = Math.Round(resultMs, 1);
                _logger.Info(AppLogCategory.Audio,
                    $"Live Play latency calibrated: {_latencyOffsetMs:0.0} ms round trip " +
                    $"({calibration.SamplesMs.Count} clicks).");
                _calibration = null; // nulled HERE or the next tick re-fires the outcome
                ui.CalibrationFinished = true;
                ui.CalibrationResultMs = _latencyOffsetMs;
                break;
            case LatencyCalibrationState.Failed:
                _calibration = null;
                ui.CalibrationFinished = true;
                ui.CalibrationResultMs = null;
                break;
        }
    }

    /// <summary>UI-side calibration completion (posted from the worker):
    /// persists the measured offset, updates the status line and tears the
    /// analyzer down when nothing else needs it. A cancel that raced the
    /// completion wins — <see cref="IsCalibrating"/> is already false then.</summary>
    private void ApplyCalibrationOutcome(double? resultMs)
    {
        if (!IsCalibrating) return;

        if (resultMs is { } ms)
        {
            SaveConfig(c =>
            {
                c.LivePlayLatencyOffsetMs = ms;
                // A fresh measurement supersedes whatever this device had LEARNED —
                // otherwise the stale learned value would immediately override the
                // number the player just took the trouble to measure.
                c.LivePlayLatencyOffsetsByDevice.Remove(CurrentDeviceKey(c));
            });
            CalibrationStatus = string.Format(
                _localization["LivePlay_CalibrateDone"], ms);
        }
        else
        {
            CalibrationStatus = _localization["LivePlay_CalibrateFailed"];
        }

        IsCalibrating = false;
        if (!IsActive && !ShowDetectionMonitor) TearDownAnalysis();
    }

    // ---- tick loop -------------------------------------------------------------

    private readonly float[] _levelProbeBuffer = new float[2048];

    /// <summary>Peak of the latest captured input window (non-destructive
    /// read — safe alongside the streaming detection consumer). Drives the
    /// pre-play signal meter so a dead input is obvious BEFORE a run.</summary>
    public float ReadInputPeak()
    {
        var read = _audio.ReadInputCaptureWindow(_levelProbeBuffer);
        var peak = 0f;
        for (var i = 0; i < read; i++)
        {
            var a = Math.Abs(_levelProbeBuffer[i]);
            if (a > peak) peak = a;
        }

        return peak;
    }

    /// <summary>True while an active session has produced nothing but pure
    /// digital silence for several seconds — virtually always an input-device
    /// problem (a SECOND app instance holding the exclusive ASIO input, wrong
    /// input channel, unplugged interface), never honest playing. Zeros with
    /// a live mic/DI always carry noise-floor bits.</summary>
    [ObservableProperty] private bool _isInputSilent;

    /// <summary>True while the input-capture ring is skipping samples because
    /// the detection reader can't keep up with the audio thread — virtually
    /// always CPU starvation from concurrent stem stretching / stem separation.
    /// Distinct from <see cref="IsInputSilent"/> (a dead input): here the signal
    /// is fine but samples are being dropped before detection sees them, so
    /// notes get missed. Surfaced by the shell as an "input starved by
    /// processing" warning.</summary>
    [ObservableProperty] private bool _isInputStarved;

    /// <summary>Consecutive exactly-zero samples, and whether this run ever saw
    /// a non-zero one. PER RUN — <c>StartCore</c> and <c>TearDownAnalysis</c>
    /// both reset them, or the next run starts already most of the way to the
    /// ~4 s warning on the previous run's total.</summary>
    private int _silentSamples;

    private bool _sawAnySignal;

    /// <summary>Worker shadow of <see cref="IsInputSilent"/> — the observable
    /// is UI-confined, so the worker tracks its own state and publishes flips.
    /// Reset with the two counters above, and never without also clearing the
    /// observable: a shadow of false under a banner of true can never flip it
    /// back down.</summary>
    private bool _workerInputSilent;

    private void TrackInputSilence(ReadOnlySpan<float> samples, TickUiState ui)
    {
        foreach (var s in samples)
        {
            if (s != 0f)
            {
                _silentSamples = 0;
                _sawAnySignal = true;
                if (_workerInputSilent)
                {
                    _workerInputSilent = false;
                    ui.InputSilent = false;
                }

                return;
            }
        }

        _silentSamples += samples.Length;
        // ~4 s of exact zeros at 48 kHz. Only warn during an active session:
        // idle silence is normal.
        if (_silentSamples < 4 * 48000 || _workerInputSilent || !_session.IsActive) return;
        _workerInputSilent = true;
        ui.InputSilent = true;
        _logger.Warning(AppLogCategory.Audio,
            "Live Play input is pure digital silence — likely another app instance holds the audio " +
            $"input, or the wrong input channel is selected (everSawSignal={_sawAnySignal}).");
    }

    /// <summary>Closes the capture-drop accounting for this tick and publishes
    /// the <see cref="IsInputStarved"/> flip, logging (Audio) only on the rising
    /// edge so a sustained stall doesn't spam. Gated on an active session so
    /// idle free-play never trips the warning.</summary>
    private void UpdateInputStarvation(TickUiState ui)
    {
        var monitor = _captureMonitor;
        if (monitor == null) return;

        var droppedThisTick = monitor.TickDroppedSamples;
        var now = Stopwatch.GetTimestamp();
        var gapMs = _lastStarvationCheckTimestamp == 0
            ? 0.0
            : Stopwatch.GetElapsedTime(_lastStarvationCheckTimestamp, now).TotalMilliseconds;
        _lastStarvationCheckTimestamp = now;
        var changed = monitor.EndTick(_session.IsActive);
        if (changed) ui.InputStarved = monitor.IsStarved;

        if (changed && monitor.IsStarved)
        {
            _logger.Warning(AppLogCategory.Audio,
                "Live Play input capture is starved: the detection reader fell behind the audio thread " +
                $"and the ring skipped ~{droppedThisTick} samples (~{monitor.SamplesToMs(droppedThisTick):0} ms) " +
                $"this tick (total dropped {monitor.TotalDroppedSamples}); the previous tick ran {gapMs:0} ms " +
                "earlier. A gap near the drop means this reader was BLOCKED (stem stretching, a " +
                "stem-separation job, or a native call waiting on the engine); a gap near one tick means " +
                "the capture stream JUMPED (an engine restart), not that audio was lost to load. " +
                "Notes will be missed until it recovers.");
        }
    }

    /// <summary>The worker thread's tick: session/detection work under the
    /// gate, then at most ONE coalesced snapshot posted to the UI thread.</summary>
    private void WorkerTick()
    {
        TickUiState? ui;
        lock (_sessionGate)
        {
            ui = TickCore();
        }

        if (ui != null && ui.HasContent) _uiChannel.Publish(ui);
    }

    private TickUiState? TickCore()
    {
        var analyzer = _analyzer;
        if (analyzer == null) return null;

        var ui = new TickUiState { Epoch = Volatile.Read(ref _uiEpoch) };

        // The playhead comes straight from the audio service, NOT from
        // ProjectState.CurrentTimeMs: the latter is the ≤60 Hz UI-thread
        // coalesced copy, so a starved UI thread would stall it and then jump —
        // re-importing the exact failure mode this worker exists to remove.
        // The service getter is designed for cross-thread polling (the render
        // threads already poll it every frame).
        var playheadMs = _audio.CurrentTimeMs;
        var isPlaying = _audio.IsPlaying;

        // The CQT anchor is refreshed from the live playhead on every playing tick, so
        // there is nothing to invalidate here when the transport stops. Capture keeps
        // streaming while paused and we keep pushing it — that keeps the ring warm and
        // gives the noise floor a genuine idle sample.

        PumpCalibration(ui);

        // Iteration-capped drain: at 30 Hz a tick produces ~1.6k samples, so
        // one 16k read normally suffices; the cap guarantees a hostile or
        // buggy source can only cost us a few reads per tick, never a hang.
        for (var drain = 0; drain < 8; drain++)
        {
            var read = _audio.ReadInputCapture(_captureBuffer, out var streamPos);
            if (read <= 0) break;

            // Watch for a stream-position jump: the native ring skipped ahead
            // because we fell behind (stem/CPU load), meaning detection lost
            // audio. Accounted here, surfaced by UpdateInputStarvation below.
            _captureMonitor?.OnRead(streamPos, read);

            TrackInputSilence(_captureBuffer.AsSpan(0, read), ui);

            // Take-recorder tee: bounded SPSC ring, drop-don't-block,
            // allocation-free — mirrors the PCM without touching the pump.
            _takeRecorder?.Write(_captureBuffer.AsSpan(0, read));

            var events = analyzer.Push(_captureBuffer.AsSpan(0, read), streamPos);

            // Detection v8: mirror the same PCM into the rolling CQT.
            //
            // Push it on its RAW capture-stream axis. The CQT's position parameter is a
            // write cursor, not a label — it advances by span.Length per push, and any
            // other position is treated as a stream discontinuity that wipes every
            // buffered frame. The old code scaled the position by playback speed, so at
            // ANY speed != 1.0 every push reset the ring; it never accumulated the 4096
            // samples a frame needs, stayed permanently empty, and — because a pitched
            // note has no evidence path other than the CQT in verify mode — judged 100%
            // of them Miss. That was "note detection doesn't work at 50%".
            //
            // The chart time is mapped INTO this axis at Verify time instead (the anchor
            // below). Re-anchoring every tick means there is no projection to drift, and
            // seek / pause / speed-change all need no special handling.
            if (_cqtVerifier is { } cqt)
            {
                cqt.Push(_captureBuffer.AsSpan(0, read), (long)streamPos);
                if (isPlaying && TryMapStreamPosToTransportMs(streamPos, playheadMs, out var tms))
                    cqt.SetAnchor((long)streamPos, tms, _audio.PlaybackSpeed);
            }

            // Provisional onsets BEFORE the events: one may finalize in this same read.
            foreach (var notice in analyzer.OnsetNotices)
            {
                if (!_session.IsActive || !isPlaying) continue;
                TryMapStreamPosToTransportMs(notice.StreamSamplePosition, playheadMs, out var noticeMs);
                _session.OfferOnsetNotice(notice, noticeMs, _graded);
                foreach (var grade in _graded) OnGraded(grade, ui);
            }

            foreach (var ev in events)
            {
                if (ShowDetectionMonitor) LogEvent(ev, playheadMs, ui);

                if (_calibration != null && TryGetEventHostTicks(ev, out var hostTicks))
                    _calibration.OfferOnset(hostTicks);

                // Judge only while the transport runs — otherwise noodling
                // while paused would score (or fail) chart notes near the
                // playhead.
                if (!_session.IsActive || !isPlaying) continue;

                var detectedMs = MapEventToTransportMs(ev, playheadMs);
                var result = _session.Offer(ev.Pitches, ev.Chroma, detectedMs, ev.Techniques,
                    ev.SpectralCentroidHz, _simultaneousJudged, _graded, ev.DetectorSamplePosition);
                if (result is { } judged) OnJudged(judged, ui);
                // One attack can resolve more than one expected sound (a double
                // stop the chart spells as two notes). The session has already
                // tallied them; this is what puts them on the HUD, the rock
                // meter and the 3D feedback queue like any other verdict.
                foreach (var extra in _simultaneousJudged) OnJudged(extra, ui);
                // CQT mode: this onset may time hits already credited (and shown)
                // when the CQT confirmed them. Timing cues only - see OnGraded.
                foreach (var grade in _graded) OnGraded(grade, ui);
            }

            if (read < _captureBuffer.Length) break;
        }

        UpdateInputStarvation(ui);

        if (!_session.IsActive) return WithStringConfidence(ui, playheadMs);

        UpdateTuningDrift(isPlaying, ui);

        // Seek evidence: the audio service bumps SeekGeneration on every
        // deliberate seek. A large playhead jump with an unchanged generation
        // is this tick loop having stalled (CPU starvation, GC pause) — the
        // session then advances through the gap instead of rewinding (the
        // rare now that the pump owns a thread, but still the correct
        // interpretation of an unseeked gap).
        var seekGen = _audio.SeekGeneration;
        var sawSeek = seekGen != _lastSeekGeneration;
        _lastSeekGeneration = seekGen;

        // Pre-first-note count-in: click the beats leading into the first note as
        // the playhead reaches them (only while the transport is running).
        // TryScheduleClick is engine-safe off the UI thread — the coordinator
        // already calls it from the position-timer thread.
        if (isPlaying) ScheduleFirstNoteCountIn(sawSeek);

        var tick = _session.HandleTick(playheadMs, isPlaying, _tickResult, sawSeek);
        foreach (var missed in tick.Events) OnJudged(missed, ui);
        foreach (var grade in tick.Grades) OnGraded(grade, ui);   // provisional cues
        foreach (var check in tick.TechniqueJudgments) OnTechniqueJudged(check, playheadMs, ui);

        // After the verdicts of this tick: advance the stakes clock (drift +
        // fail debounce). May post RockMeterFailed — never inline.
        Meters.PumpRockMeter(playheadMs, isPlaying);

        // Loop pre-roll: metronome count-in + remaining-beats display.
        // PlayMetronomeTick is a trigger-now engine call, thread-agnostic.
        for (var i = 0; i < tick.CountInTicks; i++) _audio.PlayMetronomeTick(i == 0);
        ui.HasCountIn = true;
        ui.CountInRemaining = tick.InCountIn ? _session.CountInBeatsRemaining(playheadMs) : 0;

        if (tick.PassCompleted) OnPassCompleted(tick, playheadMs, ui);
        if (tick.SpeedChange is { } speed) ui.SpeedRequest = speed;
        if (tick.SeekToMs is { } seekTo)
        {
            // Loop wrap. SeekTo is engine-safe off the UI thread: it snapshots
            // sources, catches disposal races, and its synchronous
            // PositionChanged is raised on the caller's thread — which the
            // coordinator (whose normal source is a timer thread) already
            // handles cross-thread. No CQT bookkeeping needed: the anchor is
            // re-taken from the playhead on the next playing tick, and the CQT
            // itself is indexed on the capture stream, which a transport seek
            // does not disturb.
            _audio.SeekTo(seekTo);
        }

        if (tick.DifficultyChanged)
        {
            ui.SectionsChanged = true;
            PublishGameplayLevel();
        }

        ComposeStats(ui, playheadMs);
        return WithStringConfidence(ui, playheadMs);
    }

    /// <summary>While the monitor shows, a tick carries the per-string confidence at the playhead
    /// whenever it changed — or the epoch did, since a Start/Stop drops the snapshot that
    /// carried the last one.</summary>
    private TickUiState WithStringConfidence(TickUiState ui, double playheadMs)
    {
        if (!ShowDetectionMonitor)
        {
            _lastStringConfidence = null;
            return ui;
        }

        var readings = _stringConfidence.Sample(playheadMs);
        if (_lastStringConfidence is { } last && _lastStringConfidenceEpoch == ui.Epoch &&
            last.AsSpan().SequenceEqual(readings))
        {
            return ui;
        }

        _lastStringConfidence = readings;
        _lastStringConfidenceEpoch = ui.Epoch;
        ui.StringConfidence = readings;
        return ui;
    }
}
