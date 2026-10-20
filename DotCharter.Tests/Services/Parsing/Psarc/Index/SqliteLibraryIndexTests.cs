using System.Text.Json;
using DotCharter.Services.Library;
using DotCharter.Services.Library.Index;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace DotCharter.Tests.Services.Parsing.Psarc.Index;

public sealed class SqliteLibraryIndexTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-libindex-").FullName;
    private readonly FakeAppLogger _logger = new();

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string DbPath => Path.Combine(_tempDir, "library-index.db");

    private SqliteLibraryIndex CreateIndex() => new(_logger, DbPath);

    private static LibrarySongEntry MakeSong(string psarcPath, string key, string coverImagePath = "",
        double difficulty = 0)
    {
        return new LibrarySongEntry
        {
            PackagePath = psarcPath,
            SongKey = key,
            Difficulty = difficulty,
            SongBank = $"song_{key.ToLowerInvariant()}.bnk",
            Title = $"Title {key}",
            Artist = $"Artist {key}",
            Album = $"Album {key}",
            Year = 1987,
            AverageTempo = 128.5,
            SongLength = 245.75,
            TuningSummary = "Eb Standard",
            CapoSummary = 2,
            CoverImagePath = coverImagePath,
            CoverImageEntryPath = $"gfxassets/album_art/album_{key.ToLowerInvariant()}_256.dds",
            Arrangements =
            [
                new LibraryArrangementEntry
                {
                    ArrangementName = "Lead",
                    PersistentId = "AAAA1111BBBB2222",
                    SngPath = $"songs/bin/generic/{key.ToLowerInvariant()}_lead.sng",
                    Tones = new Dictionary<int, string> { [0] = "Tone_A_Name", [2] = "Tone_C_Name" },
                    RouteMask = 1,
                    Represent = true,
                    BonusArr = false
                },
                new LibraryArrangementEntry
                {
                    ArrangementName = "Rhythm",
                    PersistentId = "CCCC3333DDDD4444",
                    SngPath = $"songs/bin/generic/{key.ToLowerInvariant()}_rhythm.sng",
                    Tones = new Dictionary<int, string>(),
                    RouteMask = 2,
                    Represent = false,
                    BonusArr = true
                }
            ]
        };
    }

    private static void AssertSongsEqual(LibrarySongEntry expected, LibrarySongEntry actual)
    {
        Assert.Equal(expected.PackagePath, actual.PackagePath);
        Assert.Equal(expected.SongKey, actual.SongKey);
        Assert.Equal(expected.SongBank, actual.SongBank);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.Artist, actual.Artist);
        Assert.Equal(expected.Album, actual.Album);
        Assert.Equal(expected.Year, actual.Year);
        Assert.Equal(expected.AverageTempo, actual.AverageTempo);
        Assert.Equal(expected.SongLength, actual.SongLength);
        Assert.Equal(expected.TuningSummary, actual.TuningSummary);
        Assert.Equal(expected.CapoSummary, actual.CapoSummary);
        Assert.Equal(expected.CoverImageEntryPath, actual.CoverImageEntryPath);
        Assert.Equal(expected.Arrangements.Count, actual.Arrangements.Count);
        for (var i = 0; i < expected.Arrangements.Count; i++)
        {
            var e = expected.Arrangements[i];
            var a = actual.Arrangements[i];
            Assert.Equal(e.ArrangementName, a.ArrangementName);
            Assert.Equal(e.PersistentId, a.PersistentId);
            Assert.Equal(e.SngPath, a.SngPath);
            Assert.Equal(e.RouteMask, a.RouteMask);
            Assert.Equal(e.Represent, a.Represent);
            Assert.Equal(e.BonusArr, a.BonusArr);
            Assert.Equal(
                e.Tones.OrderBy(t => t.Key).ToList(),
                a.Tones.OrderBy(t => t.Key).ToList());
        }
    }

    [Fact]
    public void Round_trips_songs_by_size_and_mtime()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");
        var songA = MakeSong(psarcPath, "Alpha");
        var songB = MakeSong(psarcPath, "Beta");

        using (var scan = CreateIndex().BeginScan())
        {
            Assert.NotNull(scan);
            scan.UpsertFile(psarcPath, 1234, 5678, [songA, songB]);
        }

        // Fresh session (fresh connection) so we prove persistence, not caching.
        using var read = CreateIndex().BeginScan();
        Assert.NotNull(read);
        Assert.True(read.TryGetCachedSongs(psarcPath, 1234, 5678, out var cached));
        Assert.Equal(2, cached.Count);
        AssertSongsEqual(songA, cached[0]);
        AssertSongsEqual(songB, cached[1]);
    }

    [Fact]
    public void Hits_with_empty_list_for_an_indexed_archive_without_songs()
    {
        var psarcPath = Path.Combine(_tempDir, "empty.psarc");

        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(psarcPath, 10, 20, []);
        }

        using var read = CreateIndex().BeginScan();
        Assert.True(read!.TryGetCachedSongs(psarcPath, 10, 20, out var cached));
        Assert.Empty(cached);
    }

    [Fact]
    public void Misses_when_mtime_or_size_changed()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using var scan = CreateIndex().BeginScan();
        scan!.UpsertFile(psarcPath, 1234, 5678, [MakeSong(psarcPath, "Alpha")]);

        Assert.False(scan.TryGetCachedSongs(psarcPath, 1234, 9999, out _)); // mtime changed
        Assert.False(scan.TryGetCachedSongs(psarcPath, 4321, 5678, out _)); // size changed
        Assert.False(scan.TryGetCachedSongs(Path.Combine(_tempDir, "other.psarc"), 1234, 5678, out _)); // unknown
        Assert.True(scan.TryGetCachedSongs(psarcPath, 1234, 5678, out _)); // sanity: exact match still hits
    }

    [Fact]
    public void Upsert_replaces_previous_rows_for_the_same_file()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using var scan = CreateIndex().BeginScan();
        scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Old1"), MakeSong(psarcPath, "Old2")]);
        scan.UpsertFile(psarcPath, 2, 2, [MakeSong(psarcPath, "New")]);

        Assert.False(scan.TryGetCachedSongs(psarcPath, 1, 1, out _));
        Assert.True(scan.TryGetCachedSongs(psarcPath, 2, 2, out var cached));
        Assert.Equal(["New"], cached.Select(s => s.SongKey));
    }

    [Fact]
    public void Purges_deleted_files_only_under_the_scanned_root()
    {
        var rootA = Path.Combine(_tempDir, "rootA");
        var rootB = Path.Combine(_tempDir, "rootB");
        var kept = Path.Combine(rootA, "kept.psarc");
        var deleted = Path.Combine(rootA, "deleted.psarc");
        var otherRoot = Path.Combine(rootB, "other.psarc");

        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(kept, 1, 1, [MakeSong(kept, "Kept")]);
            scan.UpsertFile(deleted, 2, 2, [MakeSong(deleted, "Deleted")]);
            scan.UpsertFile(otherRoot, 3, 3, [MakeSong(otherRoot, "Other")]);

            scan.PurgeMissing(rootA, [kept]);
        }

        using var read = CreateIndex().BeginScan();
        Assert.True(read!.TryGetCachedSongs(kept, 1, 1, out _));
        Assert.False(read.TryGetCachedSongs(deleted, 2, 2, out _));
        // A different library folder's cache survives scans of rootA.
        Assert.True(read.TryGetCachedSongs(otherRoot, 3, 3, out _));
    }

    /// <summary>
    ///     A library pointed at a whole DRIVE still purges.
    ///     <para>
    ///         <c>Path.TrimEndingDirectorySeparator</c> does not trim the
    ///         separator that IS the root, so <c>"D:\"</c> came back unchanged
    ///         and the code appended a second one. No enumerated path starts
    ///         with <c>"D:\\"</c>, so the purge found nothing stale, deleted
    ///         nothing, and did not even log that it had run — for the whole
    ///         life of the install. A dedicated CDLC volume is an ordinary way
    ///         to keep a library, and UNC roots hid it (the trim does bite on
    ///         <c>"\\srv\share\"</c>).
    ///     </para>
    ///     <para>
    ///         `PurgeMissing` touches no filesystem, so this needs no real
    ///         <c>D:</c> drive.
    ///     </para>
    /// </summary>
    [Fact]
    public void Purges_deleted_files_when_the_scanned_root_is_a_drive_root()
    {
        const string kept = @"D:\kept.psarc";
        const string deleted = @"D:\gone.psarc";

        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(kept, 1, 1, [MakeSong(kept, "Kept")]);
            scan.UpsertFile(deleted, 2, 2, [MakeSong(deleted, "Gone")]);

            scan.PurgeMissing(@"D:\", [kept]);
        }

        using var read = CreateIndex().BeginScan();
        Assert.True(read!.TryGetCachedSongs(kept, 1, 1, out _));
        Assert.False(read.TryGetCachedSongs(deleted, 2, 2, out _));
    }

    /// <summary>
    ///     A case-only rename leaves no zombie row.
    ///     <para>
    ///         <c>psarc_path</c> is a TEXT PRIMARY KEY under SQLite's BINARY
    ///         collation and every read and write addresses it ORDINALLY — the
    ///         point lookups with <c>=</c>, the prefetch dictionaries with
    ///         <c>StringComparer.Ordinal</c> (deliberately, per their own
    ///         comment). Only the purge's present-set was
    ///         <c>OrdinalIgnoreCase</c>. Renaming <c>Song.psarc</c> to
    ///         <c>song.psarc</c> — legal on NTFS, and it changes neither size
    ///         nor mtime — therefore missed the cache, inserted a SECOND row
    ///         (the DELETE in <c>UpsertFile</c> is ordinal too), and the purge
    ///         then declared the old row "present" and kept it. Never hit,
    ///         never purged, and carried in every later scan's bulk read.
    ///     </para>
    /// </summary>
    [Fact]
    public void Purges_a_row_whose_path_differs_from_the_present_file_only_by_case()
    {
        var root = Path.Combine(_tempDir, "lib");
        var oldCasing = Path.Combine(root, "Song.psarc");
        var newCasing = Path.Combine(root, "song.psarc");

        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(oldCasing, 1, 1, [MakeSong(oldCasing, "Song")]);
            scan.UpsertFile(newCasing, 1, 1, [MakeSong(newCasing, "Song")]);

            scan.PurgeMissing(root, [newCasing]);
        }

        using var read = CreateIndex().BeginScan();
        Assert.True(read!.TryGetCachedSongs(newCasing, 1, 1, out _));
        Assert.False(read.TryGetCachedSongs(oldCasing, 1, 1, out _));
    }

    [Fact]
    public void Corrupt_database_is_deleted_and_rebuilt_with_a_warning()
    {
        // Not a SQLite file at all.
        File.WriteAllText(DbPath, "this is definitely not a sqlite database, not even close.");

        using (var scan = CreateIndex().BeginScan())
        {
            Assert.NotNull(scan); // rebuilt from scratch
            var psarcPath = Path.Combine(_tempDir, "pack.psarc");
            scan.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
            Assert.True(scan.TryGetCachedSongs(psarcPath, 1, 1, out _));
        }

        Assert.True(_logger.HasEntryContaining("WARN", "library index unreadable"));
    }

    [Fact]
    public void Schema_version_mismatch_drops_and_rebuilds()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");
        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        }

        // Simulate a db written by a different (newer/older) app version.
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '9999' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        using var read = CreateIndex().BeginScan();
        Assert.NotNull(read); // usable after the silent rebuild...
        Assert.False(read.TryGetCachedSongs(psarcPath, 1, 1, out _)); // ...but the old rows are gone

        // And the rebuilt schema works end to end.
        read.UpsertFile(psarcPath, 2, 2, [MakeSong(psarcPath, "Beta")]);
        Assert.True(read.TryGetCachedSongs(psarcPath, 2, 2, out var cached));
        Assert.Equal("Beta", cached[0].SongKey);
    }

    [Fact]
    public void Cover_image_path_is_revalidated_on_read()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");
        var coverPath = Path.Combine(_tempDir, "cover.png");
        File.WriteAllBytes(coverPath, [1, 2, 3]);

        using var scan = CreateIndex().BeginScan();
        scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha", coverPath)]);

        Assert.True(scan.TryGetCachedSongs(psarcPath, 1, 1, out var cached));
        Assert.Equal(coverPath, cached[0].CoverImagePath); // file exists → kept

        File.Delete(coverPath);
        Assert.True(scan.TryGetCachedSongs(psarcPath, 1, 1, out cached));
        Assert.Equal("", cached[0].CoverImagePath); // stale extraction → cleared
    }

    /// <summary>Home's hero asks the index for a cover by song key: the first pack's cover still
    /// on disk wins, and a key with none (or a deleted png) answers "".</summary>
    [Fact]
    public void FindCoverImage_answers_the_first_cover_still_on_disk()
    {
        var packA = Path.Combine(_tempDir, "a.psarc");
        var packB = Path.Combine(_tempDir, "b.psarc");
        var goneCover = Path.Combine(_tempDir, "gone.png");
        var liveCover = Path.Combine(_tempDir, "live.png");
        File.WriteAllBytes(goneCover, [1]);
        File.WriteAllBytes(liveCover, [1]);

        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(packA, 1, 1, [MakeSong(packA, "Alpha", goneCover), MakeSong(packA, "Bare")]);
            scan.UpsertFile(packB, 1, 1, [MakeSong(packB, "Alpha", liveCover)]);
        }

        File.Delete(goneCover);
        var index = CreateIndex();

        Assert.Equal(liveCover, index.FindCoverImage("Alpha"));
        Assert.Equal("", index.FindCoverImage("Bare"));
        Assert.Equal("", index.FindCoverImage("Unknown"));
        Assert.Equal("", index.FindCoverImage(""));
    }

    /// <summary>A row indexed before its cover was ever shown has no cover path (the owner's whole
    /// library read 0 of 157); the browser has usually extracted it since, under a derived name.</summary>
    [Fact]
    public void FindCoverImage_falls_back_to_the_extracted_cover()
    {
        var pack = Path.Combine(_tempDir, "a.psarc");
        var extracted = Path.Combine(_tempDir, "a_Bare_art.png");
        using (var scan = CreateIndex().BeginScan())
            scan!.UpsertFile(pack, 1, 1, [MakeSong(pack, "Bare"), MakeSong(pack, "Never")]);

        File.WriteAllBytes(extracted, [1]);
        string Cache(string psarc, string key) =>
            psarc == pack ? Path.Combine(_tempDir, $"a_{key}_art.png") : "";

        Assert.Equal(extracted, CreateIndex().FindCoverImage("Bare", Cache));
        Assert.Equal("", CreateIndex().FindCoverImage("Never", Cache));
    }

    [Fact]
    public void Interrupted_scan_leaves_completed_files_readable()
    {
        // Per-file transactional upserts: a scan cancelled after file A but
        // before file B must leave A fully served on the next scan and B
        // simply absent — never a torn half-file.
        var fileA = Path.Combine(_tempDir, "a.psarc");
        var fileB = Path.Combine(_tempDir, "b.psarc");

        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(fileA, 1, 1, [MakeSong(fileA, "Alpha"), MakeSong(fileA, "Alpha2")]);
            // Scan cancelled here — fileB never gets upserted; the session is
            // disposed exactly like a package library scan's using-scope does.
        }

        using var read = CreateIndex().BeginScan();
        Assert.True(read!.TryGetCachedSongs(fileA, 1, 1, out var cached));
        Assert.Equal(2, cached.Count);
        Assert.False(read.TryGetCachedSongs(fileB, 2, 2, out _));
    }

    [Fact]
    public void Rating_defaults_to_zero_and_survives_a_round_trip()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using (var scan = CreateIndex().BeginScan())
        {
            // Scan-written rows carry no rating (a .hsan has no note data).
            scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
            Assert.True(scan.TryGetCachedSongs(psarcPath, 1, 1, out var fresh));
            Assert.Equal(0.0, fresh[0].Difficulty);
        }

        // A load-time rating write is a separate, best-effort cache update.
        CreateIndex().TrySetSongRating(psarcPath, "Alpha", 6.8);

        using var read = CreateIndex().BeginScan();
        Assert.True(read!.TryGetCachedSongs(psarcPath, 1, 1, out var cached));
        Assert.Equal(6.8, cached[0].Difficulty, 3);
    }

    /// <summary>
    ///     A schema bump DROPS the cache — that is the migration path, and it is
    ///     free for everything a scan can re-derive. A difficulty RATING cannot
    ///     be re-derived: a .hsan carries no note data, so it is only written
    ///     when a song is actually LOADED, and a wiped rating is re-earned one
    ///     song at a time. The v5→v6 bump was a derivation fix that changed no
    ///     column, so on a 4,000-song library it would have silently cost months
    ///     of accumulated ratings. `song_ratings` survives the drop.
    /// </summary>
    [Fact]
    public void A_schema_rebuild_preserves_ratings_it_cannot_recompute()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using (var scan = CreateIndex().BeginScan())
            scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        CreateIndex().TrySetSongRating(psarcPath, "Alpha", 7.25);

        // Force the rebuild the way a version bump does: leave the file exactly
        // as it is and make its recorded schema stale.
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        var carried = ReadCarriedRating(psarcPath, "Alpha");
        Assert.True(carried is > 7.2 and < 7.3,
            $"HARVEST: song_ratings holds {(carried is null ? "no row" : carried.ToString())} for Alpha, expected 7.25");

        // Opening a scan runs EnsureSchema, which drops and rebuilds. The cached
        // rows are gone (the cache is re-derivable, so that is correct)…
        using (var scan = CreateIndex().BeginScan())
        {
            Assert.False(scan!.TryGetCachedSongs(psarcPath, 1, 1, out _));
            // …and re-scanning restores the row WITH its rating, which the scan
            // itself does not know (it supplies 0).
            scan.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        }

        using var read = CreateIndex().BeginScan();
        Assert.True(read!.TryGetCachedSongs(psarcPath, 1, 1, out var cached));
        Assert.Equal(7.25, cached[0].Difficulty, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Purge_after_schema_rebuild_removes_only_missing_archives_carried_ratings(bool present)
    {
        var outsidePath = Path.Combine(_tempDir, "other-root", "outside.psarc");
        var root = Path.Combine(_tempDir, "library");
        var path = Path.Combine(root, "pack.psarc");
        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(path, 1, 1, [MakeSong(path, "Alpha")]);
            scan.UpsertFile(outsidePath, 1, 1, [MakeSong(outsidePath, "Outside")]);
        }
        CreateIndex().TrySetSongRating(path, "Alpha", 7.25);
        CreateIndex().TrySetSongRating(outsidePath, "Outside", 6);
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            command.ExecuteNonQuery();
        }

        using (var scan = CreateIndex().BeginScan())
        {
            Assert.NotNull(scan);
            Assert.False(scan.TryGetCachedSongs(path, 1, 1, out _));
            scan.PurgeMissing(root, present ? [path] : []);
            Assert.Equal(present ? 7.25 : null, ReadCarriedRating(path, "Alpha"));
            Assert.Equal(6, ReadCarriedRating(outsidePath, "Outside"));
            scan.UpsertFile(path, 2, 2, [MakeSong(path, "Alpha")]);
            Assert.True(scan.TryGetCachedSongs(path, 2, 2, out var songs));
            Assert.Equal(present ? 7.25 : 0, Assert.Single(songs).Difficulty);
        }
    }

    /// <summary>Reads the carry-over table directly, so a failure says WHICH
    /// half broke: the harvest into it, or the restore out of it.</summary>
    private double? ReadCarriedRating(string psarcPath, string songKey)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT rating FROM song_ratings WHERE psarc_path = $p AND song_key = $k;";
        command.Parameters.AddWithValue("$p", psarcPath);
        command.Parameters.AddWithValue("$k", songKey);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToDouble(value);
    }

    [Fact]
    public void UpsertFile_carries_a_known_rating_through_a_reparse()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");
        var rated = MakeSong(psarcPath, "Alpha", difficulty: 4.5);

        using var scan = CreateIndex().BeginScan();
        scan!.UpsertFile(psarcPath, 1, 1, [rated]);
        Assert.True(scan.TryGetCachedSongs(psarcPath, 1, 1, out var cached));
        Assert.Equal(4.5, cached[0].Difficulty, 3);
    }

    /// <summary>
    ///     A CDLC UPDATED IN PLACE does not keep the old chart's difficulty.
    ///     <para>
    ///         `song_ratings` is keyed by <c>(psarc_path, song_key)</c> alone —
    ///         no <c>(size, mtime)</c> dimension — so it was exempt from the
    ///         freshness check every other row obeys. A re-downloaded package
    ///         under the same name was correctly detected as stale and
    ///         re-parsed, and then had its PRE-UPDATE rating copied straight
    ///         back over the scan's 0 by the carry-over UPDATE. The library kept
    ///         showing the old difficulty until the user happened to open that
    ///         song again, which is the only thing that recomputes it.
    ///     </para>
    /// </summary>
    [Fact]
    public void An_archive_that_changed_under_the_same_path_loses_its_carried_rating()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using (var scan = CreateIndex().BeginScan())
            scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        CreateIndex().TrySetSongRating(psarcPath, "Alpha", 7.25);

        using (var scan = CreateIndex().BeginScan())
        {
            Assert.True(scan!.TryGetCachedSongs(psarcPath, 1, 1, out var before));
            Assert.Equal(7.25, before[0].Difficulty, 3);

            // Same path, new bytes: the archive was replaced.
            scan.UpsertFile(psarcPath, 2, 2, [MakeSong(psarcPath, "Alpha")]);
            Assert.True(scan.TryGetCachedSongs(psarcPath, 2, 2, out var after));
            Assert.Equal(0.0, after[0].Difficulty);
        }

        // …and the carry-over row is gone too, so it cannot come back through
        // the next schema rebuild (which finds no `files` row to judge it by).
        Assert.Null(ReadCarriedRating(psarcPath, "Alpha"));
    }

    /// <summary>
    ///     A PURGED archive takes its carried rating with it - and a replacement
    ///     dropped at the same path does not inherit it.
    ///     <para>
    ///         <c>PurgeMissing</c> deleted <c>songs</c> and <c>files</c> and left
    ///         <c>song_ratings</c> standing. But <c>UpsertFile</c> decides
    ///         whether to carry a rating over by asking whether a <c>files</c>
    ///         row exists, reading "none" as "a schema rebuild - carry it over".
    ///         The purge manufactured a THIRD producer of that state: the archive
    ///         was DELETED. So a psarc removed from the library and later
    ///         replaced at the same path - a re-downloaded CDLC, another cut
    ///         under the same file name; song keys are stable across revisions -
    ///         had the dead chart's difficulty stamped onto the new one, and it
    ///         stayed until the user happened to open that song.
    ///     </para>
    ///     <para>
    ///         The same statement is what stops the table growing forever:
    ///         nothing else ever removed a row for an archive that simply went
    ///         away.
    ///     </para>
    /// </summary>
    [Fact]
    public void A_purged_archive_does_not_leave_its_rating_for_the_next_file_at_that_path()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using (var scan = CreateIndex().BeginScan())
            scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        CreateIndex().TrySetSongRating(psarcPath, "Alpha", 7.25);
        Assert.Equal(7.25, ReadCarriedRating(psarcPath, "Alpha") ?? -1, 3);

        // The file is gone from disk: the next scan purges it.
        using (var scan = CreateIndex().BeginScan())
            scan!.PurgeMissing(_tempDir, []);

        Assert.Null(ReadCarriedRating(psarcPath, "Alpha"));

        // A DIFFERENT archive later lands at the same path with the same song
        // key. It must be rated 0 (unrated), not 7.25.
        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(psarcPath, 99, 99, [MakeSong(psarcPath, "Alpha")]);
            Assert.True(scan.TryGetCachedSongs(psarcPath, 99, 99, out var cached));
            Assert.Equal(0.0, cached[0].Difficulty);
        }
    }

    /// <summary>The purge above must not eat a rating it has no business
    /// eating: a file still PRESENT under the scanned root keeps its
    /// carry-over row, which is what makes it survive a schema rebuild.</summary>
    [Fact]
    public void A_present_archive_keeps_its_carried_rating_through_a_purge()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using (var scan = CreateIndex().BeginScan())
            scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        CreateIndex().TrySetSongRating(psarcPath, "Alpha", 7.25);

        using (var scan = CreateIndex().BeginScan())
            scan!.PurgeMissing(_tempDir, [psarcPath]);

        Assert.Equal(7.25, ReadCarriedRating(psarcPath, "Alpha") ?? -1, 3);
    }

    /// <summary>The mtime guard above must not cost the case it was built for:
    /// a rewrite of an UNCHANGED archive still carries the rating.</summary>
    [Fact]
    public void An_unchanged_archive_keeps_its_carried_rating_through_a_rewrite()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using (var scan = CreateIndex().BeginScan())
            scan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        CreateIndex().TrySetSongRating(psarcPath, "Alpha", 7.25);

        using var rescan = CreateIndex().BeginScan();
        rescan!.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        Assert.True(rescan.TryGetCachedSongs(psarcPath, 1, 1, out var cached));
        Assert.Equal(7.25, cached[0].Difficulty, 3);
    }

    [Fact]
    public void TrySetSongRating_is_a_no_op_for_an_unindexed_song()
    {
        // No db yet, and an unknown key — must never throw or create bogus rows.
        CreateIndex().TrySetSongRating(Path.Combine(_tempDir, "missing.psarc"), "Nope", 5.0);

        using (var scan = CreateIndex().BeginScan())
        {
            scan!.UpsertFile(Path.Combine(_tempDir, "pack.psarc"), 1, 1,
                [MakeSong(Path.Combine(_tempDir, "pack.psarc"), "Alpha")]);
        }

        // A rating for a key that was never indexed reaches no `songs` row. (It
        // is remembered in the carry-over table, so a later scan that DOES index
        // that key picks it up — but nothing here can observe it.)
        CreateIndex().TrySetSongRating(Path.Combine(_tempDir, "pack.psarc"), "Ghost", 9.0);

        using var read = CreateIndex().BeginScan();
        Assert.True(read!.TryGetCachedSongs(Path.Combine(_tempDir, "pack.psarc"), 1, 1, out var cached));
        Assert.Single(cached);
        Assert.Equal(0.0, cached[0].Difficulty);
    }

    [Fact]
    public void Two_concurrent_sessions_share_the_database()
    {
        // Two app instances scanning at once (WAL + busy_timeout, best effort):
        // a commit in one session must become visible to the other.
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");

        using var writer = CreateIndex().BeginScan();
        using var reader = CreateIndex().BeginScan();
        Assert.NotNull(writer);
        Assert.NotNull(reader);

        writer.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Alpha")]);
        Assert.True(reader.TryGetCachedSongs(psarcPath, 1, 1, out var cached));
        Assert.Equal("Alpha", cached[0].SongKey);
    }

    /// <summary>
    ///     One session answers MANY files, and since the two lookup statements are
    ///     prepared ONCE and re-executed with a rebound parameter (1,825 files on
    ///     the owner's library — a fresh command per file re-ran
    ///     <c>sqlite3_prepare_v2</c> ~3,650 times per scan), the failure mode of
    ///     that reuse is the one thing worth pinning: a parameter that did not
    ///     rebind would serve the PREVIOUS file's songs, silently and plausibly.
    /// </summary>
    [Fact]
    public void One_session_serves_many_different_files_without_bleeding_between_them()
    {
        var paths = Enumerable.Range(0, 5)
            .Select(i => Path.Combine(_tempDir, $"pack{i}.psarc")).ToArray();

        using (var writer = CreateIndex().BeginScan())
        {
            Assert.NotNull(writer);
            for (var i = 0; i < paths.Length; i++)
                writer.UpsertFile(paths[i], 100 + i, 200 + i, [MakeSong(paths[i], $"Key{i}")]);
        }

        using var scan = CreateIndex().BeginScan();
        Assert.NotNull(scan);

        // Interleave hits, a miss and a stale entry: each must answer for ITSELF,
        // and none may leave the reused statement holding the previous path.
        for (var i = 0; i < paths.Length; i++)
        {
            Assert.True(scan.TryGetCachedSongs(paths[i], 100 + i, 200 + i, out var hit));
            Assert.Equal($"Key{i}", Assert.Single(hit).SongKey);
            Assert.Equal(paths[i], hit[0].PackagePath);

            // Never indexed at all.
            Assert.False(scan.TryGetCachedSongs(
                Path.Combine(_tempDir, $"absent{i}.psarc"), 1, 1, out var absent));
            Assert.Empty(absent);

            // Indexed, but the archive changed since — must not serve the old rows.
            Assert.False(scan.TryGetCachedSongs(paths[i], 999, 200 + i, out var stale));
            Assert.Empty(stale);
        }

        // ...and the session still works after all of that.
        Assert.True(scan.TryGetCachedSongs(paths[0], 100, 200, out var again));
        Assert.Equal("Key0", Assert.Single(again).SongKey);
    }

    // ---- The bulk prefetch (the warm-scan read path) ------------------------

    /// <summary>
    ///     A warm session reads both tables ONCE, up front — it does not run two
    ///     point lookups per PSARC. Asserted by deleting every row through a
    ///     SECOND connection after the session has answered once: a session that
    ///     still queried per file would find nothing and miss, while one serving
    ///     from its own snapshot answers unchanged.
    ///     <para>
    ///         Measured on the owner's 1,825-file / 2,160-song library, both arms
    ///         interleaved in one process so a contended box hits them equally:
    ///         744.5 ms per-file vs 392.6 ms bulk, 1.90x
    ///         (<c>LocalOnly_PsarcScanAbProbe</c>). Preparing the statements
    ///         (0.44) had already removed the parse+plan cost; the round trip
    ///         itself is what is left, and only one read removes that.
    ///     </para>
    /// </summary>
    [Fact]
    public void AWarmSession_ServesFromOneBulkRead_NotAQueryPerFile()
    {
        var paths = Enumerable.Range(0, 4)
            .Select(i => Path.Combine(_tempDir, $"bulk{i}.psarc")).ToArray();

        using (var writer = CreateIndex().BeginScan())
        {
            Assert.NotNull(writer);
            for (var i = 0; i < paths.Length; i++)
                writer.UpsertFile(paths[i], 100 + i, 200 + i, [MakeSong(paths[i], $"Bulk{i}")]);
        }

        using var scan = CreateIndex().BeginScan();
        Assert.NotNull(scan);

        // The first answer is what forces the bulk read.
        Assert.True(scan.TryGetCachedSongs(paths[0], 100, 200, out var first));
        Assert.Equal("Bulk0", Assert.Single(first).SongKey);

        // Now empty the tables underneath it, through a different connection.
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM songs; DELETE FROM files;";
            command.ExecuteNonQuery();
        }

        // Every remaining file still answers, from the snapshot. Reverting the
        // prefetch turns each of these into a miss, which is what makes this
        // test non-vacuous.
        for (var i = 1; i < paths.Length; i++)
        {
            Assert.True(scan.TryGetCachedSongs(paths[i], 100 + i, 200 + i, out var served));
            Assert.Equal($"Bulk{i}", Assert.Single(served).SongKey);
        }
    }

    /// <summary>
    ///     The snapshot is dropped for any path this session WROTE, because what
    ///     <c>UpsertFile</c> commits is not what it was handed — the rating
    ///     carry-over can replace the supplied 0 with a months-old value, and a
    ///     re-parsed archive re-extracts its cover. A written path must therefore
    ///     read back the COMMITTED row, not the pre-write snapshot.
    ///     <para>
    ///         Both halves of that shipped broken in the first cut of the
    ///         prefetch and were caught by
    ///         <see cref="Schema_version_mismatch_drops_and_rebuilds" /> and
    ///         <see cref="Cover_image_path_is_revalidated_on_read" />; this pins
    ///         the rule itself rather than relying on those two to notice again.
    ///     </para>
    /// </summary>
    [Fact]
    public void APathWrittenDuringTheScan_IsReReadInsteadOfServedFromTheSnapshot()
    {
        var psarcPath = Path.Combine(_tempDir, "written.psarc");

        using var scan = CreateIndex().BeginScan();
        Assert.NotNull(scan);

        // Force the bulk read while the index is still empty.
        Assert.False(scan.TryGetCachedSongs(psarcPath, 1, 1, out _));

        scan.UpsertFile(psarcPath, 1, 1, [MakeSong(psarcPath, "Written")]);

        // The snapshot says "absent"; the committed row says otherwise, and the
        // committed row is the answer.
        Assert.True(scan.TryGetCachedSongs(psarcPath, 1, 1, out var served));
        Assert.Equal("Written", Assert.Single(served).SongKey);
    }

    /// <summary>
    ///     The arrangements column is source-generated now, and the emitted shape
    ///     must stay byte-identical to what the reflection serializer wrote —
    ///     every already-indexed row was written by it. A naming policy or a
    ///     casing option added to the context would make existing rows
    ///     deserialize to DEFAULTS, silently and forever: nothing about the
    ///     archives changes when the app is upgraded, so a stale <c>(size,
    ///     mtime)</c> hit keeps serving the wrong answer — the same trap the
    ///     <c>SchemaVersion</c> comment describes.
    ///     <para>
    ///         The instance comes from <c>DomainFieldProbe</c> rather than a
    ///         hand-written literal, so a NEW property on
    ///         <see cref="LibraryArrangementEntry" /> is covered the day it is
    ///         added instead of needing this test edited too.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheArrangementsJson_KeepsTheShapeTheReflectionSerializerWrote()
    {
        List<LibraryArrangementEntry> entries = [DomainFieldProbe.Populated<LibraryArrangementEntry>()];

        var reflection = JsonSerializer.Serialize(entries, JsonSerializerOptions.Default);
        var sourceGen = JsonSerializer.Serialize(
            (IReadOnlyList<LibraryArrangementEntry>)entries,
            PsarcIndexJsonContext.Default.IReadOnlyListLibraryArrangementEntry);

        Assert.Equal(reflection, sourceGen);

        // ...and a row written by the OLD serializer still reads back whole.
        var rehydrated = JsonSerializer.Deserialize(
            reflection, PsarcIndexJsonContext.Default.ListLibraryArrangementEntry);
        Assert.NotNull(rehydrated);
        var differences = DeepCompare.Diff("arrangements", entries, rehydrated,
            new HashSet<string>(StringComparer.Ordinal));
        Assert.True(differences.Count == 0, string.Join("\n", differences));
    }
}
