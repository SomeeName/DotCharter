using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Package.DcSong;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>The writer must publish only bounded, complete packages, preserving an existing export on refusal.</summary>
public sealed class DcSongWriterPublicationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-dcsong-publication-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedChartOrManifest_IsRefusedBeforeReplacingThePreviousPackage(bool chart)
    {
        var project = new ChartProject();
        if (!chart) project.Chart.SongName = new string('x', (int)DcSongFormat.MaxManifestBytes);
        var output = Path.Combine(_dir, "kept.dcsong");
        File.WriteAllText(output, "previous package");
        string? stagingDirectory = null;
        var writer = Writer(dir => stagingDirectory = dir);
        var progress = new InlineProgress(p =>
        {
            if (!chart || p.Stage != DcSongWriteStage.WritingPackage) return;
            // Expand the serialized chart on disk: no giant domain graph or real encoder is needed.
            Assert.NotNull(stagingDirectory);
            using var stream = new FileStream(Path.Combine(stagingDirectory, DcSongFormat.ChartPath), FileMode.Open, FileAccess.Write);
            stream.SetLength(DcSongFormat.MaxChartBytes + 1);
        });

        var error = await Assert.ThrowsAsync<DcSongWriteException>(() => writer.WriteAsync(new DcSongWriteRequest
        {
            Project = project, AudioPath = Audio(), OutputPath = output
        }, progress, TestContext.Current.CancellationToken));

        Assert.Equal("PackageTooLarge", error.Reason.ToString());
        Assert.Equal("DcSong_WriteFailure_PackageTooLarge", error.ResourceKey);
        Assert.Equal("previous package", File.ReadAllText(output));
        Assert.False(File.Exists(output + ".writing"));
    }

    [Fact]
    public async Task CancellingAtTheFinalStage_PreservesThePreviousPackageAndCleansStaging()
    {
        var output = Path.Combine(_dir, "cancelled.dcsong");
        File.WriteAllText(output, "previous package");
        using var cancelled = new CancellationTokenSource();
        string? stagingDirectory = null;
        var writer = Writer(dir => stagingDirectory = dir);
        var finalStage = false;
        var progress = new InlineProgress(p =>
        {
            if (p.Stage != DcSongWriteStage.WritingPackage) return;
            finalStage = true;
            cancelled.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(), AudioPath = Audio(), OutputPath = output
        }, progress, cancelled.Token));

        Assert.True(finalStage);
        Assert.Equal("previous package", File.ReadAllText(output));
        Assert.False(File.Exists(output + ".writing"));
        Assert.NotNull(stagingDirectory);
        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Fact]
    public async Task ACompletedPackage_PassesTheReadersSizeAndHashChecks()
    {
        var output = Path.Combine(_dir, "complete.dcsong");
        await Writer(_ => { }).WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(), AudioPath = Audio(), OutputPath = output
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var package = DcSongPackage.Open(output);
        package.VerifyAll();
        Assert.NotNull(package.LoadChart());
        Assert.False(File.Exists(output + ".writing"));
    }

    private string Audio() => DcSongTestKit.WriteWav(Path.Combine(_dir, "song.wav"), 48000, 2, 0.1);

    private static DcSongWriter Writer(Action<string> staged) => new(new FfmpegStemEncoder(
        ffmpegResolver: () => "fake-ffmpeg",
        runner: (_, args, _) =>
        {
            staged(Path.GetDirectoryName(args[^1])!);
            File.WriteAllText(args[^1], "encoded audio");
            return Task.FromResult(true);
        }));

    private sealed class InlineProgress(Action<DcSongWriteProgress> report) : IProgress<DcSongWriteProgress>
    {
        public void Report(DcSongWriteProgress value) => report(value);
    }
}
