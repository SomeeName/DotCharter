namespace DotCharter.Services.Export.Audio;

/// <summary>
///     Cuts the short in-game preview from the full song. Length and fades
///     follow DLC Builder (Preview.fs): 28 s, 2.5 s linear fade-in, 3 s linear
///     fade-out, both halved when the clip is shorter than their sum.
/// </summary>
public static class PreviewClip
{
    public const double DefaultLengthMs = 28_000;
    private const double FadeInMs = 2_500;
    private const double FadeOutMs = 3_000;

    /// <summary>Where the clip starts when nobody chose: a quarter into the song,
    /// kept far enough from the end for a whole clip. Shared by the Rocksmith
    /// package and the <c>.dcsong</c> package so both preview the same bars.</summary>
    public static double DefaultStartMs(double totalMs) =>
        Math.Clamp(totalMs * 0.25, 0, Math.Max(0, totalMs - DefaultLengthMs));

    public static PcmAudio Create(PcmAudio source, double startMs, double lengthMs = DefaultLengthMs)
    {
        var channels = Math.Max(1, source.Channels);
        var totalFrames = source.FrameCount;
        if (totalFrames == 0) return source;

        var startFrame = Math.Clamp(
            (int)(startMs / 1000.0 * source.SampleRate), 0, Math.Max(0, totalFrames - 1));
        var frameCount = Math.Min(
            (int)(lengthMs / 1000.0 * source.SampleRate), totalFrames - startFrame);
        if (frameCount <= 0) return source;

        var samples = new short[frameCount * channels];
        Array.Copy(source.Samples, startFrame * channels, samples, 0, samples.Length);

        var clipMs = frameCount * 1000.0 / source.SampleRate;
        var (fadeInMs, fadeOutMs) = clipMs < FadeInMs + FadeOutMs
            ? (FadeInMs / 2, FadeOutMs / 2)
            : (FadeInMs, FadeOutMs);

        var fadeInFrames = Math.Min(frameCount, (int)(fadeInMs / 1000.0 * source.SampleRate));
        for (var f = 0; f < fadeInFrames; f++)
        {
            var gain = f / (double)fadeInFrames;
            for (var c = 0; c < channels; c++)
                samples[(f * channels) + c] = (short)(samples[(f * channels) + c] * gain);
        }

        var fadeOutFrames = Math.Min(frameCount, (int)(fadeOutMs / 1000.0 * source.SampleRate));
        for (var f = 0; f < fadeOutFrames; f++)
        {
            var gain = f / (double)fadeOutFrames;
            var frame = frameCount - 1 - f;
            for (var c = 0; c < channels; c++)
                samples[(frame * channels) + c] = (short)(samples[(frame * channels) + c] * gain);
        }

        return source with { Samples = samples };
    }
}
