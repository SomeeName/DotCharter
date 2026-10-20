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
///         <c>CurrentAudioFileName</c>, so every caller has to accept one.
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
            if (File.Exists(audioFileName)) return audioFileName;

            // The stale-absolute case: keep looking under the project, by name.
            var bareName = Path.GetFileName(audioFileName);
            return string.IsNullOrEmpty(bareName) || bareName == audioFileName
                ? null
                : TryResolve(projectPath, bareName);
        }

        var projectDirectory = DirectoryOf(projectPath);
        if (projectDirectory == null) return null;

        var directPath = Path.Combine(projectDirectory, audioFileName);
        if (File.Exists(directPath)) return directPath;

        // The chart may sit in a per-arrangement subfolder while the audio
        // lives once at the song root — walk up rather than give up.
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
    ///     concrete location when the file is missing (the loader logs it) or
    ///     that are choosing a write target rather than reading one.
    /// </summary>
    public static string ResolveOrDefault(string? projectPath, string audioFileName)
        => TryResolve(projectPath, audioFileName)
           ?? Path.Combine(DirectoryOf(projectPath) ?? "", audioFileName);

    /// <summary>
    ///     The directory that owns the resolved audio — the stems/output root.
    ///     For a rooted audio reference that is the audio's own folder, which is
    ///     what keeps stem separation working for a PSARC session that was never
    ///     saved to disk.
    /// </summary>
    public static string? ResolveOutputDirectory(string? projectPath, string? audioFileName)
    {
        var resolved = TryResolve(projectPath, audioFileName);
        if (resolved != null) return Path.GetDirectoryName(resolved);

        return audioFileName != null && Path.IsPathRooted(audioFileName)
            ? Path.GetDirectoryName(audioFileName)
            : DirectoryOf(projectPath);
    }

    private static string? DirectoryOf(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        var directory = Path.GetDirectoryName(projectPath);
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }
}
