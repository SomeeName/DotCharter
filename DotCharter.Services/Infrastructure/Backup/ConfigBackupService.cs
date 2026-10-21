using System.IO.Compression;

namespace DotCharter.Services.Infrastructure.Backup;

/// <summary>Outcome of an export or import. <see cref="SkippedEntries" /> lists
/// archive entries an import refused (unknown/credential/unsafe paths, files that
/// are not data) — an export never skips, it just doesn't pick files up.</summary>
public sealed record ConfigBackupResult(int FileCount, IReadOnlyList<string> SkippedEntries)
{
    /// <summary>Settings the backup carried that an import deliberately did NOT take
    /// (<see cref="AppConfigBackupFilter" />: plug-in, executable, endpoint and
    /// folder paths). This machine's value stays; the user has to set them again.</summary>
    public IReadOnlyList<string> NotRestoredSettings { get; init; } = [];
}

/// <summary>
///     Exports the user's whole DotCharter state (settings, keybinds, profiles
///     and gameplay data, community language/theme
///     packs, recovery snapshots) into one versioned zip archive, and imports
///     one back. What travels is governed by <see cref="ConfigBackupRules" />
///     — credential stores and caches never do.
/// </summary>
public interface IConfigBackupService
{
    /// <summary>Writes the backup archive to <paramref name="archivePath" />
    /// (overwriting an existing file).</summary>
    ConfigBackupResult Export(string archivePath);

    /// <summary>Restores a backup archive over the current config root
    /// (merge-overwrite: files in the archive win, everything else is kept) —
    /// except what a received file must not decide: only data files restore
    /// (<see cref="ConfigBackupRules.ShouldTransfer" />), and <c>appconfig.json</c> is merged
    /// through <see cref="AppConfigBackupFilter" />, so plug-in, executable, endpoint and
    /// auto-open settings keep THIS machine's value (listed in
    /// <see cref="ConfigBackupResult.NotRestoredSettings" />). Refuses archives without a manifest or from a newer schema major.</summary>
    /// <exception cref="InvalidDataException">Not a DotCharter backup archive,
    /// or one from a newer, incompatible schema.</exception>
    ConfigBackupResult Import(string archivePath);
}

/// <inheritdoc />
public sealed class ConfigBackupService : IConfigBackupService
{
    private readonly string? _configRootOverride;

    /// <param name="configRoot">Override for tests; defaults to
    /// <see cref="WritablePaths.ConfigRoot" /> resolved at call time.</param>
    public ConfigBackupService(string? configRoot = null) => _configRootOverride = configRoot;

    private string ConfigRoot => _configRootOverride ?? WritablePaths.ConfigRoot;

    public ConfigBackupResult Export(string archivePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ConfigRoot));
        var fullArchivePath = Path.GetFullPath(archivePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullArchivePath)!);
        if (File.Exists(fullArchivePath)) File.Delete(fullArchivePath);

        var count = 0;
        try
        {
            using var zip = ZipFile.Open(fullArchivePath, ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                // The archive may be saved inside the config root — never pack ourselves.
                if (string.Equals(Path.GetFullPath(file), fullArchivePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var relative = Path.GetRelativePath(root, file);
                if (!ConfigBackupRules.ShouldTransfer(relative)) continue;

                zip.CreateEntryFromFile(file, relative.Replace('\\', '/'), CompressionLevel.Optimal);
                count++;
            }

            var manifest = new ConfigBackupManifest(
                ConfigBackupRules.SchemaMajor,
                ConfigBackupRules.SchemaMinor,
                DotCharter.Services.Infrastructure.AppVersionInfo.Current,
                DateTimeOffset.UtcNow,
                count);
            var manifestEntry = zip.CreateEntry(ConfigBackupRules.ManifestEntryName, CompressionLevel.Optimal);
            using var writer = new StreamWriter(manifestEntry.Open());
            writer.Write(ConfigBackupRules.SerializeManifest(manifest));
        }
        catch
        {
            BestEffortDelete.File(fullArchivePath);
            throw;
        }

        return new ConfigBackupResult(count, []);
    }

    /// <summary>
    ///     Members an import archive may declare. A populated config root is a
    ///     few hundred files (profiles, avatars, recovery snapshots, packs), so
    ///     this is generous — it exists so that merely reaching
    ///     <c>zip.Entries</c> cannot be made to materialise one
    ///     <c>ZipArchiveEntry</c> per member from a tiny file.
    /// </summary>
    public const int MaxImportEntries = 16_384;

    /// <summary>Bytes an import may write, given the archive it came from. A
    /// real backup carries checkpoints and recovery snapshots, so the floor is
    /// generous; the 8× scale is what stops a small crafted archive from filling
    /// the disk. Same idiom as <c>AppPayload.InflationCeiling</c>.</summary>
    public static long ImportInflationCeiling(long archiveLengthBytes)
        => Math.Max(512L * 1024 * 1024, Math.Max(0, archiveLengthBytes) * 8);

    /// <summary>Overrides the extraction budget <see cref="Import" /> enforces.
    /// Test seam only: the ceiling floors at 512 MiB, and proving the budget
    /// bites through it would cost the gate half a gigabyte of disk writes. The
    /// FORMULA is pinned separately and purely on
    /// <see cref="ImportInflationCeiling" />.</summary>
    internal long? ImportBudgetOverrideForTests { get; set; }

    /// <summary>
    ///     Where an import inflates BEFORE anything live is touched. A sibling of
    ///     the config root's real files rather than a temp folder, because the
    ///     promote step is <see cref="AtomicFile.ReplaceWithRetry" /> and a rename
    ///     is only atomic within one volume.
    ///     <para>
    ///         It is on <c>PayloadReceiptFilter.ExcludedTopDirs</c>: on a PORTABLE
    ///         install the config root IS the install dir, so a staging folder
    ///         inside an update payload would otherwise be copied straight over
    ///         this machine's — carrying somebody else's settings, keybinds and
    ///         profiles out of their backup archive.
    ///     </para>
    /// </summary>
    public const string ImportStagingFolderName = ".import-staging";

    /// <summary>
    ///     Restores a backup archive. Members are inflated into
    ///     <see cref="ImportStagingFolderName" /> first and only swapped into
    ///     place once the WHOLE archive has been accepted.
    ///     <para>
    ///         That ordering is the point. <c>ExtractBounded</c> refuses
    ///         MID-ARCHIVE (budget exceeded, unsafe path, too many entries) and a
    ///         corrupt member throws straight out of the inflater — and because
    ///         <c>Export</c> enumerates with <c>SearchOption.AllDirectories</c>,
    ///         the config-root FILES (<c>appconfig.json</c>, <c>keybinds.json</c>,
    ///         <c>player-profiles.json</c>, the practice store, the usage
    ///         statistics) are written into the archive BEFORE the large subtrees,
    ///         so they are exactly the members already overwritten when a
    ///         truncated archive fails later. Writing straight into the live root
    ///         therefore answered "the archive was refused" while the user's
    ///         settings and every stored personal best had already been replaced
    ///         by the partial copy, with no undo.
    ///     </para>
    ///     <para>
    ///         The promote loop is renames only, so its own failure window is
    ///         orders of magnitude narrower than a decompression — and no refusal
    ///         path reaches it at all.
    ///     </para>
    /// </summary>
    public ConfigBackupResult Import(string archivePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ConfigRoot));

        // A backup archive is a file the user RECEIVED from somewhere — a second
        // machine, a cloud drive, a friend — so it goes through
        // BoundedZipExtractor like every other untrusted archive. Bound the
        // member count from the end-of-central-directory record BEFORE opening,
        // because opening and touching Entries is itself the cost.
        var precheck = BoundedZipExtractor.CheckDeclaredEntryCount(archivePath, MaxImportEntries);
        if (precheck != ZipExtractionRefusal.None)
        {
            throw new InvalidDataException(
                $"Refusing the backup archive: the member count could not be bounded ({precheck}).");
        }

        using var zip = ZipFile.OpenRead(archivePath);

        var manifestEntry = zip.GetEntry(ConfigBackupRules.ManifestEntryName) ?? throw new InvalidDataException(
                $"Not a DotCharter backup archive (no {ConfigBackupRules.ManifestEntryName}).");
        ConfigBackupManifest? manifest;
        using (var reader = new StreamReader(manifestEntry.Open()))
        {
            manifest = ConfigBackupRules.TryParseManifest(reader.ReadToEnd());
        }

        if (manifest == null)
            throw new InvalidDataException("The backup archive's manifest is unreadable.");
        if (manifest.SchemaMajor > ConfigBackupRules.SchemaMajor)
        {
            throw new InvalidDataException(
                $"The backup was created by a newer DotCharter (archive schema {manifest.SchemaMajor}.{manifest.SchemaMinor}, " +
                $"this build understands up to {ConfigBackupRules.SchemaMajor}.x). Update DotCharter, then import.");
        }

        Directory.CreateDirectory(root);

        var staging = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(root, ImportStagingFolderName)));
        // A previous run that was killed between the inflate and the swap leaves
        // this behind; it is scratch, never a source of truth.
        BestEffortDelete.Directory(staging);
        Directory.CreateDirectory(staging);

        var skipped = new List<string>();

        try
        {
            // The zip-slip and allowlist guards are unchanged and still decide WHICH
            // members land where; the extractor decides HOW MANY bytes may come out
            // of the inflater while they do. `null` from the selector skips a member
            // without refusing the archive, which is what keeps "a crafted entry is
            // reported, not fatal" true.
            var extraction = BoundedZipExtractor.ExtractBounded(
                zip, staging,
                ImportBudgetOverrideForTests ?? ImportInflationCeiling(SafeLength(archivePath)),
                MaxImportEntries,
                entry =>
                {
                    if (string.Equals(entry.FullName, ConfigBackupRules.ManifestEntryName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }
                    // Directory placeholder entries carry no data.
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0)
                        return null;

                    // A crafted archive must neither escape the config root nor plant
                    // files we would never have exported (credential stores included).
                    var relative = ConfigBackupRules.SanitizeEntryPath(entry.FullName);
                    if (relative == null || !ConfigBackupRules.ShouldTransfer(relative))
                    {
                        skipped.Add(entry.FullName);
                        return null;
                    }

                    var destination = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!destination.StartsWith(staging + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add(entry.FullName); // belt and braces after sanitization
                        return null;
                    }

                    return relative;
                });

            if (!extraction.Succeeded)
            {
                throw new InvalidDataException(
                    $"Refusing the backup archive: it was rejected ({extraction.Refusal}) after " +
                    $"{extraction.BytesWritten} bytes in {extraction.FilesWritten} file(s). " +
                    "Nothing was changed.");
            }

            var (notRestored, droppedConfig) = FilterStagedAppConfig(staging, root, skipped);

            PromoteStagedFiles(staging, root);
            return new ConfigBackupResult(extraction.FilesWritten - (droppedConfig ? 1 : 0), skipped)
            {
                NotRestoredSettings = notRestored
            };
        }
        finally
        {
            // On every exit: a refusal, a corrupt member thrown out of the
            // inflater, or a clean promote that emptied it anyway.
            BestEffortDelete.Directory(staging);
        }
    }

    /// <summary>
    ///     Runs the staged <c>appconfig.json</c> through <see cref="AppConfigBackupFilter" />
    ///     against this machine's own file, BEFORE anything is promoted: the executable
    ///     and path settings never reach the live root. A file that is not a JSON object
    ///     is dropped (reported in <paramref name="skipped" />) so the user's working
    ///     settings are not replaced by something the app cannot read.
    /// </summary>
    private static (IReadOnlyList<string> NotRestored, bool Dropped) FilterStagedAppConfig(
        string staging, string root, List<string> skipped)
    {
        var staged = Path.Combine(staging, "appconfig.json");
        if (!File.Exists(staged)) return ([], false);

        var live = Path.Combine(root, "appconfig.json");
        string? currentJson = null;
        try
        {
            if (File.Exists(live)) currentJson = File.ReadAllText(live);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: nothing to keep from it.
        }

        var filtered = AppConfigBackupFilter.TryFilter(File.ReadAllText(staged), currentJson);
        if (filtered == null)
        {
            File.Delete(staged);
            skipped.Add("appconfig.json");
            return ([], true);
        }

        if (filtered.Changed) File.WriteAllText(staged, filtered.Json);
        return (filtered.NotRestored, false);
    }

    /// <summary>
    ///     Swaps every staged file into place with
    ///     <see cref="AtomicFile.ReplaceWithRetry" /> — the same bounded
    ///     exponential retry every other stage-then-swap in the app uses, because
    ///     a rename onto a file an antivirus or indexer is holding fails on
    ///     Windows and a bare <c>File.Move</c> would surface that as a failed
    ///     restore.
    /// </summary>
    private static void PromoteStagedFiles(string staging, string root)
    {
        // Materialized: the loop MOVES files out of the tree it is walking, and a
        // lazy enumeration over a directory being emptied can skip entries.
        var staged = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).ToList();

        // Each file lands atomically, but the SET did not: a failure on file 3 of
        // 6 left three imported files beside three of the user's own — a config
        // root that never existed on either side of the restore — and the caller's
        // `finally` then deleted the staging tree, so the other three were gone
        // for good. So: keep the previous bytes of every destination we are about
        // to overwrite, and put them back if any promote fails. This directory is
        // a SIBLING of staging, never inside it, because the enumeration above
        // walks staging and the caller wipes it.
        var rollbackRoot = staging + ".rollback";
        var journal = new List<(string Destination, string? Saved)>();

        try
        {
            foreach (var file in staged)
            {
                var relative = Path.GetRelativePath(staging, file);
                var destination = Path.Combine(root, relative);
                var directory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                string? saved = null;
                if (File.Exists(destination))
                {
                    saved = Path.Combine(rollbackRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved, overwrite: true);
                }

                // Journaled BEFORE the swap: if the swap is what throws, the entry
                // restores identical bytes, which costs nothing.
                journal.Add((destination, saved));
                AtomicFile.ReplaceWithRetry(file, destination);
            }
        }
        catch (Exception ex)
        {
            var rollbackFailures = RollBackPromotedFiles(journal);
            if (rollbackFailures.Count > 0)
            {
                throw new AggregateException(
                    "A config restore failed part-way and could not be fully rolled back; the config " +
                    "root is a MIX of imported and original files.", [ex, .. rollbackFailures]);
            }

            throw;
        }
        finally
        {
            BestEffortDelete.Directory(rollbackRoot);
        }
    }

    /// <summary>
    ///     Puts every destination this promote already touched back the way it was,
    ///     newest first. Returns what it could not undo — the caller must NOT report
    ///     a clean failure when this comes back non-empty.
    /// </summary>
    private static List<Exception> RollBackPromotedFiles(List<(string Destination, string? Saved)> journal)
    {
        var failures = new List<Exception>();
        for (var i = journal.Count - 1; i >= 0; i--)
        {
            var (destination, saved) = journal[i];
            try
            {
                if (saved != null) File.Copy(saved, destination, overwrite: true);
                else if (File.Exists(destination)) File.Delete(destination);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        return failures;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
