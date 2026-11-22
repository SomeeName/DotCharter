using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>Paths shared by saves, exports and project-local metadata.</summary>
public static class ProjectFolderLayout
{
    public const string ExportFolderName = "export";
    public const string MetadataFolderName = ".dotcharter";

    public static string ProjectDirectory(string projectPath) => Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

    public static string ExportDirectory(string projectPath) => Path.Combine(ProjectDirectory(projectPath), ExportFolderName);

    public static string CompanionPath(string projectPath, string extension) =>
        Path.Combine(ExportDirectory(projectPath), Path.GetFileNameWithoutExtension(projectPath) + extension);

    public static string MetadataDirectory(string projectDirectory)
    {
        var directory = Directory.CreateDirectory(Path.Combine(projectDirectory, MetadataFolderName));
        try
        {
            if (OperatingSystem.IsWindows()) directory.Attributes |= FileAttributes.Hidden;
        }
        catch (PlatformNotSupportedException) { /* Hidden attributes are optional on unsupported filesystems. */ }
        return directory.FullName;
    }

    internal static string RootForArtifact(string path)
    {
        var parent = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (ProjectFiles.IsProject(path) || File.Exists(Path.Combine(parent.FullName, ".dotcharter", "companions.json")))
            return parent.FullName;
        for (var directory = parent; directory.Parent != null; directory = directory.Parent)
        {
            if (directory.Name.Equals(ExportFolderName, StringComparison.OrdinalIgnoreCase) ||
                directory.Name.Equals(MetadataFolderName, StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent.FullName;
            }
        }
        return parent.Name is "DTRS XML" or "DTRS XML.saving" or "DTRS XML.old" && parent.Parent != null
            ? parent.Parent.FullName : parent.FullName;
    }

    internal static string CacheDirectory(string audioPath, string? projectDirectory = null) =>
        Directory.CreateDirectory(Path.Combine(MetadataDirectory(projectDirectory ?? RootForArtifact(audioPath)), "cache")).FullName;
}
