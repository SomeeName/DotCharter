using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;
using DotCharter.Services.Library;

namespace DotCharter.Services.Infrastructure.Configuration;

public class JsonConfigurationService : IConfigurationService, IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _loggerSync = new();

    // Early errors (before a logger is wired up) are buffered and replayed on
    // first assignment so the user still sees configuration load failures. The
    // Rocksmith key notes ride along at Info, not Warning: a warning reaches
    // the status bar, and the keys have no UI anywhere.
    private readonly List<(string Message, Exception? Exception, ReportLevel Level)> _pendingErrors = [];

    private readonly object _configSync = new();
    private volatile AppConfig? _appConfigCache;
    private IAppLogger? _logger;

    /// <summary>
    ///     The stand-in config served while <c>appconfig.json</c> exists but could
    ///     not be READ. Deliberately NOT <see cref="_appConfigCache" />: leaving the
    ///     cache null makes the next load retry the file, so a transient lock heals
    ///     itself mid-session. Guarded by <see cref="_configSync" />.
    /// </summary>
    private AppConfig? _degraded;

    /// <summary>Log-once latch for the degraded state (the load path is re-entered
    /// on every read while it lasts). Reset when the file becomes readable.</summary>
    private bool _degradedLogged;

    /// <summary>The same three-outcome shape for <c>keybinds.json</c>: true while
    /// the last read FAILED for a reason other than "absent" or "corrupt and
    /// quarantined", in which case <see cref="SaveKeybinds" /> is a no-op — the
    /// factory defaults it would otherwise write are not the user's binds.
    /// Guarded by <see cref="_keybindsSync" />.</summary>
    private bool _keybindsDegraded;

    private bool _keybindsDegradedLogged;
    private readonly object _keybindsSync = new();

    /// <summary>
    ///     Every app-config write goes through here, on a background thread and
    ///     coalesced — see <see cref="CoalescedFileWriter{TPayload}" /> for the
    ///     measurements that made it necessary. The payload is the LIVE
    ///     <see cref="AppConfig" />, not pre-rendered JSON, so the serialize
    ///     moves off the UI thread too and still happens under
    ///     <see cref="_configSync" /> (see <see cref="WriteAppConfigNow" />).
    /// </summary>
    private readonly CoalescedFileWriter<AppConfig> _appConfigWriter;

    public JsonConfigurationService() : this(null, null) { }

    /// <summary>
    ///     The app's ONE configuration service (<c>App</c>'s field), which is also where
    /// the Rocksmith keys are read from: <see cref="Psarc.RocksmithKeys" />
    ///     reads this service's <see cref="AppConfig.RocksmithKeys" />. Wiring only —
    ///     nothing is loaded here. A second service must never do this.
    /// </summary>
    public static JsonConfigurationService ForApplication()
    {
        var service = new JsonConfigurationService();
        Psarc.RocksmithKeys.UseConfiguration(service);
        return service;
    }

    /// <param name="debounce">Test seam; defaults to
    /// <see cref="CoalescedFileWriter{TPayload}.DefaultDebounce" />.</param>
    /// <param name="delay">Test seam for the debounce wait.</param>
    internal JsonConfigurationService(
        TimeSpan? debounce, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _appConfigWriter = new CoalescedFileWriter<AppConfig>(
            WriteAppConfigNow,
            ex => ReportError($"Failed to save app config: {ex.Message}", ex),
            debounce, delay);
    }

    private string AppConfigPath => WritablePaths.ResolveConfigFile("appconfig.json");
    private string KeybindsPath => WritablePaths.ResolveConfigFile("keybinds.json");

    /// <summary>
    ///     Hook up the logger after DI resolves both services. Any errors captured
    ///     during early load are replayed through this logger.
    /// </summary>
    public IAppLogger? Logger
    {
        get
        {
            lock (_loggerSync)
            {
                return _logger;
            }
        }
        set
        {
            List<(string Message, Exception? Exception, ReportLevel Level)>? replay = null;
            lock (_loggerSync)
            {
                _logger = value;
                if (value != null && _pendingErrors.Count > 0)
                {
                    replay = [.. _pendingErrors];
                    _pendingErrors.Clear();
                }
            }

            if (replay == null) return;
            foreach (var (message, exception, level) in replay) Emit(value!, message, exception, level);
        }
    }

    /// <summary>
    ///     Loads (and caches) <c>appconfig.json</c>. Three outcomes, and telling
    ///     them apart is the whole point — the same rule
    ///     <see cref="Gameplay.Profiles.PlayerProfileService" /> and
    ///     <see cref="Audio.LivePlay.PracticeProfileService" /> already follow:
    ///     ABSENT is a new install, CORRUPT is content that is not a config, and
    ///     UNREADABLE is a file presumed intact that could not be opened right now.
    ///     <para>
    ///         Treating all three the same — swallow, cache
    ///         <c>new AppConfig()</c>, and then let the very next
    ///         <see cref="Update" /> (one happens on nearly every open and save)
    ///         atomically write those defaults over the file — would REPLACE a config momentarily
    ///         locked by an antivirus scan, a cloud sync or a backup tool,
    ///         on the first property read, by a factory-fresh
    ///         one: every setting, path, device choice, keybind-adjacent flag and
    ///         recent-file list the user had, gone, with a single line in the log.
    ///     </para>
    /// </summary>
    public AppConfig LoadAppConfig()
    {
        // Lock-free fast path — the cache reference is only ever swapped whole.
        var cached = _appConfigCache;
        if (cached != null) return cached;

        // The first load is warmed on a worker thread during app startup while
        // the UI thread parses XAML, so this must be safe against a concurrent
        // first call from the service-wiring path.
        lock (_configSync)
        {
            if (_appConfigCache != null) return _appConfigCache;

            var path = AppConfigPath;
            var unreadable = false;
            try
            {
                if (File.Exists(path))
                {
                    // AtomicFile.ReadAllText, not File.ReadAllText: this file is
                    // WRITTEN through AtomicFile, and a plain read denies delete
                    // — which is exactly the save/load collision the write-side
                    // retry exists to absorb. FileShare.Delete lets the writer's
                    // rename proceed while this handle keeps reading the whole
                    // old content.
                    var json = AtomicFile.ReadAllText(path);
                    var config = JsonSerializer.Deserialize<AppConfig>(json, _jsonOptions);
                    if (config != null)
                    {
                        NormalizeAppConfig(config);
                        AdoptRocksmithKeys(config, path);
                        _degraded = null;
                        _degradedLogged = false;
                        _appConfigCache = config;
                        LogTrace($"Loaded app config from '{path}' ({json.Length} bytes).");
                        return config;
                    }

                    // A literal `null` document: readable bytes that are not a
                    // config. Same class as a JsonException — quarantine, not
                    // overwrite.
                    ReportError($"Parsed '{path}' was null; falling back to defaults.", null);
                    unreadable = !TryQuarantineCorruptConfig(path, "the file parsed as null");
                }
                else
                {
                    LogTrace($"App config file '{path}' not found; using defaults.");
                }
            }
            catch (JsonException ex)
            {
                // CORRUPT: the bytes are readable and are not a config. Starting
                // fresh is the only way forward, but the old bytes are the user's
                // only copy of their settings, so the file is moved ASIDE rather
                // than overwritten — and if it cannot be moved, we do not write.
                ReportError($"Failed to load app config from '{path}': {ex.Message}", ex);
                unreadable = !TryQuarantineCorruptConfig(path, ex.Message);
            }
            catch (Exception ex)
            {
                // COULD NOT READ RIGHT NOW (IOException, UnauthorizedAccessException,
                // …). The file is presumed intact; defaults must never become
                // authoritative, and nothing may be written this session.
                ReportError($"Failed to load app config from '{path}': {ex.Message}", ex);
                unreadable = true;
            }

            if (unreadable)
            {
                if (!_degradedLogged)
                {
                    _degradedLogged = true;
                    ReportError(
                        $"App config '{path}' could not be read; running on DEFAULTS this session and " +
                        "NOT writing over it. Settings changed now will not persist.", null);
                }

                // Deliberately NOT cached: leaving _appConfigCache null makes the
                // next call retry the read, so a transient lock heals itself and
                // the session goes back to the real file.
                return _degraded ??= new AppConfig();
            }

            _degraded = null;
            _degradedLogged = false;
            var fallback = new AppConfig();
            AdoptRocksmithKeys(fallback, path);
            _appConfigCache = fallback;
            return fallback;
        }
    }

    /// <summary>
    ///     Moves a config whose CONTENT is not a config aside as
    ///     <c>appconfig.corrupt-&lt;stamp&gt;.json</c>, so the user still has the
    ///     bytes. Returns false when the move failed — in which case the original
    ///     is still in place and must not be written over.
    /// </summary>
    private bool TryQuarantineCorruptConfig(string path, string cause, string label = "App config")
    {
        var quarantine = Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}" +
            Path.GetExtension(path));

        try
        {
            // ReplaceWithRetry, not a bare File.Move: whatever left this file
            // unparseable may well still have it open, and a rename denied by an
            // AV or indexer hold would otherwise report the config as
            // unquarantinable and refuse the write for the whole session.
            AtomicFile.ReplaceWithRetry(path, quarantine);
            ReportError($"{label} was unparseable ({cause}); moved to " +
                        $"'{Path.GetFileName(quarantine)}' and starting fresh.", null);
            return true;
        }
        catch (Exception ex)
        {
            ReportError($"{label} is unparseable ({cause}) and could not be quarantined; " +
                        "refusing to write over it.", ex);
            return false;
        }
    }

    public void SaveAppConfig(AppConfig config)
    {
        lock (_configSync)
        {
            SaveLocked(config);
        }
    }

    /// <summary>
    ///     Performs any coalesced app-config write immediately and waits for it.
    ///     Both exit paths call this (<c>App.TerminateNow</c> — which
    ///     <c>TerminateProcess</c>es, so <see cref="Dispose" /> never runs — and
    ///     <c>App.RunCleanShutdown</c>), as does anything about to read
    ///     <c>appconfig.json</c> FROM DISK rather than through this service (the
    ///     diagnostics bundle and the config backup are the two).
    ///     <para>
    ///         Deliberately does NOT take <see cref="_configSync" />: the write it
    ///         waits for takes that lock on a DIFFERENT thread, and <c>lock</c> is
    ///         reentrant per thread, not per call stack.
    ///     </para>
    /// </summary>
    public void Flush() => _appConfigWriter.Flush();

    public void Dispose()
    {
        _appConfigWriter.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Update(Action<AppConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        // The whole read-modify-write is atomic under _configSync. `lock` is
        // reentrant, so the LoadAppConfig() cache-miss path (which locks the
        // same object) nests safely; no other thread can serialize the config
        // in the window between the mutation and the save.
        lock (_configSync)
        {
            var config = LoadAppConfig();
            mutate(config);
            SaveLocked(config);
        }
    }

    /// <summary>
    ///     Caller must hold _configSync. Publishes the new config to the cache
    ///     SYNCHRONOUSLY — several callers <c>LoadAppConfig()</c> straight after
    ///     a save and must see the values they just wrote — and only ARMS the
    ///     file write. Nothing here touches the disk.
    /// </summary>
    private void SaveLocked(AppConfig config)
    {
        if (_degraded != null)
        {
            // Never write over a config we failed to READ. What we hold is a
            // factory-fresh AppConfig plus whatever this session changed; the file
            // it would replace is everything the user has ever set. The change is
            // lost; the settings are not. The next load retries the file, so this
            // clears itself the moment the lock goes away.
            _degraded = config; // keep this session's edits visible in memory
            LogTrace("App config was not readable this session; skipping the write so the existing " +
                     "settings survive. This change was not persisted.");
            return;
        }

        _appConfigCache = config;
        _appConfigWriter.Queue(AppConfigPath, config);
    }

    /// <summary>
    ///     The background half of a save. Serializes under the same lock the
    ///     read-modify-write path (<see cref="Update" />) and the load-cache path
    ///     use, so a collection mutation driven through <see cref="Update" /> can
    ///     never run its List/Dictionary write concurrently with this serialize
    ///     (which would throw "Collection was modified" and silently drop the
    ///     save). This is the ONLY place appconfig.json is written.
    /// </summary>
    private void WriteAppConfigNow(string path, AppConfig config)
    {
        string json;
        lock (_configSync)
        {
            json = JsonSerializer.Serialize(config, _jsonOptions);
        }

        // Atomic — a truncated appconfig.json resets every setting the user has.
        AtomicFile.WriteAllText(path, json);
        LogTrace($"Saved app config to '{path}' ({json.Length} bytes).");
    }

    public void InvalidateCache()
    {
        // Drop the pending write FIRST. This is called after something replaced
        // appconfig.json behind the service (a backup import); a payload queued
        // before the import predates it, and letting it land would write the
        // pre-import settings straight back over the imported file — the exact
        // failure the cache invalidation itself exists to prevent.
        _appConfigWriter.Discard(AppConfigPath);

        lock (_configSync)
        {
            _appConfigCache = null;
            _degraded = null;
            _degradedLogged = false;
        }

        LogTrace("App config cache invalidated; the next load re-reads the file.");
    }

    /// <summary>
    ///     Same three outcomes as <see cref="LoadAppConfig" />:
    ///     ABSENT starts fresh, CORRUPT is quarantined aside and starts fresh,
    ///     and UNREADABLE — an <see cref="IOException" />, a sharing violation,
    ///     an AV/cloud-sync hold — serves defaults but marks the store DEGRADED,
    ///     so <see cref="SaveKeybinds" /> refuses to write until a read succeeds.
    ///     This had the bare <c>catch (Exception)</c> that returned defaults for
    ///     all three: the settings page then read them back through
    ///     <c>KeybindSettingsViewModel</c>, and its next save wrote every
    ///     factory chord over the user's binds.
    /// </summary>
    public KeybindConfig LoadKeybinds()
    {
        var path = KeybindsPath;
        var unreadable = false;
        try
        {
            if (File.Exists(path))
            {
                // Same rule as LoadAppConfig, and it matters MORE here: nothing
                // serializes SaveKeybinds against LoadKeybinds in-process (they
                // take no lock, unlike the app config's _configSync), so a plain
                // read is the collision rather than merely risking it.
                var json = AtomicFile.ReadAllText(path);
                var config = JsonSerializer.Deserialize<KeybindConfig>(json, _jsonOptions);
                if (config != null)
                {
                    var stampBefore = config.MigrationVersion;
                    NormalizeKeybinds(config);
                    LogTrace($"Loaded keybinds from '{path}' ({json.Length} bytes).");
                    MarkKeybindsReadable();

                    // Persist the stamp, or the waves re-run on every launch and
                    // a chord the user deliberately chose that HAPPENS to be a
                    // retired default is re-migrated for ever. This writes at
                    // most once per upgrade — the next load reads the stamp and
                    // does nothing.
                    if (config.MigrationVersion > stampBefore) SaveKeybinds(config);
                    return config;
                }

                ReportError($"Parsed '{path}' was null; falling back to defaults.", null);
                unreadable = !TryQuarantineCorruptConfig(path, "the file parsed as null", "Keybinds");
            }
            else
            {
                LogTrace($"Keybinds file '{path}' not found; using defaults.");
            }
        }
        catch (JsonException ex)
        {
            ReportError($"Failed to load keybinds from '{path}': {ex.Message}", ex);
            unreadable = !TryQuarantineCorruptConfig(path, ex.Message, "Keybinds");
        }
        catch (Exception ex)
        {
            ReportError($"Failed to load keybinds from '{path}': {ex.Message}", ex);
            unreadable = true;
        }

        lock (_keybindsSync)
        {
            _keybindsDegraded = unreadable;
            if (unreadable && !_keybindsDegradedLogged)
            {
                _keybindsDegradedLogged = true;
                ReportError($"Keybinds '{path}' could not be read; running on DEFAULTS and NOT writing over it. " +
                            "Keybind changes made now will not persist until the file can be read.", null);
            }

            if (!unreadable) _keybindsDegradedLogged = false;
        }

        // Factory defaults ARE the current wave, so stamp them rather than
        // leaving a fresh install at version 0 — otherwise the first
        // SaveKeybinds writes an unstamped file and the waves run over it once
        // more on the next launch. Nothing is written here: an absent
        // keybinds.json stays absent until the user changes a bind.
        return new KeybindConfig { MigrationVersion = KeybindConfig.CurrentMigrationVersion };
    }

    private void MarkKeybindsReadable()
    {
        lock (_keybindsSync)
        {
            _keybindsDegraded = false;
            _keybindsDegradedLogged = false;
        }
    }

    /// <summary>One-time config migrations applied on every successful load
    /// (idempotent; persisted with the next save).</summary>
    private static void NormalizeAppConfig(AppConfig config)
    {
        // v0.32.5: the DEFAULT present mode changed Fifo → Mailbox (identical
        // pixels, no vblank block — the 3D loop can reach high-refresh rates).
        // Configs written under the old default still carry "Fifo"; migrate
        // them ONCE. Re-choosing Fifo in Settings afterwards sticks (the latch
        // stays true). "Immediate" users are never touched.
        if (!config.PresentModeMailboxMigrated)
        {
            if (string.Equals(config.PresentMode, "Fifo", StringComparison.OrdinalIgnoreCase))
                config.PresentMode = "Mailbox";
            config.PresentModeMailboxMigrated = true;
        }

        // 0.57.4: the CQT verifier became the Live Play default (it confirms 92 % of
        // perfectly played sounds on real DI against the classic matcher's 60 %). Configs
        // saved while it was opt-in carry the old DEFAULT, so switch them over ONCE; a
        // later "off" sticks because the latch stays true.
        if (!config.LivePlayCqtDefaultMigrated)
        {
            config.LivePlayUseCqtVerifier = true;
            config.LivePlayCqtDefaultMigrated = true;
        }

        // 0.69: technique judging split into one switch per technique. The 0.68 switch
        // was off by default, so `false` is a default, not a choice — the new defaults stand
        // (bends on). `true` was a choice to judge everything: all three stay on. Read once; the
        // next save drops it (WhenWritingNull).
        if (config.LivePlayJudgeTechniqueContours is { } judgedAll)
        {
            if (judgedAll)
            {
                config.LivePlayJudgeBends = true;
                config.LivePlayJudgeSlides = true;
                config.LivePlayJudgeVibrato = true;
            }

            config.LivePlayJudgeTechniqueContours = null;
        }

        // An explicit `"Ai": null` in the file. System.Text.Json ASSIGNS that over
        // the property initializer, so AppConfig.Ai — declared non-nullable, and
        // therefore read without a null check by everything that is entitled to —
        // comes out of the cache null and is handed to the whole app. A file with
        // no "Ai" property at all (every config written by an older build) keeps its
        // initializer and is already fine; this is the other half.
        //
        // Deliberately NOT a null-forgiving read at each call site: two of them
        // defended and the rest did not, which is exactly the split that makes this
        // shape survive review.
        config.Ai ??= DotCharter.Tone.Ai.AiProviderSettings.Disabled;

        // an explicit `"RocksmithKeys": null` (or a null value inside it) reads
        // as the empty section, so the next save writes the empty fields back.
        config.RocksmithKeys ??= new RocksmithKeysConfig();
        config.RocksmithKeys.Normalize();

        MigrateLibrarySongKeys(config);
    }

    /// <summary>
    /// the per-song settings keyed by song identity filed a library song under
    ///     <c>psarc:&lt;key&gt;</c>; they now use <c>lib:&lt;key&gt;</c>. Re-keyed in memory on
    ///     every load (idempotent); the next atomic save writes the new keys and drops the
    ///     old ones in the same write. An entry already under <c>lib:</c> wins over its
    ///     <c>psarc:</c> twin.
    /// </summary>
    internal static void MigrateLibrarySongKeys(AppConfig config)
    {
        config.SongTonePreferences ??= [];
        LibrarySongIdentity.MigrateKeys(config.SongTonePreferences);

        config.DropTunerSongs ??= [];
        var songs = new List<string>(config.DropTunerSongs.Count);
        foreach (var song in config.DropTunerSongs)
        {
            if (string.IsNullOrWhiteSpace(song)) continue;
            var current = LibrarySongIdentity.Normalize(song);
            if (!songs.Contains(current, StringComparer.OrdinalIgnoreCase)) songs.Add(current);
        }

        config.DropTunerSongs = songs;
    }

    /// <summary>
    ///     On a load that will be CACHED (never a degraded one): moves a
    ///     <see cref="LegacyRocksmithKeyFile" /> from older builds into the config
    ///     once, then logs each malformed key value by NAME — never its content —
    ///     at Info (see <see cref="_pendingErrors" />).
    ///     <para>
    ///         The migration fills only EMPTY fields (a key already in appconfig.json
    ///         wins), writes the config SYNCHRONOUSLY (the caller holds
    ///         <see cref="_configSync" />, which the write takes re-entrantly), and
    ///         deletes the old file only once that write succeeded, so no outcome
    ///         leaves the keys in neither place. A malformed old file is left in place
    ///         and logged; a failed write keeps it for the next launch while this
    ///         session still uses the keys in memory.
    ///     </para>
    /// </summary>
    private void AdoptRocksmithKeys(AppConfig config, string path)
    {
        var legacy = LegacyRocksmithKeyFile.PathBeside(path);
        if (File.Exists(legacy))
        {
            try
            {
                LegacyRocksmithKeyFile.FillEmpty(config.RocksmithKeys,
                    LegacyRocksmithKeyFile.Parse(File.ReadAllLines(legacy)));
                WriteAppConfigNow(path, config);
                File.Delete(legacy);
                ReportInfo($"Moved the keys from {LegacyRocksmithKeyFile.FileName} into appconfig.json " +
                              $"(RocksmithKeys) and deleted {LegacyRocksmithKeyFile.FileName}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                ReportInfo($"{LegacyRocksmithKeyFile.FileName} was not moved into appconfig.json " +
                              $"({ex.GetType().Name}: {ex.Message}); it was left in place.");
            }
        }

        foreach (var problem in config.RocksmithKeys.Problems()) ReportInfo(problem);
        CheckKeysFingerprint(config, path);
    }

    /// <summary>
    /// older releases drop the <c>RocksmithKeys</c> section on their next
    ///     save. Keeps a <see cref="RocksmithKeysFingerprint" /> record beside the config
    ///     (a hash, never a key): while keys are set it holds their fingerprint; keys
    ///     gone but a record present means they were wiped, and the user is told on
    ///     every launch until they are back, at Warning so it reaches the status bar. A
    ///     first run has no record, so it is silent.
    /// </summary>
    private void CheckKeysFingerprint(AppConfig config, string path)
    {
        var record = RocksmithKeysFingerprint.PathBeside(path);
        try
        {
            if (config.RocksmithKeys.HasAnyValue())
            {
                var current = RocksmithKeysFingerprint.Compute(config.RocksmithKeys);
                if (current != null && !string.Equals(RocksmithKeysFingerprint.Read(record), current, StringComparison.Ordinal))
                    AtomicFile.WriteAllText(record, current);
            }
            else if (RocksmithKeysFingerprint.Read(record) != null)
            {
                ReportWarning(RocksmithKeysFingerprint.VanishedMessage);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportInfo($"The Rocksmith keys fingerprint could not be updated ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    ///     The keybind migration waves, each applied AT MOST ONCE and then
    ///     stamped into <see cref="KeybindConfig.MigrationVersion" />.
    ///     <para>
    ///         Every wave below is an equality test against the chord a verb
    ///         defaulted to in older builds, so that a user who rebound the verb keeps
    ///         their choice. That rule has a hole the tests never reached: a
    ///         user who rebinds a verb ONTO its old default — Ctrl+I for the
    ///         Inspector, Ctrl+F for 3D fingering, Ctrl+B to add a beat, each of
    ///         them the obvious chord for its verb and therefore the one it used
    ///         to have — was re-migrated on EVERY load, so that chord became
    ///         permanently unusable for that verb and the reset happened
    ///         silently at each launch. The waves are gated on the stamp now;
    ///         the equality tests stay, because they are still what protects a
    ///         user mid-upgrade.
    ///     </para>
    ///     <para>
    ///         A config with no stamp (everything written before this existed)
    ///         reads as version 0 and gets every wave once, exactly as before.
    ///         <see cref="LoadKeybinds" /> persists the new stamp so the next
    ///         launch starts from it.
    ///     </para>
    /// </summary>
    private static void NormalizeKeybinds(KeybindConfig config)
    {
        var applied = config.MigrationVersion;

        if (applied < 1) NormalizeKeybindsWave1(config);
        if (applied < 2) NormalizeKeybindsWave2(config);
        if (applied < 3) NormalizeKeybindsWave3(config);
        if (applied < 4) NormalizeKeybindsWave4(config);

        config.MigrationVersion = Math.Max(applied, KeybindConfig.CurrentMigrationVersion);
        // Not a wave: a hand-edited file can carry a bad MIDI bind on any load.
        config.SanitizeMidiBindings();
    }

    /// <summary>Wave 1: the hand-shape collision and the v0.35 string-mask row.</summary>
    private static void NormalizeKeybindsWave1(KeybindConfig config)
    {
        var oldGenerateHandShape = new KeyChord(Key.H, KeyModifiers.Shift);
        if (config.GenerateHandShape == oldGenerateHandShape &&
            config.ToggleHarmonic == oldGenerateHandShape)
        {
            config.GenerateHandShape = new KeyChord(Key.G, KeyModifiers.Control | KeyModifiers.Shift);
        }

        // v0.35: the string-edit mask took the whole Ctrl+0..9 row
        // (StringMaskBase/StringMaskClear), which collided with the old
        // TogglePreview3D default of Ctrl+3. A config still carrying that exact
        // default is moved to the new one (Ctrl+P); a user who deliberately
        // rebound it is left alone, and so is one who chose Ctrl+3 for
        // something else — collisions resolve last-registration-wins with a
        // logged warning, never silently on load.
        var oldTogglePreview3D = new KeyChord(Key.D3, KeyModifiers.Control);
        if (config.TogglePreview3D == oldTogglePreview3D)
            config.TogglePreview3D = new KeyChord(Key.P, KeyModifiers.Control);

    }

    /// <summary>Wave 2 (v0.39): the first EOF alignment pass.</summary>
    private static void NormalizeKeybindsWave2(KeybindConfig config)
    {
        // v0.39: EOF's Seek family took the PgUp/PgDn key pair whole
        // (EOF_SRC/menu/song.c:64-94). Every action that lived on one of
        // those rows moves to its new home — but ONLY when the config still
        // holds the exact old default. A user who deliberately rebound one of
        // them keeps their choice, which is the same rule the two migrations
        // above follow, and the reason each of these is an equality test rather
        // than an unconditional write.
        MoveIfDefault(() => config.SeekNext, v => config.SeekNext = v,
            new KeyChord(Key.PageDown), new KeyChord(Key.PageDown, KeyModifiers.Shift));
        MoveIfDefault(() => config.SeekPrev, v => config.SeekPrev = v,
            new KeyChord(Key.PageUp), new KeyChord(Key.PageUp, KeyModifiers.Shift));
        MoveIfDefault(() => config.SeekNextExtend, v => config.SeekNextExtend = v,
            new KeyChord(Key.PageDown, KeyModifiers.Shift),
            new KeyChord(Key.PageDown, KeyModifiers.Shift | KeyModifiers.Alt));
        MoveIfDefault(() => config.SeekPrevExtend, v => config.SeekPrevExtend = v,
            new KeyChord(Key.PageUp, KeyModifiers.Shift),
            new KeyChord(Key.PageUp, KeyModifiers.Shift | KeyModifiers.Alt));
        MoveIfDefault(() => config.SeekPrevGridLine, v => config.SeekPrevGridLine = v,
            new KeyChord(Key.Left, KeyModifiers.Alt),
            new KeyChord(Key.PageUp, KeyModifiers.Control | KeyModifiers.Shift));
        MoveIfDefault(() => config.SeekNextGridLine, v => config.SeekNextGridLine = v,
            new KeyChord(Key.Right, KeyModifiers.Alt),
            new KeyChord(Key.PageDown, KeyModifiers.Control | KeyModifiers.Shift));
        MoveIfDefault(() => config.QueuePrevMatch, v => config.QueuePrevMatch = v,
            new KeyChord(Key.PageUp, KeyModifiers.Control),
            new KeyChord(Key.Y, KeyModifiers.Control | KeyModifiers.Shift));
        MoveIfDefault(() => config.QueueNextMatch, v => config.QueueNextMatch = v,
            new KeyChord(Key.PageDown, KeyModifiers.Control), new KeyChord(Key.Y, KeyModifiers.Shift));

        // v0.39: the three verb chords EOF spells differently and whose new
        // home was free here.
        MoveIfDefault(() => config.PasteAtMouse, v => config.PasteAtMouse = v,
            new KeyChord(Key.Insert, KeyModifiers.Control), new KeyChord(Key.Insert, KeyModifiers.Shift));
        MoveIfDefault(() => config.SnapSelected, v => config.SnapSelected = v,
            new KeyChord(Key.R, KeyModifiers.Alt), new KeyChord(Key.R, KeyModifiers.Shift));
        MoveIfDefault(() => config.ToggleMute, v => config.ToggleMute = v,
            new KeyChord(Key.M, KeyModifiers.Shift), new KeyChord(Key.X, KeyModifiers.Shift));
        MoveIfDefault(() => config.ToggleMuteGroup, v => config.ToggleMuteGroup = v,
            new KeyChord(Key.M, KeyModifiers.Shift | KeyModifiers.Alt),
            new KeyChord(Key.X, KeyModifiers.Shift | KeyModifiers.Alt));

    }

    /// <summary>Wave 3 (v0.47): the second EOF alignment pass.</summary>
    private static void NormalizeKeybindsWave3(KeybindConfig config)
    {
        // v0.47: the second EOF alignment pass — owner: "I want the
        // keybinds to generally be just like EOF has so it's familiar". Each of
        // these is a verb DotCharter already had under a chord of its own
        // invention, moved onto the one EOF_SRC spells for the same verb, and
        // every new home was free (KeybindDefaultsTests.NoTwoDefaultChordsCollide
        // is what says so). The ones EOF has and DotCharter has no verb for —
        // Toggle Anchor (A), Place RS Phrase (Shift+P), Select Rest (Shift+End) —
        // are NOT invented here; a keybind onto nothing is worse than none.
        MoveIfDefault(() => config.SetLoopStart, v => config.SetLoopStart = v,
            new KeyChord(Key.OemOpenBrackets, KeyModifiers.Alt),
            new KeyChord(Key.Home, KeyModifiers.Alt)); // EOF: Set start point
        MoveIfDefault(() => config.SetLoopEnd, v => config.SetLoopEnd = v,
            new KeyChord(Key.OemCloseBrackets, KeyModifiers.Alt),
            new KeyChord(Key.End, KeyModifiers.Alt)); // EOF: Set end point
        MoveIfDefault(() => config.SelectLike, v => config.SelectLike = v,
            new KeyChord(Key.A, KeyModifiers.Control | KeyModifiers.Shift),
            new KeyChord(Key.L, KeyModifiers.Shift)); // EOF: Precise select like
        MoveIfDefault(() => config.StampSection, v => config.StampSection = v,
            new KeyChord(Key.M, KeyModifiers.Shift),
            new KeyChord(Key.S, KeyModifiers.Shift)); // EOF: Place RS Section
        MoveIfDefault(() => config.SnapAll, v => config.SnapAll = v,
            new KeyChord(Key.R, KeyModifiers.Control | KeyModifiers.Alt),
            new KeyChord(Key.R, KeyModifiers.Alt)); // EOF: Resnap Auto
        MoveIfDefault(() => config.Toggle3DFingering, v => config.Toggle3DFingering = v,
            new KeyChord(Key.F, KeyModifiers.Control), new KeyChord(Key.F8)); // EOF: Fingering view
        MoveIfDefault(() => config.ToggleInspector, v => config.ToggleInspector = v,
            new KeyChord(Key.I, KeyModifiers.Control), new KeyChord(Key.F9)); // EOF: Properties

    }

    /// <summary>Wave 4 (v0.60): the third EOF alignment pass.</summary>
    private static void NormalizeKeybindsWave4(KeybindConfig config)
    {
        // v0.60: the third EOF alignment pass, same rule as the two above — an
        // equality test against the exact OLD default, so a user who rebound one
        // of these keeps their choice.
        //
        // Zoom onto the ALT row of the grid-snap keys (EOF_SRC/editor.c:2064 and
        // :2035). This also VACATES Ctrl+[ / Ctrl+], which EOF spends on
        // move-notes-by-one-grid-snap; DotCharter has no such verb, so they are
        // left unbound.
        MoveIfDefault(() => config.ZoomIn, v => config.ZoomIn = v,
            new KeyChord(Key.OemCloseBrackets, KeyModifiers.Control),
            new KeyChord(Key.OemPeriod, KeyModifiers.Alt)); // EOF: zoom in (ALT+.)
        MoveIfDefault(() => config.ZoomOut, v => config.ZoomOut = v,
            new KeyChord(Key.OemOpenBrackets, KeyModifiers.Control),
            new KeyChord(Key.OemComma, KeyModifiers.Alt)); // EOF: zoom out (ALT+,)

        // The beat verbs onto the insert/delete keys EOF names them for
        // (EOF_SRC/menu/beat.c:144-145). Ctrl+B / Ctrl+Shift+B are vacated;
        // EOF spends bare Ctrl+B on a drum cymbal toggle DotCharter has no
        // verb for, so they are left unbound.
        MoveIfDefault(() => config.InsertBeatAtPlayhead, v => config.InsertBeatAtPlayhead = v,
            new KeyChord(Key.B, KeyModifiers.Control),
            new KeyChord(Key.Insert, KeyModifiers.Control)); // EOF: Beat Add
        MoveIfDefault(() => config.RemoveBeatAtPlayhead, v => config.RemoveBeatAtPlayhead = v,
            new KeyChord(Key.B, KeyModifiers.Control | KeyModifiers.Shift),
            new KeyChord(Key.Delete, KeyModifiers.Control)); // EOF: Beat Delete

        // Halve-tempo onto EOF's chord (EOF_SRC/menu/beat.c:84), completing the
        // pair whose other half (DoubleTime, Ctrl+Shift+D, beat.c:77) already
        // matched. This is a SWAP, so the order matters: ToggleChordPassOtherNotes
        // is read for its OLD default (Ctrl+Shift+X) and moved to the chord
        // HalfTime just left, and both tests are equality tests against the old
        // default, so a user who rebound either keeps their choice and the swap
        // simply does not happen for them.
        MoveIfDefault(() => config.HalfTime, v => config.HalfTime = v,
            new KeyChord(Key.H, KeyModifiers.Control | KeyModifiers.Shift),
            new KeyChord(Key.X, KeyModifiers.Control | KeyModifiers.Shift)); // EOF: Halve tempo
        MoveIfDefault(() => config.ToggleChordPassOtherNotes, v => config.ToggleChordPassOtherNotes = v,
            new KeyChord(Key.X, KeyModifiers.Control | KeyModifiers.Shift),
            new KeyChord(Key.H, KeyModifiers.Control | KeyModifiers.Shift)); // displaced by HalfTime

        // The RS ignore flag onto the chord EOF spells for that exact status
        // (EOF_SRC/menu/note.c:722). Its group variant follows the letter but
        // NOT onto Ctrl+Shift+Alt+I: Ctrl+Alt is AltGr on the owner's German
        // layout. InsertVocalAtPlayhead is displaced to Ctrl+Shift+J, the one
        // letter EOF binds on no row at all.
        MoveIfDefault(() => config.ToggleIgnore, v => config.ToggleIgnore = v,
            new KeyChord(Key.G),
            new KeyChord(Key.I, KeyModifiers.Control | KeyModifiers.Shift)); // EOF: Ignore
        MoveIfDefault(() => config.ToggleIgnoreGroup, v => config.ToggleIgnoreGroup = v,
            new KeyChord(Key.G, KeyModifiers.Alt),
            new KeyChord(Key.I, KeyModifiers.Shift | KeyModifiers.Alt)); // follows ToggleIgnore
        MoveIfDefault(() => config.InsertVocalAtPlayhead, v => config.InsertVocalAtPlayhead = v,
            new KeyChord(Key.I, KeyModifiers.Control | KeyModifiers.Shift),
            new KeyChord(Key.J, KeyModifiers.Control | KeyModifiers.Shift)); // displaced by ToggleIgnore
    }

    /// <summary>Re-home a keybind ONLY if it still carries the old default —
    /// never overwrite a chord the user chose themselves.</summary>
    private static void MoveIfDefault(Func<KeyChord> read, Action<KeyChord> write,
        KeyChord oldDefault, KeyChord newDefault)
    {
        if (read() == oldDefault) write(newDefault);
    }

    public void SaveKeybinds(KeybindConfig config)
    {
        var path = KeybindsPath;
        lock (_keybindsSync)
        {
            if (_keybindsDegraded)
            {
                // Never write over a file we failed to READ: what we hold is the
                // factory defaults plus this session's edits, and the file is
                // every bind the user ever set. The next load retries the file.
                LogTrace($"Keybinds '{path}' were not readable; skipping the write so the existing " +
                         "binds survive. This change was not persisted.");
                return;
            }
        }

        try
        {
            var json = JsonSerializer.Serialize(config, _jsonOptions);
            AtomicFile.WriteAllText(path, json);
            LogTrace($"Saved keybinds to '{path}' ({json.Length} bytes).");
        }
        catch (Exception ex)
        {
            ReportError($"Failed to save keybinds to '{path}': {ex.Message}", ex);
        }
    }

    private void LogTrace(string message)
    {
        IAppLogger? logger;
        lock (_loggerSync)
        {
            logger = _logger;
        }

        logger?.Trace(AppLogCategory.Settings, message);
    }

    private enum ReportLevel
    {
        Error,
        Info,
        Warning
    }

    private static void Emit(IAppLogger logger, string message, Exception? ex, ReportLevel level)
    {
        switch (level)
        {
            case ReportLevel.Info:
                logger.Info(AppLogCategory.Settings, message);
                break;
            case ReportLevel.Warning:
                logger.Warning(AppLogCategory.Settings, message);
                break;
            default:
                logger.Error(AppLogCategory.Settings, message, ex);
                break;
        }
    }

    private void ReportError(string message, Exception? ex) => Report(message, ex, ReportLevel.Error);

    private void ReportInfo(string message) => Report(message, null, ReportLevel.Info);

    private void ReportWarning(string message) => Report(message, null, ReportLevel.Warning);

    private void Report(string message, Exception? ex, ReportLevel level)
    {
        IAppLogger? logger;
        lock (_loggerSync)
        {
            logger = _logger;
            if (logger == null) _pendingErrors.Add((message, ex, level));
        }

        if (logger != null)
        {
            Emit(logger, message, ex, level);
        }
        else
        {
            Debug.WriteLine(
                $"[{nameof(JsonConfigurationService)}] {message}{(ex == null ? string.Empty : $" ({ex})")}");
        }
    }
}
