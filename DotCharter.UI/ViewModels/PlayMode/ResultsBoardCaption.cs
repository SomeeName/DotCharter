using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The caption under the results screen's local leaderboard: which note
/// detector's runs it ranks.
///     <para>
///         The board is per detector
///         (<c>ILeaderboardService.GetSongLeaderboard(…, detector)</c>), because
///         the CQT verifier and the classic path hear the same playing
///         differently. A player who flipped the verifier at Get Ready saw a
///         board that had lost their old best with no reason given; the caption
///         is that reason. <see cref="LiveDetectorMode.Unknown" /> is the
///         pre-0.64.0 board, every profile's best whatever produced it, and says
///         so.
///     </para>
///     <para>
///         The library's per-song best (Song Select's "Your best") stays
///         cross-detector on purpose; see <c>docs/agents/ui/play-mode.md</c>.
///     </para>
/// </summary>
public static class ResultsBoardCaption
{
    /// <summary>The resource key naming <paramref name="detector" />'s board.</summary>
    public static string KeyFor(LiveDetectorMode detector) => detector switch
    {
        LiveDetectorMode.Cqt => "PlayMode_Results_Leaderboard_Cqt",
        LiveDetectorMode.Classic => "PlayMode_Results_Leaderboard_Classic",
        _ => "PlayMode_Results_Leaderboard_AnyDetector"
    };

    /// <summary>The localized caption for <paramref name="detector" />'s board.</summary>
    public static string For(LiveDetectorMode detector, ILocalizationService localization) =>
        localization[KeyFor(detector)];
}
