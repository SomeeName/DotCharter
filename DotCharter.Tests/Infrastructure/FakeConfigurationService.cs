namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Minimal in-memory <see cref="IConfigurationService" /> for tests that need
///     services depending on AppConfig but don't care about persistence.
/// </summary>
public sealed class FakeConfigurationService : IConfigurationService
{
    public AppConfig AppConfig { get; set; } = new();
    public KeybindConfig Keybinds { get; set; } = new();

    public AppConfig LoadAppConfig() => AppConfig;

    public void SaveAppConfig(AppConfig config)
    {
        AppConfig = config;
        RecordWrite();
    }

    public void Update(Action<AppConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_sync)
        {
            mutate(AppConfig);
        }

        RecordWrite();
    }

    /// <summary>
    ///     A snapshot of the remembered-directory fields taken at the moment of
    ///     each PERSISTING call. The fake hands back ONE mutable instance, so a
    ///     test that reads <see cref="AppConfig" /> afterwards cannot tell a
    ///     value that was written from one merely assigned to the cached object —
    ///     and that difference is a real bug shape (a bare
    ///     <c>AppConfig.LastRscpDir = dir</c> arms no write at all against the
    ///     real <c>JsonConfigurationService</c>).
    /// </summary>
    public List<(string LastProjectPath, string LastRscpDir, string LastAudioDir)> Writes { get; } = [];

    private void RecordWrite() =>
        Writes.Add((AppConfig.LastProjectPath, AppConfig.LastRscpDir, AppConfig.LastAudioDir));

    private readonly object _sync = new();

    public KeybindConfig LoadKeybinds() => Keybinds;

    public void SaveKeybinds(KeybindConfig config) => Keybinds = config;

    /// <summary>Nothing is cached from disk here; counted so tests can assert the
    /// import path actually asks for an invalidation.</summary>
    public int InvalidateCacheCount { get; private set; }

    public void InvalidateCache() => InvalidateCacheCount++;

    /// <summary>Nothing is deferred here; counted so tests can assert a caller
    /// that reads appconfig.json FROM DISK flushed the coalesced writer first.</summary>
    public int FlushCount { get; private set; }

    public void Flush() => FlushCount++;
}
