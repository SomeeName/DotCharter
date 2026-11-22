using System.Text.RegularExpressions;
using DotCharter.Launcher;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     Where the launcher writes its one-line failure note
///     (<c>DotCharter.Launcher/LaunchCommandLine.cs</c>, compiled into the tests by
///     link). Beside the launcher when that folder takes it; otherwise in the temp
///     folder under a name nobody can know in advance, created exclusively - never
///     appended to a fixed <c>%TEMP%\DotCharter\...</c> path a stranger could have
///     pre-created or pointed somewhere.
/// </summary>
public sealed class LauncherFailureLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-launcher-log-" + Guid.NewGuid().ToString("N"));

    public LauncherFailureLogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    private string Temp()
    {
        var temp = Path.Combine(_root, "temp");
        Directory.CreateDirectory(temp);
        return temp;
    }

    /// <summary>A launcher folder that cannot take a file: a path UNDER an existing file.</summary>
    private string UnwritableLauncherFolder()
    {
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "x");
        return Path.Combine(file, "install");
    }

    [Fact]
    public void ALauncherFolderThatTakesTheFile_GetsTheNoteThere()
    {
        var launcher = Path.Combine(_root, "install");
        Directory.CreateDirectory(launcher);

        var written = LaunchCommandLine.TryWriteFailureLog(launcher, Temp(), "line one");

        Assert.Equal(Path.Combine(launcher, LaunchCommandLine.FailureLogName), written);
        Assert.Contains("line one", File.ReadAllText(written!));
        Assert.Empty(Directory.GetFileSystemEntries(Temp()));
    }

    [Fact]
    public void AnUnwritableLauncherFolder_FallsBackToAUniquelyNamedFileDirectlyInTemp()
    {
        var temp = Temp();

        var written = LaunchCommandLine.TryWriteFailureLog(UnwritableLauncherFolder(), temp, "line one");

        Assert.NotNull(written);
        Assert.Equal(temp, Path.GetDirectoryName(written));
        Assert.Matches(@"^dotcharter-launcher-[0-9a-f]{16,}\.log$", Path.GetFileName(written));
        Assert.Contains("line one", File.ReadAllText(written));
        // No fixed, guessable DotCharter subfolder.
        Assert.False(Directory.Exists(Path.Combine(temp, "DotCharter")));
    }

    [Fact]
    public void TwoFailures_AreTwoFilesWithTwoNames()
    {
        var temp = Temp();
        var launcher = UnwritableLauncherFolder();

        var first = LaunchCommandLine.TryWriteFailureLog(launcher, temp, "a");
        var second = LaunchCommandLine.TryWriteFailureLog(launcher, temp, "b");

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(temp).Length);
    }

    /// <summary>A file already sitting on the name (someone guessed it, or a link was
    /// planted there) is never opened: the create is exclusive, so the note goes to
    /// the next name instead.</summary>
    [Fact]
    public void AFileAlreadyOnTheChosenName_IsNeitherAppendedToNorOverwritten()
    {
        var temp = Temp();
        var squatter = Path.Combine(temp, LaunchCommandLine.FallbackFailureLogName("0000000000000000"));
        File.WriteAllText(squatter, "SQUATTER");
        var tokens = new Queue<string>(["0000000000000000", "1111111111111111"]);

        var written = LaunchCommandLine.TryWriteFailureLog(
            UnwritableLauncherFolder(), temp, "line one", tokens.Dequeue);

        Assert.Equal("SQUATTER", File.ReadAllText(squatter));
        Assert.Equal(Path.Combine(temp, LaunchCommandLine.FallbackFailureLogName("1111111111111111")), written);
        Assert.Contains("line one", File.ReadAllText(written!));
    }

    [Fact]
    public void NothingWritable_ReturnsNullInsteadOfThrowing()
    {
        var file = Path.Combine(_root, "plain-file");
        File.WriteAllText(file, "x");

        Assert.Null(LaunchCommandLine.TryWriteFailureLog(
            Path.Combine(file, "a"), Path.Combine(file, "b"), "line"));
    }

    // ---- #383: a failed start is shown, through a seam ----------------------

    [Fact]
    public void FailureMessage_NamesTheReasonTheExitCodeAndTheLog()
    {
        var text = LaunchCommandLine.FailureMessage("the file does not exist", LaunchCommandLine.MissingAppExitCode, @"C:\x\log.txt");

        Assert.Contains("the file does not exist", text);
        Assert.Contains("0x44430001", text);
        Assert.Contains(@"C:\x\log.txt", text);
    }

    [Fact]
    public void ReportFailure_HandsTheMessageToTheReporter_AndReturnsTheExitCode()
    {
        var shown = new List<string>();

        var code = LaunchCommandLine.ReportFailure(
            Path.Combine(_root, "launcher"), Temp(), @"C:\x\app\DotCharter.exe", "the file does not exist",
            LaunchCommandLine.MissingAppExitCode, showDialog: true, shown.Add);

        Assert.Equal(LaunchCommandLine.MissingAppExitCode, code);
        var message = Assert.Single(shown);
        Assert.Contains("the file does not exist", message);
        Assert.Contains(Path.Combine(_root, "launcher", LaunchCommandLine.FailureLogName), message);
    }

    [Fact]
    public void ReportFailure_StaysSilentWhenNoDialogIsWanted()
    {
        var shown = new List<string>();

        LaunchCommandLine.ReportFailure(
            Path.Combine(_root, "launcher"), Temp(), "x", "r", LaunchCommandLine.StartFailedExitCode, false, shown.Add);

        Assert.Empty(shown);
        Assert.True(File.Exists(Path.Combine(_root, "launcher", LaunchCommandLine.FailureLogName)));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void ShouldShowDialog_IsOnlyForAPersonAtTheDesktop(bool redirected, bool desktop, bool expected)
        => Assert.Equal(expected, LaunchCommandLine.ShouldShowDialog(redirected, desktop));

    /// <summary>
    ///     #605: the app missing on a redirected or hidden-desktop launch writes the note and
    ///     shows NO dialog; a person at the desktop still gets the missing-app message. The
    ///     policy is decided inside the missing-app path from the STARTUPINFO flags it is handed.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void AMissingApp_ShowsTheDialogOnlyToAPersonAtTheDesktop(bool redirected, bool desktop, bool dialog)
    {
        var shown = new List<string>();
        var launcher = Path.Combine(_root, "launcher");

        var code = LaunchCommandLine.ReportIfAppMissing(
            launcher, Temp(), @"C:\x\app\DotCharter.exe", appExists: false, redirected, desktop, shown.Add);

        Assert.Equal(LaunchCommandLine.MissingAppExitCode, code);
        Assert.Equal(dialog ? 1 : 0, shown.Count);
        Assert.True(File.Exists(Path.Combine(launcher, LaunchCommandLine.FailureLogName)), "the failure note was not written");
    }

    [Fact]
    public void APresentApp_IsNoFailure()
    {
        var shown = new List<string>();

        Assert.Null(LaunchCommandLine.ReportIfAppMissing(
            Path.Combine(_root, "launcher"), Temp(), "x", appExists: true, false, false, shown.Add));
        Assert.Empty(shown);
    }

    /// <summary>
    ///     #605, the wiring: <c>Program.Main</c> is a native entry point the tests cannot run, so
    ///     this is the one part pinned on the source. No static dialog flag that starts out true,
    ///     the missing-app check goes through <see cref="LaunchCommandLine.ReportIfAppMissing" />,
    ///     and every other failure passes the policy read from STARTUPINFO.
    /// </summary>
    [Fact]
    public void Main_HasNoDialogFlagAFailureCouldReadBeforeTheStartupInfo()
    {
        var program = SourceText.StripComments(
            File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Launcher", "Program.cs")));

        Assert.DoesNotContain("_showDialog", program, StringComparison.Ordinal);
        Assert.Contains("LaunchCommandLine.ReportIfAppMissing(", program, StringComparison.Ordinal);
        Assert.Equal(
            Regex.Matches(program, @"return Fail\(").Count,
            Regex.Matches(program, @"return Fail\([^;]*,\s*showDialog\);").Count);
    }

    [Theory]
    [InlineData(1, 200, true)]
    [InlineData(0, 200, false)]
    [InlineData(1, 60000, false)]
    public void IsQuickCrash_NeedsANonzeroExitSoonAfterTheStart(int code, long elapsed, bool expected)
        => Assert.Equal(expected, LaunchCommandLine.IsQuickCrash(code, elapsed));
}
