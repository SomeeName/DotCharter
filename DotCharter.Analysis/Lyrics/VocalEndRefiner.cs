namespace DotCharter.Analysis.Lyrics;

/// <summary>Thresholds for <see cref="VocalEndRefiner" />. All injected so a test can name its own.</summary>
public sealed record VocalEndOptions
{
    /// <summary>Product defaults.</summary>
    public static VocalEndOptions Default { get; } = new();

    /// <summary>
    ///     A frame is silent below this fraction of the phrase's reference level
    ///     (linear amplitude; 0.30 is about −10 dB). Relative, so a quiet verse and a
    ///     shouted chorus are judged against their own loudness.
    /// </summary>
    public double SilenceRatio { get; init; } = 0.30;

    /// <summary>
    ///     Absolute linear floor under the relative threshold. Keeps separation
    ///     residue in an otherwise empty stretch from reading as a voice.
    /// </summary>
    public double SilenceFloor { get; init; } = 0.002;

    /// <summary>
    ///     How long the envelope must STAY silent before it counts as the voice
    ///     stopping. A consonant closure or a flutter in a held note is shorter;
    ///     a breath or a rest is longer.
    /// </summary>
    public double MinSilenceSeconds { get; init; } = 0.050;

    /// <summary>Percentile of the phrase's frame levels taken as its reference level.</summary>
    public double ReferencePercentile { get; init; } = 0.90;

    /// <summary>
    ///     Shortest stretch a phrase's reference level is read over. A phrase
    ///     whose CTC spans are a few tens of milliseconds long would otherwise be
    ///     judged on its onset click alone.
    /// </summary>
    public double MinReferenceSeconds { get; init; } = 0.300;
}

/// <summary>
///     Moves a syllable's END to where the voice stops, in both directions.
///     <para>
///         The CTC end is only a FLOOR. A speech-trained, peaky model stops
///         emitting a sung vowel soon after its onset, so a held phrase-final
///         syllable comes out far too short; and the chart's span rule (extend to
///         the next onset) bridges breaths because it never listens. With a
///         vocal envelope both are one rule: from the floor, follow the voice
///         until it falls silent and stop there.
///     </para>
///     <para>
///         The result is never earlier than the floor and never later than the
///         limit; the caller owns what those mean (the next syllable's start
///         minus the minimum gap, <c>MaxSyllableSeconds</c>).
///     </para>
/// </summary>
public static class VocalEndRefiner
{
    /// <summary>
    ///     The reference level for a phrase that sounds over
    ///     <c>[startSeconds, endSeconds]</c>.
    /// </summary>
    public static float ReferenceLevel(
        VocalEnvelope envelope, double startSeconds, double endSeconds, VocalEndOptions options)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(options);

        var to = Math.Max(endSeconds, startSeconds + options.MinReferenceSeconds);
        return envelope.ReferenceLevel(startSeconds, to, options.ReferencePercentile);
    }

    /// <summary>
    ///     The syllable's end, in seconds.
    /// </summary>
    /// <param name="envelope">The vocal stem's envelope.</param>
    /// <param name="referenceLevel">The phrase's loudness, from <see cref="ReferenceLevel" />.</param>
    /// <param name="floorSeconds">The end the syllable already has (the CTC end); never undercut.</param>
    /// <param name="limitSeconds">The latest the end may be.</param>
    /// <param name="bridgeWhenVoiced">
    ///     What to do when the voice never falls silent before the limit. True
    ///     (the boundary is one the chart spans) runs to the limit; false keeps
    ///     the floor, because the voice rolling straight into the next syllable
    ///     is not a fade and the boundary is one the user asked not to span.
    /// </param>
    /// <param name="options">Thresholds.</param>
    public static double Refine(
        VocalEnvelope envelope, double referenceLevel, double floorSeconds, double limitSeconds,
        bool bridgeWhenVoiced, VocalEndOptions options)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(options);

        if (limitSeconds <= floorSeconds || envelope.FrameCount == 0) return floorSeconds;

        var threshold = Math.Max(referenceLevel * options.SilenceRatio, options.SilenceFloor);
        var minRun = Math.Max(1, (int)Math.Ceiling(options.MinSilenceSeconds / envelope.HopSeconds - 1e-9));
        var last = envelope.FrameAt(limitSeconds);
        var runStart = -1;

        for (var f = envelope.FrameAt(floorSeconds); f <= last; f++)
        {
            if (envelope.Level(f) >= threshold)
            {
                runStart = -1;
                continue;
            }

            if (runStart < 0) runStart = f;
            if (f - runStart + 1 >= minRun)
                return Math.Clamp(runStart * envelope.HopSeconds, floorSeconds, limitSeconds);
        }

        return bridgeWhenVoiced ? limitSeconds : floorSeconds;
    }
}
