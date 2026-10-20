using DotCharter.Services.State.Checkpoints;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     <c>CheckpointManifestOps.Normalize</c> documents itself as keeping "only
///     entries with a usable id + snapshot file name" and checked the name for
///     BLANK alone. Everything downstream does <c>Path.Combine(projectDir,
///     name)</c>, and <c>Path.Combine</c> lets a rooted or traversing second
///     argument replace the first — so a manifest carrying <c>..\..\x</c> aimed
///     <c>CheckpointService.Delete</c>'s <c>File.Delete</c>, and
///     <c>Restore</c>'s read, outside the checkpoint store.
/// </summary>
public sealed class CheckpointSnapshotNameTests
{
    private static CheckpointManifest ManifestWith(string snapshotFileName)
    {
        var manifest = new CheckpointManifest();
        manifest.Checkpoints.Add(new CheckpointEntry
        {
            Id = "abc",
            Name = "Before the solo",
            SnapshotFileName = snapshotFileName,
            CreatedUtc = DateTimeOffset.UnixEpoch
        });
        return manifest;
    }

    [Theory]
    [InlineData(@"..\..\appconfig.json")]
    [InlineData("../../appconfig.json")]
    [InlineData(@"sub\nested.rscp")]
    [InlineData("sub/nested.rscp")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"\\server\share\x.rscp")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEntryThatCouldEscapeTheCheckpointFolderIsDropped(string snapshotFileName)
    {
        Assert.False(CheckpointManifestOps.IsBareFileName(snapshotFileName));
        Assert.Empty(CheckpointManifestOps.Normalize(ManifestWith(snapshotFileName)).Checkpoints);
    }

    [Theory]
    [InlineData("9f2c4b1e6a7d4f30.rscp")]
    [InlineData("Before the solo.rscp")]
    [InlineData("no-extension")]
    public void AnOrdinaryNameSurvives(string snapshotFileName)
    {
        Assert.True(CheckpointManifestOps.IsBareFileName(snapshotFileName));
        var entry = Assert.Single(CheckpointManifestOps.Normalize(ManifestWith(snapshotFileName)).Checkpoints);
        Assert.Equal(snapshotFileName, entry.SnapshotFileName);
    }

    /// <summary>Everything this app writes is a GUID plus the extension, so the
    /// rule cannot cost a real checkpoint.</summary>
    [Fact]
    public void TheNameTheServiceItselfMintsIsAccepted()
    {
        var entry = CheckpointManifestOps.CreateEntry("x", null, null, DateTimeOffset.UnixEpoch);

        Assert.True(CheckpointManifestOps.IsBareFileName(entry.SnapshotFileName));
    }

    /// <summary>A rejected entry must not take its intact neighbours with it —
    /// Normalize's contract is "drop what cannot be trusted", not "give up".</summary>
    [Fact]
    public void TheIntactEntriesBesideItAreKept()
    {
        var manifest = ManifestWith(@"..\escape.rscp");
        manifest.Checkpoints.Add(new CheckpointEntry
        {
            Id = "def",
            Name = "Good",
            SnapshotFileName = "def.rscp",
            CreatedUtc = DateTimeOffset.UnixEpoch
        });

        var kept = Assert.Single(CheckpointManifestOps.Normalize(manifest).Checkpoints);

        Assert.Equal("def", kept.Id);
    }
}
