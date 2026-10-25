namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     A short-hop loudness envelope of a vocal recording: one RMS value per hop,
///     frame <c>k</c> centred on <c>k * HopSeconds</c>.
///     <para>
///         Pure and allocation-light — samples in, a few thousand floats out, no
///         I/O and no model. It answers one question for lyric timing: <i>is the
///         voice still sounding at time t?</i> CTC cannot, because it is peaky and
///         speech-trained: it stops emitting a sung vowel shortly after the onset.
///     </para>
/// </summary>
public sealed class VocalEnvelope
{
    private readonly float[] _levels;

    /// <summary>Builds an envelope from precomputed per-hop levels (tests, caches).</summary>
    public VocalEnvelope(float[] levels, double hopSeconds)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hopSeconds);

        _levels = levels;
        HopSeconds = hopSeconds;
    }

    /// <summary>Seconds between two envelope frames.</summary>
    public double HopSeconds { get; }

    /// <summary>Number of frames.</summary>
    public int FrameCount => _levels.Length;

    /// <summary>Seconds the envelope covers.</summary>
    public double DurationSeconds => _levels.Length * HopSeconds;

    /// <summary>RMS level of frame <paramref name="frame" />, linear amplitude.</summary>
    public float Level(int frame) => _levels[frame];

    /// <summary>The frame whose centre is nearest <paramref name="seconds" />, clamped to the envelope.</summary>
    public int FrameAt(double seconds) =>
        _levels.Length == 0 ? 0 : Math.Clamp((int)Math.Round(seconds / HopSeconds), 0, _levels.Length - 1);

    /// <summary>
    ///     Frame RMS over a mono signal. Each frame covers
    ///     <paramref name="windowSeconds" /> centred on its own time; the edges
    ///     are clipped, not zero-padded, so a frame never reads quieter than the
    ///     audio it actually covers.
    /// </summary>
    public static VocalEnvelope FromSamples(
        ReadOnlySpan<float> samples, int sampleRate, double hopSeconds = 0.010, double windowSeconds = 0.030)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hopSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSeconds);

        var hop = Math.Max(1, (int)Math.Round(hopSeconds * sampleRate));
        var half = Math.Max(1, (int)Math.Round(windowSeconds * sampleRate / 2));
        var frames = samples.Length / hop;
        var levels = new float[frames];

        for (var k = 0; k < frames; k++)
        {
            var centre = k * hop;
            var from = Math.Max(0, centre - half);
            var to = Math.Min(samples.Length, centre + half);
            var sum = 0.0;
            for (var n = from; n < to; n++) sum += (double)samples[n] * samples[n];

            levels[k] = to > from ? (float)Math.Sqrt(sum / (to - from)) : 0f;
        }

        return new VocalEnvelope(levels, hop / (double)sampleRate);
    }

    /// <summary>
    ///     The level the voice reaches over <c>[fromSeconds, toSeconds]</c>: the
    ///     given percentile of the frame levels (0.9 shrugs off a single click and
    ///     still tracks a loud passage). Zero for an empty range.
    /// </summary>
    public float ReferenceLevel(double fromSeconds, double toSeconds, double percentile)
    {
        if (_levels.Length == 0 || toSeconds < fromSeconds) return 0f;

        var first = FrameAt(fromSeconds);
        var last = FrameAt(toSeconds);
        var window = new float[last - first + 1];
        Array.Copy(_levels, first, window, 0, window.Length);
        Array.Sort(window);

        return window[(int)Math.Clamp(percentile * (window.Length - 1), 0, window.Length - 1)];
    }
}
