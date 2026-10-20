using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>
///     Project → <c>.dcsong</c> → project, through the real writer and reader, the
///     real ffmpeg (Opus and FLAC) and the real <c>.rscp</c> serializer. Skips where
///     no ffmpeg can be found; the reader's refusals need none and live in
///     <see cref="DcSongReaderSafetyTests" />.
/// </summary>
public sealed class DcSongRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_dcsong_rt_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task ARichProject_SurvivesThePackage_ValueForValue()
    {
        Assert.SkipUnless(FfmpegAvailable, "no ffmpeg on this machine.");
        var original = RichProjectFactory.Build();
        var before = ChartProjectCloner.Clone(original);
        var song = WriteWav(P("song.wav"), 48000, 2, 3.0);
        System.IO.File.WriteAllBytes(P("cover.png"), Png);
        System.IO.File.WriteAllBytes(P("banner.jpg"), Jpeg);

        var result = await new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = original,
            AudioPath = song,
            OutputPath = P("rich.dcsong"),
            CoverPath = P("cover.png"),
            BannerPath = P("banner.jpg"),
            // The fixture's own two stems, so their mixer state rides along.
            Stems = [new DcSongStemSource("drums", WriteWav(P("drums.wav"), 48000, 2, 3.0, seed: 11)),
                     new DcSongStemSource("bass", WriteWav(P("bass.wav"), 48000, 2, 3.0, seed: 12))]
        }, cancellationToken: TestContext.Current.CancellationToken);

        // The writer packages a CLONE: the caller's project is untouched.
        Assert.Empty(DeepCompare.Diff("project", before, original, new HashSet<string>()));

        using var package = DcSongPackage.Open(result.OutputPath);
        package.VerifyAll();
        var loaded = package.LoadChart();

        // The expectation is the original as the .rscp writer canonicalizes it
        // (RscpDeepValueRoundTripTests does the same), with its FILE REFERENCES
        // pointed into the package — stated here, value by value, rather than by
        // calling the code under test.
        var expected = ChartProjectCloner.Clone(original);
        foreach (var arrangement in expected.Arrangements) new RscpTemplateManager().RegenerateTemplates(arrangement);
        expected.AudioFileName = "audio/song.opus";
        expected.Chart.CoverArtPath = "images/cover.png";
        expected.ArrangementFiles.Clear();
        expected.Stems[0].Path = "stems/drums.opus";
        expected.Stems[0].Local = true;
        expected.Stems[1].Path = "stems/bass.opus";
        expected.Stems[1].Local = true;

        var diffs = DeepCompare.Diff("project", expected, loaded, RscpDeepValueRoundTripTests.Ignored);
        Assert.True(diffs.Count == 0, "The .dcsong round trip changed these values:\n  " + string.Join("\n  ", diffs));

        // Everything else the package carries.
        var manifest = package.Manifest;
        Assert.Equal((DcSongFormat.CurrentMajor, DcSongFormat.CurrentMinor), (manifest.FormatMajor, manifest.FormatMinor));
        Assert.Equal("The Song", manifest.Song.Title);
        Assert.Equal("The Artist", manifest.Song.Artist);
        Assert.Equal("The Album", manifest.Song.Album);
        Assert.Equal("1997", manifest.Song.Year);
        Assert.Equal("SomeName", manifest.Song.Charter);
        Assert.Equal(original.ProjectId, manifest.Song.ProjectId);
        Assert.Equal(original.Arrangements.Select(a => a.Name), manifest.Song.Arrangements.Select(a => a.Name));
        Assert.InRange(manifest.Song.LengthMs, 2990, 3010);
        Assert.StartsWith("DotCharter ", manifest.Writer, StringComparison.Ordinal);
        Assert.Equal(new[] { "drums", "bass" }, package.StemNames);
        Assert.Equal(Png, package.ReadCover()!.Bytes);
        Assert.Equal(DcSongImages.Jpeg, package.ReadBanner()!.MediaType);
        Assert.Equal(Jpeg, package.ReadBanner()!.Bytes);
        Assert.NotNull(package.OpenPreview());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task OnlyTheTickedArrangements_AreInThePackageAndItsManifest()
    {
        Assert.SkipUnless(FfmpegAvailable, "no ffmpeg on this machine.");
        var song = WriteWav(P("song.wav"), 44100, 2, 2.0);
        var project = new ChartProject();
        foreach (var name in new[] { "Lead", "Rhythm", "Bass" }) project.Arrangements.Add(new Arrangement { Name = name });

        var result = await new DcSongExporter(new FakeAppLogger(), () => null).WriteAsync(new DcSongWriteRequest
        {
            Project = project, AudioPath = song, OutputPath = P("picked.dcsong"), ArrangementIndexes = [0, 2]
        }, null, TestContext.Current.CancellationToken);

        using var package = DcSongPackage.Open(result.OutputPath);
        Assert.Equal(new[] { "Lead", "Bass" }, package.Manifest.Song.Arrangements.Select(a => a.Name));
        Assert.Equal(new[] { "Lead", "Bass" }, package.LoadChart().Arrangements.Select(a => a.Name));
        Assert.Equal(3, project.Arrangements.Count);
    }

    [Fact]
    public async Task AnOpusPackage_DecodesToTheRightLengthAndRate()
    {
        Assert.SkipUnless(FfmpegAvailable, "no ffmpeg on this machine.");
        const double seconds = 4.0;
        var song = WriteWav(P("song.wav"), 44100, 2, seconds);

        var result = await new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(), AudioPath = song, OutputPath = P("opus.dcsong")
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var package = DcSongPackage.Open(result.OutputPath);
        Assert.Equal(DcSongAudioCodec.Opus, package.Manifest.Audio.Codec);
        Assert.Equal(DcSongFormat.OpusBitrateKbps, package.Manifest.Audio.BitrateKbps);
        Assert.False(package.IsLossless);

        var decoded = await DecodeAsync(package.OpenAudio(), StemCodec.Opus, "song");
        Assert.Equal(48000, decoded.SampleRate);
        Assert.Equal(2, decoded.Channels);
        var frames = decoded.Samples.Length / decoded.Channels;
        // 4 s of 44.1 kHz audio is 4 s at 48 kHz; Opus trims its own pre-skip.
        Assert.InRange(frames, (int)(48000 * seconds) - 960, (int)(48000 * seconds) + 960);
        Assert.Contains(decoded.Samples, s => Math.Abs((int)s) > 3000);

        // The preview of a song shorter than a preview is the whole song.
        var preview = await DecodeAsync(package.OpenPreview()!, StemCodec.Opus, "preview");
        Assert.Equal(48000, preview.SampleRate);
        Assert.InRange(preview.Samples.Length / preview.Channels, (int)(48000 * seconds) - 960, (int)(48000 * seconds) + 960);
    }

    [Fact]
    public async Task ALosslessPackage_KeepsTheSourceSamplesBitExact()
    {
        Assert.SkipUnless(FfmpegAvailable, "no ffmpeg on this machine.");
        // 44.1 kHz on purpose: the lossless package keeps the SOURCE's rate, not the
        // 48 kHz the Opus path and the Rocksmith export conform to.
        var samples = SongSamples(44100, 2, 3.0);
        var song = P("master.wav");
        WriteWav(song, samples, 44100, 2);

        var result = await new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(), AudioPath = song, OutputPath = P("lossless.dcsong"), Lossless = true
        }, cancellationToken: TestContext.Current.CancellationToken);

        using var package = DcSongPackage.Open(result.OutputPath);
        Assert.True(package.IsLossless);
        Assert.Equal(DcSongAudioCodec.Flac, package.Manifest.Audio.Codec);
        Assert.Null(package.Manifest.Audio.BitrateKbps);
        Assert.Empty(result.Warnings);

        var decoded = await DecodeAsync(package.OpenAudio(), StemCodec.Flac, "song");
        Assert.Equal(44100, decoded.SampleRate);
        Assert.Equal(2, decoded.Channels);
        Assert.Equal(samples.Length, decoded.Samples.Length);
        Assert.True(samples.AsSpan().SequenceEqual(decoded.Samples), "the FLAC member is not bit-exact to the source");
    }

    [Fact]
    public async Task ALosslessPackageOfALossySource_SaysSo()
    {
        Assert.SkipUnless(FfmpegAvailable, "no ffmpeg on this machine.");
        // An Opus "master" is lossy, so its lossless package can only hold its decode.
        var opus = await new FfmpegStemEncoder().EncodeAsync(WriteWav(P("song.wav"), 48000, 2, 2.0), P("song"),
            StemCodec.Opus, TestContext.Current.CancellationToken);

        var result = await new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(), AudioPath = opus, OutputPath = P("from-lossy.dcsong"), Lossless = true
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { DcSongWriteWarning.LossySourceInLosslessPackage }, result.Warnings);
        using var package = DcSongPackage.Open(result.OutputPath);
        Assert.True(package.IsLossless);
        package.VerifyAll();
    }

    [Fact]
    public async Task ProgressNamesEveryStageInOrder_AndEndsAtTheLastStep()
    {
        Assert.SkipUnless(FfmpegAvailable, "no ffmpeg on this machine.");
        var reports = new List<DcSongWriteProgress>();
        await new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(),
            AudioPath = WriteWav(P("song.wav"), 48000, 2, 1.0),
            OutputPath = P("progress.dcsong"),
            Stems = [new DcSongStemSource("vocals", WriteWav(P("vocals.wav"), 48000, 2, 1.0, seed: 3))]
        }, new SynchronousProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Enum.GetValues<DcSongWriteStage>(), reports.Select(r => r.Stage));
        Assert.Equal(Enumerable.Range(1, 6), reports.Select(r => r.Step));
        Assert.All(reports, r => Assert.Equal(6, r.TotalSteps));
        Assert.Equal("DcSong_WriteStage_WritingPackage", reports[^1].ResourceKey);
    }

    [Fact]
    public async Task AFailedWrite_LeavesThePreviousPackageAndNoStagingFile()
    {
        // An encoder that finds "ffmpeg" but whose every run fails — no real ffmpeg needed.
        var failing = new FfmpegStemEncoder(ffmpegResolver: () => "ffmpeg-that-fails",
            runner: (_, _, _) => Task.FromResult(false), opusBitrateKbps: DcSongFormat.OpusBitrateKbps);
        var output = P("kept.dcsong");
        System.IO.File.WriteAllText(output, "the previous package");

        var ex = await Assert.ThrowsAsync<DcSongWriteException>(() => new DcSongWriter(failing).WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(), AudioPath = WriteWav(P("song.wav"), 48000, 2, 0.5), OutputPath = output
        }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(DcSongWriteFailure.EncodeFailed, ex.Reason);
        Assert.Equal("the previous package", System.IO.File.ReadAllText(output));
        Assert.False(System.IO.File.Exists(output + ".writing"));
    }

    [Fact]
    public async Task NoFfmpeg_IsRefusedUpFront_WithItsOwnReason()
    {
        var none = new FfmpegStemEncoder(ffmpegResolver: () => null);
        var ex = await Assert.ThrowsAsync<DcSongWriteException>(() => new DcSongWriter(none).WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(), AudioPath = WriteWav(P("song.wav"), 48000, 2, 0.5), OutputPath = P("x.dcsong")
        }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(DcSongWriteFailure.FfmpegMissing, ex.Reason);
        Assert.False(System.IO.File.Exists(P("x.dcsong")));
    }

    [Theory]
    [InlineData("Drums")]
    [InlineData("../drums")]
    [InlineData("")]
    public async Task ABadStemName_IsRefused(string name)
    {
        var encoder = new FfmpegStemEncoder(ffmpegResolver: () => "ffmpeg", runner: (_, _, _) => Task.FromResult(true));
        var ex = await Assert.ThrowsAsync<DcSongWriteException>(() => new DcSongWriter(encoder).WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(),
            AudioPath = WriteWav(P("song.wav"), 48000, 2, 0.5),
            OutputPath = P("x.dcsong"),
            Stems = [new DcSongStemSource(name, P("song.wav"))]
        }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(DcSongWriteFailure.InvalidStem, ex.Reason);
    }

    [Fact]
    public async Task AnExplicitCoverThatIsNotAnImage_FailsTheWrite()
    {
        var encoder = new FfmpegStemEncoder(ffmpegResolver: () => "ffmpeg", runner: (_, _, _) => Task.FromResult(true));
        System.IO.File.WriteAllText(P("cover.png"), "not a png");

        var ex = await Assert.ThrowsAsync<DcSongWriteException>(() => new DcSongWriter(encoder).WriteAsync(new DcSongWriteRequest
        {
            Project = new ChartProject(), AudioPath = WriteWav(P("song.wav"), 48000, 2, 0.5), OutputPath = P("x.dcsong"),
            CoverPath = P("cover.png")
        }, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(DcSongWriteFailure.ImageUnsupported, ex.Reason);
        Assert.False(System.IO.File.Exists(P("x.dcsong")));
    }

    [Fact]
    public async Task TheProjectsOwnCoverThatIsNotAnImage_IsSkippedWithAWarning()
    {
        Assert.SkipUnless(FfmpegAvailable, "no ffmpeg on this machine.");
        // Not something the user just chose, so it must not cost them the package.
        System.IO.File.WriteAllText(P("cover.png"), "not a png");
        var project = new ChartProject();
        project.Chart.CoverArtPath = "cover.png";

        var result = await new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = project, AudioPath = WriteWav(P("song.wav"), 48000, 2, 0.5), OutputPath = P("x.dcsong"),
            ProjectDirectory = _dir
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { DcSongWriteWarning.CoverSkipped }, result.Warnings);
        using var package = DcSongPackage.Open(result.OutputPath);
        Assert.Null(package.ReadCover());
        Assert.Equal("", package.LoadChart().Chart.CoverArtPath);
    }

    private async Task<(short[] Samples, int SampleRate, int Channels)> DecodeAsync(Stream member, StemCodec codec, string name)
    {
        var encoded = P($"{name}.decode-in.{codec.FileExtension()}");
        using (member) CopyToFile(member, encoded);
        var wav = await new FfmpegStemEncoder().DecodeToWavAsync(encoded, codec, P($"{name}.decoded.wav"),
            TestContext.Current.CancellationToken);
        return ReadWav16(wav);
    }

    /// <summary>Reports inline, in order — <see cref="Progress{T}" /> posts to the pool.</summary>
    private sealed class SynchronousProgress(Action<DcSongWriteProgress> report) : IProgress<DcSongWriteProgress>
    {
        public void Report(DcSongWriteProgress value) => report(value);
    }
}
