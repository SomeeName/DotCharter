namespace DotCharter.Services.Import.Youtube;

/// <summary>
///     Locates a bundled/installed command-line media tool (yt-dlp, ffmpeg).
///     Resolution order: an explicit configured path, then next to the app
///     executable (the bundled copy), then the app's <c>tools/</c> subfolder
///     (where releases ship ffmpeg/ffprobe/yt-dlp), then the repo's packer cache
///     (a dev run), and only then each directory on PATH - which callers pass in
///     only when the user opted in (<see cref="PathDirsFrom" />).
///     Pure and unit-testable — the filesystem probe is injected.
/// </summary>
public static class MediaToolResolver
{
    /// <summary>
    ///     Resolves <paramref name="baseName" /> (e.g. "yt-dlp", "ffmpeg") to a
    ///     full path, or null when not found. On Windows the ".exe" variant is
    ///     tried first. Precedence: configured path → exe dir → exe dir/<c>tools</c>
    ///     → dev-tree packer cache → PATH (the <c>tools</c> subfolder is the canonical
    ///     release bundle location; an exe-dir copy still wins over it, and everything
    ///     the app ships wins over an ambient PATH entry).
    /// </summary>
    public static string? Resolve(
        string baseName,
        string? exeDir,
        string? configuredPath,
        IEnumerable<string> pathDirs,
        Func<string, bool> fileExists,
        bool isWindows)
    {
        // An explicit user-configured path wins outright.
        if (!string.IsNullOrWhiteSpace(configuredPath) && fileExists(configuredPath))
            return configuredPath;

        var fileNames = isWindows ? new[] { baseName + ".exe", baseName } : [baseName];

        // A copy bundled with the app (exe dir, then its "tools/" subfolder) is
        // preferred over an ambient PATH one. Releases ship the tools in tools/ —
        // at the INSTALL's top, which is the exe dir's parent when the
        // app runs from the app/ folder of a split install.
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            var installRoot = InstallLocation.ResolveInstallRoot(exeDir, fileExists);
            var bundled = new List<string> { exeDir, Path.Combine(exeDir, "tools") };
            if (!string.Equals(installRoot, Path.TrimEndingDirectorySeparator(exeDir), StringComparison.OrdinalIgnoreCase))
                bundled.Add(Path.Combine(installRoot, "tools"));
            foreach (var appDir in bundled)
            {
                foreach (var name in fileNames)
                {
                    var candidate = Path.Combine(appDir, name);
                    if (fileExists(candidate)) return candidate;
                }
            }
        }

        // Dev-tree fallback: running from bin/Debug there is no bundled
        // copy, so silence baking / tag extraction silently degraded on dev
        // runs (owner report). The repo's release-packer cache
        // (tools/redist/win-x64) has the tools — walk up from the exe dir.
        // Releases never get here: their bundled tools/ copy wins above. PATH
        // (opt-in only) comes after this, not before it.
        if (isWindows && !string.IsNullOrWhiteSpace(exeDir))
        {
            var probe = exeDir;
            for (var i = 0; i < 8 && probe is not null; i++, probe = Path.GetDirectoryName(probe))
            {
                var redist = Path.Combine(probe, "tools", "redist", "win-x64");
                foreach (var name in fileNames)
                {
                    var candidate = Path.Combine(redist, name);
                    if (fileExists(candidate)) return candidate;
                }
            }
        }

        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in fileNames)
            {
                var candidate = Path.Combine(dir, name);
                if (fileExists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     The environment variable that lets the resolver fall back to PATH. Unset (or
    ///     anything but <c>1</c>) means PATH is not consulted at all.
    /// </summary>
    public const string PathOptInVariable = "DOTCHARTER_TOOLS_FROM_PATH";

    /// <summary>
    ///     The directories to search on PATH: those of the current process when
    ///     <see cref="PathOptInVariable" /> is <c>1</c>, else none. An executable found by
    ///     walking PATH is whatever an earlier directory on it happens to hold, so the
    ///     app never reaches for one on its own: releases bundle every tool in
    ///     <c>tools/</c>, and a tool installed elsewhere is named in the
    ///     <c>FfmpegPath</c> / <c>YtDlpPath</c> settings.
    /// </summary>
    public static IEnumerable<string> CurrentPathDirs()
        => PathDirsFrom(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable(PathOptInVariable));

    /// <summary>The pure half of <see cref="CurrentPathDirs" />.</summary>
    public static IEnumerable<string> PathDirsFrom(string? path, string? optIn)
        => optIn == "1"
            ? path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              ?? []
            : [];
}
