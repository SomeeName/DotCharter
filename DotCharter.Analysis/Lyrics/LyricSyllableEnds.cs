namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     One aligned syllable as <see cref="LyricSyllableEnds" /> reads it. <c>SpansToNext</c>
///     is the caller's span rule: whether the end may move toward the next onset.
/// </summary>
public readonly record struct LyricEndSyllable(double StartSeconds, double EndSeconds, int LineIndex, bool SpansToNext);

/// <summary>
///     Every syllable's END in seconds, for the app (<c>LyricAutoSyncService.BuildPhrases</c>)
///     and the lyric-sync CLI alike: the CTC end
///     clamped to the next onset and the longest syllable, then moved toward the latest
///     allowed end by the span rule, or by the voice when there is an envelope
///     (<see cref="VocalEndRefiner" />). Each caller passes what differs: its span rule,
///     and where a spanned end may reach (the beat grid's minimum gap in the app, a
///     fixed gap in seconds in the CLI). The app then applies its chart spacing within phrases.
/// </summary>
public static class LyricSyllableEnds
{
    /// <summary>
    ///     The ends, one per syllable. <paramref name="spanTarget" /> is the latest allowed
    ///     end, given a syllable's start and the next one's.
    /// </summary>
    public static double[] Compute(
        IReadOnlyList<LyricEndSyllable> syllables, Func<double, double, double> spanTarget,
        double maxSyllableSeconds, VocalEnvelope? envelope, VocalEndOptions options) =>
        ComputeWithReview(syllables, spanTarget, maxSyllableSeconds, envelope, options).Ends;

    /// <summary>Ends together with the syllables that reached their duration cap.</summary>
    public static LyricEndResult ComputeWithReview(
        IReadOnlyList<LyricEndSyllable> syllables, Func<double, double, double> spanTarget,
        double maxSyllableSeconds, VocalEnvelope? envelope, VocalEndOptions options)
    {
        ArgumentNullException.ThrowIfNull(syllables);
        ArgumentNullException.ThrowIfNull(spanTarget);
        ArgumentNullException.ThrowIfNull(options);

        var levels = envelope == null ? [] : PhraseLevels(syllables, envelope, options);
        var ends = new double[syllables.Count];
        var capHits = new bool[syllables.Count];
        for (var i = 0; i < syllables.Count; i++)
        {
            var s = syllables[i];
            var maxEnd = s.StartSeconds + maxSyllableSeconds;
            var hasNext = i + 1 < syllables.Count;

            var end = s.EndSeconds;
            if (hasNext) end = Math.Min(end, syllables[i + 1].StartSeconds);
            end = Math.Min(end, maxEnd);
            if (end < s.StartSeconds) end = s.StartSeconds;

            var spans = hasNext && s.SpansToNext;
            var lineFinal = !hasNext || syllables[i + 1].LineIndex != s.LineIndex;
            var naturalLimit = hasNext ? spanTarget(s.StartSeconds, syllables[i + 1].StartSeconds) : envelope?.DurationSeconds ?? maxEnd;
            var limit = Math.Min(naturalLimit, maxEnd);
            if (limit <= end)
                ends[i] = end;
            else if (envelope != null)
                ends[i] = RefineEnd(s, envelope, levels[s.LineIndex], end, limit, naturalLimit, maxEnd, spans, lineFinal, options);
            else
                ends[i] = spans ? limit : end;
            capHits[i] = ends[i] >= maxEnd - 1e-9;
        }

        return new LyricEndResult(ends, capHits);
    }

    /// <summary>
    ///     A line-final or cap-limited end is judged against the quieter of the syllable's own
    ///     level and the phrase's, so a soft held note is followed down to its fade. An in-line
    ///     end keeps the phrase level: measured, the local level only moved those ends further
    ///     from the hand-corrected ones.
    /// </summary>
    private static double RefineEnd(
        LyricEndSyllable syllable, VocalEnvelope envelope, float phraseLevel,
        double floor, double limit, double naturalLimit, double cap, bool spans, bool lineFinal, VocalEndOptions options)
    {
        var capLimited = naturalLimit > cap;
        var reference = phraseLevel;
        if (lineFinal || capLimited)
            reference = Math.Min(reference, VocalEndRefiner.ReferenceLevel(envelope, syllable.StartSeconds, syllable.EndSeconds, options));
        var end = VocalEndRefiner.Refine(envelope, reference, floor, limit, spans || lineFinal || capLimited, options);
        if (!capLimited || end < cap - 1e-9) return end;

        return VocalEndRefiner.Refine(envelope, reference, end, naturalLimit, true, options);
    }

    /// <summary>
    ///     The loudness each phrase is judged against, indexed by line: read over the
    ///     stretch from its first onset to its last CTC end.
    /// </summary>
    private static float[] PhraseLevels(IReadOnlyList<LyricEndSyllable> syllables, VocalEnvelope envelope, VocalEndOptions options)
    {
        var lines = syllables.Count == 0 ? 0 : syllables.Max(s => s.LineIndex) + 1;
        var first = new double[lines];
        var last = new double[lines];
        Array.Fill(first, double.MaxValue);
        foreach (var s in syllables)
        {
            first[s.LineIndex] = Math.Min(first[s.LineIndex], s.StartSeconds);
            last[s.LineIndex] = Math.Max(last[s.LineIndex], s.EndSeconds);
        }

        var levels = new float[lines];
        for (var line = 0; line < lines; line++)
        {
            if (first[line] != double.MaxValue)
                levels[line] = VocalEndRefiner.ReferenceLevel(envelope, first[line], last[line], options);
        }

        return levels;
    }
}

/// <summary>One result per input syllable; a cap hit remains a review signal after extension.</summary>
public sealed record LyricEndResult(double[] Ends, bool[] CapHits);
