namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
/// Resolves filesystem paths for app config / keybinds / logs with an
/// automatic fallback when the application directory is not writable
/// (Program Files install, MSIX-virtualised location, network share with
/// no write permission, etc.).
///
/// Strategy per file:
///   0. When <see cref="UserDataFolders.DataRootVariable"/> redirects the user
/// folders (test and measurement launches), everything resolves under
///      <see cref="UserDataFolders.RoamingAppData"/> and a portable copy beside the
///      install is ignored: such a launch never reads or writes the user's state.
///   1. If a copy already exists in the install's top folder AND that location
///      is still writable, keep using it (back-compat with portable installs).
///      That folder is <see cref="InstallLocation.InstallRoot"/>: the exe's own
/// folder on a flat install, the launcher's on a split one.
///   2. Otherwise resolve to <c>%APPDATA%\DotCharter\</c> on Windows or
///      the XDG / equivalent user-config dir on other OSes
///      (<see cref="Environment.SpecialFolder.ApplicationData"/> →
///      <c>~/.config/DotCharter/</c> on Linux,
///      <c>~/Library/Application Support/DotCharter/</c> on macOS).
///   3. As a last-ditch fallback if even AppData isn't usable, return the
///      path inside <see cref="Path.GetTempPath"/>.
///
/// Logs go to a <c>logs/</c> subfolder of the same root.
///
/// All resolution is cached after first probe so we don't hammer the disk
/// with access checks on every save.
/// </summary>
public static class WritablePaths
{
    private static string _appFolderName = "DotCharter";

    private static readonly object Sync = new();

    /// <summary>
    ///     Renames the per-user folder (<c>%APPDATA%\&lt;name&gt;</c>, the temp
    ///     fallback, the logs) for a host that is not DotCharter — the standalone
    ///     rig passes <c>"DotCharter RigLab"</c> so its settings and logs never
    ///     land in, or read from, DotCharter's. Call it first thing in
    ///     <c>Main</c>: it THROWS once a root has been resolved, because a path
    ///     handed out under the old name would silently keep being used.
    /// </summary>
    public static void UseAppFolderName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (Sync)
        {
            if (_cachedConfigRoot != null || _cachedLogRoot != null)
            {
                throw new InvalidOperationException(
                    "WritablePaths already resolved a root; set the app folder name before anything asks for one.");
            }

            _appFolderName = name;
        }
    }
    private static string? _cachedConfigRoot;
    private static string? _cachedLogRoot;
    private static string? _overrideConfigRoot;
    private static string? _overrideLogRoot;

    /// <summary>
    /// Test-only hook: forces the config / log roots to specific directories
    /// (skipping the probe + AppData fallback chain). Pass <c>null</c> to
    /// restore normal resolution.
    /// </summary>
    public static void OverrideRootsForTesting(string? configRoot, string? logRoot)
    {
        lock (Sync)
        {
            _overrideConfigRoot = configRoot;
            _overrideLogRoot = logRoot;
            _cachedConfigRoot = null;
            _cachedLogRoot = null;
        }
    }

    /// <summary>
    /// Test-only hook: claims the config / log roots, but ONLY if no
    /// <see cref="OverrideRootsForTesting"/> is currently in force. Returns
    /// <c>false</c> when one is, and changes nothing.
    /// <para>
    /// This exists for the headless test session, which boots the REAL app —
    /// and therefore reads (and can write) the developer's live
    /// <c>%APPDATA%\DotCharter\appconfig.json</c> unless it is pointed
    /// somewhere else. It cannot use the unconditional setter above: the
    /// session is created lazily, from a collection that runs in parallel with
    /// the two classes which own that setter, so an unconditional write could
    /// yank the roots out from under one of them mid-test. The check and the
    /// write happen together under <see cref="Sync"/>, so "claim it only if
    /// nobody else has" is atomic.
    /// </para>
    /// </summary>
    public static bool TryClaimRootsForTesting(string configRoot, string logRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(logRoot);

        lock (Sync)
        {
            if (_overrideConfigRoot != null || _overrideLogRoot != null) return false;
            _overrideConfigRoot = configRoot;
            _overrideLogRoot = logRoot;
            _cachedConfigRoot = null;
            _cachedLogRoot = null;
            return true;
        }
    }

    /// <summary>
    /// Directory where appconfig.json / keybinds.json should live.
    /// Created on first access if it doesn't exist.
    /// </summary>
    public static string ConfigRoot
    {
        get
        {
            if (_cachedConfigRoot != null) return _cachedConfigRoot;
            lock (Sync)
            {
                _cachedConfigRoot ??= ResolveConfigRoot();
                return _cachedConfigRoot;
            }
        }
    }

    /// <summary>
    /// Directory for log files. Same precedence as <see cref="ConfigRoot"/>
    /// but rooted under a <c>logs/</c> subfolder of the writable user dir.
    /// </summary>
    public static string LogRoot
    {
        get
        {
            if (_cachedLogRoot != null) return _cachedLogRoot;
            lock (Sync)
            {
                _cachedLogRoot ??= ResolveLogRoot();
                return _cachedLogRoot;
            }
        }
    }

    /// <summary>
    /// Resolves a config file path for <paramref name="fileName"/> (e.g.
    /// "appconfig.json"). Honors any pre-existing portable copy in the app
    /// directory so users with a portable install don't suddenly lose their
    /// settings.
    /// </summary>
    public static string ResolveConfigFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            // ReSharper disable once LocalizableElement -- developer-facing argument validation.
            throw new ArgumentException("File name must not be empty.", nameof(fileName));

        if (_overrideConfigRoot != null || UserDataFolders.IsRedirected)
            return Path.Combine(ConfigRoot, fileName);

        // Honor an existing portable file in the app directory if it is
        // still readable; otherwise fall through to the user dir.
        // The install root, not the exe's folder: a split install runs
        // from app\ and keeps its portable state in the folder above it.
        var portable = Path.Combine(InstallLocation.InstallRoot, fileName);
        if (File.Exists(portable) && IsDirectoryWritable(InstallLocation.InstallRoot))
            return portable;

        return Path.Combine(ConfigRoot, fileName);
    }

    /// <summary>
    /// Resolves a log file path for <paramref name="configuredPath"/> (typically
    /// the value from AppConfig.LogFilePath). If the configured path is absolute
    /// and writable, that's used as-is. If it's a relative path under "logs/"
    /// (the default), it's rooted under <see cref="LogRoot"/>.
    /// </summary>
    public static string ResolveLogFile(string configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? "logs/dotcharter.log" : configuredPath;

        if (Path.IsPathRooted(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && (Directory.Exists(dir) || TryCreateDirectory(dir))
                                          && IsDirectoryWritable(dir))
            {
                return path;
            }
            // Absolute path is unusable — strip back to a filename and reroot.
            path = Path.GetFileName(path);
            if (string.IsNullOrEmpty(path)) path = "dotcharter.log";
        }
        else
        {
            // For relative paths, only keep the filename so we always land
            // inside our writable LogRoot. (The default "logs/dotcharter.log"
            // becomes "<LogRoot>/dotcharter.log".)
            path = Path.GetFileName(path);
            if (string.IsNullOrEmpty(path)) path = "dotcharter.log";
        }

        return Path.Combine(LogRoot, path);
    }

    private static string ResolveConfigRoot()
    {
        if (_overrideConfigRoot != null)
        {
            TryCreateDirectory(_overrideConfigRoot);
            return _overrideConfigRoot;
        }

        if (RedirectedConfigRoot() is { } redirected) return redirected;

        // Honor a portable install where the user has an existing
        // appconfig.json next to the exe AND can still write there.
        // The INSTALL root: a split install's portable config sits beside
        // the launcher, exactly where a v0.62 flat install kept it.
        var appDir = InstallLocation.InstallRoot;
        if (File.Exists(Path.Combine(appDir, "appconfig.json")) && IsDirectoryWritable(appDir))
            return appDir;

        var userDir = GetUserAppDir();
        if (TryCreateDirectory(userDir) && IsDirectoryWritable(userDir))
            return userDir;

        // Last-ditch: app dir if writable, otherwise temp.
        if (IsDirectoryWritable(appDir)) return appDir;

        var tempDir = Path.Combine(Path.GetTempPath(), _appFolderName);
        TryCreateDirectory(tempDir);
        return tempDir;
    }

    private static string ResolveLogRoot()
    {
        if (_overrideLogRoot != null)
        {
            TryCreateDirectory(_overrideLogRoot);
            return _overrideLogRoot;
        }

        if (RedirectedLogRoot() is { } redirected) return redirected;

        var appDir = InstallLocation.InstallRoot;
        var portableConfig = File.Exists(Path.Combine(appDir, "appconfig.json")) && IsDirectoryWritable(appDir);
        return ResolveLogRootFor(portableConfig, appDir, GetUserAppDir(),
            Path.Combine(Path.GetTempPath(), _appFolderName, "logs"),
            Directory.Exists, dir => TryCreateDirectory(dir) && IsDirectoryWritable(dir));
    }

    /// <summary>
    ///     The log-root rule, pure. Logs live under "&lt;root&gt;/logs" of the
    ///     SAME root the config resolved to: a PORTABLE install (an
    ///     <c>appconfig.json</c> beside the exe) keeps its logs beside the exe
    ///     too. A pre-existing <c>logs/</c> folder is not required
    ///     there; otherwise a portable install with no such folder — every fresh
    ///     staging smoke-run of a release, and every user who drops a config
    ///     beside the exe — would write its logs into the roaming profile instead,
    ///     which is exactly what "portable" is supposed to prevent.
    /// </summary>
    /// <param name="portableConfig">Whether the config root is the app dir.</param>
    /// <param name="appDir">The directory the exe runs from.</param>
    /// <param name="userAppDir">The per-user roaming app folder.</param>
    /// <param name="tempLogs">The last-ditch temp folder.</param>
    /// <param name="exists">Whether a directory already exists.</param>
    /// <param name="usable">Creates the directory if needed and reports whether
    /// it is writable.</param>
    internal static string ResolveLogRootFor(bool portableConfig, string appDir, string userAppDir, string tempLogs,
        Func<string, bool> exists, Func<string, bool> usable)
    {
        var portable = Path.Combine(appDir, "logs");
        if (portableConfig && usable(portable)) return portable;
        if (exists(portable) && usable(portable)) return portable;

        var userLogs = Path.Combine(userAppDir, "logs");
        if (usable(userLogs)) return userLogs;

        if (usable(portable)) return portable;

        usable(tempLogs);
        return tempLogs;
    }

    /// <summary>
    /// The config root of a launch whose user folders are redirected, or
    ///     null when they are not. Such a launch never falls back to a portable
    ///     config, the install folder or temp: its state lives under the override,
    ///     full stop. Uncached and free of the test overrides above, so the data-root
    ///     test reads it without touching the roots other classes own.
    /// </summary>
    internal static string? RedirectedConfigRoot()
    {
        if (!UserDataFolders.IsRedirected) return null;
        var root = GetUserAppDir();
        TryCreateDirectory(root);
        return root;
    }

    /// <summary>The log root under a data-root redirect, or null; see <see cref="RedirectedConfigRoot" />.</summary>
    internal static string? RedirectedLogRoot()
    {
        if (!UserDataFolders.IsRedirected) return null;
        var root = Path.Combine(GetUserAppDir(), "logs");
        TryCreateDirectory(root);
        return root;
    }

    /// <summary>The per-user app folder (<c>%APPDATA%\&lt;name&gt;</c>, or its stand-in under a data-root override).</summary>
    internal static string GetUserAppDir()
    {
        // Environment.SpecialFolder.ApplicationData == Roaming AppData on
        // Windows (most user-installed apps put their config there);
        // on Linux it maps to $XDG_CONFIG_HOME (~/.config), and on macOS to
        // ~/Library/Application Support. All three are correct conventions
        // for per-user app data, so we use the same call cross-platform.
        // Through UserDataFolders, so DOTCHARTER_DATA_ROOT can redirect it.
        var root = UserDataFolders.RoamingAppData;
        if (string.IsNullOrWhiteSpace(root))
            root = UserDataFolders.LocalAppData;
        if (string.IsNullOrWhiteSpace(root))
            root = Path.GetTempPath();

        return Path.Combine(root, _appFolderName);
    }

    private static bool TryCreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDirectoryWritable(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;

            // Probe with a short-lived hidden file. We can't trust ACL inspection
            // alone (Program Files virtualisation, sandboxed installs, mounted
            // network shares with stale permissions all lie), so the only
            // reliable test is "can we actually create a file here?".
            var probe = Path.Combine(path, $".dc-write-probe-{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.DeleteOnClose))
            {
                fs.WriteByte(0);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
