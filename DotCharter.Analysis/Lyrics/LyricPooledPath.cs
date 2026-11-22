namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Combines the forced path over the geometric blend with the one over the linear pool
///     (<see cref="CtcEmissions.Pooled" />). Where the two put a syllable within
///     <see cref="AgreementSeconds" /> of each other the blend's spans stay: it is the more
///     precise of the two. Where they disagree by more, the pool's spans win: that is a word only
///     one head heard, which the blend piled onto a neighbouring line or another repeat.
/// </summary>
internal static class LyricPooledPath
{
    /// <summary>Two paths this close agree, and the blend's sharper onset is kept.</summary>
    internal const double AgreementSeconds = 0.3;

    internal static CtcAlignment Merge(CtcAlignment blended, CtcAlignment pooled, LyricTokenization tokenization, double secondsPerFrame)
    {
        if (!pooled.Succeeded || pooled.Tokens.Count != blended.Tokens.Count) return blended;

        var limit = AgreementSeconds / secondsPerFrame;
        var spans = blended.Tokens.ToArray();
        foreach (var syllable in tokenization.Syllables)
        {
            if (syllable.TokenCount == 0) continue;
            var first = syllable.TokenStart;
            if (Math.Abs(blended.Tokens[first].StartFrame - pooled.Tokens[first].StartFrame) <= limit) continue;
            for (var t = first; t < first + syllable.TokenCount; t++) spans[t] = pooled.Tokens[t];
        }

        return new CtcAlignment(spans, blended.TotalLogProb, null);
    }
}
