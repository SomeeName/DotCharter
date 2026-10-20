using DotCharter.Services.Gameplay.Scoring;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>One chart section with its live hit/miss tally (for the section
/// strip / riff-repeater picker).</summary>
public sealed class LivePlaySection
{
    public string Name { get; init; } = string.Empty;
    public double StartMs { get; init; }
    public double EndMs { get; init; }
    public int SoundCount { get; init; }
    public int Hits { get; internal set; }
    public int Misses { get; internal set; }
    /// <summary>Zero judged is 0 — see <see cref="AccuracyMath" />. An UNPLAYED
    /// section is not a perfect one; <c>LivePlaySectionGraph.HeatBrush</c> and
    /// <c>LivePlaySessionReport.WorstSection</c> both check the tally before
    /// they read this.</summary>
    public double Accuracy => AccuracyMath.FromCounts(Hits, Misses);

    /// <summary>Whether this section has been played at all yet.</summary>
    public bool IsMeasured => AccuracyMath.IsMeasured(Hits, Misses);

    /// <summary>1-based bar this section starts on, or null when the chart
    /// carries no measure markers. Captured HERE, while the beat map is open —
    /// the practice store persists no beat map, so a recorded run can never be
    /// re-barred afterwards. Null means "not recorded", never bar 0.</summary>
    public int? StartBar { get; init; }

    /// <summary>1-based bar this section ends on, inclusive (see
    /// <see cref="LiveBarNumbers.RangeOf" /> for why it is not the next
    /// section's first bar).</summary>
    public int? EndBar { get; init; }
}

/// <summary>One phrase span for the Rocksmith-style structure strip — the
/// purple density bars; sections are the orange underline.</summary>
public sealed class LivePlayPhrase
{
    public string Name { get; init; } = string.Empty;
    public double StartMs { get; init; }
    public double EndMs { get; init; }
    public int SoundCount { get; init; }
}

/// <summary>
///     An A/B loop over the chart timeline — the ONE loop region in the app.
///     Live Play drives it as a practice repeater with count-in, passes and a
///     speed ladder; the EDITOR drives it as a plain transport loop
///     (<c>Editing/Playback/EditorLoopService</c>). Both share this type, its
///     normalization and its wrap test, so there is one implementation of "what
///     is a legal loop and when does it come round" rather than two that drift.
/// </summary>
public readonly record struct LivePlayLoop(double StartMs, double EndMs)
{
    /// <summary>Shorter than this and a loop is a stutter, not a practice
    /// region — and at a 250 Hz tick it could wrap several times per second.</summary>
    public const double MinLengthMs = 250;

    public double LengthMs => EndMs - StartMs;

    /// <summary>
    ///     Builds a loop from two punch points in either order. False (and no
    ///     loop) when the region is degenerate, which is how both callers spell
    ///     "the user has not marked a usable region yet".
    /// </summary>
    public static bool TryCreate(double aMs, double bMs, out LivePlayLoop loop)
    {
        if (bMs < aMs) (aMs, bMs) = (bMs, aMs);
        if (bMs - aMs < MinLengthMs)
        {
            loop = default;
            return false;
        }

        loop = new LivePlayLoop(aMs, bMs);
        return true;
    }

    /// <summary>The pass is over — the transport must come back to
    /// <see cref="StartMs" />. Which playhead the caller measures against is the
    /// caller's decision and they differ: Live Play judges on the AUDIBLE
    /// (latency-compensated) axis, the editor loop wraps on the RAW one so the
    /// samples past the loop end are never rendered at all.</summary>
    public bool ShouldWrap(double playheadMs) => playheadMs >= EndMs;
}

/// <summary>One missed sound, for the "you missed these" replay list.</summary>
public readonly record struct LivePlayMissInfo(double TimeMs, string Label);

/// <summary>How a live-play session is configured when it starts.</summary>
public sealed class LivePlaySessionOptions
{
    public LiveHitWindowPreset HitWindow { get; init; } = LiveHitWindowPreset.Normal;

    /// <summary>Play the leveled chart RS-style, moving per-phrase levels
    /// with live accuracy. When false the fixed level is used.</summary>
    public bool DynamicDifficulty { get; init; }

    /// <summary>Persisted per-phrase ladder levels (phrase name → level) to
    /// resume dynamic difficulty where the player left it last session.
    /// Null/empty = every phrase starts fresh. Ignored without
    /// <see cref="DynamicDifficulty"/>.</summary>
    public IReadOnlyDictionary<string, int>? InitialPhraseLevels { get; init; }

    /// <summary>Where an unseen phrase starts on its ladder, 0..1: the player's depth from
    /// their history on other songs (<see cref="PlayerLadderDepth" />). Null starts
    /// at the bottom. Ignored without <see cref="DynamicDifficulty"/>.</summary>
    public double? StartingDepth { get; init; }

    /// <summary>Speed ladder; null = play at the transport's own speed.</summary>
    public SpeedLadder? Ladder { get; init; }

    /// <summary>Count-in beats played before a loop pass starts.</summary>
    public int CountInBeats { get; init; } = 4;

    /// <summary>Chord voicing strictness passed through to the matcher.</summary>
    public LiveChordVoicingStrictness ChordStrictness { get; init; } = LiveChordVoicingStrictness.Standard;

    /// <summary>String-aware verification (drop-pedal-aware pitch map).</summary>
    public bool StringAwareVerification { get; init; }

    /// <summary>Detection v8: when set, the matcher judges pitched sounds by
    /// pull-verifying them against the rolling CQT (fed by the UI capture tap)
    /// instead of matching pushed pitch candidates. Null = classic path.</summary>
    public Func<ExpectedSound, ChartVerifyOutcome>? ChartVerify { get; init; }

    /// <summary>Detection v8's EARLY check (<see cref="LiveNoteMatcher.ChartVerifyEarly" />):
    /// credits a pitched sound the moment it is confirmed instead of when its window closes.
    /// <see cref="LiveCqtVerifier.VerifyEarly" /> in the app. Null = hits wait for the close.</summary>
    public Func<ExpectedSound, ChartVerifyOutcome>? ChartVerifyEarly { get; init; }

    /// <summary>Judge bends, slides and vibrato from the pitch contour
    /// (<see cref="LiveNoteMatcher.JudgeContour" />): <see cref="LiveCqtVerifier.JudgeTechnique" />
    /// when any of the per-technique switches is on (<c>AppConfig.LivePlayJudgeBends</c>,
    /// <c>LivePlayJudgeSlides</c>, <c>LivePlayJudgeVibrato</c>). CQT mode only. Null = off.</summary>
    public Func<ExpectedSound, TechniqueContourVerdict?>? JudgeContour { get; init; }

    /// <summary>Which techniques <see cref="JudgeContour" /> rules on
    /// (<see cref="LiveNoteMatcher.JudgedTechniques" />). All by default.</summary>
    public ContourTechniques JudgedTechniques { get; init; } = ContourTechniques.All;

    /// <summary>Tuning-match transpose: the song plays shifted by this many semitones to the
    /// player's tuning, so every expected pitch moves with it
    /// (<see cref="LiveNoteMatcher.TransposeSemitones" />). 0 = the chart's own pitches.</summary>
    public int TransposeSemitones { get; init; }
}

/// <summary>Final numbers of one live-play session, for persistence.
/// Tier counts (Perfect/Great/Good) may not sum to <paramref name="Hits"/>:
/// hits judged without timing evidence (CQT-confirmed, no attack onset)
/// carry no tier. <paramref name="Detector"/> is the judging path that produced
/// it: runs of different detectors are never ranked against each other.</summary>
public readonly record struct LivePlaySessionResult(
    long Score, double Accuracy, int Hits, int Misses, int TotalSounds, int BestStreak,
    double DurationSeconds, double SpeedPercent, double SongProgress,
    int PerfectCount = 0, int GreatCount = 0, int GoodCount = 0,
    LiveDetectorMode Detector = LiveDetectorMode.Unknown)
{
    /// <summary>Full combo: EVERY sound of the chart was judged and none
    /// missed. A clean partial run (seeked-over or aborted early) is not an
    /// FC — the denominator is the whole chart, matching how
    /// <see cref="LivePlayStats.SongProgress"/> counts coverage. Technique
    /// downgrades (<see cref="LivePlayVerdict.HitWrongTechnique"/>) don't
    /// break a combo today, so they don't break an FC either.</summary>
    public bool IsFullCombo => TotalSounds > 0 && Misses == 0 && Hits == TotalSounds;

    /// <summary>
    ///     Fraction of the chart this run actually JUDGED, derived from the raw
    ///     counts rather than read from <see cref="SongProgress" /> so a caller
    ///     cannot make the two disagree. <see cref="Accuracy" /> is
    ///     <c>Hits / judged</c> and says nothing about how much of the song was
    ///     played — this is the other half, and the gate on
    ///     <see cref="PracticeProfileService.MinCoverageForBestAccuracy" />.
    /// </summary>
    public double Coverage => TotalSounds <= 0 ? 0.0 : (Hits + Misses) / (double)TotalSounds;
}

/// <summary>What one tick of the session produced — the UI applies the
/// transport side effects (seek, speed, metronome ticks) and repaints.</summary>
public sealed class LivePlayTickResult
{
    public List<LivePlayEvent> Events { get; } = [];

    /// <summary>The contour judge's rulings on hits already credited. Not judgments:
    /// a NotMet is a downgrade the HUD shows, never a new hit or miss.</summary>
    public List<LiveTechniqueJudgment> TechniqueJudgments { get; } = [];

    /// <summary>Timing cues for hits credited THIS tick that a provisional onset already times
    /// (<see cref="LiveNoteMatcher.ProvisionalGrades" />). Not judgments, and not scored.</summary>
    public List<LivePlayGrade> Grades { get; } = [];

    /// <summary>Transport seek the session requests (loop wrap / pass start).</summary>
    public double? SeekToMs { get; set; }

    /// <summary>New playback speed after a ladder step (fraction of real time).</summary>
    public double? SpeedChange { get; set; }

    /// <summary>Metronome count-in ticks to emit this tick.</summary>
    public int CountInTicks { get; set; }

    /// <summary>True while the playhead is inside the pre-roll before the loop start.</summary>
    public bool InCountIn { get; set; }

    public bool PassCompleted { get; set; }
    public double PassAccuracy { get; set; }
    public int PassJudged { get; set; }
    public bool DifficultyChanged { get; set; }
    public bool StatsChanged { get; set; }

    /// <summary>Clears every field so one instance can be reused across the
    /// 30 Hz ticks (see the HandleTick overload taking a result).</summary>
    public void Reset()
    {
        Events.Clear();
        TechniqueJudgments.Clear();
        Grades.Clear();
        SeekToMs = null;
        SpeedChange = null;
        CountInTicks = 0;
        InCountIn = false;
        PassCompleted = false;
        PassAccuracy = 0;
        PassJudged = 0;
        DifficultyChanged = false;
        StatsChanged = false;
    }
}

/// <summary>
///     Owns one Live Play practice session: matcher lifecycle, section
///     tallies, A/B looping with count-in, the speed ladder and dynamic
///     difficulty. Pure application logic — no audio, no dispatcher, no UI —
///     driven by <see cref="HandleTick(double, bool)"/> with the transport playhead and by
///     <see cref="Offer"/> with detection events, so every practice-suite
///     rule is unit-testable. The view model applies the returned transport
///     side effects (seeks, speed changes, metronome ticks).
/// </summary>
public sealed class LivePlaySessionService
{
    private const double SeekJumpThresholdMs = 750;
    private const int RecentMissCap = 16;

    private Arrangement? _arrangement;
    private IList<BeatMarker> _beats = Array.Empty<BeatMarker>();
    private LiveDifficultySelector? _selector;
    private List<LivePlaySection> _sections = [];
    private List<LivePlayPhrase> _phrases = [];
    private List<LivePhraseSpan> _phraseSpans = [];
    private readonly HashSet<int> _completedPhrases = [];
    private readonly List<LivePlayMissInfo> _recentMisses = [];
    private double _lastPlayheadMs;
    private double? _pendingSeekTargetMs;
    private double? _countInUntilMs;
    private double _loopCountInStartMs;
    private DateTime _startedUtc;

    public LiveNoteMatcher? Matcher { get; private set; }
    public bool IsActive => Matcher != null;
    public SpeedLadder? Ladder { get; private set; }
    public DynamicDifficultyEngine? Difficulty { get; private set; }
    public LivePlayLoop? Loop { get; private set; }
    public int PassCount { get; private set; }

    public IReadOnlyList<LivePlaySection> Sections => _sections;

    /// <summary>Phrase spans with expected-sound counts (structure strip).</summary>
    public IReadOnlyList<LivePlayPhrase> Phrases => _phrases;
    public IReadOnlyList<LivePhraseSpan> PhraseSpans => _phraseSpans;

    /// <summary>Misses of the last completed loop pass ("you missed these").</summary>
    public IReadOnlyList<LivePlayMissInfo> LastPassMisses { get; private set; } = Array.Empty<LivePlayMissInfo>();

    /// <summary>Rolling recent misses during free (non-loop) play.</summary>
    public IReadOnlyList<LivePlayMissInfo> RecentMisses => _recentMisses;

    /// <summary>Bumped whenever section tallies change, so the strip repaints.</summary>
    public int SectionsVersion { get; private set; }

    public LivePlayStats? Stats => Matcher?.Stats;

    /// <summary>Starts a session over the arrangement. With dynamic
    /// difficulty the leveled chart is loaded phrase by phrase; otherwise
    /// <paramref name="fixedLevel"/> (or the highest level) plays whole.</summary>
    public void Start(Arrangement arrangement, ChartLevel? fixedLevel, IList<BeatMarker> beats,
        LivePlaySessionOptions options)
    {
        _arrangement = arrangement;
        _beats = beats;
        _startedUtc = DateTime.UtcNow;
        PassCount = 0;
        Loop = null;
        _pendingSeekTargetMs = null;
        _countInUntilMs = null;
        _completedPhrases.Clear();
        _recentMisses.Clear();
        LastPassMisses = Array.Empty<LivePlayMissInfo>();
        LastPhraseOutcomes = Array.Empty<PhraseMasteryOutcome>();
        LastTechniqueTallies = Array.Empty<TechniqueResult>();
        CountInBeats = Math.Max(0, options.CountInBeats);

        Ladder = options.Ladder;
        Ladder?.Reset();

        var matcher = LiveHitWindows.CreateMatcher(options.HitWindow,
            options.ChordStrictness, options.StringAwareVerification, options.ChartVerify, options.ChartVerifyEarly,
            options.TransposeSemitones, options.JudgeContour, options.JudgedTechniques);

        _phraseSpans = LiveExpectedTimeline.BuildPhraseSpans(arrangement, beats);
        if (options.DynamicDifficulty && _phraseSpans.Count > 0)
        {
            Difficulty = new DynamicDifficultyEngine { StartingDepth = options.StartingDepth };
            if (options.InitialPhraseLevels is { Count: > 0 } seed) Difficulty.Seed(seed);
            _selector = span => Difficulty!.LevelFor(span);
            matcher.Load(arrangement, beats, _selector);
        }
        else
        {
            Difficulty = null;
            _selector = null;
            if (fixedLevel != null) matcher.Load(arrangement, fixedLevel, beats);
            else matcher.Load(arrangement, beats, _ => int.MaxValue);
        }

        Matcher = matcher;
        BuildSections(arrangement);
    }

    /// <summary>Count-in beats configured for loop passes.</summary>
    public int CountInBeats { get; private set; } = 4;

    /// <summary>Per-phrase dynamic-difficulty outcomes of the LAST stopped
    /// session (empty without dynamic difficulty) — captured by
    /// <see cref="Stop"/> before the engine is torn down, for the practice
    /// store's per-phrase mastery persistence.</summary>
    public IReadOnlyList<PhraseMasteryOutcome> LastPhraseOutcomes { get; private set; } =
        Array.Empty<PhraseMasteryOutcome>();

    /// <summary>Per-technique tallies of the LAST stopped session (0.21
    /// technique report card) — one scan over the judged sounds, captured by
    /// <see cref="Stop"/> before the matcher is torn down, for the practice
    /// store's per-session technique persistence. Empty when the chart
    /// demanded no techniques (or nothing was judged).</summary>
    public IReadOnlyList<TechniqueResult> LastTechniqueTallies { get; private set; } =
        Array.Empty<TechniqueResult>();

    /// <summary>Ends the session and returns its final numbers.</summary>
    /// <param name="transportSpeed">
    ///     The PLAYBACK speed the run ended at, as a factor (1.0 = real time) —
    ///     i.e. the ordinary speed slider. Required, not defaulted: the recorded
    ///     speed is NOT <c>Ladder?.CurrentSpeed ?? 1.0</c>, because
    ///     <see cref="LivePlaySessionOptions.Ladder" /> is non-null ONLY for the
    ///     riff-repeater ladder or an armed smart-practice drill. So an ordinary
    ///     50 %-speed run would record as 100 %, which inflates
    ///     <c>PracticeArrangementProfile.MasteryPercent</c> (it multiplies by the
    ///     speed factor) and flattens the weekly average-speed trend to
    ///     100 %. A default here would silently
    ///     reintroduce exactly that for the next caller.
    /// </param>
    public LivePlaySessionResult Stop(double transportSpeed)
    {
        // A hit credited within the onset's reporting delay of the end keeps its
        // credit and is graded untimed, never dropped.
        Matcher?.ResolvePendingGrades();
        var stats = Matcher?.Stats;
        // The ladder OWNS the transport while it is running, so its speed is the
        // authority when there is one; otherwise the slider is.
        var speed = Ladder?.CurrentSpeed ?? transportSpeed;
        var result = stats == null
            ? default
            : new LivePlaySessionResult(stats.Score, stats.Accuracy, stats.Hits, stats.Misses,
                stats.TotalSounds, stats.BestStreak,
                (DateTime.UtcNow - _startedUtc).TotalSeconds,
                speed * 100.0, stats.SongProgress,
                stats.PerfectCount, stats.GreatCount, stats.GoodCount,
                Matcher is { ChartVerifyMode: true } ? LiveDetectorMode.Cqt : LiveDetectorMode.Classic);
        LastPhraseOutcomes = Difficulty?.SnapshotOutcomes()
                             ?? (IReadOnlyList<PhraseMasteryOutcome>)[];
        LastTechniqueTallies = Matcher is { } matcher
            ? LiveTechniqueTally.Compute(matcher.Expected)
            : Array.Empty<TechniqueResult>();
        Matcher = null;
        Loop = null;
        Ladder = null;
        Difficulty = null;
        _arrangement = null;
        return result;
    }

    /// <summary>Positions the session at the given playhead (initial sync).</summary>
    public void SyncTo(double playheadMs)
    {
        Matcher?.Rewind(playheadMs);
        _lastPlayheadMs = playheadMs;
        RecomputeSectionTallies();
    }

    // ---- A/B loop -----------------------------------------------------------

    /// <summary>
    ///     Sets the practice loop and arms the first pass. Returns the seek
    ///     target (loop start minus the count-in pre-roll) the transport
    ///     should jump to; null when the session is inactive or the region
    ///     is degenerate.
    /// </summary>
    public double? SetLoop(double startMs, double endMs)
    {
        if (Matcher == null) return null;
        if (!LivePlayLoop.TryCreate(startMs, endMs, out var loop)) return null;

        Loop = loop;
        _loopCountInStartMs = CountInStartFor(loop.StartMs);
        return ArmLoopPass();
    }

    /// <summary>Loops one chart section (riff-repeater pick).</summary>
    public double? SetLoop(LivePlaySection section) => SetLoop(section.StartMs, section.EndMs);

    public void ClearLoop()
    {
        Loop = null;
        _countInUntilMs = null;
    }

    private double? ArmLoopPass()
    {
        var loop = Loop!.Value;
        // RewindToLoop, not Rewind: the pass's judging starts AT the loop, and
        // the sounds in the hit-window band just before it are inside the
        // count-in pre-roll, where Offer refuses to judge — re-arming them made
        // every pass expire them as misses nobody could have hit. See
        // LiveNoteMatcher.RewindToLoop.
        Matcher!.RewindToLoop(loop.StartMs);
        RecomputeSectionTallies();
        _completedPhrases.RemoveWhere(i => i >= 0 && i < _phraseSpans.Count &&
                                           _phraseSpans[i].EndMs > loop.StartMs);
        _countInUntilMs = loop.StartMs;
        _pendingSeekTargetMs = _loopCountInStartMs;
        return _loopCountInStartMs;
    }

    /// <summary>Time of the beat <see cref="CountInBeats"/> beats before the
    /// loop start (clamped to the chart start). Falls back to 500 ms beats
    /// when the beat grid doesn't reach back far enough.</summary>
    private double CountInStartFor(double loopStartMs)
    {
        if (CountInBeats == 0) return loopStartMs;
        var before = 0;
        for (var i = _beats.Count - 1; i >= 0; i--)
        {
            if (_beats[i].Time >= loopStartMs) continue;
            before++;
            if (before == CountInBeats) return Math.Max(0, _beats[i].Time);
        }

        return Math.Max(0, loopStartMs - CountInBeats * 500.0);
    }

    // ---- resume-rewind (Rocksmith pause semantics) ---------------------------

    /// <summary>
    ///     Arms the Rocksmith-style resume rewind: on unpause the transport backs
    ///     up by <paramref name="leadInMs" /> so the player re-orients before notes
    ///     arrive. Returns the seek target the caller must seek to (null = no
    ///     rewind: inactive session, non-positive lead-in, or already at/behind the
    ///     target).
    ///     <para>
    ///     This is the loop wrap MINUS the rewind: the seek is registered as the
    ///     session's OWN (<see cref="_pendingSeekTargetMs" />) and the last playhead
    ///     is moved with it, so <see cref="HandleTick(double, bool)" /> can never see a backwards
    ///     jump and never calls <see cref="LiveNoteMatcher.Rewind" /> — which would
    ///     DELETE the verdicts (and the score) of everything after the pause point.
    ///     Sounds resolved before the pause therefore stay resolved: the matcher's
    ///     cursor is past them, so <c>AdvanceTo</c> emits no phantom misses and
    ///     <c>Offer*</c> can't re-score them. Detection over the replayed stretch is
    ///     inert for free.
    ///     </para>
    ///     Deliberately does NOT touch <c>_completedPhrases</c> or the section
    ///     tallies: a phrase re-completion at replay accuracy would DEMOTE the
    ///     dynamic-difficulty ladder (it persists downward), and no verdict changed.
    /// </summary>
    public double? BeginResumeRewind(double pausedAtMs, double leadInMs)
    {
        if (Matcher == null || leadInMs <= 0) return null;

        var target = Math.Max(0, pausedAtMs - leadInMs);
        if (Loop is { } loop) target = Math.Max(target, loop.StartMs); // never rewind out of the loop
        if (target >= pausedAtMs) return null;

        _pendingSeekTargetMs = target;
        // LOAD-BEARING: with the last playhead moved to the target, the jump the
        // seek produces is invisible to HandleTick — suppression does not depend
        // on the SeekJumpThreshold/landing-tolerance constants lining up.
        _lastPlayheadMs = target;
        return target;
    }

    // ---- per-tick drive ------------------------------------------------------

    /// <summary>
    ///     Advances the session to the current playhead: detects external
    ///     seeks (Rocksmith rewind semantics), emits count-in ticks during a
    ///     loop pre-roll, wraps the loop (completing the pass: accuracy →
    ///     speed ladder / dynamic difficulty), completes phrases for dynamic
    ///     difficulty during free play, and expires hit windows into misses.
    /// </summary>
    public LivePlayTickResult HandleTick(double playheadMs, bool isPlaying)
        => HandleTick(playheadMs, isPlaying, new LivePlayTickResult());

    /// <summary>
    ///     Back-compat overload: callers that cannot report seek evidence keep
    ///     the historical behavior (every large jump treated as a seek).
    /// </summary>
    public LivePlayTickResult HandleTick(double playheadMs, bool isPlaying, LivePlayTickResult result)
        => HandleTick(playheadMs, isPlaying, result, externalSeek: true);

    /// <summary>
    ///     Allocation-free variant for the steady-state 30 Hz tick:
    ///     <paramref name="result"/> is Reset() and repopulated, so a caller
    ///     can reuse one instance for the session's lifetime.
    ///     <paramref name="externalSeek"/> is the caller's seek evidence for
    ///     this tick (e.g. the audio service's seek-generation counter moved):
    ///     a large playhead jump WITH it is a real seek and rewinds (Rocksmith
    ///     semantics); a large FORWARD jump WITHOUT it is a stalled tick loop
    ///     (an incident: a starved UI-thread timer produced >750 ms
    ///     gaps, every tick "rewound", and judging went permanently inert) —
    ///     the session then simply advances, resolving the gap's notes as the
    ///     misses an unstalled session would have judged. Unseeked BACKWARD
    ///     jumps still rewind: time does not run backwards without a seek, so
    ///     treating them as seeks is the safe interpretation.
    /// </summary>
    public LivePlayTickResult HandleTick(double playheadMs, bool isPlaying, LivePlayTickResult result,
        bool externalSeek)
    {
        result.Reset();
        var matcher = Matcher;
        if (matcher == null)
        {
            _lastPlayheadMs = playheadMs;
            return result;
        }

        var lastPlayhead = _lastPlayheadMs;
        var jumped = Math.Abs(playheadMs - lastPlayhead) > SeekJumpThresholdMs;
        // Our own loop-wrap seek landing — matcher already positioned.
        var ownSeekLanded = _pendingSeekTargetMs is { } target && Math.Abs(playheadMs - target) < 500;
        if (ownSeekLanded) _pendingSeekTargetMs = null;
        // A forward jump with NO seek evidence is a stalled tick loop, not a
        // seek: fall through to the ordinary advance, which resolves the gap's
        // notes as misses (see the externalSeek doc above).
        var stalledForward = jumped && !externalSeek && isPlaying && playheadMs > lastPlayhead;
        if (jumped && !ownSeekLanded && !stalledForward)
        {
            _pendingSeekTargetMs = null;
            matcher.Rewind(playheadMs);
            RecomputeSectionTallies();
            _completedPhrases.RemoveWhere(i => i >= 0 && i < _phraseSpans.Count &&
                                               _phraseSpans[i].EndMs > playheadMs);
            result.StatsChanged = true;
        }

        _lastPlayheadMs = playheadMs;
        if (!isPlaying) return result;

        // Count-in pre-roll: metronome ticks on crossed beats, no judging.
        if (_countInUntilMs is { } loopStartMs)
        {
            if (playheadMs < loopStartMs)
            {
                result.InCountIn = true;
                result.CountInTicks = CountBeatsCrossed(jumped ? playheadMs - 1 : lastPlayhead, playheadMs);
                return result;
            }

            _countInUntilMs = null;
        }

        // Wrap only once the previous pass's seek has landed — otherwise a
        // slow transport seek would complete phantom zero-judged passes.
        if (Loop is { } loop && _pendingSeekTargetMs == null && loop.ShouldWrap(playheadMs))
        {
            CompletePass(result);
            return result;
        }

        if (Loop == null && Difficulty != null)
            CompleteElapsedPhrases(playheadMs, result);

        foreach (var missed in matcher.AdvanceTo(playheadMs))
        {
            ApplyTally(missed);
            result.Events.Add(missed);
        }

        result.TechniqueJudgments.AddRange(matcher.TechniqueJudgments);
        result.Grades.AddRange(matcher.ProvisionalGrades);

        if (result.Events.Count > 0) result.StatsChanged = true;
        return result;
    }

    /// <summary>
    ///     Offers one detection event to the matcher and tallies the verdict(s).
    ///     <paramref name="alsoJudged" /> is CLEARED and then receives every
    ///     judgment BEYOND the returned one — one attack resolves every expected
    ///     sound at the same chart time (a double stop the chart spells as two
    ///     notes; see <see cref="LiveNoteMatcher.OfferPitches" />). Pass it only
    ///     to surface them: every judgment is tallied here either way.
    ///     <paramref name="graded" /> is CLEARED and then receives the timing grades the onset
    /// gave hits ALREADY credited (CQT mode). They are not judgments and tally nothing
    ///     here — the hit was tallied when it was credited; the stats are graded either way.
    /// </summary>
    public LivePlayEvent? Offer(ReadOnlySpan<LivePitch> pitches, ReadOnlySpan<float> chroma,
        double timeMs, LiveTechniqueFlags techniques, float centroidHz,
        List<LivePlayEvent>? alsoJudged = null, List<LivePlayGrade>? graded = null,
        ulong? detectorSamplePosition = null)
    {
        alsoJudged?.Clear();
        graded?.Clear();
        var matcher = Matcher;
        if (matcher == null) return null;
        if (_countInUntilMs is { } loopStart && timeMs < loopStart) return null;

        var result = matcher.OfferPitches(pitches, timeMs, techniques, centroidHz, alsoJudged, graded,
                         detectorSamplePosition is { } onsetKey ? (long)onsetKey : -1)
                     ?? matcher.OfferChroma(chroma, timeMs, centroidHz, techniques);
        if (result is { } judged) ApplyTally(judged);
        if (alsoJudged != null)
        {
            foreach (var extra in alsoJudged)
                ApplyTally(extra);
        }

        return result;
    }

    /// <summary>
    /// Offers a provisional onset — the attack detector firing at
    ///     <paramref name="timeMs" />, ~170 ms before the event that finalizes it reaches
    ///     <see cref="Offer" /> — or its withdrawal. <paramref name="graded" /> is CLEARED and
    ///     then receives the timing CUES it gave hits already credited
    ///     (<see cref="LivePlayGradeStage.Provisional" />). Nothing is tallied or scored; outside
    ///     the CQT matcher this does nothing.
    /// </summary>
    public void OfferOnsetNotice(in LiveOnsetNotice notice, double timeMs, List<LivePlayGrade>? graded = null)
    {
        graded?.Clear();
        var matcher = Matcher;
        if (matcher == null) return;
        var key = (long)notice.DetectorSamplePosition;
        if (notice.IsWithdrawn)
        {
            matcher.WithdrawProvisionalOnset(key);
            return;
        }

        if (_countInUntilMs is { } loopStart && timeMs < loopStart) return;
        matcher.OfferProvisionalOnset(key, timeMs, graded);
    }

    // ---- internals -----------------------------------------------------------

    private void CompletePass(LivePlayTickResult result)
    {
        var matcher = Matcher!;
        var loop = Loop!.Value;

        var hits = 0;
        var misses = 0;
        var passMisses = new List<LivePlayMissInfo>();
        foreach (var sound in matcher.Expected)
        {
            if (sound.TimeMs < loop.StartMs || sound.TimeMs >= loop.EndMs) continue;
            if (sound.Verdict is not { } v) continue;
            if (v == LivePlayVerdict.Miss)
            {
                misses++;
                passMisses.Add(new LivePlayMissInfo(sound.TimeMs, sound.Label));
            }
            else
            {
                hits++;
            }
        }

        var judgedCount = hits + misses;
        PassCount++;
        LastPassMisses = passMisses;
        result.PassCompleted = true;
        result.PassJudged = judgedCount;
        result.PassAccuracy = AccuracyMath.FromCounts(hits, misses);
        result.StatsChanged = true;

        if (Ladder is { } ladder)
        {
            var before = ladder.CurrentSpeed;
            var after = ladder.CompletePass(result.PassAccuracy, judgedCount);
            if (Math.Abs(after - before) > 1e-9) result.SpeedChange = after;
        }

        if (Difficulty is { } difficulty && _arrangement is { } arrangement)
        {
            var changed = false;
            foreach (var span in _phraseSpans)
            {
                if (span.StartMs < loop.StartMs || span.StartMs >= loop.EndMs) continue;
                changed |= CompletePhraseSpan(difficulty, span);
            }

            if (changed && _selector != null)
            {
                matcher.Reload(arrangement, _beats, _selector, loop.StartMs);
                RebuildSectionSoundCounts();
                result.DifficultyChanged = true;
            }
        }

        result.SeekToMs = ArmLoopPass();
    }

    /// <summary>Free-play dynamic difficulty: once the playhead has fully
    /// passed a phrase iteration, its accuracy moves the phrase level and
    /// later iterations of the same phrase are reloaded at the new level.</summary>
    private void CompleteElapsedPhrases(double playheadMs, LivePlayTickResult result)
    {
        var matcher = Matcher!;
        if (Difficulty is not { } difficulty || _arrangement is not { } arrangement || _selector == null) return;

        var horizon = playheadMs - matcher.HitWindowLateMs;
        var changed = false;
        foreach (var span in _phraseSpans)
        {
            if (span.EndMs > horizon || _completedPhrases.Contains(span.IterationIndex)) continue;
            _completedPhrases.Add(span.IterationIndex);
            changed |= CompletePhraseSpan(difficulty, span);
        }

        if (!changed) return;
        matcher.Reload(arrangement, _beats, _selector, playheadMs);
        RebuildSectionSoundCounts();
        result.DifficultyChanged = true;
        result.StatsChanged = true;
    }

    /// <summary>
    /// "Start at 0": the run's ladder starts over at the bottom and the sounds still
    ///     ahead of <paramref name="playheadMs" /> are reloaded at it. False without a ladder.
    /// </summary>
    public bool StartLadderOverAtBottom(double playheadMs)
    {
        if (Difficulty is not { } difficulty || Matcher is not { } matcher || _arrangement is not { } arrangement
            || _selector == null)
        {
            return false;
        }

        difficulty.StartOverAtInitialLevel();
        matcher.Reload(arrangement, _beats, _selector, playheadMs);
        RebuildSectionSoundCounts();
        return true;
    }

    private bool CompletePhraseSpan(DynamicDifficultyEngine difficulty, LivePhraseSpan span)
    {
        var hits = 0;
        var misses = 0;
        foreach (var sound in Matcher!.Expected)
        {
            if (sound.PhraseIterationIndex != span.IterationIndex) continue;
            if (sound.Verdict is not { } v) continue;
            if (v == LivePlayVerdict.Miss) misses++;
            else hits++;
        }

        return difficulty.CompletePhrase(span, hits, misses);
    }

    /// <summary>Beats left in the count-in pre-roll (0 when not counting in).</summary>
    public int CountInBeatsRemaining(double playheadMs)
    {
        if (_countInUntilMs is not { } loopStart || playheadMs >= loopStart) return 0;
        return CountBeatsCrossed(playheadMs, loopStart);
    }

    private int CountBeatsCrossed(double fromMs, double toMs)
    {
        if (toMs <= fromMs) return 0;
        var count = 0;
        // Indexed: foreach over the IList boxes its enumerator per call.
        for (var i = 0; i < _beats.Count; i++)
        {
            var time = _beats[i].Time;
            if (time > fromMs && time <= toMs) count++;
            else if (time > toMs) break;
        }

        return count;
    }

    private void ApplyTally(LivePlayEvent ev)
    {
        if (ev.Verdict == LivePlayVerdict.Miss)
        {
            _recentMisses.Add(new LivePlayMissInfo(ev.Sound.TimeMs, ev.Sound.Label));
            while (_recentMisses.Count > RecentMissCap) _recentMisses.RemoveAt(0);
        }

        var section = SectionAt(ev.Sound.TimeMs);
        if (section == null) return;
        if (ev.Verdict == LivePlayVerdict.Miss) section.Misses++;
        else section.Hits++;
        SectionsVersion++;
    }

    public LivePlaySection? SectionAt(double timeMs)
    {
        foreach (var s in _sections)
        {
            if (timeMs >= s.StartMs && timeMs < s.EndMs)
                return s;
        }

        return _sections.Count > 0 && timeMs >= _sections[^1].EndMs ? _sections[^1] : null;
    }

    /// <summary>Sections from the arrangement's event points (falling back to
    /// one whole-song block), each carrying its expected-sound density.</summary>
    private void BuildSections(Arrangement arrangement)
    {
        var matcher = Matcher!;
        var beats = _beats;
        var songEndMs = matcher.Expected.Count > 0 ? matcher.Expected[^1].TimeMs + 2000 : 0;

        var markers = new List<(double Ms, string Name)>();
        foreach (var ep in arrangement.EventPoints)
        {
            if (string.IsNullOrEmpty(ep.Section)) continue;
            markers.Add((ep.Position.GetPositionMs(beats), ep.Section));
        }

        markers.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));
        var sections = new List<LivePlaySection>();
        if (markers.Count == 0)
        {
            var wholeSong = LiveBarNumbers.RangeOf(beats, 0, Math.Max(songEndMs, 1));
            sections.Add(new LivePlaySection
            {
                Name = "Song",
                StartMs = 0,
                EndMs = Math.Max(songEndMs, 1),
                SoundCount = matcher.Expected.Count,
                StartBar = wholeSong.Start,
                EndBar = wholeSong.End
            });
        }
        else
        {
            for (var i = 0; i < markers.Count; i++)
            {
                var start = markers[i].Ms;
                var end = i + 1 < markers.Count ? markers[i + 1].Ms : Math.Max(songEndMs, start + 1);
                var count = 0;
                foreach (var sound in matcher.Expected)
                {
                    if (sound.TimeMs >= start && sound.TimeMs < end)
                        count++;
                }

                var bars = LiveBarNumbers.RangeOf(beats, start, end);
                sections.Add(new LivePlaySection
                {
                    Name = markers[i].Name,
                    StartMs = start,
                    EndMs = end,
                    SoundCount = count,
                    StartBar = bars.Start,
                    EndBar = bars.End
                });
            }
        }

        _sections = sections;
        BuildPhrases(arrangement, songEndMs);
        SectionsVersion++;
    }

    /// <summary>Phrase spans from the arrangement's event points, mirroring
    /// <see cref="BuildSections"/>; empty when the chart has no phrases.</summary>
    private void BuildPhrases(Arrangement arrangement, double songEndMs)
    {
        var matcher = Matcher!;
        var beats = _beats;
        var markers = new List<(double Ms, string Name)>();
        foreach (var ep in arrangement.EventPoints)
        {
            if (string.IsNullOrEmpty(ep.Phrase)) continue;
            markers.Add((ep.Position.GetPositionMs(beats), ep.Phrase));
        }

        markers.Sort(static (a, b) => a.Ms.CompareTo(b.Ms));
        var phrases = new List<LivePlayPhrase>(markers.Count);
        for (var i = 0; i < markers.Count; i++)
        {
            var start = markers[i].Ms;
            var end = i + 1 < markers.Count ? markers[i + 1].Ms : Math.Max(songEndMs, start + 1);
            var count = 0;
            foreach (var sound in matcher.Expected)
            {
                if (sound.TimeMs >= start && sound.TimeMs < end)
                    count++;
            }

            phrases.Add(new LivePlayPhrase
            {
                Name = markers[i].Name,
                StartMs = start,
                EndMs = end,
                SoundCount = count
            });
        }

        _phrases = phrases;
    }

    /// <summary>After a difficulty reload the per-section densities change;
    /// rebuild counts and tallies without moving the section boundaries.</summary>
    private void RebuildSectionSoundCounts()
    {
        var matcher = Matcher!;
        var rebuilt = new List<LivePlaySection>(_sections.Count);
        foreach (var s in _sections)
        {
            var count = 0;
            foreach (var sound in matcher.Expected)
            {
                if (sound.TimeMs >= s.StartMs && sound.TimeMs < s.EndMs)
                    count++;
            }

            rebuilt.Add(new LivePlaySection
            {
                Name = s.Name, StartMs = s.StartMs, EndMs = s.EndMs, SoundCount = count,
                // StartBar/EndBar are init-only and are captured ONCE in
                // BuildSections, because the practice store persists no beat map
                // and a recorded run can never be re-barred afterwards. Omitting
                // them here silently nulled them: nothing about a difficulty
                // reload moves where a section starts.
                StartBar = s.StartBar, EndBar = s.EndBar
            });
        }

        _sections = rebuilt;
        RecomputeSectionTallies();
    }

    /// <summary>Rebuilds per-section tallies from the matcher's verdicts —
    /// used after a seek clears part of the judgement history.</summary>
    private void RecomputeSectionTallies()
    {
        var matcher = Matcher;
        if (matcher == null) return;
        foreach (var s in _sections)
        {
            s.Hits = 0;
            s.Misses = 0;
        }

        foreach (var sound in matcher.Expected)
        {
            if (sound.Verdict is not { } v) continue;
            var section = SectionAt(sound.TimeMs);
            if (section == null) continue;
            if (v == LivePlayVerdict.Miss) section.Misses++;
            else section.Hits++;
        }

        SectionsVersion++;
    }
}
