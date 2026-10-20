namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>The app's settings. Extends <see cref="IRigConfigService" />
/// (DotCharter.Tone): the rig and the audio host see the <see cref="RigConfig" />
/// half of the same <see cref="AppConfig" /> instance.</summary>
public interface IConfigurationService : IRigConfigService
{
    RigConfig IRigConfigService.LoadRigConfig() => LoadAppConfig();

    void IRigConfigService.UpdateRigConfig(Action<RigConfig> mutate) => Update(config => mutate(config));

    AppConfig LoadAppConfig();
    void SaveAppConfig(AppConfig config);

    /// <summary>
    ///     Atomically applies <paramref name="mutate" /> to the shared
    ///     <see cref="AppConfig" /> and persists it, holding an internal lock across
    ///     the whole read-modify-write. Use this — never
    ///     <c>LoadAppConfig()</c> + mutate + <c>SaveAppConfig()</c> — whenever the
    ///     mutation touches a collection member (<c>UserTonePresets</c>,
    ///     <c>ExportPresets</c>, <c>ThemeColorOverrides</c>, …): without the lock a
    ///     concurrent serialize throws "Collection was modified" and the save is
    ///     silently dropped. Plain scalar writes may still use
    ///     <see cref="SaveAppConfig" /> (its serialize is now locked too).
    /// </summary>
    void Update(Action<AppConfig> mutate);

    /// <summary>
    ///     Performs any COALESCED app-config write immediately and waits for it.
    ///     <para>
    ///         <see cref="SaveAppConfig" /> and <see cref="Update" /> update the
    ///         in-process config synchronously but only ARM the file write — the
    ///         atomic write + fsync measured 4.40 ms median / 13.48 ms max and
    ///         twelve <c>On&lt;X&gt;Changed</c> setters were paying it on the UI
    ///         thread, one of them per zoom step. Call this before anything reads
    ///         <c>appconfig.json</c> FROM DISK rather than through this service
    ///         (the diagnostics bundle and the config backup), and on both exit
    ///         paths — a crash inside the debounce window otherwise costs that
    ///         window's worth of settings.
    ///     </para>
    /// </summary>
    void Flush();

    KeybindConfig LoadKeybinds();
    void SaveKeybinds(KeybindConfig config);

    /// <summary>
    ///     Drops the in-process <see cref="AppConfig" /> cache so the next
    ///     <see cref="LoadAppConfig" /> re-reads the file. Required after anything
    ///     replaces <c>appconfig.json</c> BEHIND the service (backup import) —
    ///     without it the stale cached instance is handed to every reader and is
    ///     eventually written straight back over the new file.
    /// </summary>
    void InvalidateCache();
}
