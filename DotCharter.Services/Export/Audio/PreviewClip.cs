namespace DotCharter.Services.Export.Audio;

/// <summary>
///     Cuts the short in-game preview from the full song. Length and fades
///     follow DLC Builder (Preview.fs + AudioFader.fs): 28 s, 2.5 s linear
///     fade-in, 3 s linear fade-out, both halved (whole ms) when the WHOLE SONG
///     is shorter than their sum. The ramps are DLC Builder's own: the fade-in
///     climbs from 0 to 1, the fade-out starts at 1 and ends one step above 0,
///     and it begins at <c>min(song, 28 s) - 3 s</c> into the clip.
/// </summary>
public static class PreviewClip
{
    public const double DefaultLengthMs = 28_000;
    private const double FadeInMs = 2_500;
    private const double FadeOutMs = 3_000;

    /// <summary>Where the clip starts when nobody chose: a quarter into the song,
    /// kept far enough from the end for a whole clip. Shared by the Rocksmith
    /// package and the <c>.dcsong</c> package so both preview the same bars.</summary>
    public static double DefaultStartMs(double totalMs) => ClampStartMs(totalMs * 0.25, totalMs);

    /// <summary>The latest start that still leaves a whole clip, and never before 0.
    /// A song shorter than the clip starts at 0.</summary>
    public static double ClampStartMs(double startMs, double totalMs) =>
        Math.Clamp(startMs, 0, Math.Max(0, totalMs - DefaultLengthMs));

    public static PcmAudio Create(PcmAudio source, double startMs, double lengthMs = DefaultLengthMs)
    {
        var channels = Math.Max(1, source.Channels);
        var totalFrames = source.FrameCount;
        if (totalFrames == 0) return source;

        // DLC Builder reads the song length in whole ms, then halves the fades (whole ms) for a short song.
        var songMs = source.Duration.TotalMilliseconds;
        startMs = ClampStartMs(startMs, songMs);
        var startFrame = Math.Clamp(
            (int)(startMs / 1000.0 * source.SampleRate), 0, Math.Max(0, totalFrames - 1));
        var frameCount = Math.Min(
            (int)(lengthMs / 1000.0 * source.SampleRate), totalFrames - startFrame);
        if (frameCount <= 0) return source;

        var samples = new short[frameCount * channels];
        Array.Copy(source.Samples, startFrame * channels, samples, 0, samples.Length);

        var (fadeInMs, fadeOutMs) = songMs < FadeInMs + FadeOutMs
            ? ((long)(songMs / 2), (long)(songMs / 2))
            : ((long)FadeInMs, (long)FadeOutMs);
        var clipLengthMs = Math.Min((long)songMs, (long)lengthMs);

        // AudioFader: the fade-in multiplies frame p by p / count for p = 0..count (count + 1 frames, the last at 1).
        var fadeInCount = (int)(fadeInMs * source.SampleRate / 1000);
        for (var f = 0; f < frameCount && f < fadeInCount; f++)
        {
            var gain = f / (float)fadeInCount;
            for (var c = 0; c < channels; c++)
                samples[(f * channels) + c] = (short)(samples[(f * channels) + c] * gain);
        }

        // The fade-out runs from (clip - fade-out) to the end: frame k of it is 1 - k / count.
        var fadeOutCount = (int)(fadeOutMs * source.SampleRate / 1000);
        var fadeOutStartSample = (clipLengthMs - fadeOutMs) * source.SampleRate * channels / 1000;
        var fadeOutStartFrame = (int)Math.Max(0, fadeOutStartSample / channels);
        for (var f = fadeOutStartFrame; f < frameCount; f++)
        {
            var gain = Math.Max(0f, 1f - ((f - fadeOutStartFrame) / (float)fadeOutCount));
            for (var c = 0; c < channels; c++)
                samples[(f * channels) + c] = (short)(samples[(f * channels) + c] * gain);
        }

        return source with { Samples = samples };
    }
}
