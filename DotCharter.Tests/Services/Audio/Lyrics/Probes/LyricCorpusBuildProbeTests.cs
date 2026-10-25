using DotCharter.Analysis.Lyrics;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Onnx;

namespace DotCharter.Tests.Services.Audio.Lyrics.Probes;

/// <summary>
///     Builds the lyric probe corpus (#237) once, so every later measurement is a re-read
///     of cached audio rather than a separation per song. Both facts skip unless their
///     environment variables are set (<see cref="LyricProbeCorpus" />), so the default gate
///     builds them and runs neither, on the owner's machine as anywhere else.
///     <para>
///         The stock English arrangements come from the package reader, which is not in
///         this tree; its exporter writes the same <c>*.lyric-truth.json</c> sources into
///         <see cref="LyricProbeCorpus.SourcesVariable" />, and <see cref="BuildCorpus" />
///         treats every source alike.
///     </para>
/// </summary>
public sealed class LyricCorpusBuildProbeTests(ITestOutputHelper output)
{
    /// <summary>The owner's hand-synced romaji charts in <c>TestData/EOF</c>: song folder, vocals, mix.</summary>
    private static readonly (string Song, string Vocals, string Audio)[] RomajiCharts =
    [
        ("Yorushika - Matasaburo", "arr_vocals_RS2.xml", "Matasaburo.wav"),
        ("Yorushika - Replicant", "arr_vocals_RS2.xml", "Replicant.wav"),
        ("Yorushika - Ghost In A Flower", "Vocals.xml", "Ghost In A Flower.wav"),
        ("Yorushika - Just a Sunny Day for You", "1_Vocals__Vocals_RS2.xml", "Just a Sunny Day for You.wav")
    ];

    [Fact]
    public void ExportRomajiSources()
    {
        var sources = LyricProbeCorpus.Variable(LyricProbeCorpus.SourcesVariable);
        Assert.SkipWhen(sources == null, $"Set {LyricProbeCorpus.SourcesVariable} to export the romaji sources.");
        var root = LocalFixtures.TestData("EOF");
        Assert.SkipUnless(root != null && Directory.Exists(root), "TestData/EOF is not on this machine.");

        var exported = 0;
        foreach (var (song, vocals, audio) in RomajiCharts)
        {
            var folder = Path.Combine(root, song);
            var vocalsPath = Path.Combine(folder, vocals);
            var audioPath = Path.Combine(folder, audio);
            var gridPath = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*_RS2.xml")
                    .Where(p => !p.EndsWith(".debug.xml", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(p => LyricProbeCorpus.ReadBeatXml(p).Count > 1)
                : null;
            if (!File.Exists(vocalsPath) || !File.Exists(audioPath) || gridPath == null)
            {
                output.WriteLine($"  {song}: vocals, audio or beat grid missing");
                continue;
            }

            LyricProbeCorpus.WriteSource(sources, new LyricTruthSource(song, "romaji", audioPath,
                [.. LyricProbeCorpus.ReadBeatXml(gridPath)], [.. LyricProbeCorpus.ReadVocalXml(vocalsPath)]));
            exported++;
        }

        output.WriteLine($"exported {exported} romaji sources");
        Assert.True(exported > 0, "no romaji chart was found under TestData/EOF");
    }

    [Fact]
    public async Task BuildCorpus()
    {
        var root = LyricProbeCorpus.Variable(LyricProbeCorpus.CorpusVariable);
        var sources = LyricProbeCorpus.Variable(LyricProbeCorpus.SourcesVariable);
        Assert.SkipWhen(root == null || sources == null,
            $"Set {LyricProbeCorpus.CorpusVariable} and {LyricProbeCorpus.SourcesVariable} to build the lyric corpus.");

        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(root);
        var songs = LyricProbeCorpus.Load(root);
        var done = songs.Select(s => s.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var logger = new FakeAppLogger();
        var config = new FakeConfigurationService();
        config.AppConfig.HtDemucsActiveModelId = LyricProbeCorpus.StemModelId;
        var stemModels = LyricProbeCorpus.Variable(LyricProbeCorpus.StemModelsVariable) ?? Path.Combine(root, "stem-models");
        var manager = new StemModelManager(config, logger, stemModels);
        if (!manager.IsInstalled(LyricProbeCorpus.StemModelId)) await manager.DownloadAsync(LyricProbeCorpus.StemModelId, ct: ct);
        var separator = new OnnxStemSeparationService(manager, logger);
        output.WriteLine(await separator.DescribeBackendAsync(ct));

        foreach (var source in LyricProbeCorpus.ReadSources(sources))
        {
            var slug = LyricProbeCorpus.Slugify(source.Title);
            if (done.Contains(slug)) continue;

            // Index-aligned by construction, so a sheet the parser splits differently
            // would make every comparison meaningless; drop the song instead.
            var parsed = LyricSheetParser.Parse(LyricProbeCorpus.ToPasteText(source.Syllables)).Syllables.Count;
            if (parsed != source.Syllables.Length)
            {
                output.WriteLine($"  {source.Title}: the sheet parses to {parsed} syllables, not {source.Syllables.Length}");
                continue;
            }

            var scratch = Path.Combine(root, "scratch", slug);
            try
            {
                var separated = await separator.SeparateAsync(source.AudioPath, scratch, ct: ct);
                var vocals = separated.Stems.FirstOrDefault(t => t.Name.Equals("vocals", StringComparison.OrdinalIgnoreCase));
                if (!separated.Success || vocals == null || !File.Exists(vocals.Path))
                {
                    output.WriteLine($"  {source.Title}: no vocal stem ({separated.ErrorMessage})");
                    continue;
                }

                LyricProbeCorpus.WriteAudio(LyricProbeCorpus.AudioPath(root, slug, "mix"), LyricAudioSource.LoadMono16k(source.AudioPath));
                LyricProbeCorpus.WriteAudio(LyricProbeCorpus.AudioPath(root, slug, "vocals"), LyricAudioSource.LoadMono16k(vocals.Path));
            }
            finally
            {
                if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
            }

            songs.Add(new LyricCorpusSong(slug, source.Title, source.Language, source.Beats, source.Syllables));
            LyricProbeCorpus.Save(root, songs);
            output.WriteLine($"  [{songs.Count,2}] {source.Language} {source.Title}: {source.Syllables.Length} syllables");
        }

        output.WriteLine($"corpus: {songs.Count} songs, {songs.Sum(s => s.Syllables.Length):N0} syllables");
        Assert.NotEmpty(songs);
    }
}
