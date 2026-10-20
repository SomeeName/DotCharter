using DotCharter.Services.Parsing;
using DotCharter.Services.State.Checkpoints;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.State;

public sealed class CheckpointServiceTests : IDisposable
{
    private const string Identity = "C:/charts/song.rscp";

    private readonly string _root;
    private readonly CheckpointService _service;

    public CheckpointServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dc_checkpoints_{Guid.NewGuid():N}");
        _service = new CheckpointService(_root);
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    private static ChartProject BuildProject(string songName, int noteCount)
    {
        var arrangement = new Arrangement { Name = "Lead Guitar" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        for (var i = 0; i < noteCount; i++)
            level.Notes.Add(new GuitarNote { StringIndex = 2, Fret = i, Position = new FractionalPosition(i) });
        arrangement.Levels.Add(level);

        var project = new ChartProject
        {
            Bpm = 120,
            AudioFileName = "song.ogg",
            Chart = new ChartData { SongName = songName, Artist = "Tester", BeatsPerMinute = 120 }
        };
        project.Arrangements.Add(arrangement);
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4 });
        return project;
    }

    [Fact]
    public void Create_Then_List_ReturnsEntry()
    {
        var entry = _service.Create(Identity, "My checkpoint", BuildProject("Song", 3), "Song", "1.0.0");

        var list = _service.List(Identity);

        Assert.Single(list);
        Assert.Equal(entry.Id, list[0].Id);
        Assert.Equal("My checkpoint", list[0].Name);
        Assert.Equal("Song", list[0].SongName);
    }

    [Fact]
    public void Create_Then_Restore_RoundTripsProjectGraph()
    {
        var project = BuildProject("Restore Me", 4);
        var entry = _service.Create(Identity, "cp", project);

        var restored = _service.Restore(Identity, entry);

        Assert.Equal(project.Chart.SongName, restored.Chart.SongName);
        Assert.Equal(project.Arrangements[0].Levels[0].Notes.Count, restored.Arrangements[0].Levels[0].Notes.Count);
        Assert.Equal(project.Beats.Count, restored.Beats.Count);
    }

    [Fact]
    public void Delete_RemovesEntryAndSnapshotFile()
    {
        var entry = _service.Create(Identity, "cp", BuildProject("Song", 2));
        Assert.Single(_service.List(Identity));

        var deleted = _service.Delete(Identity, entry.Id);

        Assert.True(deleted);
        Assert.Empty(_service.List(Identity));
        Assert.Throws<FileNotFoundException>(() => _service.Restore(Identity, entry));
    }

    [Fact]
    public void Delete_UnknownId_ReturnsFalse()
    {
        _service.Create(Identity, "cp", BuildProject("Song", 1));
        Assert.False(_service.Delete(Identity, "does-not-exist"));
        Assert.Single(_service.List(Identity));
    }

    [Fact]
    public void MultipleCheckpoints_AreListedNewestFirst()
    {
        _service.Create(Identity, "first", BuildProject("Song", 1));
        Thread.Sleep(5);
        _service.Create(Identity, "second", BuildProject("Song", 2));
        Thread.Sleep(5);
        _service.Create(Identity, "third", BuildProject("Song", 3));

        var list = _service.List(Identity);

        Assert.Equal(3, list.Count);
        Assert.Equal("third", list[0].Name);
        Assert.Equal("first", list[^1].Name);
    }

    [Fact]
    public void DifferentIdentities_AreIsolated()
    {
        _service.Create("C:/a.rscp", "a-cp", BuildProject("A", 1));
        _service.Create("C:/b.rscp", "b-cp", BuildProject("B", 1));

        Assert.Single(_service.List("C:/a.rscp"));
        Assert.Single(_service.List("C:/b.rscp"));
        Assert.Equal("a-cp", _service.List("C:/a.rscp")[0].Name);
    }

    [Fact]
    public void List_UnknownIdentity_IsEmpty()
    {
        Assert.Empty(_service.List("never-created"));
        Assert.Empty(_service.List(null));
    }

    [Fact]
    public void Checkpoints_SurviveNewServiceInstance()
    {
        _service.Create(Identity, "persisted", BuildProject("Song", 2));

        var fresh = new CheckpointService(_root);
        var list = fresh.List(Identity);

        Assert.Single(list);
        Assert.Equal("persisted", list[0].Name);
    }

    [Fact]
    public void Create_LeavesNoTempFiles()
    {
        _service.Create(Identity, "cp", BuildProject("Song", 2));
        var dir = Path.Combine(_root, CheckpointService.ComputeKey(Identity));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp"));
    }

    /// <summary>
    ///     A manifest that cannot be READ must never be treated as a manifest
    ///     that does not EXIST.
    ///     <para>
    ///         Create adds its entry to whatever index it read and writes the
    ///         result back atomically — so an empty guess replaces the real index
    ///         with a one-entry file, every earlier checkpoint disappears from the
    ///         dialog, and its snapshot stays on disk as an orphan Delete can no
    ///         longer reach. Nothing in the UI reports it: the status line says
    ///         "created" and the list dutifully shows the one that survived.
    ///     </para>
    ///     <para>
    ///         The lock here is exactly what an antivirus or indexer scan of the
    ///         just-written manifest does — the same hold
    ///         <c>AtomicFile.ReplaceWithRetry</c> exists to absorb on the write
    ///         side.
    ///     </para>
    /// </summary>
    [Fact]
    public void ALockedManifest_RefusesTheWrite_RatherThanUnlistingEveryCheckpoint()
    {
        _service.Create(Identity, "first", BuildProject("Song", 1));
        _service.Create(Identity, "second", BuildProject("Song", 2));
        var manifest = Path.Combine(_root, CheckpointService.ComputeKey(Identity), "manifest.json");

        using (new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() =>
                _service.Create(Identity, "third", BuildProject("Song", 3)));

            // Deleting is refused for the same reason, and says so by removing
            // nothing rather than by rewriting an index it could not read.
            Assert.False(_service.Delete(Identity, "does-not-matter"));
        }

        // The index is intact, and both snapshots are still reachable.
        var list = _service.List(Identity);
        Assert.Equal(2, list.Count);
        Assert.Equal("second", list[0].Name);
        Assert.Equal("first", list[1].Name);
        foreach (var entry in list) Assert.NotNull(_service.Restore(Identity, entry));
    }

    /// <summary>
    ///     …and the refusal happens BEFORE the snapshot is written, or it leaves
    ///     behind precisely the unreachable orphan it exists to prevent.
    /// </summary>
    [Fact]
    public void ARefusedCreate_WritesNothingAtAll()
    {
        _service.Create(Identity, "first", BuildProject("Song", 1));
        var dir = Path.Combine(_root, CheckpointService.ComputeKey(Identity));
        var before = Directory.GetFiles(dir).Length;

        using (new FileStream(Path.Combine(dir, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => _service.Create(Identity, "second", BuildProject("Song", 2)));

        Assert.Equal(before, Directory.GetFiles(dir).Length);
    }

    /// <summary>
    ///     The other half: an ABSENT manifest really is an empty index, and every
    ///     path must keep working. Without this the fix above would read as "any
    ///     read failure refuses", which would make the first checkpoint of every
    ///     new project impossible.
    /// </summary>
    [Fact]
    public void AnAbsentManifest_IsStillAnEmptyIndexAndCreateProceeds()
    {
        Assert.Empty(_service.List("brand-new"));
        var entry = _service.Create("brand-new", "cp", BuildProject("Song", 1));
        Assert.Single(_service.List("brand-new"));
        Assert.Equal(entry.Id, _service.List("brand-new")[0].Id);
    }

    /// <summary>
    ///     A serializer that dies part-way through must leave the folder exactly
    ///     as it found it. The half-written <c>.tmp</c> is in no manifest, so
    ///     <see cref="ICheckpointService.Delete" /> can never reach it — it just
    ///     accumulates, one file per failed attempt, in the folder holding the
    ///     user's unpublished work.
    /// </summary>
    [Fact]
    public void AFailedSnapshotWrite_LeavesNothingBehind()
    {
        var service = new CheckpointService(_root, new ThrowingSerializer());

        Assert.Throws<IOException>(() => service.Create(Identity, "doomed", BuildProject("Song", 1)));

        var dir = Path.Combine(_root, CheckpointService.ComputeKey(Identity));
        Assert.True(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0,
            "A refused Create left files behind: " +
            string.Join(", ", Directory.EnumerateFiles(dir).Select(Path.GetFileName)));
        Assert.Empty(service.List(Identity));
    }

    /// <summary>
    ///     …and the SNAPSHOT is forced to the platter before the rename makes it
    ///     visible, not just the manifest.
    ///     <para>
    ///         A power cut can retire the atomic rename while the serializer's
    ///         bytes are still in the page cache: the manifest then lists a
    ///         checkpoint whose <c>.rscp</c> is empty, it survives the restart,
    ///         and Restore throws on it. Structural rather than behavioural
    ///         because nothing in a test can pull the plug — but the ORDER is the
    ///         whole contract (<c>Save</c> → <c>FlushFile</c> → <c>ReplaceWithRetry</c>),
    ///         and comments are stripped first so the doc-comment above cannot
    ///         satisfy it.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheStagedSnapshot_IsFlushedBeforeTheAtomicSwap()
    {
        var source = ReadCodeLines(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Services", "State", "Checkpoints", "CheckpointService.cs"));

        var save = source.IndexOf("_serializer.Save(tempPath", StringComparison.Ordinal);
        var flush = source.IndexOf("AtomicFile.FlushFile(tempPath)", StringComparison.Ordinal);
        var swap = source.IndexOf("AtomicFile.ReplaceWithRetry(tempPath", StringComparison.Ordinal);

        Assert.True(save >= 0, "CheckpointService no longer stages the snapshot through a temp file.");
        Assert.True(flush >= 0,
            "The staged checkpoint snapshot is never flushed to disk, so a power cut can swap in an " +
            "empty .rscp that the manifest still lists. Call AtomicFile.FlushFile on the temp file.");
        Assert.True(save < flush && flush < swap,
            $"The flush must sit BETWEEN the write and the swap (save={save}, flush={flush}, swap={swap}).");
    }

    /// <summary>
    ///     Restore is a pure read: doing it twice yields two INDEPENDENT graphs.
    ///     Restoring swaps the project graph into the live session, so a shared
    ///     instance would let a restore-then-edit-then-restore-again sequence
    ///     bring the edits back with it.
    /// </summary>
    [Fact]
    public void Restore_IsIdempotent_AndHandsOutIndependentGraphs()
    {
        var entry = _service.Create(Identity, "cp", BuildProject("Twice", 3));

        var first = _service.Restore(Identity, entry);
        first.Chart.SongName = "mutated";
        first.Arrangements[0].Levels[0].Notes.Clear();

        var second = _service.Restore(Identity, entry);

        Assert.NotSame(first, second);
        Assert.Equal("Twice", second.Chart.SongName);
        Assert.Equal(3, second.Arrangements[0].Levels[0].Notes.Count);
    }

    /// <summary>
    ///     One torn snapshot must not brick the rest: the index still lists every
    ///     checkpoint and the intact ones still restore. A corrupt file that took
    ///     the whole dialog down would hide the very snapshots the user needs.
    /// </summary>
    [Fact]
    public void ATornSnapshot_DoesNotBlockTheOtherCheckpoints()
    {
        var good = _service.Create(Identity, "good", BuildProject("Good", 2));
        var torn = _service.Create(Identity, "torn", BuildProject("Torn", 2));

        var dir = Path.Combine(_root, CheckpointService.ComputeKey(Identity));
        File.WriteAllText(Path.Combine(dir, torn.SnapshotFileName), ""); // the power-cut shape

        Assert.Equal(2, _service.List(Identity).Count);
        Assert.Equal("Good", _service.Restore(Identity, good).Chart.SongName);
        Assert.ThrowsAny<Exception>(() => _service.Restore(Identity, torn));

        // ...and the broken one can still be cleaned up, which is the recovery.
        Assert.True(_service.Delete(Identity, torn.Id));
        Assert.Single(_service.List(Identity));
        Assert.Equal("Good", _service.Restore(Identity, good).Chart.SongName);
    }

    private sealed class ThrowingSerializer : IChartSerializer
    {
        public ChartProject Load(string filePath) => throw new NotSupportedException();

        public void Save(string filePath, ChartProject project, double? audioLengthMs = null)
        {
            // Half a file, then the disk fills up / the AV kills the handle.
            File.WriteAllText(filePath, "<Project><Half");
            throw new IOException("simulated write failure");
        }
    }

    /// <summary>The file with its comment lines blanked — a doc-comment naming a
    /// call must not satisfy a source lint.</summary>
    private static string ReadCodeLines(string path)
    {
        var lines = File.ReadAllLines(path).Select(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("*", StringComparison.Ordinal)
                ? string.Empty
                : line;
        });
        return string.Join('\n', lines);
    }
}
