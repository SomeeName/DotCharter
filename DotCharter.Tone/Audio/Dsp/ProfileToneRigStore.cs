using System.Globalization;
using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.Presets;

namespace DotCharter.Tone.Audio.Dsp;

/// <summary>A cloned seat, its read authority, publication state and owning profile generation.</summary>
public readonly record struct ProfileToneRigRead(UserTonePresetDto? Rig, bool IsAuthoritative, bool IsPersisted, long Generation);

/// <summary>
///     Where a player's live tone rig lives: one <c>tone-rig.json</c> per PLAYER
///     PROFILE, holding that profile's board for every SEAT.
///     <para>
///         <b>Profile and seat are different axes and this is where they meet.</b>
///         A SEAT (ABI v28, <see cref="IPlayerDspChains" />) is a native DSP chain
///         — which guitar input is being processed. A PROFILE is whose settings
///         those are. The app has exactly one active profile at a time, even in
///         couch co-op, so the composition is: <b>the active profile owns a rig
///         per seat</b>, keyed here by the zero-based player index ("0", "1", …).
///         Switching profile brings that profile's whole set of seat rigs; it
///         never renumbers, merges or shares seats.
///     </para>
///     <para>
///         <b>Why a file in the profile directory rather than more
///         <c>AppConfig</c> fields.</b> This is the mechanism every other
///         piece of per-profile state already uses —
///         <c>PracticeProfileService</c>, the replay and take stores all keep a JSON
///         file under <c>profiles/&lt;id&gt;/</c>. Living inside that tree means
///         the existing directory rules already EXCLUDE it from LAN update
///         packages and INCLUDE it in whole-install backups, so no allow-list
///         changes, and deleting a profile takes its rigs with it for free.
///     </para>
///     <para>
///         <b>Scope is PINNED, not resolved per call, and that is load-bearing.</b>
///         <c>IPlayerProfileService.ActiveProfileChanged</c> is raised
///         AFTER the switch has happened, so a store that resolved the active
///         profile on every access would write the OUTGOING player's last few
///         seconds of knob-twiddling into the INCOMING player's file — the exact
///         cross-profile leak this whole feature exists to prevent.
///         <c>ProfileToneRigCoordinator</c> (DotCharter.Services) therefore flushes first and
///         calls <see cref="Rescope" /> second.
///     </para>
///     Throw-free throughout: this is read from the audio engine's attach path.
/// </summary>
public interface IProfileToneRigStore
{
    /// <summary>The profile whose rigs this store is currently reading and
    /// writing. Resolved from the active profile on first use and thereafter only
    /// by <see cref="Rescope" />.</summary>
    Guid ScopedProfileId { get; }

    /// <summary>This profile's stored rig for one seat, or null when the
    /// seat has none — which is deliberately NOT "inherit seat 0" and NOT
    /// "inherit the previous profile".</summary>
    UserTonePresetDto? GetRig(int player);

    /// <summary>Persists one seat's rig; false means the caller must retry.</summary>
    bool SetRig(int player, UserTonePresetDto rig);

    ProfileToneRigRead ReadRig(int player) => new(GetRig(player), true, true, ScopeGeneration);

    bool SetRig(int player, UserTonePresetDto rig, long generation)
        => generation == ScopeGeneration && SetRig(player, rig);

    /// <summary>The last full rig preserved when a supported replacement superseded an incomplete restore.</summary>
    UserTonePresetDto? GetRecoveryRig(int player) => null;

    /// <summary>Publishes the replacement and its recovery copy atomically; unsupported stores refuse recovery writes.</summary>
    bool SetRigWithRecovery(int player, UserTonePresetDto rig, UserTonePresetDto? recovery, long generation)
        => recovery is null && SetRig(player, rig, generation);

    void FlushPending() { }

    /// <summary>Monotonic scope identity, including switches away and back.</summary>
    long ScopeGeneration { get; }

    /// <summary>Repoints at the now-active profile and drops the cache. Returns
    /// true when the scope actually changed.</summary>
    bool Rescope();

    /// <summary>Drops the in-memory copy so the next read re-loads from disk
    /// (a backup import replaced the file behind the service).</summary>
    void InvalidateCache();
}

/// <summary>
///     JSON-file-backed <see cref="IProfileToneRigStore" />. See the interface for
///     the profile/seat composition and the pinned-scope rule.
/// </summary>
public sealed class ProfileToneRigStore : IProfileToneRigStore
{
    public const string StoreFileName = "tone-rig.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly IToneRigProfileScope? _playerProfiles;
    private readonly string? _fixedDirectory;
    private readonly IRigConfigService? _legacyConfig;
    private readonly IAppLogger? _logger;

    private Guid? _scopedProfileId;
    private StoreFile? _store;
    private string? _loadedPath;
    private bool _loadFailed;
    private readonly Dictionary<string, StoreFile> _pending = new(StringComparer.Ordinal);
    private long _scopeGeneration;

    /// <summary>Player-profile-scoped store (production). The pre-0.45 global rig
    /// in <paramref name="legacyConfig" /> is adopted once — see
    /// <see cref="AdoptLegacyGlobalRigLocked" />; a null config disables the
    /// migration entirely.</summary>
    public ProfileToneRigStore(
        IToneRigProfileScope playerProfiles, IRigConfigService? legacyConfig = null,
        IAppLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(playerProfiles);
        _playerProfiles = playerProfiles;
        _legacyConfig = legacyConfig;
        _logger = logger;
    }

    /// <summary>Fixed-directory store without player-profile scoping (tests,
    /// tools).</summary>
    public ProfileToneRigStore(string directory, IRigConfigService? legacyConfig = null,
        IAppLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _fixedDirectory = directory;
        _legacyConfig = legacyConfig;
        _logger = logger;
    }

    public Guid ScopedProfileId
    {
        get
        {
            lock (_gate) return ScopedProfileIdLocked();
        }
    }

    public UserTonePresetDto? GetRig(int player) => ReadRig(player).Rig;

    public ProfileToneRigRead ReadRig(int player)
    {
        lock (_gate)
        {
            var store = LoadLocked();
            var rig = player >= 0 && store.Rigs.TryGetValue(Key(player), out var dto) ? dto?.Clone() : null;
            return new ProfileToneRigRead(rig, !_loadFailed, !_pending.ContainsKey(ResolveStorePathLocked()), _scopeGeneration);
        }
    }

    public bool SetRig(int player, UserTonePresetDto rig) => SetRig(player, rig, ScopeGeneration);

    public bool SetRig(int player, UserTonePresetDto rig, long generation)
        => SetRigWithRecovery(player, rig, null, generation);

    public UserTonePresetDto? GetRecoveryRig(int player)
    {
        lock (_gate)
            return LoadLocked().RecoveryRigs.TryGetValue(Key(player), out var rig) ? rig?.Clone() : null;
    }

    public bool SetRigWithRecovery(int player, UserTonePresetDto rig, UserTonePresetDto? recovery, long generation)
    {
        if (player < 0 || rig is null) return false;
        lock (_gate)
        {
            if (generation != _scopeGeneration) return false;
            var store = LoadLocked();
            if (_loadFailed) return false;
            store.Rigs[Key(player)] = rig.Clone();
            if (recovery is not null) store.RecoveryRigs[Key(player)] = recovery.Clone();
            store.Revision++;
            return SaveLocked(store);
        }
    }

    public void FlushPending()
    {
        lock (_gate)
        {
            foreach (var (path, store) in _pending.ToArray()) SaveLocked(store, path);
        }
    }

    public long ScopeGeneration => Interlocked.Read(ref _scopeGeneration);

    public bool Rescope()
    {
        lock (_gate)
        {
            if (_playerProfiles is null) return false;
            var active = ActiveProfileIdLocked();
            if (_scopedProfileId == active) return false;
            _scopedProfileId = active;
            Interlocked.Increment(ref _scopeGeneration);
            _store = null;
            _loadedPath = null;
            return true;
        }
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _store = null;
            _loadedPath = null;
        }
    }

    private static string Key(int player) => player.ToString(CultureInfo.InvariantCulture);

    private Guid ScopedProfileIdLocked()
    {
        if (_playerProfiles is null) return Guid.Empty;
        return _scopedProfileId ??= ActiveProfileIdLocked();
    }

    private Guid ActiveProfileIdLocked()
    {
        try
        {
            return _playerProfiles!.ActiveProfileId;
        }
        catch (Exception ex)
        {
            // The registry is the profile service's problem, not the tone rig's:
            // report and fall back to an empty scope, which resolves to no stored
            // rig and therefore to the first-run default preset.
            _logger?.Warning(AppLogCategory.Audio,
                "Tone rig store could not resolve the active profile", ex);
            return Guid.Empty;
        }
    }

    private string ResolveStorePathLocked()
    {
        if (_fixedDirectory is not null) return Path.Combine(_fixedDirectory, StoreFileName);
        var id = ScopedProfileIdLocked();
        return Path.Combine(_playerProfiles!.GetDataDirectory(id), StoreFileName);
    }

    private StoreFile LoadLocked()
    {
        var path = ResolveStorePathLocked();
        if (_store != null && path == _loadedPath) return _store;

        _store = null;
        _loadFailed = false;
        if (_pending.TryGetValue(path, out var pending))
        {
            _loadedPath = path;
            return _store = pending;
        }
        var absent = false;
        try
        {
            var published = ReadCandidate(path);
            var journal = ReadCandidate(path + ".pending");
            var useJournal = journal is not null && (published is null || journal.Revision > published.Revision);
            _store = useJournal ? journal : published;
            if (useJournal) _pending[path] = journal!;
            absent = _store is null;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _logger?.Warning(AppLogCategory.Audio, "Tone rig store unreadable; refusing to replace it", ex);
        }
        if (_loadFailed)
        {
            _store = null;
            return new StoreFile();
        }
        _loadedPath = path;
        _store ??= new StoreFile();
        if (absent) AdoptLegacyGlobalRigLocked(_store, path);
        return _store;
    }

    private StoreFile? ReadCandidate(string path)
    {
        if (Directory.Exists(path)) return null;
        try
        {
            var candidate = JsonSerializer.Deserialize<StoreFile>(AtomicFile.ReadAllText(path), JsonOptions);
            if (candidate?.Rigs is null || candidate.RecoveryRigs is null || candidate.Revision < 0 || candidate.Revision >= long.MaxValue - 1)
                throw new JsonException("Invalid rig map or revision.");
            try
            {
                foreach (var rig in candidate.Rigs.Values.Concat(candidate.RecoveryRigs.Values)) _ = rig?.Clone();
            }
            catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException)
            {
                throw new JsonException("Invalid rig contents.", ex);
            }
            return candidate;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException ex)
        {
            AtomicFile.MoveAside(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
            _logger?.Warning(AppLogCategory.Audio, "Corrupt tone rig store preserved", ex);
            return null;
        }
    }

    /// <summary>
    ///     The upgrade path for a player who already had a rig before rigs were
    ///     per-profile: <c>RigConfig.ActiveToneRig</c> (seat 0) and
    ///     <c>ActiveToneRigsByPlayer</c> (seats 1..N) are MOVED into the profile
    ///     that is active the first time this store resolves, then cleared.
    ///     <para>
    ///         <b>The active profile, not the default one</b> — which is where this
    ///         deliberately diverges from <c>PracticeProfileService</c>'s
    ///         pre-profile migration. That one had a file with no owner, so the
    ///         DEFAULT profile was the only deterministic home for it. Here the
    ///         global rig demonstrably belonged to whoever was last playing, and on
    ///         a machine whose active profile is not the default, adopting into the
    ///         default would greet that player with the factory tone and leave
    ///         their board in someone else's folder.
    ///     </para>
    ///     <para>
    ///         Clearing the config is what makes it one-shot, and it is done only
    ///         after the profile file has been written — a failed write leaves the
    ///         global rig exactly where it was, so the next launch retries. A
    ///         DEGRADED session (the profile registry could not be read, so every
    ///         profile is standing in as one fixed id) never adopts: moving the rig
    ///         into a stand-in folder would strand it somewhere no real profile
    ///         ever reads.
    ///     </para>
    /// </summary>
    private void AdoptLegacyGlobalRigLocked(StoreFile store, string path)
    {
        if (_legacyConfig is null) return;
        if (_playerProfiles is not null && _playerProfiles.IsDegradedProfile(ScopedProfileIdLocked())) return;

        RigConfig config;
        try
        {
            config = _legacyConfig.LoadRigConfig();
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "Tone rig migration skipped", ex);
            return;
        }

        var adopted = 0;
        if (config.ActiveToneRig is { } seat0)
        {
            store.Rigs[Key(0)] = seat0.Clone();
            adopted++;
        }

        foreach (var (key, dto) in config.ActiveToneRigsByPlayer)
        {
            if (dto is null) continue;
            // Only the seat keys this store understands; anything else was never
            // readable as a seat and is dropped rather than carried forward.
            if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var player) ||
                player < 0)
            {
                continue;
            }

            store.Rigs[Key(player)] = dto.Clone();
            adopted++;
        }

        if (adopted == 0) return;

        if (!SaveLocked(store)) return;

        try
        {
            _legacyConfig.UpdateRigConfig(c =>
            {
                c.ActiveToneRig = null;
                c.ActiveToneRigsByPlayer.Clear();
            });
            _logger?.Info(AppLogCategory.Audio,
                $"Adopted the pre-0.45 global tone rig ({adopted} seat(s)) into profile {ScopedProfileIdLocked():N}.");
        }
        catch (Exception ex)
        {
            // The rigs are already safe in the profile file; a config that could
            // not be cleared only means the next launch re-adopts the same bytes
            // over the same file, which is idempotent.
            _logger?.Warning(AppLogCategory.Audio,
                "Tone rig migration wrote the profile store but could not clear the global rig", ex);
        }
    }

    private bool SaveLocked(StoreFile store) => SaveLocked(store, ResolveStorePathLocked());

    private bool SaveLocked(StoreFile store, string path)
    {
        _pending[path] = store;
        string? json = null;
        try
        {
            json = JsonSerializer.Serialize(store, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, json);
            _pending.Remove(path);
            try { File.Delete(path + ".pending"); }
            catch (Exception ex) { _logger?.Warning(AppLogCategory.Audio, "Published rig journal could not be removed", ex); }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "Tone rig store not writable; retaining pending rig", ex);
            try { if (json is not null) AtomicFile.WriteAllText(path + ".pending", json); }
            catch (Exception journalError) { _logger?.Warning(AppLogCategory.Audio, "Pending rig journal could not be written", journalError); }
            return false;
        }
    }

    /// <summary>On-disk shape: seat index (as a string, so the JSON stays an
    /// object) to that seat's rig, in the SAME persistence shape as a user tone
    /// preset — dropping the string params would persist a modelless namamp, i.e.
    /// a silent amp.</summary>
    internal sealed class StoreFile
    {
        public long Revision { get; set; }

        public Dictionary<string, UserTonePresetDto?> Rigs { get; set; } = new(StringComparer.Ordinal);

        public Dictionary<string, UserTonePresetDto?> RecoveryRigs { get; set; } = new(StringComparer.Ordinal);
    }
}
