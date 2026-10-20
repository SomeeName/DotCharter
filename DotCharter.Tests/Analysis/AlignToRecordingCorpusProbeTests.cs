using System.Globalization;
using System.Xml.Linq;
using DotCharter.Analysis.BeatMap;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Analysis;

/// <summary>
///     Align to recording (#784, #792) on REAL songs: every <c>TestData/EOF</c> folder holding a Guitar Pro
///     tab, its recording (<c>*.wav</c>) and a finished Rocksmith arrangement XML. The tab is imported on its
///     written tempo (the grid the import builds), aligned to the recording, and every aligned beat is
///     measured against the NEAREST beat of the hand-synced reference grid (its <c>&lt;ebeat&gt;</c>s), inside
///     the reference's span. Reports median / p90 per song and overall, before and after, for every option
///     set in <see cref="Variants" /> (features are computed once per song and shared), and the coarse-warp
///     candidates' cost per chart frame next to their own accuracy (what <c>CoarseWarp.Choose</c> picks from).
///     <para>It also counts the corpus songs that have instrument stems already in the app's stem cache
///     (never separating) and, for those, measures the stems too. <c>DOTCHARTER_ALIGN_DUMP=&lt;folder outside the
///     repo&gt;</c> writes one CSV per song: every beat's written, coarse and aligned time and signed error.</para>
///     <para>Env-gated (<c>DOTCHARTER_ALIGN_CORPUS=1</c>) and it never asserts a quality number: the corpus is
///     git-ignored, so a threshold would be vacuous in every tree but the owner's. Nearest-beat error cannot
///     see a whole-beat slip; it says whether beats sit ON the performance's beats.</para>
/// </summary>
public sealed class AlignToRecordingCorpusProbeTests(ITestOutputHelper output)
{
    private const string EnvVar = "DOTCHARTER_ALIGN_CORPUS";

    /// <summary>The option sets compared: what #784 shipped with, and the current defaults.</summary>
    private static readonly (string Name, Func<Input, IReadOnlyList<double>> Run)[] Variants =
    [
        ("#784", i => Run(i, RecordingAlignerDenseMixTests.Shipped784)),
        ("#792", i => Run(i, new RecordingAlignmentOptions()))
    ];

    private static IReadOnlyList<double> Run(Input i, RecordingAlignmentOptions options) =>
        RecordingAligner.Align(i.Features, i.Plan.Beats, i.Plan.Events, i.Sections, options, i.Ct).BeatTimesMs;

    private sealed record Input(RecordingAlignmentFeatures Features, RecordingAlignmentPlan Plan, int[] Sections, CancellationToken Ct);

    [Fact]
    public void AlignEveryTabToItsRecording_AndMeasureAgainstTheHandSyncedGrid()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(EnvVar) != "1", $"Set {EnvVar}=1 to run the align-to-recording corpus probe.");
        var root = AutoChartCorpusProbeTests.FindTestDataRoot();
        Assert.SkipWhen(root is null, "No TestData/ (it is git-ignored).");
        var eof = Path.Combine(root!, "EOF");
        Assert.SkipWhen(!Directory.Exists(eof), "No TestData/EOF corpus.");
        var ct = TestContext.Current.CancellationToken;

        var before = new List<double>();
        var after = Variants.Select(_ => new List<double>()).ToArray();
        var lines = new List<string>();
        var candidateLines = new List<string>();
        var songs = 0;
        var withStems = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(eof).OrderBy(d => d, StringComparer.Ordinal))
        {
            var tab = PickTab(dir);
            var wav = AutoChartCorpusProbeTests.FindMixWav(dir);
            var xml = Directory.EnumerateFiles(dir, "*.xml")
                .Where(f => !f.EndsWith(".debug.xml", StringComparison.OrdinalIgnoreCase) && XDocument.Load(f).Root?.Element("ebeats") != null)
                .Order(StringComparer.Ordinal).FirstOrDefault();
            if (tab == null || wav == null || xml == null) continue;

            var song = Path.GetFileName(dir);
            songs++;
            try
            {
                var stems = CachedInstrumentStems(wav);
                if (stems.Count > 0) withStems.Add(song);
                var plan = Plan(tab);
                var reference = Reference(xml);
                var b = Nearest(plan.OldTimes, reference);
                before.AddRange(b);
                var (mono, rate, _) = RecordingAlignmentAudio.Load(wav, ct);
                var cells = MeasureVariants(plan, mono, rate, reference, after, ct);
                candidateLines.Add($"| {song} | {CandidateCells(plan, mono, rate, reference, ct)} |");
                if (Environment.GetEnvironmentVariable("DOTCHARTER_ALIGN_DUMP") is { Length: > 0 } dump) Dump(dump, song, plan, mono, rate, reference, ct);
                lines.Add($"| {song} | {b.Count} | {Stat(b, 0.5)} / {Stat(b, 0.9)} | {cells} |");
                if (stems.Count > 0)
                {
                    var (stemMono, stemRate) = RecordingAlignmentAudio.SumStems(stems, ct);
                    lines.Add($"| {song} (stems) | | | {MeasureVariants(plan, stemMono, stemRate, reference, null, ct)} |");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lines.Add($"| {song} | failed: {ex.GetType().Name}: {ex.Message} | | |");
            }
        }

        Assert.SkipWhen(before.Count == 0, "TestData/EOF present but no folder has a tab, a recording and a reference XML.");
        output.WriteLine($"Songs: {songs}; with cached instrument stems: {withStems.Count} ({string.Join(", ", withStems)})");
        output.WriteLine($"| Song | Beats | Before median / p90 ms | {string.Join(" | ", Variants.Select(v => v.Name + " median / p90"))} |");
        output.WriteLine($"|---|---|---|{string.Concat(Variants.Select(_ => "---|"))}");
        foreach (var line in lines) output.WriteLine(line);
        output.WriteLine("");
        output.WriteLine("Coarse candidates: cost per chart frame, coarse median / p90 ms");
        foreach (var line in candidateLines) output.WriteLine(line);
        output.WriteLine($"| **All** | {before.Count} | {Stat(before, 0.5)} / {Stat(before, 0.9)} | " +
                         $"{string.Join(" | ", after.Select(a => $"{Stat(a, 0.5)} / {Stat(a, 0.9)}"))} |");
    }

    private static string MeasureVariants(RecordingAlignmentPlan plan, float[] mono, int rate, double[] reference,
        List<double>[]? totals, CancellationToken ct)
    {
        var features = RecordingAlignmentFeatures.Compute(mono, rate, new RecordingAlignmentOptions().CoarseHopMs, ct);
        var input = new Input(features, plan, plan.Sections.Select(s => s.StartBeat).ToArray(), ct);
        var cells = new List<string>(Variants.Length);
        for (var v = 0; v < Variants.Length; v++)
        {
            var a = Nearest(Variants[v].Run(input), reference);
            totals?[v].AddRange(a);
            cells.Add($"{Stat(a, 0.5)} / {Stat(a, 0.9)}");
        }

        return string.Join(" | ", cells);
    }

    /// <summary>Per beat: the written time, the coarse estimate, the aligned time and each one's SIGNED distance
    ///     to the nearest reference beat, as CSV in <paramref name="folder" /> (outside the repo).</summary>
    private static void Dump(string folder, string song, RecordingAlignmentPlan plan, float[] mono, int rate, double[] reference, CancellationToken ct)
    {
        var options = new RecordingAlignmentOptions();
        var features = RecordingAlignmentFeatures.Compute(mono, rate, options.CoarseHopMs, ct);
        var coarse = CoarseWarp.Estimate(features, plan.OldTimes, RecordingAligner.UsableEvents(plan.Events, plan.Beats.Length), options, ct);
        var aligned = RecordingAligner.Align(features, plan.Beats, plan.Events, plan.Sections.Select(s => s.StartBeat).ToArray(), options, ct).BeatTimesMs;
        var lines = new List<string> { "beat,old,coarse,aligned,old_err,coarse_err,aligned_err,coarse_beat_ms" };
        for (var i = 0; i < plan.OldTimes.Length; i++)
        {
            lines.Add(string.Join(',', new[] { i, plan.OldTimes[i], coarse.Times[i], aligned[i], Signed(plan.OldTimes[i], reference),
                Signed(coarse.Times[i], reference), Signed(aligned[i], reference), coarse.BeatMs[i] }.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture))));
        }

        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, song + ".csv"), lines);
    }

    private static double Signed(double t, double[] reference)
    {
        var i = Array.BinarySearch(reference, t);
        if (i >= 0) return 0;
        i = ~i;
        if (i == 0) return t - reference[0];
        if (i == reference.Length) return t - reference[^1];
        return t - reference[i - 1] < reference[i] - t ? t - reference[i - 1] : t - reference[i];
    }

    [Fact]
    public void PickTab_PrefersTheTabNamedAfterTheSong()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dc-align-picktab-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "Artist - Second Song");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Artist - First Song.gp5"), "");
            File.WriteAllText(Path.Combine(dir, "Artist - Second Song.gp5"), "");
            Assert.Equal("Artist - Second Song.gp5", Path.GetFileName(PickTab(dir)));

            File.Delete(Path.Combine(dir, "Artist - Second Song.gp5"));
            Assert.Equal("Artist - First Song.gp5", Path.GetFileName(PickTab(dir)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The folder's tab: one named after the song when the folder holds several (one corpus folder
    ///     also carries another song's tab, which the ordinal pick used to align), else the first by name.</summary>
    internal static string? PickTab(string dir)
    {
        var tabs = Directory.EnumerateFiles(dir, "*.gp").Concat(Directory.EnumerateFiles(dir, "*.gp5")).Order(StringComparer.Ordinal).ToList();
        var folder = Path.GetFileName(dir);
        var title = folder[(folder.IndexOf(" - ", StringComparison.Ordinal) + 1)..].Trim(' ', '-');
        return tabs.FirstOrDefault(t => Path.GetFileNameWithoutExtension(t).Contains(title, StringComparison.OrdinalIgnoreCase)) ?? tabs.FirstOrDefault();
    }

    private static string CandidateCells(RecordingAlignmentPlan plan, float[] mono, int rate, double[] reference, CancellationToken ct)
    {
        var options = new RecordingAlignmentOptions();
        var features = RecordingAlignmentFeatures.Compute(mono, rate, options.CoarseHopMs, ct);
        var events = RecordingAligner.UsableEvents(plan.Events, plan.Beats.Length);
        if (options.LetRing) events = RecordingAligner.LetRing(events, options.RingCapBeats);
        var candidates = CoarseWarp.Candidates(features, plan.OldTimes, events, options, ct);
        return string.Join(" | ", candidates.Select(c =>
        {
            var e = Nearest(c.Estimate.Times, reference);
            return string.Create(CultureInfo.InvariantCulture, $"{c.Name} {c.CostPerFrame:0.0000}: {Stat(e, 0.5)} / {Stat(e, 0.9)}");
        }));
    }

    private static RecordingAlignmentPlan Plan(string tab)
    {
        var importer = new GpImporter();
        var tracks = importer.GetAvailableTracks(tab).Select(t => t.Id).ToList();
        var result = importer.ImportTracksWithMetadata(tab, tracks);
        var project = new ChartProject();
        foreach (var beat in GpBeatMapBuilder.Build(result.Metadata, GpBeatMapBuilder.InitialBpm(result.Metadata, 120), 0)) project.Beats.Add(beat);
        foreach (var arrangement in result.Arrangements) project.Arrangements.Add(arrangement);
        return RecordingAlignmentPlan.Build(project, RecordingAlignmentRange.WholeSong, keepAnchors: true);
    }

    private static double[] Reference(string xml) =>
        XDocument.Load(xml).Root!.Element("ebeats")!.Elements("ebeat")
            .Select(e => double.Parse((string)e.Attribute("time")!, CultureInfo.InvariantCulture) * 1000).Order().ToArray();

    /// <summary>
    ///     The pitched-instrument stems the app ALREADY separated from this exact audio: the separator's
    ///     WAVs the tone matcher keeps (<c>match-eval/&lt;token&gt;/raw</c>, or its expansion), else the
    ///     durable archive's entries extracted to a temp folder. Never separates.
    /// </summary>
    private static IReadOnlyList<string> CachedInstrumentStems(string wav)
    {
        var key = StemCacheSource.TryResolve(StemCacheIdentity.ForAudioFile(wav), wav)?.Key;
        if (key == null) return [];
        var cacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotCharter", "StemCache");
        var playback = Path.Combine(cacheRoot, "match-eval", key.DirectoryToken);
        foreach (var folder in new[] { Path.Combine(playback, "raw"), playback })
        {
            if (!Directory.Exists(folder)) continue;
            var picked = RecordingAlignmentAudio.PickInstrumentStems(Directory.EnumerateFiles(folder));
            if (picked.Count > 0) return picked;
        }

        var archive = Path.Combine(cacheRoot, key.DirectoryToken + StemArchive.ArchiveExtension);
        if (!File.Exists(archive)) return [];
        var temp = Path.Combine(Path.GetTempPath(), "dotcharter-align-probe", key.DirectoryToken);
        if (!Directory.Exists(temp)) StemArchive.ExtractStems(archive, temp);
        return RecordingAlignmentAudio.PickInstrumentStems(Directory.EnumerateFiles(temp));
    }

    /// <summary>|t − nearest reference beat| for each beat inside the reference's span.</summary>
    private static List<double> Nearest(IReadOnlyList<double> beats, double[] reference)
    {
        var errors = new List<double>();
        foreach (var t in beats)
        {
            if (t < reference[0] || t > reference[^1]) continue;
            var i = Array.BinarySearch(reference, t);
            if (i >= 0)
            {
                errors.Add(0);
                continue;
            }

            i = ~i;
            errors.Add(Math.Min(Math.Abs(t - reference[i - 1]), Math.Abs(reference[i] - t)));
        }

        return errors;
    }

    private static string Stat(List<double> values, double q)
    {
        if (values.Count == 0) return "—";
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Round(q * (sorted.Length - 1))].ToString("0.0", CultureInfo.InvariantCulture);
    }
}
