
namespace DotCharter.Services.Session;

/// <summary>
///     Keeps the original bytes of a project file written by a NEWER build before this build's
///     first save replaces it: this build cannot re-attach everything the newer one wrote, so
///     the overwrite is otherwise unrecoverable. One copy per file and writer version, never
///     replaced.
/// </summary>
internal static class NewerWriterBackup
{
    /// <summary>The <c>&lt;file&gt;.&lt;version&gt;.bak</c> path beside <paramref name="projectPath" />.</summary>
    internal static string PathFor(string projectPath, string writerVersion)
    {
        // No trim: an existing backup's name must come out the same.
        var safe = SafeFileName.ReplaceInvalid(writerVersion);
        return $"{projectPath}.{safe}.bak";
    }

    /// <summary>
    ///     Copies <paramref name="projectPath" /> to its <c>.bak</c> unless one exists or there is no
    ///     file yet. Returns the backup path when a copy was made. Throws on an IO failure, so the
    ///     save fails rather than overwriting an original it could not keep.
    /// </summary>
    internal static string? EnsureBackup(string projectPath, string? newerWriterVersion, IAppLogger? logger = null)
    {
        if (string.IsNullOrEmpty(newerWriterVersion) || !File.Exists(projectPath)) return null;
        var backup = PathFor(projectPath, newerWriterVersion);
        if (File.Exists(backup)) return null;

        File.Copy(projectPath, backup, overwrite: false);
        logger?.Info(AppLogCategory.Project,
            $"'{projectPath}' was written by a newer build ({newerWriterVersion}); kept the original as '{backup}' before overwriting it.");
        return backup;
    }
}
