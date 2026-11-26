using Microsoft.Data.Sqlite;

namespace DotCharter.Services.Library.Index;

/// <summary>Read-only song-key probes of the library index database (export key-clash checks).</summary>
internal sealed class SongKeyLookup(string dbPath, IAppLogger logger)
{
    public bool HasSongKey(string songKey)
    {
        if (string.IsNullOrWhiteSpace(songKey)) return false;
        try
        {
            if (!File.Exists(dbPath)) return false;

            using var connection = SqliteLibraryIndex.Connect(dbPath, SqliteOpenMode.ReadOnly, busySeconds: 1);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM songs WHERE song_key = $k COLLATE NOCASE LIMIT 1;";
            command.Parameters.AddWithValue("$k", songKey);
            return command.ExecuteScalar() is not null;
        }
        catch (Exception ex)
        {
            logger.Trace(AppLogCategory.Import,
                $"PSARC library index key lookup unavailable. Db='{dbPath}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
            return false;
        }
    }

    public IReadOnlyList<string> PackagePathsForSongKey(string songKey)
    {
        if (string.IsNullOrWhiteSpace(songKey)) return [];
        try
        {
            if (!File.Exists(dbPath)) return [];

            using var connection = SqliteLibraryIndex.Connect(dbPath, SqliteOpenMode.ReadOnly, busySeconds: 1);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT psarc_path FROM songs WHERE song_key = $k COLLATE NOCASE;";
            command.Parameters.AddWithValue("$k", songKey);
            using var reader = command.ExecuteReader();
            var paths = new List<string>();
            while (reader.Read()) paths.Add(reader.GetString(0));
            return paths;
        }
        catch (Exception ex)
        {
            logger.Trace(AppLogCategory.Import,
                $"PSARC library index key path lookup unavailable. Db='{dbPath}', " +
                $"type={ex.GetType().Name}, message='{ex.Message}'.");
            return [];
        }
    }
}
