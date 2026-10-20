using DotCharter.Localization;
using DotCharter.Services.Export.Validation;
using DotCharter.Tone.Audio.Tuning;

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
        if (finding.Rule != null) return finding.Rule.Message;
        if (finding.Export != null) return ExportFindingFormatter.FormatMessage(finding.Export, localization);
        if (finding.Audio is not { } audio) return finding.Code;

        var time = TimeSpan.FromMilliseconds(Math.Max(0, audio.TimeMs)).ToString(@"m\:ss\.f");
        var charted = ArrangementTunerLogic.NoteNameOf(audio.ChartedMidi);
        return audio.ObservedMidi is { } heard
            ? localization.Format("Inspector_AudioLint_FindingFmt", charted, time, ArrangementTunerLogic.NoteNameOf(heard))
            : localization.Format("Inspector_AudioLint_FindingSilentFmt", charted, time);
    }
}
