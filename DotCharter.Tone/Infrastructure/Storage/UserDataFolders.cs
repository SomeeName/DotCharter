namespace DotCharter.Tone.Infrastructure.Storage;

/// <summary>
///     The ONE place product code learns where the per-user AppData folders are.
/// Every path under <c>%APPDATA%</c> or <c>%LOCALAPPDATA%</c> — the
///     config and logs (<see cref="WritablePaths" />), the PSARC audio, stem,
///     waveform and album-art caches, downloaded models, the AI-match stores, the
///     Vulkan pipeline cache — starts from <see cref="RoamingAppData" /> or
///     <see cref="LocalAppData" />. <c>UserDataFolderLintTests</c> fails the gate on a
///     direct <c>Environment.GetFolderPath</c> of either folder anywhere else.
///     <para>
///         <see cref="Environment.GetFolderPath(Environment.SpecialFolder)" /> ignores
///         environment variables, so without this nothing could point a test or
///         measurement launch somewhere else: one could run against the
///         owner's real <c>%LOCALAPPDATA%\DotCharter</c>, where the one-time stem
///         re-keying pass renames his stem archives and the launch's cleanup
///         deletes them. Setting <see cref="DataRootVariable" /> to a folder moves
///         BOTH roots under it — <c>&lt;root&gt;\Roaming</c> and
///         <c>&lt;root&gt;\Local</c> — and makes <see cref="WritablePaths" /> ignore
///         a portable config beside the install. Unset, every path is exactly what
///         <c>GetFolderPath</c> returns.
///     </para>
/// </summary>
public static class UserDataFolders
{
    /// <summary>The variable every automated or measurement launch sets to a fresh temp folder.</summary>
    public const string DataRootVariable = "DOTCHARTER_DATA_ROOT";

    /// <summary>The stand-in for <c>%APPDATA%</c> under an override.</summary>
    public const string RoamingFolderName = "Roaming";

    /// <summary>The stand-in for <c>%LOCALAPPDATA%</c> under an override.</summary>
    public const string LocalFolderName = "Local";

    private static readonly AsyncLocal<string?> TestRoot = new();

    /// <summary>
    ///     The override root, full path, or null when the user's real folders are
    ///     in use. Read on every call: nothing caches a path here, so a test scope
    ///     (<see cref="UseDataRootForTesting" />) sees its own root.
    /// </summary>
    public static string? DataRoot => Normalize(TestRoot.Value ?? Environment.GetEnvironmentVariable(DataRootVariable));

    /// <summary>Whether <see cref="DataRootVariable" /> (or a test scope) redirects the user folders.</summary>
    public static bool IsRedirected => DataRoot != null;

    /// <summary>
    ///     Stands in for <c>GetFolderPath(SpecialFolder.ApplicationData)</c>: the
    ///     roaming profile, or <c>&lt;root&gt;\Roaming</c> (created) under an override.
    /// </summary>
    public static string RoamingAppData => Resolve(DataRoot, RoamingFolderName, Environment.SpecialFolder.ApplicationData);

    /// <summary>
    ///     Stands in for <c>GetFolderPath(SpecialFolder.LocalApplicationData)</c>: the
    ///     local profile, or <c>&lt;root&gt;\Local</c> (created) under an override.
    /// </summary>
    public static string LocalAppData => Resolve(DataRoot, LocalFolderName, Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    ///     The REAL <c>%LOCALAPPDATA%</c>, never redirected — ONLY for finding OTHER
    ///     programs' per-user installs (<c>Programs\Common\VST3</c>,
    ///     <c>Programs\OpenAI\Codex</c>). Read-only: nothing of ours is ever written
    ///     under it.
    /// </summary>
    public static string OtherProgramsLocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The startup log line when an override is active, else null.</summary>
    public static string? DescribeOverride() => DataRoot is { } root
        ? $"Data root override: {DataRootVariable}={root} (roaming data in {Path.Combine(root, RoamingFolderName)}, " +
          $"local data in {Path.Combine(root, LocalFolderName)}); the user's AppData folders are not used."
        : null;

    /// <summary>
    ///     Test-only: redirects both folders to <paramref name="root" /> for the
    ///     calling async flow (an <see cref="AsyncLocal{T}" />, so a parallel test
    ///     never sees it). Dispose to restore.
    /// </summary>
    public static IDisposable UseDataRootForTesting(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var previous = TestRoot.Value;
        TestRoot.Value = root;
        return new Restore(previous);
    }

    /// <summary>A usable override, or null: blank is "not set", a relative path is made absolute.</summary>
    internal static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim().Trim('"');
        return trimmed.Length == 0 ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
    }

    private static string Resolve(string? root, string folderName, Environment.SpecialFolder folder)
    {
        if (root == null) return Environment.GetFolderPath(folder);

        var redirected = Path.Combine(root, folderName);
        try
        {
            Directory.CreateDirectory(redirected);
        }
        catch (IOException)
        {
            // The caller creates its own subfolder and reports that failure; the
            // path stays under the override either way, never the real profile.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }

        return redirected;
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => TestRoot.Value = previous;
    }
}
