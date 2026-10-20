// Partial: live-input capture reads and the ABI v28 per-player routing — the state plus the AttachPerPlayerStateToEngine replay that brings it back across a rebuild.
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    public int ReadInputCapture(float[] destination)
    {
        var engine = _engine;
        if (engine == null || destination.Length == 0) return 0;
        // Streaming variant: returns 0 when caught up. The windowed
        // ReadInputCapture always returns a full buffer of the latest
        // window, which deadlocks drain-poll consumers (Live Play froze
        // the UI thread on exactly that).
        try { return engine.ReadInputCaptureStream(destination); }
        catch { return 0; }
    }

    public int ReadInputCapture(float[] destination, out ulong streamPosition)
    {
        streamPosition = 0;
        var engine = _engine;
        if (engine == null || destination.Length == 0) return 0;
        try { return engine.ReadInputCaptureStream(destination, out streamPosition); }
        catch { return 0; }
    }

    public int ReadInputCaptureWindow(float[] destination)
    {
        var engine = _engine;
        if (engine == null || destination.Length == 0) return 0;
        // Windowed variant: always re-serves the latest window, no cursor —
        // safe for concurrent tuner + detection consumers.
        try { return engine.ReadInputCapture(destination); }
        catch { return 0; }
    }

    public bool TryGetInputCaptureSync(out ulong capturedSamples, out long hostTicks)
    {
        capturedSamples = 0;
        hostTicks = 0;
        var engine = _engine;
        if (engine == null) return false;
        try { return engine.TryGetInputCaptureSync(out capturedSamples, out hostTicks); }
        catch { return false; }
    }

    public int InputSampleRate
    {
        get
        {
            var engine = _engine;
            if (engine == null) return 0;
            try { return engine.SampleRate; }
            catch { return 0; }
        }
    }

    // ── Per-player rigs (ABI v28) ──────────────────────────────────────────────

    /// <summary>
    ///     Points EVERY seat's tone rig at <paramref name="engine" />, or
    ///     detaches them all with <see cref="nint.Zero" />.
    ///     <para>
    ///         The DETACH ordering rule is unchanged and still load-bearing:
    ///         detach BEFORE destroying an engine, always. <c>AttachEngine</c>
    ///         writes the handle under each chain's own gate, which is the only
    ///         reason the detach is a real barrier against a knob drag already
    ///         inside <c>dca_engine_dsp_set_param</c>. What v28 adds is that
    ///         there are now up to four such handles to clear, not one — a seat
    ///         left attached to a freed engine is exactly the use-after-free the
    ///         rule exists to prevent.
    ///     </para>
    /// </summary>
    private void AttachDspChainsToEngine(JuceAudioEngine? engine)
    {
        // The ENGINE OBJECT, not just its handle: that is what lets the rig go
        // through JuceDspChain, which pins the handle across every call. Detach
        // is still `null`, and still has to happen BEFORE the engine is
        // destroyed — the pinning narrows the window, it does not remove the
        // rule, because a chain op that has not started yet is not pinning
        // anything.
        if (_dspChains != null) _dspChains.AttachEngineToAll(engine);
        else _dspChain?.AttachEngine(engine);
    }

    /// <summary>
    ///     Points the WHOLE per-player surface at a live engine: every seat's
    ///     tone rig, and the live-input routing behind it.
    ///     <para>
    ///         The two must happen together, which is why this exists as one
    ///         method: <see cref="SetLiveInputBaseline" /> and
    ///         <see cref="SetLiveInputOverride" /> write engine state that is
    ///         NOT part of <c>_activeEngineConfig</c>, so a rebuild left the
    ///         chains correctly attached to a fresh engine that was back to ONE
    ///         live-input rig with every seat on its default channel. A device
    ///         change, the NAM 48 kHz request or a watchdog recovery mid-song
    ///         therefore ended the per-seat routing with nothing on screen
    ///         changing.
    ///     </para>
    /// </summary>
    private void AttachPerPlayerStateToEngine(JuceAudioEngine engine)
    {
        AttachDspChainsToEngine(engine);

        // A re-amp take (ABI v37) is per-seat engine state too: a rebuilt engine
        // starts with every seat on its live input.
        _reampTakes.ReplayTakesTo(engine);

        // The chart synth (ABI v38) is per-seat engine state too: a rebuilt engine
        // starts with it off and its note table empty.
        _chartSynth.ReplayTo(engine);

        // The external process insert (ABI v47) is per-seat engine state too: a
        // REPLACED engine has none open (a reconfigured one keeps its own).
        _externalInserts.ReplayTo(new ExternalInsert.JuceExternalInsertEngine(engine));

        // Nothing has been routed away from the engine's own defaults ⇒ nothing
        // to say. This is the solo path, so an ordinary device change costs no
        // P/Invokes and a pre-v28 DLL is never asked for exports it lacks.
        if (_playerRouting.IsDefault) return;

        // The monitor TRIM travels with the count and the channels: without it a
        // device change mid-song put every seat back to unity, i.e. the balance
        // between two guitars vanished with nothing on screen changing — the
        // exact bug the channels themselves shipped.
        WriteRoutingTo(engine, "re-asserted after engine rebuild");
    }

    /// <summary>
    ///     Records the layout the INPUT CONFIGURATION asks for and pushes it,
    ///     unless an override is currently in force.
    ///     <para>
    ///         Called from <c>ApplyAudioConfigurationCore</c> — startup, every
    ///         settings apply, and the watchdog's recovery rebuild all reach it,
    ///         which is why it lives there rather than at a UI call site that a
    ///         fourth entry point could forget. <b>This is the writer the seat
    ///         count never had.</b> Two guitars on inputs 0 and 1 were only ever
    ///         routed per-seat inside a run-scoped override; outside one the engine
    ///         stayed on its one-seat stereo fill, i.e. one guitar hard left and
    ///         the other hard right.
    ///     </para>
    /// </summary>
    internal void SetLiveInputBaseline(LiveInputSeatLayout layout)
    {
        _playerRouting.AdoptBaseline(layout);
        PushRoutingToEngine("input configuration");
    }

    /// <inheritdoc />
    public void SetLiveInputOverride(LiveInputSeatLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        // Recorded FIRST and unconditionally: an override can arm before the engine
        // exists (or across a failed rebuild), and the state is what makes the
        // routing come back with the next engine either way.
        _playerRouting.AdoptOverride(layout);
        PushRoutingToEngine("override");
    }

    /// <inheritdoc />
    public void RestoreLiveInputBaseline()
    {
        _playerRouting.ClearOverride();
        PushRoutingToEngine("override ended, back to the input configuration");
    }

    /// <summary>
    ///     The ONE place the engine's per-seat routing is written. Everything
    ///     above only decides WHICH layout is effective; this is what
    ///     <c>dca_engine_set_player_count</c>, the per-seat channels and the
    ///     per-seat trims are reached from, and
    ///     <see cref="AttachPerPlayerStateToEngine" /> is the same write replayed
    ///     onto a rebuilt engine.
    /// </summary>
    private void PushRoutingToEngine(string reason)
    {
        // A fresh engine already stands on the defaults, so an app that never
        // leaves them costs no P/Invokes and a pre-v28 DLL is never asked for
        // exports it lacks. The latch is what makes the way BACK still push:
        // clearing an override lands on a default layout, and that write is
        // the one that ends the override's routing.
        if (_playerRouting.IsDefault && !_routingAssertedOnce) return;
        _routingAssertedOnce = true;

        var engine = _engine;
        // No engine yet (an override can arm before one exists, and a settings apply
        // runs alongside the bring-up): the STATE carries the layout, and
        // AttachPerPlayerStateToEngine puts it on the next engine.
        if (engine == null) return;
        WriteRoutingTo(engine, reason);
    }

    /// <summary>The engine write itself, shared by the live push and the rebuild
    /// replay so the two can never drift into writing different surfaces.</summary>
    private void WriteRoutingTo(JuceAudioEngine engine, string reason)
    {
        try
        {
            engine.SetPlayerCount(Math.Clamp(_playerRouting.PlayerCount, 1, Dsp.DspPlayers.MaxPlayers));
            for (var player = 0; player < _playerRouting.Channels.Count; player++)
                engine.SetPlayerInputChannel(player, _playerRouting.Channels[player]);
            for (var player = 0; player < _playerRouting.MonitorGains.Count; player++)
                engine.SetPlayerMonitorGain(player, _playerRouting.MonitorGains[player]);
            // …and each seat's DROP TUNE (ABI v31), which is the fourth piece of
            // per-seat state that appears in no config. Without it a device
            // change mid-run hands the player back a guitar a whole step out of
            // tune with the song, with nothing on screen changing — the same
            // shape the count, the channels and the trims each shipped with.
            for (var player = 0; player < _playerRouting.PitchShiftSemitones.Count; player++)
            {
                engine.SetInputPitchShiftMode(player, (JuceDropTuneMode)_playerRouting.PitchShiftModes[player]);
                engine.SetInputPitchShift(player, _playerRouting.PitchShiftSemitones[player]);
            }

            _logger?.Info(AppLogCategory.Audio,
                $"Live-input routing ({reason}): {_playerRouting.PlayerCount} rig(s), " +
                $"channels [{string.Join(", ", _playerRouting.Channels)}], " +
                $"monitor gains [{string.Join(", ", _playerRouting.MonitorGains)}], " +
                $"drop tune [{string.Join(", ", _playerRouting.PitchShiftSemitones)}] semis.");
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Writing the live-input routing failed ({reason}): {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void SetPlayerInputPitchShift(int player, double semitones)
    {
        if (!Dsp.DspPlayers.IsValid(player)) return;
        // Down only: the native CSVD shifter clamps a positive shift to 0 and
        // still answers OK, so an upward request would read as applied.
        var clamped = Math.Clamp(semitones, -24.0, 0.0);
        // Recorded FIRST and unconditionally, like the count / channel / trim:
        // this is engine state no config carries, so the record is what brings a
        // drop tune back across a device change, the NAM 48 kHz request or a
        // watchdog recovery. A dropped drop tune is the loudest of the four —
        // the guitar comes back a whole step out of tune with the song, mid-run.
        _playerRouting.RecordPitchShiftSemitones(player, (float)clamped);
        if (player == 0) _inputPitchShiftSemitones = clamped;
        var engine = _engine;
        if (engine == null) return;
        try { engine.SetInputPitchShift(player, (float)clamped); }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Setting player {player + 1}'s drop tune failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void SetPlayerInputPitchShiftMode(int player, int mode)
    {
        if (!Dsp.DspPlayers.IsValid(player)) return;
        // 0..2 (Tight / Balanced / Smooth). The native export REJECTS anything
        // else, so a wider value here would be a write the engine silently
        // refuses — and this property's old `value == 1 ? 1 : 0` was exactly
        // that for Smooth.
        var clamped = Math.Clamp(mode, 0, 2);
        _playerRouting.RecordPitchShiftMode(player, clamped);
        if (player == 0) _inputPitchShiftMode = clamped;
        var engine = _engine;
        if (engine == null) return;
        try { engine.SetInputPitchShiftMode(player, (JuceDropTuneMode)clamped); }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio,
                $"Setting player {player + 1}'s drop-tune mode failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     There is an engine, so there are the v31 entry points: the ABI version
    ///     is compared for EQUALITY at open, so a DLL without them never becomes
    ///     an engine at all. The property exists so a surface ASKS instead of
    ///     assuming — the rule the parametric EQ is gated by.
    /// </remarks>
    public bool SupportsPerPlayerDropTune => _engine != null;

    /// <inheritdoc />
    public int ReadPlayerInputCaptureStream(int player, Span<float> destination, out ulong streamPosition)
    {
        streamPosition = 0;
        var engine = _engine;
        if (engine == null || !Dsp.DspPlayers.IsValid(player)) return 0;
        try { return engine.ReadInputCaptureStream(player, destination, out streamPosition); }
        catch { return 0; }
    }

    /// <inheritdoc />
    public int ReadPlayerInputCaptureWindow(int player, float[] destination)
    {
        var engine = _engine;
        if (engine == null || destination.Length == 0 || !Dsp.DspPlayers.IsValid(player)) return 0;
        // Windowed variant — the seat's own no-cursor read. Deliberately NOT
        // ReadInputCaptureStream: the tuner must not drain the samples
        // a streaming reader may be scoring that same seat from.
        try { return engine.ReadInputCapture(player, destination); }
        catch { return 0; }
    }

    /// <inheritdoc />
    public int LiveInputPlayerCount => _playerRouting.PlayerCount;

    /// <inheritdoc />
    public bool TryGetPlayerInputCaptureSync(int player, out ulong capturedSamples, out long hostTicks)
    {
        capturedSamples = 0;
        hostTicks = 0;
        var engine = _engine;
        if (engine == null || !Dsp.DspPlayers.IsValid(player)) return false;
        try { return engine.TryGetInputCaptureSync(player, out capturedSamples, out hostTicks); }
        catch { return false; }
    }

    /// <inheritdoc />
    public float GetAndResetPlayerInputPeak(int player)
    {
        var engine = _engine;
        if (engine == null || !Dsp.DspPlayers.IsValid(player)) return 0f;
        try { return engine.GetAndResetPlayerInputPeak(player); }
        catch { return 0f; }
    }
}
