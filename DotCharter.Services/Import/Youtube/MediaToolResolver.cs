namespace DotCharter.Services.Import.Youtube;

/// <summary>
///     Locates a bundled/installed command-line media tool (yt-dlp, ffmpeg).
///     Resolution order: an explicit configured path, then next to the app
///     executable (the bundled copy), then the app's <c>tools/</c> subfolder
///     (where releases ship ffmpeg/ffprobe/yt-dlp), then each directory on PATH.
///     Pure and unit-testable — the filesystem probe is injected.
/// </summary>
public static class MediaToolResolver
{
    /// <summary>
    ///     Resolves <paramref name="baseName" /> (e.g. "yt-dlp", "ffmpeg") to a
    ///     full path, or null when not found. On Windows the ".exe" variant is
    ///     tried first. Precedence: configured path → exe dir → exe dir/<c>tools</c>
    ///     → PATH (the <c>tools</c> subfolder is the canonical release bundle
    ///     location; an exe-dir copy still wins over it, and both win over PATH).
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

        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var name in fileNames)
            {
                var candidate = Path.Combine(dir, name);
                if (fileExists(candidate)) return candidate;
            }
        }

        // Dev-tree last resort: running from bin/Debug there is no bundled
        // copy, so silence baking / tag extraction silently degraded on dev
        // runs (owner report). The repo's release-packer cache
        // (tools/redist/win-x64) has the tools — walk up from the exe dir.
        // Releases never get here: their bundled tools/ copy wins above.
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

        return null;
    }

    /// <summary>The directories on the current process PATH.</summary>
    public static IEnumerable<string> CurrentPathDirs()
        => (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
