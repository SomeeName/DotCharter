using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Tuner;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>One string's live row in the guided tuning check. Mirrors the
/// pure <see cref="TuningCheckStringState"/> and carries the current cents/hold
/// so the pre-play screen can draw a per-string progress line.</summary>
public sealed partial class TuningStringRow : ObservableObject
{
    private readonly ILocalizationService _localization;

    public TuningStringRow(int index, string noteName, ILocalizationService localization,
        double inTuneCents = FireTunerScale.DefaultInTuneCents)
    {
        _localization = localization;
        Index = index;
        NoteName = noteName;
        InTuneCents = inTuneCents;
    }

    public int Index { get; }

    /// <summary>Target note name for this string (e.g. "E2") — from the check's
    /// derived <see cref="TuningCheckTarget"/> table.</summary>
    public string NoteName { get; }

    /// <summary>The judge's own tolerance, carried onto the row so the meter's
    /// band and the row's own verdict cannot disagree with
    /// <see cref="GuidedTuningCheck.InTuneCents"/>.</summary>
    public double InTuneCents { get; }

    /// <summary>The Fire token key for this string's FIXED identity colour, or
    /// the neutral one past the six Fire declares.</summary>
    public string ColorTokenKey =>
        FireStringIdentity.TokenKeyFor(Index) ?? FireStringIdentity.NeutralTokenKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmed))]
    [NotifyPropertyChangedFor(nameof(IsSkipped))]
    [NotifyPropertyChangedFor(nameof(IsClose))]
    [NotifyPropertyChangedFor(nameof(IsInTune))]
    [NotifyPropertyChangedFor(nameof(Band))]
    [NotifyPropertyChangedFor(nameof(IsReadingGood))]
    [NotifyPropertyChangedFor(nameof(IsReadingBad))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingPluck))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private TuningCheckStringState _state;

    [ObservableProperty] private bool _isCurrent;

    /// <summary>
    ///     Has this string been HEARD yet? Distinct from <see cref="Cents" />
    ///     being zero, which is what a never-plucked row would otherwise look
    ///     like — and "perfectly in tune" versus "you have not played it" is the
    ///     single most important distinction on this screen. Silence never
    ///     clears it: the reading stands until the check is restarted.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Band))]
    [NotifyPropertyChangedFor(nameof(IsReadingGood))]
    [NotifyPropertyChangedFor(nameof(IsReadingBad))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingPluck))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _hasReading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Band))]
    [NotifyPropertyChangedFor(nameof(IsReadingGood))]
    [NotifyPropertyChangedFor(nameof(IsReadingBad))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private double _cents;

    [ObservableProperty] private string _centsText = "";
    [ObservableProperty] private double _holdProgress;

    public bool IsConfirmed => State == TuningCheckStringState.Confirmed;
    public bool IsSkipped => State == TuningCheckStringState.Skipped;
    public bool IsClose => State == TuningCheckStringState.Close;
    public bool IsInTune => State == TuningCheckStringState.InTune;

    /// <summary>What the meter draws — the SAME pure function the meter itself
    /// uses, so the needle's colour and the row's right-hand state can never
    /// disagree.</summary>
    public FireTunerBand Band => FireTunerScale.BandFor(HasReading, Cents, InTuneCents);

    /// <summary>Green: this string is where the chart wants it. A confirmed
    /// string stays green even after the player stops holding it — the check
    /// has already accepted it.</summary>
    public bool IsReadingGood => IsConfirmed || Band == FireTunerBand.InTune;

    /// <summary>Amber: you have to do something to the guitar. Includes a
    /// SKIPPED string — skipping does not make it in tune.</summary>
    public bool IsReadingBad => !IsConfirmed && (IsSkipped || Band == FireTunerBand.OutOfTune);

    /// <summary>Dim: nothing has been heard on this string yet.</summary>
    public bool IsAwaitingPluck => !IsConfirmed && !IsSkipped && Band == FireTunerBand.NoReading;

    /// <summary>The right-hand state cell. Five real states, each visually
    /// distinct; the mockup draws three of them.</summary>
    public string StateText
    {
        get
        {
            var loc = _localization;
            if (IsConfirmed) return loc["PlayMode_GetReady_Tuner_Verified"];
            if (IsSkipped) return loc["PlayMode_GetReady_Tuner_Skipped"];
            return Band switch
            {
                FireTunerBand.NoReading => loc["PlayMode_GetReady_Tuner_Pluck"],
                FireTunerBand.InTune => loc["PlayMode_GetReady_Tuner_InTune"],
                _ => loc.Format("PlayMode_GetReady_Tuner_Cents", Cents)
            };
        }
    }
}

/// <summary>
///     Pre-play guided tuning check: drives the tested, pure
///     <see cref="GuidedTuningCheck"/> state machine (one string at a time,
///     lowest first) off the non-destructive input-capture tap, exactly the way
///     <see cref="RigTunerViewModel"/> drives the rig tuner. Pitch comes from the
///     shared <see cref="TunerPitchEngine"/> (MPM→YIN); the machine judges each
///     reading against the CURRENT string only and advances on a cumulative
///     in-tune hold. No scoring, no audio writes — a read-only pre-flight check.
///     <para>
///     Lifecycle is owned by <see cref="PlayModeViewModel"/>: <see cref="Begin"/>
///     when the Tuning-check screen is shown (with the picked arrangement) and
///     <see cref="Stop"/> on every way off it. The poll timer is lazy and
///     provably parked when stopped — a hidden surface never keeps it ticking.
///     </para>
/// </summary>
public sealed partial class GuidedTuningCheckViewModel : ObservableObject
{
    private const int WindowSize = 4096;
    private const float RmsFloor = 0.005f;

    private readonly IAudioPlaybackService? _audio;
    private readonly ILocalizationService _localization;
    private readonly bool _driveWithTimer;
    private readonly Stopwatch _clock = new();
    private readonly float[] _window = new float[WindowSize];

    private DispatcherTimer? _timer;
    private TunerPitchEngine? _engine;
    private int _engineSampleRate;
    private GuidedTuningCheck? _check;

    /// <param name="audio">Input tap source (null ⇒ no live pitch, rows still build).</param>
    /// <param name="localization">The check's status lines and each row's state text.</param>
    /// <param name="driveWithTimer">Production polls the input from an internal
    /// ~15 Hz <see cref="DispatcherTimer"/>. Tests pass false so no Avalonia
    /// dispatcher is needed to exercise the row/state wiring.</param>
    public GuidedTuningCheckViewModel(IAudioPlaybackService? audio, ILocalizationService localization,
        bool driveWithTimer = true)
    {
        _audio = audio;
        _localization = localization;
        _driveWithTimer = driveWithTimer;
    }

    /// <summary>Per-string rows, lowest string first — rebuilt on every
    /// <see cref="Begin"/>.</summary>
    public ObservableCollection<TuningStringRow> Strings { get; } = [];

    [ObservableProperty] private bool _hasCheck;
    [ObservableProperty] private bool _isComplete;

    /// <summary>Zero-based index of the string the check is currently on.</summary>
    [ObservableProperty] private int _currentStringIndex;

    /// <summary>Localized status line under the strings (listening / tune the
    /// low E / a different string / all in tune / place the capo …).</summary>
    [ObservableProperty] private string _statusText = "";

    /// <summary>True when a reading looks like a DIFFERENT string than the one
    /// being checked — surfaced so the player knows to pluck the right one.</summary>
    [ObservableProperty] private bool _wrongStringSuspected;

    /// <summary>True while the poll timer is running (verification hook).</summary>
    public bool IsPolling => _timer?.IsEnabled == true;

    /// <summary>
    ///     The live state machine, read-only. A test drives it with real
    ///     frequencies and asserts the ROWS follow — which is the only way to
    ///     prove the screen's per-string states come from the real tuner rather
    ///     than from something the test set by hand.
    /// </summary>
    internal GuidedTuningCheck? Check => _check;

    /// <summary>The live check's own in-tune tolerance, in cents — what the
    /// meter's target band is drawn from. Falls back to the machine's own
    /// default before a check exists, so the meter never divides by zero.</summary>
    public double InTuneCents => _check?.InTuneCents ?? FireTunerScale.DefaultInTuneCents;

    /// <summary>How many strings the check has CONFIRMED. Counted from the rows,
    /// which are the same objects the screen draws — a separate tally is how a
    /// chip ends up disagreeing with the list under it.</summary>
    public int VerifiedCount => Strings.Count(r => r.IsConfirmed);

    /// <summary>How many strings this arrangement's check covers (4 on a bass).</summary>
    public int StringCount => Strings.Count;

    /// <summary>"5 OF 6 VERIFIED".</summary>
    public string VerifiedLabel => _localization.Format(
        "PlayMode_GetReady_Tuner_VerifiedCount", VerifiedCount, StringCount);

    /// <summary>Every string confirmed — the chip goes green.</summary>
    public bool AllVerified => StringCount > 0 && VerifiedCount == StringCount;

    /// <summary>Re-announces everything derived from the rows' states. Called
    /// from every path that can change one.</summary>
    private void RefreshVerifiedTally()
    {
        OnPropertyChanged(nameof(VerifiedCount));
        OnPropertyChanged(nameof(StringCount));
        OnPropertyChanged(nameof(VerifiedLabel));
        OnPropertyChanged(nameof(AllVerified));
    }

    /// <summary>(Re)builds the check for <paramref name="arrangement"/> and starts
    /// polling the input. A null arrangement (nothing picked yet) just clears.
    /// <paramref name="transposeSemitones"/> is the tuning-match shift: the song
    /// plays that far up, so the strings are checked against the player's own tuning.</summary>
    public void Begin(Arrangement? arrangement, int transposeSemitones = 0)
    {
        Stop();
        Strings.Clear();
        WrongStringSuspected = false;
        IsComplete = false;

        if (arrangement == null)
        {
            _check = null;
            HasCheck = false;
            StatusText = "";
            return;
        }

        _check = new GuidedTuningCheck(arrangement, transposeSemitones: transposeSemitones);
        _check.ProgressChanged += OnProgress;
        _check.StringConfirmed += OnStringConfirmed;
        _check.Completed += OnCompleted;

        foreach (var target in _check.Targets)
            Strings.Add(new TuningStringRow(target.StringIndex, target.NoteName, _localization, _check.InTuneCents));

        HasCheck = Strings.Count > 0;
        CurrentStringIndex = _check.CurrentStringIndex;
        OnPropertyChanged(nameof(InTuneCents));
        RefreshVerifiedTally();
        SyncCurrentFlags();
        StatusText = _audio == null
            ? _localization["Tuner_NoInput"]
            : _localization["Tuner_Listening"];

        if (!HasCheck || !_driveWithTimer) return;

        _timer ??= CreateTimer();
        _clock.Restart();
        _timer.Start();
        OnPropertyChanged(nameof(IsPolling));
    }

    /// <summary>Parks the poll timer and detaches the check's events. Idempotent
    /// — safe on every screen-change / exit path.</summary>
    public void Stop()
    {
        _timer?.Stop();
        _clock.Stop();
        if (_check != null)
        {
            _check.ProgressChanged -= OnProgress;
            _check.StringConfirmed -= OnStringConfirmed;
            _check.Completed -= OnCompleted;
        }

        OnPropertyChanged(nameof(IsPolling));
    }

    /// <summary>Restarts the whole check at the lowest string (button on the card).</summary>
    [RelayCommand]
    private void Restart()
    {
        if (_check == null) return;
        _check.Restart();
        IsComplete = false;
        WrongStringSuspected = false;
        foreach (var row in Strings)
        {
            row.State = _check.StateOf(row.Index);
            row.HoldProgress = 0;
            row.CentsText = "";
            // A restart un-hears every string: the rows go back to PLUCK rather
            // than keeping the last needle position, which would claim a reading
            // the restarted check has not taken.
            row.HasReading = false;
            row.Cents = 0;
        }

        CurrentStringIndex = _check.CurrentStringIndex;
        RefreshVerifiedTally();
        SyncCurrentFlags();
        StatusText = _localization["Tuner_Listening"];
    }

    /// <summary>Skips the current string (revisit later by tapping it).</summary>
    [RelayCommand]
    private void SkipCurrent()
    {
        if (_check == null || _check.IsComplete) return;
        _check.SkipCurrentString();
        SyncFromCheck();
    }

    /// <summary>Re-selects a string (also the way back to a skipped/confirmed one).</summary>
    [RelayCommand]
    private void SelectString(TuningStringRow? row)
    {
        if (_check == null || row == null) return;
        _check.SelectString(row.Index);
        IsComplete = false;
        SyncFromCheck();
        StatusText = _localization["Tuner_Listening"];
    }

    private DispatcherTimer CreateTimer()
    {
        // ~15 Hz — plenty for a per-string needle, each tick reads well inside the
        // ~0.7 s input-capture ring (same cadence as the rig tuner).
        var t = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(66)
        };
        t.Tick += (_, _) => Tick();
        return t;
    }

    private void Tick()
    {
        if (_audio == null || _check == null) return;
        var nowMs = _clock.Elapsed.TotalMilliseconds;

        if (!TryEnsureEngine())
        {
            _check.ProcessSilence();
            return;
        }

        if (_audio.ReadInputCaptureWindow(_window) < WindowSize)
        {
            _check.ProcessSilence();
            return;
        }

        float sumSquares = 0;
        foreach (var s in _window) sumSquares += s * s;
        var rms = MathF.Sqrt(sumSquares / WindowSize);
        if (rms < RmsFloor)
        {
            _check.ProcessSilence();
            return;
        }

        var reading = _engine!.Detect(_window);
        if (!reading.HasPitch || reading.FrequencyHz <= 0)
        {
            _check.ProcessSilence();
            return;
        }

        // The machine judges this reading against the current string only and
        // raises ProgressChanged (→ OnProgress) with the resulting state.
        _check.Process(reading.FrequencyHz, nowMs);
    }

    private bool TryEnsureEngine()
    {
        var sampleRate = _audio!.InputSampleRate;
        if (sampleRate <= 0)
        {
            StatusText = _localization["Tuner_NoInput"];
            return false;
        }

        if (_engine == null || _engineSampleRate != sampleRate)
        {
            _engineSampleRate = sampleRate;
            // 25 Hz floor covers drop-tuned basses (B0 ≈ 30.9 Hz) with margin.
            _engine = new TunerPitchEngine(sampleRate, WindowSize, 25.0, 1500.0);
        }

        return true;
    }

    private void OnProgress(TuningCheckProgress progress)
    {
        WrongStringSuspected = progress.WrongStringSuspected;
        if (progress.StringIndex >= 0 && progress.StringIndex < Strings.Count)
        {
            var row = Strings[progress.StringIndex];
            row.State = progress.State;
            row.Cents = progress.CentsOff;
            row.HasReading = true;
            row.HoldProgress = progress.HoldProgress;
            row.CentsText = string.Create(CultureInfo.CurrentCulture,
                $"{progress.CentsOff:+0;-0;±0} ¢");
            RefreshVerifiedTally();
        }

        // The current string can move on a confirm — keep the flags/index in step.
        if (_check != null && CurrentStringIndex != _check.CurrentStringIndex)
        {
            CurrentStringIndex = _check.CurrentStringIndex;
            SyncCurrentFlags();
        }

        UpdateStatus();
    }

    private void OnStringConfirmed(int stringIndex)
    {
        if (stringIndex >= 0 && stringIndex < Strings.Count)
        {
            Strings[stringIndex].State = TuningCheckStringState.Confirmed;
            Strings[stringIndex].HoldProgress = 1.0;
            RefreshVerifiedTally();
        }
    }

    private void OnCompleted()
    {
        IsComplete = true;
        WrongStringSuspected = false;
        SyncCurrentFlags();
        StatusText = _check is { CapoFret: > 0 }
            ? _localization.Format("PlayMode_GuidedTuning_PlaceCapo",
                _check.CapoFret.ToString())
            : _localization["PlayMode_GuidedTuning_AllInTune"];
    }

    /// <summary>Pushes the whole machine state back into the rows (after a
    /// skip / re-select, which can move the current string arbitrarily).</summary>
    private void SyncFromCheck()
    {
        if (_check == null) return;
        foreach (var row in Strings)
        {
            row.State = _check.StateOf(row.Index);
            if (row.State is TuningCheckStringState.Waiting or TuningCheckStringState.Detecting)
                row.HoldProgress = 0;
        }

        CurrentStringIndex = _check.CurrentStringIndex;
        IsComplete = _check.IsComplete;
        RefreshVerifiedTally();
        SyncCurrentFlags();
        UpdateStatus();
    }

    private void SyncCurrentFlags()
    {
        foreach (var row in Strings)
            row.IsCurrent = !IsComplete && row.Index == CurrentStringIndex;
    }

    private void UpdateStatus()
    {
        if (IsComplete) return;
        var loc = _localization;
        if (WrongStringSuspected)
        {
            StatusText = loc["PlayMode_GuidedTuning_WrongString"];
            return;
        }

        if (CurrentStringIndex >= 0 && CurrentStringIndex < Strings.Count)
        {
            StatusText = loc.Format("PlayMode_GuidedTuning_TuneString",
                Strings[CurrentStringIndex].NoteName);
        }
    }
}
