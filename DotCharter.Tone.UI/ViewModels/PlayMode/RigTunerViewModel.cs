using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Tone.Audio.Tuner;
using DotCharter.Core.Domain.Project;
using DotCharter.Tone.Audio.Tuning;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// The pedalboard's in-rig tuner panel (AmpliTube parity): a compact
/// strobe + needle card driven by the EXISTING managed tuner suite —
/// <see cref="TunerPitchEngine"/> (MPM→YIN coarse pitch),
/// <see cref="VirtualStrobe"/> (single-bin DFT fine cents; fed the two
/// contiguous halves of each snapshot window) and
/// <see cref="TunerDisplaySmoother"/> (median→EMA + stability hold) — off the
/// non-destructive <c>IAudioPlaybackService.ReadInputCaptureWindow</c> tap.
/// <para>
/// Polling follows the MenuBackdropControl gating pattern: the UI-side timer
/// runs ONLY while the panel is open AND at least one hosting
/// <c>PedalboardView</c> is attached + effectively visible
/// (<see cref="SetSurfaceLive"/> — the editor and Play Mode host the same
/// singleton VM, hence a per-surface set, not a flag). Closed / hidden /
/// detached ⇒ the timer stops, nothing touches the audio engine. The timer is
/// created lazily on first start so the VM constructs without an Avalonia
/// dispatcher (unit tests). Display math is the pure <see cref="RigTunerMath"/>.
/// </para>
/// <para>
/// <b>Per SEAT.</b> Every seat has its own capture
/// ring (ABI v28), and this panel must not read the GLOBAL tap for all of them —
/// so on a two-guitar couch, "tune your guitar" tuned player 1's, whichever
/// seat's rig was on screen. <see cref="Player"/> follows the rig's own seat
/// picker (<c>PedalboardViewModel.SelectedSeat</c>), which is what keeps the
/// needle and the pedals a player is editing on ONE guitar. Seat 0 still reads
/// the global window, so nothing about a solo tune changed.
/// </para>
/// </summary>
public sealed partial class RigTunerViewModel : ObservableObject
{
    private const int WindowSize = 4096;
    private const int StrobeBlock = WindowSize / 2;
    private const float RmsFloor = 0.005f;

    // Needle bar geometry, shared with the panel XAML (bar 220 px, needle 4).
    private const double BarWidthPx = 220;
    private const double NeedleWidthPx = 4;

    private readonly IAudioPlaybackService? _audio;
    private readonly IPlayerInputRouting? _routing;
    private readonly IRigConfigService? _config;
    private readonly ILocalizationService _loc;
    private readonly Stopwatch _clock = new();
    private readonly float[] _window = new float[WindowSize];
    private readonly HashSet<object> _liveSurfaces = [];
    private readonly TunerDisplaySmoother _smoother = new();

    private DispatcherTimer? _timer;
    private TunerPitchEngine? _engine;
    private VirtualStrobe? _strobe;
    private int _engineSampleRate;
    private int _currentMidi = -1;
    private double _lastTickMs;

    /// <param name="audio">The transport; supplies the sample rate and seat 0's
    /// global capture window.</param>
    /// <param name="localization">The words of its hints and tuning picker.</param>
    /// <param name="routing">The per-seat live-input surface (ABI v28). Null on
    /// an implementation with no engine behind it, in which case only seat 0 can
    /// be read — which is exactly what <see cref="Player"/> is then clamped to.</param>
    /// <param name="config">Where the player's concert pitch lives
    /// (<c>AppConfig.TunerReferenceHz</c>). OPTIONAL and last, so every existing
    /// caller is unchanged: a tuner with no config tunes to A440 and remembers
    /// nothing, which is what it did before.</param>
    public RigTunerViewModel(IAudioPlaybackService? audio, ILocalizationService localization,
        IPlayerInputRouting? routing = null, IRigConfigService? config = null)
    {
        _audio = audio;
        _loc = localization;
        _routing = routing ?? audio as IPlayerInputRouting;
        _config = config;
        _referenceHz = LoadPersistedReference();
        Tunings = [new RigTunerTuningChoice(null, localization), .. TunerTunings.All.Select(t => new RigTunerTuningChoice(t, localization))];
        _selectedTuning = Tunings.FirstOrDefault(c => c.Tuning?.Id == LoadPersistedTuning()) ?? Tunings[0];
        LoadTargets();
        // The footer draws this hint whenever there is no reading, and the tick
        // is the only other writer — so a tuner with no transport behind it
        // would print an EMPTY string next to a still needle and read as a
        // control with no function. It is the one state the poll can never
        // report, because with no transport it never runs.
        if (_audio is null)
        {
            _statusHint = _loc["Tuner_NoInput"];
            _statusShort = _loc["Tuner_NoInput_Short"];
        }
    }

    /// <summary>
    ///     Which seat this needle is listening to. 0 — the default — is the
    ///     single-player tap byte-for-byte.
    ///     <para>
    ///         Written by <c>PedalboardViewModel.OnSelectedSeatChanged</c> so the
    ///         tuner and the board are always looking at the same guitar. A seat
    ///         with no per-seat routing behind it falls back to 0 rather than
    ///         reading nothing: an unreachable seat must not present as a silent
    ///         guitar.
    ///     </para>
    /// </summary>
    public int Player
    {
        get => _player;
        set
        {
            var wanted = _routing is null ? 0 : Math.Max(0, value);
            if (wanted == _player) return;
            _player = wanted;
            // A different guitar is a different signal: keep no smoothing, no
            // strobe lock and no note from the seat we just left.
            _smoother.Reset();
            _strobe?.Reset();
            _currentMidi = -1;
            ResetReadingState();
            OnPropertyChanged(nameof(Player));
            OnPropertyChanged(nameof(IsSeatLive));
        }
    }

    private int _player;

    /// <summary>
    ///     Whether the engine is actually RUNNING the seat this panel is pointed
    ///     at. Seat 0 always is; a higher seat only while the input
    ///     configuration (or an override) has raised the live-input player count, because the audio callback writes no
    ///     capture ring for a seat it does not walk.
    ///     <para>
    ///         The tick prints <c>Tuner_SeatNotLive</c> instead of standing on
    ///         "Listening…" forever when this is false — an unlit needle over a
    ///         seat that can never produce one is the same lie as a permanent
    ///         zero score with no reason beside it.
    ///     </para>
    /// </summary>
    public bool IsSeatLive =>
        Player <= 0 || (_routing is not null && Player < _routing.LiveInputPlayerCount);

    // ---- concert pitch (AppConfig.TunerReferenceHz) --------------------------

    /// <summary>
    ///     The concert pitch this needle judges against — A4 in Hz, 440 by
    ///     default, clamped to <see cref="RigTunerMath.MinReferenceHz" />..
    ///     <see cref="RigTunerMath.MaxReferenceHz" /> and persisted as
    ///     <c>AppConfig.TunerReferenceHz</c>.
    ///     <para>
    ///         It moves the whole note grid: the nearest-note mapping, the cents
    ///         readout, the needle and the strobe's target all go through
    ///         <see cref="RigTunerMath" />'s reference overloads, so at A442 a
    ///         string at 442 Hz reads <b>0 cents</b> rather than +8. A tuner that
    ///         knew only 440 was useless to anyone playing with an orchestra or to
    ///         a piano that has settled.
    ///     </para>
    /// </summary>
    public double ReferenceHz
    {
        get => _referenceHz;
        set
        {
            var clamped = RigTunerMath.ClampReference(value);
            if (Math.Abs(clamped - _referenceHz) < 1e-9) return;
            _referenceHz = clamped;
            // A different reference is a different target: keep no smoothing, no
            // strobe lock and no note from the pitch we just left, exactly as a
            // seat change does.
            _smoother.Reset();
            _strobe?.Reset();
            _currentMidi = -1;
            ResetReadingState();
            PersistReference(clamped);
            LoadTargets();
            OnPropertyChanged(nameof(ReferenceHz));
            OnPropertyChanged(nameof(ReferenceLabel));
            OnPropertyChanged(nameof(IsStandardReference));
        }
    }

    private double _referenceHz = RigTunerMath.DefaultReferenceHz;

    /// <summary>"A4 = 440 Hz" — the footer's reference readout. Formatted through
    /// the localization service, so the number follows the user's own region while
    /// the sentence follows the UI language.</summary>
    public string ReferenceLabel =>
        _loc.Format("Rig_TunerReference", Math.Round(ReferenceHz));

    /// <summary>False as soon as the player has moved off A440 — what a
    /// "non-standard pitch" hint binds to, because a needle silently judging
    /// against 442 is the kind of surprise that reads as a broken tuner.</summary>
    public bool IsStandardReference =>
        Math.Abs(ReferenceHz - RigTunerMath.DefaultReferenceHz) < 0.5;

    private double LoadPersistedReference()
    {
        if (_config == null) return RigTunerMath.DefaultReferenceHz;
        try { return RigTunerMath.ClampReference(_config.LoadRigConfig().TunerReferenceHz); }
        catch { return RigTunerMath.DefaultReferenceHz; }
    }

    private void PersistReference(double value)
    {
        if (_config == null) return;
        try
        {
            if (Math.Abs(_config.LoadRigConfig().TunerReferenceHz - value) < 0.01) return;
            _config.UpdateRigConfig(c => c.TunerReferenceHz = value);
        }
        catch { /* a reference pitch is a preference — never let a config write break the tuner */ }
    }

    // ---- open/close + surface gating ---------------------------------------

    [ObservableProperty] private bool _isOpen;

    partial void OnIsOpenChanged(bool value) => SyncRunning();

    // No [RelayCommand] on this pair: the qualified reachability
    // pass (P2-14) would find their generated wrappers bound by NOTHING — they read
    // as reachable only because every dialog has an Open/CloseCommand of its
    // own. Open is called by AmpFxRigView (a live rig surface IS an open tuner)
    // and by PedalboardViewModel (selecting a Pitch-family pedal); Close is its
    // API pair. The third-verdict rule: the METHOD is live, the wrapper is the
    // dead part.
    //
    // There is no Toggle(): there is no tuner panel to toggle — the footer
    // draws the needle permanently — so nothing would call the method.
    // (RelayCommandReachabilityTests.KnownUnreachable only ever shrinks.)
    public void Open() => IsOpen = true;

    public void Close() => IsOpen = false;

    /// <summary>A hosting view reports whether its surface is live (attached +
    /// effectively visible). Two hosts share this singleton VM, so liveness is
    /// a set keyed by the view instance. The poll timer runs only while the
    /// panel is open AND any surface is live — never while hidden.</summary>
    public void SetSurfaceLive(object surface, bool live)
    {
        if (live) _liveSurfaces.Add(surface);
        else _liveSurfaces.Remove(surface);
        SyncRunning();
    }

    /// <summary>True while the poll timer is running (verification hook).</summary>
    public bool IsPolling => _timer?.IsEnabled == true;

    private void SyncRunning()
    {
        var live = IsOpen && _liveSurfaces.Count > 0 && _audio != null;
        if (live == IsPolling) return;

        if (live)
        {
            // Lazy timer: only ever created on a real UI surface.
            _timer ??= CreateTimer();
            _smoother.Reset();
            _strobe?.Reset();
            _currentMidi = -1;
            ResetReadingState();
            _clock.Restart();
            _lastTickMs = 0;
            _timer.Start();
        }
        else
        {
            _timer?.Stop();
            _clock.Stop();
        }

        OnPropertyChanged(nameof(IsPolling));
    }

    private DispatcherTimer CreateTimer()
    {
        // ~15 Hz: plenty for a needle/strobe, each tick reads well inside the
        // ~0.7 s input capture ring (same cadence as the TunerDialog).
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(66)
        };
        t.Tick += (_, _) => Tick();
        return t;
    }

    // ---- the tuning to tune TO (AppConfig.TunerTuning) -------------------------

    /// <summary>Chromatic first, then every <see cref="TunerTunings" /> entry.</summary>
    public IReadOnlyList<RigTunerTuningChoice> Tunings { get; }

    /// <summary>
    ///     The tuning the needle judges against. Chromatic names whatever note it
    ///     hears; a tuning snaps each reading to the NEAREST of its strings and
    ///     measures the cents off THAT string, so a guitar in E standard reads its
    ///     low string as "D2, +200 ¢" against Drop D and shows which way to turn.
    ///     Persisted as <c>AppConfig.TunerTuning</c>.
    /// </summary>
    public RigTunerTuningChoice SelectedTuning
    {
        get => _selectedTuning;
        set
        {
            var choice = value ?? Tunings[0];
            if (ReferenceEquals(choice, _selectedTuning)) return;
            _selectedTuning = choice;
            PersistTuning(choice.Tuning?.Id ?? TunerTunings.ChromaticId);
            LoadTargets();
            _currentMidi = -1;
            ResetReadingState();
            OnPropertyChanged(nameof(SelectedTuning));
            OnPropertyChanged(nameof(HasTuning));
        }
    }

    private RigTunerTuningChoice _selectedTuning;

    /// <summary>The chosen tuning as the AI tone designer is told it ("Drop D
    /// (D A D G B E)", "bass: Drop D (D A D G)"); null when chromatic. English on
    /// purpose: it goes to a model, not to the screen.</summary>
    public string? DesignTuning => _selectedTuning.Tuning is { } t
        ? $"{(t.IsBass ? "bass: " : string.Empty)}{t.Name} ({_selectedTuning.Notes})"
        : null;

    /// <summary>A tuning is chosen (the string chips show).</summary>
    public bool HasTuning => _selectedTuning.Tuning is not null;

    /// <summary>The chosen tuning's open strings, low first: the one being
    /// tuned lit, the ones heard in tune marked.</summary>
    public ObservableCollection<RigTunerStringChip> Strings { get; } = [];

    private readonly ArrangementTunerLogic _targets = new();

    /// <summary>The tuning's open-string targets at the current concert pitch.</summary>
    private void LoadTargets()
    {
        Strings.Clear();
        if (_selectedTuning?.Tuning is not { } tuning)
        {
            _targets.Load(ArrangementType.Lead, 0, [], 0);
            return;
        }

        var centOffset = 1200.0 * Math.Log2(ReferenceHz / RigTunerMath.DefaultReferenceHz);
        _targets.Load(tuning.IsBass ? ArrangementType.Bass : ArrangementType.Lead, tuning.Steps.Count, tuning.Steps, centOffset);
        foreach (var target in _targets.Targets) Strings.Add(new RigTunerStringChip(target.Midi));
    }

    /// <summary>The note a reading is judged against: the nearest string of the
    /// chosen tuning, or the nearest semitone when chromatic.</summary>
    internal int TargetMidiFor(double frequencyHz)
    {
        if (Strings.Count == 0) return RigTunerMath.NearestMidi(frequencyHz, ReferenceHz);
        var index = _targets.NearestString(frequencyHz);
        for (var i = 0; i < Strings.Count; i++) Strings[i].IsCurrent = i == index;
        return index < 0 ? -1 : _targets.Targets[index].Midi;
    }

    private string LoadPersistedTuning()
    {
        if (_config == null) return TunerTunings.ChromaticId;
        try { return TunerTunings.Find(_config.LoadRigConfig().TunerTuning)?.Id ?? TunerTunings.ChromaticId; }
        catch { return TunerTunings.ChromaticId; }
    }

    private void PersistTuning(string id)
    {
        if (_config == null) return;
        try
        {
            if (_config.LoadRigConfig().TunerTuning == id) return;
            _config.UpdateRigConfig(c => c.TunerTuning = id);
        }
        catch
        {
            // a tuner that cannot save its choice still tunes to it
        }
    }

    /// <summary>The poll's clock (ms). Tests replace it; the live tuner reads its stopwatch.</summary>
    internal Func<double>? TickClock { get; set; }

    // ---- display state --------------------------------------------------------

    [ObservableProperty] private bool _hasInput;
    [ObservableProperty] private bool _hasReading;
    [ObservableProperty] private string _noteText = "–";
    [ObservableProperty] private string _centsText = string.Empty;

    /// <summary>
    ///     The same deviation <see cref="CentsText" /> prints, as a number, so a
    ///     centre-anchored meter can be driven without re-parsing a formatted,
    ///     culture-dependent string. Clamped to the tuner's display range exactly
    ///     like the text is — one clamp, one value, two readouts that cannot
    ///     disagree.
    /// </summary>
    [ObservableProperty] private double _centsOff;
    [ObservableProperty] private double _needleLeft = (BarWidthPx - NeedleWidthPx) / 2;
    [ObservableProperty] private bool _isInTune;
    [ObservableProperty] private double _wheelPhase;
    [ObservableProperty] private string _statusHint = string.Empty;

    /// <summary>
    ///     <see cref="StatusHint" /> in the few words the footer's 86 px cell holds
    /// ("No engine", "Input off"); the sentence is the cell's tooltip.
    ///     The cell does not ellipsize the sentence to "Audio engine …", which says
    ///     nothing, with no tooltip to read the rest.
    /// </summary>
    [ObservableProperty] private string _statusShort = string.Empty;

    /// <summary>Sets the hint and its short form from one resx key (the short one is
    /// <c>key + "_Short"</c>); null clears both.</summary>
    private void SetStatus(string? key)
    {
        StatusHint = key is null ? string.Empty : _loc[key];
        StatusShort = key is null ? string.Empty : _loc[key + "_Short"];
    }

    // ---- the poll tick --------------------------------------------------------

    /// <summary>One poll. <c>internal</c> rather than private so the default gate
    /// can drive a real read without an Avalonia dispatcher or an audio
    /// device — the seam rule in <c>DotCharter.Tests/AGENTS.md</c>.</summary>
    internal void Tick()
    {
        if (_audio == null) return;
        var nowMs = TickClock?.Invoke() ?? _clock.Elapsed.TotalMilliseconds;
        var deltaMs = Math.Clamp(nowMs - _lastTickMs, 0, 200);
        _lastTickMs = nowMs;

        if (!TryEnsureEngine()) return;

        if (ReadSeatWindow() < WindowSize)
        {
            OnSilence(nowMs);
            return;
        }

        float sumSquares = 0;
        foreach (var s in _window) sumSquares += s * s;
        var rms = MathF.Sqrt(sumSquares / WindowSize);
        if (rms < RmsFloor)
        {
            OnSilence(nowMs);
            return;
        }

        var reading = _engine!.Detect(_window);
        if (!reading.HasPitch)
        {
            OnSilence(nowMs);
            return;
        }

        // Against the player's OWN concert pitch: at A442 the note grid, the
        // cents and the strobe target all move together.
        var midi = TargetMidiFor(reading.FrequencyHz);
        if (midi < 0)
        {
            OnSilence(nowMs);
            return;
        }

        if (midi != _currentMidi)
        {
            _currentMidi = midi;
            _strobe!.Retarget(RigTunerMath.FrequencyOfMidi(midi, ReferenceHz));
            _smoother.Reset();
        }

        // Coarse cents from the pitch engine; fine cents from the virtual
        // strobe when it locks. The two halves of ONE snapshot window are
        // contiguous by construction, so per tick the strobe restarts and
        // measures the phase drift across the window's midpoint.
        var cents = RigTunerMath.CentsOff(reading.FrequencyHz, midi, ReferenceHz);
        _strobe!.Reset();
        _strobe.Process(_window.AsSpan(0, StrobeBlock));
        var fine = _strobe.Process(_window.AsSpan(StrobeBlock, StrobeBlock));
        if (fine.HasSignal && fine.IsTracking &&
            Math.Abs(fine.CentsOffset) <= RigTunerMath.MaxDisplayCents)
        {
            cents = fine.CentsOffset;
        }

        var smoothed = _smoother.Process(cents, nowMs);

        HasReading = true;
        NoteText = ArrangementTunerLogic.NoteNameOf(midi);
        var clamped = Math.Clamp(smoothed.Cents, -RigTunerMath.MaxDisplayCents, RigTunerMath.MaxDisplayCents);
        CentsText = string.Create(CultureInfo.CurrentCulture, $"{clamped:+0;-0;±0} ¢");
        CentsOff = clamped;
        NeedleLeft = (BarWidthPx - NeedleWidthPx) * RigTunerMath.NeedleFraction(smoothed.Cents);
        IsInTune = smoothed.InTune;
        if (smoothed.InTune)
        {
            foreach (var chip in Strings)
                if (chip.IsCurrent) chip.IsTuned = true;
        }

        WheelPhase = RigTunerMath.AdvanceWheel(WheelPhase, smoothed.Cents, deltaMs);
        SetStatus(null);
    }

    /// <summary>
    ///     The latest capture window for <see cref="Player" /> — the seat's own
    ///     ring for a seat, the global tap for seat 0.
    ///     <para>
    ///         Both are the WINDOWED read, never the streaming one: seat N's
    ///         stream cursor belongs to a streaming reader, and a needle that
    ///         consumed it would steal its samples.
    ///     </para>
    /// </summary>
    private int ReadSeatWindow()
    {
        if (Player <= 0 || _routing is null) return _audio!.ReadInputCaptureWindow(_window);
        return _routing.ReadPlayerInputCaptureWindow(Player, _window);
    }

    private void OnSilence(double nowMs)
    {
        _smoother.ProcessSilence(nowMs);
        ResetReadingState();
    }

    private void ResetReadingState()
    {
        HasReading = false;
        IsInTune = false;
        NoteText = "–";
        CentsText = string.Empty;
        CentsOff = 0;
        NeedleLeft = (BarWidthPx - NeedleWidthPx) / 2;
        foreach (var chip in Strings) chip.IsCurrent = false;
        if (HasInput) SetStatus("Tuner_Listening");
    }

    /// <summary>(Re)creates the pitch engine + strobe once an input stream
    /// exists; tracks sample-rate changes while the panel sits open.</summary>
    private bool TryEnsureEngine()
    {
        var sampleRate = _audio!.InputSampleRate;
        if (sampleRate <= 0)
        {
            HasInput = false;
            SetStatus("Tuner_NoInput");
            return false;
        }

        // A seat the engine is not running has no ring to read: say so rather
        // than sitting on "Listening…" over a guitar that can never appear.
        if (!IsSeatLive)
        {
            HasInput = false;
            SetStatus("Tuner_SeatNotLive");
            return false;
        }

        if (_engine == null || _engineSampleRate != sampleRate)
        {
            _engineSampleRate = sampleRate;
            // 25 Hz floor covers drop-tuned basses (B0 ≈ 30.9 Hz) with margin.
            _engine = new TunerPitchEngine(sampleRate, WindowSize, 25.0, 1500.0);
            _strobe = new VirtualStrobe(sampleRate, 82.41, StrobeBlock); // retargeted per note
            _currentMidi = -1;
            _smoother.Reset();
        }

        if (!HasInput)
        {
            HasInput = true;
            SetStatus("Tuner_Listening");
        }

        return true;
    }
}

/// <summary>One row of the tuner's tuning picker: a tuning, or chromatic (null).</summary>
public sealed class RigTunerTuningChoice(TunerTuning? tuning, ILocalizationService localization)
{
    public TunerTuning? Tuning { get; } = tuning;

    /// <summary>"Drop D", "Bass · Drop D", or "Chromatic".</summary>
    public string Label => Tuning switch
    {
        null => localization["Rig_TunerChromatic"],
        { IsBass: true } bass => localization.Format("Rig_TunerBassTuning", bass.Name),
        var guitar => guitar.Name
    };

    /// <summary>The open strings, low first ("D A D G A D").</summary>
    public string Notes => Tuning is null ? string.Empty : string.Join(' ', TunerTuningNotes.Of(Tuning));

    public override string ToString() => Label;
}

/// <summary>One open string of the chosen tuning, as a chip in the tuner strip.</summary>
public sealed partial class RigTunerStringChip(int midi) : ObservableObject
{
    public int Midi { get; } = midi;

    /// <summary>The pitch letter without its octave ("D", "C#").</summary>
    public string Note { get; } = TunerTuningNotes.LetterOf(midi);

    /// <summary>The string the last reading was judged against.</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>Heard in tune since the tuning was picked.</summary>
    [ObservableProperty] private bool _isTuned;
}

/// <summary>Note letters for a tuning's open strings. Pure.</summary>
public static class TunerTuningNotes
{
    public static string LetterOf(int midi) =>
        ArrangementTunerLogic.NoteNameOf(midi).TrimEnd('-', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');

    public static IEnumerable<string> Of(TunerTuning tuning)
    {
        var logic = new ArrangementTunerLogic();
        logic.Load(tuning.IsBass ? ArrangementType.Bass : ArrangementType.Lead, tuning.Steps.Count, tuning.Steps);
        return logic.Targets.Select(t => LetterOf(t.Midi));
    }
}
