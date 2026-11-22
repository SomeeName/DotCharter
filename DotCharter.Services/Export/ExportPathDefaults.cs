using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Export;

/// <summary>
///     Where an export's files default to, resolved against the project folder: the output
///     folder, the project snapshot's export audio, and the
///     chart's embedded cover. Every lookup goes through <see cref="IExportTargetWriter" />.
/// </summary>
public static class ExportPathDefaults
{
    /// <summary>A saved project's export folder; otherwise the last export folder or Documents.</summary>
    public static string ExportDirectory(AppConfig config, string projectPath, IExportTargetWriter files)
    {
        if (!string.IsNullOrWhiteSpace(projectPath)) return ProjectFolderLayout.ExportDirectory(projectPath);
        if (!string.IsNullOrWhiteSpace(config.LastExportDir) && files.DirectoryExists(config.LastExportDir))
            return config.LastExportDir;
        return ProjectDirectory(projectPath) is { Length: > 0 } dir ? dir : files.DocumentsFolder;
    }

    /// <summary>The shared artist/title/version release name used by the save companion and export dialog.</summary>
    public static string PackageFileName(ChartProject project) =>
        RocksmithPackageName.For(project, project.Chart.Version);

    /// <summary>The snapshot's export audio; an unrelated sibling never supplies a missing source.</summary>
    public static string? ProjectAudio(ChartProject snapshot, string audioFileName, string projectPath, IExportTargetWriter files) =>
        ProjectAudio(ProjectExportAudio.ForSnapshot(snapshot, audioFileName), projectPath, files);

    /// <summary>The exact working audio reference, or null when missing or unsafe.</summary>
    public static string? ProjectAudio(string audioFileName, string projectPath, IExportTargetWriter files) =>
        ProjectRelativePath.IsReadableMediaReference(audioFileName)
            ? ProjectAudioLocator.TryResolve(projectPath, audioFileName, files.FileExists)
            : null;

    /// <summary>The chart's cover on disk, or "" when unset or missing (the builder then uses a placeholder).</summary>
    public static string CoverArt(ChartProject project, string projectPath, IExportTargetWriter files)
    {
        var cover = project.Chart.CoverArtPath;
        if (string.IsNullOrWhiteSpace(cover)) return "";
        // Packed into the export, so only a file inside the project folder qualifies.
        var full = ProjectRelativePath.TryResolve(ProjectDirectory(projectPath), cover);
        return full != null && files.FileExists(full) ? full : "";
    }

    /// <summary>Rewrites the platform suffix and keeps the extended-range tag only on PC.</summary>
    public static string WithPlatformSuffix(string outputPath, bool mac)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) return outputPath;
        var dir = Path.GetDirectoryName(outputPath) ?? "";
        var pc = StripPsarcSuffix(Path.GetFileName(outputPath)) + "_p.psarc";
        pc = Rocksmith.ExtendedRangePackageName.Apply(pc, !mac && pc.EndsWith("_rsxr_p.psarc", StringComparison.OrdinalIgnoreCase));
        var rebuilt = mac ? pc[..^"_p.psarc".Length] + "_m.psarc" : pc;
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
