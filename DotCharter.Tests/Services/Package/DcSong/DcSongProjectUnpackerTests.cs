using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>
///     Opening a <c>.dcsong</c> as a project (#709): what lands in the NEW folder, the
///     new-folder rules, and what a refused or failed open leaves behind (nothing).
///     Hand-built packages and a fake decoder, so none of this needs ffmpeg; the
///     ffmpeg round trip (open → save → re-export) is
///     <see cref="DcSongOpenSaveReexportTests" />.
/// </summary>
public sealed class DcSongProjectUnpackerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_dcsong_open_").FullName;

    public DcSongProjectUnpackerTests() => Directory.CreateDirectory(BaseDir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string BaseDir => Path.Combine(_dir, "Projects");

    [Fact]
    public async Task AFlacPackage_OpensIntoANewFolder_WithItsChartAudioAndCover()
    {
        var package = BuildProjectPackage(_dir, lossless: true, cover: true);

        var result = await Unpacker(new FakeDecoder(available: false)).UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(BaseDir, "The Artist - The Song"), result.ProjectDirectory);
        Assert.Equal(Path.Combine(result.ProjectDirectory, "project.dcproj"), result.ProjectPath);
        Assert.False(result.AudioLossy);
        Assert.Equal("The Song", result.Title);
        // The FLAC is copied as it is — the engine decodes FLAC, no ffmpeg involved.
        Assert.Equal(PackagedSong, File.ReadAllBytes(Path.Combine(result.ProjectDirectory, "song.flac")));
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(result.ProjectDirectory, "cover.png")));

        var project = new RscpChartSerializer().Load(result.ProjectPath);
        Assert.Equal("song.flac", project.AudioFileName);
        Assert.Equal("cover.png", project.Chart.CoverArtPath);
        Assert.Equal("The Song", project.Chart.SongName);
        Assert.Equal(new[] { "Lead", "Bass" }, project.Arrangements.Select(a => a.Name));
    }

    [Fact]
    public async Task AnOpusPackage_DecodesItsAudioAndStemsToWavs_AndSaysTheAudioIsLossy()
    {
        var decoder = new FakeDecoder(available: true);
        var package = BuildProjectPackage(_dir, lossless: false, stems: true);

        var result = await Unpacker(decoder).UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken);

        Assert.True(result.AudioLossy);
        Assert.Equal(new[] { "drums" }, result.Stems);
        Assert.Empty(result.StemsSkipped);
        Assert.Equal(FakeDecoder.Decoded(PackagedSong), File.ReadAllBytes(Path.Combine(result.ProjectDirectory, "song.wav")));
        Assert.Equal(FakeDecoder.Decoded(PackagedDrums), File.ReadAllBytes(Path.Combine(result.ProjectDirectory, "stems", "drums.wav")));
        // Nothing but the project's own files: the Opus bytes went through a scratch folder.
        Assert.Equal(new[] { "project.dcproj", "song.wav", Path.Combine("stems", "drums.wav") },
            Directory.EnumerateFiles(result.ProjectDirectory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(result.ProjectDirectory, f)).Order(StringComparer.Ordinal));

        var project = new RscpChartSerializer().Load(result.ProjectPath);
        Assert.Equal("song.wav", project.AudioFileName);
        var stem = Assert.Single(project.Stems);
        Assert.Equal("drums", stem.Name);
        Assert.Equal(Path.Combine("stems", "drums.wav"), stem.Path);
        // The mixer state the package carried survives the open.
        Assert.Equal(0.25, stem.Volume);
        Assert.True(stem.Muted);
    }

    [Fact]
    public async Task AFlacPackage_WithoutFfmpeg_OpensWithoutItsStems_AndSaysSo()
    {
        var package = BuildProjectPackage(_dir, lossless: true, stems: true);

        var result = await Unpacker(new FakeDecoder(available: false)).UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken);

        Assert.Empty(result.Stems);
        Assert.Equal(new[] { "drums" }, result.StemsSkipped);
        Assert.False(Directory.Exists(Path.Combine(result.ProjectDirectory, "stems")));
        // A stem the project cannot play is not declared either.
        Assert.Empty(new RscpChartSerializer().Load(result.ProjectPath).Stems);
    }

    [Fact]
    public async Task OpeningTheSamePackageAgain_NeverLandsOnAnEarlierCopy()
    {
        var package = BuildProjectPackage(_dir, lossless: true);
        var unpacker = Unpacker(new FakeDecoder(available: false));

        var first = await unpacker.UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken);
        var firstChart = File.ReadAllBytes(first.ProjectPath);
        File.AppendAllText(first.ProjectPath, "<!-- the user's edits -->");
        var edited = File.ReadAllBytes(first.ProjectPath);
        var second = await unpacker.UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(BaseDir, "The Artist - The Song (2)"), second.ProjectDirectory);
        Assert.Equal(edited, File.ReadAllBytes(first.ProjectPath));
        Assert.Equal(firstChart, File.ReadAllBytes(second.ProjectPath));
    }

    [Fact]
    public async Task AnOccupiedFolderOfThatName_IsLeftAlone()
    {
        var occupied = Directory.CreateDirectory(Path.Combine(BaseDir, "The Artist - The Song")).FullName;
        File.WriteAllText(Path.Combine(occupied, "project.dcproj"), "somebody else's project");

        var result = await Unpacker(new FakeDecoder(available: false))
            .UnpackAsync(BuildProjectPackage(_dir, lossless: true), BaseDir, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(BaseDir, "The Artist - The Song (2)"), result.ProjectDirectory);
        Assert.Equal("somebody else's project", File.ReadAllText(Path.Combine(occupied, "project.dcproj")));
        Assert.Single(Directory.EnumerateFileSystemEntries(occupied));
    }

    [Fact]
    public async Task AnOpusPackage_WithoutFfmpeg_IsRefusedBeforeAnyFolderIsMade()
    {
        var package = BuildProjectPackage(_dir, lossless: false);

        var ex = await Assert.ThrowsAsync<DcSongReadException>(() =>
            Unpacker(new FakeDecoder(available: false)).UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken));

        Assert.Equal(DcSongReadFailure.DecoderMissing, ex.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(BaseDir));
    }

    [Fact]
    public async Task AFailedDecode_RemovesTheFolderItMade()
    {
        var package = BuildProjectPackage(_dir, lossless: false, cover: true);

        var ex = await Assert.ThrowsAsync<DcSongReadException>(() =>
            Unpacker(new FakeDecoder(available: true, fail: true)).UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken));

        Assert.Equal(DcSongReadFailure.DecodeFailed, ex.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(BaseDir));
    }

    [Fact]
    public async Task ATamperedSong_IsRefused_AndLeavesNothingBehind()
    {
        var package = BuildProjectPackage(_dir, lossless: true, cover: true);
        FlipByteInside(package, PackagedSong);

        var ex = await Assert.ThrowsAsync<DcSongReadException>(() =>
            Unpacker(new FakeDecoder(available: false)).UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken));

        Assert.Equal(DcSongReadFailure.ChecksumMismatch, ex.Reason);
        Assert.Empty(Directory.EnumerateFileSystemEntries(BaseDir));
    }

    [Fact]
    public async Task APreCreatedEmptyFolder_IsUsed_AndKeptAfterAFailure()
    {
        // NewProjectFolder's rule: an existing EMPTY folder is free (users make the
        // destination in the picker). A failed open empties it again but leaves it.
        var empty = Directory.CreateDirectory(Path.Combine(BaseDir, "The Artist - The Song")).FullName;
        var package = BuildProjectPackage(_dir, lossless: false);

        await Assert.ThrowsAsync<DcSongReadException>(() =>
            Unpacker(new FakeDecoder(available: true, fail: true)).UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(empty));

        var result = await Unpacker(new FakeDecoder(available: true)).UnpackAsync(package, BaseDir, TestContext.Current.CancellationToken);
        Assert.Equal(empty, result.ProjectDirectory);
    }

    [Theory]
    [InlineData("AC/DC", "T.N.T.", "AC_DC - T.N.T")]
    [InlineData("  A  ", " B ", "A - B")]
    [InlineData("a<b", "c:d?", "a_b - c_d_")]
    public void TheFolderName_IsArtistDashTitle_WithUnsafeCharactersReplaced(string artist, string title, string expected) =>
        Assert.Equal(expected, DcSongProjectUnpacker.FolderName(artist, title));

    private static DcSongProjectUnpacker Unpacker(FakeDecoder decoder) => new(new FakeAppLogger(), () => decoder);
}
