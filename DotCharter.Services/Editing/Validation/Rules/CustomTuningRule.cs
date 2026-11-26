using DotCharter.Services.Editing.Arrangements;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D26: warns once when an arrangement's tuning has no known name: it is none of the arrangement
///     settings' presets (<see cref="ArrangementSettingsEdit.TuningPresets" />, tested with
///     <see cref="ArrangementSettingsEdit.PresetFromOffsets" />, the picker's own test) AND
///     <see cref="TuningNameResolver.ResolveFromOffsets" /> has no name for it at that string count
///     (B Standard or Drop A on a 4-string bass, DADGAD or Open G on a guitar are named, not custom).
///     A custom tuning is sometimes right and just as often a typo or a half-step slip, and Rocksmith
///     only shows it as custom, so charters double-check it. The export writes no tuning name, only the
///     per-string offsets; the game names the tuning from them.
/// </summary>
public sealed class CustomTuningRule : IArrangementRule
{
    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var arrangement = context.Arrangement;
        if (arrangement.ArrangementType == ArrangementType.Vocal) return;

        var offsets = TuningOffsetResolution.Resolve(arrangement).ToArray();
        // An RS-XML bass carries six tuning entries for its four strings: judge the strings it has.
        if (arrangement.StringCount > 0 && offsets.Length > arrangement.StringCount) offsets = offsets[..arrangement.StringCount];
        if (offsets.Length == 0) return;

        var bass = arrangement.ArrangementType == ArrangementType.Bass;
        if (ArrangementSettingsEdit.PresetFromOffsets(offsets, bass) != ArrangementSettingsEdit.CustomPreset) return;
        if (TuningNameResolver.ResolveFromOffsets(offsets.Length, offsets) is not null) return;

        var names = string.Join(" ", TuningUtilities.GetTuningNames(offsets.Length, offsets));
        issues.Add(ArrangementIssues.Make(ArrangementIssueType.CustomTuning, "Validation_CustomTuning", args: [names]));
    }
}
