using DotCharter.Services.Library;
using DotCharter.Services.Library.Index;
using DotCharter.Tests.Services.Library;
using static DotCharter.Tests.Services.Package.DcSong.DcSongTestKit;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>The tests of <see cref="DcSongLibraryScanTests" /> that need no package reader; its fixtures are in <c>DcSongLibraryScanTests.Fixtures.cs</c>.</summary>
public sealed partial class DcSongLibraryScanTests
{
    [Fact]
    public async Task A_package_is_listed_with_its_own_kind_and_the_facts_its_chart_holds()
    {
        DcSongLibraryKit.Write(P("Deftones - My Own Summer.dcsong"), DcSongLibraryKit.Song("My Own Summer", "Deftones"),
            "MyOwnSummer", lossless: true, cover: true);

        var package = Assert.Single(await Service().ScanAsync(LibraryDir));

        Assert.Equal(LibrarySourceKind.DcSong, package.SourceKind);
        Assert.Equal(P("Deftones - My Own Summer.dcsong"), package.PackagePath);
        Assert.Equal("MyOwnSummer", package.SongKey);
        Assert.Equal(("My Own Summer", "Deftones", "Around the Fur", 1997, "Alex"),
            (package.Title, package.Artist, package.Album, package.Year, package.Charter));
        Assert.Equal(181.2345, package.SongLength, 6);
        Assert.Equal("audio/song.flac", package.SongBank);
        Assert.False(package.NeedsAudioDecoder);
        Assert.Equal("images/cover.png", package.CoverImageEntryPath);
        Assert.True(package.CanLoad);
        // The song's tuning is its guitar's; every path keeps its own.
        Assert.Equal(("D A D G B E", 2), (package.TuningSummary, package.CapoSummary));
        Assert.Equal(new[] { "Lead", "Bass", "Vocals" }, package.Arrangements.Select(a => a.PathLabel));
        var lead = package.Arrangements[0];
        Assert.Equal((1, "D A D G B E", 2, 12), (lead.RouteMask, lead.Tuning, lead.Capo, lead.NoteCount));
        var bass = package.Arrangements[1];
        Assert.Equal((4, "E A D G", 0, 7), (bass.RouteMask, bass.Tuning, bass.Capo, bass.NoteCount));
        // A package's chart is read at scan, so it is rated at once.
        Assert.True(package.Difficulty > 0);
    }

    [Fact]
    public async Task A_damaged_or_tampered_package_is_skipped_with_its_reason_and_the_rest_are_listed()
    {
        DcSongLibraryKit.Write(P("good.dcsong"), DcSongLibraryKit.Song("Good", "Band"), "Good", lossless: true);
        var tampered = DcSongLibraryKit.Write(P("tampered.dcsong"), DcSongLibraryKit.Song("Tampered", "Band"), "Tampered", lossless: true,
            audio: RandomBytes(8000, 11));
        FlipByteInside(tampered, RandomBytes(8000, 11));
        var truncated = DcSongLibraryKit.Write(P("truncated.dcsong"), DcSongLibraryKit.Song("Truncated", "Band"), "Truncated", lossless: true);
        var bytes = File.ReadAllBytes(truncated);
        File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);
        File.WriteAllText(P("not-a-package.dcsong"), "hello");

        var songs = await Service().ScanAsync(LibraryDir);

        Assert.Equal("Good", Assert.Single(songs).Title);
        Assert.True(_logger.HasEntryContaining("WARN", "scan skipped '" + tampered + "'"));
        Assert.True(_logger.HasEntryContaining("WARN", "DcSongReadException"));
        Assert.True(_logger.HasEntryContaining("WARN", "does not match"));
        Assert.True(_logger.HasEntryContaining("WARN", "scan skipped '" + truncated + "'"));
        Assert.True(_logger.HasEntryContaining("WARN", "scan skipped '" + P("not-a-package.dcsong") + "'"));
    }

    [Fact]
    public async Task The_index_serves_an_unchanged_package_and_rereads_one_whose_file_changed()
    {
        var path = DcSongLibraryKit.Write(P("song.dcsong"), DcSongLibraryKit.Song("First Cut", "Band"), "Song", lossless: false);
        DcSongLibraryKit.Write(P("other.dcsong"), DcSongLibraryKit.Song("Other", "Band"), "Other", lossless: true);
        var index = new SqliteLibraryIndex(_logger, Path.Combine(_dir, "library-index.db"));
        var parsed = new List<string>();

        var cold = await Service(index, parsed).ScanAsync(LibraryDir);
        Assert.Equal(2, parsed.Count);

        parsed.Clear();
        var warm = await Service(index, parsed).ScanAsync(LibraryDir);
        Assert.Empty(parsed);
        var served = Assert.Single(warm, s => s.SongKey == "Song");
        // What the index serves back says what the package says: its kind, its codec, its paths.
        Assert.Equal(LibrarySourceKind.DcSong, served.SourceKind);
        Assert.True(served.NeedsAudioDecoder);
        Assert.Equal(Describe(cold), Describe(warm));

        // The same path, a different file: re-read, and only that one.
        DcSongLibraryKit.Write(path + ".new", DcSongLibraryKit.Song("Second Cut", "Band"), "Song", lossless: false);
        File.Move(path + ".new", path, overwrite: true);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        var rescanned = await Service(index, parsed).ScanAsync(LibraryDir);
        Assert.Equal(new[] { path }, parsed);
        Assert.Equal("Second Cut", Assert.Single(rescanned, s => s.SongKey == "Song").Title);

        // A package that is gone leaves the index with its file.
        File.Delete(path);
        parsed.Clear();
        var afterDelete = await Service(index, parsed).ScanAsync(LibraryDir);
        Assert.Empty(parsed);
        Assert.Equal("Other", Assert.Single(afterDelete).Title);
        Assert.Equal(new LibraryIndexFacts(1, 1), index.ReadFacts());
    }

    [Fact]
    public async Task An_opus_package_shows_blocked_without_ffmpeg_and_a_flac_package_does_not()
    {
        DcSongLibraryKit.Write(P("opus.dcsong"), DcSongLibraryKit.Song("Opus Song", "Band"), "Opus", lossless: false);
        DcSongLibraryKit.Write(P("flac.dcsong"), DcSongLibraryKit.Song("Flac Song", "Band"), "Flac", lossless: true);
        var decoder = new FakeDecoder(available: false);
        var packages = DcSongLibraryKit.Source(Path.Combine(_dir, "cache"), decoder, _logger);
        var opus = new LibrarySongEntry { PackagePath = P("opus.dcsong"), SongBank = "audio/song.opus" };

        // Before any probe a row shows live (a load would then say why).
        Assert.Equal(LibrarySongBlock.None, packages.BlockFor(opus));

        var songs = await TestLibraries.Create(_logger, null, Cache(), packages).ScanAsync(LibraryDir);

        Assert.Equal(LibrarySongBlock.AudioDecoderMissing, packages.BlockFor(Assert.Single(songs, s => s.SongKey == "Opus")));
        Assert.Equal(LibrarySongBlock.None, packages.BlockFor(Assert.Single(songs, s => s.SongKey == "Flac")));
        // A Rocksmith archive is never the decoder's business.
        Assert.Equal(LibrarySongBlock.None, packages.BlockFor(new LibrarySongEntry { PackagePath = P("x.psarc"), SongBank = "x.opus" }));

        var found = DcSongLibraryKit.Source(Path.Combine(_dir, "cache"), new FakeDecoder(available: true), _logger);
        found.ProbeDecoder();
        Assert.Equal(LibrarySongBlock.None, found.BlockFor(Assert.Single(songs, s => s.SongKey == "Opus")));
    }
}
