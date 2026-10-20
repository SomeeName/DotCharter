using System.Text.RegularExpressions;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     #753: everything third-party the app ships, downloads or keeps in the
///     repository has an entry in THIRD-PARTY-NOTICES.md, so a new one without a
///     notice fails the gate instead of shipping silently. Four inventories are read
///     from where they are DECLARED, never from a copy: every <c>PackageReference</c>
///     in the DotCharter, JuceSharp and tool projects; every folder of a DotCharter
///     project that holds a font, picture, model, capture, archive, binary or audio
///     file; every tool New-Release.ps1 copies into the install's tools folder; and
///     every model file the app can download. Each must appear in the notices in
///     backticks (`Name`, a folder as `Path/To/Folder/`).
/// </summary>
public sealed partial class ThirdPartyNoticeCoverageTests
{
    private const string NoticesFileName = "THIRD-PARTY-NOTICES.md";

    /// <summary>A data file this size or bigger counts as an asset whatever its extension
    /// (tone_gear_data.json); smaller JSON is configuration.</summary>
    private const long LargeDataFileBytes = 64 * 1024;

    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nam", ".aidax", ".ttf", ".otf", ".woff", ".woff2", ".png", ".webp", ".jpg", ".jpeg", ".gif", ".ico", ".svg",
        ".zip", ".bin", ".onnx", ".ogg", ".wem", ".wav", ".mp3", ".flac", ".opus", ".psarc", ".dcvp", ".dll", ".exe",
        ".so", ".dylib", ".spv", ".npy", ".pt", ".gp", ".gp5", ".gpx", ".mid", ".midi"
    };

    /// <summary>Build output (managed and native), the ignored corpus junction, test-run dumps,
    /// the release tool cache and dot-folders: generated or local, never committed.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "build", "_deps", "TestData", "TestResults", "redist"
    };

    private static string Root => TestPaths.RepoRoot();

    private static string Notices() => File.ReadAllText(Path.Combine(Root, NoticesFileName));

    /// <summary>The names <paramref name="notices" /> does not carry in backticks, sorted.</summary>
    internal static IReadOnlyList<string> Missing(string notices, IEnumerable<string> names) =>
        names.Distinct(StringComparer.Ordinal)
            .Where(name => !notices.Contains($"`{name}`", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Missing_names_exactly_what_the_notices_do_not_list()
    {
        const string notices = "| `Avalonia` | MIT |\n| `DotCharter.UI/NamCaptures/` | §2.6 |\nffmpeg.exe without backticks";

        var missing = Missing(notices, ["Avalonia", "Avalonia.Desktop", "DotCharter.UI/NamCaptures/", "ffmpeg.exe"]);

        Assert.Equal(["Avalonia.Desktop", "ffmpeg.exe"], missing);
    }

    [Fact]
    public void Every_NuGet_package_has_a_notice()
    {
        var packages = PackageReferences().ToList();
        Assert.True(packages.Count >= 30, $"Read only {packages.Count} package ids — the project scan is broken.");

        var missing = Missing(Notices(), packages);
        Assert.True(missing.Count == 0,
            $"{NoticesFileName} §2.3 has no entry for: {string.Join(", ", missing)}. Add each with its licence and source.");
    }

    [Fact]
    public void Every_bundled_asset_folder_has_a_notice()
    {
        var folders = AssetFolders().ToList();
        Assert.True(folders.Count >= 10, $"Found only {folders.Count} asset folders — the scan is broken.");

        var missing = Missing(Notices(), folders);
        Assert.True(missing.Count == 0,
            $"{NoticesFileName} §2.12 does not list: {string.Join(", ", missing)}. Say where the files come from and " +
            "under which licence, then add the folder to the inventory.");
    }

    [Fact]
    public void Every_tool_the_release_bundles_has_a_notice()
    {
        var script = File.ReadAllText(Path.Combine(Root, "tools", "release", "New-Release.ps1"));
        var tonelab = TonelabFiles().Match(script);
        Assert.True(tonelab.Success, "New-Release.ps1 no longer lists $TonelabFiles.");
        var tools = BundledTool().Matches(script).Select(m => m.Groups[1].Value)
            .Concat(QuotedBinary().Matches(tonelab.Groups[1].Value).Select(m => m.Groups[1].Value))
            .ToList();
        Assert.Contains("ffmpeg.exe", tools);
        Assert.Contains("yt-dlp.exe", tools);
        Assert.Contains("tonelab.dll", tools);
        Assert.Contains("tonelab-launch.exe", tools);

        var missing = Missing(Notices(), tools);
        Assert.True(missing.Count == 0, $"{NoticesFileName} §2.4 has no entry for: {string.Join(", ", missing)}.");
    }

    [Fact]
    public void Every_downloadable_model_has_a_notice()
    {
        var models = StemModelCatalog.BuiltIn.Select(m => m.FileName)
            .Concat([LyricModelCatalog.MultilingualDownload.AssetName, LyricModelCatalog.EnglishDownload.AssetName])
            .Concat(RigEstimatorModelCatalog.All.Select(d => d.AssetName))
            .ToList();
        Assert.True(models.Count >= 12, $"Read only {models.Count} model files — a catalogue moved.");

        var missing = Missing(Notices(), models);
        Assert.True(missing.Count == 0,
            $"{NoticesFileName} §2.9 has no entry for: {string.Join(", ", missing)}. Name where it is downloaded " +
            "from and its weights' licence (or that it is unknown).");
    }

    /// <summary>
    ///     #757, #767: every voice pack the release ships (<c>$VoicePackFiles</c>) is
    ///     named in the notices, and the notices carry no entry for the dropped
    ///     non-commercial pack.
    /// </summary>
    [Fact]
    public void Every_voice_pack_has_a_notice_and_the_dropped_non_commercial_pack_has_none()
    {
        var script = File.ReadAllText(Path.Combine(Root, "tools", "release", "New-Release.ps1"));
        var shipped = PowerShellList(script, "$VoicePackFiles");
        Assert.True(shipped.Count >= 8, $"Read {shipped.Count} pack files — the release script moved.");

        var missing = Missing(Notices(), shipped.Where(f => f.EndsWith(".dcvp", StringComparison.Ordinal)));
        Assert.True(missing.Count == 0, $"{NoticesFileName} §2.2.1 has no entry for: {string.Join(", ", missing)}.");

        foreach (var gone in new[] { "Rickenbacker", "Project16", "CC BY-NC-SA", "voices-nc" })
        {
            Assert.DoesNotContain(gone, Notices(), StringComparison.Ordinal);
        }
    }

    /// <summary>The single-quoted items of the PowerShell array assigned to <paramref name="variable" />.</summary>
    private static List<string> PowerShellList(string script, string variable)
    {
        var start = script.IndexOf(variable + " = @(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"New-Release.ps1 no longer assigns {variable}.");
        var body = script[start..script.IndexOf(')', start)];
        return Regex.Matches(body, "'([^']+)'").Select(m => m.Groups[1].Value).ToList();
    }

    private static IEnumerable<string> PackageReferences()
    {
        var projects = Directory.EnumerateDirectories(Root, "DotCharter*")
            .Concat(new[] { Path.Combine(Root, "tools"), Path.Combine(Root, "JuceSharp", "src") }.Where(Directory.Exists))
            .SelectMany(dir => SourceFiles(dir).Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            .Concat(new[] { Path.Combine(Root, "Directory.Build.props"), Path.Combine(Root, "JuceSharp", "Directory.Build.props") }
                .Where(File.Exists));

        return projects.SelectMany(p => PackageReference().Matches(File.ReadAllText(p)).Select(m => m.Groups[1].Value));
    }

    private static IEnumerable<string> AssetFolders() =>
        Directory.EnumerateDirectories(Root, "DotCharter*")
            .SelectMany(SourceFiles)
            .Where(IsAsset)
            .Select(f => Path.GetRelativePath(Root, Path.GetDirectoryName(f)!).Replace('\\', '/') + "/")
            .Distinct(StringComparer.Ordinal);

    private static bool IsAsset(string file) =>
        AssetExtensions.Contains(Path.GetExtension(file)) ||
        (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length >= LargeDataFileBytes);

    /// <summary>Every file under <paramref name="dir" />, skipping <see cref="SkippedFolders" /> and dot-folders.</summary>
    private static IEnumerable<string> SourceFiles(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            yield return file;

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (SkippedFolders.Contains(name) || name.StartsWith('.')) continue;
            foreach (var file in SourceFiles(sub))
                yield return file;
        }
    }

    [GeneratedRegex("""<PackageReference\s+Include="([^"]+)"[^>]*>""")]
    private static partial Regex PackageReference();

    [GeneratedRegex("""Join-Path \$toolsDir '([^']+\.exe)'""")]
    private static partial Regex BundledTool();

    [GeneratedRegex(@"\$TonelabFiles\s*=\s*@\(([^)]*)\)")]
    private static partial Regex TonelabFiles();

    [GeneratedRegex(@"'([^']+\.(?:exe|dll))'")]
    private static partial Regex QuotedBinary();
}
