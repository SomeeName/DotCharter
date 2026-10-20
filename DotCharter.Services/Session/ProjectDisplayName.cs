namespace DotCharter.Services.Session;

/// <summary>
///     What a project is CALLED on screen. Every project saves as <c>project.rscp</c> inside
///     its own "Artist - Title" folder, so the file name alone named every chart "project".
/// </summary>
public static class ProjectDisplayName
{
    /// <summary>
    ///     "Artist - Title" from the chart when it has a song name; otherwise the file name,
    ///     or its folder's name when the file is the standard <c>project.rscp</c>; empty
    ///     without either.
    /// </summary>
    public static string For(string? projectPath, ChartData? chart = null)
    {
        var song = chart?.SongName?.Trim() ?? "";
        if (song.Length > 0)
        {
            var artist = chart!.Artist?.Trim() ?? "";
            return artist.Length > 0 ? $"{artist} - {song}" : song;
        }

        if (string.IsNullOrWhiteSpace(projectPath)) return "";
        var name = Path.GetFileNameWithoutExtension(projectPath);
        if (!string.Equals(name, "project", StringComparison.OrdinalIgnoreCase)) return name;
        var folder = Path.GetFileName(Path.GetDirectoryName(projectPath) ?? "");
        return string.IsNullOrWhiteSpace(folder) ? name : folder;
    }
}
