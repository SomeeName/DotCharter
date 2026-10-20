namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     Where the index gets a genre for one project file. Production is
///     <see cref="RscpChartGenreSource" />; a test substitutes a counting fake,
///     which is what makes "read ONCE per scan, never per plan generation"
///     assertable rather than assumed.
/// </summary>
public interface IChartGenreSource
{
    /// <summary>A cheap identity for the file's CURRENT content — a stat, not a
    /// read. Null when the file is gone. An unchanged stamp is what lets the
    /// index skip a re-read; a changed one is what makes an edited chart pick up
    /// its new genre on the next scan.</summary>
    string? StampOf(string projectPath);

    /// <summary>Reads the authored genre. MAY throw — the index catches, logs
    /// and records "unknown"; a malformed chart must never fail a scan.</summary>
    string? ReadGenre(string projectPath);
}

/// <summary>The real source: a file stat plus <see cref="RscpGenreReader" />.</summary>
public sealed class RscpChartGenreSource : IChartGenreSource
{
    public string? StampOf(string projectPath)
    {
        var info = new FileInfo(projectPath);
        return info.Exists ? $"{info.LastWriteTimeUtc.Ticks:X}:{info.Length:X}" : null;
    }

    public string? ReadGenre(string projectPath) => RscpGenreReader.ReadGenre(projectPath);
}

/// <summary>
///     A scan-time cache of the genre authored in each of the player's OWN
///     charts, so the practice planner's genre preference has real data behind
///     it without ever touching a disk from the UI thread.
/// </summary>
/// <remarks>
///     Two rules the callers depend on:
///     <para>
///         <b><see cref="Warm" /> does the I/O, <see cref="GenreFor" /> does
///         not.</b> Warm is called ONCE per library scan, on a pool thread, next
///         to the PSARC scan that already runs there. GenreFor is a dictionary
///         lookup and is safe on the UI thread — which matters because the
///         planner's preview regenerates on every knob turn and
///         <c>BuildPlanRequest</c> is assembled on the UI thread before the
///         generation hops to the pool.
///     </para>
///     <para>
///         <b>Unknown is a real answer.</b> A file that is missing, unreadable,
///         malformed or simply carries no <c>dcGenre</c> all record as null, and
///         a consumer must read null as "unknown" — never as "excluded". Turning
///         a missing genre into an exclusion would hide most of a library the
///         moment a genre filter is picked.
///     </para>
/// </remarks>
public interface IChartGenreIndex
{
    /// <summary>The cached genre for a project path; null when unknown or not
    /// yet warmed. Pure lookup — no I/O, safe on the UI thread.</summary>
    string? GenreFor(string projectPath);

    /// <summary>Reads every path whose content changed since it was last seen.
    /// Returns how many files were actually READ, which is 0 for a re-warm of an
    /// unchanged set.</summary>
    int Warm(IEnumerable<string> projectPaths);

    /// <summary>
    ///     The distinct genres the player has ACTUALLY authored, most-used
    ///     first, capped at <paramref name="max" />. Pure lookup over the same
    ///     warmed cache — no I/O, safe on the UI thread.
    ///     <para>
    ///         This is what makes the Song Metadata dialog's quick-picks real
    ///         data rather than four invented words: a Rocksmith package carries
    ///         no genre anywhere, so the only honest source of "genres that mean
    ///         something to this user" is their own charts. An empty list is a
    ///         legitimate answer (nothing scanned yet, or nobody has ever typed
    ///         a genre) and the caller shows no chips at all.
    ///     </para>
    /// </summary>
    IReadOnlyList<string> KnownGenres(int max);
}

/// <inheritdoc cref="IChartGenreIndex" />
public sealed class ChartGenreIndex : IChartGenreIndex
{
    /// <summary>Stamp recorded for a path whose file is missing/unstattable, so
    /// the miss is remembered but a file that later APPEARS still re-reads (its
    /// real stamp can never be the empty string).</summary>
    private const string MissingStamp = "";

    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly IAppLogger? _logger;
    private readonly IChartGenreSource _source;

    public ChartGenreIndex(IChartGenreSource? source = null, IAppLogger? logger = null)
    {
        _source = source ?? new RscpChartGenreSource();
        _logger = logger;
    }

    public string? GenreFor(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        lock (_gate)
        {
            return _cache.TryGetValue(projectPath, out var entry) ? entry.Genre : null;
        }
    }

    public int Warm(IEnumerable<string> projectPaths)
    {
        var reads = 0;
        foreach (var path in projectPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            string? stamp;
            try
            {
                stamp = _source.StampOf(path);
            }
            catch (Exception ex)
            {
                LogSoft(path, ex);
                stamp = null;
            }

            if (stamp == null)
            {
                lock (_gate) _cache[path] = new Entry(MissingStamp, null);
                continue;
            }

            lock (_gate)
            {
                if (_cache.TryGetValue(path, out var have) &&
                    string.Equals(have.Stamp, stamp, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            string? genre = null;
            reads++;
            try
            {
                genre = _source.ReadGenre(path);
            }
            catch (Exception ex)
            {
                // Cached WITH the stamp on purpose: a chart that cannot be read
                // must not be retried on every scan for the rest of the session.
                LogSoft(path, ex);
            }

            lock (_gate)
            {
                _cache[path] = new Entry(stamp, string.IsNullOrWhiteSpace(genre) ? null : genre.Trim());
            }
        }

        return reads;
    }

    public IReadOnlyList<string> KnownGenres(int max)
    {
        if (max <= 0) return [];

        List<string> authored;
        lock (_gate)
        {
            authored = _cache.Values
                .Select(e => e.Genre)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g!.Trim())
                .ToList();
        }

        // Case-folded into ONE chip, but the spelling offered is the one the user
        // typed most often — writing "rock" back into a chart whose siblings all
        // say "Rock" would quietly split their own library in two.
        return authored
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(g => g.GroupBy(s => s, StringComparer.Ordinal)
                .OrderByDescending(s => s.Count())
                .ThenBy(s => s.Key, StringComparer.Ordinal)
                .First().Key)
            .ToList();
    }

    private void LogSoft(string path, Exception ex) => _logger?.Warning(AppLogCategory.Project,
        $"Could not read the genre of '{path}': {ex.Message}. Treating it as unknown.");

    private readonly record struct Entry(string Stamp, string? Genre);
}
