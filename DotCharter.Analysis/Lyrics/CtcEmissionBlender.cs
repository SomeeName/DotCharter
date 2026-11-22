namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Combines two CTC heads' per-frame distributions into one.
///     <para>
///         <b>Why this beats picking a model.</b> Alignment errors do not arrive
///         as jitter, they arrive in coherent RUNS — measured at 177 runs of mean
///         9.1 syllables — which is the trellis losing the thread through one hard
///         passage rather than being a little off everywhere. Two independently
///         trained heads lose it in DIFFERENT passages, so where one is confused
///         the other still carries the path. That is why the win shows up in the
///         TAIL: on 40 English arrangements p90 |error| fell 602 → 292 ms while
///         the median barely moved.
///     </para>
///     <para>
///         Both heads are wav2vec2 at the same 20 ms stride over what is
///         essentially the same A–Z alphabet, which is what makes a per-frame
///         combination meaningful at all. Blending heads with different frame
///         rates or genuinely different symbol sets would need resampling and a
///         mapping this does not attempt.
///     </para>
/// </summary>
public static class CtcEmissionBlender
{
    /// <summary>
    ///     The primary's share in the linear opinion pool (<see cref="CtcEmissions.Pooled" />). The
    ///     geometric blend needs both heads to agree on a letter, so one head's confident blank
    ///     drowns a word only the other head heard; the pool keeps a letter that EITHER head
    ///     heard. The secondary gets the larger share because the pool exists to recover what
    ///     the primary missed (measured on Jamendo: 0.3 → 1.86% gross, 0.5 → 2.11%, 0.7 → 2.38%).
    /// </summary>
    public const double PoolPrimaryWeight = 0.3;

    /// <summary>
    ///     Weighted geometric mean of two emission matrices, expressed in
    ///     <paramref name="primaryVocabulary" />'s symbol space (the one the
    ///     tokenizer emits, so the aligner needs no other change).
    ///     <para>
    ///         <paramref name="primaryWeight" /> 1.0 returns the primary unchanged
    ///         — the single-model path is therefore byte-identical, not merely
    ///         equivalent — and 0.0 is the secondary's opinion mapped across.
    ///     </para>
    /// </summary>
    /// <param name="primary">Emissions whose symbol space the result uses.</param>
    /// <param name="secondary">The other head's emissions.</param>
    /// <param name="primaryVocabulary">Vocabulary of <paramref name="primary" />.</param>
    /// <param name="secondaryVocabulary">Vocabulary of <paramref name="secondary" />.</param>
    /// <param name="primaryWeight">Share given to the primary, clamped to 0..1.</param>
    public static CtcEmissions Blend(
        CtcEmissions primary,
        CtcEmissions secondary,
        LyricVocabulary primaryVocabulary,
        LyricVocabulary secondaryVocabulary,
        double primaryWeight)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(primaryVocabulary);
        ArgumentNullException.ThrowIfNull(secondaryVocabulary);

        var weight = Math.Clamp(primaryWeight, 0.0, 1.0);
        if (weight >= 1.0) return primary;

        // Conv padding can leave two heads one frame apart on the same audio, so
        // the blend covers what they both saw.
        var frames = Math.Min(primary.FrameCount, secondary.FrameCount);
        if (frames <= 0) return primary;

        var width = primary.Width;
        var map = BuildSymbolMap(primaryVocabulary, secondaryVocabulary, width);
        var blended = new float[frames * width];
        var pooled = new float[frames * width];

        for (var f = 0; f < frames; f++)
        {
            var p = f * width;
            var s = f * secondary.Width;

            var max = float.NegativeInfinity;
            for (var i = 0; i < width; i++)
            {
                // A symbol the other head has no opinion about keeps the primary's
                // value: dropping it would make the blend refuse characters the
                // primary can align perfectly well.
                var value = map[i] >= 0
                    ? (float)(weight * primary.LogProbs[p + i] +
                              (1 - weight) * secondary.LogProbs[s + map[i]])
                    : primary.LogProbs[p + i];

                blended[p + i] = value;
                pooled[p + i] = map[i] >= 0
                    ? (float)Math.Log(PoolPrimaryWeight * Math.Exp(primary.LogProbs[p + i]) +
                                      (1 - PoolPrimaryWeight) * Math.Exp(secondary.LogProbs[s + map[i]]) + float.Epsilon)
                    : primary.LogProbs[p + i];
                if (value > max) max = value;
            }

            Normalize(pooled.AsSpan(p, width));

            // A weighted mean of two log-softmax rows is not itself one, and the
            // trellis compares absolute scores ACROSS frames — an unnormalized row
            // would silently weight some frames more than others.
            var sum = 0.0;
            for (var i = 0; i < width; i++) sum += Math.Exp(blended[p + i] - max);
            var logZ = (float)(max + Math.Log(sum));
            for (var i = 0; i < width; i++) blended[p + i] -= logZ;
        }

        return new CtcEmissions(blended, frames, width, primary.SecondsPerFrame)
        {
            Pooled = new CtcEmissions(pooled, frames, width, primary.SecondsPerFrame)
        };
    }

    /// <summary>Renormalise one row of log-probabilities so it sums to 1.</summary>
    private static void Normalize(Span<float> row)
    {
        var max = float.NegativeInfinity;
        foreach (var value in row) max = Math.Max(max, value);
        var sum = 0.0;
        foreach (var value in row) sum += Math.Exp(value - max);
        var logZ = (float)(max + Math.Log(sum));
        for (var i = 0; i < row.Length; i++) row[i] -= logZ;
    }

    /// <summary>
    ///     primary symbol id → secondary symbol id, or −1 where the secondary has
    ///     no such symbol.
    ///     <para>
    ///         Case needs no handling here even though one head spells its alphabet
    ///         lower-case and the other upper-case: <see cref="LyricVocabulary.FromTokens" />
    ///         already folds every token to lower case, which is exactly what makes
    ///         these two alphabets comparable at all.
    ///     </para>
    /// </summary>
    private static int[] BuildSymbolMap(LyricVocabulary primary, LyricVocabulary secondary, int width)
    {
        var map = new int[width];
        Array.Fill(map, -1);

        if (primary.BlankId >= 0 && primary.BlankId < width) map[primary.BlankId] = secondary.BlankId;

        foreach (var c in primary.Characters)
        {
            if (!primary.TryGetId(c, out var id) || id < 0 || id >= width) continue;
            if (secondary.TryGetId(c, out var other)) map[id] = other;
        }

        return map;
    }
}
