using System.Diagnostics;
using System.Text.RegularExpressions;
using DotCharter.Services.Library;
using DotCharter.Services.Library.Index;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace DotCharter.Tests.Services.Parsing.Psarc.Index;

/// <summary>
///     <c>BeginScan</c> used to treat EVERY failure to open the index the same
///     way: "unreadable, rebuilding" — which means
///     <c>DeleteDatabaseFiles()</c>, i.e. the whole cache AND the
///     <c>song_ratings</c> table that survives a schema rebuild precisely
///     because it cannot be re-derived (a rating is only earned by LOADING the
///     song; on a 4,000-song library that is months of accumulated data).
///     <para>
///         A locked database is not a corrupt one. It is the single most
///         ordinary transient failure this file has: the app's own
///         <c>TrySetSongRating</c> opens a second connection, a second app
///         instance scans, a backup tool or an antivirus holds the file for a
///         moment. Deleting on that is destroying the user's data because
///         somebody else was mid-write.
///     </para>
/// </summary>
public sealed class LibraryIndexBusyRetryTests : IDisposable
{
    private readonly FakeAppLogger _logger = new();
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-libbusy-").FullName;

    private string DbPath => Path.Combine(_tempDir, "library-index.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        BestEffortDelete.Directory(_tempDir);
    }

    private SqliteLibraryIndex CreateIndex() => new(_logger, DbPath);

    private static LibrarySongEntry MakeSong(string psarcPath, string key) => new()
    {
        PackagePath = psarcPath,
        SongKey = key,
        Title = $"Title {key}",
        Artist = $"Artist {key}",
        Album = string.Empty,
        SongBank = string.Empty,
        TuningSummary = string.Empty,
        CoverImagePath = string.Empty,
        CoverImageEntryPath = string.Empty,
        Arrangements = []
    };

    /// <summary>Opens a connection that holds the database EXCLUSIVELY, the way
    /// a concurrent writer (or a second instance mid-scan) does.</summary>
    private SqliteConnection OpenExclusiveHolder()
    {
        var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        holder.Open();

        using var pragma = holder.CreateCommand();
        pragma.CommandText = "PRAGMA locking_mode=EXCLUSIVE;";
        pragma.ExecuteNonQuery();

        // The lock is only actually TAKEN on the first write under
        // locking_mode=EXCLUSIVE, and it is then held until the connection
        // closes.
        using var write = holder.CreateCommand();
        write.CommandText =
            "CREATE TABLE IF NOT EXISTS busy_probe(x INTEGER); INSERT INTO busy_probe(x) VALUES(1);";
        write.ExecuteNonQuery();
        return holder;
    }

    /// <summary>
    ///     THE POINT: a busy database must never be mistaken for a corrupt one.
    ///     The file, its songs and — above all — its ratings must still be there
    ///     when the holder goes away.
    /// </summary>
    [Fact]
    public void ABusyDatabase_IsNotTreatedAsCorruptAndIsNotDeleted()
    {
        var psarcPath = Path.Combine(_tempDir, "pack.psarc");
        using (var scan = CreateIndex().BeginScan())
        {
            Assert.NotNull(scan);
            scan.UpsertFile(psarcPath, 111, 222, [MakeSong(psarcPath, "Alpha")]);
        }

        CreateIndex().TrySetSongRating(psarcPath, "Alpha", 4.25);

        using (var holder = OpenExclusiveHolder())
        {
            // The scan cannot proceed — but it must degrade to "no cache this
            // time", not to "throw the user's library away".
            var waited = Stopwatch.StartNew();
            var blocked = CreateIndex().BeginScan();
            blocked?.Dispose();
            // Logged, never asserted: ~5 s (the scan's busy_timeout); ~35 s
            // means the library's own 30 s retry is back (see the next test).
            TestContext.Current.TestOutputHelper?.WriteLine($"A locked index held the scan for {waited.Elapsed.TotalSeconds:0.0} s.");

            Assert.True(File.Exists(DbPath), "A locked index must not be deleted.");
            Assert.False(
                _logger.HasEntryContaining("WARN", "unreadable, rebuilding"),
                "A busy database was reported as corrupt; that log line is the delete branch.");

            holder.Close();
        }

        SqliteConnection.ClearAllPools();

        // …and everything is still in it once the holder lets go.
        using var read = CreateIndex().BeginScan();
        Assert.NotNull(read);
        Assert.True(read.TryGetCachedSongs(psarcPath, 111, 222, out var cached));
        var song = Assert.Single(cached);
        Assert.Equal("Alpha", song.SongKey);
        Assert.Equal(4.25, song.Difficulty);
    }

    /// <summary>
    ///     A locked db is waited on for the connection's busy time, ONCE. Two
    ///     timers wait on SQLITE_BUSY: SQLite's <c>busy_timeout</c> and
    ///     Microsoft.Data.Sqlite's retry loop around it, which runs to the
    ///     command's <c>CommandTimeout</c> (30 s by default). With only the first
    ///     set, the test above held its scan ~35 s instead of 5 s. Structure,
    ///     not a stopwatch: both timers on one connection carry the same value.
    /// </summary>
    [Fact]
    public void BothBusyTimers_CarryTheConnectionsBusyTime()
    {
        using var connection = SqliteLibraryIndex.Connect(DbPath, SqliteOpenMode.ReadWriteCreate, busySeconds: 2);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";

        Assert.Equal(2000L, Convert.ToInt64(command.ExecuteScalar()));
        Assert.Equal(2, command.CommandTimeout);
    }

    /// <summary>…and every connection the index opens goes through that one
    /// helper, so a new one cannot quietly bring the 30 s retry back.</summary>
    [Fact]
    public void EveryIndexConnection_IsOpenedThroughConnect()
    {
        var folder = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Library", "Index");
        var files = Directory.GetFiles(folder, "SqliteLibraryIndex*.cs");
        Assert.NotEmpty(files);
        var opened = files.Sum(f => Regex.Matches(SourceText.StripComments(File.ReadAllText(f)), @"new\s+SqliteConnection\s*\(").Count);

        Assert.True(opened == 1, $"expected the one `new SqliteConnection(` inside Connect, found {opened}.");
    }

    /// <summary>
    ///     A database that really IS unreadable content still gets the old
    ///     treatment — delete and rebuild. The busy fix must not turn a corrupt
    ///     index into a permanent one.
    /// </summary>
    [Fact]
    public void AGenuinelyCorruptDatabase_IsStillRebuilt()
    {
        File.WriteAllText(DbPath, "this is not a sqlite database, not even a little bit");

        using var scan = CreateIndex().BeginScan();

        Assert.NotNull(scan);
        Assert.True(_logger.HasEntryContaining("WARN", "unreadable, rebuilding"));
    }
}
