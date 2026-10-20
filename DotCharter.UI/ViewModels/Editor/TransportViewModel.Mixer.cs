// TransportViewModel: the Mixer - gain staging with the solo/mute matrix, the six-band EQ, pan, speed and the per-seat drop-tune pedal (the app's one IGuitarMonitorMixer).
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Constants;
using DotCharter.Services.Audio.Coordination;

namespace DotCharter.UI.ViewModels.Editor;

// Mixer surface of the transport: gain staging (master/song/input/MIDI/
// monitor volumes with the solo/mute matrix), EQ filters and curve, pan,
// playback speed and pitch shift. Backing fields live in the root file.
//
// It is also the app's ONE `IGuitarMonitorMixer` (0.56): the chart editor's tone
// preview has to be able to tell whether the guitar can actually be heard, and
// to put back exactly what it changed. Declared here rather than on the root
// partial because every member of that contract is on this surface.
public partial class TransportViewModel : IGuitarMonitorMixer
{
    /// <summary>
    ///     The Mixer tab's six-band parametric EQ (ABI v29).
    ///     <para>
    ///         It REPLACES the three fixed filters this file drives
    ///         (<c>HighPassFreq</c> &amp; co, still below and still on the
    ///         service — the ABI's three fixed sections are unchanged and run
    ///         AHEAD of the six bands, so nothing that used them broke). The
    ///         inspector does not surface them: six typed bands with per-band
    ///         gain, solo, routing and a trim is a strict superset, and showing
    ///         both would give the user two EQs over one signal with no way to
    ///         tell which one they were hearing.
    ///     </para>
    /// </summary>
    public Inspector.EqModuleViewModel Eq { get; private set; } = null!;

    public double Volume
    {
        get => _volume;
        set => SetAndSnap(ref _volume, value, _ => ApplyVolumes());
    }

    public double SongVolume
    {
        get => _songVolume;
        set => SetAndSnap(ref _songVolume, value, _ => ApplyVolumes());
    }

    public double InputVolume
    {
        get => _inputVolume;
        set => SetAndSnap(ref _inputVolume, value, _ => ApplyVolumes());
    }

    public double MidiVolume
    {
        get => _midiVolume;
        set => SetAndSnap(ref _midiVolume, value, _ => ApplyVolumes());
    }

    public double MonitorVolume
    {
        get => _monitorVolume;
        set => SetAndSnap(ref _monitorVolume, value, v => _audioService.MonitorVolume = v);
    }

    /// <summary>The engine's audio-callback load (ABI v46) for the rig footer's CPU
    /// readout; null while no engine runs.</summary>
    public AudioCallbackLoad? ReadCallbackLoad()
        => _audioService.TryGetCallbackLoad(out var load) ? load : null;

    /// <summary>
    /// Digital drop-tune pedal, in semitones (negative = drop, 0 = bypass).
    /// Applied to the live input in front of detection, the FX chain and
    /// monitoring; persisted so the virtual pedalboard survives restarts.
    /// </summary>
    /// <remarks>
    ///     <b>Per SEAT since ABI v31</b> — it reads and writes
    ///     <see cref="DropTunePlayer" />'s pedal, and that seat is the one
    ///     <see cref="DropTuneSeat" /> names. Until v31 the engine held ONE
    ///     shifter ahead of the per-player split, so on a couch with two guitars
    ///     in different tunings, dropping one dropped BOTH and the seat that did
    ///     not ask for it was detuned with nothing on screen saying so.
    ///     <para>
    ///         Only SEAT 0 is persisted. <c>AppConfig.InputPitchShiftSemis</c> is
    ///         one integer and the higher seats belong to a roster, not to the
    ///         install — writing them there would restore a second player's drop
    ///         tune onto a solo session. They live in
    ///         <c>PlayerInputRoutingState</c> for the run, which is what carries
    ///         them across an engine rebuild.
    ///     </para>
    /// </remarks>
    public int InputPitchShiftSemis
    {
        get => _pitchShiftSemisBySeat[DropTunePlayer];
        set
        {
            var seat = DropTunePlayer;
            // Down only: the engine's shifter cannot tune up (RigDropTuneViewModel).
            var clamped = Math.Clamp(value, -12, 0);
            if (_pitchShiftSemisBySeat[seat] == clamped) return;
            _pitchShiftSemisBySeat[seat] = clamped;
            if (seat == 0) _inputPitchShiftSemis = clamped;

            if (_audioService is IPlayerInputRouting routing)
            {
                routing.SetPlayerInputPitchShift(seat, clamped);
            }
            else if (seat == 0)
            {
                // No per-seat surface behind this implementation: seat 0 is the
                // only pedal there is, and the picker is not offered.
                _audioService.InputPitchShiftSemitones = clamped;
            }

            if (seat == 0)
            {
                var cfg = _configService.LoadAppConfig();
                cfg.InputPitchShiftSemis = clamped;
                _configService.SaveAppConfig(cfg);
            }

            OnPropertyChanged();
        }
    }

    private readonly int[] _pitchShiftSemisBySeat =
        new int[DotCharter.Tone.Audio.Dsp.DspPlayers.MaxPlayers];

    /// <summary>
    ///     Which seat's drop-tune pedal <see cref="InputPitchShiftSemis" />
    ///     addresses. Null / seat 0 for every solo session, where the picker is
    ///     not drawn at all.
    ///     <para>
    ///         Deliberately over the SAME <see cref="VstSeats" /> collection the
    ///         VST rows offer: one roster, one list of seats. Two surfaces
    ///         resolving it separately is how they come to disagree about which
    ///         seats exist.
    ///     </para>
    /// </summary>
    public Tone.UI.ViewModels.PlayMode.RigSeatOption? DropTuneSeat
    {
        get => _dropTuneSeat;
        set
        {
            if (ReferenceEquals(_dropTuneSeat, value)) return;
            _dropTuneSeat = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DropTunePlayer));
            // The combo shows the NEW seat's pedal, not the one just left.
            OnPropertyChanged(nameof(InputPitchShiftSemis));
        }
    }

    private Tone.UI.ViewModels.PlayMode.RigSeatOption? _dropTuneSeat;

    /// <summary>The seat every drop-tune edit lands on. 0 whenever nothing has
    /// been selected, so no path can silently detune a seat the roster does not
    /// have.</summary>
    public int DropTunePlayer
    {
        get
        {
            var seat = _dropTuneSeat?.Player ?? 0;
            return seat >= 0 && seat < _pitchShiftSemisBySeat.Length ? seat : 0;
        }
    }

    /// <summary>Whether to offer the drop-tune seat picker at all. One seat is
    /// not a choice, and a dropdown over it is a live-looking control over
    /// nothing.</summary>
    public bool HasMultipleDropTuneSeats => VstSeats.Count > 1;

    /// <summary>
    ///     Playback speed, stored at ONE-PERCENT resolution.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately NOT <see cref="SetAndSnap" />, which quantizes to the
    ///         0.05 grid the gain sliders use. That grid silently ate the fine
    ///         speed keybind: <c>IncreaseSpeed(0.01)</c> from 1.00 produced 1.01,
    ///         rounded back to 1.00, and the setter saw no change — so the step
    ///         could never accumulate either, because the next press read the
    ///         snapped field again. The row did nothing, at any speed, forever.
    ///     </para>
    ///     <para>
    ///         Two decimals is what the readouts print (<c>{0:0.00}x</c>) and what
    ///         the presets are expressed in, and the second branch re-announces so
    ///         a slider that produced an off-grid value is pulled back onto it —
    ///         the same shape <see cref="SetAndSnap" /> uses for that reason.
    ///     </para>
    /// </remarks>
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            var snapped = Math.Round(value, 2);
            if (Math.Abs(_playbackSpeed - snapped) > 0.0005)
            {
                _playbackSpeed = snapped;
                _audioService.PlaybackSpeed = snapped;
                OnPropertyChanged();
            }
            else if (Math.Abs(value - snapped) > 0.0005)
            {
                OnPropertyChanged();
            }
        }
    }

    public double HighPassSliderValue
    {
        get => FreqToSlider(HighPassFreq);
        set
        {
            HighPassFreq = SliderToFreq(value);
            _audioService.HighPassFreq = HighPassFreq;
            UpdateEqCurve();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HighPassFreq));
        }
    }

    public double LowPassSliderValue
    {
        get => FreqToSlider(LowPassFreq);
        set
        {
            LowPassFreq = SliderToFreq(value);
            _audioService.LowPassFreq = LowPassFreq;
            UpdateEqCurve();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LowPassFreq));
        }
    }

    public double BandPassSliderValue
    {
        get => FreqToSlider(BandPassFreq);
        set
        {
            BandPassFreq = SliderToFreq(value);
            _audioService.BandPassFreq = BandPassFreq;
            UpdateEqCurve();
            OnPropertyChanged();
            OnPropertyChanged(nameof(BandPassFreq));
        }
    }

    private void SetAndSnap(ref double field, double incomingValue, Action<double> updateAction,
        [CallerMemberName] string? propertyName = null)
    {
        var snapped = Math.Round(Math.Round(incomingValue / 0.05) * 0.05, 2);
        if (Math.Abs(field - snapped) > 0.001)
        {
            field = snapped;
            updateAction(snapped);
            OnPropertyChanged(propertyName);
        }
        else if (Math.Abs(incomingValue - snapped) > 0.001)
        {
            OnPropertyChanged(propertyName);
        }
    }

    /// <summary>
    ///     Any channel soloed ⇒ every channel that is NOT soloed is silent,
    ///     whatever its mute flag says (see <see cref="ApplyVolumes" />). One
    ///     definition, because the tone preview asks the same question the mix
    ///     itself answers — a second copy is how the two come apart.
    /// </summary>
    public bool IsAnythingSoloed => IsSongSoloed || IsInputSoloed || IsMidiSoloed;

    private void ApplyVolumes()
    {
        var anySolo = IsAnythingSoloed;

        var effSong = _songVolume;
        if (IsSongMuted || (anySolo && !IsSongSoloed)) effSong = 0;

        var effInput = _inputVolume;
        if (IsInputMuted || (anySolo && !IsInputSoloed)) effInput = 0;

        var effMidi = _midiVolume;
        if (IsMidiMuted || (anySolo && !IsMidiSoloed)) effMidi = 0;

        _audioService.Volume = IsMasterMuted ? 0 : _volume;
        _audioService.SongVolume = effSong;
        _audioService.InputVolume = effInput;
        _audioService.VstInstrumentVolume = effMidi;
        // MIDI in is the chart's instrument, whichever plays it: the plugin or the
        // built-in synth, with the same mute and solo.
        if (ChartSynth is { } synth) synth.ChartSynthGain = (float)effMidi;

        // The Mix popover's QUICK BALANCE lights whichever of the three the
        // levels currently ARE. Announced from here because that is the one
        // place every level change funnels through — and derived rather than
        // remembered, because a "which chip did I press" flag survives the
        // slider being dragged and then lights the wrong one.
        OnPropertyChanged(nameof(IsMixSongOnly));
        OnPropertyChanged(nameof(IsMixEven));
        OnPropertyChanged(nameof(IsMixGuitarOnly));
    }

    /// <summary>Half a percent, the same tolerance the speed presets use — below
    /// what a chip could meaningfully claim and well above round-trip error.</summary>
    private const double BalanceTolerance = 0.005;

    private static bool Near(double a, double b) => Math.Abs(a - b) < BalanceTolerance;

    /// <inheritdoc cref="SetMixBalance" />
    public bool IsMixSongOnly => Near(_songVolume, 1.0) && Near(_inputVolume, 0.0);

    /// <inheritdoc cref="SetMixBalance" />
    public bool IsMixEven => Near(_songVolume, 1.0) && Near(_inputVolume, 1.0);

    /// <inheritdoc cref="SetMixBalance" />
    public bool IsMixGuitarOnly => Near(_songVolume, 0.0) && Near(_inputVolume, 1.0);

    partial void OnIsSongMutedChanged(bool value)
    {
        _ = value;
        ApplyVolumes();
    }

    partial void OnIsSongSoloedChanged(bool value)
    {
        _ = value;
        ApplyVolumes();
    }

    partial void OnIsMasterMutedChanged(bool value)
    {
        _ = value;
        ApplyVolumes();
    }

    partial void OnIsInputMutedChanged(bool value)
    {
        _ = value;
        ApplyVolumes();
    }

    partial void OnIsInputSoloedChanged(bool value)
    {
        _ = value;
        ApplyVolumes();
    }

    partial void OnIsMidiMutedChanged(bool value)
    {
        _ = value;
        ApplyVolumes();
    }

    partial void OnIsMidiSoloedChanged(bool value)
    {
        _ = value;
        ApplyVolumes();
    }

    public void IncreaseSpeed(double step) => PlaybackSpeed = Math.Min(ChartEditorConstants.PlaybackSpeedMax, PlaybackSpeed + step);

    public void DecreaseSpeed(double step) => PlaybackSpeed = Math.Max(ChartEditorConstants.PlaybackSpeedMin, PlaybackSpeed - step);

    partial void OnPanChanged(double value)
    {
        _audioService.Pan = value;
    }

    partial void OnPitchShiftChanged(double value)
    {
        _audioService.PitchShift = value;
    }

    // Logarithmic slider conversions
    public double FreqToSlider(double freq)
    {
        var logMin = Math.Log10(20);
        var logMax = Math.Log10(20000);
        return (Math.Log10(Math.Clamp(freq, 20, 20000)) - logMin) / (logMax - logMin) * 100.0;
    }

    public double SliderToFreq(double sliderVal)
    {
        var logMin = Math.Log10(20);
        var logMax = Math.Log10(20000);
        return Math.Pow(10, logMin + Math.Clamp(sliderVal, 0, 100) / 100.0 * (logMax - logMin));
    }

    partial void OnHighPassQChanged(double value)
    {
        _audioService.HighPassQ = value;
    }

    partial void OnLowPassQChanged(double value)
    {
        _audioService.LowPassQ = value;
    }

    partial void OnBandPassQChanged(double value)
    {
        _audioService.BandPassQ = value;
    }

    partial void OnHighPassFreqChanged(double value)
    {
        _audioService.HighPassFreq = value;
        UpdateEqCurve();
        OnPropertyChanged(nameof(HighPassSliderValue));
    }

    partial void OnLowPassFreqChanged(double value)
    {
        _audioService.LowPassFreq = value;
        UpdateEqCurve();
        OnPropertyChanged(nameof(LowPassSliderValue));
    }

    partial void OnBandPassFreqChanged(double value)
    {
        _audioService.BandPassFreq = value;
        UpdateEqCurve();
        OnPropertyChanged(nameof(BandPassSliderValue));
    }

    private void UpdateEqCurve()
    {
        var logMin = Math.Log10(20);
        var logRange = Math.Log10(20000) - logMin;

        double GetX(double f) => (Math.Log10(Math.Clamp(f, 20, 20000)) - logMin) / logRange * 100.0;

        var hpX = GetX(HighPassFreq);
        var lpX = Math.Max(hpX + 1, GetX(LowPassFreq));

        EqCurveData =
        [
            new(0, 100),
            new(Math.Max(0, hpX - 4), 100),
            new(hpX, 0),
            new(lpX, 0),
            new(Math.Min(100, lpX + 4), 100),
            new(100, 100),
        ];
    }

    /// <summary>
    /// Resets one mixer/DSP parameter to its default value. Keys match the
    /// slider names in MixerInspectorView; the defaults live here so views
    /// never carry domain constants (butterworth Q, filter band edges, ...).
    /// </summary>
    [RelayCommand]
    private void ResetMixerParameter(string? key)
    {
        switch (key)
        {
            case "MasterSlider": Volume = 1.0; break;
            case "SongGainSlider": SongVolume = 1.0; break;
            case "InputGainSlider": InputVolume = 1.0; break;
            case "MidiInstSlider": MidiVolume = 1.0; break;
            case "TrackPanSlider": Pan = 0.0; break;
            case "SpeedSlider": PlaybackSpeed = 1.0; break;
            case "PitchShiftSlider": PitchShift = 0; break;
            case "HighPassSlider": HighPassFreq = 20; break;
            case "HighPassQSlider": HighPassQ = 0.707; break;
            case "LowPassSlider": LowPassFreq = 20000; break;
            case "LowPassQSlider": LowPassQ = 0.707; break;
            case "BandPassSlider": BandPassFreq = 20; break;
            case "BandPassQSlider": BandPassQ = 0.707; break;
        }
    }

    /// <summary>
    ///     The Mix popover's QUICK BALANCE row: the three mixes a charter
    ///     actually switches between while checking a pass — the backing track
    ///     alone, both together, or just what they are playing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         These move <see cref="SongVolume" />, never <see cref="Volume" />.
    ///         Master gain is applied LAST on the device bus in the engine, so it
    ///         scales the monitored guitar along with everything else: driving
    ///         these chips from master made "guitar only" set it to zero and
    ///         silence the whole output — the one mix it was meant to isolate —
    ///         while its chip lit up, because the indicator read the same field.
    ///     </para>
    ///     A STRING parameter, never a double: a <c>RelayCommand&lt;T&gt;</c> of a
    ///     value type fed a literal <c>CommandParameter</c> throws during layout
    ///     (DotCharter.UI/AGENTS.md), which is a clean build and a dead view.
    /// </remarks>
    [RelayCommand]
    private void SetMixBalance(string? which)
    {
        switch (which)
        {
            case "song": SongVolume = 1.0; InputVolume = 0.0; break;
            case "even": SongVolume = 1.0; InputVolume = 1.0; break;
            case "guitar": SongVolume = 0.0; InputVolume = 1.0; break;
        }
    }

    /// <summary>
    ///     The Mix popover's footer: every level back to unity at once — master
    ///     included, because the popover does not show master and a bus left
    ///     at 0.2 is otherwise only recoverable from the Mixer inspector.
    /// </summary>
    [RelayCommand]
    private void ResetMixLevels()
    {
        Volume = 1.0;
        SongVolume = 1.0;
        InputVolume = 1.0;
        MidiVolume = 1.0;
    }

    /// <summary>
    ///     The Tempo popover's SPEED PRESETS. String parameter for the reason
    ///     above, and parsed invariantly so a German locale does not read "0.65"
    ///     as 65.
    /// </summary>
    [RelayCommand]
    private void SetSpeedPreset(string? percent)
    {
        if (double.TryParse(percent, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            PlaybackSpeed = value;
    }

    /// <summary>The Tempo popover's footer: back to 1.00x / 0 st.</summary>
    [RelayCommand]
    private void ResetTempo()
    {
        PlaybackSpeed = 1.0;
        PitchShift = 0;
    }
}
