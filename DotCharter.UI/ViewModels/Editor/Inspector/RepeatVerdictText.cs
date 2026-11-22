using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The inspector's one line about a selected chord's repeat box: "In game: compact repeat box
///     (same chord as the strum before)", "In game: full chord box with its name (vibrato on string A (5))". The verdict is
///     <see cref="RepeatChords.Explain" />, the very function the export's set is derived from, so the line
///     cannot disagree with what ships. Each reason is the resx key
///     <c>Inspector_RepeatReason_&lt;RepeatChordReason&gt;</c>; a per-string reason takes the string's
///     <see cref="GuitarStringLabel" />.
/// </summary>
public static class RepeatVerdictText
{
    /// <summary>The resx key of one reason.</summary>
    public static string ReasonKey(RepeatChordReason reason) => "Inspector_RepeatReason_" + reason;

    public static string Format(RepeatChordVerdict verdict, int stringCount, IReadOnlyList<string>? tuning,
        ILocalizationService localization)
    {
        var key = ReasonKey(verdict.Reason);
        var reason = verdict.StringIndex >= 0
            ? localization.Format(key,
                GuitarStringLabel.Format(verdict.StringIndex, stringCount, tuning, localization["Inspector_StringLabelFmt"]))
            : localization[key];
        return localization.Format(FrameKey(verdict), reason);
    }

    /// <summary>The resx key of the sentence frame: what the player SEES in game for this verdict.</summary>
    public static string FrameKey(RepeatChordVerdict verdict) => verdict.Reason switch
    {
        RepeatChordReason.SplitIntoNotes or RepeatChordReason.LinkLanding => "Inspector_RepeatVerdict_Notes",
        RepeatChordReason.UnresolvedTemplate => "Inspector_RepeatVerdict_NotShown",
        _ => verdict.Ships ? "Inspector_RepeatVerdict_Yes" : "Inspector_RepeatVerdict_No"
    };
}
