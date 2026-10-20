using DotCharter.Services.Library;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Library.Cache;
using DotCharter.Services.Library.Prefetch;
using DotCharter.Tests.Services.Library;
using DotCharter.Tone.Infrastructure.Storage;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>
///     Opening a library <c>.dcsong</c> to play (#708): the chart straight from the package, the
///     audio once into the bounded decoded-audio cache (FLAC copied, Opus decoded), the stems
///     decoded for the mixer with the package's mixer state, and the cache keyed by the
///     package's path and last-write time. Hand-built packages, a fake decoder, a temp cache
///     and a redirected data root: no ffmpeg, and nothing of the user's is touched.
/// </summary>
public sealed class DcSongLibraryLoadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_dcsong_play_").FullName;
    private readonly FakeAppLogger _logger = new();

    public DcSongLibraryLoadTests() => Directory.CreateDirectory(LibraryDir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string LibraryDir => Path.Combine(_dir, "library");

    private string CacheRoot => Path.Combine(_dir, "cache");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_flac_package_plays_its_own_bytes_from_the_cache_with_its_sections_and_its_stems()
    {
        var (service, entry) = Scanned(lossless: true, stems: true, cover: true);

        var loaded = await service.LoadSongAsync(entry);

        var folder = Path.Combine(CacheRoot, LibraryAudioCacheStore.DirectoryNameFor(entry.PackagePath));
        Assert.Equal(Path.Combine(folder, "song.flac"), loaded.PlayableAudioPath);
        // The engine decodes FLAC: the stored member, verified, byte for byte.
        Assert.Equal(PackagedSong, File.ReadAllBytes(loaded.PlayableAudioPath));
        Assert.False(loaded.AudioConverted);
        Assert.Equal("audio/song.flac", loaded.AudioEntryPath);
        Assert.Equal(Path.Combine(folder, LibraryAudioCacheStore.SourceMarkerFileName), Directory.GetFiles(folder, ".psarcsource").Single());

        var project = loaded.Project;
        Assert.Equal("song.flac", project.AudioFileName);
        Assert.Equal("", project.Chart.CoverArtPath);
        // The song map comes from the chart: its sections and phrases travel in it.
        var lead = project.Arrangements.Single(a => a.Name == "Lead");
        Assert.Equal(DcSongLibraryKit.Sections, lead.EventPoints.Select(e => e.Section));
        Assert.Equal(64, project.Beats.Count);

        // The packaged stem feeds the mixer: decoded beside the song, mixer state kept.
        var stem = Assert.Single(project.Stems);
        Assert.Equal(("drums", Path.Combine(folder, "package-stems", "drums.wav"), 0.25, true),
            (stem.Name, stem.Path, stem.Volume, stem.Muted));
        Assert.Equal(FakeDecoder.Decoded(PackagedDrums), File.ReadAllBytes(stem.Path));
        // Staging names never outlive a write.
        Assert.DoesNotContain(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories), f => f.Contains(".part-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_opus_package_is_decoded_once_and_a_second_open_is_a_hit()
    {
        var (service, entry) = Scanned(lossless: false, stems: true);

        var first = await service.LoadSongAsync(entry);
        var written = File.GetLastWriteTimeUtc(first.PlayableAudioPath);
        var stemWritten = File.GetLastWriteTimeUtc(first.Project.Stems.Single().Path);
        var second = await service.LoadSongAsync(entry);

        Assert.Equal("song.wav", Path.GetFileName(first.PlayableAudioPath));
        Assert.True(first.AudioConverted);
        Assert.Equal(FakeDecoder.Decoded(PackagedSong), File.ReadAllBytes(first.PlayableAudioPath));
        Assert.Equal(first.PlayableAudioPath, second.PlayableAudioPath);
        Assert.Equal(written, File.GetLastWriteTimeUtc(second.PlayableAudioPath));
        Assert.Equal(stemWritten, File.GetLastWriteTimeUtc(second.Project.Stems.Single().Path));
        Assert.True(_logger.HasEntryContaining("INFO", "cached=True"));
    }

    [Fact]
    public async Task An_opus_package_without_ffmpeg_is_refused_and_writes_nothing()
    {
        var (service, entry) = Scanned(lossless: false, decoder: new FakeDecoder(available: false));

        var refused = await Assert.ThrowsAsync<DcSongReadException>(() => service.LoadSongAsync(entry));

        Assert.Equal(DcSongReadFailure.DecoderMissing, refused.Reason);
        Assert.False(Directory.Exists(CacheRoot) && Directory.EnumerateFiles(CacheRoot, "song.*", SearchOption.AllDirectories).Any());
        Assert.Equal(LibraryAudioPrepareOutcome.NoAudio, service.PrepareAudio(entry, Ct));
    }

    [Fact]
    public async Task A_flac_package_without_ffmpeg_plays_without_its_stems()
    {
        var (service, entry) = Scanned(lossless: true, stems: true, decoder: new FakeDecoder(available: false));

        var loaded = await service.LoadSongAsync(entry);

        Assert.Equal(PackagedSong, File.ReadAllBytes(loaded.PlayableAudioPath));
        Assert.Empty(loaded.Project.Stems);
        Assert.True(_logger.HasEntryContaining("INFO", "skippedStems=[drums]"));
    }

    [Fact]
    public async Task A_package_tampered_after_the_scan_is_refused_at_open_and_leaves_no_audio()
    {
        var audio = RandomBytes(9000, 21);
        var (service, entry) = Scanned(lossless: true, audio: audio);
        FlipByteInside(entry.PackagePath, audio);

        var refused = await Assert.ThrowsAsync<DcSongReadException>(() => service.LoadSongAsync(entry));

        Assert.Equal(DcSongReadFailure.ChecksumMismatch, refused.Reason);
        Assert.Empty(Directory.EnumerateFiles(CacheRoot, "song*", SearchOption.AllDirectories));
    }

    /// <summary>The cache is keyed by the package's path and last-write time: a package
    /// rewritten in place plays its NEW audio from a new folder, never the old decode.</summary>
    [Fact]
    public async Task A_package_that_changed_on_disk_plays_its_new_audio_from_a_new_folder()
    {
        var (service, entry) = Scanned(lossless: true);
        var before = await service.LoadSongAsync(entry);

        var newAudio = RandomBytes(7000, 31);
        var rewritten = DcSongLibraryKit.Write(entry.PackagePath + ".new", DcSongLibraryKit.Song("Song", "Band"), "Song", lossless: true, audio: newAudio);
        File.Move(rewritten, entry.PackagePath, overwrite: true);
        File.SetLastWriteTimeUtc(entry.PackagePath, DateTime.UtcNow.AddMinutes(5));
        var after = await service.LoadSongAsync(entry);

        Assert.NotEqual(Path.GetDirectoryName(before.PlayableAudioPath), Path.GetDirectoryName(after.PlayableAudioPath));
        Assert.Equal(newAudio, File.ReadAllBytes(after.PlayableAudioPath));
    }

    [Fact]
    public async Task Convert_on_select_writes_the_song_an_open_then_finds()
    {
        var (service, entry) = Scanned(lossless: false);

        Assert.Equal(LibraryAudioPrepareOutcome.Prepared, service.PrepareAudio(entry, Ct));
        var folder = Path.Combine(CacheRoot, LibraryAudioCacheStore.DirectoryNameFor(entry.PackagePath));
        var wav = Path.Combine(folder, "song.wav");
        var written = File.GetLastWriteTimeUtc(wav);
        var loaded = await service.LoadSongAsync(entry);

        Assert.Equal(wav, loaded.PlayableAudioPath);
        Assert.Equal(written, File.GetLastWriteTimeUtc(wav));
    }

    [Fact]
    public async Task The_cover_is_saved_once_into_the_album_art_cache()
    {
        using var dataRoot = UserDataFolders.UseDataRootForTesting(Path.Combine(_dir, "data"));
        var (service, entry) = Scanned(lossless: true, cover: true);

        var cover = await service.LoadCoverImageAsync(entry);

        Assert.StartsWith(Path.Combine(_dir, "data"), cover, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DcSongLibraryKit.CoverPng, File.ReadAllBytes(cover));
        Assert.Equal(cover, await service.LoadCoverImageAsync(entry));
        Assert.Equal("", await service.LoadCoverImageAsync(Scanned(lossless: true, name: "plain").Entry));
    }

    private (LibraryService Service, LibrarySongEntry Entry) Scanned(
        bool lossless, bool stems = false, bool cover = false, FakeDecoder? decoder = null, byte[]? audio = null, string name = "song")
    {
        var path = DcSongLibraryKit.Write(Path.Combine(LibraryDir, name + ".dcsong"), DcSongLibraryKit.Song("Song", "Band"), "Song",
            lossless, cover, stems, audio);
        var packages = DcSongLibraryKit.Source(CacheRoot, decoder ?? new FakeDecoder(available: true), _logger);
        var service = TestLibraries.Create(_logger, null, new LibraryAudioCacheStore(_logger, CacheRoot), packages);
        return (service, Assert.Single(packages.ReadForIndex(path)));
    }
}
