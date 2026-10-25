using System.Globalization;
using System.Text;
using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics.Probes;

/// <summary>One placed syllable against the charter's: signed seconds, positive = the sync is LATE.</summary>
public sealed record LyricSyllableError(string Text, bool PhraseFinal, double Onset, double End);

/// <summary>A signed error distribution, as the lyric probes report it.</summary>
public sealed record LyricErrorStats(
    int Count, double Median, double P90Absolute, double Within50, double Within100, double LateShare, double EarlyShare);

/// <summary>
///     The numbers the lyric probes report (#237), pure so the default gate pins them.
///     Errors are SIGNED because the asymmetry is perceptual: a lyric early by a third of
///     a second is tolerated where one late by a fifth is not, so a sync biased late is
///     worse than its absolute error says. "Late" is more than <see cref="LateSeconds" />.
/// </summary>
public static class LyricProbeReport
{
    public const double LateSeconds = 0.050;

    /// <summary>
    ///     Index-aligned errors, or empty when the counts differ: the sheet is built FROM the
    ///     truth, so a mismatch means the parser split differently and a comparison would be
    ///     meaningless rather than merely noisy.
    /// </summary>
    public static List<LyricSyllableError> Compare(
        IReadOnlyList<LyricTruthSyllable> truth, IReadOnlyList<(double Start, double End)> placed)
    {
        if (placed.Count != truth.Count) return [];
        return
        [
            .. truth.Select((t, i) =>
                new LyricSyllableError(t.Text, t.EndsLine, placed[i].Start - t.Start, placed[i].End - t.End))
        ];
    }

    public static LyricErrorStats Of(IReadOnlyCollection<double> signed)
    {
        if (signed.Count == 0) return new LyricErrorStats(0, 0, 0, 0, 0, 0, 0);
        var sorted = signed.Order().ToList();
        var absolute = signed.Select(Math.Abs).Order().ToList();
        double Share(Func<double, bool> pick) => 100.0 * signed.Count(pick) / signed.Count;
        return new LyricErrorStats(
            signed.Count, At(sorted, 0.5), At(absolute, 0.9),
            Share(e => Math.Abs(e) <= 0.050), Share(e => Math.Abs(e) <= 0.100),
            Share(e => e > LateSeconds), Share(e => e < 0));
    }

    /// <summary>
    ///     The report's rows for one run: onsets overall and by how the syllable opens
    ///     (<see cref="SyllableOnsetClassifier" />), then ends overall and phrase-final.
    /// </summary>
    public static List<(string Label, LyricErrorStats Stats)> Rows(IReadOnlyList<LyricSyllableError> errors) =>
    [
        ("onset, all", Of([.. errors.Select(e => e.Onset)])),
        .. Enum.GetValues<SyllableOnsetClass>().Select(c => ($"onset, {c.ToString().ToLowerInvariant()}-initial",
            Of([.. errors.Where(e => SyllableOnsetClassifier.Classify(e.Text) == c).Select(e => e.Onset)]))),
        ("end, all", Of([.. errors.Select(e => e.End)])),
        ("end, phrase-final", Of([.. errors.Where(e => e.PhraseFinal).Select(e => e.End)]))
    ];

    /// <summary>A markdown table: one row per <paramref name="rows" /> entry, times in ms.</summary>
    public static string Table(IEnumerable<(string Label, LyricErrorStats Stats)> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| | n | median | p90 abs | within 50 ms | within 100 ms | late > 50 ms | early |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (label, s) in rows)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {label} | {s.Count:N0} | {s.Median * 1000:+0;-0;0} ms | {s.P90Absolute * 1000:0} ms | " +
                $"{s.Within50:0.0}% | {s.Within100:0.0}% | {s.LateShare:0.0}% | {s.EarlyShare:0.0}% |"));
        }

        return sb.ToString();
    }

    private static double At(List<double> sorted, double p) =>
        sorted[(int)Math.Clamp(p * (sorted.Count - 1), 0, sorted.Count - 1)];
}
