// LivePlayViewModel: what a finished run WRITES to the practice profile - the profile keys, the recorded session and the published bests.

using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     What a finished run WRITES to the practice profile, and the keys it
///     writes under: the arrangement profile, the session record and the bests
///     published back to the HUD. The take and the replay are
///     <see cref="LivePlayRunArchive" /> (<see cref="RunArchive" />). The key
///     derivations are <c>internal static</c> because the store and its tests
///     have to agree with them exactly.
///     <para>
///         Part of <see cref="LivePlayViewModel" />. The type's fields, its
///         whole bound surface and its constructor stay in the ROOT file, so
///         "where does this come from" is one look rather than a search.
///     </para>
/// </summary>
public partial class LivePlayViewModel
{
    /// <summary>
    ///     The practice store's song key. Project path first (own charts), then
    ///     the stable PSARC identity set by the song browser — PSARC loads have
    ///     no path, and keying them all as "untitled" would merge every library
    ///     song's bests.
    ///     <para><b>Public because reading a best has to use the same key writing
    ///     one does.</b> The Get Ready screen's per-path personal best is looked
    ///     up through this pair; a second copy of the rule would report every
    ///     path as unplayed the moment either half drifted, which is
    ///     indistinguishable from a genuinely new song.</para>
    /// </summary>
    internal static string ProfileSongKey(string? projectPath, string? songIdentity) =>
        !string.IsNullOrEmpty(projectPath)
            ? projectPath
            : !string.IsNullOrEmpty(songIdentity)
                ? songIdentity
                : "untitled";

    /// <inheritdoc cref="ProfileSongKey" />
    internal static string ProfileArrangementKey(Arrangement arrangement) =>
        $"{arrangement.ArrangementType}:{arrangement.Name}";

    private PracticeArrangementProfile LoadProfile(Arrangement arrangement)
    {
        // Project path first (own charts), then the stable PSARC identity
        // set by the song browser — PSARC loads have no path, and keying
        // them all as "untitled" would merge every library song's bests.
        _profileSongKey = ProfileSongKey(
            _projectState.CurrentProjectPath, _projectState.CurrentSongIdentity);
        _profileArrangement = ProfileArrangementKey(arrangement);
        var profile = _profiles.GetProfile(_profileSongKey, _profileArrangement);
        PublishProfile(profile);
        return profile;
    }

    private bool RecordSession(LivePlaySessionResult result, IReadOnlyList<SectionResult>? sections = null)
    {
        if (result.Hits + result.Misses == 0 || string.IsNullOrEmpty(_profileSongKey)) return false;
        PublishProfile(_profiles.Record(_profileSongKey, _profileArrangement, result, SongDisplayName(),
            _session.LastPhraseOutcomes, sections, _session.LastTechniqueTallies, _chartDifficulty,
            failed: Meters.RunFailed));
        return true;
    }

    /// <summary>"Artist - Title" for the stats/leaderboard pages — the store
    /// key (path / psarc id) is not fit for display.</summary>
    private string? SongDisplayName()
    {
        var chart = _projectState.ActiveProject?.Chart;
        if (chart == null) return null;
        var title = chart.SongName?.Trim();
        var artist = chart.Artist?.Trim();
        if (string.IsNullOrEmpty(title)) return null;
        return string.IsNullOrEmpty(artist) || artist == "Unknown Artist" ? title : $"{artist} - {title}";
    }

    private void PublishProfile(PracticeArrangementProfile profile)
    {
        BestScore = profile.BestScore;
        MasteryPercent = profile.MasteryPercent;
        BestAccuracyPercent = Math.Round(profile.BestAccuracy * 100.0, 1);
        ProfileSessions = profile.Sessions;
        OnPropertyChanged(nameof(ProfileSessions));
        ProfileVersion++;
    }

    private static LiveChordVoicingStrictness ParseChordStrictness(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "lenient" => LiveChordVoicingStrictness.Lenient,
            "strict" => LiveChordVoicingStrictness.Strict,
            _ => LiveChordVoicingStrictness.Standard
        };
}
