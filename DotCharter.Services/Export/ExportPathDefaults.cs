namespace DotCharter.Services.Export;

/// <summary>
///     Where an export's files default to, resolved against the project folder: the output
///     folder, the playback audio when it already is (or sits beside) a .wem/.wav, and the
///     chart's embedded cover. Every lookup goes through <see cref="IExportTargetWriter" />.
/// </summary>
public static class ExportPathDefaults
{
    /// <summary>The last export folder while it exists, else the project's folder, else Documents.</summary>
    public static string ExportDirectory(AppConfig config, string projectPath, IExportTargetWriter files)
    {
        if (!string.IsNullOrWhiteSpace(config.LastExportDir) && files.DirectoryExists(config.LastExportDir))
            return config.LastExportDir;
        return ProjectDirectory(projectPath) is { Length: > 0 } dir ? dir : files.DocumentsFolder;
    }

    /// <summary>The playback audio when it is .wem/.wav, else a sibling .wem/.wav of it; null when none exists.</summary>
    public static string? ProjectAudio(string audioFileName, string projectPath, IExportTargetWriter files)
    {
        if (string.IsNullOrWhiteSpace(audioFileName)) return null;
        var full = Path.IsPathRooted(audioFileName) ? audioFileName : Path.Combine(ProjectDirectory(projectPath) ?? "", audioFileName);
        foreach (var candidate in new[] { full, Path.ChangeExtension(full, "wem"), Path.ChangeExtension(full, "wav") })
        {
            if (!files.FileExists(candidate)) continue;
            if (candidate.EndsWith(".wem", StringComparison.OrdinalIgnoreCase) ||
                candidate.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The chart's cover on disk, or "" when unset or missing (the builder then uses a placeholder).</summary>
    public static string CoverArt(ChartProject project, string projectPath, IExportTargetWriter files)
    {
        var cover = project.Chart.CoverArtPath;
        if (string.IsNullOrWhiteSpace(cover)) return "";
        var full = Path.IsPathRooted(cover) ? cover : Path.Combine(ProjectDirectory(projectPath) ?? "", cover);
        return files.FileExists(full) ? full : "";
    }

    /// <summary>Rewrites the file name's platform suffix (_p / _m), keeping its folder and base name.</summary>
    public static string WithPlatformSuffix(string outputPath, bool mac)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return outputPath;
        var dir = Path.GetDirectoryName(outputPath) ?? "";
        var rebuilt = StripPsarcSuffix(Path.GetFileName(outputPath)) + (mac ? "_m.psarc" : "_p.psarc");
        return string.IsNullOrEmpty(dir) ? rebuilt : Path.Combine(dir, rebuilt);
    }

    private static string StripPsarcSuffix(string file)
    {
        foreach (var suffix in new[] { "_p.psarc", "_m.psarc", ".psarc" })
        {
            if (file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return file[..^suffix.Length];
        }

        return file;
    }

    private static string? ProjectDirectory(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
