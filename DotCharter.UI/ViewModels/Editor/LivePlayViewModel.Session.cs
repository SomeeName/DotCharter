// LivePlayViewModel: starting and stopping a judged run - the analyzer lifetime, StartCore/StopCore, and the completed-session event.

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     Starting and stopping a judged run: the guitar analyzer's lifetime, the
///     run-end auto-calibration, <c>StartCore</c> / <c>StopCore</c> (the one
///     place a session, its pump, its recorder and its speed are armed and torn
///     down) and the completed-session event.
///     <para>
///         Part of <see cref="LivePlayViewModel" />. The type's fields, its
///         whole bound surface and its constructor stay in the ROOT file, so
///         "where does this come from" is one look rather than a search.
///     </para>
/// </summary>
public partial class LivePlayViewModel
{
    /// <summary>
    ///     Folds this run's residual timing bias into the judging offset for the current
    ///     device. Opt-in, and heavily guarded — see <see cref="LatencyBiasCalibrator" />
    ///     for why a player who simply drags must not be able to move the goalposts.
    /// </summary>
    private void AutoCalibrateFromRun(LiveTimingReport? timing)
    {
        var config = _configService.LoadAppConfig();
        if (!config.LivePlayAutoCalibrateTiming) return;

        var decision = LatencyBiasCalibrator.Evaluate(
            _latencyOffsetMs, config.LivePlayLatencyOffsetMs, timing);

        if (!decision.Adjusted)
        {
            _logger.Debug(AppLogCategory.Audio, $"Timing auto-calibration skipped: {decision.Reason}.");
            return;
        }

        var key = CurrentDeviceKey(config);
        _latencyOffsetMs = decision.NewOffsetMs;
        SaveConfig(c => c.LivePlayLatencyOffsetsByDevice[key] = decision.NewOffsetMs);

        _logger.Info(AppLogCategory.Audio,
            $"Timing auto-calibration: {decision.Reason} (device '{key}').");
    }

    /// <summary>Creates the analyzer and starts the detection worker if input
    /// is available; reports via <see cref="StatusHint"/> when it isn't.
    /// UI-thread only (sets <see cref="StatusHint"/> directly); takes the gate,
    /// so the worker observes the analyzer atomically with its creation.</summary>
    private bool EnsureAnalyzer()
    {
        lock (_sessionGate)
        {
            if (_analyzer != null) return true;

            var sampleRate = _audio.InputSampleRate;
            if (sampleRate <= 0)
            {
                StatusHint = _localization["LivePlay_NoInput"];
                return false;
            }

            _analyzer = new GuitarInputAnalyzer(sampleRate);
            _captureMonitor = new CaptureStreamMonitor(sampleRate);
            _lastStarvationCheckTimestamp = 0;
            // Drain whatever sat in the ring before activation so stale audio
            // can't score the first notes. Iteration-capped defensively: an
            // unbounded drain of a misbehaving source pins the caller.
            // No race with the worker: the pump only runs while an analyzer
            // exists, and we hold the gate.
            for (var i = 0; i < 8 && _audio.ReadInputCapture(_captureBuffer) > 0; i++) { }
            _pump.Start();
            return true;
        }
    }

    /// <summary>Stops the detection worker (JOINS it — never call with the
    /// gate held) and drops the analysis state. UI-thread only.</summary>
    private void TearDownAnalysis()
    {
        _pump.Stop(); // after this returns, no tick is running or will run
        _analyzer = null;
        _captureMonitor = null;
        if (IsInputStarved) IsInputStarved = false;
        // …and its sibling banner. IsInputSilent must be cleared here too, or
        // "another app holds your input" stays on screen with no input being
        // read at all. Safe to touch the worker's shadow: the pump is JOINED
        // above, so no tick is running or will run.
        if (IsInputSilent) IsInputSilent = false;
        _workerInputSilent = false;
        _silentSamples = 0;
        _sawAnySignal = false;
    }

    private void Start()
    {
        bool started;
        lock (_sessionGate)
        {
            started = StartCore();
        }

        // Outside the gate on purpose: flipping IsActive reenters Stop(),
        // whose teardown path JOINS the worker — never while holding the gate.
        if (!started) IsActive = false;
    }

    /// <summary>
    /// Tuning-match transpose: the semitones the SONG is shifted to the player's
    ///     tuning for this run, so judging expects the chart's frets that much higher. Play
    ///     Mode sets it before a run and puts it back to 0 when the run unwinds; the editor's
    ///     own Live Play never sets it. Read once, at session start.
    /// </summary>
    public int TransposeSemitones
    {
        get => _transposeSemitones;
        set => SetProperty(ref _transposeSemitones, value);
    }

    private int _transposeSemitones;

    /// <summary>Session start under the gate. Returns false when Live Play
    /// refused to start (no chart / no input) — the caller then clears
    /// <see cref="IsActive"/> outside the gate.</summary>
    private bool StartCore()
    {
        Volatile.Write(ref _uiEpoch, _uiEpoch + 1); // stale worker snapshots die here

        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        if (arrangement == null || level == null || _projectState.Beats.Count == 0)
        {
            StatusHint = _localization["LivePlay_NoChart"];
            return false;
        }

        if (!EnsureAnalyzer()) return false;

        var config = _configService.LoadAppConfig();
        // Per-device when auto-calibration has learned one for this exact setup,
        // otherwise the loopback-measured baseline.
        _latencyOffsetMs = ResolveLatencyOffset(config);

        // Detection v8: build the CQT verifier when the mode is on; its Verify
        // delegate becomes the matcher's evidence source (pitched sounds).
        if (config.LivePlayUseCqtVerifier)
        {
            // Lenient is also the forgiving detection mode.
            _cqtVerifier = LiveCqtVerifier.ForPreset(_analyzer!.SampleRate,
                (LiveHitWindowPreset)Math.Clamp(HitWindowIndex, 0, 2));
        }
        else
        {
            _cqtVerifier = null;
        }

        // Profile first: its persisted per-phrase mastery seeds the dynamic
        // difficulty ladder so sessions resume where the player left off.
        var storedProfile = LoadProfile(arrangement);
        // A song with no ladder history of its own starts where the player usually plays.
        var startingDepth = IsDynamicDifficultyOn && storedProfile.PhraseMastery.Count == 0
            ? PlayerLadderDepth.From(_profiles.GetAll().Values)
            : null;

        // 0.20 difficulty wiring: rate the loaded chart once per session
        // start (pure O(sounds) pass over the hardest level) so the practice
        // store can stamp it when the run records — song select sorts by it.
        _chartDifficulty = ChartDifficultyEstimator.Estimate(arrangement, _projectState.Beats);

        // The per-technique switches; none on = no contour judge at all.
        var judgedTechniques = TechniqueContourJudge.Switched(
            config.LivePlayJudgeBends, config.LivePlayJudgeSlides, config.LivePlayJudgeVibrato);
        var options = new LivePlaySessionOptions
        {
            HitWindow = (LiveHitWindowPreset)Math.Clamp(HitWindowIndex, 0, 2),
            ChordStrictness = ParseChordStrictness(config.LivePlayChordStrictness),
            StringAwareVerification = config.LivePlayStringAware,
            TransposeSemitones = TransposeSemitones,
            ChartVerify = _cqtVerifier is { } cv ? cv.Verify : null,
            ChartVerifyEarly = _cqtVerifier is { } cve ? cve.VerifyEarly : null,
            JudgeContour = judgedTechniques != ContourTechniques.None && _cqtVerifier is { } cvt
                ? cvt.JudgeTechnique
                : null,
            JudgedTechniques = judgedTechniques,
            // The toggle's own answer: never on for a chart with nothing to level.
            DynamicDifficulty = IsDynamicDifficultyOn,
            InitialPhraseLevels = IsDynamicDifficultyOn && storedProfile.PhraseMastery.Count > 0
                ? storedProfile.PhraseMastery.ToDictionary(kv => kv.Key, kv => kv.Value.Level)
                : null,
            StartingDepth = startingDepth,
            CountInBeats = Math.Clamp(config.LivePlayCountInBeats, 0, 16),
            Ladder = IsSpeedLadderEnabled
                ? new SpeedLadder
                {
                    StartSpeed = Math.Clamp(config.LivePlaySpeedLadderStartPercent, 25, 100) / 100.0,
                    StepUp = Math.Clamp(config.LivePlaySpeedLadderStepPercent, 1, 25) / 100.0,
                    StepDown = Math.Clamp(config.LivePlaySpeedLadderStepPercent, 1, 25) / 100.0
                }
                : null
        };

        _sessionHadLadder = options.Ladder != null;
        // A one-level chart rides a ladder generated for this run; the chart is untouched.
        var judged = IsDynamicDifficultyOn ? DynamicDifficultyLadder.For(arrangement, _projectState.Beats) : arrangement;
        _session.Start(judged, level, _projectState.Beats, options);
        _stringConfidence.Clear();
        _judgedArrangement = IsDynamicDifficultyOn ? judged : null;
        _judgedBeats = _projectState.Beats;
        HistoryStartPercent = startingDepth is { } depth ? (int)Math.Round(depth * 100) : 0;
        IsStartingFromHistory = startingDepth != null && _session.Difficulty != null;
        PublishGameplayLevel();
        _session.SyncTo(_projectState.CurrentTimeMs);
        // Opt-in take recorder: tee the dry input capture of this run to disk.
        _takeRecorder = RunArchive.TryStartTake(_analyzer!.SampleRate);
        // The verdict is about the CURRENT tuning, so a new run starts with no
        // evidence — and picks up this arrangement's authored cent offset.
        ResetTuningDrift(arrangement);
        FeedbackEvents.Clear();
        MissedRecently.Clear();
        _timing.Reset();
        Meters.BeginRun(); // a new run may fail on its own account
        // The input-silence watch is per-RUN state too: its counter, its
        // ever-saw-signal memory, the worker's shadow and the banner itself.
        // Leaving the counter behind made a run that follows a silent one trip
        // the ~4 s warning a fraction of a second in, on the PREVIOUS run's
        // total; leaving the banner behind left "another app holds your input"
        // on screen after the run that raised it ended.
        _silentSamples = 0;
        _sawAnySignal = false;
        _workerInputSilent = false;
        if (IsInputSilent) IsInputSilent = false;
        _pendingLoopStartMs = null;
        LoopActive = false;
        LoopStartArmed = false;
        LoopLabel = string.Empty;
        PassSummary = string.Empty;
        CountInRemaining = 0;
        StatusHint = string.Empty;
        LastFeedback = string.Empty;

        _speedBeforeSession = _audio.PlaybackSpeed;
        if (_session.Ladder is { } ladder)
            RequestSpeed(ladder.CurrentSpeed);
        else
            CurrentSpeedPercent = (int)Math.Round(_audio.PlaybackSpeed * 100);

        PublishStats();
        OnPropertyChanged(nameof(Sections));
        SectionsVersion = _session.SectionsVersion;
        _logger.Info(AppLogCategory.Audio,
            $"Live Play started. Arrangement='{arrangement.Name}', expected sounds={_session.Matcher!.Expected.Count}, " +
            $"sections={Sections.Count}, sr={_analyzer!.SampleRate}, hitWindow={options.HitWindow}, " +
            $"ladder={(options.Ladder != null ? options.Ladder.StartSpeed.ToString("0.00") : "off")}, dd={options.DynamicDifficulty}, transpose={options.TransposeSemitones}.");
        return true;
    }

    /// <summary>Raised after every session stop (recorded or not) with the
    /// full results-screen payload. Play Mode drives its results UI off this.</summary>
    public event Action<LivePlaySessionReport>? SessionCompleted;

    // What a dynamic-difficulty run judges, for the highway's composite; null when off.
    private Arrangement? _judgedArrangement;
    private IList<BeatMarker> _judgedBeats = [];

    /// <summary>Publishes what the highway draws for this run: the composite of the judged levels
    /// while dynamic difficulty runs, else nothing (the editor's level). Called at run start and on
    /// a phrase level change, on whichever thread drives the session; the result is swapped in whole.</summary>
    /// <summary>The run started its unseen phrases from the player's history: the strip
    /// says so and offers <see cref="StartLadderAtBottomCommand" />.</summary>
    [ObservableProperty] private bool _isStartingFromHistory;

    /// <summary>How far up each ladder the run started, 0–100 (%).</summary>
    [ObservableProperty] private int _historyStartPercent;

    /// <summary>"Start at 0": this run's ladder starts over at the bottom.</summary>
    [RelayCommand]
    private void StartLadderAtBottom()
    {
        lock (_sessionGate)
        {
            if (!_session.StartLadderOverAtBottom(_projectState.CurrentTimeMs)) return;
            PublishGameplayLevel();
        }

        IsStartingFromHistory = false;
    }

    private void PublishGameplayLevel() =>
        _projectState.GameplayLevel = _judgedArrangement is { } judged && _session.Difficulty is { } difficulty
            ? DynamicDifficultyComposite.Build(judged, _judgedBeats, difficulty.LevelFor)
            : null;

    private void Stop()
    {
        LivePlaySessionReport? report;
        lock (_sessionGate)
        {
            report = StopCore();
        }

        // Events + teardown outside the gate: the handlers are heavyweight
        // (results screen), and TearDownAnalysis JOINS the worker — a join
        // under the gate the worker is waiting on would deadlock.
        if (report != null)
        {
            // SessionCompleted rebuilds the results SCREEN, i.e. bound state —
            // it is the only one of the three run events that was raised
            // inline while TakeSaved and RockMeterFailed both marshal. Stop()
            // is only reachable from OnIsActiveChanged today, so it is safe;
            // it is one careless `IsActive = false` from a worker away from
            // not being. Dispatching only when off-thread keeps the current
            // ordering exactly (the results screen still builds before Stop
            // returns) while closing the hole.
            if (Dispatcher.UIThread.CheckAccess()) PublishSessionCompleted(report);
            else Dispatcher.UIThread.Post(() => PublishSessionCompleted(report));
        }

        // The analyzer survives while the detection monitor still wants it.
        if (!ShowDetectionMonitor && !IsCalibrating) TearDownAnalysis();
    }

    private void PublishSessionCompleted(LivePlaySessionReport report) => SessionCompleted?.Invoke(report);

    /// <summary>Session stop under the gate. Returns the results-screen report
    /// (null when no session was active) for the caller to publish outside it.</summary>
    private LivePlaySessionReport? StopCore()
    {
        Volatile.Write(ref _uiEpoch, _uiEpoch + 1); // stale worker snapshots die here
        LivePlaySessionReport? report = null;

        // Detach the take tee before the session tears down so no further
        // pump ticks write into it; finalized asynchronously below.
        var takeRecorder = _takeRecorder;
        _takeRecorder = null;

        IsStartingFromHistory = false;
        if (_session.IsActive)
        {
            // Section snapshot BEFORE _session.Stop() — a later Start() resets
            // the per-section counters in place.
            var sections = Sections
                .Select(s => new SectionResult(s.Name, s.StartMs, s.EndMs, s.SoundCount, s.Hits, s.Misses,
                    s.StartBar, s.EndBar))
                .ToArray();
            // v0.32 run replay: the matcher's judged sounds ARE the replay —
            // snapshot the references BEFORE Stop for the same reason.
            var replaySounds = RunArchive.KeepsReplays ? _session.Matcher?.Expected.ToArray() : null;
            // The run's REAL speed: the ladder's when one drove the transport,
            // else the ordinary speed slider. Read before the ladder-restore
            // below puts _speedBeforeSession back.
            var result = _session.Stop(_audio.PlaybackSpeed);
            // The highway goes back to the editor's level with the run.
            _judgedArrangement = null;
            _projectState.GameplayLevel = null;
            _logger.Info(AppLogCategory.Audio,
                $"Live Play stopped. Score={result.Score}, hits={result.Hits}, misses={result.Misses}, " +
                $"best streak={result.BestStreak}, accuracy={result.Accuracy:P1}, speed={result.SpeedPercent:0}%.");
            var beforeProfile = _profiles.GetProfile(_profileSongKey, _profileArrangement);
            var before = PracticeBests.From(beforeProfile);
            // Captured BEFORE recording: a genuine first attempt vs. a repeat.
            var hadPriorRun = beforeProfile.Sessions.Count > 0;
            var recorded = RecordSession(result, sections);
            var after = PracticeBests.From(_profiles.GetProfile(_profileSongKey, _profileArrangement));
            if (_sessionHadLadder) RequestSpeed(_speedBeforeSession);
            // Built from the same section snapshot, so the per-section bias lines up
            // with the section rows the results screen already shows.
            var timing = _timing.BuildReport(sections);
            // Opt-in: refine this device's judging offset from the run's residual bias.
            AutoCalibrateFromRun(timing);
            report = new LivePlaySessionReport(result, before, after, recorded,
                _profileSongKey, _profileArrangement, sections, hadPriorRun, timing, _session.LastTechniqueTallies);
            // Take recorder: finalize off-thread (WAV close + optional Opus
            // encode), attach to the just-recorded session, sweep orphans.
            if (takeRecorder != null)
            {
                RunArchive.FinalizeTakeAsync(takeRecorder, recorded, _profileSongKey, _profileArrangement);
                takeRecorder = null;
            }

            // Run replay (v0.32): projected UNDER the gate (pure, the sounds
            // may be reset by a later Start), persisted + attached off-thread
            // — never disk I/O under the session gate. Recorded runs only.
            if (recorded && replaySounds != null)
            {
                RunArchive.SaveReplayAsync(DotCharter.Services.Audio.LivePlay.Replays.RunReplayCapture.FromExpected(
                    replaySounds, _profileSongKey, _profileArrangement,
                    result.SpeedPercent, result.Score, DateTime.UtcNow));
            }
        }

        // Session never became active (or nothing judged): close the file;
        // the unattached take is swept by the next cleanup pass.
        if (takeRecorder != null)
            RunArchive.FinalizeTakeAsync(takeRecorder, attach: false, string.Empty, string.Empty);

        LoopActive = false;
        LoopStartArmed = false;
        CountInRemaining = 0;
        _firstNoteCountIn.Disarm(); // no run, no pre-note count-in
        DisarmRockMeter(); // stakes end with the run, on every exit path
        DisarmCrowdMeter(); // the crowd HUD ends with the run too
        ResetTuningDrift(null); // no run, no drift nudge
        PublishResolvedThrough(double.NegativeInfinity); // no run, no dead notes
        return report;
    }
}
