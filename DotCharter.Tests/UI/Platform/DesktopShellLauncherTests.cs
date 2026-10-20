using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.Platform;

namespace DotCharter.Tests.UI.Platform;

/// <summary>
///     The shell launcher's arguments, pinned without launching anything: every
///     case either reads the pure start-info builders or takes the early return
///     before a process would start.
/// </summary>
public sealed class DesktopShellLauncherTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-shell-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Theory]
    [InlineData("https://github.com/login/device")]
    [InlineData(@"C:\some folder\LICENSE.md")]
    public void ShellOpen_HandsTheTargetToTheShell(string target)
    {
        var info = DesktopShellLauncher.ForShellOpen(target);

        Assert.Equal(target, info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Equal("", info.Arguments);
    }

    [Fact]
    public void Reveal_SelectsTheAbsolutePathInExplorer_Quoted()
    {
        var relative = Path.Combine("out", "My Song_p.psarc");

        var info = DesktopShellLauncher.ForReveal(relative);

        Assert.Equal("explorer.exe", info.FileName);
        Assert.Equal($"/select,\"{Path.GetFullPath(relative)}\"", info.Arguments);
        Assert.True(info.UseShellExecute);
    }

    [Fact]
    public void OpenFolder_OnAMissingFolder_OpensNothing()
        => Assert.False(new DesktopShellLauncher().OpenFolder(Path.Combine(_dir, "missing")));

    [Fact]
    public void Reveal_OfAMissingFile_OpensNothing()
        => Assert.False(new DesktopShellLauncher().RevealInFileManager(Path.Combine(_dir, "missing.rscp")));
}
