using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Recovery;

public sealed class RecoverySnapshotStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _originalsDir;
    private readonly RecoverySnapshotStore _store;

    public RecoverySnapshotStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dc_recovery_{Guid.NewGuid():N}");
        _originalsDir = Path.Combine(_root, "originals");
        Directory.CreateDirectory(_originalsDir);
        _store = new RecoverySnapshotStore(Path.Combine(_root, "recovery"));
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    private static ChartProject BuildProject()
    {
        var arrangement = new Arrangement { Name = "Lead Guitar" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { StringIndex = 2, Fret = 5, Position = new FractionalPosition(4) });
        level.Notes.Add(new GuitarNote { StringIndex = 3, Fret = 7, Position = new FractionalPosition(6) });
        arrangement.Levels.Add(level);

        var project = new ChartProject
        {
            Bpm = 128,
            AudioFileName = "song.ogg",
            Chart = new ChartData
            {
                SongName = "Recovery Song", Artist = "Crash Test", BeatsPerMinute = 128
            }
        };
        project.Arrangements.Add(arrangement);
        // 468.75 ms per beat == exactly 128 BPM; the RSCP loader derives the
        // project BPM from beat spacing, so this keeps Bpm round-trippable.
        for (var i = 0; i < 8; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 468.75,
                Bpm = 128,
                IsMeasure = i % 4 == 0,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        return project;
    }

    private static RecoverySnapshotMetadata Metadata(string originalPath)
    {
        return new RecoverySnapshotMetadata
        {
            OriginalProjectPath = originalPath,
            OriginalAudioFileName = "song.ogg",
            SongName = "Recovery Song",
            TimestampUtc = DateTimeOffset.UtcNow,
            AppVersion = "test"
        };
    }

    [Fact]
    public void WriteSnapshot_RoundTripsThroughSerializer()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        var project = BuildProject();

        _store.WriteSnapshot(project, Metadata(original));

        var snapshotPath = _store.GetSnapshotPath(original);
        Assert.True(File.Exists(snapshotPath));

        var reloaded = _store.LoadSnapshot(snapshotPath);
        Assert.Equal(project.Beats.Count, reloaded.Beats.Count);
        Assert.Equal(project.Arrangements.Count, reloaded.Arrangements.Count);
        Assert.Equal(project.Arrangements[0].Levels[0].Notes.Count, reloaded.Arrangements[0].Levels[0].Notes.Count);
        Assert.Equal(project.Chart.SongName, reloaded.Chart.SongName);
        Assert.Equal(project.Bpm, reloaded.Bpm, 3);
        Assert.Equal(project.AudioFileName, reloaded.AudioFileName);
    }

    [Fact]
    public void WriteSnapshot_LeavesNoTempFiles_AndOverwritesPrevious()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        _store.WriteSnapshot(BuildProject(), Metadata(original));
        _store.WriteSnapshot(BuildProject(), Metadata(original));

        var files = Directory.GetFiles(_store.RootDirectory);
        Assert.DoesNotContain(files, f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, files.Length); // one .rscp + one .rscp.json, no duplicates
    }

    [Fact]
    public void TornTempFile_IsNeverListedAsSession()
    {
        Directory.CreateDirectory(_store.RootDirectory);
        // Simulates a crash in the middle of the snapshot write: only the
        // .tmp exists. It must be invisible to recovery.
        File.WriteAllText(Path.Combine(_store.RootDirectory, "deadbeefdeadbeef.rscp.tmp"), "torn write");

        Assert.Empty(_store.ListSessions());
    }

    [Fact]
    public void IsRecoverable_OriginalMissing_True()
    {
        var original = Path.Combine(_originalsDir, "gone.rscp");
        _store.WriteSnapshot(BuildProject(), Metadata(original));

        var session = Assert.Single(_store.ListSessions());
        Assert.True(_store.IsRecoverable(session));
        Assert.Equal(original, session.Metadata.OriginalProjectPath);
    }

    [Fact]
    public void IsRecoverable_SnapshotNewerThanOriginal_True()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        File.WriteAllText(original, "existing project");
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddHours(-1));

        _store.WriteSnapshot(BuildProject(), Metadata(original));

        var session = Assert.Single(_store.ListSessions());
        Assert.True(_store.IsRecoverable(session));
    }

    [Fact]
    public void IsRecoverable_OriginalNewerThanSnapshot_False()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        _store.WriteSnapshot(BuildProject(), Metadata(original));

        // The user saved AFTER the snapshot was written — the real file
        // supersedes it, nothing to recover.
        File.WriteAllText(original, "existing project");
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddHours(1));

        var session = Assert.Single(_store.ListSessions());
        Assert.False(_store.IsRecoverable(session));
    }

    [Fact]
    public void UntitledSnapshot_AlwaysRecoverable()
    {
        _store.WriteSnapshot(BuildProject(), Metadata(string.Empty));

        var session = Assert.Single(_store.ListSessions());
        Assert.True(session.IsUntitled);
        Assert.True(_store.IsRecoverable(session));
        Assert.EndsWith($"{RecoverySnapshotStore.UntitledKey}.rscp", session.SnapshotPath);
    }

    [Fact]
    public void MissingSidecar_SurfacesAsUntitledSession()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        _store.WriteSnapshot(BuildProject(), Metadata(original));
        File.Delete(Path.ChangeExtension(_store.GetSnapshotPath(original), null) + ".rscp.json");

        var session = Assert.Single(_store.ListSessions());
        Assert.True(session.IsUntitled);
        Assert.True(_store.IsRecoverable(session));
    }

    [Fact]
    public void DeleteSnapshot_RemovesSnapshotAndSidecar()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        _store.WriteSnapshot(BuildProject(), Metadata(original));
        Assert.Single(_store.ListSessions());

        _store.DeleteSnapshot(original);

        Assert.Empty(_store.ListSessions());
        Assert.Empty(Directory.GetFiles(_store.RootDirectory));
    }

    /// <summary>
    ///     The sidecar is written by <c>AtomicFile</c> (temp → flush → rename)
    ///     and was read back with a bare <c>File.ReadAllText</c>, which takes a
    ///     share mode DENYING delete. That is the exact save/load collision
    ///     <c>AtomicFile.ReadAllText</c> was added for in v0.31, and here it is
    ///     worse than a dropped write: <c>TryReadSidecar</c> swallows the
    ///     exception and returns null, so a titled session silently degrades to
    ///     an UNTITLED one — the recovery dialog then loses the link back to the
    ///     user's real project file and Save cannot be re-pointed at it.
    ///     <para>
    ///         The autosave timer and the startup enumeration genuinely overlap
    ///         (a snapshot lands every 10 s of quiet), so this is the production
    ///         shape, not a synthetic one. Bounded by ITERATIONS, never by a
    ///         clock.
    ///     </para>
    /// </summary>
    [Fact]
    public void ListSessions_DuringConcurrentWrites_NeverLosesTheOriginalProjectPath()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        // Seed one complete snapshot: before the first write there is legitimately
        // no sidecar, and "untitled" is the correct answer for that.
        _store.WriteSnapshot(BuildProject(), Metadata(original));

        Exception? writeFailure = null;
        var degraded = 0;
        var done = false;

        var reader = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                foreach (var session in _store.ListSessions())
                {
                    if (session.IsUntitled)
                        Interlocked.Increment(ref degraded);
                }
            }
        })
        { IsBackground = true };
        reader.Start();

        try
        {
            var project = BuildProject();
            for (var i = 0; i < 40; i++) _store.WriteSnapshot(project, Metadata(original));
        }
        catch (Exception ex)
        {
            writeFailure = ex;
        }
        finally
        {
            Volatile.Write(ref done, true);
            reader.Join();
        }

        Assert.Null(writeFailure);
        Assert.Equal(0, Volatile.Read(ref degraded));
    }

    /// <summary>
    ///     The snapshot itself is streamed out by the serializer, which owns its
    ///     own stream — so it is exactly the case <c>AtomicFile.FlushFile</c>
    ///     exists for, and it was not being used. Without the flush the
    ///     stage-then-swap is only half done: the rename is atomic, but a power
    ///     cut can persist the RENAME while the bytes are still in the page
    ///     cache, which atomically swaps in an EMPTY <c>.rscp</c> — over the file
    ///     whose entire job is to survive a crash. Asserted on the SOURCE because
    ///     no test can pull the plug.
    /// </summary>
    [Fact]
    public void TheStreamedSnapshotIsForcedDurableBeforeTheSwap()
    {
        var source = ReadServiceSource("Infrastructure", "Recovery", "RecoverySnapshotStore.cs");

        Assert.Contains("AtomicFile.FlushFile(tempPath)", source, StringComparison.Ordinal);

        // The look-behind matters: `AtomicFile.ReadAllText(` CONTAINS
        // `File.ReadAllText(`, so a plain substring check flags the fix as the
        // bug. Only the bare BCL calls are the offence.
        var bare = new System.Text.RegularExpressions.Regex(
            @"(?<![A-Za-z0-9_.])File\.(ReadAllText|WriteAllText|ReadAllBytes|WriteAllBytes)\(");
        var offenders = bare.Matches(source).Select(m => m.Value).Distinct().ToList();
        Assert.True(offenders.Count == 0,
            "RecoverySnapshotStore reads/writes files AtomicFile owns; route them through AtomicFile " +
            "(share-delete on read, flush-then-rename on write): " + string.Join(", ", offenders));
    }

    private static string ReadServiceSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DotCharter.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        var path = Path.Combine(new[] { dir!.FullName, "DotCharter.Services" }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"source not found: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    ///     A VISIBLE snapshot always has its sidecar. The file name is a one-way
    ///     hash of the project path (<see cref="RecoverySnapshotStore.ComputeKey" />),
    ///     so the sidecar is the only record of which project a snapshot belongs
    ///     to — and of the original, usually RELATIVE, audio reference, since the
    ///     snapshot's own <c>AudioFileName</c> is rewritten absolute on the way in.
    ///     <para>
    ///         The sidecar used to be written AFTER the snapshot was renamed into
    ///         place, so a crash between the two — the exact moment a crash
    ///         snapshot exists for — left a `.rscp` with no metadata.
    ///         <see cref="RecoverySnapshotStore.ListSessions" /> surfaces that as
    ///         an UNTITLED session (correctly: it has no path to show), so the
    ///         player's titled project came back with Save pointing at nothing and
    ///         the audio path baked absolute — and
    ///         <see cref="RecoverySnapshotStore.IsRecoverable" /> can never gate an
    ///         untitled session, so it re-offered itself on every launch.
    ///     </para>
    ///     A directory squatting on the sidecar's own temp name makes the sidecar
    ///     write fail deterministically, standing in for the crash.
    /// </summary>
    [Fact]
    public void WriteSnapshot_WhoseSidecarFails_LeavesNoUnnamedSnapshot()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        var snapshotPath = _store.GetSnapshotPath(original);
        Directory.CreateDirectory(_store.RootDirectory);
        // AtomicFile.WriteAllText stages "<sidecar>.tmp" first; a directory there
        // makes its FileStream throw before anything is renamed.
        Directory.CreateDirectory(Path.ChangeExtension(snapshotPath, null) + ".rscp.json.tmp");

        Assert.ThrowsAny<Exception>(() => _store.WriteSnapshot(BuildProject(), Metadata(original)));

        Assert.False(File.Exists(snapshotPath), "the snapshot must not be visible without its sidecar");
        Assert.Empty(_store.ListSessions());
        Assert.False(File.Exists(snapshotPath + ".tmp"), "the staged snapshot is swept, not orphaned");
    }

    /// <summary>…and the ordinary write still produces both, with the sidecar
    /// naming the project — so the guard above is about ORDER, not about
    /// refusing to write.</summary>
    [Fact]
    public void WriteSnapshot_ProducesASidecarThatNamesTheProject()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        _store.WriteSnapshot(BuildProject(), Metadata(original));

        var session = Assert.Single(_store.ListSessions());
        Assert.False(session.IsUntitled);
        Assert.Equal(original, session.Metadata.OriginalProjectPath);
        Assert.Equal("song.ogg", session.Metadata.OriginalAudioFileName);
    }

    [Fact]
    public void ComputeKey_IsStable_CaseInsensitive_AndUntitledForEmpty()
    {
        var a = RecoverySnapshotStore.ComputeKey(@"C:\Projects\Song\project.rscp");
        var b = RecoverySnapshotStore.ComputeKey(@"c:\projects\song\PROJECT.rscp");
        var c = RecoverySnapshotStore.ComputeKey(@"C:\Projects\Other\project.rscp");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(16, a.Length);
        Assert.Equal(RecoverySnapshotStore.UntitledKey, RecoverySnapshotStore.ComputeKey(null));
        Assert.Equal(RecoverySnapshotStore.UntitledKey, RecoverySnapshotStore.ComputeKey("  "));
    }
}
