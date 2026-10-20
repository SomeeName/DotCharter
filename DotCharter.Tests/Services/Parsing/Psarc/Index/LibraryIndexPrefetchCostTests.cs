using DotCharter.Services.Library;
using DotCharter.Services.Library.Index;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using System.Text;

namespace DotCharter.Tests.Services.Parsing.Psarc.Index;

/// <summary>
///     The library scan's bulk prefetch reads the WHOLE <c>songs</c> table once
///     per scan session, including the startup scan. It used to end
///     <c>ORDER BY psarc_path, rowid</c>: <c>songs</c> is a rowid table whose
///     only index is the implicit <c>(psarc_path, song_key)</c> primary key, so
///     SQLite served the first term from that index and sorted the LAST term in
///     a temp b-tree — a full sort of every row, each carrying its whole
///     <c>arrangements_json</c> blob, plus one rowid seek per row instead of a
///     sequential page walk. On a 2,000-song library that is the dominant cost
///     of a warm scan.
///     <para>
///     The path term was never observable: the reader buckets rows into a
///     dictionary keyed by <c>psarc_path</c>, so only the order WITHIN a path
///     matters, and <c>ORDER BY rowid</c> preserves that exactly.
///     </para>
///     <para>
///     Asserting the PLAN, not the clock, and against the production SQL
///     constant so the guard cannot drift from the query that runs.
///     </para>
/// </summary>
public sealed class LibraryIndexPrefetchCostTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-libindex-plan-").FullName;
    private readonly FakeAppLogger _logger = new();

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string DbPath => Path.Combine(_tempDir, "library-index.db");

    /// <summary>Creates the real schema by running a real (empty) scan session,
    /// then seeds several archives' worth of rows so the planner has a table
    /// worth planning for.</summary>
    private void SeedRealIndex(int archives, int songsPerArchive)
    {
        using var scan = new SqliteLibraryIndex(_logger, DbPath).BeginScan();
        Assert.NotNull(scan);
        for (var a = 0; a < archives; a++)
        {
            var path = Path.Combine(_tempDir, $"pack{a}.psarc");
            var songs = new List<LibrarySongEntry>();
            for (var s = 0; s < songsPerArchive; s++)
            {
                songs.Add(new LibrarySongEntry
                {
                    PackagePath = path,
                    SongKey = $"a{a}s{s}",
                    Title = $"Title {a}-{s}",
                    Artist = $"Artist {a}",
                    Album = "Album",
                    Year = 1999,
                    SongBank = "song.bnk",
                    TuningSummary = "E Standard",
                    Arrangements = []
                });
            }

            scan.UpsertFile(path, a + 1, a + 2, songs);
        }
    }

    private SqliteConnection OpenSeeded()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection;
    }

    private static string PlanOf(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        var plan = new StringBuilder();
        using var reader = command.ExecuteReader();
        while (reader.Read()) plan.AppendLine(reader.GetString(reader.GetOrdinal("detail")));
        return plan.ToString();
    }

    [Fact]
    public void ThePrefetchOfEverySongRowNeitherSortsTheTableNorSeeksThroughAnIndex()
    {
        SeedRealIndex(archives: 8, songsPerArchive: 5);
        using var connection = OpenSeeded();

        var plan = PlanOf(connection, SqliteLibraryIndex.PrefetchSongsSql);

        // The regression this exists for: `ORDER BY psarc_path, rowid` plans as
        // "SCAN songs USING INDEX sqlite_autoindex_songs_1" + "USE TEMP B-TREE
        // FOR LAST TERM OF ORDER BY".
        Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USING INDEX", plan, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SCAN songs", plan, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The plan guard above is only worth anything if the ordering it drops
    ///     was never load-bearing. The prefetch buckets by <c>psarc_path</c>, so
    ///     the claim is exactly this: rowid order and (path, rowid) order agree
    ///     on the sequence of rows WITHIN every path.
    /// </summary>
    [Fact]
    public void RowidOrderAgreesWithTheOldPathThenRowidOrderWithinEveryArchive()
    {
        SeedRealIndex(archives: 6, songsPerArchive: 4);
        using var connection = OpenSeeded();

        var byRowid = ReadKeysPerPath(connection, SqliteLibraryIndex.PrefetchSongsSql);
        var byPathThenRowid = ReadKeysPerPath(connection,
            "SELECT psarc_path, song_key FROM songs ORDER BY psarc_path, rowid;");

        Assert.Equal(6, byRowid.Count);
        Assert.Equal(byPathThenRowid.Keys.OrderBy(k => k, StringComparer.Ordinal),
            byRowid.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (path, keys) in byPathThenRowid)
            Assert.Equal(keys, byRowid[path]);

        // Not vacuous: every archive really did contribute several rows.
        Assert.All(byRowid.Values, keys => Assert.Equal(4, keys.Count));
    }

    private static Dictionary<string, List<string>> ReadKeysPerPath(SqliteConnection connection, string sql)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (!result.TryGetValue(path, out var list)) result[path] = list = [];
            list.Add(reader.GetString(1));
        }

        return result;
    }
}
