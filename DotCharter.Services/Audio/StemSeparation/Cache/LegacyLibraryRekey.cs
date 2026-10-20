using DotCharter.Services.Library;

namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Re-keys a library song's stem archive from the <c>psarc:</c> identity older
/// builds hashed into its file name to the <c>lib:</c> one (the library identity was
/// renamed; the stem cache kept the old spelling for a while).
/// <para><b>The archive is the only copy of minutes of separation, and stems are never
/// re-separated</b>, so the order is fixed: copy the archive with its recorded identity
/// rewritten into a staging file, flush it, read it back and check every stem's bytes
/// against the original, rename it to the new name (never over an existing file), and
/// only THEN delete the old one. A failure at any step leaves the old archive where it
/// was, under the old name, and the next lookup tries again; a crash leaves at worst a
/// staging directory (swept by <see cref="StemCacheStore.SweepAbandonedStaging"/>) or both
/// archives, in which case the new one is used and the old one is kept.</para>
/// <para>An archive already filed under the NEW name always wins: the old one is left
/// alone and logged, never deleted automatically.</para>
/// </summary>
internal static class LegacyLibraryRekey
{
    /// <summary>The points a test can fail a re-key at.</summary>
    internal enum Stage
    {
        /// <summary>The rewritten copy is in staging, not yet verified.</summary>
        Written,

        /// <summary>The verified copy is under the new name; the old one still exists.</summary>
        Moved,
    }

    /// <summary>
    /// Files the archive an earlier build wrote under <paramref name="current"/>'s
    /// <c>psarc:</c> form (<see cref="StemCacheKey.LegacyLibraryForm"/>) under
    /// <paramref name="current"/>. False when there is no such archive or nothing moved.
    /// </summary>
    internal static bool TryRekey(StemCacheStore store, StemCacheKey current, IAppLogger? logger)
    {
        if (current.LegacyLibraryForm is not { } legacy) return false;
        var from = store.ArchivePath(legacy);
        return File.Exists(from) && TryRekeyAt(store, from, legacy, current, "named after the psarc: identity before #898", logger);
    }

    /// <summary>
    /// Re-keys the archive at <paramref name="archivePath"/>, which records a key
    /// <paramref name="legacy"/> accepts, as an archive <paramref name="current"/>
    /// accepts, filed under <paramref name="current"/>. Never throws, never overwrites.
    /// </summary>
    internal static bool TryRekeyAt(
        StemCacheStore store, string archivePath, StemCacheKey legacy, StemCacheKey current, string reason,
        IAppLogger? logger)
    {
        var name = Path.GetFileName(archivePath);
        try
        {
            var to = store.ArchivePath(current);
            if (!File.Exists(archivePath) ||
                string.Equals(Path.GetFullPath(archivePath), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (File.Exists(to))
            {
                logger?.Info(AppLogCategory.Audio,
                    $"Stem cache: '{name}' ({reason}) has a twin already filed as {current.DirectoryToken}; " +
                    "using that one and keeping both (a duplicate is never deleted automatically).");
                return false;
            }

            var manifest = StemArchive.ReadManifest(archivePath);
            if (manifest is null || manifest.SchemaVersion > StemCacheManifest.CurrentSchemaVersion ||
                !legacy.AcceptsRecordedKey(manifest.Key))
            {
                logger?.Warning(AppLogCategory.Audio,
                    $"Stem cache: '{name}' could not be re-keyed (its manifest is unreadable or names another key); " +
                    "left where it is.");
                return false;
            }

            var rekeyed = StemCacheManifest.FromJson(manifest.ToJson())!;
            rekeyed.Key = LibrarySongIdentity.Normalize(manifest.Key);
            rekeyed.LogicalId = LibrarySongIdentity.NormalizeOrNull(manifest.LogicalId);
            if (!current.AcceptsRecordedKey(rekeyed.Key)) return false;

            Directory.CreateDirectory(store.Root);
            var work = Path.Combine(store.Root,
                StemCacheStore.StagingPrefix + current.DirectoryToken + "-rekey-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var staged = Path.Combine(work, "archive" + StemArchive.ArchiveExtension);
                StemArchive.Rewrite(archivePath, staged, rekeyed);
                store.RekeyFaultHook?.Invoke(Stage.Written, staged);

                var readBack = StemArchive.ReadManifest(staged);
                if (readBack is null || !string.Equals(readBack.Key, rekeyed.Key, StringComparison.Ordinal) ||
                    readBack.Stems.Count != manifest.Stems.Count || !StemArchive.HasSamePayload(archivePath, staged))
                {
                    logger?.Warning(AppLogCategory.Audio,
                        $"Stem cache: the re-keyed copy of '{name}' did not read back identical; " +
                        "kept the original under its old name.");
                    return false;
                }

                File.Move(staged, to, overwrite: false);
            }
            finally
            {
                BestEffortDelete.Directory(work);
            }

            store.RekeyFaultHook?.Invoke(Stage.Moved, to);

            // Only now, with a verified copy under the new name, does the old name go.
            try
            {
                File.Delete(archivePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Warning(AppLogCategory.Audio,
                    $"Stem cache: re-keyed '{name}' as {current.DirectoryToken} but could not remove the old file " +
                    $"({ex.Message}); the new one is used.");
            }

            logger?.Info(AppLogCategory.Audio,
                $"Stem cache: re-keyed '{name}' as {current.DirectoryToken} ({reason}); stems are not re-separated.");
            return true;
        }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Audio,
                $"Stem cache: could not re-key '{name}' ({ex.GetType().Name}: {ex.Message}); " +
                "the archive stays under its old name.");
            return false;
        }
    }
}
