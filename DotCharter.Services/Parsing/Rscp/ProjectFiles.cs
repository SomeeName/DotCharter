using System.Xml;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     The ONE spelling of DotCharter's project file: <c>.dcproj</c>. Its content is the
///     RsCharter-derived XML that <see cref="RscpWriter" /> writes (the format family keeps its
///     <c>Rscp</c> code name); only the extension says "this is a DotCharter project".
///     <para>
///         Every product path that names, filters for, or creates a project file goes through
///         here or through <see cref="CharterRscp" />, so the two extensions cannot drift apart
///         between the picker, the drop router, the library scan and the save path.
///     </para>
/// </summary>
public static class ProjectFiles
{
    /// <summary>The project file extension, with its dot.</summary>
    public const string Extension = ".dcproj";

    /// <summary>The picker / directory-scan pattern for a project file.</summary>
    public const string Pattern = "*" + Extension;

    /// <summary>The file name a project gets inside its own folder.</summary>
    public const string DefaultFileName = "project" + Extension;

    /// <summary>The patterns of every file that opens as a project: ours first, then Charter's.</summary>
    public static readonly IReadOnlyList<string> OpenablePatterns = [Pattern, CharterRscp.Pattern];

    /// <summary>True for a <c>.dcproj</c> path (case-insensitive).</summary>
    public static bool IsProject(string? path) => HasExtension(path, Extension);

    /// <summary>True for a path that opens as a project: a <c>.dcproj</c> or a Charter / older DotCharter <c>.rscp</c>.</summary>
    public static bool IsOpenable(string? path) => IsProject(path) || CharterRscp.IsCharterProject(path);

    /// <summary>The default project path inside <paramref name="folder" />.</summary>
    public static string DefaultPath(string folder) => Path.Combine(folder, DefaultFileName);

    /// <summary>The <c>.dcproj</c> beside <paramref name="path" /> with the same name.</summary>
    public static string Beside(string path) => Path.ChangeExtension(path, Extension);

    /// <summary>
    ///     The file to open for <paramref name="path" />: a <c>.rscp</c> whose same-named
    ///     <c>.dcproj</c> sits beside it is that project's Charter companion, so the
    ///     <c>.dcproj</c> is opened instead. Anything else comes back unchanged.
    /// </summary>
    public static string PreferProject(string path, Func<string, bool>? fileExists = null)
    {
        if (!CharterRscp.IsCharterProject(path)) return path;
        var project = Beside(path);
        return (fileExists ?? File.Exists)(project) ? project : path;
    }

    /// <summary>
    ///     The project files among <paramref name="paths" />: every <c>.dcproj</c>, plus every
    ///     <c>.rscp</c> that has no same-named <c>.dcproj</c> beside it (a companion is never
    ///     listed in its project's place).
    /// </summary>
    public static IReadOnlyList<string> WithoutShadowedCompanions(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var projects = new HashSet<string>(list.Where(IsProject).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        return list.Where(p => !CharterRscp.IsCharterProject(p) || !projects.Contains(Path.GetFullPath(Beside(p)))).ToList();
    }

    internal static bool HasExtension(string? path, string extension) =>
        !string.IsNullOrEmpty(path) && string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
///     The ONE spelling of Charter's project file, <c>.rscp</c>: what DotCharter reads from
///     Charter (RsCharter) and from its own older builds, and the Charter-compatible
///     companion it writes beside every <see cref="ProjectFiles.Extension" /> on save.
/// </summary>
public static class CharterRscp
{
    /// <summary>Charter's project extension, with its dot.</summary>
    public const string Extension = ".rscp";

    /// <summary>The picker / directory-scan pattern for a Charter project.</summary>
    public const string Pattern = "*" + Extension;

    /// <summary>
    ///     The suffix an older DotCharter <c>.rscp</c> is renamed with when its project first
    ///     saves as a <c>.dcproj</c>: the bytes are kept, under a name that does not open as a project.
    /// </summary>
    public const string LegacyBackupSuffix = ".bak";

    /// <summary>True for a <c>.rscp</c> path (case-insensitive).</summary>
    public static bool IsCharterProject(string? path) => ProjectFiles.HasExtension(path, Extension);

    /// <summary>The Charter companion of a project: the same name with <c>.rscp</c>.</summary>
    public static string CompanionPath(string projectPath) => Path.ChangeExtension(projectPath, Extension);

    /// <summary>
    ///     The first free backup name for an older DotCharter <c>.rscp</c>:
    ///     <c>&lt;name&gt;.rscp.bak</c>, then <c>&lt;name&gt;.rscp.2.bak</c> and so on, so an
    ///     earlier backup is never overwritten.
    /// </summary>
    public static string LegacyBackupPath(string rscpPath, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        var candidate = rscpPath + LegacyBackupSuffix;
        for (var n = 2; exists(candidate); n++)
            candidate = $"{rscpPath}.{n}{LegacyBackupSuffix}";
        return candidate;
    }

    /// <summary>
    ///     Renames an older DotCharter <c>.rscp</c> to its <see cref="LegacyBackupPath" />, keeping its bytes.
    ///     Returns the backup path, or null when the file is not there. The caller has already written the
    ///     <c>.dcproj</c> that replaces it; an <see cref="IOException" /> or
    ///     <see cref="UnauthorizedAccessException" /> leaves the file where it is.
    /// </summary>
    public static string? MoveToLegacyBackup(string rscpPath)
    {
        if (!File.Exists(rscpPath)) return null;
        var backup = LegacyBackupPath(rscpPath);
        File.Move(rscpPath, backup);
        return backup;
    }

    /// <summary>
    ///     Whether the <c>.rscp</c> at <paramref name="path" /> was written by DotCharter: its
    ///     root carries <c>&lt;dcWriterVersion&gt;</c>. A plain Charter project does not. Only the
    ///     root's direct children are visited; an unreadable file answers false.
    /// </summary>
    public static bool IsWrittenByDotCharter(string path)
    {
        try
        {
            using var stream = SafeXml.OpenRead(path);
            using var reader = SafeXml.CreateReader(stream);
            if (reader.MoveToContent() != XmlNodeType.Element) return false;
            if (reader.IsEmptyElement) return false;
            var rootDepth = reader.Depth;
            reader.Read();
            while (!reader.EOF && reader.Depth > rootDepth)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth == rootDepth + 1)
                {
                    if (reader.LocalName == RscpFormatContract.WriterVersionElement) return true;
                    reader.Skip();
                    continue;
                }

                reader.Read();
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                                       or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
