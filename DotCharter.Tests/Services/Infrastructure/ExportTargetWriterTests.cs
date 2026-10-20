using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>The disk half of the export/import dialogs: the behaviours the
/// dialogs rely on from the calls they used to make themselves.</summary>
public sealed class ExportTargetWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-exporttarget-").FullName;
    private readonly ExportTargetWriter _files = new();

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void CopyFile_OverwritesAnExistingDestination()
    {
        var source = Path.Combine(_dir, "song.ogg");
        var destination = Path.Combine(_dir, "copy.ogg");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "old");

        _files.CopyFile(source, destination);

        Assert.Equal("new", File.ReadAllText(destination));
    }

    [Fact]
    public void Create_MakesTheMissingParentFolder()
    {
        var path = Path.Combine(_dir, "screenshots", "shot.png");

        using (var stream = _files.Create(path)) stream.WriteByte(1);

        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void DeleteIfExists_RemovesTheFile_AndIgnoresAMissingOne()
    {
        var path = Path.Combine(_dir, "temp.mp3");
        File.WriteAllText(path, "x");

        _files.DeleteIfExists(path);
        _files.DeleteIfExists(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void FindFiles_IsRecursive()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a", "b"));
        File.WriteAllText(Path.Combine(_dir, "top.rscp"), "");
        File.WriteAllText(Path.Combine(_dir, "a", "b", "deep.rscp"), "");
        File.WriteAllText(Path.Combine(_dir, "a", "notes.txt"), "");

        var found = _files.FindFiles(_dir, "*.rscp").Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(["deep.rscp", "top.rscp"], found);
    }
}
