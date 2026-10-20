using DotCharter.Services.Import.Youtube;

namespace DotCharter.Tests.Services.Import.Youtube;

/// <summary>
///     Resolution order for a bundled/installed media tool: configured path →
///     exe dir → exe dir/<c>tools</c> → PATH, with the Windows ".exe" variant
///     tried first. The filesystem probe is injected so these stay pure.
/// </summary>
public sealed class MediaToolResolverTests
{
    private static string? Resolve(string baseName, string? exeDir, string? configured,
        string[] pathDirs, HashSet<string> existing, bool isWindows = true)
        => MediaToolResolver.Resolve(baseName, exeDir, configured, pathDirs,
            p => existing.Contains(p), isWindows);

    [Fact]
    public void ConfiguredPath_WinsWhenItExists()
    {
        var existing = new HashSet<string> { @"C:\tools\yt-dlp.exe", @"C:\app\yt-dlp.exe" };
        var result = Resolve("yt-dlp", @"C:\app", @"C:\tools\yt-dlp.exe", [], existing);
        Assert.Equal(@"C:\tools\yt-dlp.exe", result);
    }

    [Fact]
    public void ConfiguredPath_IgnoredWhenMissing_FallsToExeDir()
    {
        var existing = new HashSet<string> { @"C:\app\yt-dlp.exe" };
        var result = Resolve("yt-dlp", @"C:\app", @"C:\tools\gone.exe", [], existing);
        Assert.Equal(@"C:\app\yt-dlp.exe", result);
    }

    [Fact]
    public void BundledExeDir_PreferredOverPath()
    {
        var existing = new HashSet<string> { @"C:\app\ffmpeg.exe", @"C:\other\ffmpeg.exe" };
        var result = Resolve("ffmpeg", @"C:\app", null, [@"C:\other"], existing);
        Assert.Equal(@"C:\app\ffmpeg.exe", result);
    }

    [Fact]
    public void FoundOnPath_WhenNotBundled()
    {
        var existing = new HashSet<string> { @"C:\bin\yt-dlp.exe" };
        var result = Resolve("yt-dlp", @"C:\app", null, [@"C:\nope", @"C:\bin"], existing);
        Assert.Equal(@"C:\bin\yt-dlp.exe", result);
    }

    [Fact]
    public void RepoRedistCache_IsTheDevRunLastResort()
    {
        // Dev runs (bin/Debug) have no bundled tools; the repo's release-packer
        // cache several levels up must be found so silence baking and tag
        // extraction work without a release install.
        var existing = new HashSet<string> { @"C:\repo\tools\redist\win-x64\ffmpeg.exe" };
        var result = Resolve("ffmpeg",
            @"C:\repo\DotCharter.UI\bin\Debug\net10.0", null, [@"C:\nope"], existing);
        Assert.Equal(@"C:\repo\tools\redist\win-x64\ffmpeg.exe", result);
    }

    [Fact]
    public void Path_WinsOver_RepoRedistCache()
    {
        var existing = new HashSet<string>
        {
            @"C:\bin\ffmpeg.exe",
            @"C:\repo\tools\redist\win-x64\ffmpeg.exe"
        };
        var result = Resolve("ffmpeg",
            @"C:\repo\DotCharter.UI\bin\Debug\net10.0", null, [@"C:\bin"], existing);
        Assert.Equal(@"C:\bin\ffmpeg.exe", result);
    }

    [Fact]
    public void ToolsSubfolder_Resolves_WhenNotDirectlyInExeDir()
    {
        // The canonical release bundle location: <exeDir>/tools/yt-dlp.exe.
        var existing = new HashSet<string> { @"C:\app\tools\yt-dlp.exe" };
        var result = Resolve("yt-dlp", @"C:\app", null, [], existing);
        Assert.Equal(@"C:\app\tools\yt-dlp.exe", result);
    }

    [Fact]
    public void ExeDir_WinsOver_ToolsSubfolder()
    {
        var existing = new HashSet<string> { @"C:\app\yt-dlp.exe", @"C:\app\tools\yt-dlp.exe" };
        var result = Resolve("yt-dlp", @"C:\app", null, [], existing);
        Assert.Equal(@"C:\app\yt-dlp.exe", result);
    }

    [Fact]
    public void ToolsSubfolder_WinsOver_Path()
    {
        var existing = new HashSet<string> { @"C:\app\tools\ffmpeg.exe", @"C:\bin\ffmpeg.exe" };
        var result = Resolve("ffmpeg", @"C:\app", null, [@"C:\bin"], existing);
        Assert.Equal(@"C:\app\tools\ffmpeg.exe", result);
    }

    [Fact]
    public void NotFound_ReturnsNull()
    {
        var result = Resolve("yt-dlp", @"C:\app", null, [@"C:\bin"], []);
        Assert.Null(result);
    }

    [Fact]
    public void NonWindows_UsesBareName()
    {
        // Build the expected path via Path.Combine so it matches on any host
        // separator; the point of the test is that no ".exe" variant is used.
        var expected = Path.Combine("/usr/bin", "yt-dlp");
        var existing = new HashSet<string> { expected };
        var result = Resolve("yt-dlp", "/app", null, ["/usr/bin"], existing, isWindows: false);
        Assert.Equal(expected, result);
        Assert.DoesNotContain(".exe", result);
    }
}
