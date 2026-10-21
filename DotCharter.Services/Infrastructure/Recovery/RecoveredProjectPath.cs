namespace DotCharter.Services.Infrastructure.Recovery;

/// <summary>
///     Whether the project path a crash-recovery sidecar names may be adopted as the
///     place "Save" writes the recovered work back to.
///     <para>
///         The sidecar sits in <c>recovery/</c> beside the snapshot and is read back
///         as-is, so its <c>OriginalProjectPath</c> is a value somebody other than the
///         current session can have written (a restored backup, a copied config
///         folder). Adopting it blindly turned "recover" into "overwrite whatever path
///         the file names". So it is taken only when it is a <c>.rscp</c> file, spelt
///         as a plain absolute local path, outside the operating system's and the
///         installed programs' folders. Anything else leaves the session UNTITLED:
///         the work is still recovered, and the user chooses where it is saved.
///     </para>
///     <para>
///         Network (UNC) and device paths are refused: a project that lives on a share
///         is recovered as untitled and saved wherever the user points. The path that IS
///         adopted is the one the recovery dialog prints for the user to read.
///     </para>
/// </summary>
public static class RecoveredProjectPath
{
    private const string ProjectExtension = ".rscp";

    private static readonly Environment.SpecialFolder[] ProtectedFolders =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles,
        Environment.SpecialFolder.CommonProgramFilesX86
    ];

    public static bool IsAdoptable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!string.Equals(Path.GetExtension(path), ProjectExtension, StringComparison.OrdinalIgnoreCase)) return false;

        // UNC (\\server\share), device (\\?\, \\.\) and rooted-without-drive (\dir) forms.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
        if (!Path.IsPathFullyQualified(path)) return false;

        // Spelt plainly: no ".." or "." segments and no alternate data stream.
        foreach (var segment in path.Split('\\', '/'))
        {
            if (segment is "." or "..") return false;
        }

        if (path.IndexOf(':', 2) >= 0) return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var folder in ProtectedFolders)
        {
            var protectedRoot = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(protectedRoot)) continue;

            var root = Path.TrimEndingDirectorySeparator(protectedRoot) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
