using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DotCharter.Tone.Audio.Tonelab;

/// <summary>Where a registry value is read from; the locator asks for exactly these two.</summary>
public enum GameLocatorRegistryRoot
{
    /// <summary><c>HKCU</c>, the default view.</summary>
    CurrentUser,

    /// <summary><c>HKLM</c>, the 32-bit view (where Steam writes its uninstall keys).</summary>
    LocalMachine32
}

/// <summary>The machine as the locator sees it: environment, registry and file system. Read only; a test hands in a fake.</summary>
public interface IGameLocatorSystem
{
    string? EnvironmentVariable(string name);

    string? RegistryString(GameLocatorRegistryRoot root, string subKey, string valueName);

    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>The whole text of the file, or null when it cannot be read.</summary>
    string? ReadAllText(string path);
}

/// <summary>Which place the game folder came from, or why none was found.</summary>
public sealed record GameFolderFinding(string? Directory, string Source, string Reason)
{
    public static GameFolderFinding Found(string directory, string source) => new(directory, source, "");

    public static GameFolderFinding NotFound(string reason) => new(null, "", reason);
}

/// <summary>
///     Finds the Rocksmith 2014 folder without assuming Steam wrote its uninstall key (it does not always):
///     the configured folder (<c>DOTCHARTER_RS2014_DIR</c>) first, then Steam's own library data
///     (<c>HKCU\Software\Valve\Steam\SteamPath</c>, <c>steamapps\libraryfolders.vdf</c>, the library holding
///     <c>appmanifest_221680.acf</c>, <c>steamapps\common\&lt;installdir&gt;</c>), then the uninstall key. Pure over
///     <see cref="IGameLocatorSystem" />.
/// </summary>
public static partial class RocksmithGameLocator
{
    public const string ConfiguredFolderVariable = "DOTCHARTER_RS2014_DIR";
    public const string ExeName = RocksmithGame.ProcessName + ".exe";

    private const string SteamKey = @"Software\Valve\Steam";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + RocksmithGame.SteamAppId;
    private const string ManifestName = "appmanifest_" + RocksmithGame.SteamAppId + ".acf";

    public static GameFolderFinding Find(IGameLocatorSystem system)
    {
        if (system.EnvironmentVariable(ConfiguredFolderVariable) is { Length: > 0 } configured && HasGame(system, configured))
            return GameFolderFinding.Found(configured, ConfiguredFolderVariable);

        if (FromSteamLibraries(system) is { } steamDir) return GameFolderFinding.Found(steamDir, "Steam library");

        var uninstall = system.RegistryString(GameLocatorRegistryRoot.LocalMachine32, UninstallKey, "InstallLocation");
        if (uninstall is { Length: > 0 } && system.DirectoryExists(uninstall)) return GameFolderFinding.Found(uninstall, "Steam uninstall key");

        return GameFolderFinding.NotFound("no configured folder, no Steam library with Rocksmith 2014 and no uninstall key");
    }

    /// <summary>The <c>"path"</c> values of a <c>libraryfolders.vdf</c>, unescaped; a tolerant read that ignores everything else.</summary>
    public static IReadOnlyList<string> LibraryPaths(string vdf) =>
        [.. PathPattern().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal))];

    /// <summary>The <c>installdir</c> of an <c>appmanifest_*.acf</c>, or null.</summary>
    public static string? InstallDir(string manifest) =>
        InstallDirPattern().Match(manifest) is { Success: true } m && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;

    private static string? FromSteamLibraries(IGameLocatorSystem system)
    {
        if (system.RegistryString(GameLocatorRegistryRoot.CurrentUser, SteamKey, "SteamPath") is not { Length: > 0 } steamPath) return null;
        var libraries = new List<string> { steamPath.Replace('/', Path.DirectorySeparatorChar) };
        if (system.ReadAllText(Path.Combine(libraries[0], "steamapps", "libraryfolders.vdf")) is { } vdf)
            libraries.AddRange(LibraryPaths(vdf).Select(p => p.Replace('/', Path.DirectorySeparatorChar)));

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (system.ReadAllText(Path.Combine(steamapps, ManifestName)) is not { } manifest || InstallDir(manifest) is not { } installDir) continue;
            var candidate = Path.Combine(steamapps, "common", installDir);
            if (HasGame(system, candidate)) return candidate;
        }

        return null;
    }

    private static bool HasGame(IGameLocatorSystem system, string directory) =>
        system.DirectoryExists(directory) && system.FileExists(Path.Combine(directory, ExeName));

    [GeneratedRegex("\"path\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathPattern();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirPattern();
}

/// <summary>The real machine; Windows only (elsewhere the registry answers null).</summary>
public sealed class WindowsGameLocatorSystem : IGameLocatorSystem
{
    public static WindowsGameLocatorSystem Instance { get; } = new();

    public string? EnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string? RegistryString(GameLocatorRegistryRoot root, string subKey, string valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var hive = root == GameLocatorRegistryRoot.CurrentUser
                ? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
                : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public string? ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
