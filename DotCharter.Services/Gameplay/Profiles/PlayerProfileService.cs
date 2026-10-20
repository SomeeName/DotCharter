using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

using DotCharter.Localization;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Services.Gameplay.Profiles;

/// <summary>The local profile registry. Extends <see cref="IToneRigProfileScope" />
/// (DotCharter.Tone) so the per-profile tone-rig store reads it directly.</summary>
public interface IPlayerProfileService : IToneRigProfileScope
{
    /// <summary>All local profiles, creation order. Never empty.</summary>
    IReadOnlyList<PlayerProfile> Profiles { get; }

    /// <summary>The profile all gameplay reads/writes are scoped to right now.</summary>
    PlayerProfile ActiveProfile { get; }

    // There is deliberately no `ActiveDataDirectory` convenience here: every caller in the
    // app resolves a directory for a SPECIFIC profile id (the practice store, the
    // goals store, the plan store, the leaderboard's cross-profile read), so it
    // would be driven by nothing. `GetDataDirectory(ActiveProfile.Id)`
    // is the one-liner.

    /// <summary>Raised after the active profile changed (switch, or delete of the active one).</summary>
    event Action<PlayerProfile>? ActiveProfileChanged;

    /// <summary>Creates a profile and makes it available for switching. Name must be non-blank.</summary>
    PlayerProfile Create(string name, string? accentColor = null);

    void Rename(Guid id, string newName);

    /// <summary>Sets the profile's left-handed flag (v0.34). When it is the
    /// ACTIVE profile, <see cref="ActiveProfileChanged"/> is raised so the
    /// render-option mirror re-stages live.</summary>
    void SetLeftyFlip(Guid id, bool lefty);

    /// <summary>Stores the player's own tuning (a <c>TunerTunings</c> id, empty = E
    /// standard) and the tuning-match opt-in. Raises nothing: Play Mode's
    /// pre-play card is the only reader and reads it on demand.</summary>
    void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning);

    /// <summary>Switches the active profile. Returns false for an unknown id.</summary>
    bool SwitchTo(Guid id);

    /// <summary>
    ///     Deletes a profile AND its gameplay data directory. Refused (false)
    ///     for the last remaining profile. Deleting the active profile
    ///     switches to another one first.
    /// </summary>
    bool Delete(Guid id);

    // GetDataDirectory(Guid) is inherited from IToneRigProfileScope: the data
    // directory for one profile, created on demand.

    Guid IToneRigProfileScope.ActiveProfileId => ActiveProfile.Id;

    bool IToneRigProfileScope.IsDegradedProfile(Guid id) => id == PlayerProfileService.DegradedProfileId;

    /// <summary>
    ///     Copies an image into the profile's data directory (as
    ///     <c>avatar&lt;ext&gt;</c>) and records it in the registry. Returns
    ///     false for an unknown profile or unreadable source file.
    /// </summary>
    bool SetAvatar(Guid id, string sourceImagePath);

    /// <summary>Full path of the profile's avatar image, or null when unset/missing.</summary>
    string? GetAvatarPath(Guid id);

    /// <summary>
    ///     Writes a portable snapshot of one profile: its registry entry as
    ///     <c>profile.json</c> plus the entire data directory (under
    ///     <c>data/</c>) into a zip at <paramref name="zipPath"/>.
    /// </summary>
    bool ExportProfile(Guid id, string zipPath);

    /// <summary>
    ///     Restores a profile exported via <see cref="ExportProfile"/> as a
    ///     NEW profile (fresh id; name suffixed when already taken). The data
    ///     directory — avatar included — is restored alongside. Returns null
    ///     when the archive is unreadable or not a profile export.
    /// </summary>
    PlayerProfile? ImportProfile(string zipPath);

    /// <summary>
    ///     Drops the in-memory registry so the next read re-loads
    ///     <c>player-profiles.json</c>. Required after a backup import replaces
    ///     the file behind the service.
    /// </summary>
    void InvalidateCache();
}

/// <summary>
///     JSON-file-backed local profile registry (<c>player-profiles.json</c> in
///     the writable config root). Per-profile gameplay data lives in
///     <c>profiles/&lt;id&gt;/</c> next to the registry so one root override
///     redirects everything (tests, portable installs).
/// </summary>
public sealed class PlayerProfileService : IPlayerProfileService
{
    internal static readonly string[] AccentPalette =
    [
        "#4F8EF7", "#E5484D", "#46A758", "#F5A524", "#8E4EC6", "#0FB5BA"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    /// <summary>
    ///     Id of the stand-in profile served while the registry file could not be
    ///     READ (see <see cref="LoadLocked" />). It is a CONSTANT on purpose: the
    ///     practice store, the goals store and the practice plan all resolve their
    ///     file from <see cref="ActiveProfile" />'s data directory, so a fresh
    ///     random id every call would scatter one degraded session's writes across
    ///     a new folder each time. With a fixed id the whole session lands in one
    ///     recognisable <c>profiles/00000000…0dc0/</c> folder that a human can find
    ///     and merge, and no real profile can ever collide with it.
    /// </summary>
    public static readonly Guid DegradedProfileId = new("00000000-0000-0000-0000-000000000dc0");

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _defaultProfileName;
    private readonly Func<string> _localizedDefaultName;
    private readonly IAppLogger? _logger;
    private Store? _store;

    /// <summary>
    ///     Non-null while the registry file EXISTS but could not be read this
    ///     session. It is never cached in <see cref="_store" /> (so every call
    ///     retries the read and a transient lock heals itself) and
    ///     <see cref="SaveLocked" /> refuses to write while it is set — what is in
    ///     memory is not the player's registry, and writing it would replace every
    ///     profile with one fresh stand-in.
    /// </summary>
    private Store? _degraded;

    /// <param name="localization">Says "Player 1" for a profile without a name, at display time.</param>
    /// <param name="logger">Optional app logger.</param>
    /// <param name="filePath">The registry; the app's config folder by default.</param>
    /// <param name="defaultProfileName">A stored default name to clear (the "Player 1" of the
    /// language the app first ran in); the localized one by default.</param>
    public PlayerProfileService(ILocalizationService localization, IAppLogger? logger = null, string? filePath = null,
        string? defaultProfileName = null)
    {
        _logger = logger;
        _filePath = filePath ?? Path.Combine(WritablePaths.ConfigRoot, "player-profiles.json");
        defaultProfileName ??= localization["Profile_DefaultName"];
        _defaultProfileName = string.IsNullOrWhiteSpace(defaultProfileName)
            ? PlayerProfile.FallbackDefaultName
            : defaultProfileName.Trim();
        _localizedDefaultName = () => localization["Profile_DefaultName"];
    }

    /// <summary>Gives <paramref name="profile" /> this service's way of saying "Player 1".</summary>
    private PlayerProfile Named(PlayerProfile profile)
    {
        profile.DefaultName = _localizedDefaultName;
        return profile;
    }

    private Store Named(Store store)
    {
        foreach (var profile in store.Profiles) Named(profile);
        return store;
    }

    public event Action<PlayerProfile>? ActiveProfileChanged;

    public IReadOnlyList<PlayerProfile> Profiles
    {
        get
        {
            lock (_gate) return LoadLocked().Profiles.ToArray();
        }
    }

    public void InvalidateCache()
    {
        lock (_gate)
        {
            _store = null;
            _degraded = null;
        }
    }

    /// <summary>
    ///     True while the registry file exists but could not be read, so the
    ///     service is serving the stand-in profile and refusing every write. The
    ///     status bar surfaces it; nothing in the service branches on it.
    /// </summary>
    public bool IsDegraded
    {
        get
        {
            lock (_gate)
            {
                LoadLocked();
                return _degraded != null;
            }
        }
    }

    public PlayerProfile ActiveProfile
    {
        get
        {
            lock (_gate) return ActiveLocked(LoadLocked());
        }
    }

    public PlayerProfile Create(string name, string? accentColor = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            // ReSharper disable once LocalizableElement -- developer-facing argument validation.
            throw new ArgumentException("Profile name must not be blank.", nameof(name));

        lock (_gate)
        {
            var store = LoadLocked();
            var profile = new PlayerProfile
            {
                Name = name.Trim(),
                AccentColor = accentColor ?? AccentPalette[store.Profiles.Count % AccentPalette.Length],
                CreatedUtc = DateTime.UtcNow,
                LastUsedUtc = DateTime.UtcNow
            };
            store.Profiles.Add(Named(profile));
            SaveLocked(store);
            return profile;
        }
    }

    public void Rename(Guid id, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        lock (_gate)
        {
            var store = LoadLocked();
            var profile = store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return;
            profile.Name = newName.Trim();
            SaveLocked(store);
        }
    }

    public void SetLeftyFlip(Guid id, bool lefty)
    {
        PlayerProfile? changedActive = null;
        lock (_gate)
        {
            var store = LoadLocked();
            var profile = store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null || profile.LeftyFlip == lefty) return;
            profile.LeftyFlip = lefty;
            SaveLocked(store);
            if (store.ActiveProfileId == id) changedActive = profile;
        }

        // Outside the lock (event handlers re-enter the service): the render
        // mirror re-stages off the same event a profile switch uses.
        if (changedActive != null) ActiveProfileChanged?.Invoke(changedActive);
    }

    public void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning)
    {
        lock (_gate)
        {
            var store = LoadLocked();
            var profile = store.Profiles.FirstOrDefault(p => p.Id == id);
            var unchanged = profile?.MyTuningId == myTuningId && profile.PlayInMyTuning == playInMyTuning;
            if (profile == null || unchanged) return;
            profile.MyTuningId = myTuningId;
            profile.PlayInMyTuning = playInMyTuning;
            SaveLocked(store);
        }
    }

    public bool SwitchTo(Guid id)
    {
        PlayerProfile switched;
        lock (_gate)
        {
            var store = LoadLocked();
            var profile = store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return false;
            if (store.ActiveProfileId == id) return true;

            store.ActiveProfileId = id;
            profile.LastUsedUtc = DateTime.UtcNow;
            SaveLocked(store);
            switched = profile;
        }

        ActiveProfileChanged?.Invoke(switched);
        return true;
    }

    public bool Delete(Guid id)
    {
        PlayerProfile? newActive = null;
        string dataDir;
        lock (_gate)
        {
            var store = LoadLocked();
            var profile = store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null || store.Profiles.Count <= 1) return false;

            store.Profiles.Remove(profile);
            if (store.ActiveProfileId == id)
            {
                newActive = store.Profiles[0];
                store.ActiveProfileId = newActive.Id;
                newActive.LastUsedUtc = DateTime.UtcNow;
            }

            SaveLocked(store);
            dataDir = DataDirectoryLocked(id);
        }

        try
        {
            if (Directory.Exists(dataDir)) Directory.Delete(dataDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"Profile data directory not fully deleted ({dataDir}): {ex.Message}");
        }

        if (newActive != null) ActiveProfileChanged?.Invoke(newActive);
        return true;
    }

    public string GetDataDirectory(Guid id)
    {
        string dir;
        lock (_gate) dir = DataDirectoryLocked(id);
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application, $"Profile data directory not creatable ({dir}): {ex.Message}");
        }

        return dir;
    }

    public bool SetAvatar(Guid id, string sourceImagePath)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath)) return false;

        lock (_gate)
        {
            var store = LoadLocked();
            var profile = store.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null) return false;

            var dataDir = DataDirectoryLocked(id);
            var ext = Path.GetExtension(sourceImagePath).ToLowerInvariant();
            var fileName = "avatar" + (string.IsNullOrEmpty(ext) ? ".png" : ext);
            try
            {
                Directory.CreateDirectory(dataDir);
                File.Copy(sourceImagePath, Path.Combine(dataDir, fileName), overwrite: true);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Avatar not copied ({sourceImagePath}): {ex.Message}");
                return false;
            }

            // A previous avatar with a different extension would otherwise
            // linger in the data dir (and win a stale re-read); drop it.
            if (!string.IsNullOrEmpty(profile.AvatarFileName) && profile.AvatarFileName != fileName)
            {
                try
                {
                    var stale = Path.Combine(dataDir, profile.AvatarFileName);
                    if (File.Exists(stale)) File.Delete(stale);
                }
                catch
                {
                    // best effort cleanup
                }
            }

            profile.AvatarFileName = fileName;
            SaveLocked(store);
            return true;
        }
    }

    public string? GetAvatarPath(Guid id)
    {
        lock (_gate)
        {
            var profile = LoadLocked().Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null || string.IsNullOrEmpty(profile.AvatarFileName)) return null;
            var path = Path.Combine(DataDirectoryLocked(id), profile.AvatarFileName);
            return File.Exists(path) ? path : null;
        }
    }

    public bool ExportProfile(Guid id, string zipPath)
    {
        lock (_gate)
        {
            var profile = LoadLocked().Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null || string.IsNullOrWhiteSpace(zipPath)) return false;

            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(zipPath));
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (File.Exists(zipPath)) File.Delete(zipPath);

                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                using (var writer = new StreamWriter(zip.CreateEntry("profile.json").Open()))
                    writer.Write(JsonSerializer.Serialize(profile, JsonOptions));

                var dataDir = DataDirectoryLocked(id);
                if (Directory.Exists(dataDir))
                {
                    foreach (var file in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
                    {
                        zip.CreateEntryFromFile(file,
                            "data/" + Path.GetRelativePath(dataDir, file).Replace('\\', '/'));
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Application, $"Profile export failed ({zipPath}): {ex.Message}");
                try
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                }
                catch
                {
                    // best effort cleanup of the partial archive
                }

                return false;
            }
        }
    }

    /// <summary>
    ///     Expansion ceiling for an imported profile bundle. A profile export is
    ///     a few stats JSONs plus one downscaled avatar, so this is already
    ///     orders of magnitude over anything real. It matters because the
    ///     bundle is a file the user received from somewhere, and a size cap on
    ///     the COMPRESSED bytes is precisely the ratio a zip bomb exploits.
    /// </summary>
    public const long MaxImportExpansionBytes = 16L * 1024 * 1024;

    public PlayerProfile? ImportProfile(string zipPath)
    {
        lock (_gate)
        {
            // A degraded session cannot register anything (SaveLocked refuses),
            // so extracting the bundle would only leave an orphan data folder no
            // registry names. Refuse BEFORE writing rather than after.
            LoadLocked();
            if (_degraded != null)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Profile import refused ({zipPath}): the profile registry is not readable this session.");
                return null;
            }

            try
            {
                // Bound the member count before opening: an archive with
                // millions of tiny members costs one live object each, which
                // would be spent before any per-entry guard could run.
                if (BoundedZipExtractor.CheckDeclaredEntryCount(zipPath) != ZipExtractionRefusal.None)
                {
                    _logger?.Warning(AppLogCategory.Application,
                        $"Profile import refused ({zipPath}): implausible archive member count.");
                    return null;
                }

                using var zip = ZipFile.OpenRead(zipPath);
                var profileEntry = zip.GetEntry("profile.json");
                if (profileEntry == null) return null;

                PlayerProfile? imported;
                using (var reader = new StreamReader(profileEntry.Open()))
                    imported = JsonSerializer.Deserialize<PlayerProfile>(reader.ReadToEnd(), JsonOptions);
                if (imported == null) return null;
                Named(imported);

                var store = LoadLocked();
                imported.Id = Guid.NewGuid(); // never collide with an existing profile
                // An exported DEFAULT profile has no custom name; here it is one profile of
                // several, so it takes its display name as a real one.
                imported.Name = UniqueName(store, imported.DisplayName.Trim());
                imported.IsDefault = false;
                imported.LastUsedUtc = DateTime.UtcNow;

                var dataDir = DataDirectoryLocked(imported.Id);

                // The traversal guard was already right; what was missing was a
                // ceiling on how much the archive may EXPAND. The shared
                // extractor keeps both, and counts the bytes it really inflates
                // rather than the ones the archive declares.
                var extraction = BoundedZipExtractor.ExtractBounded(zip, dataDir, MaxImportExpansionBytes,
                    selectRelativePath: e => e.FullName.StartsWith("data/", StringComparison.Ordinal)
                        ? e.FullName["data/".Length..]
                        : null);
                if (!extraction.Succeeded)
                {
                    _logger?.Warning(AppLogCategory.Application,
                        $"Profile import refused ({zipPath}): {extraction.Refusal} after " +
                        $"{extraction.BytesWritten} bytes. The half-written data folder is discarded.");
                    BestEffortDelete.Directory(dataDir);
                    return null;
                }

                // Avatar referenced but not shipped in the archive: drop the
                // reference so the UI falls back to the accent dot cleanly.
                if (!string.IsNullOrEmpty(imported.AvatarFileName) &&
                    !File.Exists(Path.Combine(dataDir, imported.AvatarFileName)))
                {
                    imported.AvatarFileName = "";
                }

                store.Profiles.Add(imported);
                SaveLocked(store);
                return imported;
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Application, $"Profile import failed ({zipPath}): {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    ///     The default profile stored the "Player 1" of whatever language the app first ran in,
    ///     so a German first run showed "Spieler 1" in an English UI. A default profile still
    ///     carrying one of those names has no custom name; it renders localized from now on.
    /// </summary>
    private bool ClearDefaultNames(Store store)
    {
        var changed = false;
        foreach (var profile in store.Profiles.Where(p => p.IsDefault && p.HasCustomName))
        {
            if (!KnownDefaultNames.Contains(profile.Name.Trim()) &&
                !string.Equals(profile.Name.Trim(), _defaultProfileName, StringComparison.Ordinal))
            {
                continue;
            }

            profile.Name = "";
            changed = true;
        }

        return changed;
    }

    /// <summary>Every language's default profile name, as older builds stored it.</summary>
    private static readonly HashSet<string> KnownDefaultNames = new(StringComparer.Ordinal)
    {
        "Player 1", "Spieler 1", "プレイヤー1"
    };

    private static string UniqueName(Store store, string baseName)
    {
        // By DISPLAY name: the default profile's stored name is empty and reads "Player 1".
        bool Taken(string candidate) =>
            store.Profiles.Any(p => string.Equals(p.DisplayName, candidate, StringComparison.OrdinalIgnoreCase));

        if (!Taken(baseName)) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!Taken(candidate)) return candidate;
        }
    }

    private string DataDirectoryLocked(Guid id)
        => Path.Combine(Path.GetDirectoryName(_filePath)!, "profiles", id.ToString("N"));

    private static PlayerProfile ActiveLocked(Store store)
        => store.Profiles.FirstOrDefault(p => p.Id == store.ActiveProfileId) ?? store.Profiles[0];

    /// <summary>
    ///     Loads (and caches) the registry. Three outcomes, and telling them apart
    ///     is the whole point — the same rule
    ///     <see cref="Audio.LivePlay.PracticeProfileService" /> already follows for
    ///     the scores it stores: ABSENT is a new install, CORRUPT is content that
    ///     is not a registry, and UNREADABLE is a file presumed intact that could
    ///     not be opened right now.
    ///     <para>
    ///         Treating all three the same — swallow, start fresh, and
    ///         then <see cref="SaveLocked" /> the fresh store straight over the
    ///         file — would REPLACE a registry momentarily locked by an antivirus scan, a cloud
    ///         sync or a backup tool, on the first property
    ///         read, by a single new "Player 1" with a brand-new id. Nothing would be
    ///         deleted, but everything would be orphaned: per-profile data lives in
    ///         <c>profiles/&lt;id&gt;/</c>, and the registry is the only map from
    ///         those ids to a name — so every profile's scores, best runs, session
    ///         history, goals and practice plan would go invisible at once, and the
    ///         file that could have identified the folders would be gone.
    ///     </para>
    /// </summary>
    private Store LoadLocked()
    {
        if (_store != null) return _store;

        Store? loaded = null;
        var unreadable = false;
        try
        {
            if (File.Exists(_filePath))
                loaded = JsonSerializer.Deserialize<Store>(AtomicFile.ReadAllText(_filePath), JsonOptions);
        }
        catch (JsonException ex)
        {
            // CORRUPT: the bytes are readable and are not a registry. Starting
            // fresh is the only way forward, but this file is the only map from
            // the profile folders on disk to their names, so it is moved ASIDE
            // rather than overwritten — and if it cannot be moved, we do not write.
            unreadable = !TryQuarantineCorruptRegistry(ex);
        }
        catch (Exception ex)
        {
            // COULD NOT READ RIGHT NOW (IOException, UnauthorizedAccessException,
            // …). The file is presumed intact; the empty registry must never
            // become authoritative.
            unreadable = true;
            _logger?.Warning(AppLogCategory.Application,
                $"Profile registry could not be read ({ex.GetType().Name}: {ex.Message}); running on a " +
                "stand-in profile this session and NOT writing over it.");
        }

        // Deliberately NOT cached in _store: leaving it null makes the next call
        // retry the read, so a transient lock heals itself mid-session.
        if (unreadable) return _degraded ??= Named(DegradedStore());

        _degraded = null;
        _store = Named(loaded ?? new Store());
        if (_store.Profiles.Count == 0)
        {
            var fallback = new PlayerProfile
            {
                Name = "",
                AccentColor = AccentPalette[0],
                IsDefault = true,
                CreatedUtc = DateTime.UtcNow,
                LastUsedUtc = DateTime.UtcNow
            };
            _store.Profiles.Add(Named(fallback));
            _store.ActiveProfileId = fallback.Id;
            SaveLocked(_store);
        }
        else if (ClearDefaultNames(_store))
        {
            SaveLocked(_store);
        }
        else if (_store.Profiles.All(p => p.Id != _store.ActiveProfileId))
        {
            _store.ActiveProfileId = _store.Profiles[0].Id;
            SaveLocked(_store);
        }

        return _store;
    }

    /// <summary>The one stand-in profile served while the registry is unreadable.
    /// Not default-flagged: <c>PracticeProfileService</c>'s legacy migration MOVES
    /// the pre-profile score file into the DEFAULT profile's folder the first time
    /// it resolves, and a degraded session must not be what consumes that
    /// one-shot migration.</summary>
    private Store DegradedStore()
    {
        var stand = new PlayerProfile
        {
            Id = DegradedProfileId,
            Name = "",
            AccentColor = AccentPalette[0],
            IsDefault = false,
            CreatedUtc = DateTime.UtcNow,
            LastUsedUtc = DateTime.UtcNow
        };
        return new Store { ActiveProfileId = stand.Id, Profiles = { stand } };
    }

    /// <summary>
    ///     Moves a registry whose CONTENT is not a registry aside as
    ///     <c>player-profiles.corrupt-&lt;stamp&gt;.json</c>, so the ids it names
    ///     survive and the profile folders can still be matched back to people.
    ///     Returns false when the move failed — the original is then still in
    ///     place and must not be written over.
    /// </summary>
    private bool TryQuarantineCorruptRegistry(Exception cause)
    {
        var quarantine = Path.Combine(
            Path.GetDirectoryName(_filePath) ?? string.Empty,
            $"{Path.GetFileNameWithoutExtension(_filePath)}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}" +
            Path.GetExtension(_filePath));

        try
        {
            File.Move(_filePath, quarantine, true);
            _logger?.Warning(AppLogCategory.Application,
                $"Profile registry was unparseable ({cause.Message}); moved to " +
                $"'{Path.GetFileName(quarantine)}' and starting fresh.");
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Application,
                $"Profile registry is unparseable ({cause.Message}) and could not be quarantined; " +
                "refusing to write over it.", ex);
            return false;
        }
    }

    private void SaveLocked(Store store)
    {
        if (ReferenceEquals(store, _degraded))
        {
            // Never write over a registry we failed to READ: this store is one
            // stand-in profile, and the file it would replace names every real
            // one. The change is lost; the profiles are not.
            _logger?.Warning(AppLogCategory.Application,
                "Profile registry was not readable this session; skipping the write so the existing " +
                "profiles survive. This change was not persisted.");
            return;
        }

        try
        {
            // Atomic — a torn write here loses the whole profile registry.
            AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(store, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application, $"Profile registry not writable: {ex.Message}");
        }
    }

    private sealed class Store
    {
        public Guid ActiveProfileId { get; set; }
        public List<PlayerProfile> Profiles { get; set; } = [];
    }
}
