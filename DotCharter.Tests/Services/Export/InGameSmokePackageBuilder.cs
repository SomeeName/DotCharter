using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Produces a real, installable CDLC from a real chart so the ONE thing our unit
///     tests cannot answer can be answered: does Rocksmith 2014 actually decode a
///     Wwise-free <c>.wem</c>?
/// </summary>
/// <remarks>
///     Every codec claim in <see cref="WemCodec" /> rests on Audiokinetic's statement
///     that PCM/ADPCM are core (non-pluggable) codecs. Our tests prove the container
///     is right and the bank agrees with it — they cannot prove the game's sound
///     engine registered those decoders. Every one of the ~22,500 wems shipped in the
///     game is Vorbis, so the game's own files are no evidence either.
///     <para>
///         This is opt-in and skipped by default: it writes real files and needs
///         TestData. Set <c>DOTCHARTER_SMOKE_PSARC_OUT</c> to an output directory:
///         <code>
///         $env:DOTCHARTER_SMOKE_PSARC_OUT = "F:\...\Rocksmith2014\dlc"
///         dotnet test --filter "FullyQualifiedName~InGameSmokePackageBuilder"
///         </code>
///         Then launch Rocksmith and look for the song. A failed decode in Wwise is
///         SILENT — the song still loads and scrolls — so judge it by whether you
///         hear audio, not by whether it appears.
///     </para>
/// </remarks>
public sealed class InGameSmokePackageBuilder
{
    private const string OutputDirVariable = "DOTCHARTER_SMOKE_PSARC_OUT";

    [Theory]
    [InlineData(WemCodec.Pcm)]
    [InlineData(WemCodec.Vorbis)]
    public async Task Build_an_installable_package(WemCodec codec)
    {
        var outputDir = Environment.GetEnvironmentVariable(OutputDirVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(outputDir),
            $"Opt-in: set {OutputDirVariable} to an output directory to build an installable package.");

        var song = FindEofProject();
        Assert.NotNull(song);

        var eof = Path.Combine(song!, "notes.eof");
        var audio = Path.Combine(song, "guitar.ogg");
        Assert.True(File.Exists(eof), $"missing {eof}");
        Assert.True(File.Exists(audio), $"missing {audio}");

        var importer = new EofImporter();
        var playable = importer.GetAvailableTracks(eof).Where(t => t.NoteCount > 0).Select(t => t.Id).ToList();
        Assert.NotEmpty(playable);

        var imported = importer.ImportTracksWithMetadata(eof, playable);

        var project = new ChartProject
        {
            AudioFileName = Path.GetFileName(audio),
            Chart =
            {
                // Tag it so it is obvious in-game which build is being heard.
                SongName = $"DotCharter {codec}",
                Artist = string.IsNullOrWhiteSpace(imported.Metadata.Artist) ? "DotCharter" : imported.Metadata.Artist,
                AlbumName = imported.Metadata.Album,
                AlbumYear = imported.Metadata.Year
            }
        };

        // Same mapping the EOF import dialog uses.
        foreach (var beat in imported.Metadata.Beats)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = beat.TimeMs,
                Bpm = beat.Bpm,
                BeatsInMeasure = beat.BeatsInMeasure,
                NoteDenominator = beat.NoteDenominator,
                IsMeasure = beat.IsMeasureStart
            });
        }

        foreach (var arrangement in imported.Arrangements) project.Arrangements.Add(arrangement);

        Assert.NotEmpty(project.Beats);
        Assert.NotEmpty(project.Arrangements);

        Directory.CreateDirectory(outputDir);
        var output = Path.Combine(outputDir, $"DotCharter_{codec}_p.psarc");

        var builder = new RocksmithPackageBuilder(new FakeAppLogger());
        var result = await builder.BuildPsarcAsync(project, new RocksmithBuildOptions
        {
            OutputPath = output,
            AudioPath = audio,
            AudioCodec = codec
        }, progress: null, CancellationToken.None);

        Assert.True(File.Exists(result.PsarcPath));

        var megabytes = new FileInfo(result.PsarcPath).Length / (1024.0 * 1024.0);
        Assert.True(megabytes > 0.1, "package is suspiciously small");
    }

    private static string? FindEofProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "TestData")))
            dir = dir.Parent;
        if (dir is null) return null;

        var root = Path.Combine(dir.FullName, "TestData", "EOF");
        if (!Directory.Exists(root)) return null;

        return Directory.EnumerateDirectories(root)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "notes.eof")) &&
                                 File.Exists(Path.Combine(d, "guitar.ogg")));
    }
}
