namespace DotCharter.Services.Session;

/// <summary>The project reference and on-disk destination for staged audio attachments.</summary>
internal static class ProjectAudioPaths
{
    internal static (string Reference, string Destination) ForAttachment(string sourcePath, string? projectPath, IAppLogger logger)
    {
        var directory = GetProjectDirectory(projectPath, logger);
        var source = Path.GetFullPath(sourcePath);
        if (directory is null) return (source, source);

        Directory.CreateDirectory(directory);
        var reference = IsInsideDirectory(sourcePath, directory) ? Path.GetFileName(sourcePath) : "song" + Path.GetExtension(sourcePath);
        return (reference, Path.Combine(directory, reference));
    }

    private static string? GetProjectDirectory(string? projectPath, IAppLogger logger)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectPath));
            return string.IsNullOrWhiteSpace(directory) ? null : directory;
        }
        catch (Exception ex)
        {
            logger.Warning(AppLogCategory.Project, $"Could not derive a project directory from '{projectPath}'", ex);
            return null;
        }
    }

    /// <summary>True when <paramref name="filePath" /> sits directly in
    /// <paramref name="directory" /> (so copying it there would copy it onto
    /// itself).</summary>
    internal static bool IsInsideDirectory(string filePath, string directory)
    {
        try
        {
            var fileDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (string.IsNullOrEmpty(fileDirectory)) return false;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(fileDirectory),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
