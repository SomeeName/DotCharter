using System.Text.Json;

namespace DotCharter.Services.Session;

/// <summary>Project-local notices survive reopening while migration keeps retrying eligible files.</summary>
internal sealed class ProjectTidyNotices
{
    private static readonly object Gate = new();
    private readonly string _root;
    private readonly string _statePath;
    private readonly IAppLogger _logger;

    internal ProjectTidyNotices(string root, IAppLogger logger)
    {
        _root = root;
        _statePath = Path.Combine(root, ProjectFolderLayout.MetadataFolderName, "tidy-notices.json");
        _logger = logger;
    }

    internal void Unproven(string path) => Info(path, "ownership could not be verified");

    internal void Collision(string path) => Info(path, "the export destination already exists");

    internal void Failed(string path, Exception exception)
    {
        if (!First(path)) return;
        _logger.Warning(AppLogCategory.Project, $"Project folder tidy left '{Path.GetFileName(path)}' in place; the next open will try again.", exception);
    }

    private void Info(string path, string detail)
    {
        if (First(path)) _logger.Info(AppLogCategory.Project, $"Project folder tidy left '{Path.GetFileName(path)}' alone: {detail}.");
    }

    private bool First(string path)
    {
        lock (Gate)
        {
            var shown = Read();
            var key = Path.GetRelativePath(_root, path).Replace('\\', '/');
            if (!shown.Add(key)) return false;
            try
            {
                ProjectFolderLayout.MetadataDirectory(_root);
                AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(shown));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // An unwritable notice record must never move or remove a candidate.
            }
            return true;
        }
    }

    private HashSet<string> Read()
    {
        try
        {
            return File.Exists(_statePath)
                ? JsonSerializer.Deserialize<HashSet<string>>(AtomicFile.ReadAllText(_statePath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
