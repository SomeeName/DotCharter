using DotCharter.Services.State.Checkpoints;

namespace DotCharter.Tests.Services.State;

public sealed class CheckpointManifestOpsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SanitizeName_TrimsCollapsesAndFallsBackWhenEmpty()
    {
        Assert.Equal("Before the solo", CheckpointManifestOps.SanitizeName("  Before   the\tsolo \n"));
        Assert.Equal(CheckpointManifestOps.DefaultName, CheckpointManifestOps.SanitizeName("   "));
        Assert.Equal(CheckpointManifestOps.DefaultName, CheckpointManifestOps.SanitizeName(null));
    }

    [Fact]
    public void SanitizeName_CapsLength()
    {
        var longName = new string('x', CheckpointManifestOps.MaxNameLength + 40);
        var result = CheckpointManifestOps.SanitizeName(longName);
        Assert.Equal(CheckpointManifestOps.MaxNameLength, result.Length);
    }

    [Fact]
    public void CreateEntry_DerivesSnapshotFileNameFromId()
    {
        var entry = CheckpointManifestOps.CreateEntry("My point", "Song", "1.2.3", T0, id: "abc123");

        Assert.Equal("abc123", entry.Id);
        Assert.Equal("abc123" + CheckpointManifestOps.SnapshotExtension, entry.SnapshotFileName);
        Assert.Equal("My point", entry.Name);
        Assert.Equal("Song", entry.SongName);
        Assert.Equal("1.2.3", entry.AppVersion);
        Assert.Equal(T0, entry.CreatedUtc);
    }

    [Fact]
    public void CreateEntry_GeneratesUniqueIdsWhenNotPinned()
    {
        var a = CheckpointManifestOps.CreateEntry("a", null, null, T0);
        var b = CheckpointManifestOps.CreateEntry("b", null, null, T0);
        Assert.NotEqual(a.Id, b.Id);
        Assert.False(string.IsNullOrWhiteSpace(a.Id));
    }

    [Fact]
    public void Add_ReplacesEntryWithSameId()
    {
        var manifest = new CheckpointManifest();
        CheckpointManifestOps.Add(manifest, CheckpointManifestOps.CreateEntry("first", null, null, T0, "same"));
        CheckpointManifestOps.Add(manifest, CheckpointManifestOps.CreateEntry("second", null, null, T0, "same"));

        Assert.Single(manifest.Checkpoints);
        Assert.Equal("second", manifest.Checkpoints[0].Name);
    }

    [Fact]
    public void Remove_ReturnsRemovedEntryAndDropsIt()
    {
        var manifest = new CheckpointManifest();
        CheckpointManifestOps.Add(manifest, CheckpointManifestOps.CreateEntry("keep", null, null, T0, "k"));
        CheckpointManifestOps.Add(manifest, CheckpointManifestOps.CreateEntry("drop", null, null, T0, "d"));

        var removed = CheckpointManifestOps.Remove(manifest, "d");

        Assert.NotNull(removed);
        Assert.Equal("drop", removed!.Name);
        Assert.Single(manifest.Checkpoints);
        Assert.Equal("k", manifest.Checkpoints[0].Id);
        Assert.Null(CheckpointManifestOps.Remove(manifest, "missing"));
    }

    [Fact]
    public void OrderedForDisplay_IsNewestFirst()
    {
        var manifest = new CheckpointManifest();
        CheckpointManifestOps.Add(manifest, CheckpointManifestOps.CreateEntry("old", null, null, T0, "1"));
        CheckpointManifestOps.Add(manifest, CheckpointManifestOps.CreateEntry("new", null, null, T0.AddMinutes(5), "2"));
        CheckpointManifestOps.Add(manifest, CheckpointManifestOps.CreateEntry("mid", null, null, T0.AddMinutes(2), "3"));

        var ordered = CheckpointManifestOps.OrderedForDisplay(manifest);

        Assert.Equal(new[] { "new", "mid", "old" }, ordered.Select(c => c.Name));
    }

    [Fact]
    public void Normalize_HandlesNullManifest()
    {
        var result = CheckpointManifestOps.Normalize(null);
        Assert.Equal(CheckpointManifestOps.CurrentSchemaVersion, result.SchemaVersion);
        Assert.Empty(result.Checkpoints);
    }

    [Fact]
    public void Normalize_DropsInvalidAndDuplicateEntries()
    {
        var manifest = new CheckpointManifest
        {
            SchemaVersion = 0, // invalid → clamped
            Checkpoints =
            [
                new() { Id = "good", SnapshotFileName = "good.rscp", Name = "  Good  " },
                new() { Id = "", SnapshotFileName = "x.rscp" },              // no id
                new() { Id = "nofile", SnapshotFileName = "" },              // no file
                new() { Id = "good", SnapshotFileName = "dup.rscp" },        // duplicate id
                null!
            ]
        };

        var result = CheckpointManifestOps.Normalize(manifest);

        Assert.Equal(CheckpointManifestOps.CurrentSchemaVersion, result.SchemaVersion);
        Assert.Single(result.Checkpoints);
        Assert.Equal("good", result.Checkpoints[0].Id);
        Assert.Equal("Good", result.Checkpoints[0].Name);
    }

    [Fact]
    public void Normalize_PreservesFutureSchemaVersion()
    {
        // Forward-compatibility: a newer-schema manifest is still readable; we
        // keep its version rather than downgrading it.
        var result = CheckpointManifestOps.Normalize(new CheckpointManifest { SchemaVersion = 99 });
        Assert.Equal(99, result.SchemaVersion);
    }
}
