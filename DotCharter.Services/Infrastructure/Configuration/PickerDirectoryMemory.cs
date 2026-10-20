namespace DotCharter.Services.Infrastructure.Configuration;

/// <summary>
/// Shared "remember the folder the user last picked from" helper for the file-
/// picker dialogs. Remembering a picker directory is cosmetic, so a config-save
/// failure must NEVER break the picker flow — hence the swallow. This was
/// copy-pasted across ~8 dialog view models, and most of the copies dropped the
/// try/catch, so a config-save failure could break the picker flow.
/// </summary>
public static class PickerDirectoryMemory
{
    /// <summary>
    ///     Persists the directory of <paramref name="filePath" /> into
    ///     <see cref="AppConfig" /> via <paramref name="assign" /> (e.g.
    ///     <c>(c, d) =&gt; c.LastGpDir = d</c>), atomically under the config lock
    ///     (<see cref="IConfigurationService.Update" />). No-ops on a null/empty
    ///     path or an empty directory; never throws.
    /// </summary>
    public static void RememberDirectory(this IConfigurationService config, string? filePath,
        Action<AppConfig, string> assign)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return;
            config.Update(c => assign(c, dir));
        }
        catch
        {
            // Remembering a last-used directory is cosmetic — never fail the flow.
        }
    }

    /// <summary>
    ///     Persists <paramref name="folderPath" /> VERBATIM (from a folder picker,
    ///     where the picked path already IS the directory — not a file inside it)
    ///     into <see cref="AppConfig" /> via <paramref name="assign" />, atomically
    ///     under the config lock. No-ops on a null/empty path; never throws.
    /// </summary>
    public static void RememberPickedFolder(this IConfigurationService config, string? folderPath,
        Action<AppConfig, string> assign)
    {
        try
        {
            if (string.IsNullOrEmpty(folderPath)) return;
            config.Update(c => assign(c, folderPath));
        }
        catch
        {
            // Remembering a last-used directory is cosmetic — never fail the flow.
        }
    }
}
