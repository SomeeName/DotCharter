namespace DotCharter.Services.Audio.Coordination;

/// <summary>
/// The Tone designer's door onto the chart's tone playback: a tone being
///     edited is heard AS IT IS EDITED wherever the chart's tones drive the rig (the
///     headphones preview, the chart synth) — not only after the edit is committed.
///     Implemented by the shell's tone rig, which forwards to
///     <see cref="ChartTonePreviewController" /> when one exists; nothing is driving
///     the chart's tones otherwise, so there is nothing to update.
/// </summary>
public interface IChartToneLiveEdit
{
    /// <summary>The buffered body of the project tone <paramref name="key" />
    /// (its committed key — what the chart names), as tone JSON.</summary>
    void PreviewToneEdit(string key, string json);

    /// <summary>The designer closed: play the committed bodies again.</summary>
    void EndToneEditPreview();
}
