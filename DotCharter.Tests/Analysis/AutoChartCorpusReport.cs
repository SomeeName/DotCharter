using System.Globalization;
using System.Text;
using DotCharter.Services.Import.Audio;
using static DotCharter.Tests.Analysis.AutoChartCorpus;

namespace DotCharter.Tests.Analysis;

/// <summary>The markdown half of the auto-chart corpus report. Everything here
/// is arithmetic over <see cref="AutoChartRow" />s; the CSV is
/// <see cref="AutoChartCorpus.Csv" />.</summary>
internal static class AutoChartCorpusReport
{
    public static string Markdown(IReadOnlyList<AutoChartRow> rows, int songs, IReadOnlyList<string> skipped,
        IReadOnlyList<string> timings, DateTime runAt)
    {
        var sb = new StringBuilder();
        var all = Sum(rows.Select(r => r.Score));
        sb.AppendLine("# Auto-chart corpus score");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Run {runAt:yyyy-MM-dd HH:mm}. Corpus `TestData/EOF`: {songs} songs, {rows.Count} arrangements, " +
                          $"{all.TruthOnsets:N0} charted onsets / {all.TruthNotes:N0} pitch-checkable charted notes."));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Input: each song's MIX WAV (no stems), the chart's own beat grid, the hardest level, " +
                          $"`ChartFromAudioAnalyzer.OptionsFor(arrangement, {ChartFromAudioAnalyzer.DefaultSensitivity})` " +
                          $"(MinConfidence {ChartFromAudioAnalyzer.MinConfidenceFor(ChartFromAudioAnalyzer.DefaultSensitivity):0.000}). " +
                          $"Onset window ±{ToleranceSeconds * 1000:0} ms; draft notes at their snapped grid position."));
        sb.AppendLine();

        sb.AppendLine("## Overall and per arrangement kind (±50 ms)");
        sb.AppendLine();
        sb.AppendLine("| | arr. | onset P | onset R | onset F1 | pitch acc. | pitch-class acc. | string/fret | note P | note R | note F1 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        sb.AppendLine(Line("**ALL**", rows.Count, all));
        foreach (var group in rows.GroupBy(r => r.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
            sb.AppendLine(Line(group.Key, group.Count(), Sum(group.Select(r => r.Score))));
        sb.AppendLine();

        sb.AppendLine("## Tolerance (ALL)");
        sb.AppendLine();
        sb.AppendLine("| window | onset F1 | note F1 |");
        sb.AppendLine("|---|---:|---:|");
        var s30 = Sum(rows.Select(r => r.Score30));
        var s100 = Sum(rows.Select(r => r.Score100));
        var raw = Sum(rows.Select(r => r.RawOnsets));
        sb.AppendLine($"| ±30 ms | {Pct(s30.OnsetF1)} | {Pct(s30.NoteF1)} |");
        sb.AppendLine($"| **±50 ms** | {Pct(all.OnsetF1)} | {Pct(all.NoteF1)} |");
        sb.AppendLine($"| ±100 ms | {Pct(s100.OnsetF1)} | {Pct(s100.NoteF1)} |");
        sb.AppendLine($"| ±50 ms, raw onset time (not snapped) | {Pct(raw.OnsetF1)} | {Pct(raw.NoteF1)} |");
        var unbiased = Sum(rows.Select(r => r.BiasCorrected));
        sb.AppendLine("| ±50 ms, raw time minus each arrangement's median offset (oracle diagnostic) | " +
                      $"{Pct(unbiased.OnsetF1)} | {Pct(unbiased.NoteF1)} |");
        sb.AppendLine();

        sb.AppendLine("## Spread over arrangements (±50 ms)");
        sb.AppendLine();
        sb.AppendLine("| metric | min | p25 | median | p75 | max |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        sb.AppendLine(Spread("onset F1", rows.Select(r => r.Score.OnsetF1)));
        sb.AppendLine(Spread("note F1", rows.Select(r => r.Score.NoteF1)));
        sb.AppendLine(Spread("pitch acc.", rows.Select(r => r.Score.PitchAccuracy)));
        sb.AppendLine(Spread("string/fret", rows.Select(r => r.Score.PositionAgreement)));
        var biases = rows.Select(r => r.BiasMs).Where(b => !double.IsNaN(b)).Order().ToList();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| median onset offset, ms (±100 ms match) | {Quantile(biases, 0):0} | {Quantile(biases, 0.25):0} | " +
            $"{Quantile(biases, 0.5):0} | {Quantile(biases, 0.75):0} | {Quantile(biases, 1):0} |"));
        sb.AppendLine();

        sb.AppendLine("## Where the notes go (ALL, ±50 ms)");
        sb.AppendLine();
        var misses = all.TruthNotesAtMatchedOnsets - all.PitchHits;
        var octave = all.PitchClassHits - all.PitchHits;
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Pitch accuracy at found onsets: single notes {Pct(all.SinglePitchAccuracy)} " +
                          $"({all.SingleNotesAtMatchedOnsets:N0}), chord tones {Pct(all.ChordPitchAccuracy)} ({all.ChordNotesAtMatchedOnsets:N0})."));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Pitch misses at found onsets: {misses:N0}; of them right pitch CLASS, wrong octave: " +
                          $"{octave:N0} ({Pct(misses == 0 ? 0 : (double)octave / misses)})."));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Drafted notes {all.DraftNotes:N0} vs charted {all.TruthNotes:N0}; drafted onsets {all.DraftOnsets:N0} " +
                          $"vs charted {all.TruthOnsets:N0}. Median signed onset error {all.MedianOnsetErrorMs:0.0} ms."));
        sb.AppendLine();

        sb.AppendLine("## Sensitivity sweep (ALL, ±50 ms) — same detection, only MinConfidence moves");
        sb.AppendLine();
        sb.AppendLine("| sensitivity | MinConfidence | draft notes | onset P | onset R | onset F1 | note P | note R | note F1 |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        for (var i = 0; i < SweepSensitivities.Length; i++)
        {
            var s = Sum(rows.Select(r => r.Sweep[i]));
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {SweepSensitivities[i]:0} | {ChartFromAudioAnalyzer.MinConfidenceFor(SweepSensitivities[i]):0.000} | " +
                              $"{s.DraftNotes:N0} | {Pct(s.OnsetPrecision)} | {Pct(s.OnsetRecall)} | {Pct(s.OnsetF1)} | " +
                              $"{Pct(s.NotePrecision)} | {Pct(s.NoteRecall)} | {Pct(s.NoteF1)} |"));
        }

        sb.AppendLine();

        sb.AppendLine("## Per arrangement (±50 ms)");
        sb.AppendLine();
        sb.AppendLine("| song / arrangement | kind | charted | drafted | onset F1 | pitch acc. | string/fret | note F1 | median err. |");
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            var s = r.Score;
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"| {r.Song} / {r.Arrangement} | {r.Kind} | {s.TruthNotes} | {s.DraftNotes} | {Pct(s.OnsetF1)} | " +
                              $"{Pct(s.PitchAccuracy)} | {Pct(s.PositionAgreement)} | {Pct(s.NoteF1)} | {s.MedianOnsetErrorMs:0} ms |"));
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Skipped");
            sb.AppendLine();
            foreach (var line in skipped) sb.AppendLine($"- {line}");
        }

        sb.AppendLine();
        sb.AppendLine("## Detection time per song (Debug build, informational)");
        sb.AppendLine();
        foreach (var line in timings) sb.AppendLine($"- {line}");
        return sb.ToString();
    }

    private static string Line(string label, int count, AutoChartScore s) =>
        $"| {label} | {count} | {Pct(s.OnsetPrecision)} | {Pct(s.OnsetRecall)} | {Pct(s.OnsetF1)} | {Pct(s.PitchAccuracy)} | " +
        $"{Pct(s.PitchClassAccuracy)} | {Pct(s.PositionAgreement)} | {Pct(s.NotePrecision)} | {Pct(s.NoteRecall)} | {Pct(s.NoteF1)} |";

    private static string Spread(string label, IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return $"| {label} | {Pct(Quantile(sorted, 0))} | {Pct(Quantile(sorted, 0.25))} | {Pct(Quantile(sorted, 0.5))} | " +
               $"{Pct(Quantile(sorted, 0.75))} | {Pct(Quantile(sorted, 1))} |";
    }
}
