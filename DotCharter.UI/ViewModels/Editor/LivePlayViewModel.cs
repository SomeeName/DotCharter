// Root partial of LivePlayViewModel: the detection-monitor payload record, every field, the whole bound surface, the constructor and the config-backed setting handlers.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Services.Gameplay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>One line of the detection monitor. Immutable; colored by kind.
/// <paramref name="IsUnsure"/>: a miss the detector found evidence for and still called a
/// miss (<see cref="LiveDetectionCertainty.IsUnsureMiss"/>) — it wears the "low confidence" hint.</summary>
public sealed record LiveDetectionEntry(string Text, bool IsHit, bool IsMiss, bool IsUnsure = false);

/// <summary>
///     Live Play mode: listens to the raw (dry, pre-VST) guitar input while
///     the chart plays, detects what the player actually sounds and scores it
///     against the expected chart in real time — Rocksmith-style. v0.10 turns
///     this into a practice suite: A/B section looping with count-in, a speed
///     ladder that ramps a slowed song back to 100%, dynamic difficulty,
///     per-song progress persistence and latency auto-calibration.
///     <para>
///     All practice rules live in <see cref="LivePlaySessionService"/> (pure,
///     unit-tested); this view model is a thin binder that pumps the session
///     from a DEDICATED WORKER THREAD (<see cref="LivePlayDetectionPump"/>),
///     applies its transport side effects (seek/speed/metronome) on that
///     thread, and marshals every UI-facing output to the UI thread through
///     one coalesced snapshot per tick (<see cref="CoalescedUiChannel{T}"/>).
///     The detection monitor is independent of scoring and also works in
///     free play.
///     </para>
///     <para>
///     THREADING CONTRACT. The
///     session/matcher and all detection state are WORKER-CONFINED: the worker
///     tick runs under <c>_sessionGate</c>, and every UI-side operation that
///     touches session state (Start/Stop via <see cref="IsActive"/>, the loop
///     commands, <see cref="BeginResumeRewind"/>, rock-meter arm/disarm,
///     <see cref="ArmFirstNoteCountIn"/>, calibration arm/cancel) takes the
///     same gate — they stay synchronous on the UI thread, serialized against
///     the tick. Worker → UI state flows ONLY through the coalesced snapshot
///     (payload-replace, list payloads merged forward) plus two existing
///     lock-free channels the render thread drains directly
///     (<see cref="FeedbackEvents"/>, <see cref="ResolvedThroughMs"/>). Never
///     set an observable property from the worker.
///     </para>
/// </summary>
public partial class LivePlayViewModel : ObservableObject
{
    private const int DetectionLogCap = 80;
    private const int FeedbackQueueCap = 256;
    private const int MissListCap = 8;
    private static readonly string[] NoteNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private readonly IAudioPlaybackService _audio;
    private readonly ProjectState _projectState;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly IConfigurationService _configService;
    private readonly IPracticeProfileService _profiles;

    /// <summary>The passive tuning-drift watchdog over the live input. Worker-confined
    /// like the session: checked from the tick, reset by Start/Stop, all under
    /// <c>_sessionGate</c>.</summary>
    private readonly LiveTuningDriftMonitor _tuningDrift;

    /// <summary>The dedicated detection worker (~33 ms cadence). Runs while an
    /// analyzer exists (session, detection monitor or calibration); stopped —
    /// with a join, so no tick survives it — by <see cref="TearDownAnalysis"/>.</summary>
    private readonly LivePlayDetectionPump _pump;

    /// <summary>Serializes the worker tick against the UI-side session
    /// operations. The worker holds it for the whole tick; UI-side holders are
    /// short. NEVER call <see cref="LivePlayDetectionPump.Stop"/> (a join)
    /// while holding it — teardown paths stop the pump outside the gate.</summary>
    private readonly object _sessionGate = new();

    /// <summary>Worker → UI marshaling: one coalesced snapshot per tick max.</summary>
    private readonly CoalescedUiChannel<TickUiState> _uiChannel;

    /// <summary>Session epoch, bumped by Start/Stop (under the gate). A posted
    /// snapshot stamped with an older epoch is stale — a run boundary landed
    /// after it was composed — and is dropped on apply (calibration outcomes
    /// excepted; they survive run boundaries by design).</summary>
    private int _uiEpoch;
    // Wide enough to drain a full native capture ring in a single read even
    // after the reader was stalled, so a burst never leaves a backlog that
    // would force the ring to skip (and the analyzer to lose its onset
    // history) on the following tick.
    private readonly float[] _captureBuffer = new float[32768];

    // Reused across the 30 Hz session ticks (HandleTick Reset()s it) — a
    // fresh result + its Events list per tick was measurable GC churn.
    private readonly LivePlayTickResult _tickResult = new();
    private long _lastSeekGeneration;

    private GuitarInputAnalyzer? _analyzer;
    private CaptureStreamMonitor? _captureMonitor;

    // Per-string detector certainty for the analysis monitor. WORKER-CONFINED like the
    // session: recorded in OnJudged, sampled into the tick snapshot, cleared at run start.
    private readonly LiveStringConfidenceTracker _stringConfidence = new();

    // The readings last put in a snapshot, and that snapshot's epoch: an unchanged strip is not
    // re-posted 30 times a second. Null while the monitor is hidden, so showing it republishes.
    private LiveStringConfidenceReading[]? _lastStringConfidence;
    private int _lastStringConfidenceEpoch;
    // Worker-only: when the previous starvation check ran, so the warning can say
    // whether the READER stalled (gap ~ the drop) or the capture stream JUMPED
    // (gap ~ one tick). Zeroed wherever the monitor is (re)created.
    private long _lastStarvationCheckTimestamp;
    private readonly LivePlaySessionService _session = new();

    /// <summary>The pre-first-note count-in clicks of this run and which of them
    /// the engine already has. Armed by <see cref="ArmFirstNoteCountIn" />, walked by
    /// the worker tick, disarmed by <c>StopCore</c> — all under <c>_sessionGate</c>.</summary>
    private readonly FirstNoteCountInSchedule _firstNoteCountIn = new();

    private LatencyCalibrationSession? _calibration;
    private double _latencyOffsetMs;

    /// <summary>Computed 1–10 rating of the chart this session plays
    /// (<see cref="ChartDifficultyEstimator"/>), captured at Start and
    /// stamped onto the practice profile when the run records.</summary>
    private double? _chartDifficulty;

    // Detection v8: the rolling CQT verifier, when the mode is enabled. Fed the same
    // capture PCM as the analyzer, on its RAW capture-stream axis; the chart time is
    // mapped into that axis at Verify time via an anchor re-taken every playing tick.
    // (Pushing "on the transport axis" via a speed-scaled position would
    // violate JuceConstantQ.Push's write-cursor contract and reset the transform on
    // every push, so detection would confirm NOTHING at any speed != 1.0.)
    private LiveCqtVerifier? _cqtVerifier;

    /// <summary>This run's take tee (see <see cref="LivePlayRunArchive.TryStartTake" />):
    /// written by the worker, detached by <c>StopCore</c>, both under the gate.</summary>
    private LiveTakeRecorder? _takeRecorder;
    private double? _pendingLoopStartMs;

    /// <summary>Post-run timing analysis (rushing/dragging). Fed from the single
    /// judgment funnel; a separate consumer by design — it never touches scoring.</summary>
    private readonly LiveTimingCollector _timing = new();

    /// <summary>Judgments one attack made BEYOND the one <c>Offer</c> returns —
    /// a double stop the chart spells as two notes is one onset resolving two
    /// expected sounds. Worker-confined and reused: <c>Offer</c> clears it.</summary>
    private readonly List<LivePlayEvent> _simultaneousJudged = [];
    private readonly List<LivePlayGrade> _graded = [];
    private double _speedBeforeSession = 1.0;
    private string _profileSongKey = string.Empty;
    private string _profileArrangement = string.Empty;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private long _score;
    [ObservableProperty] private int _streak;
    [ObservableProperty] private int _bestStreak;
    [ObservableProperty] private int _multiplier = 1;
    /// <summary>Opens at 0, not 100: nothing has been judged yet, and
    /// <c>AccuracyMath</c> is the one place that decides what 0/0 means for
    /// every readout in the app. A HUD that opens at 100% tells a player who has
    /// not touched a string that they are perfect.</summary>
    [ObservableProperty] private double _accuracyPercent;
    [ObservableProperty] private int _hits;
    [ObservableProperty] private int _misses;
    [ObservableProperty] private int _totalSounds;
    // Timing-tier breakdown (0.16 timing-graded judgment). May not sum to
    // Hits: CQT pull-verified hits carry no timing evidence, hence no tier.
    [ObservableProperty] private int _perfectCount;
    [ObservableProperty] private int _greatCount;
    [ObservableProperty] private int _goodCount;
    [ObservableProperty] private string _lastFeedback = string.Empty;
    [ObservableProperty] private bool _lastWasHit;
    /// <summary>EARLY/LATE for the last judgment, or empty when the player was
    /// inside the on-time band — and, crucially, also empty for judgments that
    /// carry no honest attack time (misses, CQT pull-verified hits), which would
    /// otherwise all read as hugely "late". See <see cref="LiveTiming.Classify"/>.</summary>
    [ObservableProperty] private string _lastTimingLabel = string.Empty;
    [ObservableProperty] private bool _lastTimingIsEarly;
    [ObservableProperty] private bool _lastTimingIsLate;
    [ObservableProperty] private string _statusHint = string.Empty;
    /// <summary>Bumped whenever per-section hit/miss tallies change so the
    /// section graph knows to repaint without observing each section.</summary>
    [ObservableProperty] private int _sectionsVersion;
    [ObservableProperty] private bool _showDetectionMonitor;

    // ---- practice suite ----
    [ObservableProperty] private bool _loopActive;
    [ObservableProperty] private string _loopLabel = string.Empty;
    [ObservableProperty] private bool _loopStartArmed;
    [ObservableProperty] private int _countInRemaining;
    [ObservableProperty] private string _passSummary = string.Empty;
    [ObservableProperty] private bool _isSpeedLadderEnabled;
    [ObservableProperty] private bool _isDynamicDifficultyEnabled;
    [ObservableProperty] private int _currentSpeedPercent = 100;
    [ObservableProperty] private int _hitWindowIndex = 1; // 0 lenient, 1 normal, 2 strict
    [ObservableProperty] private int _chordStrictnessIndex = 1; // 0 lenient, 1 standard, 2 strict
    /// <summary>Detection v8: judge with the CQT chart verifier instead of the
    /// classic pitch-candidate matcher. Off by default (opt-in); takes effect on
    /// the next session start.</summary>
    [ObservableProperty] private bool _useCqtVerifier;
    /// <summary>Judge bends from the pitch contour. On by default; needs the CQT
    /// verifier; takes effect on the next session start.</summary>
    [ObservableProperty] private bool _judgeBends;

    /// <summary>Judge slides from the pitch contour. Off by default (not measured on real
    /// DI); needs the CQT verifier; takes effect on the next session start.</summary>
    [ObservableProperty] private bool _judgeSlides;

    /// <summary>Judge vibrato from the pitch contour. Off by default (misses subtle
    /// vibrato); needs the CQT verifier; takes effect on the next session start.</summary>
    [ObservableProperty] private bool _judgeVibrato;
    [ObservableProperty] private long _bestScore;
    [ObservableProperty] private double _masteryPercent;
    [ObservableProperty] private double _bestAccuracyPercent;
    [ObservableProperty] private string _calibrationStatus = string.Empty;
    [ObservableProperty] private bool _isCalibrating;
    /// <summary>Bumped when the practice profile (history) changes so the
    /// progress sparkline repaints.</summary>
    [ObservableProperty] private int _profileVersion;

    /// <summary>Stored session history of the current song + arrangement,
    /// oldest first — the data behind the progress graph.</summary>
    public IReadOnlyList<PracticeSessionRecord> ProfileSessions { get; private set; } =
        Array.Empty<PracticeSessionRecord>();

    public IReadOnlyList<LivePlaySection> Sections => _session.Sections;

    /// <summary>The performance-stake meters (rock meter, crowd meter, the run's
    /// fail mark). Worker-confined like the session: every call into it is made
    /// under <c>_sessionGate</c> — see <c>LivePlayViewModel.Meters.cs</c> for the
    /// shell's gate-taking door.</summary>
    public LivePlayMetersViewModel Meters { get; }

    /// <summary>What a finished run leaves on disk besides its record — the take
    /// and the replay — and the <see cref="LivePlayRunArchive.TakeSaved" /> event
    /// listen-back waits for.</summary>
    public LivePlayRunArchive RunArchive { get; }

    /// <summary>Phrase spans for the structure strip's purple density bars.</summary>
    public IReadOnlyList<LivePlayPhrase> Phrases => _session.Phrases;

    /// <summary>Active A/B loop (null when not looping) — for the section strip overlay.</summary>
    public LivePlayLoop? CurrentLoop => _session.Loop;

    /// <summary>Mistake replay: the sounds missed in the last loop pass (or
    /// the most recent misses in free play), as "[mm:ss] label" lines.</summary>
    public ObservableCollection<string> MissedRecently { get; } = [];

    /// <summary>Detection monitor lines, newest first (UI-thread only).</summary>
    public ObservableCollection<LiveDetectionEntry> DetectionLog { get; } = [];

    /// <summary>The detection monitor's per-string confidence strip (UI-thread only).</summary>
    public LiveStringConfidenceViewModel StringConfidence { get; }

    /// <summary>Judged sounds pending pickup by the 3D preview, which drains
    /// them into its frame snapshot on the render thread. Bounded (see
    /// <see cref="FeedbackQueueCap"/>) so a hidden 3D view can't accumulate a
    /// whole session's worth of verdicts.</summary>
    public ConcurrentQueue<LivePlayFeedbackEvent> FeedbackEvents { get; } = new();

    /// <summary>Raised when the practice suite wants a new playback speed —
    /// the shell routes it through the transport so the speed slider stays
    /// in sync with the engine.</summary>
    public event Action<double>? PlaybackSpeedChangeRequested;

    // Chart time through which every sound is already judged (the matcher's
    // cursor horizon). Read by the 3D render thread (Preview3DRenderDriver) to
    // draw a resume-rewind replay's dead notes dimmed, so it is published as a
    // raw bit pattern: a plain double field is not spec-guaranteed atomic.
    private long _resolvedThroughBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);

    /// <summary>Chart time through which every sound is resolved and can never be
    /// re-judged this run. Notes AHEAD of the playhead but at/behind this horizon
    /// are the replayed "dead" notes of a resume-rewind. Thread-safe (render
    /// thread reads it); <see cref="double.NegativeInfinity"/> when nothing is judged.</summary>
    public double ResolvedThroughMs => BitConverter.Int64BitsToDouble(Volatile.Read(ref _resolvedThroughBits));

    private void PublishResolvedThrough(double value) =>
        Volatile.Write(ref _resolvedThroughBits, BitConverter.DoubleToInt64Bits(value));

    public LivePlayViewModel(IAudioPlaybackService audio, ProjectState projectState, IAppLogger logger,
        IConfigurationService configService, IPracticeProfileService profiles,
        LiveTuningDriftMonitor tuningDrift,
        LivePlayMetersViewModel meters,
        LivePlayRunArchive runArchive,
        ILocalizationService localization)
    {
        _audio = audio;
        _localization = localization;
        _projectState = projectState;
        _logger = logger;
        _configService = configService;
        _profiles = profiles;
        _tuningDrift = tuningDrift;
        Meters = meters;
        StringConfidence = new LiveStringConfidenceViewModel(localization);
        RunArchive = runArchive;

        var config = _configService.LoadAppConfig();
        _latencyOffsetMs = config.LivePlayLatencyOffsetMs;
        _isSpeedLadderEnabled = config.LivePlaySpeedLadderEnabled;
        _isDynamicDifficultyEnabled = config.LivePlayDynamicDifficulty;
        _hitWindowIndex = (int)LiveHitWindows.Parse(config.LivePlayHitWindow);
        _chordStrictnessIndex = (int)ParseChordStrictness(config.LivePlayChordStrictness);
        _useCqtVerifier = config.LivePlayUseCqtVerifier;
        _judgeBends = config.LivePlayJudgeBends;
        _judgeSlides = config.LivePlayJudgeSlides;
        _judgeVibrato = config.LivePlayJudgeVibrato;
        _projectState.PropertyChanged += OnProjectStateChanged;

        // 30 Hz keeps well ahead of the ~0.7 s capture ring while the DSP
        // cost per tick (a few 2048-sample windows) stays negligible.
        // A DEDICATED WORKER THREAD, not a dispatcher timer: any dispatcher timer shares the UI
        // thread, so sustained render/compositor load stretches its tick gaps
        // past the session's seek-jump threshold and judging rewinds itself
        // inert. The worker cannot be starved by rendering; the session's
        // stall-vs-seek evidence (SeekGeneration) still covers whatever
        // residual gap a saturated machine produces (see
        // LivePlaySessionService.HandleTick's externalSeek doc).
        _pump = new LivePlayDetectionPump(WorkerTick);
        _uiChannel = new CoalescedUiChannel<TickUiState>(
            static action => Dispatcher.UIThread.Post(action),
            ApplyTickUiState,
            MergeTickUiState);
    }

    partial void OnIsActiveChanged(bool value)
    {
        if (value) Start();
        else Stop();
    }

    partial void OnShowDetectionMonitorChanged(bool value)
    {
        if (value)
        {
            // Free-play monitoring needs only the analyzer, not a chart.
            if (!EnsureAnalyzer()) ShowDetectionMonitor = false;
            return;
        }

        DetectionLog.Clear();
        if (!IsActive && !IsCalibrating) TearDownAnalysis();
    }

    /// <summary>True while the loop pre-roll count-in is running (HUD badge).</summary>
    public bool IsCountingIn => CountInRemaining > 0;

    partial void OnCountInRemainingChanged(int value) => OnPropertyChanged(nameof(IsCountingIn));

    partial void OnIsSpeedLadderEnabledChanged(bool value) => SaveConfig(c => c.LivePlaySpeedLadderEnabled = value);

    partial void OnIsDynamicDifficultyEnabledChanged(bool value)
    {
        SaveConfig(c => c.LivePlayDynamicDifficulty = value);
        OnPropertyChanged(nameof(IsDynamicDifficultyOn));
        OnPropertyChanged(nameof(IsDynamicDifficultyGenerated));
    }

    /// <summary>
    ///     False only when the active arrangement has nothing to level (no notes or
    ///     chords). The toggle is then disabled and says why; the stored preference
    ///     is kept for the next chart. A one-level chart CAN use it: each run judges a
    /// ladder generated from it (<see cref="DynamicDifficultyLadder" />).
    /// </summary>
    public bool CanUseDynamicDifficulty => DynamicDifficultyLadder.CanLadder(_projectState.ActiveArrangement);

    /// <summary>Dynamic difficulty is on and will judge a generated ladder: the HUD says so.</summary>
    public bool IsDynamicDifficultyGenerated =>
        IsDynamicDifficultyOn && DynamicDifficultyLadder.IsGenerated(_projectState.ActiveArrangement);

    /// <summary>What the toggle shows and what a session starts with: the
    /// preference, but never "on" for a chart it cannot level.</summary>
    public bool IsDynamicDifficultyOn
    {
        get => IsDynamicDifficultyEnabled && CanUseDynamicDifficulty;
        set
        {
            if (CanUseDynamicDifficulty) IsDynamicDifficultyEnabled = value;
            else OnPropertyChanged();
        }
    }

    private void OnProjectStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Levels change with the arrangement, and generating or squashing levels
        // re-points the active level, so these two cover every way the count moves.
        if (e.PropertyName is not (nameof(ProjectState.ActiveArrangement) or nameof(ProjectState.ActiveLevel))) return;
        OnPropertyChanged(nameof(CanUseDynamicDifficulty));
        OnPropertyChanged(nameof(IsDynamicDifficultyOn));
        OnPropertyChanged(nameof(IsDynamicDifficultyGenerated));
    }

    partial void OnHitWindowIndexChanged(int value)
        => SaveConfig(c => c.LivePlayHitWindow = ((LiveHitWindowPreset)Math.Clamp(value, 0, 2)).ToString());

    partial void OnChordStrictnessIndexChanged(int value)
        => SaveConfig(c => c.LivePlayChordStrictness =
            ((LiveChordVoicingStrictness)Math.Clamp(value, 0, 2)).ToString());

    partial void OnUseCqtVerifierChanged(bool value) => SaveConfig(c => c.LivePlayUseCqtVerifier = value);

    partial void OnJudgeBendsChanged(bool value) => SaveConfig(c => c.LivePlayJudgeBends = value);

    partial void OnJudgeSlidesChanged(bool value) => SaveConfig(c => c.LivePlayJudgeSlides = value);

    partial void OnJudgeVibratoChanged(bool value) => SaveConfig(c => c.LivePlayJudgeVibrato = value);

    private void SaveConfig(Action<AppConfig> mutate)
    {
        var config = _configService.LoadAppConfig();
        mutate(config);
        _configService.SaveAppConfig(config);
    }

    /// <summary>Identifies the current audio setup, so a learned offset follows the
    /// device it was learned on rather than the app. The drop tuner counts as part of
    /// the setup — it sits in front of the capture ring, so engaging it changes the
    /// true input latency, and a run with it on must not teach ordinary runs.</summary>
    private string CurrentDeviceKey(AppConfig config) => AudioDeviceKey.From(
        config.AudioDriver,
        config.AudioDriver == "ASIO" ? config.AsioInputDeviceName : config.WasapiInputDeviceName,
        config.AudioDriver == "ASIO" ? config.AsioOutputDeviceName : config.WasapiOutputDeviceName,
        _audio.ActiveSampleRate,
        _audio.ActualBufferFrames,
        (int)Math.Round(_audio.InputPitchShiftSemitones),
        _audio.InputPitchShiftMode);

    /// <summary>The offset to judge with: this device's auto-refined value when one
    /// exists, otherwise the loopback-measured baseline.</summary>
    private double ResolveLatencyOffset(AppConfig config) =>
        config.LivePlayAutoCalibrateTiming &&
        config.LivePlayLatencyOffsetsByDevice.TryGetValue(CurrentDeviceKey(config), out var learned)
            ? learned
            : config.LivePlayLatencyOffsetMs;
}
