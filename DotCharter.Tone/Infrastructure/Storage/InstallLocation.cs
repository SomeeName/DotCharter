namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
/// Where this install is, as opposed to where this exe is.
///     <para>
///         A release is SPLIT: the top folder holds only the launcher
///         (<c>DotCharter.exe</c>), <c>voices/</c>, <c>tools/</c> and a readme, and
///         the app with its ~390 files runs from <c>app/</c>. So
///         <see cref="AppContext.BaseDirectory" /> is <c>&lt;install&gt;\app</c>, and
///         everything that belongs to the INSTALL rather than to the binaries — the
///         portable <c>appconfig.json</c> and every other piece of user state beside
///         it, the bundled media tools, the voice packs, downloaded models — is
///         resolved from <see cref="InstallRoot" /> instead. A v0.62 install keeps
///         its portable config, logs and profiles in its top folder; that is where
///         they stay.
///     </para>
///     <para>
///         A flat folder (a dev build, the standalone rig, any pre-0.63 release) is
///         its own install root, so every caller behaves exactly as before there.
///     </para>
/// </summary>
public static class InstallLocation
{
    /// <summary>The folder a split install keeps the app in.</summary>
    public const string AppFolderName = "app";

    /// <summary>The launcher at the top of a split install, and the app's own exe inside <see cref="AppFolderName" />.</summary>
    public const string ExeName = "DotCharter.exe";

    /// <summary>The variable the native voice store reads before it looks beside <c>juce_sharp.dll</c>.</summary>
    public const string VoiceDirectoryVariable = "DOTCHARTER_VOICE_DIR";

    /// <summary>The voice packs' folder at the top of an install.</summary>
    public const string VoicesFolderName = "voices";

    private static readonly Lazy<string> Root = new(() => ResolveInstallRoot(AppContext.BaseDirectory, File.Exists));

    /// <summary>The folder the app's binaries are in (<see cref="AppContext.BaseDirectory" />, no trailing separator).</summary>
    public static string AppDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>The install's top folder: the launcher's folder on a split install, else <see cref="AppDirectory" />.</summary>
    public static string InstallRoot => Root.Value;

    /// <summary>Whether this process runs from the <c>app/</c> folder of a split install.</summary>
    public static bool IsSplitInstall =>
        !string.Equals(InstallRoot, AppDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     The install root for an app running from <paramref name="appDirectory" />.
    ///     It is the PARENT only when the folder is called <c>app</c>, the app's
    ///     exe is in it and the launcher is beside it — a shape nothing else on a
    ///     machine has by accident. Pure: the probe is injected.
    /// </summary>
    public static string ResolveInstallRoot(string appDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(appDirectory)) return appDirectory;

        var app = Path.TrimEndingDirectorySeparator(appDirectory);
        var parent = Path.GetDirectoryName(app);
        if (string.IsNullOrEmpty(parent) ||
            !string.Equals(Path.GetFileName(app), AppFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return app;
        }

        return fileExists(Path.Combine(app, ExeName)) && fileExists(Path.Combine(parent, ExeName)) ? parent : app;
    }

    /// <summary>
    ///     The voice folder to export in <see cref="VoiceDirectoryVariable" />, or
    ///     null to leave the variable alone. The native store looks beside its own
    ///     module (<c>app\voices</c>) unless told otherwise, and a split install
    ///     keeps the packs at its top. A value the user (or a test) already set
    ///     wins. Pure.
    /// </summary>
    public static string? VoiceDirectoryToExport(string appDirectory, string installRoot, string? currentValue)
    {
        if (!string.IsNullOrWhiteSpace(currentValue)) return null;
        if (string.Equals(Path.TrimEndingDirectorySeparator(appDirectory), Path.TrimEndingDirectorySeparator(installRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(installRoot, VoicesFolderName);
    }

    /// <summary>
    ///     Points the native voice store at the install's <c>voices/</c>. Call it
    ///     before anything loads <c>juce_sharp.dll</c>; the store reads the
    ///     variable through <c>GetEnvironmentVariableW</c>, so setting it in-process
    ///     is enough.
    /// </summary>
    public static void ExportVoiceDirectory()
    {
        var value = VoiceDirectoryToExport(AppDirectory, InstallRoot,
            Environment.GetEnvironmentVariable(VoiceDirectoryVariable));
        if (value != null) Environment.SetEnvironmentVariable(VoiceDirectoryVariable, value);
    }
}
