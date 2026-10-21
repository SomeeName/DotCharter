namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>
///     The single "where is this project's audio?" resolver.
///     <para>
///         Separate copies disagree: a project LOADER that walks
///         up the parent directories; stem separation that only probes the
///         project folder; a run-video exporter that calls
///         <see cref="File.Exists(string)" /> on the raw
///         <c>CurrentAudioFileName</c>, which is a bare file name for every
///         normal project and therefore never exists. The result is a song
///         that plays fine but whose stems or replay video "have no audio" —
///         with no message pointing at the real cause.
///     </para>
///     <para>
///         Rooted names are real: a PSARC-loaded session (and any flow that
///         loads audio without a saved project) stores an ABSOLUTE path in
///         <c>CurrentAudioFileName</c>, so every caller has to accept one — but
///         only a LOCAL drive path: a UNC, device or network-drive name read
///         from a shared file is never probed (see <see cref="ProjectRelativePath" />).
///     </para>
/// </summary>
public static class ProjectAudioLocator
{
    /// <summary>
    ///     Resolves the audio file on disk, or null when it cannot be found.
    ///     Order: a rooted name that exists → the project folder → each parent
    ///     directory in turn.
    ///     <para>
    ///         A rooted reference whose file is GONE falls back to its bare file
    ///         name against the project, rather than stopping dead. An absolute
    ///         audio path is not exotic — a PSARC session, an imported audio
    ///         file and (since the Save As fix) a project saved into a new folder
    ///         all store one — and the moment the user moves the song folder
    ///         somewhere else, that path names nothing. The audio is usually
    ///         sitting right next to the project it was moved with, and the
    ///         relative walk below finds it; refusing to look was how "I moved my
    ///         project and it lost its audio" happened. The absolute path is
    ///         still tried FIRST, so nothing that resolves today resolves
    ///         differently.
    ///     </para>
    /// </summary>
    public static string? TryResolve(string? projectPath, string? audioFileName)
    {
        if (string.IsNullOrWhiteSpace(audioFileName)) return null;

        if (Path.IsPathRooted(audioFileName))
        {
            // The name came out of a file: a UNC, device or network-drive path is
            // never probed (the probe alone hands NTLM credentials to the host).
            if (ProjectRelativePath.IsLocalAbsolute(audioFileName) && File.Exists(audioFileName)) return audioFileName;

            // The stale-absolute case: keep looking under the project, by name.
            var bareName = BareNameOf(audioFileName);
            return bareName == null ? null : TryResolve(projectPath, bareName);
        }

        if (!ProjectRelativePath.IsReadableMediaReference(audioFileName)) return null;

        var projectDirectory = DirectoryOf(projectPath);
        if (projectDirectory == null) return null;

        var directPath = Path.Combine(projectDirectory, audioFileName);
        if (File.Exists(directPath)) return directPath;

        // The chart may sit in a per-arrangement subfolder while the audio
        // lives once at the song root — walk up rather than give up. A bare
        // file name only: a declared sub-path is not searched for elsewhere.
        if (audioFileName.IndexOfAny(['\\', '/']) >= 0) return null;
        var directory = new DirectoryInfo(projectDirectory).Parent;
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, audioFileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    ///     Like <see cref="TryResolve" />, but falls back to the path the audio
    ///     WOULD occupy next to the project. For callers that must report a
    ///     concrete location when the file is missing (the loader logs it).
    ///     The fallback is always under the project folder: a refused or rooted
    ///     reference contributes only its bare file name.
    /// </summary>
    public static string ResolveOrDefault(string? projectPath, string audioFileName)
    {
        var resolved = TryResolve(projectPath, audioFileName);
        if (resolved != null) return resolved;

        var name = !Path.IsPathRooted(audioFileName) && ProjectRelativePath.IsReadableMediaReference(audioFileName)
            ? audioFileName
            : BareNameOf(audioFileName) ?? "";
        return Path.Combine(DirectoryOf(projectPath) ?? "", name);
    }

    /// <summary>
    ///     The directory derived outputs (stems, padded audio) are WRITTEN to.
    ///     <para>
    ///         With a project on disk, its audio reference is data from a file, so
    ///         it may only pick the folder when it is a plain relative name: the
    ///         project folder, or the song root a per-arrangement chart walks up
    ///         to. A rooted or <c>..</c> reference writes into the project folder
    ///         instead — never into a folder the file named.
    ///     </para>
    ///     <para>
    ///         Without one (a PSARC session never saved, audio attached to an
    ///         unsaved session) the rooted reference was made by THIS session, so
    ///         the outputs go beside the audio, as they always have.
    ///     </para>
    /// </summary>
    public static string? ResolveOutputDirectory(string? projectPath, string? audioFileName)
    {
        var projectDirectory = DirectoryOf(projectPath);
        if (projectDirectory != null)
        {
            if (ProjectRelativePath.IsSafeRelative(audioFileName) &&
                TryResolve(projectPath, audioFileName) is { } relative)
            {
                return Path.GetDirectoryName(relative);
            }

            return projectDirectory;
        }

        var resolved = TryResolve(null, audioFileName);
        if (resolved != null) return Path.GetDirectoryName(resolved);

        return ProjectRelativePath.IsLocalAbsolute(audioFileName) ? Path.GetDirectoryName(audioFileName) : null;
    }

    private static string? BareNameOf(string audioFileName)
    {
        // Both separators, on every platform: the name may come from a Windows-authored file.
        var bareName = audioFileName[(audioFileName.LastIndexOfAny(['\\', '/']) + 1)..];
        if (bareName.Length >= 2 && bareName[1] == ':') bareName = bareName[2..];
        return string.IsNullOrEmpty(bareName) || bareName == audioFileName || !ProjectRelativePath.IsSafeRelative(bareName)
            ? null
            : bareName;
    }

    private static string? DirectoryOf(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var directory = Path.GetDirectoryName(projectPath);
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }
}
