using DotCharter.Core.Domain.Project;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Editing.Linting;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The quality report's audio half: the loaded song, and the audio lint over
/// each instrumental arrangement, run only when asked because it decodes the song.</summary>
public sealed class QualityAudioCheck(IAudioChartLintService lint, IPlaybackTransport playback)
{
    public string AudioPath => playback.CurrentAudioPath;

    public bool HasAudio => !string.IsNullOrEmpty(playback.CurrentAudioPath);

    public double? AudioLengthMs => HasAudio && playback.GetTotalAudioLengthMs() is > 0 and var ms ? ms : null;

    public async Task<IReadOnlyDictionary<Arrangement, AudioChartLintOutcome>> RunAsync(ChartProject project,
        CancellationToken cancellationToken)
    {
        var outcomes = new Dictionary<Arrangement, AudioChartLintOutcome>(ReferenceEqualityComparer.Instance);
        var path = AudioPath;
        foreach (var arrangement in project.Arrangements.Where(a => a.ArrangementType != ArrangementType.Vocal).ToList())
            outcomes[arrangement] = await lint.LintAsync(arrangement, project.Beats, path, cancellationToken);
        return outcomes;
    }
}
