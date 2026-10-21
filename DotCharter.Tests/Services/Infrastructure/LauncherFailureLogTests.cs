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
}
