using DotCharter.Localization;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Services.Editing.Quality;

/// <summary>
/// A finding's message in its own check's words: the rule's text, the export gate's
///     resx message, or the audio lint's sentence the Inspector prints. Here rather than in the
///     report screen, so the screen never touches the audio lint's Analysis types.
/// </summary>
public static class QualityFindingText
{
    public static string Message(QualityFinding finding, ILocalizationService localization)
    {
        if (finding.Rule != null) return ArrangementIssueText.Of(finding.Rule, localization);
        if (finding.Export != null) return ExportFindingFormatter.FormatMessage(finding.Export, localization);
        if (finding.Audio is not { } audio) return finding.Code;

        var time = ElapsedTimeLabel.Format(audio.TimeMs, 1);
        var charted = PitchName(finding, audio.ChartedMidi);
        return audio.ObservedMidi is { } heard
            ? localization.Format("Inspector_AudioLint_FindingFmt", charted, time, PitchName(finding, heard))
            : localization.Format("Inspector_AudioLint_FindingSilentFmt", charted, time);
    }

    private static string PitchName(QualityFinding finding, int midi) => finding.Arrangement is { } arrangement
        ? TuningDisplayNames.Pitch(midi, arrangement) : SoundingPitch.NameFor(midi);
}
