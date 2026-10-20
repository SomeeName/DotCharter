using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class BestEffortDeleteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-besteffort-").FullName;

    [Fact]
    public void File_DeletesAnExistingFile()
    {
        var path = Path.Combine(_dir, "scratch.tmp");
        File.WriteAllText(path, "x");

        BestEffortDelete.File(path);

        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void File_IgnoresANullOrEmptyPath(string? path) => BestEffortDelete.File(path);

    [Fact]
    public void File_IgnoresAMissingFile() => BestEffortDelete.File(Path.Combine(_dir, "never-written.tmp"));

    [Fact]
    public void Directory_DeletesATreeRecursively()
    {
        var tree = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(Path.Combine(tree, "nested"));
        File.WriteAllText(Path.Combine(tree, "nested", "payload.bin"), "x");

        BestEffortDelete.Directory(tree);

        Assert.False(Directory.Exists(tree));
    }

    [Fact]
    public void Directory_IgnoresAMissingDirectory() => BestEffortDelete.Directory(Path.Combine(_dir, "never-created"));

    [Fact]
    public void File_OnADirectoryPath_LeavesTheDirectoryAlone()
    {
        var folder = Path.Combine(_dir, "a-folder");
        Directory.CreateDirectory(folder);

        BestEffortDelete.File(folder);

        Assert.True(Directory.Exists(folder));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
