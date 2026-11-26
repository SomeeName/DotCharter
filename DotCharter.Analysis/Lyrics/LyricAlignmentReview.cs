using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     Pure plausibility checks over the measured CTC spans, before ends are spanned and before
///     the fold enforces the minimum length (so a squeezed pair stays flagged after it is spread).
/// </summary>
public static class LyricAlignmentReview
{
    public const double LowConfidenceThreshold = 0.05;
    /// <summary>A blank stretch longer than this inside a line, not held by the voice, is a <c>Gap</c>.</summary>
    internal const double MaxBlankGapSeconds = 1.0;
    private const double CompressedOnsetSeconds = 0.080;
    private const double CompressedSpanSeconds = 0.040;

    /// <summary>
    ///     Review reasons parallel to the sheet; existing reasons are never removed. With a stem
    ///     <paramref name="envelope" />, a long blank stretch the voice holds through is a held
    ///     note, not a gap.
    /// </summary>
    public static LyricReviewReason[] Evaluate(
        LyricSheet sheet, IReadOnlyList<AlignedSyllable> aligned, VocalEnvelope? envelope = null)
    {
        var reasons = new LyricReviewReason[aligned.Count];
        for (var i = 0; i < aligned.Count; i++)
        {
            var s = aligned[i];
            reasons[i] |= s.ReviewReasons;
            if (s.IsInterpolated) reasons[i] |= LyricReviewReason.Interpolated;
            if (s.Confidence < LowConfidenceThreshold) reasons[i] |= LyricReviewReason.LowScore;
            if (i + 1 == aligned.Count) continue;

            // Compressed is judged on the aligner's RAW spans, across a line end too: the fold
            // later spreads such a pair to the shared minimum (VocalTiming), and the flag must
            // still point the user at it.
            var next = aligned[i + 1];
            if (s.EndSeconds - s.StartSeconds <= CompressedSpanSeconds + 1e-9 &&
                next.StartSeconds - s.StartSeconds <= CompressedOnsetSeconds + 1e-9)
            {
                reasons[i] |= LyricReviewReason.Compressed;
                reasons[i + 1] |= LyricReviewReason.Compressed;
            }

            if (sheet.Syllables[i].LineIndex != sheet.Syllables[i + 1].LineIndex) continue;
            if (next.StartSeconds - s.EndSeconds > MaxBlankGapSeconds && !IsHeld(sheet, aligned, envelope, i))
            {
                reasons[i] |= LyricReviewReason.Gap;
                reasons[i + 1] |= LyricReviewReason.Gap;
            }
        }

        return reasons;
    }

    /// <summary>Whether the stem sounds without a rest from this onset to the next one in its line.</summary>
    private static bool IsHeld(LyricSheet sheet, IReadOnlyList<AlignedSyllable> aligned, VocalEnvelope? envelope, int i)
    {
        if (envelope == null) return false;

        var line = sheet.Syllables[i].LineIndex;
        var first = i;
        while (first > 0 && sheet.Syllables[first - 1].LineIndex == line) first--;
        var last = i + 1;
        while (last + 1 < aligned.Count && sheet.Syllables[last + 1].LineIndex == line) last++;
        var before = first > 0 ? aligned[first - 1].EndSeconds : 0;
        var after = last + 1 < aligned.Count ? aligned[last + 1].StartSeconds : envelope.DurationSeconds;
        var voice = LyricVocalRegions.Find(envelope, before, after, CancellationToken.None);
        var region = voice?.RegionOf(aligned[i].StartSeconds) ?? -1;
        return region >= 0 && region == voice!.RegionOf(aligned[i + 1].StartSeconds);
    }
}
