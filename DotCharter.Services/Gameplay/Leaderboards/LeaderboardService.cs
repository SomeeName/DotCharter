using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Library;

namespace DotCharter.Services.Gameplay.Leaderboards;

/// <summary>One profile's row in a per-song local leaderboard.</summary>
public sealed record LeaderboardEntry(
    int Rank,
    Guid ProfileId,
    string ProfileName,
    string AccentColor,
    bool IsActiveProfile,
    long BestScore,
    double BestAccuracy,
    int BestStreak,
    LetterGrade Grade,
    DateTime LastPlayedUtc,
    LiveDetectorMode Detector = LiveDetectorMode.Unknown)
{
    /// <summary>
    ///     This profile has a MEASURED best accuracy for the song. False is an
    ///     ordinary state, not an empty row: the store's best accuracy is
    ///     coverage-gated (<c>PracticeProfileService.MinCoverageForBestAccuracy</c>),
    ///     so a profile whose runs are all section drills or mid-song quits ranks
    ///     on score with its accuracy still at the 0 default — which
    ///     <see cref="Grade" /> turns into a confident <b>F</b>.
    /// </summary>
    public bool HasBestAccuracy => AccuracyMath.IsMeasuredBest(BestAccuracy);

    /// <summary>The grade to PRINT — the letter once one has been earned, the
    /// unmeasured em dash until then. Bind this, never <see cref="Grade" />.
    /// </summary>
    public string GradeLabel =>
        HasBestAccuracy ? GradeCalculator.Label(Grade) : AccuracyMath.UnmeasuredLabel;
}

public interface ILeaderboardService
{
    /// <summary>
    ///     Ranking across ALL local profiles for one song+arrangement and ONE
    ///     <paramref name="detector" />, best score first: each profile's best run
    ///     with that detector (<see cref="PracticeArrangementProfile.DetectorBests" />).
    /// Profiles that never played it with that detector are omitted — the
    ///     two detectors hear differently, so their scores are not comparable.
    ///     <see cref="LiveDetectorMode.Unknown" /> is the board of runs recorded before the detector was stored:
    ///     every profile's overall best, whatever produced it.
    ///     <para>
    ///         There is deliberately no second, any-ARRANGEMENT overload here. Nothing
    ///         would call it: it would be declared, implemented, unit-tested and
    ///         resolved with the service — a verb the app never drives, which is
    ///         the same fault as a registration nobody resolves, one level in.
    ///         <c>GameplayReachabilityTests.EveryGameplayServiceVerb_IsDrivenByProductCode</c>
    ///         now fails on the next one. Restore it from git WITH its caller if a
    ///         cross-profile "best arrangement" board is ever wanted.
    ///     </para>
    /// </summary>
    IReadOnlyList<LeaderboardEntry> GetSongLeaderboard(string songKey, string arrangementName,
        LiveDetectorMode detector);
}

/// <summary>
///     Cross-profile read-model: every profile's practice store file is read
///     directly (read-only) so the active profile's live store is never
///     disturbed. Local-only data — no network, ever.
/// </summary>
public sealed class LeaderboardService : ILeaderboardService
{
    private readonly IPlayerProfileService _profiles;

    public LeaderboardService(IPlayerProfileService profiles) => _profiles = profiles;

    public IReadOnlyList<LeaderboardEntry> GetSongLeaderboard(string songKey, string arrangementName,
        LiveDetectorMode detector)
        => Build(songKey, arrangementName, detector);

    private IReadOnlyList<LeaderboardEntry> Build(string songKey, string arrangementName, LiveDetectorMode detector)
    {
        songKey = LibrarySongIdentity.Normalize(songKey);
        var detectorKey = LiveDetectorModes.StoreKey(detector);
        var activeId = _profiles.ActiveProfile.Id;
        var rows = new List<LeaderboardEntry>();

        foreach (var profile in _profiles.Profiles)
        {
            var store = PracticeProfileService.ReadStoreFile(
                Path.Combine(_profiles.GetDataDirectory(profile.Id), PracticeProfileService.StoreFileName));

            PracticeArrangementProfile? best = null;
            DateTime lastPlayed = default;
            foreach (var (key, entry) in store)
            {
                if (entry.Sessions.Count == 0) continue;
                if (!PracticeProfileService.TryParseKey(key, out var sk, out var arr)) continue;
                if (!string.Equals(sk, songKey, StringComparison.Ordinal)) continue;
                if (!string.Equals(arr, arrangementName, StringComparison.Ordinal)) continue;

                if (best == null || entry.BestScore > best.BestScore)
                {
                    best = entry;
                    lastPlayed = entry.Sessions[^1].EndedUtc;
                }
            }

            if (best == null) continue;
            if (detectorKey == null)
            {
                rows.Add(new LeaderboardEntry(0, profile.Id, profile.DisplayName, profile.AccentColor,
                    profile.Id == activeId, best.BestScore, best.BestAccuracy, best.BestStreak,
                    GradeCalculator.FromAccuracy(best.BestAccuracy), lastPlayed));
                continue;
            }

            if (best.DetectorBests?.GetValueOrDefault(detectorKey) is not { } mode) continue;
            rows.Add(new LeaderboardEntry(0, profile.Id, profile.DisplayName, profile.AccentColor,
                profile.Id == activeId, mode.BestScore, mode.BestAccuracy, mode.BestStreak,
                GradeCalculator.FromAccuracy(mode.BestAccuracy), mode.LastPlayedUtc, detector));
        }

        return rows
            .OrderByDescending(r => r.BestScore)
            .ThenByDescending(r => r.BestAccuracy)
            .Select((r, i) => r with { Rank = i + 1 })
            .ToArray();
    }
}
