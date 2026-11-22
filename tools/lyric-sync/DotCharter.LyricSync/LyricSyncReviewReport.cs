using System.Globalization;
using System.Text;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.LyricSync;

/// <summary>
///     The console half of the review flags: the syllables the app would mark for
///     review (<c>LyricAlignmentReview</c>, the #627 repairs, the 4 s cap), counted by
///     reason and listed with their times so a CLI user can find the doubtful places.
///     The vocals XML carries the same flags as comments and <c>--json</c> carries all of them.
/// </summary>
internal static class LyricSyncReviewReport
{
    /// <summary>The console lists this many; the rest are counted and left to <c>--json</c>.</summary>
    internal const int MaxListed = 20;

    private static readonly LyricReviewReason[] Reasons =
    [
        LyricReviewReason.LowScore, LyricReviewReason.Interpolated, LyricReviewReason.CapHit,
        LyricReviewReason.Compressed, LyricReviewReason.Gap, LyricReviewReason.Repaired
    ];

    public static void Append(StringBuilder sb, IReadOnlyList<LyricSyncSyllable> syllables)
    {
        var flagged = syllables.Where(s => s.ReviewReasons != LyricReviewReason.None).ToList();
        if (flagged.Count == 0)
        {
            sb.Append("Review: no syllables flagged.\n");
            return;
        }

        var counts = Reasons
            .Select(r => (Reason: r, Count: flagged.Count(s => s.ReviewReasons.HasFlag(r))))
            .Where(c => c.Count > 0)
            .Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Reason} {c.Count}"));
        sb.Append(CultureInfo.InvariantCulture, $"Review: {flagged.Count} syllables flagged ({string.Join(", ", counts)}).\n");

        foreach (var s in flagged.Take(MaxListed))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"  {s.StartSeconds,8:0.000} s  line {s.LineIndex + 1}  '{s.Text}'  {s.ReviewReasons}\n");
        }

        if (flagged.Count > MaxListed)
            sb.Append(CultureInfo.InvariantCulture, $"  ... and {flagged.Count - MaxListed} more; --json lists every one.\n");
    }
}
