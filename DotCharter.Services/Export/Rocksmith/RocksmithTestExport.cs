using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DotCharter.Services.Export.Rocksmith;

/// <summary>Whether Rocksmith is running right now: the one fact a test export asks the machine.</summary>
public interface IRocksmithGameProbe
{
    bool IsRunning { get; }
}

/// <inheritdoc />
public sealed class RocksmithGameProbe : IRocksmithGameProbe
{
    /// <summary>DLC Builder's own probe: a process called <c>Rocksmith2014</c>.</summary>
    public bool IsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("Rocksmith2014");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
    }
}

/// <summary>
///     What a test export writes and what it removes, the way DLC Builder's
///     <c>TestPackageBuilder</c> decides it. The package is named after the lower-cased DLC
///     key and carries the version "test". While the game runs it cannot be replaced, so the
///     build is a NEW version: <c>_vN</c> on the file name, <c>vN</c> on the DLC key and
///     <c>" vN"</c> on the title (N is one past the newest <c>_vN</c> in the folder, or the
///     number of earlier test builds when none carries one). With the game closed, the
///     earlier test builds of the package are deleted first and the plain name is reused.
/// </summary>
public sealed record RocksmithTestPlan(string DlcKey, string FileStem, string VersionTag, IReadOnlyList<string> Superseded)
{
    /// <summary>The title after the plain one: empty for the plain build, <c>" v2"</c> for a versioned one.</summary>
    public string TitleSuffix => VersionTag.Length == 0 ? "" : " " + VersionTag;
}

/// <summary>Plans a test export. Pure: the folder's files and the running game are inputs.</summary>
public static partial class RocksmithTestExport
{
    /// <summary>The version a test build carries (<c>project.Version = "test"</c>).</summary>
    public const string Version = "test";

    /// <summary>The file name stem for a key: DLC Builder's <c>createPackageName</c>.</summary>
    public static string PackageName(string dlcKey) => dlcKey.ToLowerInvariant();

    /// <summary>
    ///     The plan for <paramref name="dlcKey" /> given the files already in the test folder
    ///     (full paths). Only files whose NAME starts with the package name count, as in DLC Builder.
    /// </summary>
    public static RocksmithTestPlan Plan(string dlcKey, IEnumerable<string> testFolderFiles, bool gameRunning)
    {
        var packageName = PackageName(dlcKey);
        var existing = testFolderFiles
            .Where(f => Path.GetFileName(f).StartsWith(packageName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!gameRunning) return new RocksmithTestPlan(dlcKey, packageName, "", existing);

        var numbered = existing
            .Select(f => VersionNumber().Match(Path.GetFileName(f)))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        var latest = numbered.Count == 0 ? existing.Count : numbered.Max();
        var tag = $"v{latest + 1}";
        return new RocksmithTestPlan(dlcKey + tag, $"{packageName}_{tag}", tag, []);
    }

    [GeneratedRegex(@"_v(\d+)")]
    private static partial Regex VersionNumber();
}
