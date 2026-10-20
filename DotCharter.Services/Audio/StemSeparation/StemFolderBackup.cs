namespace DotCharter.Services.Audio.StemSeparation;

/// <summary>
///     The file work around re-separating a song whose stem folder already holds
///     WAVs. Stems the user keeps are MOVED into <c>.backup</c> so the separator
///     cannot clobber them; after a cancel or a failure they go back as they were,
///     and after a successful run they replace whatever the model produced under
///     the same name. The moved files are the LAST copy, so every step logs
///     instead of throwing and leaves the file where it can be found.
/// </summary>
public sealed class StemFolderBackup
{
    private readonly string _outputDir;
    private readonly string _backupDir;
    private readonly IAppLogger _logger;
    private readonly Dictionary<string, string> _backedUp = new(StringComparer.OrdinalIgnoreCase);

    private StemFolderBackup(string outputDir, IReadOnlyDictionary<string, string> existing, IAppLogger logger)
    {
        _outputDir = outputDir;
        _backupDir = Path.Combine(outputDir, ".backup");
        _logger = logger;
        Existing = existing;
    }

    /// <summary>The WAVs already in the folder, by stem name.</summary>
    public IReadOnlyDictionary<string, string> Existing { get; }

    /// <summary>The stems moved aside, by name, to the path they wait at.</summary>
    public IReadOnlyDictionary<string, string> BackedUp => _backedUp;

    public static StemFolderBackup Open(string outputDir, IAppLogger logger)
    {
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(outputDir))
        {
            foreach (var file in Directory.EnumerateFiles(outputDir, "*.wav", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                existing[name] = file;
            }
        }

        return new StemFolderBackup(outputDir, existing, logger);
    }

    /// <summary>Stage "keep" stems to a sibling folder so the separator can't clobber them.</summary>
    public void MoveAside(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_backupDir);
            foreach (var name in names)
            {
                if (!Existing.TryGetValue(name, out var src) || !File.Exists(src))
                {
                    continue;
                }

                var dst = Path.Combine(_backupDir, Path.GetFileName(src));
                try
                {
                    // Delete-then-Move is two steps with a window between
                    // them: a failure after the Delete has destroyed the
                    // destination for nothing. File.Move(overwrite) is one.
                    File.Move(src, dst, overwrite: true);
                    _backedUp[name] = dst;
                }
                catch (Exception ex)
                {
                    _logger.Warning(AppLogCategory.Audio, $"Could not back up stem '{name}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Audio, $"Stem backup pre-staging failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Puts the "keep" stems back after a cancel or a failed separation.
    ///     <para>
    ///         This is the LAST copy of those files — they were MOVED into
    ///         <c>.backup</c> so the separator could not clobber them — so a failure
    ///         here is the user's stems gone. It must not be a
    ///         <c>catch { Debug.WriteLine }</c>, which a Release build strips
    ///         entirely, and a Delete-then-Move whose two steps left a window where
    ///         a failure had already destroyed the destination for nothing.
    ///     </para>
    /// </summary>
    public void Restore()
    {
        foreach (var backupPath in _backedUp.Values)
        {
            try
            {
                var restored = Path.Combine(_outputDir, Path.GetFileName(backupPath));
                File.Move(backupPath, restored, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.Warning(AppLogCategory.Audio,
                    $"Could not restore the backed-up stem '{Path.GetFileName(backupPath)}'; it is still " +
                    $"in '{Path.GetDirectoryName(backupPath)}': {ex.Message}");
            }
        }

        DeleteEmptyBackupDir();
    }

    /// <summary>
    ///     Post-flight: for every stem we promised to keep, drop the freshly
    ///     generated WAV (if any) and move the backup back into place. Returns the
    ///     stem set the project should register.
    /// </summary>
    public List<StemTrack> ApplyKept(IReadOnlyList<StemTrack> produced)
    {
        var producedByName = produced.ToDictionary(s => s.Name, s => s.Path, StringComparer.OrdinalIgnoreCase);
        var finalStems = new List<StemTrack>(produced);
        foreach (var (name, backupPath) in _backedUp)
        {
            if (producedByName.TryGetValue(name, out var freshPath))
            {
                try
                {
                    if (File.Exists(freshPath))
                    {
                        File.Delete(freshPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(AppLogCategory.Audio,
                        $"Could not drop the regenerated stem '{name}' before restoring the kept one: {ex.Message}");
                }

                finalStems.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            }

            var restored = Path.Combine(_outputDir, Path.GetFileName(backupPath));
            try
            {
                File.Move(backupPath, restored, overwrite: true);
                finalStems.Add(new StemTrack { Name = name, Path = restored });
            }
            catch (Exception ex)
            {
                _logger.Warning(AppLogCategory.Audio, $"Could not restore kept stem '{name}': {ex.Message}");
            }
        }

        DeleteEmptyBackupDir();
        return finalStems;
    }

    private void DeleteEmptyBackupDir()
    {
        try
        {
            if (Directory.Exists(_backupDir) && !Directory.EnumerateFileSystemEntries(_backupDir).Any())
            {
                Directory.Delete(_backupDir);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(AppLogCategory.Audio, $"Empty stem backup dir not deletable ({_backupDir}): {ex.Message}");
        }
    }
}
