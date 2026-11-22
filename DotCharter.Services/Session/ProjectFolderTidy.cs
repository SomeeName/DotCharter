using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Session;

/// <summary>Idempotent upgrade on open. Only ownership hashes and exact legacy producer names grant permission.</summary>
internal static class ProjectFolderTidy
{
    // One tidy per folder at a time: a startup reload and a user open two seconds
    // apart each ran one, both recycled the same files and the loser logged the shell's
    // "file not found" as a failed tidy. The second now waits and finds nothing left to do.
    private static readonly ConcurrentDictionary<string, object> FolderGates = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Run(string projectPath, ChartProject project, IAppLogger logger, Action<string>? recycle = null)
    {
        if (!File.Exists(projectPath)) return false;
        var root = Path.GetFullPath(ProjectFolderLayout.ProjectDirectory(projectPath));
        lock (FolderGates.GetOrAdd(root, _ => new object()))
            return RunExclusive(projectPath, project, logger, recycle ?? RecycleBin.Remove);
    }

    private static bool RunExclusive(string projectPath, ChartProject project, IAppLogger logger, Action<string> recycle)
    {
        if (!File.Exists(projectPath)) return false;
        var root = ProjectFolderLayout.ProjectDirectory(projectPath);
        var changed = false;
        var notices = new ProjectTidyNotices(root, logger);
        void Attempt(string path, Action action)
        {
            try
            {
                action();
                changed |= !File.Exists(path) && !Directory.Exists(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or PlatformNotSupportedException)
            {
                notices.Failed(path, ex);
            }
        }

        var legacyStamp = Path.Combine(root, CompanionOwnership.LegacyStampFileName);
        if (File.Exists(legacyStamp))
            Attempt(legacyStamp, () => CompanionOwnership.ImportLegacyStamps(projectPath, recycle));

        var oldBuilderProjects = new[] { Path.ChangeExtension(projectPath, ".rs2dlc"), DlcBuilderProjectFile.PathIn(root, project) }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var oldBuilderProject in oldBuilderProjects)
        {
            if (!File.Exists(oldBuilderProject)) continue;
            if (!CompanionOwnership.IsUnmodified(oldBuilderProject))
            {
                notices.Unproven(oldBuilderProject);
                continue;
            }
            var complete = false;
            Attempt(oldBuilderProject, () => complete = MoveLegacyBundle(oldBuilderProject, projectPath, notices, recycle));
            var target = ProjectFolderLayout.CompanionPath(projectPath, ".rs2dlc");
            if (complete) Attempt(oldBuilderProject, () => MoveOwned(oldBuilderProject, target, recycle, notices));
        }

        foreach (var extension in new[] { ".eof", ".rscp" })
        {
            var old = Path.ChangeExtension(projectPath, extension);
            if (string.Equals(old, projectPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(old)) continue;
            if (!CompanionOwnership.IsUnmodified(old))
            {
                notices.Unproven(old);
                continue;
            }
            var target = ProjectFolderLayout.CompanionPath(projectPath, extension);
            Attempt(old, () => MoveOwned(old, target, recycle, notices));
        }

        MoveLegacyPackages(root, projectPath, project, notices, Attempt);
        RecycleLegacyAudio(root, project, recycle, notices, Attempt);
        TidyBackupsAndLookups(root, projectPath, recycle, Attempt);

        foreach (var file in Directory.EnumerateFiles(root, "*.psarc")) notices.Unproven(file);
        var remainingBundle = Path.Combine(root, "DTRS XML");
        if (Directory.Exists(remainingBundle))
            foreach (var file in Directory.EnumerateFiles(remainingBundle, "*.xml")) notices.Unproven(file);

        MoveLegacyChats(root, recycle, Attempt);
        return changed;
    }

    private static void MoveLegacyPackages(string root, string projectPath, ChartProject project,
        ProjectTidyNotices notices, Action<string, Action> attempt)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            RocksmithDlcKey.Create(project.Chart.Artist ?? "", project.Chart.SongName ?? "")
        };
        if (!string.IsNullOrWhiteSpace(project.Chart.DlcKey) && !RocksmithDlcKey.IsInvalidTypedKey(project.Chart.DlcKey))
            keys.Add(new string(project.Chart.DlcKey.Where(char.IsAsciiLetterOrDigit).ToArray()));
        foreach (var key in keys)
        {
            foreach (var suffix in new[] { "_p.psarc", "_m.psarc", "_rsxr_p.psarc" })
            {
                var old = Path.Combine(root, key + suffix);
                if (File.Exists(old))
                {
                    var name = RocksmithPackageName.For(project, project.Chart.Version,
                        mac: suffix == "_m.psarc", extended: suffix == "_rsxr_p.psarc");
                    attempt(old, () => MovePreserved(old, Path.Combine(ProjectFolderLayout.ExportDirectory(projectPath), name), notices, true));
                }
            }
        }
    }

    private static void RecycleLegacyAudio(string root, ChartProject project, Action<string> recycle,
        ProjectTidyNotices notices, Action<string, Action> attempt)
    {
        var source = ProjectRelativePath.TryResolve(root, project.AudioFileName);
        if (source != null && Path.GetDirectoryName(source) == root)
        {
            var stem = Path.GetFileNameWithoutExtension(source);
            var generated = new Regex($"^(?:{Regex.Escape(Path.GetFileName(source))}|{Regex.Escape(stem)}(?:\\.(?:wav|ogg|oga|mp3|flac)|\\.preview(?:\\.wav)?))\\.(?:pcm|vorbis)(?:\\.decode-v[0-9]+)?\\.wem$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (var file in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(file, source, StringComparison.OrdinalIgnoreCase)) continue;
                var plain = name.Equals(stem + ".wem", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(stem + ".preview.wem", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(stem + ".preview.wav", StringComparison.OrdinalIgnoreCase);
                if (!generated.IsMatch(name) && !plain) continue;
                if (plain && !CompanionOwnership.IsUnmodified(file))
                {
                    notices.Unproven(file);
                    continue;
                }
                attempt(file, () =>
                {
                    recycle(file);
                    if (!File.Exists(file)) CompanionOwnership.Forget(file);
                });
            }
        }
    }

    private static void TidyBackupsAndLookups(string root, string projectPath, Action<string> recycle,
        Action<string, Action> attempt)
    {
        var projectName = Regex.Escape(Path.GetFileNameWithoutExtension(projectPath));
        var originalBackup = new Regex($"^{projectName}\\.rscp(?:\\.[0-9]+)?\\.bak$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var obsolete = new Regex($"^(?:\\.customsforge-lookup\\.json|{projectName}\\.customsforge\\.(?:json|fill\\.js))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var name = Path.GetFileName(file);
            if (originalBackup.IsMatch(name))
            {
                attempt(file, () =>
                {
                    var backup = CharterRscp.LegacyBackupPath(Path.ChangeExtension(projectPath, ".rscp"));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    AtomicFile.MoveAside(file, backup, overwrite: false);
                });
            }
            if (obsolete.IsMatch(name)) attempt(file, () => recycle(file));
        }
    }

    private static void MoveLegacyChats(string root, Action<string> recycle, Action<string, Action> attempt)
    {
        var oldChats = Path.Combine(root, AiChatStore.FolderName);
        if (Directory.Exists(oldChats))
        {
            foreach (var folder in Directory.EnumerateDirectories(oldChats))
            {
                var chartId = Path.GetFileName(folder);
                if (!ProjectIdentity.IsWellFormed(chartId)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    attempt(file, () =>
                    {
                        if (AiChatJson.TryRead(AtomicFile.ReadAllBytes(file), out var chat, out _) != AiChatStatus.Ok ||
                            chat!.ChartId != chartId || chat.Id != Path.GetFileNameWithoutExtension(file))
                        {
                            return;
                        }
                        var target = Path.Combine(ProjectFolderLayout.MetadataDirectory(root), AiChatStore.FolderName, chartId, Path.GetFileName(file));
                        if (File.Exists(target)) return;
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        AtomicFile.MoveAside(file, target, overwrite: false);
                    });
                }
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) attempt(folder, () => recycle(folder));
            }
            if (!Directory.EnumerateFileSystemEntries(oldChats).Any()) attempt(oldChats, () => recycle(oldChats));
        }
    }

    private static void MoveOwned(string source, string target, Action<string> recycle, ProjectTidyNotices notices)
    {
        if (File.Exists(target))
        {
            if (CompanionOwnership.IsUnmodified(target)) recycle(source);
            if (File.Exists(source)) notices.Collision(source);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.MoveAside(source, target, overwrite: false);
        CompanionOwnership.AdoptMovedFile(target);
        CompanionOwnership.Forget(source);
    }

    private static void MovePreserved(string source, string target, ProjectTidyNotices notices, bool stamp)
    {
        if (File.Exists(target))
        {
            notices.Collision(source);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        AtomicFile.MoveAside(source, target, overwrite: false);
        if (stamp) CompanionOwnership.AdoptMovedFile(target);
        CompanionOwnership.Forget(source);
    }

    private static bool MoveLegacyBundle(string builderProject, string projectPath, ProjectTidyNotices notices, Action<string> recycle)
    {
        using var document = JsonDocument.Parse(AtomicFile.ReadAllText(builderProject));
        var root = ProjectFolderLayout.ProjectDirectory(projectPath);
        var oldBundle = Path.Combine(root, "DTRS XML");
        var pending = false;
        foreach (var reference in XmlReferences(document.RootElement))
        {
            var old = ProjectRelativePath.TryResolve(root, reference);
            if (old == null || Path.IsPathRooted(reference) || !Path.GetExtension(old).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                pending = true;
                notices.Unproven(builderProject);
                continue;
            }
            var target = Path.Combine(ProjectFolderLayout.ExportDirectory(projectPath), Path.GetRelativePath(root, old));
            if (!File.Exists(old))
            {
                if (!File.Exists(target) || !CompanionOwnership.IsUnmodified(target))
                {
                    pending = true;
                    notices.Unproven(builderProject);
                }
                continue;
            }
            MovePreserved(old, target, notices, true);
            pending |= File.Exists(old);
        }
        if (Directory.Exists(oldBundle) && !Directory.EnumerateFileSystemEntries(oldBundle).Any()) recycle(oldBundle);
        return !pending;
    }

    private static IEnumerable<string> XmlReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "Xml" && property.Value.ValueKind == JsonValueKind.String)
                    yield return property.Value.GetString()!;
                else
                    foreach (var reference in XmlReferences(property.Value)) yield return reference;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                foreach (var reference in XmlReferences(child)) yield return reference;
        }
    }
}
