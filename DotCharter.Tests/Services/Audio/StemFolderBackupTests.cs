using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

public sealed class StemFolderBackupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-stem-backup-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void Open_ListsTheWavsAlreadyInTheFolder_ByName()
    {
        Write("vocals.wav", "old vocals");
        Write("drums.wav", "old drums");
        Write("notes.txt", "not a stem");

        var folder = StemFolderBackup.Open(_dir, new FakeAppLogger());

        Assert.Equal(["drums", "vocals"], folder.Existing.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(folder.BackedUp);
    }

    [Fact]
    public void Open_OnAFolderThatDoesNotExist_ListsNothing()
    {
        var folder = StemFolderBackup.Open(Path.Combine(_dir, "missing"), new FakeAppLogger());

        Assert.Empty(folder.Existing);
    }

    [Fact]
    public void MoveAside_TakesOnlyTheNamedStemsOutOfTheSeparatorsWay()
    {
        Write("vocals.wav", "old vocals");
        Write("guitar.wav", "old guitar");
        var folder = StemFolderBackup.Open(_dir, new FakeAppLogger());

        folder.MoveAside(["guitar", "not-there"]);

        Assert.False(File.Exists(Path.Combine(_dir, "guitar.wav")));
        Assert.True(File.Exists(Path.Combine(_dir, "vocals.wav")));
        Assert.Equal(["guitar"], folder.BackedUp.Keys);
        Assert.Equal("old guitar", File.ReadAllText(folder.BackedUp["guitar"]));
    }

    [Fact]
    public void Restore_AfterACancel_PutsTheKeptStemsBackAndRemovesTheBackupFolder()
    {
        Write("guitar.wav", "old guitar");
        var folder = StemFolderBackup.Open(_dir, new FakeAppLogger());
        folder.MoveAside(["guitar"]);

        folder.Restore();

        Assert.Equal("old guitar", File.ReadAllText(Path.Combine(_dir, "guitar.wav")));
        Assert.False(Directory.Exists(Path.Combine(_dir, ".backup")));
    }

    [Fact]
    public void ApplyKept_AfterARun_ReplacesTheFreshStemWithTheKeptOne()
    {
        Write("vocals.wav", "old vocals");
        var folder = StemFolderBackup.Open(_dir, new FakeAppLogger());
        folder.MoveAside(["vocals"]);
        Write("vocals.wav", "fresh vocals");
        Write("drums.wav", "fresh drums");
        StemTrack[] produced =
        [
            new() { Name = "vocals", Path = Path.Combine(_dir, "vocals.wav") },
            new() { Name = "drums", Path = Path.Combine(_dir, "drums.wav") },
        ];

        var final = folder.ApplyKept(produced);

        Assert.Equal("old vocals", File.ReadAllText(Path.Combine(_dir, "vocals.wav")));
        Assert.Equal("fresh drums", File.ReadAllText(Path.Combine(_dir, "drums.wav")));
        Assert.Equal(["drums", "vocals"], final.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Single(final, s => s.Name == "vocals");
        Assert.False(Directory.Exists(Path.Combine(_dir, ".backup")));
    }

    [Fact]
    public void ApplyKept_WithNothingKept_ReturnsWhatTheModelProduced()
    {
        Write("drums.wav", "fresh drums");
        var folder = StemFolderBackup.Open(_dir, new FakeAppLogger());
        StemTrack[] produced = [new() { Name = "drums", Path = Path.Combine(_dir, "drums.wav") }];

        var final = folder.ApplyKept(produced);

        Assert.Equal(produced, final);
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);
}
