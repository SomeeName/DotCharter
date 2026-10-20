// Partial: master-bus mixer state — song/input/monitor gain, pan, pitch shift and the three fixed high/low/band-pass filters.

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService
{
    public double Volume
    {
        get => _volume;
        set
        {
            lock (_gate) { _volume = value; ApplyGain(); }
            _engine?.SetMasterGain((float)Math.Clamp(value, 0.0, 4.0));
        }
    }

    public double SongVolume
    {
        get => _songVolume;
        set { lock (_gate) { _songVolume = value; ApplyGain(); } }
    }

    private void ApplyGain()
    {
        // Caller must hold _gate. We iterate _stems here; LoadStemsAsync and
        // ClearStems mutate the dict under the same lock, so this is safe as
        // long as our callers obey the contract.
        // When any stem is soloed the user wants ONLY that stem audible; that
        // means the original master mix must drop out too, regardless of the
        // explicit master-mute toggle (the toggle is restored as soon as the
        // solo is released).
        var anySolo = false;
        foreach (var s in _stems.Values) if (s.UserSoloed) { anySolo = true; break; }

        var src = _source;
        if (src != null)
        {
            var silenced = _masterMixMuted || anySolo;
            try { src.Volume = silenced ? 0f : (float)Math.Clamp(_songVolume, 0.0, 4.0); }
            catch { /* source disposed mid-call */ }
        }
        ApplyStemGainsLocked();
    }

    private void ApplyStemGainsLocked()
    {
        if (_stems.Count == 0) return;
        var anySolo = false;
        foreach (var s in _stems.Values) if (s.UserSoloed) { anySolo = true; break; }
        // Stems must also respond to the song-master slider. Applying _songVolume to
        // _source alone would leave the master fader without effect when stems are active and the
        // master mix is auto-muted.
        var master = (float)Math.Clamp(_songVolume, 0.0, 4.0);
        foreach (var s in _stems.Values)
        {
            var src = s.Source;
            if (src == null) continue;
            var silenced = s.UserMuted || (anySolo && !s.UserSoloed);
            var g = silenced ? 0f : (float)Math.Clamp(s.UserVolume, 0.0, 4.0) * master;
            try { src.Volume = g; } catch { /* ignored */ }
        }
    }

    public double InputVolume
    {
        get => _inputVolume;
        set { _inputVolume = value; _engine?.SetInputGain((float)Math.Clamp(value, 0.0, 8.0)); }
    }

    public double MonitorVolume
    {
        get => _monitorVolume;
        set { _monitorVolume = value; _engine?.SetMonitorGain((float)Math.Clamp(value, 0.0, 4.0)); }
    }

    private double _inputPitchShiftSemitones;

    /// <summary>
    ///     Seat 0's drop-tune pedal. Since ABI v31 that is literally what it is:
    ///     it goes through <see cref="SetPlayerInputPitchShift" />, so the seat-0
    ///     value, the state the engine rebuild replays and whatever a per-seat
    ///     surface shows are ONE value rather than two that drift.
    /// </summary>
    public double InputPitchShiftSemitones
    {
        get => _inputPitchShiftSemitones;
        set => SetPlayerInputPitchShift(0, value);
    }

    private int _inputPitchShiftMode;

    /// <summary>
    ///     Seat 0's drop-tune splice-lag mode.
    ///     <para>
    ///         <b>It must not be clamped to <c>value == 1 ? 1 : 0</c></b>, which is
    ///         the old two-engine scale — the LIVE push of "Smooth"
    ///         (2) would store and send Tight (0). That is the same shape
    ///         <c>DropTunerEngineMap</c> exists to end, and it would hide HERE
    ///         because the config write is correct: choosing Smooth works after an
    ///         app restart, so the one mode you would restart to audition was the
    ///         one you could not hear. The range is 0..2 and the native export
    ///         REJECTS anything else.
    ///     </para>
    /// </summary>
    public int InputPitchShiftMode
    {
        get => _inputPitchShiftMode;
        set => SetPlayerInputPitchShiftMode(0, value);
    }

    private double _pan;

    public double Pan
    {
        get => _pan;
        set
        {
            _pan = value;
            var v = (float)Math.Clamp(value, -1.0, 1.0);
            try { _source?.Pan = v; } catch { /* ignored */ }
            lock (_gate)
            {
                foreach (var s in _stems.Values) { try { s.Source?.Pan = v; } catch { /* ignored */ } }
            }
        }
    }

    private double _pitchShift;

    public double PitchShift
    {
        get => _pitchShift;
        set
        {
            _pitchShift = value;
            var v = (float)Math.Clamp(value, -24.0, 24.0);
            try { _source?.PitchSemitones = v; } catch { /* ignored */ }
            lock (_gate)
            {
                // Stems share a single Rubber Band stretcher on the engine
                // so tempo/pitch are applied to their SUM (phase-coherent).
                if (_stems.Count > 0 && _engine != null)
                {
                    try { _engine.StemGroupPitchSemitones = v; } catch { /* ignored */ }
                }
            }
        }
    }

    private double _hpFreq, _hpQ = 0.707;

    public double HighPassFreq
    {
        get => _hpFreq;
        set { _hpFreq = value; _engine?.SetHighPass((float)Math.Max(0.0, value), (float)Math.Max(0.05, _hpQ)); }
    }

    public double HighPassQ
    {
        get => _hpQ;
        set { _hpQ = value; _engine?.SetHighPass((float)Math.Max(0.0, _hpFreq), (float)Math.Max(0.05, value)); }
    }

    private double _lpFreq, _lpQ = 0.707;

    public double LowPassFreq
    {
        get => _lpFreq;
        set { _lpFreq = value; _engine?.SetLowPass((float)Math.Max(0.0, value), (float)Math.Max(0.05, _lpQ)); }
    }

    public double LowPassQ
    {
        get => _lpQ;
        set { _lpQ = value; _engine?.SetLowPass((float)Math.Max(0.0, _lpFreq), (float)Math.Max(0.05, value)); }
    }

    private double _bpFreq, _bpQ = 0.707;

    public double BandPassFreq
    {
        get => _bpFreq;
        set { _bpFreq = value; _engine?.SetBandPass((float)Math.Max(0.0, value), (float)Math.Max(0.05, _bpQ)); }
    }

    public double BandPassQ
    {
        get => _bpQ;
        set { _bpQ = value; _engine?.SetBandPass((float)Math.Max(0.0, _bpFreq), (float)Math.Max(0.05, value)); }
    }
}
