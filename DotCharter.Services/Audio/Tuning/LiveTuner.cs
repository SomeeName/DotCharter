using System.Diagnostics;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Tuning;
using DotCharter.Tone.Diagnostics;
using JuceSharp.Dsp;

namespace DotCharter.Services.Audio.Tuning;

/// <summary>One open string of the arrangement being tuned, low string first.</summary>
public sealed class LiveTunerString
{
    internal LiveTunerString(string noteName) => NoteName = noteName;

    /// <summary>Target note of the open string, e.g. "E2".</summary>
    public string NoteName { get; }

    /// <summary>The string being heard right now.</summary>
    public bool IsNearest { get; internal set; }

    /// <summary>Held inside the tolerance long enough to count as tuned.</summary>
    public bool IsTuned { get; internal set; }

    /// <summary>
    ///     This string's LAST reading in cents, clamped to ±50. A monophonic
    ///     detector hears one plucked string at a time, so each string remembers
    ///     what it last heard: you tune a string, move on, and the one you left
    ///     still shows where you left it.
    /// </summary>
    public double CentsOff { get; internal set; }

    /// <summary>False until this string has been plucked once since the last
    /// fresh start — a centred needle would read as "in tune".</summary>
    public bool HasReading { get; internal set; }
}

/// <summary>
///     The live arrangement tuner the Tuner dialog and the Tools popover's
///     readout both read. While anything holds a lease it polls the input
///     capture at ~15 Hz, detects the plucked string's pitch (YIN via
///     <see cref="JuceChromaticTuner" />), matches it to the nearest open string
///     of the ACTIVE arrangement's tuning and confirms a string once it holds
///     within ±5 cents for ~0.5 s cumulative (<see cref="ArrangementTunerLogic" />).
///     The capture already carries the drop pedal's shift, so the detected pitch
///     is compared with the targets directly; <see cref="PitchShiftSemitones" />
///     lets a surface explain why an E string reads as D.
///     <para>
///         Leases are counted: the popover and the dialog can both be up, and
///         the first to let go must not stop the other's reading. Ticks and
///         <see cref="Changed" /> run on the UI thread
///         (<see cref="IUiDispatcher.SchedulePeriodic" />).
///     </para>
/// </summary>
public sealed class LiveTuner : IDisposable
{
    /// <summary>Readings are clamped to this many cents either side.</summary>
    public const double MaxDisplayCents = 50.0;

    private const int WindowSize = 4096;
    private const float RmsFloor = 0.005f;

    // ~15 Hz: plenty for a needle, and each tick reads well under the ~0.7 s
    // input capture ring, so nothing is ever lost.
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(66);

    private readonly ILiveInputCapture _audio;
    private readonly ProjectState _projectState;
    private readonly IUiDispatcher _ui;
    private readonly IAppLogger _logger;
    private readonly Stopwatch _tickClock = new();
    private readonly float[] _window = new float[WindowSize];
    private readonly TunerPitchFilter _filter = new() { MinRms = RmsFloor };
    private readonly List<LiveTunerString> _strings = [];

    private ArrangementTunerLogic _logic = new();
    private JuceChromaticTuner? _detector;
    private IDisposable? _poll;
    private int _leases;

    public LiveTuner(ILiveInputCapture audio, ProjectState projectState, IUiDispatcher ui, IAppLogger logger)
    {
        _audio = audio;
        _projectState = projectState;
        _ui = ui;
        _logger = logger;
    }

    /// <summary>Raised on the UI thread after every change to the reading or the strings.</summary>
    public event Action? Changed;

    /// <summary>True when the active arrangement has strings to tune (not none, not vocal).</summary>
    public bool CanTune => CanTuneAgainst(_projectState.ActiveArrangement);

    /// <summary>True for an arrangement with strings: not none, not vocal.</summary>
    public static bool CanTuneAgainst(Arrangement? arrangement) =>
        arrangement != null && arrangement.ArrangementType != ArrangementType.Vocal;

    /// <summary>True while at least one lease is held.</summary>
    public bool IsRunning => _leases > 0;

    /// <summary>False while no input stream exists; the poll keeps probing for one.</summary>
    public bool HasInput { get; private set; }

    /// <summary>A pitch near one of the strings is being heard right now.</summary>
    public bool HasReading { get; private set; }

    /// <summary>The heard pitch's note name with its octave ("E2"); empty without a reading.</summary>
    public string HeardNote { get; private set; } = "";

    /// <summary>The heard string's deviation, clamped to ±<see cref="MaxDisplayCents" />.</summary>
    public double CentsOff { get; private set; }

    public bool IsInTune { get; private set; }

    public bool AllTuned { get; private set; }

    /// <summary>The drop pedal's shift, rounded to whole semitones; 0 when it is off.</summary>
    public int PitchShiftSemitones { get; private set; }

    public IReadOnlyList<LiveTunerString> Strings => _strings;

    public int TunedCount => _strings.Count(s => s.IsTuned);

    /// <summary>
    ///     Starts (or joins) detection. <paramref name="fresh" /> reloads the
    ///     arrangement's strings and forgets every string's last reading even when
    ///     another lease is already running — the dialog opens clean; the popover
    ///     joins whatever is there.
    /// </summary>
    /// <returns>The lease to dispose, or null when there is nothing to tune against.</returns>
    public IDisposable? Acquire(bool fresh)
    {
        if (!CanTune) return null;

        if (_leases == 0 || fresh) Load();
        if (_leases++ == 0)
        {
            _tickClock.Restart();
            _poll = _ui.SchedulePeriodic(TickPeriod, Tick);
        }

        return new Lease(this);
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
        _leases = 0;
    }

    private void Release()
    {
        if (_leases == 0 || --_leases > 0) return;
        _poll?.Dispose();
        _poll = null;
        _tickClock.Stop();
        _detector = null;
        ClearReading();
        Changed?.Invoke();
    }

    private void Load()
    {
        _logic = new ArrangementTunerLogic();
        _logic.Load(_projectState.ActiveArrangement!);
        _strings.Clear();
        foreach (var target in _logic.Targets) _strings.Add(new LiveTunerString(target.NoteName));

        _detector = null;
        _filter.Reset();
        AllTuned = false;
        ClearReading();
        TryCreateDetector();
        UpdatePitchShift();
        Changed?.Invoke();
    }

    private void Tick()
    {
        var deltaMs = _tickClock.Elapsed.TotalMilliseconds;
        _tickClock.Restart();
        UpdatePitchShift();

        // Input may get configured while a surface sits open — keep probing.
        if (!TryCreateDetector())
        {
            Changed?.Invoke();
            return;
        }

        // A NON-DESTRUCTIVE latest-window snapshot: a streaming drain would
        // share one read cursor with live note detection, and whichever polled
        // faster would starve the other.
        if (_audio.ReadInputCaptureWindow(_window) < WindowSize)
        {
            Silence();
            return;
        }

        float sumSquares = 0;
        foreach (var sample in _window) sumSquares += sample * sample;
        var rms = MathF.Sqrt(sumSquares / WindowSize);

        // Raw YIN reading (skipped below the level gate), then stabilized: the
        // filter drops silence, ignores each pluck's ~50 ms attack (it reads
        // sharp) and median-smooths so one harmonic glitch never twitches the
        // needle. Silence never reaches the logic — its hold time is cumulative
        // across plucks by design.
        var raw = rms < RmsFloor ? null : _detector!.Detect(_window);
        if (_filter.Process(raw?.FrequencyHz, rms, deltaMs) is { } frequencyHz)
            Hear(frequencyHz, deltaMs);
        else
            Silence();
    }

    /// <summary>One stabilized pitch reading, <paramref name="deltaMs" /> after the last tick.</summary>
    internal void Hear(double frequencyHz, double deltaMs)
    {
        var frame = _logic.Process(frequencyHz, deltaMs);
        if (frame.NearestString < 0)
        {
            Silence();
            return;
        }

        var clamped = Math.Clamp(frame.CentsOff, -MaxDisplayCents, MaxDisplayCents);
        HasReading = true;
        HeardNote = ArrangementTunerLogic.NoteNameOf((int)Math.Round(ArrangementTunerLogic.MidiOf(frequencyHz)));
        CentsOff = clamped;
        IsInTune = frame.InTune;

        for (var i = 0; i < _strings.Count; i++)
        {
            var s = _strings[i];
            s.IsNearest = i == frame.NearestString;
            s.IsTuned = _logic.IsStringTuned(i);
            if (!s.IsNearest) continue;
            s.CentsOff = clamped;
            s.HasReading = true;
        }

        if (frame.StringJustConfirmed)
        {
            _logger.Info(AppLogCategory.Audio,
                $"Tuner: string {frame.NearestString} confirmed at {_logic.Targets[frame.NearestString].NoteName}.");
        }

        if (_logic.AllTuned && !AllTuned)
            _logger.Info(AppLogCategory.Audio, "Tuner: all strings in tune.");
        AllTuned = _logic.AllTuned;
        Changed?.Invoke();
    }

    private void Silence()
    {
        ClearReading();
        Changed?.Invoke();
    }

    /// <summary>Clears which string is being HEARD, never what each string last read.</summary>
    private void ClearReading()
    {
        HasReading = false;
        IsInTune = false;
        HeardNote = "";
        CentsOff = 0;
        foreach (var s in _strings) s.IsNearest = false;
    }

    private bool TryCreateDetector()
    {
        if (_detector != null) return true;

        var sampleRate = _audio.InputSampleRate;
        HasInput = sampleRate > 0;
        if (!HasInput) return false;

        // A 25 Hz floor covers drop-tuned basses (B0 ≈ 30.9 Hz) with margin.
        _detector = new JuceChromaticTuner(sampleRate, WindowSize, 25f, 1500f);
        return true;
    }

    private void UpdatePitchShift() => PitchShiftSemitones = (int)Math.Round(_audio.InputPitchShiftSemitones);

    private sealed class Lease(LiveTuner owner) : IDisposable
    {
        private LiveTuner? _owner = owner;

        public void Dispose()
        {
            _owner?.Release();
            _owner = null;
        }
    }
}
