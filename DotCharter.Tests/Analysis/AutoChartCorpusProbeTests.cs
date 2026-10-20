using System.Diagnostics;
using DotCharter.Analysis.AudioToChart;
using DotCharter.Services.Import.Audio;
using DotCharter.Services.Parsing.Xml;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     The auto-chart corpus SCORE the roadmap asks for before anything new is
///     built on Pillar A ("no new surface before a corpus number exists", #296).
///     <para>
///     It runs the product's draft — <see cref="AudioToChartAnalyzer" /> with the
///     exact options Tools → Chart from audio uses
///     (<see cref="ChartFromAudioAnalyzer.OptionsFor" /> at
///     <see cref="ChartFromAudioAnalyzer.DefaultSensitivity" />) — over
///     <c>TestData/EOF</c>'s hand-corrected charts, aligns each draft with the
///     arrangement's hardest level (<see cref="AutoChartScorer" />) and reports
///     onset, pitch and string/fret agreement per arrangement and overall.
///     The detector runs ONCE per song and every arrangement of it is drafted
///     from the same events (<c>DetectEvents</c> + <c>DraftFromEvents</c>, which
///     is exactly <c>Analyze</c>).
///     </para>
///     <para>
///     <b>It asserts nothing about accuracy.</b> The corpus is git-ignored, so a
///     threshold would be vacuous in every tree but the owner's. It is skipped
///     unless <c>DOTCHARTER_AUTOCHART_CORPUS=1</c> (CPU-heavy: whole songs
///     through the managed detector, Debug build). What it DOES assert: that it
///     measured something, that the report directory is outside the repo, and —
///     given <c>DOTCHARTER_AUTOCHART_CORPUS_COMPARE</c> — that the CSV is byte
///     identical to an earlier run's (the determinism check; the CSV carries a
///     per-draft fingerprint).
///     </para>
///     <para>
///     Audio: what the wizard listens to for that arrangement — the song's own mix
///     <c>.wav</c> (never a preview), or the instrument's already separated LOSSLESS
///     stem when <see cref="ChartFromAudioStemLocator" /> finds one in the app's stem
///     cache (read-only; never Opus). No stem separation, no GPU. The stems that DO
///     exist cover excerpts only, and are scored by <c>AutoChartStemSubsetProbeTests</c>.
///     Full method and the published numbers: <c>docs/agents/testing/autochart-eval.md</c>.
///     </para>
/// </summary>
public sealed class AutoChartCorpusProbeTests(ITestOutputHelper output)
{
    private const string EnvVar = "DOTCHARTER_AUTOCHART_CORPUS";

    /// <summary>Cap on how many SONG FOLDERS to walk. Absent = all of them.</summary>
    private const string LimitEnvVar = "DOTCHARTER_AUTOCHART_CORPUS_LIMIT";

    /// <summary>Directory for <c>report.md</c> + <c>scores.csv</c>; must be outside the repo.</summary>
    private const string OutEnvVar = "DOTCHARTER_AUTOCHART_CORPUS_OUT";

    /// <summary>An earlier run's <c>scores.csv</c>: this run must reproduce it byte for byte.</summary>
    private const string CompareEnvVar = "DOTCHARTER_AUTOCHART_CORPUS_COMPARE";

    /// <summary>Songs analysed concurrently (default 1). Each song is independent,
    /// and rows are reported in folder order whatever the interleaving.</summary>
    private const string DopEnvVar = "DOTCHARTER_AUTOCHART_CORPUS_DOP";

    private sealed record SongResult(List<AutoChartRow> Rows, List<string> Skipped, string? Timing);

    [Fact]
    public void ScoreTheDraftAgainstTheEofGroundTruth()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(EnvVar) != "1",
            $"Corpus score: set {EnvVar}=1 to run it (whole songs through the detector).");

        var root = FindTestDataRoot();
        Assert.SkipWhen(root is null, "No TestData/ (it is git-ignored).");
        var eof = Path.Combine(root!, "EOF");
        Assert.SkipWhen(!Directory.Exists(eof), "No TestData/EOF ground-truth corpus.");

        var outDir = Environment.GetEnvironmentVariable(OutEnvVar);
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            outDir = Path.GetFullPath(outDir);
            var repo = Path.GetFullPath(Path.GetDirectoryName(root!)!);
            Assert.False(outDir.StartsWith(repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                         || outDir.Equals(repo, StringComparison.OrdinalIgnoreCase),
                $"{OutEnvVar} must be OUTSIDE the repo ({repo}); got {outDir}.");
        }

        var limit = int.TryParse(Environment.GetEnvironmentVariable(LimitEnvVar), out var parsed) && parsed > 0
            ? parsed
            : int.MaxValue;
        var dop = int.TryParse(Environment.GetEnvironmentVariable(DopEnvVar), out var d) && d > 0 ? d : 1;

        var songDirs = Directory.EnumerateDirectories(eof)
            .Where(dir => FindMixWav(dir) != null)
            .OrderBy(dir => dir, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        var results = new SongResult[songDirs.Count];
        Parallel.For(0, songDirs.Count, new ParallelOptions { MaxDegreeOfParallelism = dop },
            i => results[i] = ScoreSong(songDirs[i]));

        var rows = results.SelectMany(r => r.Rows).ToList();
        var skipped = results.SelectMany(r => r.Skipped).ToList();
        var timings = results.Select(r => r.Timing).OfType<string>().ToList();

        // A corpus that produced nothing is DARKNESS, not a pass.
        Assert.SkipWhen(rows.Count == 0,
            $"TestData/EOF present but nothing measurable in it ({skipped.Count} skipped).");

        var csv = AutoChartCorpus.Csv(rows);
        var markdown = AutoChartCorpusReport.Markdown(rows, songDirs.Count, skipped, timings, DateTime.Now);
        output.WriteLine(markdown);

        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "scores.csv"), csv);
            File.WriteAllText(Path.Combine(outDir, "report.md"), markdown);
            output.WriteLine($"Report written to {outDir}");
        }

        var compare = Environment.GetEnvironmentVariable(CompareEnvVar);
        if (!string.IsNullOrWhiteSpace(compare))
        {
            var earlier = File.ReadAllText(compare).Split('\n');
            var now = csv.Split('\n');
            var firstDiff = Enumerable.Range(0, Math.Max(earlier.Length, now.Length))
                .FirstOrDefault(i => i >= earlier.Length || i >= now.Length || earlier[i] != now[i], -1);
            Assert.True(firstDiff < 0,
                $"Not deterministic: scores.csv differs from {compare} at line {firstDiff + 1}.");
            output.WriteLine($"Determinism: scores.csv identical to {compare} ({now.Length - 2} rows).");
        }
    }

    private static SongResult ScoreSong(string songDir)
    {
        var song = Path.GetFileName(songDir);
        var rows = new List<AutoChartRow>();
        var skipped = new List<string>();

        var charts = LoadCharts(songDir, skipped);

        if (charts.Count == 0) return new SongResult(rows, skipped, null);

        // What the dialog listens to for each arrangement: the instrument's already
        // separated lossless stem when the app's stem cache holds one for this exact
        // audio (read-only), else the mix. Detection runs once per distinct source.
        var mix = FindMixWav(songDir)!;
        var locator = new ChartFromAudioStemLocator();
        var detected = new Dictionary<string, (List<GuitarAudioEvent> Events, CqtPitchEvidence Cqt)>(StringComparer.Ordinal);
        var timings = new List<string>();

        foreach (var (name, project, arrangement) in charts)
        {
            var stem = locator.TryLocate(mix, arrangement.ArrangementType);
            var sourceKey = stem == null ? "mix" : stem.Path + "|" + stem.ArchiveEntry;
            if (!detected.TryGetValue(sourceKey, out var source))
            {
                float[] samples;
                int sampleRate;
                try
                {
                    (samples, sampleRate) = stem == null ? WavMonoReader.Read(mix) : WavMonoReader.Read(stem.ReadWavBytes());
                }
                catch (Exception ex)
                {
                    skipped.Add($"{song} / {name}: audio {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                var clock = Stopwatch.StartNew();
                var detectedEvents = new AudioToChartAnalyzer().DetectEvents(samples, sampleRate);
                var detection = clock.Elapsed.TotalSeconds;
                // The pitch stage's CQT depends only on the audio too: once per source.
                source = (detectedEvents, CqtPitchEvidence.FromWholeClip(samples, sampleRate));
                detected[sourceKey] = source;
                timings.Add($"{song}{(stem == null ? "" : " (" + stem.StemName + " stem)")}: " +
                            $"{samples.Length / (double)sampleRate:0} s of audio, detection {detection:0} s, " +
                            $"CQT {clock.Elapsed.TotalSeconds - detection:0} s");
            }

            var (events, cqt) = source;

            var truth = AutoChartCorpus.GroundTruth(arrangement, project.Beats);
            if (truth.Count == 0)
            {
                skipped.Add($"{song} / {name}: no charted sounds");
                continue;
            }

            var beats = project.Beats;
            var draft = AudioToChartAnalyzer.DraftFromEvents(events, beats,
                ChartFromAudioAnalyzer.OptionsFor(arrangement, ChartFromAudioAnalyzer.DefaultSensitivity), cqt);
            var snapped = AutoChartCorpus.DraftSounds(draft, beats, snapped: true);
            var sweep = AutoChartCorpus.SweepSensitivities
                .Select(s => AutoChartScorer.Score(truth,
                    AutoChartCorpus.DraftSounds(
                        AudioToChartAnalyzer.DraftFromEvents(events, beats, ChartFromAudioAnalyzer.OptionsFor(arrangement, s), cqt),
                        beats, snapped: true),
                    AutoChartCorpus.ToleranceSeconds))
                .ToList();

            var raw = AutoChartCorpus.DraftSounds(draft, beats, snapped: false);
            var (unbiased, biasMs) = AutoChartCorpus.RemoveBias(truth, raw);
            rows.Add(new AutoChartRow(
                song,
                stem == null ? name : name + " (" + stem.StemName + " stem)",
                arrangement.ArrangementType.ToString(),
                AutoChartScorer.Score(truth, snapped, AutoChartCorpus.ToleranceSeconds),
                AutoChartScorer.Score(truth, snapped, 0.030),
                AutoChartScorer.Score(truth, snapped, 0.100),
                AutoChartScorer.Score(truth, raw, AutoChartCorpus.ToleranceSeconds),
                AutoChartScorer.Score(truth, unbiased, AutoChartCorpus.ToleranceSeconds),
                sweep,
                AutoChartCorpus.Fingerprint(draft),
                biasMs));
        }

        return new SongResult(rows, skipped, timings.Count == 0 ? null : string.Join("; ", timings));
    }

    /// <summary>Every instrument arrangement XML in a corpus song folder, in file order;
    /// what it cannot use goes to <paramref name="skipped" /> with the reason.</summary>
    internal static List<(string Name, ChartProject Project, Arrangement Arrangement)> LoadCharts(string songDir,
        List<string> skipped)
    {
        var song = Path.GetFileName(songDir);
        var charts = new List<(string Name, ChartProject Project, Arrangement Arrangement)>();
        foreach (var xml in Directory.EnumerateFiles(songDir, "*_RS2.xml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (xml.EndsWith(".debug.xml", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(xml);
            try
            {
                var project = new XmlChartSerializer().Load(xml);
                var arrangement = project.Arrangements.FirstOrDefault();
                if (arrangement == null || arrangement.ArrangementType == ArrangementType.Vocal)
                    skipped.Add($"{song} / {name}: not an instrument arrangement");
                else if (project.Beats.Count == 0)
                    skipped.Add($"{song} / {name}: no beat grid");
                else
                    charts.Add((name, project, arrangement));
            }
            catch (Exception ex)
            {
                skipped.Add($"{song} / {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return charts;
    }

    /// <summary>The song's own mix WAV — never a preview (<c>guitar_preview.wav</c>,
    /// <c>guitar.preview.wav</c>) and never a stem.</summary>
    internal static string? FindMixWav(string songDir)
        => Directory.EnumerateFiles(songDir, "*.wav")
            .Where(f => !Path.GetFileName(f).Contains("preview", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();

    internal static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }
}
