using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
/// The results screen's technique line: every judged technique a run hit WITHOUT
///     its technique (<see cref="LivePlayVerdict.HitWrongTechnique" />, half score), as
///     "Played without the technique: bends 3/12 · palm mutes 1/40" — played-without over judged.
///     Only the techniques something can downgrade are listed: the palm mute (its timbre) and
///     the bend, slide and vibrato (the pitch contour, behind its setting). Empty when there
///     were none, which hides the line.
/// </summary>
public static class ResultsTechniqueLine
{
    /// <summary>The listed techniques, in the order the line names them, with their keys.</summary>
    public static IReadOnlyList<(PlayedTechnique Technique, string Key)> Listed { get; } =
    [
        (PlayedTechnique.Bend, "PlayMode_Results_Technique_Bend"),
        (PlayedTechnique.Slide, "PlayMode_Results_Technique_Slide"),
        (PlayedTechnique.Vibrato, "PlayMode_Results_Technique_Vibrato"),
        (PlayedTechnique.PalmMute, "PlayMode_Results_Technique_PalmMute")
    ];

    public static string For(IReadOnlyList<TechniqueResult>? techniques, ILocalizationService localization)
    {
        if (techniques is not { Count: > 0 }) return "";

        var parts = new List<string>(Listed.Count);
        foreach (var (technique, key) in Listed)
        {
            foreach (var t in techniques)
            {
                if (t.Technique != technique || t.WrongTechnique == 0) continue;
                parts.Add(localization.Format(key, t.WrongTechnique, t.Judged));
            }
        }

        return parts.Count == 0 ? "" : localization.Format("PlayMode_Results_TechniqueLine", string.Join(" · ", parts));
    }
}
