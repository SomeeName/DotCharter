namespace DotCharter.Services.Diagnostics;

/// <summary>Records that something was used. The seam UI code depends on.</summary>
public interface IUsageTracker
{
    /// <summary>Counts one use of <paramref name="id" />. Must never throw.</summary>
    void Record(string kind, string id);

    /// <summary>
    ///     Adds time something was open. Paired with a preceding
    ///     <see cref="Record" />; adds no use of its own.
    /// </summary>
    void RecordDuration(string kind, string id, long milliseconds);

    /// <summary>The live counts, for a report or a diagnostics bundle.</summary>
    UsageStatistics Snapshot { get; }

    /// <summary>Writes any pending counts to disk now.</summary>
    void Flush();
}

/// <summary>
///     The impure edge over <see cref="UsageStatistics" />: loads the counts once,
///     accumulates in memory and writes them back debounced.
///     <para>
///         <b>Recording is on the UI thread, on the click.</b> So it does exactly
///         two things — bump an int under a lock and set a flag. The write happens
///         on the flush, never on the keystroke: a shortcut a charter holds down
///         must not turn into file I/O per repeat.
///     </para>
///     <para>
///         Every failure is swallowed. This is a counter for deciding what to put
///         in a submenu — it may never be the reason an edit does not happen.
///     </para>
/// </summary>
public sealed class UsageTrackingService : IUsageTracker, IDisposable
{
    private readonly string _path;
    private readonly IAppLogger? _logger;
    private readonly Func<long> _clock;
    private readonly Lock _gate = new();
    private readonly UsageStatistics _stats;

    private bool _dirty;
    private bool _disposed;

    public UsageTrackingService(string? path = null, IAppLogger? logger = null, Func<long>? clock = null)
    {
        _path = path ?? Path.Combine(WritablePaths.ConfigRoot, UsageStatistics.FileName);
        _logger = logger;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        UsageStatistics loaded;
        try
        {
            loaded = UsageStatistics.FromJson(File.Exists(_path) ? AtomicFile.ReadAllText(_path) : null);
        }
        catch (IOException)
        {
            loaded = new UsageStatistics();
        }
        catch (UnauthorizedAccessException)
        {
            loaded = new UsageStatistics();
        }

        _stats = loaded;
    }

    public UsageStatistics Snapshot => _stats;

    public void Record(string kind, string id)
    {
        if (_disposed) return;

        try
        {
            lock (_gate)
            {
                _stats.Record(kind, id, _clock());
                _dirty = true;
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(AppLogCategory.Application, $"Usage counter failed for '{kind}/{id}': {ex.Message}");
        }
    }

    public void RecordDuration(string kind, string id, long milliseconds)
    {
        if (_disposed) return;

        try
        {
            lock (_gate)
            {
                _stats.RecordDuration(kind, id, milliseconds);
                _dirty = true;
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(AppLogCategory.Application, $"Usage duration failed for '{kind}/{id}': {ex.Message}");
        }
    }

    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            json = _stats.ToJson();
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, json);
        }
        catch (Exception ex)
        {
            _logger?.Debug(AppLogCategory.Application, $"Could not write usage statistics: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Flush();
    }
}
