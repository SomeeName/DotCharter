using JuceSharp;

namespace DotCharter.Tone.Audio.ChartSynth;

/// <summary>
///     Which voice actually plays an upload. The built-in voice is ONE choice for
///     the user and TWO instruments natively: a guitar arrangement is played by
///     <see cref="ChartSynthVoice.Guitar" />, a bass PART (its notes carry
///     <see cref="JuceChartNoteFlags.Bass" />, set by
///     <see cref="ChartNoteEventBuilder" /> from <see cref="ArrangementInstrument.IsBassPart(Arrangement)" />:
/// the Bass path, or a 5/6-string bass charted on a guitar path at A220)
///     by <see cref="ChartSynthVoice.Bass" />.
///     An explicit <see cref="ChartSynthVoice.Placeholder" /> is honoured as is.
///     Pure, so the rule is pinned by the default gate.
/// </summary>
public static class ChartSynthVoiceSelection
{
    /// <summary>The voice a fresh service requests: the built-in instrument.</summary>
    public const ChartSynthVoice Default = ChartSynthVoice.Guitar;

    /// <summary>
    ///     The voice to put on the engine for <paramref name="requested" /> and the
    ///     chart in <paramref name="upload" /> (null = nothing uploaded yet: the
    ///     request as it is).
    /// </summary>
    /// <param name="requested">What the user (or the default) asked for.</param>
    /// <param name="upload">The chart the engine plays, or null.</param>
    /// <returns><see cref="ChartSynthVoice.Bass" /> for a bass chart when a built-in
    /// voice is requested, <see cref="ChartSynthVoice.Guitar" /> for any other chart,
    /// and <paramref name="requested" /> unchanged when it is the placeholder.</returns>
    public static ChartSynthVoice Effective(ChartSynthVoice requested, ChartNoteUpload? upload)
    {
        if (requested == ChartSynthVoice.Placeholder || upload == null) return requested;
        return IsBass(upload) ? ChartSynthVoice.Bass : ChartSynthVoice.Guitar;
    }

    /// <summary>True when the upload is a bass arrangement.</summary>
    /// <param name="upload">The uploaded chart.</param>
    /// <returns>True when its first note carries the bass flag (the builder sets it on every note or none).</returns>
    public static bool IsBass(ChartNoteUpload upload)
    {
        ArgumentNullException.ThrowIfNull(upload);
        return upload.Notes.Length > 0 && (upload.Notes[0].Flags & JuceChartNoteFlags.Bass) != 0;
    }
}
