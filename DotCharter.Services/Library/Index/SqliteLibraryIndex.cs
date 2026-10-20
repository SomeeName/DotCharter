using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace DotCharter.Services.Library.Index;

/// <summary>
/// SQLite-backed <see cref="ILibraryIndex"/> at
/// <c>WritablePaths.ConfigRoot/library-index.db</c>.
///
/// Design constraints (the 0.12.0 "Library index"):
///  - The index is a cache, never a source of truth: every failure path
///    degrades to "no cache hit" and the scan re-parses the archive.
///  - One connection per scan session, WAL journal + busy_timeout so two app
///    instances sharing the db stay best-effort safe.
///  - Upserts are transactional per file so a cancelled scan leaves the
///    index consistent (whole files either fully cached or absent).
///  - meta.schema_version mismatch → drop + rebuild silently; a corrupt db
///    file → delete + rebuild with a Warning in the Import category. A db that
///    is merely LOCKED or otherwise held is neither: it is left untouched and
///    the scan runs without a cache (see <see cref="IsTransientlyUnavailable"/>).
/// </summary>
public sealed class SqliteLibraryIndex : ILibraryIndex
{
    /// <summary>Bump whenever the tables or the serialized JSON shape change.
    /// v2: LibraryArrangementEntry gained RouteMask/Represent/BonusArr — v1
    /// rows would deserialize with RouteMask 0 and fall back to the (wrong)
    /// name-based arrangement type until the file's mtime changed.
    /// v3: LibrarySongEntry gained Charter/Version (toolkit.version stamp) for
    /// the per-CDLC shared-leaderboard identity; v2 rows lack the columns, so
    /// a rebuild re-reads each archive's toolkit stamp once.
    /// v4: LibraryArrangementEntry gained ToneBase (manifest Tone_Base) for the
    /// in-game-tone base tone; v3 rows deserialize it empty, so a rebuild
    /// re-reads each archive's base tone once.
    /// v5: songs gained a `rating` column (the deterministic 1–10
    /// ChartDifficultyEstimator score for the unplayed library). A scan cannot
    /// compute it (no note data in a .hsan), so it is written by
    /// <see cref="TrySetSongRating"/> the first time a song is loaded and read
    /// back cheaply here; v4 rows migrate to rating 0 (= unrated).
    /// v6: no column change — `tuning_summary`/`capo` are now derived from the
    /// song's GUITAR arrangement rather than from whichever one happened to be
    /// first (see the package source). v5 rows hold
    /// the old, sometimes bass-derived value, and nothing about the archive
    /// changes when the app is upgraded — so a stale mtime would keep serving
    /// it forever. Bumping is the only thing that re-derives it. It also
    /// introduced `song_ratings`, the carry-over table that makes this and every
    /// FUTURE rebuild free: a rating cannot be re-derived by a scan, so wiping
    /// it with the rest of the cache would have cost real, months-old data.
    /// v7: no column change either — <c>LibraryArrangementEntry</c> gained
    /// <c>NoteCount</c> (the manifest's <c>NotesHard</c>, which the .hsan the
    /// scan already reads carries), and arrangements are stored as
    /// <c>arrangements_json</c>, so v6 rows deserialize it as 0 and the song
    /// library would print "—" for every note count forever. Nothing about the
    /// archives changes when the app is upgraded, so a stale mtime would keep
    /// serving those zeroes; bumping is the only thing that re-reads them. The
    /// <c>song_ratings</c> carry-over table (v6) is what makes this rebuild
    /// free — the one figure a scan cannot re-derive survives it.
    /// v8: no column change — <c>LibraryArrangementEntry</c> gained
    /// <c>Tuning</c> and <c>Capo</c> (the manifest's per-arrangement values the
    /// scan already parses). The song row still carries ONE
    /// <c>tuning_summary</c>/<c>capo</c>, derived from a guitar arrangement (v6);
    /// v8 is what lets a Lead in E standard and a Bass in drop D BOTH be stated.
    /// v7 rows deserialize the new members as "" / 0 and, because a stale
    /// mtime hit serves that forever, bumping is the only thing that re-reads
    /// them. The <c>song_ratings</c> carry-over makes the rebuild free.</summary>
    internal const int SchemaVersion = 8;

    internal const string DefaultFileName = "library-index.db";

    /// <summary>
    ///     The bulk read behind <c>Session.EnsurePrefetch</c>, as a constant so
    ///     the query-plan guard EXPLAINs the SQL the product actually runs.
    ///     <para>
    ///     It does not end <c>ORDER BY psarc_path, rowid</c>: <c>songs</c> is a
    ///     rowid table whose only index is the implicit
    ///     <c>(psarc_path, song_key)</c> primary key, so SQLite would serve the first
    ///     term from that index and sort the LAST term in a
    ///     <c>USE TEMP B-TREE FOR LAST TERM OF ORDER BY</c> — a full sort of
    ///     every song row, each carrying its whole <c>arrangements_json</c>,
    ///     plus one rowid seek per row instead of a sequential page walk.
    ///     </para>
    ///     <para>
    ///     The path term bought nothing: the reader buckets rows into a
    ///     dictionary keyed by <c>psarc_path</c>, so only the order WITHIN a
    ///     path is observable, and rowid order preserves that exactly. Plain
    ///     <c>ORDER BY rowid</c> plans as a bare <c>SCAN songs</c>.
    ///     </para>
    /// </summary>
    internal const string PrefetchSongsSql =
        """
        SELECT psarc_path, song_key, song_bank, title, artist, album, year,
               average_tempo, song_length, tuning_summary, capo,
               cover_image_path, cover_image_entry_path, arrangements_json,
               charter, version, rating
        FROM songs ORDER BY rowid;
        """;

    private readonly IAppLogger _logger;
    private readonly string _dbPath;

    public SqliteLibraryIndex(IAppLogger logger, string? dbPath = null)
    {
        _logger = logger;
        _dbPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(WritablePaths.ConfigRoot, DefaultFileName)
            : dbPath;
    }

    public ILibraryIndexScan? BeginScan()
    {
        // First attempt on the existing db; if the CONTENT is unusable the db
        // is useless as a cache, so delete it and rebuild once from scratch.
        try
        {
            return OpenSession();
        }
        catch (Exception ex) when (IsTransientlyUnavailable(ex))
        {
            // A LOCKED database is not a corrupt one, and this is the most
            // ordinary failure this file has: our own TrySetSongRating opens a
            // second connection, a second app instance scans, a backup tool or
            // an antivirus holds the file for a moment. The old code could not
            // tell the two apart and deleted on both — taking song_ratings with
            // it, the ONE table that survives a schema rebuild precisely because
            // a rating cannot be re-derived by a scan (it is earned by LOADING
            // the song; on a 4,000-song library that is months of data). The db
            // has already waited out `busy_timeout` inside SQLite's own busy
            // handler by the time we get here, so the answer is simply to scan
            // without the cache and leave the file alone — the next scan gets it.
            _logger.Warning(AppLogCategory.Import,
                $"PSARC library index is unavailable right now (locked or held by another process); " +
                $"scanning without cache and LEAVING IT IN PLACE. Db='{_dbPath}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Import,
                $"PSARC library index unreadable, rebuilding. Db='{_dbPath}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
        }

        try
        {
            DeleteDatabaseFiles();
            return OpenSession();
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Import,
                $"PSARC library index rebuild failed; scanning without cache. Db='{_dbPath}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
            return null;
        }
    }

    public void TrySetSongRating(string psarcPath, string songKey, double rating)
    {
        // Best-effort cache write: the index is never a source of truth, so any
        // failure (db locked, missing, corrupt) just means the rating shows on a
        // later scan instead. Cheap targeted UPDATE — no quick_check, and it does
        // nothing when the row is not indexed yet (the next scan inserts it, and a
        // subsequent load re-rates).
        if (string.IsNullOrEmpty(psarcPath) || string.IsNullOrEmpty(songKey) || rating <= 0) return;
        try
        {
            if (!File.Exists(_dbPath)) return; // nothing scanned yet; nothing to update

            using var connection = Connect(_dbPath, SqliteOpenMode.ReadWrite, busySeconds: 3);
            Execute(connection,
                "UPDATE songs SET rating = $r WHERE psarc_path = $p AND song_key = $k;",
                ("$r", rating), ("$p", psarcPath), ("$k", songKey));
            // …and into the table that survives a schema rebuild. Written even
            // when the UPDATE above matched no row (the song is not indexed
            // yet): the next scan inserts it and picks the rating up from here,
            // which is strictly better than waiting for another load.
            Execute(connection,
                """
                CREATE TABLE IF NOT EXISTS song_ratings(
                    psarc_path TEXT NOT NULL,
                    song_key   TEXT NOT NULL,
                    rating     REAL NOT NULL,
                    PRIMARY KEY(psarc_path, song_key));
                INSERT OR REPLACE INTO song_ratings(psarc_path, song_key, rating)
                VALUES($p, $k, $r);
                """,
                ("$r", rating), ("$p", psarcPath), ("$k", songKey));
        }
        catch (Exception ex)
        {
            _logger.Trace(AppLogCategory.Import,
                $"PSARC library index rating write skipped. Path='{psarcPath}', key='{songKey}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
        }
    }

    public LibraryIndexFacts ReadFacts()
    {
        // Read-only, best-effort, and never a rebuild trigger: the caller is a
        // readout, not a scan, so an unopenable or empty index means "nothing
        // scanned yet" rather than "delete the user's cache". DISTINCT song_key
        // because one song appears once per pack that ships it.
        try
        {
            if (!File.Exists(_dbPath)) return LibraryIndexFacts.Empty;

            using var connection = Connect(_dbPath, SqliteOpenMode.ReadOnly, busySeconds: 1);

            var songs = Scalar(connection, "SELECT COUNT(DISTINCT song_key) FROM songs;");
            var archives = Scalar(connection, "SELECT COUNT(*) FROM files;");
            return new LibraryIndexFacts(AsInt(songs), AsInt(archives));
        }
        catch (Exception ex)
        {
            _logger.Trace(AppLogCategory.Import,
                $"PSARC library index facts unavailable. Db='{_dbPath}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
            return LibraryIndexFacts.Empty;
        }

        static int AsInt(object? value) =>
            value is null or DBNull ? 0 : (int)Math.Min(int.MaxValue, Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    public string FindCoverImage(string songKey) =>
        FindCoverImage(songKey, LibraryCoverCache.GetCachedAlbumArtPngPath);

    public string FindCoverImageByTitle(string artist, string title) =>
        FindCoverImageByTitle(artist, title, LibraryCoverCache.GetCachedAlbumArtPngPath);

    internal string FindCoverImageByTitle(string artist, string title, Func<string, string, string> coverCachePath)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        try
        {
            if (!File.Exists(_dbPath)) return "";

            using var connection = Connect(_dbPath, SqliteOpenMode.ReadOnly, busySeconds: 1);
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT psarc_path, song_key, cover_image_path FROM songs " +
                "WHERE title = $t COLLATE NOCASE AND artist = $a COLLATE NOCASE;";
            command.Parameters.AddWithValue("$t", title.Trim());
            command.Parameters.AddWithValue("$a", artist.Trim());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(2);
                if (path.Length > 0 && File.Exists(path)) return path;
                var extracted = coverCachePath(reader.GetString(0), reader.GetString(1));
                if (extracted.Length > 0 && File.Exists(extracted)) return extracted;
            }

            return "";
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Import,
                $"PSARC library index cover lookup by title failed. Db='{_dbPath}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
            return "";
        }
    }

    /// <param name="songKey">The archive's song key.</param>
    /// <param name="coverCachePath">Where the browser extracts a song's cover
    /// (the package source); a seam so a test need not write to the real cache.</param>
    internal string FindCoverImage(string songKey, Func<string, string, string> coverCachePath)
    {
        if (string.IsNullOrWhiteSpace(songKey)) return "";
        try
        {
            if (!File.Exists(_dbPath)) return "";

            using var connection = Connect(_dbPath, SqliteOpenMode.ReadOnly, busySeconds: 1);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT psarc_path, cover_image_path FROM songs WHERE song_key = $k;";
            command.Parameters.AddWithValue("$k", songKey);
            using var reader = command.ExecuteReader();
            // One song can ship in several packs; the first cover still on disk wins. A row
            // indexed before its cover was first shown carries no path, but the browser has
            // usually extracted it since, under a name derived from the archive and the key.
            while (reader.Read())
            {
                var path = reader.GetString(1);
                if (path.Length > 0 && File.Exists(path)) return path;
                var extracted = coverCachePath(reader.GetString(0), songKey);
                if (extracted.Length > 0 && File.Exists(extracted)) return extracted;
            }

            return "";
        }
        catch (Exception ex)
        {
            // A Warning, not a Trace: a failed lookup shows as a song with no cover
            // on Home (the guitar strings instead of the art), and at Trace level
            // that left nothing in a normal log to say why (owner report).
            _logger.Warning(AppLogCategory.Import,
                $"PSARC library index cover lookup failed for '{songKey}'. Db='{_dbPath}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
            return "";
        }
    }

    /// <summary>
    ///     "Cannot use this file right now" as opposed to "this file is not a
    ///     database". Only the second justifies deleting the user's index.
    ///     <para>
    ///         SQLITE_BUSY/LOCKED are the concurrent-writer cases;
    ///         SQLITE_CANTOPEN/READONLY/PROTOCOL and a bare IO/access exception
    ///         are the file-held-elsewhere cases (an antivirus scan, a cloud
    ///         sync, a backup tool, a read-only attribute). Every one of them
    ///         describes a db that is presumed INTACT — the same three-outcome
    ///         rule <c>JsonConfigurationService</c> follows for appconfig.json:
    ///         ABSENT starts fresh, CORRUPT is replaced, UNREADABLE is left
    ///         alone.
    ///     </para>
    /// </summary>
    internal static bool IsTransientlyUnavailable(Exception? exception)
    {
        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            if (ex is IOException or UnauthorizedAccessException) return true;
            if (ex is SqliteException sqlite && sqlite.SqliteErrorCode is
                    5 /* BUSY */ or 6 /* LOCKED */ or 8 /* READONLY */ or
                    14 /* CANTOPEN */ or 15 /* PROTOCOL */)
            {
                return true;
            }
        }

        return false;
    }

    private Session OpenSession()
    {
        var directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // busy_timeout is set inside Connect, BEFORE any other statement, and
        // the order is the whole point: `PRAGMA journal_mode=WAL` needs an
        // exclusive lock on a db not already in WAL, so it fails
        // INSTANTLY with SQLITE_BUSY against any concurrent holder, and
        // BeginScan read that as corruption.
        var connection = Connect(_dbPath, SqliteOpenMode.ReadWriteCreate, busySeconds: 5);

        try
        {
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");

            // Proactive corruption probe: quick_check reads every page's
            // structure without the full integrity_check cost. Anything but
            // "ok" and we treat the file as corrupt (throw → delete + rebuild).
            var check = Scalar(connection, "PRAGMA quick_check(1);") as string;
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"quick_check reported '{check}'.");

            EnsureSchema(connection);
            return new Session(connection, _logger);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        // The one table that OUTLIVES a schema rebuild, and the only one that
        // has to: everything else in this file is re-derivable from the
        // archives, but a song's difficulty RATING is not. It cannot be
        // computed by a scan (a .hsan carries no note data) — it is written
        // lazily by TrySetSongRating the first time a song is actually LOADED,
        // so a wiped rating is only re-earned by opening that song again. On a
        // 4,000-song library that is months of accumulated data, and the v5→v6
        // bump (a derivation fix, not a column change) would have dropped every
        // one of them silently. Kept OUT of the DROP block below and restored
        // into each row as the scan re-inserts it.
        Execute(connection,
            """
            CREATE TABLE IF NOT EXISTS song_ratings(
                psarc_path TEXT NOT NULL,
                song_key   TEXT NOT NULL,
                rating     REAL NOT NULL,
                PRIMARY KEY(psarc_path, song_key));
            """);

        var version = ReadSchemaVersion(connection);
        if (version == SchemaVersion) return;

        if (version != null)
        {
            _logger.Debug(AppLogCategory.Import,
                $"PSARC library index schema {version} != {SchemaVersion}; dropping and rebuilding.");

            // Harvest what the outgoing `songs` table knows before it goes. Only
            // v5+ has the column, and a rating of 0 means "unrated" rather than
            // "rated zero", so it carries nothing. Best-effort: an unreadable
            // old table must not block the rebuild that fixes it.
            try
            {
                Execute(connection,
                    """
                    INSERT OR REPLACE INTO song_ratings(psarc_path, song_key, rating)
                    SELECT psarc_path, song_key, rating FROM songs WHERE rating > 0;
                    """);
            }
            catch (Exception ex)
            {
                _logger.Debug(AppLogCategory.Import,
                    $"PSARC library index rating carry-over skipped (schema {version}). " +
                    $"type={ex.GetType().Name}, message='{ex.Message}'.");
            }
        }

        // Version mismatch (or fresh file): drop + rebuild silently. The
        // cache repopulates on the very scan that triggered the rebuild.
        // NOTE: song_ratings is deliberately absent from these DROPs.
        Execute(connection,
            """
            BEGIN;
            DROP TABLE IF EXISTS songs;
            DROP TABLE IF EXISTS files;
            DROP TABLE IF EXISTS meta;
            CREATE TABLE meta(
                key   TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL);
            CREATE TABLE files(
                psarc_path      TEXT PRIMARY KEY NOT NULL,
                file_size       INTEGER NOT NULL,
                mtime_utc_ticks INTEGER NOT NULL);
            CREATE TABLE songs(
                psarc_path             TEXT NOT NULL,
                song_key               TEXT NOT NULL,
                charter                TEXT NOT NULL,
                version                TEXT NOT NULL,
                song_bank              TEXT NOT NULL,
                title                  TEXT NOT NULL,
                artist                 TEXT NOT NULL,
                album                  TEXT NOT NULL,
                year                   INTEGER NOT NULL,
                average_tempo          REAL NOT NULL,
                song_length            REAL NOT NULL,
                tuning_summary         TEXT NOT NULL,
                capo                   INTEGER NOT NULL,
                cover_image_path       TEXT NOT NULL,
                cover_image_entry_path TEXT NOT NULL,
                arrangements_json      TEXT NOT NULL,
                rating                 REAL NOT NULL DEFAULT 0,
                PRIMARY KEY(psarc_path, song_key));
            INSERT INTO meta(key, value) VALUES('schema_version', $v);
            COMMIT;
            """,
            ("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture)));
    }

    private static int? ReadSchemaVersion(SqliteConnection connection)
    {
        var hasMeta = Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='meta';");
        if (hasMeta is not long and not int || Convert.ToInt64(hasMeta, CultureInfo.InvariantCulture) == 0)
            return null;

        var value = Scalar(connection, "SELECT value FROM meta WHERE key='schema_version';") as string;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private void DeleteDatabaseFiles()
    {
        // Belt and braces: pooled connections from any earlier failure would
        // keep the file locked on Windows.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    ///     An open, unpooled connection that waits at most
    ///     <paramref name="busySeconds" /> on a locked db. TWO timers wait on
    ///     SQLITE_BUSY: SQLite's own <c>busy_timeout</c>, and Microsoft.Data.Sqlite's
    ///     retry loop around it, which runs until the command's
    ///     <c>CommandTimeout</c> (30 s by default) has passed. Setting only the
    ///     first made a locked index block a scan for ~35 s, not 5 s (measured
    ///     by <c>LibraryIndexBusyRetryTests</c>), so both are set here.
    ///     Unpooled so Dispose releases the file and a corrupt db can be deleted.
    /// </summary>
    internal static SqliteConnection Connect(string path, SqliteOpenMode mode, int busySeconds)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = busySeconds
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, $"PRAGMA busy_timeout={busySeconds * 1000};");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class Session(SqliteConnection connection, IAppLogger logger) : ILibraryIndexScan
    {
        // Log a Warning only for the FIRST failure of each kind per scan;
        // a broken db would otherwise emit one warning per psarc file.
        private bool _readFailureWarned;
        private bool _writeFailureWarned;

        // The two lookup statements, PREPARED ONCE for the whole scan.
        //
        // These run per PSARC — 1,825 times on the owner's library — and a
        // CreateCommand()/ExecuteReader()/Dispose() cycle re-runs
        // sqlite3_prepare_v2 every time: Microsoft.Data.Sqlite only keeps a
        // compiled statement alive when the command was explicitly Prepare()d
        // and outlives the execution. So the warm path was paying ~3,650
        // parse+plan cycles to read two rows each. Hoisting them is worth
        // ~90 ms of a ~330 ms all-cache-hit scan and changes no semantics.
        //
        // Safe because the session is single-threaded by contract (see
        // ILibraryIndexScan) and the commands are disposed with it.
        private SqliteCommand? _fileLookup;
        private SqliteParameter? _fileLookupPath;
        private SqliteCommand? _songLookup;
        private SqliteParameter? _songLookupPath;

        // ---- The BULK PREFETCH, which replaces those two per-file lookups ----
        //
        // Measured on the owner's real 1,825-file / 2,160-song library (Release,
        // idle box, 3 reps): 1,825 x two prepared point lookups cost 67.6 ms,
        // while reading BOTH tables whole costs 13.2 ms — the per-file round
        // trips are 54.5 ms of a warm scan that measures 518-747 ms end to end.
        // Preparing the statements (the 0.44 fix) removed the parse+plan cost;
        // what is left is the round trip itself, and only one read removes that.
        //
        // The dictionaries are keyed ORDINAL on purpose: SQLite's default TEXT
        // collation is BINARY, so `WHERE psarc_path = $p` was case-SENSITIVE and
        // an OrdinalIgnoreCase dictionary would serve hits the old path missed.
        // This is a perf change; it may not move the hit/miss boundary.
        //
        // Rows are held RAW and materialized on a hit, so the two genuinely
        // expensive per-song costs — the arrangements JSON parse and the cover
        // revalidation — stay on the hit path exactly as before. A stale file
        // still pays neither.
        //
        // The snapshot is only valid for rows this session has NOT rewritten.
        // `UpsertFile` runs inside the same scan, and what it commits is not
        // what it was handed — the rating carry-over UPDATE can replace the
        // supplied 0 with a months-old value — so a written path is dropped
        // from the snapshot and re-read through the point lookup below. A real
        // scan never re-reads a path it just wrote, so that fallback is close
        // to free; `SqliteLibraryIndexTests` is what exercises it, and
        // both `Schema_version_mismatch_drops_and_rebuilds` and
        // `Cover_image_path_is_revalidated_on_read` caught the first cut of
        // this serving the pre-write snapshot.
        private bool _prefetchAttempted;
        private bool _prefetchUsable;
        private Dictionary<string, (long Size, long Mtime)>? _prefetchedFiles;
        private Dictionary<string, List<CachedSongRow>>? _prefetchedSongs;
        private readonly HashSet<string> _writtenThisSession = new(StringComparer.Ordinal);

        // One directory listing per distinct album-art cache folder, replacing
        // one File.Exists per song. Measured on the same library: 1,522 probes
        // across ONE directory cost 156.6 ms; the listing plus a set lookup each
        // costs 52.5 ms. Keyed by (directory -> file names) rather than by full
        // path so a separator or casing difference between the stored path and
        // the enumeration cannot turn a hit into a miss.
        private Dictionary<string, HashSet<string>>? _coverListings;

        /// <summary>One raw <c>songs</c> row, exactly as the reader produced it.
        /// Materialized into a <see cref="LibrarySongEntry" /> only when its file
        /// turns out to be a cache HIT.</summary>
        private readonly record struct CachedSongRow(
            string SongKey, string SongBank, string Title, string Artist, string Album,
            int Year, double AverageTempo, double SongLength, string TuningSummary, int Capo,
            string CoverImagePath, string CoverImageEntryPath, string ArrangementsJson,
            string Charter, string Version, double Rating);

        /// <summary>Reads both tables whole, once per scan session, and lists the
        /// album-art directories the cached covers live in. Any failure leaves
        /// <see cref="_prefetchUsable" /> false and the per-file prepared
        /// statements below take over — the index is a CACHE, so a degraded read
        /// path must still be a correct one.</summary>
        private void EnsurePrefetch()
        {
            if (_prefetchAttempted) return;
            _prefetchAttempted = true;

            // Local-only A/B switch, same shape as DOTCHARTER_NO_SURFACE_PREWARM:
            // forces the per-file read path so a probe can time both arms in ONE
            // process. That matters here — absolute numbers on this box move 3x
            // with another agent building, so an A/B taken minutes apart says
            // nothing. Never set in the product.
            if (Environment.GetEnvironmentVariable("DOTCHARTER_NO_PSARC_PREFETCH") == "1")
            {
                _prefetchUsable = false;
                return;
            }

            try
            {
                var files = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT psarc_path, file_size, mtime_utc_ticks FROM files;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                        files[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
                }

                var songs = new Dictionary<string, List<CachedSongRow>>(StringComparer.Ordinal);
                var coverDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = PrefetchSongsSql;
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var path = reader.GetString(0);
                        if (!songs.TryGetValue(path, out var list))
                            songs[path] = list = [];

                        var cover = reader.GetString(11);
                        if (cover.Length > 0)
                        {
                            var dir = Path.GetDirectoryName(cover);
                            if (!string.IsNullOrEmpty(dir)) coverDirs.Add(dir);
                        }

                        list.Add(new CachedSongRow(
                            reader.GetString(1), reader.GetString(2), reader.GetString(3),
                            reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                            reader.GetDouble(7), reader.GetDouble(8), reader.GetString(9),
                            reader.GetInt32(10), cover, reader.GetString(12),
                            reader.GetString(13), reader.GetString(14), reader.GetString(15),
                            reader.GetDouble(16)));
                    }
                }

                var listings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var dir in coverDirs)
                {
                    // A directory that has been cleaned away lists as absent,
                    // which reads as "no cover here" — the same answer
                    // File.Exists gave, so the browser still falls back to
                    // in-archive extraction.
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        if (Directory.Exists(dir))
                        {
                            foreach (var file in Directory.EnumerateFiles(dir))
                                names.Add(Path.GetFileName(file));
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Trace(AppLogCategory.Import,
                            $"PSARC library index cover listing failed for '{dir}'. " +
                            $"Type={ex.GetType().Name}, message='{ex.Message}'.");
                    }

                    listings[dir] = names;
                }

                _prefetchedFiles = files;
                _prefetchedSongs = songs;
                _coverListings = listings;
                _prefetchUsable = true;
            }
            catch (Exception ex)
            {
                _prefetchedFiles = null;
                _prefetchedSongs = null;
                _coverListings = null;
                _prefetchUsable = false;
                LogFailure(ref _readFailureWarned, "prefetch", "<library>", ex);
            }
        }

        /// <summary>Revalidates one cached cover path — the extracted png may
        /// have been cleaned from the AlbumArtCache since indexing, and clearing
        /// the path is what makes the browser fall back to in-archive
        /// extraction.
        /// <para>
        ///     Answered from the prefetched directory listing when
        ///     <paramref name="trustListing" />, and by a real
        ///     <c>File.Exists</c> otherwise. Nothing in the app prunes
        ///     <c>%LOCALAPPDATA%\DotCharter\AlbumArtCache</c> — it shrinks only
        ///     when the user or a disk cleaner empties it, i.e. between runs —
        ///     so a listing taken at session start answers the same question
        ///     per-row probes did, for 37 ms instead of 149 (measured, same run,
        ///     1,522 covers in one directory). What it CANNOT see is a change
        ///     this very session caused, which is exactly why a path in
        ///     <see cref="_writtenThisSession" /> is probed for real.
        /// </para></summary>
        private bool CoverStillExists(string coverPath, bool trustListing)
        {
            if (trustListing && _coverListings != null)
            {
                var dir = Path.GetDirectoryName(coverPath);
                if (!string.IsNullOrEmpty(dir) && _coverListings.TryGetValue(dir, out var names))
                    return names.Contains(Path.GetFileName(coverPath));
            }

            return File.Exists(coverPath);
        }

        private LibrarySongEntry Materialize(string psarcPath, in CachedSongRow row, bool trustListing)
        {
            // The extracted cover png may have been cleaned from the
            // AlbumArtCache since indexing — revalidate so the browser falls
            // back to in-archive extraction.
            var coverPath = row.CoverImagePath;
            if (coverPath.Length > 0 && !CoverStillExists(coverPath, trustListing))
                coverPath = "";

            return new LibrarySongEntry
            {
                PackagePath = psarcPath,
                SongKey = row.SongKey,
                SongBank = row.SongBank,
                Title = row.Title,
                Artist = row.Artist,
                Album = row.Album,
                Year = row.Year,
                AverageTempo = row.AverageTempo,
                SongLength = row.SongLength,
                TuningSummary = row.TuningSummary,
                CapoSummary = row.Capo,
                CoverImagePath = coverPath,
                CoverImageEntryPath = row.CoverImageEntryPath,
                Arrangements = JsonSerializer.Deserialize(
                    row.ArrangementsJson, PsarcIndexJsonContext.Default.ListLibraryArrangementEntry) ?? [],
                Charter = row.Charter,
                Version = row.Version,
                Difficulty = row.Rating
            };
        }

        public bool TryGetCachedSongs(
            string psarcPath, long fileSize, long mtimeUtcTicks,
            out IReadOnlyList<LibrarySongEntry> songs)
        {
            songs = [];

            EnsurePrefetch();
            if (_prefetchUsable && !_writtenThisSession.Contains(psarcPath))
            {
                try
                {
                    if (!_prefetchedFiles!.TryGetValue(psarcPath, out var stat)) return false;
                    if (stat.Size != fileSize || stat.Mtime != mtimeUtcTicks)
                        return false; // Stale: the archive changed since it was indexed.

                    if (!_prefetchedSongs!.TryGetValue(psarcPath, out var rows))
                        return true; // Indexed, but the archive holds no songs.

                    var built = new List<LibrarySongEntry>(rows.Count);
                    foreach (var row in rows) built.Add(Materialize(psarcPath, row, trustListing: true));
                    songs = built;
                    return true;
                }
                catch (Exception ex)
                {
                    LogFailure(ref _readFailureWarned, "read", psarcPath, ex);
                    songs = [];
                    return false;
                }
            }

            return TryGetCachedSongsByLookup(psarcPath, fileSize, mtimeUtcTicks, out songs);
        }

        /// <summary>The pre-prefetch read path, kept as the fallback for a
        /// session whose bulk read failed.</summary>
        private bool TryGetCachedSongsByLookup(
            string psarcPath, long fileSize, long mtimeUtcTicks,
            out IReadOnlyList<LibrarySongEntry> songs)
        {
            songs = [];
            try
            {
                if (_fileLookup == null)
                {
                    _fileLookup = connection.CreateCommand();
                    _fileLookup.CommandText =
                        "SELECT file_size, mtime_utc_ticks FROM files WHERE psarc_path = $p;";
                    _fileLookupPath = _fileLookup.Parameters.Add("$p", SqliteType.Text);
                    _fileLookup.Prepare();

                    _songLookup = connection.CreateCommand();
                    _songLookup.CommandText =
                        """
                        SELECT song_key, song_bank, title, artist, album, year,
                               average_tempo, song_length, tuning_summary, capo,
                               cover_image_path, cover_image_entry_path, arrangements_json,
                               charter, version, rating
                        FROM songs WHERE psarc_path = $p ORDER BY rowid;
                        """;
                    _songLookupPath = _songLookup.Parameters.Add("$p", SqliteType.Text);
                    _songLookup.Prepare();
                }

                _fileLookupPath!.Value = psarcPath;
                using (var reader = _fileLookup.ExecuteReader())
                {
                    if (!reader.Read()) return false;
                    if (reader.GetInt64(0) != fileSize || reader.GetInt64(1) != mtimeUtcTicks)
                        return false; // Stale: the archive changed since it was indexed.
                }

                var result = new List<LibrarySongEntry>();
                _songLookupPath!.Value = psarcPath;
                {
                    using var reader = _songLookup!.ExecuteReader();
                    while (reader.Read())
                    {
                        result.Add(Materialize(psarcPath, new CachedSongRow(
                            reader.GetString(0), reader.GetString(1), reader.GetString(2),
                            reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                            reader.GetDouble(6), reader.GetDouble(7), reader.GetString(8),
                            reader.GetInt32(9), reader.GetString(10), reader.GetString(11),
                            reader.GetString(12), reader.GetString(13), reader.GetString(14),
                            reader.GetDouble(15)), trustListing: false));
                    }
                }

                songs = result;
                return true;
            }
            catch (Exception ex)
            {
                LogFailure(ref _readFailureWarned, "read", psarcPath, ex);
                songs = [];
                return false;
            }
        }

        public void UpsertFile(
            string psarcPath, long fileSize, long mtimeUtcTicks,
            IReadOnlyList<LibrarySongEntry> songs)
        {
            // Before the write, not after: a throw halfway through still leaves
            // the bulk snapshot describing rows that may be gone, so
            // the safe answer is to stop trusting it for this path either way.
            _writtenThisSession.Add(psarcPath);

            try
            {
                using var transaction = connection.BeginTransaction();

                // What the index knew about this archive BEFORE the rewrite is
                // the only thing that can tell a SCHEMA REBUILD (carry the
                // rating over — that is what song_ratings exists for) apart from
                // an ARCHIVE UPDATE (do not: the rating describes the chart that
                // is gone). Read it before the DELETE below removes it.
                //   song_ratings is keyed by (psarc_path, song_key) alone, with
                // no (size, mtime) dimension, so it was exempt from the
                // freshness check every other row obeys: a CDLC updated in place
                // was correctly re-parsed and then had its PRE-UPDATE difficulty
                // copied straight back over the scan's 0, and stayed wrong until
                // the user happened to open that song again.
                var previousStat = ReadFileStat(transaction, psarcPath);
                var archiveUnchanged = previousStat is null ||
                                       previousStat == (fileSize, mtimeUtcTicks);

                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText =
                        "DELETE FROM songs WHERE psarc_path = $p; DELETE FROM files WHERE psarc_path = $p;";
                    delete.Parameters.AddWithValue("$p", psarcPath);
                    delete.ExecuteNonQuery();
                }

                using (var insertFile = connection.CreateCommand())
                {
                    insertFile.Transaction = transaction;
                    insertFile.CommandText =
                        "INSERT INTO files(psarc_path, file_size, mtime_utc_ticks) VALUES($p, $s, $m);";
                    insertFile.Parameters.AddWithValue("$p", psarcPath);
                    insertFile.Parameters.AddWithValue("$s", fileSize);
                    insertFile.Parameters.AddWithValue("$m", mtimeUtcTicks);
                    insertFile.ExecuteNonQuery();
                }

                using (var insertSong = connection.CreateCommand())
                {
                    insertSong.Transaction = transaction;
                    insertSong.CommandText =
                        """
                        INSERT INTO songs(psarc_path, song_key, charter, version, song_bank, title, artist, album,
                                          year, average_tempo, song_length, tuning_summary, capo,
                                          cover_image_path, cover_image_entry_path, arrangements_json, rating)
                        VALUES($p, $key, $charter, $version, $bank, $title, $artist, $album,
                               $year, $tempo, $length, $tuning, $capo,
                               $cover, $coverEntry, $arrangements, $rating);
                        """;
                    var p = insertSong.Parameters;
                    p.AddWithValue("$p", psarcPath);
                    var key = p.AddWithValue("$key", "");
                    var charter = p.AddWithValue("$charter", "");
                    var version = p.AddWithValue("$version", "");
                    var bank = p.AddWithValue("$bank", "");
                    var title = p.AddWithValue("$title", "");
                    var artist = p.AddWithValue("$artist", "");
                    var album = p.AddWithValue("$album", "");
                    var year = p.AddWithValue("$year", 0);
                    var tempo = p.AddWithValue("$tempo", 0.0);
                    var length = p.AddWithValue("$length", 0.0);
                    var tuning = p.AddWithValue("$tuning", "");
                    var capo = p.AddWithValue("$capo", 0);
                    var cover = p.AddWithValue("$cover", "");
                    var coverEntry = p.AddWithValue("$coverEntry", "");
                    var arrangements = p.AddWithValue("$arrangements", "[]");
                    var rating = p.AddWithValue("$rating", 0.0);

                    foreach (var song in songs)
                    {
                        key.Value = song.SongKey;
                        charter.Value = song.Charter;
                        version.Value = song.Version;
                        bank.Value = song.SongBank;
                        title.Value = song.Title;
                        artist.Value = song.Artist;
                        album.Value = song.Album;
                        year.Value = song.Year;
                        tempo.Value = song.AverageTempo;
                        length.Value = song.SongLength;
                        tuning.Value = song.TuningSummary;
                        capo.Value = song.CapoSummary;
                        cover.Value = song.CoverImagePath;
                        coverEntry.Value = song.CoverImageEntryPath;
                        arrangements.Value = JsonSerializer.Serialize(song.Arrangements,
                            PsarcIndexJsonContext.Default.IReadOnlyListLibraryArrangementEntry);
                        rating.Value = song.Difficulty;
                        insertSong.ExecuteNonQuery();
                    }
                }

                // Restore ratings the scan cannot know — but ONLY while the
                // carried-over value still describes this archive (see
                // archiveUnchanged above). A scan supplies 0 (a .hsan has no
                // note data), so a carried-over value always wins; an explicit
                // rating the scan DID bring is left alone. Runs once per file
                // inside the same transaction rather than as a subquery in the
                // INSERT above — a correlated subselect sharing the INSERT's own
                // named parameters silently produced NULL, and this reads better
                // besides.
                using (var ratings = connection.CreateCommand())
                {
                    ratings.Transaction = transaction;
                    ratings.CommandText = archiveUnchanged
                        ? """
                          UPDATE songs
                             SET rating = (SELECT r.rating FROM song_ratings r
                                            WHERE r.psarc_path = songs.psarc_path
                                              AND r.song_key   = songs.song_key)
                           WHERE psarc_path = $p
                             AND rating <= 0
                             AND EXISTS (SELECT 1 FROM song_ratings r
                                          WHERE r.psarc_path = songs.psarc_path
                                            AND r.song_key   = songs.song_key);
                          """
                        // The archive CHANGED under a path we had already
                        // indexed, so the carried-over rating was computed from
                        // note data that is gone. Drop it rather than
                        // serve it — leaving it would also resurrect it after
                        // the next schema rebuild, which finds no `files` row and
                        // so cannot tell that it is stale.
                        : "DELETE FROM song_ratings WHERE psarc_path = $p;";
                    ratings.Parameters.AddWithValue("$p", psarcPath);
                    ratings.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (Exception ex)
            {
                LogFailure(ref _writeFailureWarned, "write", psarcPath, ex);
            }
        }

        public void PurgeMissing(string rootPath, IReadOnlyCollection<string> presentPsarcPaths)
        {
            // This DELETEs rows, so any snapshot taken before it is wrong.
            // A scan purges once, before its loop, so in practice this simply
            // orders the bulk read after the purge rather than re-reading.
            _prefetchAttempted = false;
            _prefetchUsable = false;
            _prefetchedFiles = null;
            _prefetchedSongs = null;
            _coverListings = null;

            try
            {
                // Prefix compare in managed code instead of SQL LIKE: LIKE
                // needs escaping for %/_ (legal in paths) and is
                // case-sensitive for non-ASCII. The index holds at most a few
                // thousand paths, so pulling them is cheap.
                //
                // TrimEndingDirectorySeparator does NOT trim the separator that
                // IS the root: "D:\" comes back as "D:\", so appending one built
                // the prefix "D:\\", which no enumerated path can start with. A
                // library pointed at a whole DRIVE therefore purged nothing,
                // ever — no stale rows removed, and not even the "purged N
                // deleted file(s)" line to say so. UNC roots were fine only
                // because the trim does bite there ("\\srv\share\").
                var trimmedRoot = Path.TrimEndingDirectorySeparator(rootPath);
                var rootPrefix = trimmedRoot.Length > 0 && IsSeparator(trimmedRoot[^1])
                    ? trimmedRoot
                    : trimmedRoot + Path.DirectorySeparatorChar;

                // ORDINAL, matching how every other statement in this file
                // addresses psarc_path. The column is TEXT PRIMARY KEY under
                // SQLite's BINARY collation, the point lookups compare with `=`,
                // and the prefetch dictionaries are built StringComparer.Ordinal
                // for exactly that reason. Only this set was ignore-case, and
                // the disagreement made a case-only rename (legal on NTFS, and
                // it changes neither size nor mtime) produce a permanent zombie:
                // the reader missed the old row and inserted a second one, the
                // ordinal DELETE in UpsertFile left the old one alone, and this
                // set then called it "present" and refused to purge it.
                var present = new HashSet<string>(presentPsarcPaths, StringComparer.Ordinal);

                var stale = new List<string>();
                using (var select = connection.CreateCommand())
                {
                    // A schema rebuild clears files but preserves song_ratings.
                    // Include those surviving paths or a deleted archive's rating
                    // is never purged and can migrate to its later replacement.
                    select.CommandText =
                        "SELECT psarc_path FROM files UNION SELECT psarc_path FROM song_ratings;";
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        var path = reader.GetString(0);
                        if (path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                            !present.Contains(path))
                        {
                            stale.Add(path);
                        }
                    }
                }

                if (stale.Count == 0) return;

                using var transaction = connection.BeginTransaction();
                using (var delete = connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    // song_ratings GOES TOO, and that third statement is the
                    // whole point of this hunk. The carry-over table is keyed by
                    // (psarc_path, song_key) with no freshness dimension, and
                    // UpsertFile reads "no `files` row" as "a schema rebuild —
                    // carry the rating over". A purge manufactures a THIRD
                    // producer of that state: the archive was DELETED. So a
                    // psarc removed from the library and later replaced at the
                    // same path — a re-downloaded CDLC, a different cut under
                    // the same file name; song keys are stable across
                    // revisions — had the dead chart's difficulty stamped onto
                    // the new one, silently, and it stayed until the user
                    // happened to open that song. Same fault
                    // An_archive_that_changed_under_the_same_path_loses_its_
                    // carried_rating exists for, reached through the purge.
                    //
                    // It also unbounds the table: nothing else ever deleted a
                    // row for an archive that simply went away.
                    delete.CommandText =
                        "DELETE FROM songs WHERE psarc_path = $p; " +
                        "DELETE FROM song_ratings WHERE psarc_path = $p; " +
                        "DELETE FROM files WHERE psarc_path = $p;";
                    var p = delete.Parameters.AddWithValue("$p", "");
                    foreach (var path in stale)
                    {
                        p.Value = path;
                        delete.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
                logger.Debug(AppLogCategory.Import,
                    $"PSARC library index purged {stale.Count} deleted file(s) under '{rootPath}'.");
            }
            catch (Exception ex)
            {
                LogFailure(ref _writeFailureWarned, "purge", rootPath, ex);
            }
        }

        /// <summary>The <c>(size, mtime)</c> currently recorded for a path, or
        /// null when the path is not indexed (a fresh db, or the empty `files`
        /// table a schema rebuild leaves behind).</summary>
        private (long Size, long Mtime)? ReadFileStat(SqliteTransaction transaction, string psarcPath)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT file_size, mtime_utc_ticks FROM files WHERE psarc_path = $p;";
            command.Parameters.AddWithValue("$p", psarcPath);
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
        }

        /// <summary>Either platform separator — a configured root may use '/'
        /// even on Windows, while the enumerated paths always use '\'.</summary>
        private static bool IsSeparator(char c)
            => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;

        public void Dispose()
        {
            // Before the connection: a prepared statement holds a sqlite3_stmt
            // that must be finalized against a live connection handle.
            _fileLookup?.Dispose();
            _songLookup?.Dispose();
            connection.Dispose();
        }

        private void LogFailure(ref bool warned, string operation, string path, Exception ex)
        {
            var message =
                $"PSARC library index {operation} failed (cache skipped). Path='{path}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.";
            if (!warned)
            {
                warned = true;
                logger.Warning(AppLogCategory.Import, message);
            }
            else
            {
                logger.Trace(AppLogCategory.Import, message);
            }
        }
    }
}

/// <summary>
///     Source-generated JSON for the index's one serialized column
///     (<c>songs.arrangements_json</c>). JSON columns hold the arrangement list
///     (incl. tone slots); the entries are tiny (a few hundred bytes per song),
///     so JSON keeps the schema flat without an arrangements table — but a warm
///     scan deserializes one per SONG, 2,160 times on the owner's library, and
///     the reflection serializer cost 78.0 ms of that scan (measured Release,
///     3 reps, real library).
///     <para>
///         The emitted shape must stay identical to what the reflection
///         serializer produced, because every already-indexed row was written by
///         it: no naming policy and no case-insensitive matching, i.e. exactly
///         <see cref="JsonSerializerOptions.Default" />. Changing either would
///         make existing rows deserialize to DEFAULTS, silently and forever —
///         nothing about the archives changes when the app is upgraded, so a
///         stale <c>(size, mtime)</c> hit keeps serving the wrong answer. That
///         is the same trap the <c>SchemaVersion</c> comment describes, and it
///         is why a shape change here needs a schema bump too.
///     </para>
/// </summary>
[JsonSerializable(typeof(List<LibraryArrangementEntry>))]
[JsonSerializable(typeof(IReadOnlyList<LibraryArrangementEntry>))]
internal partial class PsarcIndexJsonContext : JsonSerializerContext;
