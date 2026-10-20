namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     The distance every lane scores against: a weighted, block-normalised L2 over
///     <c>tonefx-v1</c> feature vectors. 0 = the same tone; lower is better
///     everywhere.
///     <para>
///         <b>Content-invariant by construction, which is the only reason a match is
///         possible at all.</b> The target is a guitar stem lifted off a record and
///         the candidate is a reference DI — a different player, a different riff, a
///         different guitar — pushed through a rig. Nothing may be compared that
///         depends on which notes were played, so this metric never touches raw
///         audio; it only ever compares two <see cref="ToneFeatures" />.
///         <see cref="MultiScaleSpectralDistance" /> is the raw-audio loss and it is
///         valid ONLY between two renders of the SAME dry take.
///     </para>
///     <para>
///         Each block contributes <c>weight · mean(squared difference over its
///         elements)</c>, and the total is the square root of the sum. Dividing by
///         the block length is what stops the 144-element mel block from outvoting
///         the 4-element distortion block by arithmetic rather than by importance;
///         the weights (all in <see cref="ToneFeatureLayout.Blocks" />) then say what
///         importance is.
///     </para>
///     <para>
///         Scale, for reading a number in a report: same clip against itself is
///         exactly 0; the same rig on two different riffs lands around 0.1–0.3; a
///         clean amp against a fuzz on the same riff is above 1.0. The eval pins
///         those relationships as a RATIO rather than as absolute thresholds, because
///         an absolute threshold is the first thing a tuning change invalidates.
///     </para>
/// </summary>
public sealed class ToneSimilarity : IToneSimilarity
{
    /// <summary>Builds the distance over a given extractor. The extractor is part of
    /// the metric: two vectors from different extractors are not comparable and
    /// <see cref="Distance(ToneFeatures, ToneFeatures)" /> refuses them.</summary>
    public ToneSimilarity(IToneFeatureExtractor extractor) =>
        Extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));

    /// <summary>Convenience for callers that just want the shipping configuration.</summary>
    public ToneSimilarity() : this(new ToneFeatureExtractor()) { }

    /// <inheritdoc />
    public IToneFeatureExtractor Extractor { get; }

    /// <inheritdoc />
    public double Distance(ToneFeatures a, ToneFeatures b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        // Refused rather than tolerated: comparing a tonefx-v1 vector against a
        // future tonefx-v2 one produces a plausible number that means nothing, and a
        // search lane would optimise it happily for an hour.
        if (!string.Equals(a.ExtractorId, b.ExtractorId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Feature vectors come from different extractors ('{a.ExtractorId}' vs '{b.ExtractorId}') " +
                "and are not comparable.", nameof(b));
        }

        if (a.Vector.Length != b.Vector.Length)
        {
            throw new ArgumentException(
                $"Feature vectors differ in length ({a.Vector.Length} vs {b.Vector.Length}).", nameof(b));
        }

        double total = 0;
        foreach (var block in ToneFeatureLayout.Blocks)
        {
            if (block.Offset + block.Count > a.Vector.Length) continue;
            double sum = 0;
            for (var i = block.Offset; i < block.Offset + block.Count; i++)
            {
                var d = a.Vector[i] - (double)b.Vector[i];
                sum += d * d;
            }

            total += block.Weight * (sum / block.Count);
        }

        return Math.Sqrt(Math.Max(total, 0));
    }

    /// <summary>
    ///     Convenience: extract both clips and compare. Every caller that has audio
    ///     rather than vectors should use this, so that "the same extractor on both
    ///     sides of every comparison" is not a rule anyone has to remember.
    /// </summary>
    public double Distance(MonoAudio a, MonoAudio b) =>
        Distance(Extractor.Extract(a), Extractor.Extract(b));

    /// <summary>
    ///     The per-block breakdown behind a distance, for reports and for debugging a
    ///     match that went wrong. The values are the block's weighted contribution to
    ///     the SQUARED total, so they sum to <c>Distance²</c>.
    /// </summary>
    /// <remarks>One implementation, in <see cref="MatchScale.BlockContributions" /> —
    /// the score's breakdown and this method must never be two different sums.</remarks>
    public IReadOnlyList<(string Block, double Contribution)> Explain(ToneFeatures a, ToneFeatures b)
        => MatchScale.BlockContributions(a, b);
}
