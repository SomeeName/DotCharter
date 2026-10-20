using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     Blending two CTC heads. The properties that matter are the ones the
///     trellis depends on: rows stay normalized, weight 1.0 is a strict no-op, and
///     a symbol only one head knows is not silently discarded.
/// </summary>
public sealed class CtcEmissionBlenderTests
{
    /// <summary>Lower-case a,b,c + blank — the shape MMS-FA's romanized vocab has.</summary>
    private static LyricVocabulary Lower() => LyricVocabulary.FromTokens(["<pad>", "a", "b", "c"]);

    /// <summary>Upper-case A,B + blank + a delimiter — the shape an English head has.</summary>
    private static LyricVocabulary Upper() => LyricVocabulary.FromTokens(["<pad>", "A", "B", "|"]);

    private static CtcEmissions Emissions(int frames, int width, Func<int, int, double> logProb)
    {
        var data = new float[frames * width];
        for (var f = 0; f < frames; f++)
        {
            for (var s = 0; s < width; s++)
                data[f * width + s] = (float)logProb(f, s);
        }

        return new CtcEmissions(data, frames, width, 0.02);
    }

    private static CtcEmissions Uniform(int frames, int width) =>
        Emissions(frames, width, (_, _) => Math.Log(1.0 / width));

    /// <summary>
    ///     Weight 1.0 must return the SAME instance, not an equivalent copy — the
    ///     single-model path is the common one and must cost nothing.
    /// </summary>
    [Fact]
    public void FullWeightOnThePrimaryIsAStrictNoOp()
    {
        var primary = Uniform(4, 4);

        var blended = CtcEmissionBlender.Blend(primary, Uniform(4, 4), Lower(), Upper(), 1.0);

        Assert.Same(primary, blended);
    }

    /// <summary>
    ///     The trellis compares absolute scores ACROSS frames, so an unnormalized
    ///     row would silently make some frames count for more than others.
    /// </summary>
    [Fact]
    public void EveryBlendedFrameIsStillANormalizedDistribution()
    {
        var primary = Emissions(6, 4, (f, s) => Math.Log(s == f % 4 ? 0.7 : 0.1));
        var secondary = Emissions(6, 4, (f, s) => Math.Log(s == (f + 1) % 4 ? 0.7 : 0.1));

        var blended = CtcEmissionBlender.Blend(primary, secondary, Lower(), Upper(), 0.5);

        for (var f = 0; f < blended.FrameCount; f++)
        {
            var total = 0.0;
            for (var s = 0; s < blended.Width; s++) total += Math.Exp(blended.LogProbs[f * blended.Width + s]);
            Assert.Equal(1.0, total, 4);
        }
    }

    /// <summary>
    ///     One head spells its alphabet lower-case and the other upper-case. They
    ///     still line up because <c>LyricVocabulary.FromTokens</c> folds every
    ///     token to lower case at construction — which is precisely what makes the
    ///     two alphabets comparable, so it is worth pinning here: if that folding
    ///     were dropped the blend would silently discard every letter and combine
    ///     nothing but the blank.
    /// </summary>
    [Fact]
    public void SymbolsAreMatchedAcrossCase()
    {
        var lower = Lower();
        var upper = Upper();
        lower.TryGetId('a', out var lowerA);

        // Spelled "A" in the vocabulary, reachable as 'a' — that IS the folding.
        Assert.True(upper.TryGetId('a', out var upperA));

        // The secondary is certain about 'A'; the primary is flat.
        var primary = Uniform(1, 4);
        var secondary = Emissions(1, 4, (_, s) => Math.Log(s == upperA ? 0.97 : 0.01));

        var blended = CtcEmissionBlender.Blend(primary, secondary, lower, upper, 0.5);

        // 'a' must now be the primary's most likely symbol — proof the opinion crossed.
        var best = 0;
        for (var s = 1; s < blended.Width; s++)
            if (blended.LogProbs[s] > blended.LogProbs[best]) best = s;

        Assert.Equal(lowerA, best);
    }

    /// <summary>
    ///     'c' exists only in the primary. Dropping it would make the blend refuse
    ///     a character the primary aligns perfectly well.
    /// </summary>
    [Fact]
    public void ASymbolTheOtherHeadLacksKeepsThePrimarysOpinion()
    {
        var lower = Lower();
        lower.TryGetId('c', out var lowerC);

        var primary = Emissions(1, 4, (_, s) => Math.Log(s == lowerC ? 0.97 : 0.01));
        var secondary = Uniform(1, 4);

        var blended = CtcEmissionBlender.Blend(primary, secondary, lower, Upper(), 0.5);

        var best = 0;
        for (var s = 1; s < blended.Width; s++)
            if (blended.LogProbs[s] > blended.LogProbs[best]) best = s;

        Assert.Equal(lowerC, best);
    }

    /// <summary>
    ///     Conv padding can leave two heads a frame apart on the same audio; the
    ///     blend covers what they both saw rather than reading past the end.
    /// </summary>
    [Fact]
    public void MismatchedFrameCountsBlendOverTheOverlap()
    {
        var blended = CtcEmissionBlender.Blend(Uniform(10, 4), Uniform(9, 4), Lower(), Upper(), 0.5);

        Assert.Equal(9, blended.FrameCount);
        Assert.Equal(9 * 4, blended.LogProbs.Length);
        Assert.Equal(0.02, blended.SecondsPerFrame);
    }

    [Fact]
    public void WeightIsClampedRatherThanTrusted()
    {
        var primary = Uniform(2, 4);

        Assert.Same(primary, CtcEmissionBlender.Blend(primary, Uniform(2, 4), Lower(), Upper(), 5.0));
        Assert.NotNull(CtcEmissionBlender.Blend(primary, Uniform(2, 4), Lower(), Upper(), -3.0));
    }
}
