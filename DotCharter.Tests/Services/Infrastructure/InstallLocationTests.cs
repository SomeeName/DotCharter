using System.Text.RegularExpressions;
using DotCharter.Launcher;
using DotCharter.Services.Import.Youtube;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     Where a split install (#413) is, as opposed to where its exe is: the app
///     runs from <c>&lt;install&gt;\app</c>, and the portable config, the bundled
///     tools, the voice packs and downloaded models belong to the install's TOP —
///     exactly where a v0.62 flat install kept them.
/// </summary>
public sealed class InstallLocationTests
{
    private static Func<string, bool> Existing(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void TheAppFolderOfASplitInstall_ResolvesToTheLaunchersFolder()
    {
        var exists = Existing(@"C:\Games\DotCharter\DotCharter.exe", @"C:\Games\DotCharter\app\DotCharter.exe");
        Assert.Equal(@"C:\Games\DotCharter", InstallLocation.ResolveInstallRoot(@"C:\Games\DotCharter\app\", exists));
        Assert.Equal(@"C:\Games\DotCharter", InstallLocation.ResolveInstallRoot(@"C:\Games\DotCharter\APP", exists));
    }

    [Theory]
    [InlineData(@"C:\Games\DotCharter\")]            // a flat install (any pre-0.63 release)
    [InlineData(@"C:\repo\DotCharter.UI\bin\Debug\net10.0\")]   // a dev build
    public void AFlatFolder_IsItsOwnInstallRoot(string appDirectory)
        => Assert.Equal(Path.TrimEndingDirectorySeparator(appDirectory),
            InstallLocation.ResolveInstallRoot(appDirectory, _ => true));

    /// <summary>A folder that merely happens to be called <c>app</c> is not a split install.</summary>
    [Fact]
    public void AnAppFolderWithoutTheLauncherAbove_IsNotSplit()
    {
        Assert.Equal(@"C:\work\app",
            InstallLocation.ResolveInstallRoot(@"C:\work\app", Existing(@"C:\work\app\DotCharter.exe")));
        Assert.Equal(@"C:\work\app",
            InstallLocation.ResolveInstallRoot(@"C:\work\app", Existing(@"C:\work\DotCharter.exe")));
    }

    [Fact]
    public void TheVoiceFolder_IsExportedOnlyForASplitInstall_AndNeverOverAUsersValue()
    {
        Assert.Equal(@"C:\DC\voices", InstallLocation.VoiceDirectoryToExport(@"C:\DC\app", @"C:\DC", null));
        Assert.Null(InstallLocation.VoiceDirectoryToExport(@"C:\DC", @"C:\DC", null));
        Assert.Null(InstallLocation.VoiceDirectoryToExport(@"C:\DC\app", @"C:\DC", @"D:\my-voices"));
    }

    /// <summary>
    ///     The test host is a flat folder, so the live property must be the exe's
    ///     own folder — the guarantee every caller relies on for dev builds.
    /// </summary>
    [Fact]
    public void ThisProcess_IsNotASplitInstall()
    {
        Assert.False(InstallLocation.IsSplitInstall);
        Assert.Equal(InstallLocation.AppDirectory, InstallLocation.InstallRoot);
    }

    [Fact]
    public void BundledTools_AreFoundAtTheTopOfASplitInstall()
    {
        var exists = Existing(@"C:\DC\DotCharter.exe", @"C:\DC\app\DotCharter.exe", @"C:\DC\tools\ffmpeg.exe");
        Assert.Equal(@"C:\DC\tools\ffmpeg.exe",
            MediaToolResolver.Resolve("ffmpeg", @"C:\DC\app", null, [], exists, isWindows: true));
    }

    /// <summary>
    ///     The paths that must follow the install root, not the exe: the portable
    ///     config (on a portable install the top folder IS the config root, so
    ///     resolving it from app\ would start a v0.62 user over with empty
    ///     settings), downloaded models, and the folder the updater copies over.
    /// </summary>
    [Theory]
    [InlineData("DotCharter.Tone/Infrastructure/Storage/WritablePaths.cs")]
    [InlineData("DotCharter.Services/Audio/Lyrics/LyricModelCatalog.cs")]
    [InlineData("DotCharter.Services/Update/GitHubUpdateService.cs")]
    [InlineData("DotCharter.Services/Audio/StemSeparation/Components/ComponentManager.cs")]
    public void InstallScopedPaths_AreNotResolvedFromTheExeFolder(string relativePath)
    {
        var source = SourceText.StripComments(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relativePath)));
        Assert.DoesNotContain("AppContext.BaseDirectory", source, StringComparison.Ordinal);
        Assert.Contains("InstallLocation.InstallRoot", source, StringComparison.Ordinal);
    }

    // ---- the launcher's own decisions (DotCharter.Launcher/LaunchCommandLine.cs, linked in) ----

    [Theory]
    [InlineData(@"""C:\Program Files\DC\DotCharter.exe"" --open ""a b.rscp""", @"--open ""a b.rscp""")]
    [InlineData(@"C:\DC\DotCharter.exe   x  y", "x  y")]
    [InlineData(@"DotCharter.exe", "")]
    [InlineData(@"""C:\DC\DotCharter.exe""", "")]
    [InlineData(@"""C:\DC\DotCharter.exe""""quoted-on""", @"""quoted-on""")]
    [InlineData("", "")]
    public void TheLauncherPassesTheArgumentsOnUnparsed(string commandLine, string tail)
        => Assert.Equal(tail, LaunchCommandLine.ArgumentTail(commandLine));

    [Fact]
    public void TheLauncherStartsTheAppInItsAppFolder_WithTheSameTail()
    {
        Assert.Equal(Path.Combine(@"C:\DC", "app", "DotCharter.exe"), LaunchCommandLine.AppExePath(@"C:\DC"));
        Assert.Equal(@"""C:\DC\app\DotCharter.exe"" --x ""y z""",
            LaunchCommandLine.ForApp(@"C:\DC\app\DotCharter.exe", @"--x ""y z"""));
        Assert.Equal(@"""C:\DC\app\DotCharter.exe""", LaunchCommandLine.ForApp(@"C:\DC\app\DotCharter.exe", ""));
        // The folder names agree with the app's side of the contract.
        Assert.Equal(InstallLocation.AppFolderName, LaunchCommandLine.AppFolderName);
        Assert.Equal(InstallLocation.ExeName, LaunchCommandLine.AppExeName);
    }

    /// <summary>
    ///     The app starts in its own folder, never in the caller's: Windows searches
    ///     the working directory for DLLs, so a launch from a Downloads folder would
    ///     otherwise let a file planted there load into the app.
    /// </summary>
    [Fact]
    public void TheLauncherStartsTheAppWithTheAppFolderAsItsWorkingDirectory()
    {
        Assert.Equal(Path.Combine(@"C:\DC", "app"), LaunchCommandLine.WorkingDirectory(LaunchCommandLine.AppExePath(@"C:\DC")));

        var program = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Launcher", "Program.cs"));
        Assert.Contains("LaunchCommandLine.WorkingDirectory(appExe)", program, StringComparison.Ordinal);
        var call = Regex.Match(program, @"CreateProcessW\(application,[^;]*;", RegexOptions.Singleline).Value;
        Assert.NotEmpty(call);
        Assert.Matches(@"null,\s*directory,\s*&startup", call);
    }
}
