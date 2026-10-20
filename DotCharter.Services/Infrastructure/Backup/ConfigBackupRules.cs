using System.Text.Json;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Update;

namespace DotCharter.Services.Infrastructure.Backup;

/// <summary>
///     Pure rules for the whole-install backup archive: what travels, what
///     never does, and how archive entry paths are sanitized on import.
///
///     The include set is an ALLOWLIST, not "everything minus exclusions",
///     because on a portable install <see cref="DotCharter.Tone.Infrastructure.Storage.WritablePaths.ConfigRoot" />
///     IS the install directory — a denylist would sweep the ~300 MB app payload
///     into every backup and, far worse, let an import overwrite the target
///     machine's binaries (the exact hostfxr/runtimeconfig layout-mixing hazard
///     <see cref="AppPayload" /> exists to prevent). Entries are sourced from the
///     owning services' constants where they exist so the list cannot drift
///     silently (same pattern as <see cref="PayloadReceiptFilter" />).
///
///     Deliberately NOT included:
///     - credential stores (<see cref="IsCredential" />): DPAPI-sealed to this
///       Windows user + machine, so they are useless on the machine being
///       migrated to and must never leave this one anyway;
///     - caches: <c>library-index.db</c> (+ SQLite side-cars), <c>updates/</c>,
///       <c>update-staging/</c>, <c>logs/</c> — all regenerable; the stem cache lives
///       under LocalAppData, outside the config root entirely.
/// </summary>
public static class ConfigBackupRules
{
    /// <summary>Archive schema. Import refuses archives whose major version is
    /// NEWER than this (they may use layouts this build does not understand);
    /// same-or-older majors are accepted.</summary>
    public const int SchemaMajor = 1;

    public const int SchemaMinor = 0;

    /// <summary>Manifest entry at the archive root. Its presence is what marks
    /// a zip as a DotCharter backup.</summary>
    public const string ManifestEntryName = "dotcharter-backup-manifest.json";

    /// <summary>Config-root-level files that carry user data.</summary>
    private static readonly HashSet<string> IncludedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "appconfig.json",                    // JsonConfigurationService — settings
        "keybinds.json",                     // JsonConfigurationService — keybinds
        "player-profiles.json",              // PlayerProfileService — profile registry
        PracticeProfileService.StoreFileName, // practice/gameplay progress
        // Months of "what do I actually use while charting". Cheap to carry and
        // impossible to reconstruct, so a migrate-to-a-new-machine must keep it.
        Diagnostics.UsageStatistics.FileName
    };

    /// <summary>Directory trees under the config root that carry user data.</summary>
    /// <summary>Exposed so <c>PayloadReceiptFilter</c> can be proven to refuse
    /// every tree this classifies as user data — the two lists drifted once, and
    /// `themes` reached the payload filter's blind spot that way.</summary>
    internal static IReadOnlyList<string> UserDataTopDirs => IncludedTopDirs;

    /// <summary>Exposed for the same reason as <see cref="UserDataTopDirs" />:
    /// the LAN packager must be provably a superset of the FILES this classifies
    /// as the user's, not only of the directories. Only the directory half was
    /// pinned, so a new root-level user-data file could reach a LAN peer.</summary>
    internal static IReadOnlyCollection<string> UserDataRootFiles => IncludedRootFiles;

    /// <summary>Exposed so the LAN packager can be proven never to broadcast a
    /// credential store. These are DPAPI-sealed and therefore useless on the
    /// receiving machine — but on a portable install the package would still
    /// OVERWRITE the receiver's own, logging them out of every service.</summary>
    internal static IReadOnlyCollection<string> CredentialFileNames => CredentialFiles;

    private static readonly string[] IncludedTopDirs =
    [
        "profiles",                                   // PlayerProfileService per-profile data
        "languages",                                  // community language packs
        "recovery",                                   // RecoverySnapshotStore crash snapshots
        "themes",                                     // community theme packs
        PhraseCatalogService.FolderName,              // named phrase catalog (.rscp fragments)
        Audio.Dsp.Tones.SongToneEditStore.FolderName  // the player's edits of songs' in-game tones
    ];

    /// <summary>Credential / token stores: never exported, and never accepted
    /// from an archive (a crafted backup must not be able to plant one).</summary>
    private static readonly HashSet<string> CredentialFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        GitHubTokenStore.FileName,                    // github-token.dat
        // The online feature is gone as of v0.60, but an install that ever had
        // it still has these two on disk — they stay on the list as literals so
        // an export can never carry a leftover sealed session out.
        "online-session.bin",
        "online-server-session.bin",
        DotCharter.Tone.Online.Tone3000.DpapiTone3000TokenStore.FileName, // tone3000-token.dat
        // AI tone design (0.55): the Anthropic / gateway API key, DPAPI-sealed to
        // this user on this machine. Useless elsewhere and must never leave here.
        DotCharter.Tone.Ai.DpapiAiSecretStore.FileName            // ai-secrets.bin
    };

    /// <summary>Whether a config-root-relative path is a credential store.
    /// Checked on BOTH export and import.</summary>
    public static bool IsCredential(string relativePath)
    {
        var normalized = Normalize(relativePath);
        return normalized != null && CredentialFiles.Contains(normalized);
    }

    /// <summary>Whether a config-root-relative file belongs in the archive.
    /// Separator-agnostic and case-insensitive.</summary>
    public static bool ShouldInclude(string relativePath)
    {
        var p = Normalize(relativePath);
        if (p == null || CredentialFiles.Contains(p)) return false;

        if (IncludedRootFiles.Contains(p)) return true;

        foreach (var dir in IncludedTopDirs)
        {
            if (p.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Sanitizes an archive entry name into a config-root-relative path:
    ///     separators normalized to '/', and rooted paths / drive letters /
    ///     traversal segments rejected (returns <c>null</c>) so a crafted
    ///     archive can never write outside the config root.
    /// </summary>
    public static string? SanitizeEntryPath(string entryName) => Normalize(entryName);

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var p = path.Replace('\\', '/').TrimStart('/');
        if (p.Length == 0) return null;
        if (p.Contains(':')) return null; // drive letter / alternate data stream

        foreach (var segment in p.Split('/'))
        {
            if (segment is "" or "." or "..")
                return null;
        }

        return p;
    }

    // --- Manifest ----------------------------------------------------------

    public static string SerializeManifest(ConfigBackupManifest manifest) => JsonSerializer.Serialize(manifest, ManifestJsonOptions);

    /// <summary>Parses a manifest, or returns <c>null</c> when the JSON is not
    /// a valid manifest object.</summary>
    public static ConfigBackupManifest? TryParseManifest(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ConfigBackupManifest>(json, ManifestJsonOptions);
            return manifest is { SchemaMajor: > 0 } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

/// <summary>Versioned stamp stored inside every backup archive.</summary>
public sealed record ConfigBackupManifest(
    int SchemaMajor,
    int SchemaMinor,
    string? AppVersion,
    DateTimeOffset CreatedUtc,
    int EntryCount);
