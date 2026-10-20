using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The inspector's one line about a selected chord's repeat box: "Repeat: yes — same chord as
///     the previous strum", "Repeat: no — vibrato on string A (5)". The verdict is
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
        return localization.Format(verdict.Ships ? "Inspector_RepeatVerdict_Yes" : "Inspector_RepeatVerdict_No", reason);
    }
}
