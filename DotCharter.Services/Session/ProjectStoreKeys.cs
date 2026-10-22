namespace DotCharter.Services.Session;

/// <summary>
///     How an own chart's play-stats key is compared. The key IS the project's path
///     (<c>LivePlayViewModel.ProfileSongKey</c>), written from two places that do not
///     spell it identically (the song scan and the File ▸ Open picker), so a re-key
///     matches the way the file system does rather than byte for byte.
/// </summary>
internal static class ProjectStoreKeys
{
    /// <summary>True when both name the same file: separators and, on Windows, case do not matter.</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        return string.Equals(Normalize(a), Normalize(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
