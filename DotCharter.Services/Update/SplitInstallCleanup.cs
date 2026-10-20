namespace DotCharter.Services.Update;

/// <summary>
///     Finishes the move of a v0.62-style FLAT install into the split layout
/// on the app's first start from <c>app/</c>.
///     <para>
///         <b>Why there is anything to finish.</b> The updater inside a flat
///         install copies the split payload over it WITHOUT purging (robocopy /E),
///         and its full-download path deletes nothing. So after the one-step
///         update the top folder holds the new launcher, <c>app/</c>,
///         <c>tools/</c> and <c>voices/</c> — and still every one of the ~390 files
///         the flat release had there, plus <see cref="LegacyUpdaterShim" />'s four.
///         The app now runs from <c>app/</c>, so none of them is loaded and all of
///         them are clutter: this removes them.
///     </para>
///     <para>
///         <b>What it may delete — three sources, never a directory walk's guess.</b>
///         A file at the top (outside <c>app/</c>) goes only when
///         (1) the OLD release's own manifest, still at the top because the split
///         payload keeps its manifest in <c>app/</c>, lists it; or
///         (2) <c>app/</c> holds a file at the same relative path — the flat
///         payload's copy of a file the app now carries; or
///         (3) it is one of the shim files.
///         And never when it is <see cref="PayloadReceiptFilter.IsLocalState" />
///         (on a portable install the top folder IS the config root — settings,
///         profiles, logs, the library index, credentials all live there), nor the
///         launcher, nor anything under <c>tools/</c> or <c>voices/</c>, nor a file
///         the NEW manifest lists at the top (the readme). A file nobody shipped —
///         a song the user dropped there — matches none of the three and stays.
///     </para>
/// </summary>
public static class SplitInstallCleanup
{
    /// <summary>Top-level folders the split layout itself owns, never cleaned.</summary>
    private static readonly string[] KeptTopFolders = [AppPayload.AppFolderName, "tools", "voices"];

    /// <summary>
    ///     Which top-level files to delete, as install-relative forward-slashed
    ///     paths. Pure.
    /// </summary>
    /// <param name="topFiles">Install-relative paths of the files OUTSIDE <c>app/</c>.</param>
    /// <param name="appHas">Whether <c>app/&lt;path&gt;</c> exists, for an install-relative path.</param>
    /// <param name="legacyManifest">The manifest at the top of the install (the old release's), if any.</param>
    /// <param name="appManifest">The manifest in <c>app/</c> (this release's), if any.</param>
    public static IReadOnlyList<string> Plan(
        IEnumerable<string> topFiles,
        Func<string, bool> appHas,
        PayloadManifest? legacyManifest,
        PayloadManifest? appManifest)
    {
        ArgumentNullException.ThrowIfNull(topFiles);
        ArgumentNullException.ThrowIfNull(appHas);

        // A v0.62 updater that took its PARTIAL path wrote the NEW manifest to the
        // top (it always writes the manifest at the payload root). That one lists
        // this release's own top-level files: it is the current manifest, not a
        // list of leftovers.
        var hasAppManifest = appManifest != null;
        if (legacyManifest?.IsSplit == true)
        {
            appManifest ??= legacyManifest;
            legacyManifest = null;
        }

        var legacy = new HashSet<string>(
            legacyManifest?.Files.Select(f => Normalize(f.Path)) ?? [], StringComparer.OrdinalIgnoreCase);
        var current = new HashSet<string>(
            appManifest?.Files.Select(f => Normalize(f.Path)) ?? [], StringComparer.OrdinalIgnoreCase);

        var deletions = new List<string>();
        foreach (var raw in topFiles)
        {
            var path = Normalize(raw);
            if (path.Length == 0 || IsKept(path)) continue;

            // Before every positive rule: a portable install's own data is never a
            // candidate, whatever a manifest or app/ says about the name.
            if (PayloadReceiptFilter.IsLocalState(path)) continue;

            if (LegacyUpdaterShim.IsShimPath(path))
            {
                if (appHas(path)) deletions.Add(path);
                continue;
            }

            // The top-level copy of the manifest: this release's lives in app/,
            // so once that one is there the old one has done its job (it is what
            // listed the files above).
            if (string.Equals(path, PayloadManifest.FileName, StringComparison.OrdinalIgnoreCase))
            {
                if (hasAppManifest) deletions.Add(path);
                continue;
            }

            if (current.Contains(path)) continue;

            if (legacy.Contains(path) || appHas(path)) deletions.Add(path);
        }

        deletions.Sort(StringComparer.OrdinalIgnoreCase);
        return deletions;
    }

    /// <summary>
    ///     Whether this start has anything to look at: the cheap probe that keeps
    ///     every start after the first at a handful of <c>File.Exists</c>.
    /// </summary>
    public static bool NeedsCleanup(string installRoot)
    {
        if (!AppPayload.IsSplitDirectory(installRoot)) return false;
        if (File.Exists(Path.Combine(installRoot, PayloadManifest.FileName))) return true;
        if (File.Exists(Path.Combine(installRoot, "DotCharter.dll"))) return true;
        return LegacyUpdaterShim.Files.Any(name => File.Exists(Path.Combine(installRoot, name)));
    }

    /// <summary>
    ///     Plans and applies the cleanup for the install at
    ///     <paramref name="installRoot" />. Best effort, file by file: a file that
    ///     cannot be deleted stays and is tried again next start. Returns how many
    ///     were removed.
    /// </summary>
    public static int Run(string installRoot, IAppLogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        if (!NeedsCleanup(installRoot)) return 0;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var appDir = Path.Combine(root, AppPayload.AppFolderName);
        var legacyPath = Path.Combine(root, PayloadManifest.FileName);
        var currentPath = Path.Combine(root, PayloadManifest.SplitInstalledPath);
        var legacy = PayloadManifest.FromJson(TryRead(legacyPath));
        var current = PayloadManifest.FromJson(TryRead(currentPath));

        // The partial-path case above, on disk: the top copy is this release's
        // manifest and app/ has none, so it moves to where the next update reads it.
        if (legacy?.IsSplit == true && current == null)
        {
            try
            {
                File.Move(legacyPath, currentPath);
                (current, legacy) = (legacy, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Info(AppLogCategory.Application,
                    $"Split-install cleanup: could not move the manifest into app ({ex.GetType().Name}).");
            }
        }

        var plan = Plan(EnumerateTopFiles(root, appDir, legacy), path => File.Exists(Path.Combine(appDir, path)),
            legacy, current);

        var removed = 0;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in plan)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                File.Delete(full);
                removed++;
                for (var dir = Path.GetDirectoryName(full);
                     dir != null && dir.Length > root.Length;
                     dir = Path.GetDirectoryName(dir))
                {
                    directories.Add(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Info(AppLogCategory.Application,
                    $"Split-install cleanup: '{relative}' stays for now ({ex.GetType().Name}).");
            }
        }

        // Folders the flat layout had (runtimes/, de-DE/, …), deepest first, and
        // only when the deletions emptied them.
        foreach (var dir in directories.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.Info(AppLogCategory.Application,
                    $"Split-install cleanup: folder '{dir}' stays ({ex.GetType().Name}).");
            }
        }

        logger?.Info(AppLogCategory.Application,
            $"Split-install cleanup: removed {removed} of {plan.Count} file(s) the flat layout left in '{root}'.");
        return removed;
    }

    /// <summary>
    ///     The files the plan looks at: every file directly in the top folder, and
    ///     the files under a top-level folder that either <c>app/</c> also has or
    ///     the old manifest names. User-data folders (profiles, logs, checkpoints…)
    ///     are not walked at all.
    /// </summary>
    private static IEnumerable<string> EnumerateTopFiles(string root, string appDir, PayloadManifest? legacy)
    {
        foreach (var file in Directory.EnumerateFiles(root))
            yield return Path.GetFileName(file);

        var legacyFolders = new HashSet<string>(
            legacy?.Files.Select(f => Normalize(f.Path)).Where(p => p.Contains('/')).Select(p => p[..p.IndexOf('/')]) ?? [],
            StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (KeptTopFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (PayloadReceiptFilter.IsLocalState(name + "/")) continue;
            if (!legacyFolders.Contains(name) && !Directory.Exists(Path.Combine(appDir, name))) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
        }
    }

    /// <summary>
    ///     A v0.62 install updated in one step to the split layout still has the flat
    ///     release's files at its top; the app runs from app\ now, so none is loaded.
    ///     Off the caller's thread, once: every later start is a few File.Exists probes
    ///     (<see cref="NeedsCleanup" />). Never throws.
    /// </summary>
    public static void StartAtStartup(IAppLogger? logger)
    {
        if (!InstallLocation.IsSplitInstall) return;
        var root = InstallLocation.InstallRoot;
        _ = Task.Run(() =>
        {
            try
            {
                Run(root, logger);
                logger?.Info(AppLogCategory.Application, DescribeLayout(root));
            }
            catch (Exception ex)
            {
                logger?.Warning(AppLogCategory.Application, "Split-install cleanup failed; it runs again next start.", ex);
            }
        });
    }

    /// <summary>
    ///     One log line saying where a split install found what lives at its top —
    ///     the first thing to read when a user reports "no guitar sound" or "import
    ///     says ffmpeg is missing" after the move. Resolved the way the app resolves
    ///     them: the voice folder the native store was pointed at, and the tools
    ///     through <c>MediaToolResolver</c> from the app's own folder.
    /// </summary>
    public static string DescribeLayout(string installRoot)
    {
        var voices = Environment.GetEnvironmentVariable(InstallLocation.VoiceDirectoryVariable);
        var packs = !string.IsNullOrEmpty(voices) && Directory.Exists(voices)
            ? Directory.GetFiles(voices, "*.dcvp").Length
            : 0;
        static string Tool(string name) => Import.Youtube.MediaToolResolver.Resolve(name, AppContext.BaseDirectory, null, [],
            File.Exists, OperatingSystem.IsWindows()) ?? "not found";
        return $"Split install at '{installRoot}': voices '{voices}' ({packs} pack(s)), " +
               $"ffmpeg '{Tool("ffmpeg")}', yt-dlp '{Tool("yt-dlp")}'.";
    }

    private static bool IsKept(string path)
    {
        if (string.Equals(path, AppPayload.ExeName, StringComparison.OrdinalIgnoreCase)) return true;
        var slash = path.IndexOf('/');
        return slash > 0 && KeptTopFolders.Contains(path[..slash], StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
