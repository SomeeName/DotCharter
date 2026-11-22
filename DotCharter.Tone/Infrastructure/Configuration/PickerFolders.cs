namespace DotCharter.Tone.Infrastructure.Configuration;

/// <summary>
///     The tone side's "open the dialog where the player last picked this kind of file".
///     Remembering a folder is cosmetic, so neither call throws: a config that cannot be
///     read gives no start folder, one that cannot be saved just forgets.
/// </summary>
public static class PickerFolders
{
    /// <summary>The remembered folder <paramref name="get" /> reads, or null (the dialog's own
    /// default) when none is stored, it is missing on disk, or no config is wired.</summary>
    public static string? StartFolder(this IRigConfigService? config, Func<RigConfig, string?> get)
    {
        if (config is null) return null;
        try
        {
            var dir = get(config.LoadRigConfig());
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) ? dir : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Remembers the folder holding <paramref name="filePath" />. No-op for a cancelled
    /// pick (null or empty path).</summary>
    public static void RememberFileFolder(this IRigConfigService? config, string? filePath,
        Action<RigConfig, string> assign)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        string? dir;
        try { dir = Path.GetDirectoryName(filePath); }
        catch (ArgumentException) { return; }
        if (string.IsNullOrEmpty(dir)) return;
        try { config?.UpdateRigConfig(c => assign(c, dir)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
