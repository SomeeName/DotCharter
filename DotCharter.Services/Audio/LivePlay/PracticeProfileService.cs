using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Library;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>One finished practice session in an arrangement's history.</summary>
public sealed class PracticeSessionRecord
{
    public DateTime EndedUtc { get; set; }
    public long Score { get; set; }
    /// <summary>Accuracy over judged sounds, 0..1.</summary>
    public double Accuracy { get; set; }
    public int BestStreak { get; set; }
    public int Hits { get; set; }
    public int Misses { get; set; }
    public int TotalSounds { get; set; }
    /// <summary>Playback speed the session ended at (100 = real time).</summary>
    public double SpeedPercent { get; set; } = 100;
    public double DurationSeconds { get; set; }

    /// <summary>File name of the recorded input take (relative to the
    /// profile's <c>takes/</c> data directory), attached after the take is
    /// finalized. Null when take recording was off or the take was empty;
    /// consumers must tolerate a name whose file was cleaned up since.</summary>
    public string? TakeFile { get; set; }

    // ---- v0.27 take library. All nullable — records written before these
    // fields existed deserialize as null (schema-tolerant), and unset values
    // are omitted so the store stays lean. The take-library UI that WROTE
    // them (star / rename / re-amp, on the History screen) was removed
    // (it lives in git history); nothing writes them now,
    // but they are KEPT so an upgraded store round-trips them and the sweep
    // still honours an existing star and existing re-amp renders — removing
    // them would let the next cleanup delete takes the player chose to keep. ----

    /// <summary>Star flag of the recorded take: a starred take (and its
    /// re-amp renders) is exempt from the size-cap sweep — the player's
    /// keepers outrank the cache budget. Null = unstarred.</summary>
    public bool? TakeStarred { get; set; }

    /// <summary>Player-given take name (display only). Null = unnamed.</summary>
    public string? TakeLabel { get; set; }

    /// <summary>Offline re-amp renders of this take (file names relative to
    /// the takes directory, one per preset). Referenced files for the sweep, so
    /// an existing render stays on disk while its take does.</summary>
    public List<string>? ReampFiles { get; set; }

    /// <summary>File name of the run's replay (v0.32; relative to the
    /// profile's <c>replays/</c> data directory) — the judged event stream
    /// behind video export and ghosts. Null on pre-v0.32 records
    /// (schema-tolerant); consumers must tolerate a missing file.</summary>
    public string? ReplayFile { get; set; }

    /// <summary>Run ended by the rock meter's fail-out (0.24 performance
    /// stakes). Schema-tolerant like the other additive fields: only an
    /// explicit <c>true</c> claims a fail-out — records written before this
    /// field existed (and ordinary completed/aborted runs) read null.</summary>
    public bool? Failed { get; set; }

    // ---- 0.20 scoring-depth metadata. All nullable: records written before
    // these fields existed deserialize as null ("unknown"), never as a false
    // claim about an old run. ----

    /// <summary>Whole-chart full combo (no miss, everything judged).</summary>
    public bool? FullCombo { get; set; }

    /// <summary>Timing-tier breakdown (0.16 timing-graded judgment). May not
    /// sum to <see cref="Hits"/> — untimed CQT hits (no attack onset) carry no tier.</summary>
    public int? PerfectCount { get; set; }
    public int? GreatCount { get; set; }
    public int? GoodCount { get; set; }

    /// <summary>The judging path that produced this run — a
    /// <see cref="LiveDetectorModes.StoreKey" /> ("Classic" / "Cqt"). Null on every record
    /// written by an older build: the detector was not recorded, and null means unknown, never
    /// either mode.</summary>
    public string? Detector { get; set; }

    /// <summary>Per-section tallies of this session (0.21 weak-spot heatmap).
    /// Only sections that were actually PLAYED are stored; null on records
    /// written before this field existed (schema-tolerant).</summary>
    public List<SectionPlayRecord>? Sections { get; set; }

    /// <summary>Per-technique tallies of this session (0.21 technique report
    /// card). Only techniques with at least one judged sound are stored;
    /// null on records written before this field existed (schema-tolerant).</summary>
    public List<TechniquePlayRecord>? Techniques { get; set; }
}

/// <summary>One technique's tally inside a
/// <see cref="PracticeSessionRecord"/> — the persisted form of
/// <see cref="TechniqueResult"/>. <see cref="Technique"/> is the
/// <see cref="PlayedTechnique"/> name as a string so a newer store with an
/// unknown technique still loads (readers skip what they can't parse).</summary>
public sealed class TechniquePlayRecord
{
    public string Technique { get; set; } = "";
    public int Hits { get; set; }

    /// <summary>Note played, technique not (the HitWrongTechnique verdict) —
    /// the report card's core signal.</summary>
    public int WrongTechnique { get; set; }

    public int Misses { get; set; }
}

/// <summary>One played section's tally inside a
/// <see cref="PracticeSessionRecord"/> — the persisted form of
/// <c>SectionResult</c>, kept flat and small for the JSON store.</summary>
public sealed class SectionPlayRecord
{
    public string Name { get; set; } = "";
    public double StartMs { get; set; }
    public double EndMs { get; set; }
    public int Hits { get; set; }
    public int Misses { get; set; }

    /// <summary>1-based chart bar this section starts on — captured at RECORD
    /// time (<see cref="LiveBarNumbers" />), because the store persists no beat
    /// map and a run can therefore never be re-barred afterwards.
    /// <para>
    /// NULLABLE and ABSENT from every store written by an older build, plus from any
    /// chart with no measure markers. Null means "not recorded" and must never
    /// be read as bar 0: consumers fall back to the time range. That is the
    /// whole back-compatibility contract for this field — an older store loads
    /// unchanged and simply has no bars.
    /// </para></summary>
    public int? StartBar { get; set; }

    /// <summary>1-based chart bar this section ends on, INCLUSIVE. Same
    /// nullable contract as <see cref="StartBar" />.</summary>
    public int? EndBar { get; set; }
}

/// <summary>
///     Persisted mastery of one PHRASE of an arrangement (0.21 per-phrase
///     mastery): the dynamic-difficulty ladder level the player last ended
///     on plus an exponentially decayed accuracy average, so a new session
///     resumes the ladder instead of re-grinding from the initial level.
///     LADDER STATE, not a best — the level persists downward too (that IS
///     the DD semantic); the best-score invariant covers bests only.
/// </summary>
public sealed class PhraseMasteryRecord
{
    /// <summary>Difficulty level the ladder ended on last session.</summary>
    public int Level { get; set; }

    /// <summary>Exponential moving average of phrase accuracy (0..1), weight
    /// <see cref="PracticeProfileService.PhraseAccuracyDecayAlpha"/> on the
    /// newest session — recent form counts more than ancient history.</summary>
    public double DecayedAccuracy { get; set; }

    /// <summary>Sessions that judged at least one sound of this phrase.</summary>
    public int TimesPlayed { get; set; }

    /// <summary>How many levels the phrase's ladder had when <see cref="Level" /> was
    /// recorded, so levels compare across songs. Zero on records written before
    /// it existed: those count for nothing in <see cref="PlayerLadderDepth" /> until replayed.</summary>
    public int LadderSize { get; set; }

    public DateTime LastPlayedUtc { get; set; }
}

/// <summary>One detector's bests for a song + arrangement
/// (<see cref="PracticeArrangementProfile.DetectorBests" />).</summary>
public sealed class PracticeDetectorBest
{
    public long BestScore { get; set; }

    /// <summary>Best coverage-qualified accuracy, 0..1 (see
    /// <see cref="PracticeProfileService.MinCoverageForBestAccuracy" />).</summary>
    public double BestAccuracy { get; set; }

    public int BestStreak { get; set; }

    /// <summary>When this detector last recorded a run here.</summary>
    public DateTime LastPlayedUtc { get; set; }
}

/// <summary>Local practice progress for one song + arrangement.</summary>
public sealed class PracticeArrangementProfile
{
    /// <summary>Human-readable song label ("Artist - Title") captured when a
    /// session is recorded — store keys are project paths / psarc ids and
    /// unfit for display. Empty on pre-0.12 data (UI falls back to the key).</summary>
    public string DisplayName { get; set; } = "";

    public long BestScore { get; set; }
    /// <summary>Best session accuracy, 0..1.</summary>
    public double BestAccuracy { get; set; }
    public int BestStreak { get; set; }

    /// <summary>Sticky "ever full-comboed this arrangement" — the song-select
    /// FC badge. Set when any run FCs; never unset.</summary>
    public bool FullComboAchieved { get; set; }

    // ---- metadata of the BEST-SCORING run ("best" ranks by score alone —
    // the invariant). Nullable: profiles whose best predates these fields
    // load null ("unknown") and stay null until a run raises the best. A
    // worse run never re-stamps them; an equal-score FC may upgrade the FC
    // flag (never downgrade). ----

    /// <summary>Whether the best-scoring run was a full combo.</summary>
    public bool? BestRunFullCombo { get; set; }

    /// <summary>Timing-tier breakdown of the best-scoring run.</summary>
    public int? BestRunPerfects { get; set; }
    public int? BestRunGreats { get; set; }
    public int? BestRunGoods { get; set; }

    /// <summary>
    ///     Mastery 0..100: the best "how much of the song did you really own"
    ///     any single session showed — accuracy × coverage (judged share of
    ///     the chart) × speed factor, so a clean half-song at 60% speed can't
    ///     claim more than a clean full run at 100%.
    /// </summary>
    public double MasteryPercent { get; set; }

    /// <summary>Computed 1–10 difficulty of the chart
    /// (<c>ChartDifficultyEstimator</c> over the hardest level), stamped from
    /// the loaded chart each time a session is recorded — the latest parse
    /// wins, so a re-charted file re-rates. Null on profiles recorded before
    /// this field existed (schema-tolerant): song select shows those unrated
    /// until the song is played again.</summary>
    public double? ChartDifficulty { get; set; }

    public List<PracticeSessionRecord> Sessions { get; set; } = [];

    /// <summary>
    /// The bests again, PER DETECTOR, keyed by <see cref="LiveDetectorModes.StoreKey" />
    ///     — what the local leaderboard ranks, so runs of the two detectors never meet on one
    ///     board. Kept by the same rules as the bests above (a worse run never lowers one;
    ///     accuracy is coverage-gated). Null on every profile last recorded by an older build: a best
    ///     whose detector is unknown is ranked on no detector's board, and still counts
    ///     everywhere else (<see cref="BestScore" /> and its siblings are untouched).
    /// </summary>
    public Dictionary<string, PracticeDetectorBest>? DetectorBests { get; set; }

    /// <summary>Per-phrase dynamic-difficulty mastery, keyed by phrase name
    /// (0.21). Schema-tolerant: stores written before this field existed
    /// deserialize to an empty map — every phrase simply starts fresh.</summary>
    public Dictionary<string, PhraseMasteryRecord> PhraseMastery { get; set; } = [];
}

public interface IPracticeProfileService
{
    /// <summary>Progress for one song + arrangement; empty profile when unplayed.</summary>
    PracticeArrangementProfile GetProfile(string songKey, string arrangementName);

    /// <summary>Records a finished session and returns the updated profile.
    /// Sessions with nothing judged are ignored (no phantom history).
    /// <paramref name="displayName"/> (when non-blank) refreshes the stored
    /// human-readable song label. <paramref name="phraseOutcomes"/> (the
    /// session's <see cref="PhraseMasteryOutcome"/>s, when dynamic difficulty
    /// ran) folds into the persisted per-phrase mastery: ladder level is
    /// overwritten (up OR down — ladder state, not a best) and the accuracy
    /// joins a decayed average. <paramref name="sections"/> (the run's
    /// per-section tallies) persists the PLAYED sections on the session
    /// record for the cross-session weak-spot heatmap.
    /// <paramref name="techniques"/> (the run's per-technique tallies, see
    /// <see cref="LiveTechniqueTally"/>) persists the non-zero techniques on
    /// the session record for the technique report card.
    /// <paramref name="chartDifficulty"/> (the loaded chart's computed 1–10
    /// rating) re-stamps <see cref="PracticeArrangementProfile.ChartDifficulty"/>
    /// when non-null; null leaves the stored rating untouched.
    /// <paramref name="failed"/> marks the session record as a rock-meter
    /// fail-out; bests/mastery treat the run exactly like any other partial
    /// run (the best-score invariant is untouched).</summary>
    PracticeArrangementProfile Record(string songKey, string arrangementName, LivePlaySessionResult result,
        string? displayName = null, IReadOnlyList<PhraseMasteryOutcome>? phraseOutcomes = null,
        IReadOnlyList<SectionResult>? sections = null,
        IReadOnlyList<TechniqueResult>? techniques = null,
        double? chartDifficulty = null,
        bool failed = false);

    /// <summary>Snapshot of the whole store, keyed "songKey|arrangementName"
    /// (see <see cref="PracticeProfileService.TryParseKey"/>). Feeds the
    /// per-profile statistics/leaderboard aggregations.</summary>
    IReadOnlyDictionary<string, PracticeArrangementProfile> GetAll();

    /// <summary>Attaches a finalized take file to the MOST RECENT session of
    /// the arrangement (takes finalize asynchronously after
    /// <see cref="Record"/>). False when no session exists or the newest one
    /// already carries a take. Returns without touching bests/mastery.</summary>
    bool AttachTakeFile(string songKey, string arrangementName, string takeFileName);

    /// <summary>v0.32: attaches a saved run replay to the MOST RECENT session
    /// (same lifecycle as <see cref="AttachTakeFile"/> — replays persist
    /// off-thread right after <see cref="Record"/>). False when no session
    /// exists or the newest one already carries a replay.</summary>
    bool AttachReplayFile(string songKey, string arrangementName, string replayFileName);

    /// <summary>
    ///     Drops the in-memory store so the next read re-loads it from disk.
    ///     Required after a backup import replaces the file behind the service.
    /// </summary>
    void InvalidateCache();

    /// <summary>
    ///     False when the last load could not READ the store file (an
    ///     <see cref="IOException" />, a sharing violation, an antivirus or cloud-sync
    ///     hold) — the file is presumed intact, and <see cref="GetAll" /> is therefore
    ///     serving an EMPTY snapshot that describes nothing.
    ///     <para>
    ///         <b>Anything that DELETES on the strength of what this store does not
    ///         mention must check this first.</b> The take sweep
    ///         (<c>LiveTakeStore.CleanupAsync</c>) reads every session's
    ///         <see cref="PracticeSessionRecord.TakeFile" /> to decide which files on
    ///         disk are still referenced; against an empty snapshot every take —
    ///         starred ones included, since the star is only consulted by the
    ///         cap-eviction pass — reads as orphaned and is deleted, on a sweep that
    ///         runs after every run and on every take delete.
    ///     </para>
    ///     <para>
    ///         Reading it performs the load, so it answers about the store the very
    ///         next call will see. A degraded read is never cached, so a transient
    ///         lock heals itself and this flips back to true on its own.
    ///     </para>
    /// </summary>
    bool IsStoreAuthoritative { get; }
}

/// <summary>
///     JSON-file-backed practice progress store (<c>practice-profiles.json</c>).
///     Small, append-mostly data — a whole-file rewrite per session end is fine.
///     When constructed with an <see cref="IPlayerProfileService"/>, the store
///     lives in the ACTIVE player profile's data directory and follows profile
///     switches; the pre-profile 0.10 store in the config root is migrated
///     into the default profile the first time it is read.
/// </summary>
public sealed class PracticeProfileService : IPracticeProfileService
{
    private const int SessionHistoryCap = 200;
    public const string StoreFileName = "practice-profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private readonly object _gate = new();
    private readonly string? _fixedFilePath;
    private readonly string _legacyFilePath;
    private readonly IPlayerProfileService? _playerProfiles;
    private readonly IAppLogger? _logger;
    private Dictionary<string, PracticeArrangementProfile>? _profiles;
    private string? _loadedPath;

    /// <summary>
    ///     True when the last load attempt could not READ an existing store —
    ///     as opposed to finding it absent (a new player) or genuinely corrupt
    ///     (quarantined below). While it is set the in-memory store is NOT the
    ///     player's history, so <see cref="SaveLocked" /> refuses to write.
    /// </summary>
    private bool _storeUnreadable;

    /// <summary>Fixed-path store without player-profile scoping (tests, tools).</summary>
    public PracticeProfileService(IAppLogger? logger = null, string? filePath = null)
    {
        _logger = logger;
        _fixedFilePath = filePath ?? Path.Combine(WritablePaths.ConfigRoot, StoreFileName);
        _legacyFilePath = _fixedFilePath;
    }

    /// <summary>Player-profile-scoped store that follows the active profile.</summary>
    public PracticeProfileService(IPlayerProfileService playerProfiles, IAppLogger? logger = null,
        string? legacyFilePath = null)
    {
        _logger = logger;
        _playerProfiles = playerProfiles;
        _legacyFilePath = legacyFilePath ?? Path.Combine(WritablePaths.ConfigRoot, StoreFileName);
        playerProfiles.ActiveProfileChanged += _ =>
        {
            lock (_gate)
            {
                _profiles = null;
                _loadedPath = null;
            }
        };
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _profiles = null;
            _loadedPath = null;
            _storeUnreadable = false;
        }
    }

    /// <inheritdoc />
    public bool IsStoreAuthoritative
    {
        get
        {
            lock (_gate)
            {
                LoadLocked();
                return !_storeUnreadable;
            }
        }
    }

    public PracticeArrangementProfile GetProfile(string songKey, string arrangementName)
    {
        lock (_gate)
        {
            var profiles = LoadLocked();
            return profiles.TryGetValue(Key(songKey, arrangementName), out var profile)
                ? profile
                : new PracticeArrangementProfile();
        }
    }

    /// <summary>EMA weight of the NEWEST session in a phrase's
    /// <see cref="PhraseMasteryRecord.DecayedAccuracy"/>.</summary>
    public const double PhraseAccuracyDecayAlpha = 0.35;

    /// <summary>
    ///     Minimum <see cref="LivePlaySessionResult.Coverage" /> a run needs before
    ///     it may raise <see cref="PracticeArrangementProfile.BestAccuracy" />.
    ///     <para>
    ///         <c>BestAccuracy</c> was a coverage-BLIND maximum over
    ///         <c>Hits / (Hits + Misses)</c>, i.e. over the JUDGED sounds only — so
    ///         playing two notes cleanly and quitting stored <c>1.0</c> forever.
    ///         That value is not decorative: <c>GradeCalculator.FromAccuracy</c>
    ///         turns it into the song's grade badge in three places, it is the local
    ///         leaderboard's secondary ranking.
    ///         A 3-note run therefore earned a permanent S
    ///         and the top leaderboard row.
    ///     </para>
    ///     <para>
    ///         The gate only ever declines to RAISE the stored value, so the
    ///         best-score invariant ("a worse run NEVER lowers a stored best", see
    ///         docs/agents/services/live-play.md) is preserved exactly. A run below
    ///         the floor is still recorded in <c>Sessions</c> and still folds into
    ///         <c>MasteryPercent</c>, which is coverage-weighted by construction.
    ///     </para>
    ///     <para>
    ///         The value is a product decision, not a derivation: half the
    ///         arrangement is the point at which "how well you play this song" starts
    ///         to mean something. Section drilling and riff-repeater passes stay
    ///         below it on purpose — a grade for a song should come from playing the
    ///         song.
    ///     </para>
    /// </summary>
    public const double MinCoverageForBestAccuracy = 0.5;

    /// <summary>Whether <paramref name="result" /> is representative enough of the
    /// whole arrangement to set <see cref="PracticeArrangementProfile.BestAccuracy" />.
    /// The single home of the rule, so the results screen's "new best accuracy"
    /// banner cannot claim something the store did not record.</summary>
    public static bool QualifiesForBestAccuracy(LivePlaySessionResult result) => result.Coverage >= MinCoverageForBestAccuracy;

    public PracticeArrangementProfile Record(string songKey, string arrangementName, LivePlaySessionResult result,
        string? displayName = null, IReadOnlyList<PhraseMasteryOutcome>? phraseOutcomes = null,
        IReadOnlyList<SectionResult>? sections = null,
        IReadOnlyList<TechniqueResult>? techniques = null,
        double? chartDifficulty = null,
        bool failed = false)
    {
        lock (_gate)
        {
            var profiles = LoadLocked();
            var key = Key(songKey, arrangementName);
            if (!profiles.TryGetValue(key, out var profile))
                profiles[key] = profile = new PracticeArrangementProfile();

            var judged = result.Hits + result.Misses;
            if (judged <= 0) return profile;

            if (!string.IsNullOrWhiteSpace(displayName)) profile.DisplayName = displayName.Trim();
            // Latest parse wins — a re-charted file re-rates; rounded so the
            // JSON store doesn't churn on 15-digit double noise.
            if (chartDifficulty is { } difficulty)
                profile.ChartDifficulty = Math.Round(Math.Clamp(difficulty, 1.0, 10.0), 2);

            var endedUtc = DateTime.UtcNow;
            var detector = LiveDetectorModes.StoreKey(result.Detector);
            profile.Sessions.Add(new PracticeSessionRecord
            {
                EndedUtc = endedUtc,
                Detector = detector,
                Score = result.Score,
                Accuracy = result.Accuracy,
                BestStreak = result.BestStreak,
                Hits = result.Hits,
                Misses = result.Misses,
                TotalSounds = result.TotalSounds,
                SpeedPercent = result.SpeedPercent,
                DurationSeconds = result.DurationSeconds,
                FullCombo = result.IsFullCombo,
                PerfectCount = result.PerfectCount,
                GreatCount = result.GreatCount,
                GoodCount = result.GoodCount,
                Sections = PlayedSectionsOf(sections),
                Techniques = JudgedTechniquesOf(techniques),
                // Null (not false) when the run wasn't a fail-out, so the JSON
                // store doesn't grow a field on every ordinary run.
                Failed = failed ? true : null
            });
            // Keep the list ascending even when the clock steps backwards, so
            // the cap below trims the genuinely OLDEST record and every reader's
            // `Sessions[^1]` really is the newest run. See SortSessions.
            SortSessions(profile);
            while (profile.Sessions.Count > SessionHistoryCap) profile.Sessions.RemoveAt(0);

            // Best-run metadata follows the SAME ranking as the best itself:
            // score alone. Re-stamped on a strictly better score (or when no
            // metadata exists yet and the run at least matches the best, e.g.
            // the first recorded run); an equal-score FC upgrades the flag;
            // a worse run never touches it — the stored best stands intact.
            if (result.Score > profile.BestScore ||
                (profile.BestRunFullCombo is null && result.Score >= profile.BestScore))
            {
                profile.BestRunFullCombo = result.IsFullCombo;
                profile.BestRunPerfects = result.PerfectCount;
                profile.BestRunGreats = result.GreatCount;
                profile.BestRunGoods = result.GoodCount;
            }
            else if (result.Score == profile.BestScore && result.IsFullCombo)
            {
                profile.BestRunFullCombo = true;
            }

            if (result.IsFullCombo) profile.FullComboAchieved = true;

            profile.BestScore = Math.Max(profile.BestScore, result.Score);
            // Coverage-gated: see MinCoverageForBestAccuracy. Never LOWERS the
            // stored value — a short run simply does not get to raise it.
            if (QualifiesForBestAccuracy(result))
                profile.BestAccuracy = Math.Max(profile.BestAccuracy, result.Accuracy);
            profile.BestStreak = Math.Max(profile.BestStreak, result.BestStreak);

            // The same bests per detector; an unknown detector records none.
            if (detector != null)
            {
                profile.DetectorBests ??= [];
                if (!profile.DetectorBests.TryGetValue(detector, out var detectorBest))
                    profile.DetectorBests[detector] = detectorBest = new PracticeDetectorBest();
                detectorBest.BestScore = Math.Max(detectorBest.BestScore, result.Score);
                if (QualifiesForBestAccuracy(result))
                    detectorBest.BestAccuracy = Math.Max(detectorBest.BestAccuracy, result.Accuracy);
                detectorBest.BestStreak = Math.Max(detectorBest.BestStreak, result.BestStreak);
                detectorBest.LastPlayedUtc = endedUtc;
            }

            var coverage = result.Coverage;
            var speedFactor = Math.Clamp(result.SpeedPercent / 100.0, 0.0, 1.0);
            var mastery = result.Accuracy * coverage * speedFactor * 100.0;
            profile.MasteryPercent = Math.Max(profile.MasteryPercent, Math.Round(mastery, 1));

            if (phraseOutcomes != null)
                FoldPhraseMastery(profile, phraseOutcomes);

            SaveLocked(profiles);
            return profile;
        }
    }

    public IReadOnlyDictionary<string, PracticeArrangementProfile> GetAll()
    {
        lock (_gate)
        {
            return new Dictionary<string, PracticeArrangementProfile>(LoadLocked());
        }
    }

    public bool AttachTakeFile(string songKey, string arrangementName, string takeFileName)
    {
        if (string.IsNullOrWhiteSpace(takeFileName)) return false;
        lock (_gate)
        {
            var profiles = LoadLocked();
            if (!profiles.TryGetValue(Key(songKey, arrangementName), out var profile) ||
                profile.Sessions.Count == 0)
            {
                return false;
            }

            var latest = profile.Sessions[^1];
            if (!string.IsNullOrEmpty(latest.TakeFile)) return false;
            latest.TakeFile = takeFileName;
            SaveLocked(profiles);
            return true;
        }
    }

    public bool AttachReplayFile(string songKey, string arrangementName, string replayFileName)
    {
        if (string.IsNullOrWhiteSpace(replayFileName)) return false;
        lock (_gate)
        {
            var profiles = LoadLocked();
            if (!profiles.TryGetValue(Key(songKey, arrangementName), out var profile) ||
                profile.Sessions.Count == 0)
            {
                return false;
            }

            var latest = profile.Sessions[^1];
            if (!string.IsNullOrEmpty(latest.ReplayFile)) return false;
            latest.ReplayFile = replayFileName;
            SaveLocked(profiles);
            return true;
        }
    }

    /// <summary>Folds one session's per-phrase outcomes into the persisted
    /// mastery map. The ladder LEVEL is overwritten (persists downward too —
    /// resuming the ladder is the point); the accuracy joins an EMA with
    /// <see cref="PhraseAccuracyDecayAlpha"/> weight on the new session.
    /// Outcomes with nothing judged never touch the map.</summary>
    private static void FoldPhraseMastery(PracticeArrangementProfile profile,
        IReadOnlyList<PhraseMasteryOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            if (outcome.Judged <= 0 || string.IsNullOrEmpty(outcome.PhraseName)) continue;
            if (!profile.PhraseMastery.TryGetValue(outcome.PhraseName, out var record))
                profile.PhraseMastery[outcome.PhraseName] = record = new PhraseMasteryRecord();

            record.Level = Math.Max(0, outcome.Level);
            record.LadderSize = Math.Max(0, outcome.LadderSize);
            record.DecayedAccuracy = record.TimesPlayed == 0
                ? outcome.Accuracy
                : PhraseAccuracyDecayAlpha * outcome.Accuracy
                  + (1.0 - PhraseAccuracyDecayAlpha) * record.DecayedAccuracy;
            record.TimesPlayed++;
            record.LastPlayedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Persisted form of the run's PLAYED sections (unplayed ones
    /// carry no signal and would bloat the store). Null in = null out.</summary>
    private static List<SectionPlayRecord>? PlayedSectionsOf(IReadOnlyList<SectionResult>? sections)
    {
        if (sections == null) return null;
        List<SectionPlayRecord>? played = null;
        foreach (var s in sections)
        {
            if (!s.WasPlayed) continue;
            (played ??= []).Add(new SectionPlayRecord
            {
                Name = s.Name,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Hits = s.Hits,
                Misses = s.Misses,
                StartBar = s.StartBar,
                EndBar = s.EndBar
            });
        }

        return played;
    }

    /// <summary>Persisted form of the run's technique tallies. The compute
    /// (<see cref="LiveTechniqueTally.Compute"/>) already yields only judged
    /// techniques; the filter here just guards direct callers. Null/empty in
    /// = null out — an untouched store stays byte-identical.</summary>
    private static List<TechniquePlayRecord>? JudgedTechniquesOf(IReadOnlyList<TechniqueResult>? techniques)
    {
        if (techniques == null) return null;
        List<TechniquePlayRecord>? judged = null;
        foreach (var t in techniques)
        {
            if (t.Judged <= 0) continue;
            (judged ??= []).Add(new TechniquePlayRecord
            {
                Technique = t.Technique.ToString(),
                Hits = t.Hits,
                WrongTechnique = t.WrongTechnique,
                Misses = t.Misses
            });
        }

        return judged;
    }

    /// <summary>The store key. A library song is filed under its <c>lib:</c> identity
    /// even when a caller still hands in the <c>psarc:</c> one.</summary>
    private static string Key(string songKey, string arrangementName)
        => $"{LibrarySongIdentity.Normalize(songKey)}|{arrangementName}";

    /// <summary>Splits a store key back into song key + arrangement name.
    /// Song keys may themselves contain '|' (legacy paths), so the LAST
    /// separator wins — arrangement names never contain one.</summary>
    public static bool TryParseKey(string key, out string songKey, out string arrangementName)
    {
        var idx = key.LastIndexOf('|');
        if (idx <= 0 || idx >= key.Length - 1)
        {
            songKey = key;
            arrangementName = string.Empty;
            return false;
        }

        songKey = key[..idx];
        arrangementName = key[(idx + 1)..];
        return true;
    }

    /// <summary>Reads a practice store file directly (read-only, no caching).
    /// Used by cross-profile aggregations (leaderboards) that must not
    /// disturb the active profile's store. Unreadable/missing → empty.</summary>
    public static Dictionary<string, PracticeArrangementProfile> ReadStoreFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                // Another profile's store, written by the same AtomicFile path
                // as ours — so it is read the same way, or this cross-profile
                // aggregation denies delete and blocks THAT profile's save.
                var store = JsonSerializer.Deserialize<Dictionary<string, PracticeArrangementProfile>>(
                                AtomicFile.ReadAllText(filePath), JsonOptions)
                            ?? [];
                LibrarySongIdentity.MigrateKeys(store);
                return SortSessions(store);
            }
        }
        catch
        {
            // Cross-profile reads are best effort; a corrupt store just
            // drops that profile from the aggregation.
        }

        return [];
    }

    /// <summary>
    ///     Loads (and caches) the store. Three outcomes, and telling them apart
    ///     is the whole point: ABSENT is a new player, CORRUPT is unrecoverable
    ///     content, and UNREADABLE is a file that is presumed intact but could
    ///     not be opened right now.
    ///     <para>
    ///         Treating all three the same — empty dictionary,
    ///         <c>_loadedPath</c> set, a WARNING in the log — would make the
    ///         empty state authoritative. A store momentarily locked by an
    ///         antivirus scan, a cloud sync or a backup tool would therefore end the
    ///         next run with <see cref="SaveLocked" /> atomically replacing
    ///         every best score, accuracy, streak, FC badge, phrase-mastery
    ///         ladder and session record the player had with a single entry.
    ///     </para>
    /// </summary>
    private Dictionary<string, PracticeArrangementProfile> LoadLocked()
    {
        var path = ResolveStorePathLocked();
        if (_profiles != null && path == _loadedPath) return _profiles;

        _profiles = null;
        _storeUnreadable = false;
        try
        {
            if (File.Exists(path))
            {
                // AtomicFile.ReadAllText shares read/write/DELETE, so an
                // in-flight atomic replace of this very file is not an error.
                _profiles = JsonSerializer.Deserialize<Dictionary<string, PracticeArrangementProfile>>(
                    AtomicFile.ReadAllText(path), JsonOptions);
            }
        }
        catch (JsonException ex)
        {
            // CORRUPT: the bytes are readable and are not a store. Starting
            // fresh is the only way forward, but this file is still the only
            // copy of the player's history, so it gets moved aside rather than
            // overwritten — and if it cannot be moved aside, we do not write.
            _storeUnreadable = !TryQuarantineCorruptStore(path, ex);
        }
        catch (Exception ex)
        {
            // COULD NOT READ RIGHT NOW (IOException, UnauthorizedAccessException,
            // …). The file is presumed intact; the empty cache must never become
            // authoritative.
            _storeUnreadable = true;
            _logger?.Warning(AppLogCategory.Audio,
                $"Practice profile store could not be read ({ex.GetType().Name}: {ex.Message}); " +
                "running without it this session and NOT writing over it.");
        }

        if (_storeUnreadable)
        {
            // Deliberately NOT cached: leaving _loadedPath null makes the next
            // call retry the read, so a transient lock heals itself.
            _profiles = null;
            _loadedPath = null;
            return [];
        }

        _loadedPath = path;
        _profiles ??= [];

        // library songs were filed under "psarc:<key>". Re-keyed in memory only —
        // the old entries leave the file with the next atomic write, which is the same
        // write that puts the new ones in, so a crash can never lose either. A "lib:"
        // entry already present wins over its "psarc:" twin.
        var migrated = LibrarySongIdentity.MigrateKeys(_profiles);
        if (migrated > 0)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"Practice profile store: {migrated} library song entr{(migrated == 1 ? "y" : "ies")} re-keyed from 'psarc:' to 'lib:'.");
        }

        return _profiles = SortSessions(_profiles);
    }

    /// <summary>
    ///     Puts every arrangement's <see cref="PracticeArrangementProfile.Sessions" />
    ///     into ascending <c>EndedUtc</c> order.
    ///     <para>
    ///         <b>Readers treat that order as a fact without enforcing it.</b>
    ///         <c>Sessions[^1]</c> is "last played" in
    ///         <c>GameplayStatsService</c> (three methods), and
    ///         <see cref="Record" />'s history cap trims with
    ///         <c>Sessions.RemoveAt(0)</c>, i.e. it deletes whatever happens to be
    ///         FIRST. (The history, trends and weak-spot read-models that also
    ///         leaned on it are gone.)
    ///     </para>
    ///     <para>
    ///         Appending <c>DateTime.UtcNow</c> keeps it ordered only while the
    ///         clock moves forward. An NTP correction backwards mid-session, a
    ///         manual clock fix, a store restored from a machine whose clock ran
    ///         ahead, or a hand-merged file all leave an older stamp after a newer
    ///         one — and then "last played" is a date from months ago, the cap
    ///         deletes the NEWEST run instead of the oldest, and the bar-range rule
    ///         reads the wrong run. Sorting once at LOAD fixes every reader at the
    ///         same time, and covers stores already on disk; <see cref="Record" />
    ///         keeps the invariant on append.
    ///     </para>
    /// </summary>
    private static Dictionary<string, PracticeArrangementProfile> SortSessions(
        Dictionary<string, PracticeArrangementProfile> profiles)
    {
        foreach (var profile in profiles.Values) SortSessions(profile);
        return profiles;
    }

    /// <summary>Ascending by <c>EndedUtc</c>; a no-op (one comparison per
    /// record) on the ordered store every write produces.</summary>
    private static void SortSessions(PracticeArrangementProfile profile)
    {
        var sessions = profile.Sessions;
        for (var i = 1; i < sessions.Count; i++)
        {
            if (sessions[i].EndedUtc < sessions[i - 1].EndedUtc)
            {
                sessions.Sort(static (a, b) => a.EndedUtc.CompareTo(b.EndedUtc));
                return;
            }
        }
    }

    /// <summary>
    ///     Moves a store whose CONTENT is not a store aside as
    ///     <c>practice-profiles.corrupt-&lt;stamp&gt;.json</c>, so the player still
    ///     has the bytes and they can be inspected. Returns false when the move
    ///     failed — in which case the original is still in place and must not be
    ///     written over.
    /// </summary>
    private bool TryQuarantineCorruptStore(string path, Exception cause)
    {
        var quarantine = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}" +
            Path.GetExtension(path));

        try
        {
            File.Move(path, quarantine, true);
            _logger?.Warning(AppLogCategory.Audio,
                $"Practice profile store was unparseable ({cause.Message}); moved to " +
                $"'{Path.GetFileName(quarantine)}' and starting fresh.");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio,
                $"Practice profile store is unparseable ({cause.Message}) and could not be quarantined; " +
                "refusing to write over it.", ex);
            return false;
        }
    }

    /// <summary>
    ///     Store path for the current mode. In player-profile mode this also
    ///     performs the one-time 0.10 migration: the pre-profile store in the
    ///     config root is MOVED into the default profile's data directory the
    ///     first time that profile's store is resolved.
    /// </summary>
    private string ResolveStorePathLocked()
    {
        if (_playerProfiles == null) return _fixedFilePath!;

        var active = _playerProfiles.ActiveProfile;
        var path = Path.Combine(_playerProfiles.GetDataDirectory(active.Id), StoreFileName);

        if (active.IsDefault && !File.Exists(path) && File.Exists(_legacyFilePath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Move(_legacyFilePath, path);
                _logger?.Info(AppLogCategory.Audio,
                    $"Migrated pre-profile practice scores into default profile '{active.Name}'.");
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Practice score migration failed, keeping legacy store readable: {ex.Message}");
                return _legacyFilePath;
            }
        }

        return path;
    }

    private void SaveLocked(Dictionary<string, PracticeArrangementProfile> profiles)
    {
        if (_storeUnreadable)
        {
            // Never write over a store we failed to READ: what is in memory is
            // not the player's history, so this write would replace every best,
            // streak and mastery record with just this run.
            _logger?.Warning(AppLogCategory.Audio,
                "Practice profile store was not readable this session; skipping the write so the " +
                "existing history survives. This run was not persisted.");
            return;
        }

        var path = _loadedPath ?? ResolveStorePathLocked();
        try
        {
            // Atomic: this file IS the player's entire history — every score, best,
            // streak and phrase-mastery record. A bare WriteAllText truncates first,
            // so a crash mid-write loses all of it.
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(profiles, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Practice profile store not writable: {ex.Message}");
        }
    }
}
