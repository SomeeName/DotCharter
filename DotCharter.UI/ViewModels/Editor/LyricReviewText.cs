namespace DotCharter.UI.ViewModels.Editor;

/// <summary>The model score and localized review reasons for the selected syllable.</summary>
internal static class LyricReviewText
{
    private static readonly (LyricReviewReason Reason, string Key)[] Reasons =
    [
        (LyricReviewReason.LowScore, "Inspector_Lyrics_ReviewLowScore"),
        (LyricReviewReason.Interpolated, "Inspector_Lyrics_ReviewInterpolated"),
        (LyricReviewReason.CapHit, "Inspector_Lyrics_ReviewCapHit"),
        (LyricReviewReason.Compressed, "Inspector_Lyrics_ReviewCompressed"),
        (LyricReviewReason.Gap, "Inspector_Lyrics_ReviewGap"),
        (LyricReviewReason.Repaired, "Inspector_Lyrics_ReviewRepaired")
    ];

    internal static string Format(LyricEvent? lyric, ILocalizationService localization)
    {
        if (lyric?.AlignmentConfidence is not { } confidence) return "";
        var reasons = string.Join(", ", Reasons.Where(r => lyric.ReviewReasons.HasFlag(r.Reason)).Select(r => localization[r.Key]));
        return localization.Format("Inspector_Lyrics_ReviewDetails", confidence, reasons).Trim();
    }
}
