using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Library;

namespace DotCharter.Services.Gameplay.Stats;

/// <summary>Per-song aggregate inside <see cref="ProfileStatistics"/>.
/// <paramref name="FullCombo"/> = the sticky "ever full-comboed" flag
/// (<see cref="PracticeArrangementProfile.FullComboAchieved"/>) — the
/// song-select FC badge chip. <paramref name="Difficulty"/> = the computed
/// 1–10 chart rating (<c>ChartDifficultyEstimator</c>, stamped when a
/// session records; null = never rated) — the song-select sort/filter
/// value. <paramref name="Grade"/> = the letter for
/// <paramref name="BestAccuracy"/> — meaningful ONLY when
/// <see cref="SongPlayStats.HasBestAccuracy"/>; a store with no qualifying run
/// yet grades 0 as F, so readouts bind
/// <see cref="SongPlayStats.GradeLabel"/>.</summary>
public sealed record SongPlayStats(
    string SongKey,
    string ArrangementName,
    int SessionCount,
    long BestScore,
    double BestAccuracy,
    double MasteryPercent,
    LetterGrade Grade,
    DateTime LastPlayedUtc,
    string DisplayName,
    bool FullCombo = false,
    double? Difficulty = null)
{
    /// <summary>
    ///     The arrangement as a person names it. <see cref="ArrangementName" /> is the store's
    ///     key, <c>Type:Name</c> ("Lead:Lead"), which is identity, not a label.
    /// </summary>
    public string ArrangementLabel
    {
        get
        {
            var colon = ArrangementName.IndexOf(':');
            return colon > 0 && Enum.TryParse<ArrangementType>(ArrangementName[..colon], out _)
                ? ArrangementName[(colon + 1)..]
                : ArrangementName;
        }
    }

    /// <summary>
    ///     A qualifying run has actually MEASURED a best accuracy for this
    ///     song+arrangement, so <see cref="BestAccuracy" /> and
    ///     <see cref="Grade" /> mean something. False is the ordinary state of a
    ///     player whose runs are all section drills or mid-song quits:
    ///     <c>PracticeProfileService.Record</c> declines to raise the stored best
    ///     below <c>PracticeProfileService.MinCoverageForBestAccuracy</c>, so
    ///     sessions exist while the figure sits at 0 — which
    ///     <c>GradeCalculator.FromAccuracy</c> maps to a confident <b>F</b>.
    ///     Every readout gates on this; <see cref="GradeLabel" /> already does.
    /// </summary>
    public bool HasBestAccuracy => AccuracyMath.IsMeasuredBest(BestAccuracy);

    /// <summary>The grade to PRINT: the letter once one has been earned, the
    /// unmeasured em dash until then. Bind this, never <see cref="Grade" />.
    /// </summary>
    public string GradeLabel =>
        HasBestAccuracy ? GradeCalculator.Label(Grade) : AccuracyMath.UnmeasuredLabel;

    /// <summary>Stored label, or a de-uglified key for pre-0.12 records.</summary>
    public static string LabelFor(string songKey, string stored)
    {
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        var key = LibrarySongIdentity.TryGetSongKey(songKey, out var libraryKey) ? libraryKey : songKey;
        return key.Contains('\\') || key.Contains('/') ? Path.GetFileNameWithoutExtension(key) : key;
    }
}

/// <summary>Everything the profile stats page shows, computed in one pass.</summary>
public sealed class ProfileStatistics
{
    public int SongsPlayed { get; init; }
    public int TotalSessions { get; init; }
    public double TotalPlayTimeSeconds { get; init; }
    public long TotalNotesHit { get; init; }
    public int HighestStreak { get; init; }
    public double BestAccuracy { get; init; }

    /// <summary>At least one arrangement has a measured best accuracy, so
    /// <see cref="BestAccuracy" /> is a real figure rather than the 0 default
    /// every coverage-gated store keeps.</summary>
    public bool HasBestAccuracy => AccuracyMath.IsMeasuredBest(BestAccuracy);

    /// <summary>Session accuracies (0..1), chronological, capped to the most recent entries.</summary>
    public IReadOnlyList<double> AccuracyHistory { get; init; } = [];

    /// <summary>Grade → number of song+arrangement entries whose BEST accuracy earns it.</summary>
    public IReadOnlyDictionary<LetterGrade, int> GradeDistribution { get; init; } =
        new Dictionary<LetterGrade, int>();

    public IReadOnlyList<SongPlayStats> MostPlayed { get; init; } = [];
    public IReadOnlyList<SongPlayStats> RecentlyPlayed { get; init; } = [];
    public double AverageMasteryPercent { get; init; }
}

/// <summary>The bare figures of ONE stored run — what a caller that wants the
/// newest session and nothing else needs: one record, never a projection of
/// the run's per-section breakdown.</summary>
public sealed record LastSessionFacts(DateTime EndedUtc, long Score, double Accuracy,
    double DurationSeconds);

public interface IGameplayStatsService
{
    /// <summary>Statistics for the ACTIVE profile (the practice store already follows it).</summary>
    ProfileStatistics GetStatistics();

    /// <summary>Best-known state for one song+arrangement in the active profile.</summary>
    SongPlayStats? GetSongStats(string songKey, string arrangementName);

    /// <summary>Best entry for a song across ALL its arrangements (song-select
    /// badge: personal best + grade), highest score wins. Null when unplayed.</summary>
    SongPlayStats? GetBestForSong(string songKey);

    /// <summary>The single most recently finished run across the whole active
    /// profile, or null when nothing has been recorded.
    /// <para>
    ///     It exists because the front door needs ONE run's figures on EVERY
    ///     entry to the Menu. The alternative, a session-history
    ///     read-model, would project every stored session and
    ///     its sections — measured 1.28 ms and 1.48 MB per call on a
    ///     200-arrangement × 6-run × 14-section store. This walk allocates ONE
    ///     record.
    /// </para></summary>
    LastSessionFacts? GetMostRecentSession();
}

/// <summary>
///     Read-model over the active profile's practice store. Nothing is
///     cached — the store is small and the stats page / song select ask
///     rarely, so recomputing keeps every number trivially consistent.
/// </summary>
public sealed class GameplayStatsService : IGameplayStatsService
{
    private const int AccuracyHistoryCap = 100;
    private const int TopListCap = 10;

    private readonly IPracticeProfileService _store;

    /// <param name="store">Practice profile store supplying the raw session/profile
    /// data the statistics are computed from.</param>
    public GameplayStatsService(IPracticeProfileService store) => _store = store;

    public ProfileStatistics GetStatistics()
    {
        var all = _store.GetAll();
        var perSong = new List<SongPlayStats>(all.Count);
        var sessions = new List<PracticeSessionRecord>();
        var gradeDist = new Dictionary<LetterGrade, int>();
        long notesHit = 0;
        var playTime = 0.0;
        var highestStreak = 0;
        var bestAccuracy = 0.0;

        foreach (var (key, profile) in all)
        {
            if (profile.Sessions.Count == 0) continue;
            PracticeProfileService.TryParseKey(key, out var songKey, out var arrangement);

            var grade = GradeCalculator.FromAccuracy(profile.BestAccuracy);
            // Only a MEASURED best earns a bar in the distribution. An
            // arrangement whose runs never cleared the coverage gate has no
            // grade at all, and counting it as F reports a player who drills
            // sections as failing songs they have never been graded on.
            if (AccuracyMath.IsMeasuredBest(profile.BestAccuracy))
                gradeDist[grade] = gradeDist.GetValueOrDefault(grade) + 1;
            perSong.Add(new SongPlayStats(songKey, arrangement, profile.Sessions.Count, profile.BestScore,
                profile.BestAccuracy, profile.MasteryPercent, grade, profile.Sessions[^1].EndedUtc,
                SongPlayStats.LabelFor(songKey, profile.DisplayName), profile.FullComboAchieved,
                profile.ChartDifficulty));

            foreach (var s in profile.Sessions)
            {
                notesHit += s.Hits;
                playTime += s.DurationSeconds;
                sessions.Add(s);
            }

            highestStreak = Math.Max(highestStreak, profile.BestStreak);
            bestAccuracy = Math.Max(bestAccuracy, profile.BestAccuracy);
        }

        sessions.Sort((a, b) => a.EndedUtc.CompareTo(b.EndedUtc));
        var history = sessions.Count > AccuracyHistoryCap
            ? sessions.Skip(sessions.Count - AccuracyHistoryCap)
            : sessions;

        return new ProfileStatistics
        {
            // SONGS, not store rows. A store key is `songKey|arrangementName`
            // (PracticeProfileService.Key), so perSong holds one row per
            // ARRANGEMENT — one song practised on Lead, Rhythm and Bass is
            // three of them, and the stats page prints this figure under
            // "Songs played". GradeDistribution is per-arrangement on purpose
            // and says so; this one never was.
            SongsPlayed = perSong.Select(p => p.SongKey).Distinct(StringComparer.Ordinal).Count(),
            TotalSessions = sessions.Count,
            TotalPlayTimeSeconds = playTime,
            TotalNotesHit = notesHit,
            HighestStreak = highestStreak,
            BestAccuracy = bestAccuracy,
            AccuracyHistory = history.Select(s => s.Accuracy).ToArray(),
            GradeDistribution = gradeDist,
            MostPlayed = perSong.OrderByDescending(p => p.SessionCount)
                .ThenByDescending(p => p.LastPlayedUtc).Take(TopListCap).ToArray(),
            RecentlyPlayed = perSong.OrderByDescending(p => p.LastPlayedUtc).Take(TopListCap).ToArray(),
            AverageMasteryPercent = perSong.Count == 0
                ? 0
                : Math.Round(perSong.Average(p => p.MasteryPercent), 1)
        };
    }

    public SongPlayStats? GetBestForSong(string songKey)
    {
        songKey = LibrarySongIdentity.Normalize(songKey);
        SongPlayStats? best = null;
        foreach (var (key, profile) in _store.GetAll())
        {
            if (profile.Sessions.Count == 0) continue;
            if (!PracticeProfileService.TryParseKey(key, out var sk, out var arrangement)) continue;
            if (!string.Equals(sk, songKey, StringComparison.Ordinal)) continue;
            if (best != null && profile.BestScore <= best.BestScore) continue;
            best = new SongPlayStats(sk, arrangement, profile.Sessions.Count, profile.BestScore,
                profile.BestAccuracy, profile.MasteryPercent,
                GradeCalculator.FromAccuracy(profile.BestAccuracy), profile.Sessions[^1].EndedUtc,
                SongPlayStats.LabelFor(sk, profile.DisplayName), profile.FullComboAchieved,
                profile.ChartDifficulty);
        }

        return best;
    }

    public LastSessionFacts? GetMostRecentSession()
    {
        LastSessionFacts? newest = null;
        foreach (var profile in _store.GetAll().Values)
        {
            foreach (var session in profile.Sessions)
            {
                // STRICTLY newer, so a tie keeps the first seen — which is the run
                // the OrderByDescending this replaced would have handed back, that
                // sort being stable.
                if (newest != null && session.EndedUtc <= newest.EndedUtc) continue;
                newest = new LastSessionFacts(session.EndedUtc, session.Score, session.Accuracy,
                    session.DurationSeconds);
            }
        }

        return newest;
    }

    public SongPlayStats? GetSongStats(string songKey, string arrangementName)
    {
        var profile = _store.GetProfile(songKey, arrangementName);
        if (profile.Sessions.Count == 0) return null;
        return new SongPlayStats(songKey, arrangementName, profile.Sessions.Count, profile.BestScore,
            profile.BestAccuracy, profile.MasteryPercent,
            GradeCalculator.FromAccuracy(profile.BestAccuracy), profile.Sessions[^1].EndedUtc,
            SongPlayStats.LabelFor(songKey, profile.DisplayName), profile.FullComboAchieved,
            profile.ChartDifficulty);
    }
}
