// Partial: transport — Play/Pause/Stop/SeekTo, playback speed and the render-clock click scheduling (which aims at RawPlayheadMs, never the compensated playhead).
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    private double _playbackSpeed = 1.0;

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            _playbackSpeed = value <= 0.0 ? 1.0 : value;
            var v = (float)_playbackSpeed;
            // Already-queued clicks were mapped to render frames using the OLD
            // speed, so their frames do not correspond to their chart times.
            // Drop them; the coordinator re-schedules from the new playhead.
            ClearScheduledClicks();
            try { _source?.TempoRatio = v; } catch { /* ignored */ }
            lock (_gate)
            {
                if (_stems.Count > 0 && _engine != null)
                {
                    try { _engine.StemGroupTempo = v; } catch { /* ignored */ }
                }
            }
        }
    }

    public bool IsPlaying { get; private set; }

    // _stems is mutated under _gate by LoadStemsAsync/ClearStems on a worker
    // thread, so the transport methods below must never enumerate it directly:
    // a Dictionary mutated mid-enumeration throws, or worse, reads a torn entry.
    // We copy the entries under the lock and iterate the copy OUTSIDE it — never
    // holding _gate across a native call, and never raising PositionChanged with
    // the lock held (a subscriber re-entering the service would deadlock).
    // A stem disposed between the snapshot and the call surfaces as
    // ObjectDisposedException from NativeHandleOwner, which the callers catch.
    private StemEntry[] SnapshotStems()
    {
        lock (_gate) { return _stems.Count == 0 ? [] : _stems.Values.ToArray(); }
    }

    public void Play()
    {
        var src = _source;
        var stems = SnapshotStems();
        if (src == null && stems.Length == 0) return;
        try
        {
            // Re-sync every source to the master's current position before
            // starting. This covers the case where stems were loaded while
            // paused (Source positions can drift independently after a
            // Seek/Load); without it, stems start at their last seeked or
            // zero frame while the master resumes at its real position.
            if (src != null && stems.Length > 0)
            {
                var pos = src.Position;
                try { src.Seek(pos); } catch { /* ignored */ }
                foreach (var s in stems)
                {
                    try { s.Source?.Seek(pos); } catch { /* ignored */ }
                }
            }

            src?.Play();
            foreach (var s in stems) { try { s.Source?.Play(); } catch { /* ignored */ } }
            IsPlaying = true;
            StartPositionTimer();
        }
        catch (Exception ex) { _logger?.Error(AppLogCategory.Audio, "JUCE play failed.", ex); }
    }

    public void Pause()
    {
        var src = _source;
        try { src?.Pause(); } catch { /* ignored */ }
        foreach (var s in SnapshotStems()) { try { s.Source?.Pause(); } catch { /* ignored */ } }
        IsPlaying = false;
        StopPositionTimer();
        _playhead.Reset();
        // The render clock keeps running while the transport does not, so any
        // click still queued for a future frame would fire into the silence
        // after the pause. Drop them.
        ClearScheduledClicks();
        FlushMidiActiveNotes();
    }

    public void Stop()
    {
        var src = _source;
        if (src != null) { try { src.Stop(); } catch { /* ignored */ } }
        foreach (var s in SnapshotStems()) { try { s.Source?.Stop(); } catch { /* ignored */ } }
        IsPlaying = false;
        StopPositionTimer();
        _playhead.Reset();
        _lastKnownPosition.Reset(0.0, _playhead.Epoch);
        _rawPlayheadMs = 0.0;
        ClearScheduledClicks();
        FlushMidiActiveNotes(resumeAtRawMs: 0.0);
        PositionChanged?.Invoke(this, 0.0);
    }

    // Monotonic seek evidence for tick-loop consumers (Live Play stall-vs-seek
    // discrimination): bumped on every deliberate transport seek,
    // read lock-free from any thread. A playhead jump with an unchanged
    // generation is a stalled reader, not a seek.
    private long _seekGeneration;

    public long SeekGeneration => Interlocked.Read(ref _seekGeneration);

    public void SeekTo(double timeMs)
    {
        Interlocked.Increment(ref _seekGeneration);
        var pos = TimeSpan.FromMilliseconds(Math.Max(0.0, timeMs));
        var src = _source;
        var stems = SnapshotStems();
        if (src != null) { try { src.Seek(pos); } catch { /* ignored */ } }
        foreach (var s in stems) { try { s.Source?.Seek(pos); } catch { /* ignored */ } }
        // Interpolator first, then the floor with the epoch that reset made: a
        // render read still holding the pre-seek sample is then from an OLDER
        // epoch and reports the target instead of writing the old position
        // back over it.
        _playhead.Reset();
        _lastKnownPosition.Reset(Math.Max(0.0, timeMs), _playhead.Epoch);
        // Seeking sets the transport to timeMs, so that is the song time the very
        // next block will render — the raw playhead, before compensation.
        _rawPlayheadMs = Math.Max(0.0, timeMs);
        // Clicks queued for the position we just left must not sound at the new one.
        ClearScheduledClicks();
        // ...and neither must MIDI parked for it; a note exactly on the target still sounds.
        FlushMidiActiveNotes(resumeAtRawMs: _rawPlayheadMs);
        // After a seek, re-assert the playing state on every source so the
        // transport keeps producing audio. Native AudioTransportSource keeps
        // its play flag across setPosition, but some seek paths (especially
        // when stems are present) can leave individual sources in a state
        // where they appear playing but never deliver audio until the user
        // toggles Pause/Play. Explicitly Play() everyone if we were playing.
        if (IsPlaying)
        {
            try { src?.Play(); } catch { /* ignored */ }
            foreach (var s in stems) { try { s.Source?.Play(); } catch { /* ignored */ } }
        }
        PositionChanged?.Invoke(this, timeMs);
    }

    public void PlayMetronomeTick(bool isMeasure)
    {
        try { _engine?.PlayClick(isMeasure ? JuceClickType.Downbeat : JuceClickType.Beat); } catch { /* ignored */ }
    }

    public void PlayClap()
    {
        try { _engine?.PlayClick(JuceClickType.Clap); } catch { /* ignored */ }
    }

    // --- Sample-accurate click scheduling (ABI v26) -------------------------
    //
    // Why this exists. PlayClick fires the voice at the next audio block, so a
    // click triggered when the LATENCY-COMPENSATED playhead crossed a note was
    // late TWICE over: the crossing was detected against what the user is already
    // HEARING (the note's audio had left the engine one output latency ago), and
    // the click we then fired had to traverse that same output buffer again. Net
    // result: every clap landed ~EstimatedOutputLatencyMs BEHIND the note it was
    // marking — a systematic bias, not noise, so it would be baked straight into
    // any attempt to use the claps to measure one's own playing latency.
    //
    // The fix is to place the click on the engine's OUTPUT RENDER CLOCK, anchored
    // on the RAW transport position:
    //
    //     renderFrames  = index of the next sample the device will render
    //     rawMs         = the song time of that very sample (AudiblePosition is
    //                     the position the transport has emitted INTO the output
    //                     buffer — pre-compensation, i.e. not yet heard)
    //
    //     frame(T) = renderFrames + (T - rawMs) / speed * sampleRate / 1000
    //
    // Output latency does not appear in that formula, and that IS the point: the
    // click is written into the same output samples that carry the note's audio,
    // so both travel through the identical device buffer, driver and hardware
    // delay and hit the ear together — whatever that delay is, and even if our
    // estimate of it is wrong. (Anchor on the compensated playhead instead and you
    // must add the latency back in by hand, re-importing every error in the
    // estimate. Anchoring on RAW makes the estimate irrelevant.)
    //
    // Sign check, concretely: rawMs runs AHEAD of the audible playhead. A note at
    // chart time T that the user has not heard yet has T > rawMs, so the frame
    // delta is POSITIVE — we schedule into the future. If we had used the audible
    // playhead the same note would already be behind us and the delta would be
    // negative: that is precisely today's "always late" behaviour.
    public bool TryScheduleClick(int clickType, double transportMs)
    {
        var engine = _engine;
        var src = _source;
        if (engine == null || src == null) return false;

        try
        {
            var sr = engine.SampleRate;
            if (sr <= 0) return false;

            if (!TryReadRenderAnchor(engine, out var frames, out var rawMs)) return false;

            var speed = _playbackSpeed <= 0.0 ? 1.0 : _playbackSpeed;
            // Chart-ms delta -> wall-clock delta (playback speed) -> output frames.
            var frame = frames + (long)Math.Round((transportMs - rawMs) / speed * sr / 1000.0);
            engine.ScheduleClick((JuceClickType)clickType, frame);
            return true;
        }
        catch (ObjectDisposedException) { return false; } // raced LoadAudioAsync/FreeAudio
        catch { return false; }
    }

    // Sample {render frame, raw song position} as one consistent pair — what the
    // click scheduler and the sample-accurate MIDI preview both anchor on.
    // Both are audio-thread state mutated inside a single callback, and the
    // render clock is published immediately after the transport pull, so
    // re-reading the frame counter detects any block that landed between the
    // two reads. Bounded retries; give up rather than spin.
    private bool TryReadRenderAnchor(JuceAudioEngine engine, out long frames, out double rawMs)
    {
        frames = 0;
        rawMs = 0.0;
        var src = _source;
        if (src == null) return false;
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (!engine.TryGetRenderClock(out frames, out _)) return false;
                rawMs = src.AudiblePosition.TotalMilliseconds;
                if (engine.TryGetRenderClock(out var frames2, out _) && frames2 == frames) return true;
            }
        }
        catch (ObjectDisposedException) { /* raced LoadAudioAsync/FreeAudio */ }

        return false;
    }

    public void ClearScheduledClicks()
    {
        try { _engine?.ClearScheduledClicks(); } catch { /* ignored */ }
    }

    public bool TryScheduleCalibrationClap(out long audibleHostTicks)
    {
        audibleHostTicks = 0;
        var engine = _engine;
        if (engine == null) return false;

        try
        {
            var sr = engine.SampleRate;
            if (sr <= 0) return false;

            // {frame currently at the callback boundary, QPC ticks at that boundary}.
            // The tick timebase is Stopwatch.GetTimestamp (same axis as the input
            // capture sync detection is stamped on), so the two ends of a loopback
            // measurement are directly comparable.
            if (!engine.TryGetRenderClock(out var frames, out var hostTicks)) return false;

            // Place the clap a small fixed lead ahead of that frame so it is still
            // schedulable (not already rendered) and lands cleanly inside a future
            // block. The clap voice is mixed into the click bus at that exact sample.
            const double leadMs = 40.0;
            var leadFrames = (long)Math.Round(leadMs / 1000.0 * sr);
            engine.ScheduleClick(JuceClickType.Clap, frames + leadFrames);

            // Host time that frame is handed to the driver = hostTicks + the lead;
            // it becomes AUDIBLE one output latency later. Add both so the returned
            // tick is the emit-at-the-speaker instant, not the call instant — which
            // is exactly the output-latency bias PlayClap cannot avoid.
            var freq = System.Diagnostics.Stopwatch.Frequency;
            var leadTicks = (long)Math.Round(leadFrames / (double)sr * freq);
            var outLatencyTicks = (long)Math.Round(EstimatedOutputLatencyMs / 1000.0 * freq);
            audibleHostTicks = hostTicks + leadTicks + outLatencyTicks;
            return true;
        }
        catch (ObjectDisposedException) { return false; } // raced FreeAudio/dispose
        catch { return false; }
    }
}
