using System.Globalization;
using System.Text;
using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics.Probes;

/// <summary>
///     Measures lyric auto-sync against the hand-synced probe corpus (#237): onsets
///     overall and by how a syllable opens, ends overall and phrase-final, each language
///     on the mix and on the vocal stem; then the vowel-initial onset shift and the
///     vocal-end thresholds as what-ifs over the same alignments. Skips unless
///     <see cref="LyricProbeCorpus.CorpusVariable" /> names a built corpus and a lyric
///     model is installed. The tables it writes are the ones in
///     <c>docs/agents/services/lyric-autosync.md</c>.
/// </summary>
public sealed class LyricCorpusProbeTests(ITestOutputHelper output)
{
    private static readonly double[] SilenceRatios = [0.30, 0.35, 0.40, 0.45, 0.50];
    private static readonly double[] MinSilenceSeconds = [0.030, 0.050, 0.080];
    private static readonly double[] VowelShifts = [0.0, -0.025, -0.050];

    private sealed record Run(LyricCorpusSong Song, LyricSyncResult Result);

    [Fact]
    public void MeasureCorpus()
    {
        var root = LyricProbeCorpus.Variable(LyricProbeCorpus.CorpusVariable);
        Assert.SkipWhen(root == null, $"Set {LyricProbeCorpus.CorpusVariable} to a built lyric corpus.");
        var songs = LyricProbeCorpus.Load(root);
        Assert.SkipWhen(songs.Count == 0, $"No corpus.json under {LyricProbeCorpus.CorpusVariable}.");

        var models = LyricProbeCorpus.Variable(LyricProbeCorpus.ModelsVariable);
        var service = new LyricAutoSyncService(FakeLocalization.English(), new FakeAppLogger(),
            vocalStemLocator: _ => null,
            audioLoader: (path, _) => LyricProbeCorpus.ReadAudio(path),
            modelRoots: models == null ? null : [models]);
        Assert.SkipUnless(service.IsModelInstalled, "No lyric model installed.");

        var ct = TestContext.Current.CancellationToken;
        var report = new StringBuilder();
        foreach (var language in songs.Select(s => s.Language).Distinct().Order())
        {
            var group = songs.Where(s => s.Language == language).ToList();
            var stemRuns = new List<Run>();
            foreach (var stem in new[] { false, true })
            {
                var runs = Align(service, root, group, stem, ct);
                if (stem) stemRuns = runs;
                var errors = runs.SelectMany(r => Errors(r, LyricAutoSyncOptions.Default)).ToList();
                report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"### {language}, {(stem ? "vocal stem" : "mix")}: {runs.Count} songs, {errors.Count:N0} syllables"));
                report.AppendLine($"Model: {string.Join(", ", runs.Select(r => r.Result.Model).Distinct())}");
                report.AppendLine();
                report.AppendLine(LyricProbeReport.Table(LyricProbeReport.Rows(errors)));
            }

            report.AppendLine(VowelShiftTable(language, stemRuns));
            report.AppendLine(ThresholdTable(language, stemRuns));
        }

        var text = report.ToString();
        output.WriteLine(text);
        if (LyricProbeCorpus.Variable(LyricProbeCorpus.ReportVariable) is { } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "lyric-corpus-probe.md"), text);
        }
    }

    private List<Run> Align(LyricAutoSyncService service, string root, List<LyricCorpusSong> songs, bool stem, CancellationToken ct)
    {
        var runs = new List<Run>();
        foreach (var song in songs)
        {
            var options = new LyricSyncOptions { VocalStemPath = stem ? LyricProbeCorpus.AudioPath(root, song.Slug, "vocals") : null };
            var result = service.Align(song.PasteText, LyricProbeCorpus.AudioPath(root, song.Slug, "mix"), options, null, ct);
            if (result.Ran && result.Aligned.Count == song.Syllables.Length) runs.Add(new Run(song, result));
            else output.WriteLine($"  {song.Title} ({(stem ? "stem" : "mix")}): {result.Error ?? "syllable count differs"}");
        }

        return runs;
    }

    /// <summary>The run's syllables folded onto its beat grid exactly as the app folds them, against the charter's.</summary>
    private static List<LyricSyllableError> Errors(Run run, LyricAutoSyncOptions options)
    {
        var beats = run.Song.BeatMarkers();
        var phrases = LyricAutoSyncService.BuildPhrases(run.Result.Sheet, run.Result.Aligned, beats, options, run.Result.Envelope);
        var placed = phrases.SelectMany(p => p.Lyrics)
            .Select(l => (l.Position.GetPositionMs(beats) / 1000.0, l.EndPosition.GetPositionMs(beats) / 1000.0))
            .ToList();
        return LyricProbeReport.Compare(run.Song.Syllables, placed);
    }

    /// <summary>
    ///     Vowel-initial onsets with a further shift on top of what the selected head
    ///     already applies: the onset moves linearly, so this is the run with that shift
    ///     (bar the rare start the trellis order would clamp to its predecessor's).
    /// </summary>
    private static string VowelShiftTable(string language, List<Run> runs)
    {
        var errors = runs.SelectMany(r => Errors(r, LyricAutoSyncOptions.Default)).ToList();
        var vowels = errors.Where(e => SyllableOnsetClassifier.Classify(e.Text) == SyllableOnsetClass.Vowel).ToList();
        var others = errors.Where(e => SyllableOnsetClassifier.Classify(e.Text) != SyllableOnsetClass.Vowel).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"#### {language}, vocal stem: a further vowel-initial onset shift");
        sb.AppendLine();
        sb.AppendLine(LyricProbeReport.Table(
        [
            ("consonant-initial (unshifted)", LyricProbeReport.Of([.. others.Select(e => e.Onset)])),
            .. VowelShifts.Select(shift => (string.Create(CultureInfo.InvariantCulture, $"vowel-initial, {shift * 1000:+0;-0;0} ms"),
                LyricProbeReport.Of([.. vowels.Select(e => e.Onset + shift)])))
        ]));
        return sb.ToString();
    }

    /// <summary>The vocal-end thresholds swept over the stem runs: only the end fold reruns.</summary>
    private static string ThresholdTable(string language, List<Run> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"#### {language}, vocal stem: vocal-end thresholds (silence = ratio of the phrase level, held for the minimum)");
        sb.AppendLine();
        sb.AppendLine("| ratio | min silence | all within 100 ms | all late > 50 ms | all p90 abs | phrase-final within 100 ms | phrase-final late > 50 ms | phrase-final median |");
        sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (ratio, hold) in SilenceRatios.SelectMany(r => MinSilenceSeconds.Select(h => (r, h))))
        {
            var options = LyricAutoSyncOptions.Default with
            {
                VocalEnd = VocalEndOptions.Default with { SilenceRatio = ratio, MinSilenceSeconds = hold }
            };
            var errors = runs.SelectMany(r => Errors(r, options)).ToList();
            var all = LyricProbeReport.Of([.. errors.Select(e => e.End)]);
            var final = LyricProbeReport.Of([.. errors.Where(e => e.PhraseFinal).Select(e => e.End)]);
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {ratio:0%} | {hold * 1000:0} ms | {all.Within100:0.0}% | {all.LateShare:0.0}% | {all.P90Absolute * 1000:0} ms | " +
                $"{final.Within100:0.0}% | {final.LateShare:0.0}% | {final.Median * 1000:+0;-0;0} ms |"));
        }

        return sb.ToString();
    }
}
