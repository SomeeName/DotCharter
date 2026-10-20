using System.Text.Json;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.RigLab;

/// <summary>
///     <c>riglab.json</c> under RigLab's own per-user folder, read once, cached,
///     and written through the same coalescing writer DotCharter's config uses.
///     <para>
///         <b>A degraded read is never authoritative</b> (the rule every JSON
///         store in this repo follows): an ABSENT file starts from
///         <see cref="RigLabConfig.CreateDefault" />; a CORRUPT one is moved aside
///         as <c>riglab.corrupt-&lt;stamp&gt;.json</c> and then starts fresh; an
///         UNREADABLE one (locked, access denied) is presumed intact, so defaults
///         are served but never cached and every save is refused until a read
///         succeeds — otherwise a sharing violation would write factory defaults
///         over the player's settings.
///     </para>
/// </summary>
public sealed class RigLabConfigService : IRigConfigService, IDisposable
{
    public const string FileName = "riglab.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly CoalescedFileWriter<string> _writer;
    private RigLabConfig? _cached;
    private bool _unreadable;

    public RigLabConfigService(string? path = null, TimeSpan? debounce = null)
    {
        _path = path ?? WritablePaths.ResolveConfigFile(FileName);
        _writer = new CoalescedFileWriter<string>(
            static (p, text) => AtomicFile.WriteAllText(p, text),
            ex => LastWriteError = ex,
            debounce);
    }

    /// <summary>Where the file lives (shown in Settings, and a test seam).</summary>
    public string FilePath => _path;

    /// <summary>The last background write failure, for the status line.</summary>
    public Exception? LastWriteError { get; private set; }

    /// <summary>True when the file exists but could not be read: saves are
    /// refused until it can be.</summary>
    public bool IsUnreadable
    {
        get { lock (_gate) return _unreadable; }
    }

    public RigLabConfig Load()
    {
        lock (_gate)
        {
            if (_cached != null) return _cached;
            var (config, cacheable) = ReadLocked();
            // A published build may carry a TONE3000 client id; a file that has
            // none takes it, so a fresh install can sign in without setup.
            if (string.IsNullOrWhiteSpace(config.Tone3000ClientId))
                config.Tone3000ClientId = RigLabBuild.Tone3000ClientId;
            if (cacheable) _cached = config;
            return config;
        }
    }

    public void Update(Action<RigLabConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        string text;
        lock (_gate)
        {
            var config = _cached ?? Load();
            mutate(config);
            if (_unreadable) return;
            text = JsonSerializer.Serialize(config, Json);
        }
        _writer.Queue(_path, text);
    }

    public void Flush() => _writer.Flush();

    RigConfig IRigConfigService.LoadRigConfig() => Load();

    void IRigConfigService.UpdateRigConfig(Action<RigConfig> mutate) => Update(c => mutate(c));

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }

    private (RigLabConfig Config, bool Cacheable) ReadLocked()
    {
        if (!File.Exists(_path))
        {
            _unreadable = false;
            return (RigLabConfig.CreateDefault(), true);
        }

        string text;
        try
        {
            text = AtomicFile.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Presumed INTACT: serve defaults, cache nothing, save nothing.
            _unreadable = true;
            return (RigLabConfig.CreateDefault(), false);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RigLabConfig>(text);
            if (parsed != null)
            {
                _unreadable = false;
                return (parsed, true);
            }
        }
        catch (JsonException)
        {
            // fall through to the quarantine below
        }

        _unreadable = !TryQuarantine();
        return (RigLabConfig.CreateDefault(), !_unreadable);
    }

    private bool TryQuarantine()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path) ?? ".";
            var aside = Path.Combine(dir,
                $"{Path.GetFileNameWithoutExtension(_path)}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json");
            File.Move(_path, aside);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
