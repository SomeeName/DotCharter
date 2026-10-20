using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>
///     #709's contract, end to end through the real writer, ffmpeg, the unpacker and the
///     <c>.rscp</c> serializer: a package opened as a project, saved, and exported again
///     carries the SAME chart as the package it came from. Skips where no ffmpeg can be
///     found; the no-ffmpeg rules live in <see cref="DcSongProjectUnpackerTests" />.
/// </summary>
public sealed class DcSongOpenSaveReexportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_dcsong_reexport_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string P(string name) => Path.Combine(_dir, name);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenSaveReexport_CarriesTheOriginalPackagesChart(bool lossless)
    {
        Assert.SkipUnless(FfmpegAvailable, "no ffmpeg on this machine.");
        var ct = TestContext.Current.CancellationToken;
        System.IO.File.WriteAllBytes(P("cover.png"), Png);
        var original = await new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = RichProjectFactory.Build(),
            AudioPath = WriteWav(P("song.wav"), 48000, 2, 3.0),
            OutputPath = P("original.dcsong"),
            Lossless = lossless,
            CoverPath = P("cover.png"),
            Stems = [new DcSongStemSource("drums", WriteWav(P("drums.wav"), 48000, 2, 3.0, seed: 11)),
                     new DcSongStemSource("bass", WriteWav(P("bass.wav"), 48000, 2, 3.0, seed: 12))]
        }, cancellationToken: ct);

        // Open: a new folder with the chart, the audio the engine can play, the cover
        // and the stems as WAVs.
        var opened = await new DcSongProjectUnpacker(new FakeAppLogger(), configuredFfmpegPath: () => null)
            .UnpackAsync(original.OutputPath, Directory.CreateDirectory(P("Projects")).FullName, ct);
        Assert.Equal(!lossless, opened.AudioLossy);
        Assert.Equal(new[] { "drums", "bass" }, opened.Stems);
        var audio = Path.Combine(opened.ProjectDirectory, lossless ? "song.flac" : "song.wav");
        Assert.True(System.IO.File.Exists(audio));
        if (!lossless)
        {
            // Opus decodes at 48 kHz, to the length the package was cut from.
            var (samples, rate, channels) = ReadWav16(audio);
            Assert.Equal((48000, 2), (rate, channels));
            Assert.InRange(samples.Length / channels, 48000 * 3 - 960, 48000 * 3 + 960);
        }

        // Open the written project and save it. (File > Open also runs the spacing pass
        // every .rscp open runs; the rich fixture is deliberately not spacing-clean, so
        // that pass would measure the enforcer, not the package — it is left out.)
        var serializer = new RscpChartSerializer();
        var project = serializer.Load(opened.ProjectPath);
        serializer.Save(opened.ProjectPath, project);
        project = serializer.Load(opened.ProjectPath);

        // Re-export from the project folder, as the export dialog would.
        var reexported = await new DcSongWriter().WriteAsync(new DcSongWriteRequest
        {
            Project = project,
            AudioPath = Path.Combine(opened.ProjectDirectory, project.AudioFileName),
            OutputPath = P("reexported.dcsong"),
            Lossless = lossless,
            ProjectDirectory = opened.ProjectDirectory,
            Stems = project.Stems.Select(s => new DcSongStemSource(s.Name, Path.Combine(opened.ProjectDirectory, s.Path))).ToList()
        }, cancellationToken: ct);
        Assert.Empty(reexported.Warnings);

        using var before = DcSongPackage.Open(original.OutputPath);
        using var after = DcSongPackage.Open(reexported.OutputPath);
        var diffs = DeepCompare.Diff("chart", before.LoadChart(), after.LoadChart(), RscpDeepValueRoundTripTests.Ignored);
        Assert.True(diffs.Count == 0, "open -> save -> re-export changed the chart:\n  " + string.Join("\n  ", diffs));
        Assert.Equal(before.ReadCover()!.Bytes, after.ReadCover()!.Bytes);
        Assert.Equal(before.StemNames, after.StemNames);
        Assert.Equal(before.Manifest.Song with { Arrangements = [] }, after.Manifest.Song with { Arrangements = [] });
        Assert.Equal(before.Manifest.Song.Arrangements, after.Manifest.Song.Arrangements);
    }
}
