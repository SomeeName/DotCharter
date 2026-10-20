using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Infrastructure.CrashReporting;
using DotCharter.Services.Library.Index;
using DotCharter.Services.State.Checkpoints;

namespace DotCharter.Services.Update;

/// <summary>
///     Refuses a staged update payload that carries files an app payload has no
///     business carrying — checked on RECEIPT, never trusted because the sender
///     filtered.
///     <para>
///         <b>Why this exists.</b> The receiving side ran
///         <see cref="AppPayload.Validate" />, which is a presence-and-layout
///         check (is there an exe, a runtimeconfig, a matching host pair) and says
///         nothing about what ELSE is in the archive, and then handed the whole
///         staged tree to <c>UpdaterScriptBuilder</c>, whose robocopy copies every
///         entry over the live install. A zip that simply was not built by our
///         release pipeline could therefore ship the victim's own binaries plus
///         one <c>appconfig.json</c>.
///     </para>
///     <para>
///         <b>That single file is a whole attack.</b> On a portable install
///         <c>WritablePaths.ConfigRoot</c> IS the install directory, and
///         <c>ResolveConfigRoot</c> treats an <c>appconfig.json</c> next to the
///         exe as the marker for one — so planting it both switches the victim to
///         a portable layout AND makes the attacker's settings authoritative, down
///         to the update channel and every path the app reads. The same copy can
///         overwrite player profiles, keybinds and the practice store, and it
///         lands with no prompt.
///     </para>
///     <para>
///         The rule this encodes is the general one: <b>a package is filtered on
///         RECEIPT, never trusted because whoever built it filtered.</b> So it
///         runs in <c>GitHubUpdateService.ApplyDownloadedUpdate</c>, which every
///         apply path funnels through.
///     </para>
/// </summary>
public static class PayloadReceiptFilter
{
    /// <summary>
    ///     Why one payload-relative path may not be applied, or null when it may.
    ///     Pure, so the rule is unit-testable without staging anything.
    /// </summary>
    public static string? Classify(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "it has an empty entry name";

        var p = relativePath.Replace('\\', '/').TrimStart('/');

        // The extractor already refuses these, and the deletion list refuses them
        // again. A third check costs nothing and this one runs immediately before
        // an irreversible robocopy: a rooted or ..-walking entry is never a
        // payload file, whatever produced it.
        if (p.Length == 0 || Path.IsPathRooted(relativePath) || p.Contains(':'))
            return $"'{relativePath}' is not a payload-relative path";
        foreach (var segment in p.Split('/'))
        {
            if (segment is "." or "..")
                return $"'{relativePath}' walks outside the payload";
        }

        if (IsLocalState(p))
        {
            return $"'{p}' is a machine's own user data, credentials or machine state, " +
                   "which an update payload never carries";
        }

        return null;
    }

    /// <summary>
    ///     Whether a file (path relative to the install dir) is this machine's own
    ///     state rather than app payload. Pure so it is unit-testable. Comparison
    ///     is case-insensitive and separator-agnostic.
    ///     <para>
    ///         This matters more than it looks: on a portable install
    ///         <c>WritablePaths.ConfigRoot</c> resolves to the install
    ///         directory itself, so every user-data file the app writes sits among
    ///         the binaries — and a payload entry that is not refused here is
    ///         robocopied straight over the receiver's copy of it.
    ///     </para>
    /// </summary>
    public static bool IsLocalState(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return true;

        var p = relativePath.Replace('\\', '/').TrimStart('/');
        // A split payload carries the app in app/. A user-state name there
        // is contraband just the same: nothing the app writes lives in app/, so a
        // payload that brings one is not ours, and the rule must not be one
        // folder away from bypassed.
        if (p.StartsWith(AppPayload.SplitPrefix, StringComparison.OrdinalIgnoreCase) &&
            IsLocalState(p[AppPayload.SplitPrefix.Length..]))
        {
            return true;
        }

        var lower = p.ToLowerInvariant();

        // DERIVED, not restated: anything the backup system classifies as the
        // user's data or as a credential store is by definition not something an
        // update payload may overwrite. Two hand-maintained lists WILL drift —
        // `themes` already did — so the coupling is structural rather than a lint
        // over two literals. The literals below stay as defence in depth (and as
        // the record of WHY each name is here); this line is what makes a name
        // added to ConfigBackupRules alone impossible to slip through.
        if (Infrastructure.Backup.ConfigBackupRules.ShouldInclude(p) ||
            Infrastructure.Backup.ConfigBackupRules.IsCredential(p))
        {
            return true;
        }

        // Credentials, settings and saved progress: never overwritten by a payload.
        if (ExcludedFiles.Contains(lower)) return true;

        // SQLite side-car files (-wal / -shm / -journal) travel with the index.
        if (lower.StartsWith(SqliteLibraryIndex.DefaultFileName, StringComparison.Ordinal)) return true;

        // Volatile trees that may live next to the exe on a portable install.
        foreach (var dir in ExcludedTopDirs)
        {
            if (lower == dir || lower.StartsWith(dir + "/", StringComparison.Ordinal))
                return true;
        }

        // Logs and temp files.
        if (lower.EndsWith(".log", StringComparison.Ordinal)) return true;
        if (lower.EndsWith(".tmp", StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>Lower-cased, install-dir-relative names of files the app writes
    /// next to itself on a portable install. Sourced from the owning services
    /// where a constant exists so this list cannot drift silently.</summary>
    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "appconfig.json",                                    // JsonConfigurationService
        "keybinds.json",                                     // JsonConfigurationService
        RocksmithKeysFingerprint.FileName,                   // JsonConfigurationService
        "player-profiles.json",                              // PlayerProfileService
        UpdaterScriptBuilder.ScriptFileName,
        // In-flight update state for THIS machine: a payload has nothing to do
        // with a stage recorded here, and applying one would offer a "finish the
        // update" pointing at a path this install has not got. (The .log and .tmp
        // siblings are covered by the suffix rules below, but named here too so
        // the set reads as one thing.)
        UpdaterScriptBuilder.PendingStateFileName,
        UpdaterScriptBuilder.LogFileName,
        UpdaterScriptBuilder.WaitProbeFileName,
        GitHubTokenStore.FileName,                           // DPAPI-sealed GitHub PAT
        // Retired with the online feature; kept as literals because an
        // install that ever had it still has them, and a payload must not carry
        // one machine's sealed session onto another.
        "online-session.bin",
        "online-server-session.bin",
        DotCharter.Tone.Online.Tone3000.DpapiTone3000TokenStore.FileName,    // DPAPI-sealed TONE3000 session
        DotCharter.Tone.Ai.DpapiAiSecretStore.FileName,                      // DPAPI-sealed AI provider key
        PracticeProfileService.StoreFileName,
        // Retired with the LAN scoreboard; a literal for the same
        // reason as the online ones above — an install that had it still does.
        "lan-scoreboard.json",
        // Which dialogs, keybinds and commands this person uses, with dates. It
        // is a per-machine behaviour record and is never anyone else's business —
        // and on a portable install a payload carrying one would OVERWRITE the
        // receiver's own counts.
        Diagnostics.UsageStatistics.FileName,
        "usage-report.txt"                                   // the rendered form of the same thing
    };

    /// <summary>
    ///     Directory trees an update payload never carries. On a PORTABLE install
    ///     <c>WritablePaths.ConfigRoot</c> IS the install dir, so anything missing
    ///     here is copied straight over the receiver's own copy — a silent
    ///     data-loss footgun, not just an oversharing one.
    ///     <para>
    ///         A superset of <c>ConfigBackupRules.UserDataTopDirs</c> — and that is
    ///         guaranteed BY CONSTRUCTION rather than by keeping two lists in step:
    ///         <see cref="IsLocalState" /> asks <c>ConfigBackupRules</c> first.
    ///         These entries remain because most of them are NOT on the backup
    ///         allowlist (caches, staging, downloaded model weights) and because
    ///         each one records why it is here. Pinned by
    ///         <c>PayloadReceiptFilterTests.Filter_RefusesEveryDirectoryTheBackupRulesCallUserData</c>
    ///         and its file/credential siblings.
    ///     </para>
    /// </summary>
    internal static readonly string[] ExcludedTopDirs =
    [
        "update-staging", "updates", "logs", "recovery",
        "profiles",                                          // PlayerProfileService per-profile data
        "screenshots",                                       // v0.32 one-key screenshots (user media)
        "videos",                                            // legacy exported-run-video output (user media)
        "languages",                                         // community language packs (user-authored)
        "themes",                                            // community theme packs (user-authored)
        PhraseCatalogService.FolderName,                     // named phrase catalog — the charter's own riffs
        Audio.Dsp.Tones.SongToneEditStore.FolderName,        // the player's edits of songs' in-game tones
        CheckpointService.FolderName,                        // whole-project snapshots — unpublished work
        CrashReportService.FolderName,                       // log tails + machine details
        "multiplayer-received",                              // legacy LAN downloads (removed)
        "lan-avatars",                                       // legacy LAN avatar cache (removed)
        // Where a config-backup restore inflates before it swaps. A crash
        // between the two leaves it behind, holding SOMEBODY ELSE'S settings,
        // keybinds and profiles out of a backup archive — the exact footgun this
        // list exists for.
        Infrastructure.Backup.ConfigBackupService.ImportStagingFolderName,
        // Downloaded on demand and published as their own release assets, NOT as
        // app payload.
        "models",                                            // lyric-align + lyric-align-en (~640 MB)
        "gpu-runtime"                                        // the old CUDA/cuDNN download, removable in Settings
    ];

    /// <summary>
    ///     Walks a staged payload directory and returns the first refusal, or null
    ///     when every file may be applied. The offending entry is named in the
    ///     message: a refusal nobody can act on is a mystery failed update.
    /// </summary>
    public static string? FindRefusal(string payloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadDirectory));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var problem = Classify(Path.GetRelativePath(root, file));
            if (problem != null) return problem;
        }

        return null;
    }
}
