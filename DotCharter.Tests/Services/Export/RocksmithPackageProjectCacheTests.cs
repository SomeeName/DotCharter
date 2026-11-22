using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export;

public sealed class RocksmithPackageProjectCacheTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("package_project_cache_").FullName;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Package_destinations_never_own_project_audio_caches(bool explicitProjectDirectory)
    {
        var projectDirectory = Directory.CreateDirectory(Path.Combine(_root, "chart")).FullName;
        var sourceDirectory = explicitProjectDirectory
            ? Directory.CreateDirectory(Path.Combine(_root, "shared-audio")).FullName : projectDirectory;
        var source = Path.Combine(sourceDirectory, "song.wav");
        PcmAudioLoader.WriteWav(source, new PcmAudio(new short[48000], 1, 48000));
        var project = Project();
        var logger = new FakeAppLogger();
        var builder = new RocksmithPackageBuilder(logger);
        string[]? firstCaches = null;
        foreach (var folder in new[] { "game-dlc-a", "game-dlc-b" })
        {
            var destination = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
            await builder.BuildPsarcAsync(project, new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(destination, "song_p.psarc"),
                ProjectDirectory = explicitProjectDirectory ? projectDirectory : null,
                AudioPath = source,
                AudioCodec = WemCodec.Pcm
            }, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(Directory.Exists(Path.Combine(destination, ".dotcharter")));
            Assert.Equal(new[] { "song_p.psarc" }, Directory.GetFileSystemEntries(destination).Select(Path.GetFileName).ToArray());
            var cache = Path.Combine(projectDirectory, ".dotcharter", "cache");
            Assert.True(Directory.Exists(cache));
            var caches = Directory.GetFiles(cache, "*.wem").Order().ToArray();
            Assert.Equal(2, caches.Length);
            Assert.Single(Directory.GetFiles(cache, "*.preview.wav"));
            Assert.True(File.Exists(Path.Combine(projectDirectory, ".dotcharter", "companions.json")));
            if (firstCaches != null) Assert.Equal(firstCaches, caches);
            firstCaches = caches;
        }
        Assert.Contains(logger.Snapshot(), entry => entry.Message.Contains("Reusing cached wem", StringComparison.Ordinal)
            && entry.Message.Contains(projectDirectory, StringComparison.Ordinal));
        if (explicitProjectDirectory)
            Assert.Equal(new[] { "song.wav" }, Directory.GetFileSystemEntries(sourceDirectory).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task Mac_publication_consults_the_guard_and_preserves_an_existing_destination_when_refused()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var source = Path.Combine(_root, "song.wav");
        PcmAudioLoader.WriteWav(source, new PcmAudio(new short[48000], 1, 48000));
        var output = Path.Combine(_root, "song_m.psarc");
        File.WriteAllText(output, "foreign Mac package");
        var guarded = new List<string>();
        var builder = new RocksmithPackageBuilder(new FakeAppLogger());

        await Assert.ThrowsAsync<IOException>(() => builder.BuildPsarcAsync(Project(), new RocksmithBuildOptions
        {
            OutputPath = output, AudioPath = source, AudioCodec = WemCodec.Pcm, Platform = ExportPlatform.Mac
        }, cancellationToken: TestContext.Current.CancellationToken, beforePublish: (path, _) =>
        {
            guarded.Add(path);
            throw new IOException("Publication refused");
        }));

        Assert.Equal([output], guarded);
        Assert.Equal("foreign Mac package", File.ReadAllText(output));
    }

    private static ChartProject Project()
    {
        var project = new ChartProject { AudioFileName = "song.wav", Chart = { Artist = "Artist", SongName = "Song" } };
        for (var i = 0; i < 4; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);
        return project;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
