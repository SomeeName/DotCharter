using DotCharter.Services.Infrastructure.Recovery;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Recovery;

public sealed class RecoverySnapshotStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _originalsDir;
    private readonly RecoverySnapshotStore _store;

    [Fact]
    public void Unreadability_after_enumeration_never_authorizes_recovery_deletion()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows share modes.");
        var original = Path.Combine(_originalsDir, "song.dcproj");
        _store.WriteSnapshot(BuildProject(), Metadata(original));
        var session = Assert.Single(_store.ListSessions());
        var locked = new LockedEnumerationStore(_store, session);
        using var recovery = new CrashRecoveryService(locked, null!, null!, null!, new ProjectState(),
            null!, null!, new FakeAppLogger(), new TestUiDispatcher());
        using (var held = new FileStream(session.SnapshotPath, FileMode.Open, FileAccess.Write, FileShare.Delete))
        {
            Assert.Empty(recovery.FindRecoverableSessions());
            Assert.True(File.Exists(session.SnapshotPath));
            Assert.True(File.Exists(session.SidecarPath));
        }

        Assert.Single(recovery.FindRecoverableSessions());
    }

    private sealed class LockedEnumerationStore(IRecoverySnapshotStore inner, RecoverableSession session) : IRecoverySnapshotStore
    {
        public string RootDirectory => inner.RootDirectory;
        public string GetSnapshotPath(string? path) => inner.GetSnapshotPath(path);
        public void WriteSnapshot(ChartProject project, RecoverySnapshotMetadata metadata) => inner.WriteSnapshot(project, metadata);
        public IReadOnlyList<RecoverableSession> ListSessions() => [session];
        public bool IsRecoverable(RecoverableSession candidate) => inner.IsRecoverable(candidate);
        public bool DeleteIfSuperseded(RecoverableSession candidate) => inner.DeleteIfSuperseded(candidate);
        public void DeleteSnapshot(string? path) => inner.DeleteSnapshot(path);
        public void DeleteSession(RecoverableSession candidate) => inner.DeleteSession(candidate);
        public ChartProject LoadSnapshot(string path) => inner.LoadSnapshot(path);
    }

    [Fact]
    public void A_different_generation_is_not_proof_that_the_original_supersedes_it()
    {
        var original = Path.Combine(_originalsDir, "song.dcproj");
        _store.WriteSnapshot(BuildProject(), Metadata(original));
        var session = Assert.Single(_store.ListSessions());
        File.WriteAllText(session.SnapshotPath, "different generation");
        File.WriteAllText(original, "newer original");
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(1));

        Assert.False(_store.DeleteIfSuperseded(session));
        Assert.True(File.Exists(session.SnapshotPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Supersession_requires_a_known_matching_generation(bool legacy)
    {
        var original = Path.Combine(_originalsDir, "song.dcproj");
        var project = BuildProject();
        _store.WriteSnapshot(project, Metadata(original));
        var session = Assert.Single(_store.ListSessions());
        new DotCharter.Services.Parsing.Rscp.RscpChartSerializer().Save(original, project);
        File.SetLastWriteTimeUtc(session.SnapshotPath, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(original, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        if (legacy) session.Metadata.SnapshotSha256 = "";

        Assert.Equal(!legacy, _store.DeleteIfSuperseded(session));
        Assert.Equal(legacy, File.Exists(session.SnapshotPath));
        Assert.Equal(legacy, File.Exists(session.SidecarPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_corrupt_or_foreign_newer_original_does_not_authorize_deletion(bool foreign)
    {
        var original = Path.Combine(_originalsDir, "song.dcproj");
        _store.WriteSnapshot(BuildProject(), Metadata(original));
        var session = Assert.Single(_store.ListSessions());
        if (foreign) new DotCharter.Services.Parsing.Rscp.RscpChartSerializer().Save(original, BuildProject());
        else File.WriteAllText(original, "corrupt project");
        File.SetLastWriteTimeUtc(original, DateTime.UtcNow.AddDays(1));

        Assert.False(_store.DeleteIfSuperseded(session));
        Assert.True(File.Exists(session.SnapshotPath));
        Assert.True(File.Exists(session.SidecarPath));
    }

    public RecoverySnapshotStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dc_recovery_{Guid.NewGuid():N}");
        _originalsDir = Path.Combine(_root, "originals");
        Directory.CreateDirectory(_originalsDir);
        _store = new RecoverySnapshotStore(Path.Combine(_root, "recovery"));
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void Snapshotting_DoesNotReuseOrTruncateAPredictableStage()
    {
        var original = Path.Combine(_originalsDir, "song.dcproj");
        Directory.CreateDirectory(_store.RootDirectory);
        var planted = _store.GetSnapshotPath(original) + ".tmp";
        File.WriteAllText(planted, "unrelated file");
        using var held = new FileStream(planted, FileMode.Open, FileAccess.Read, FileShare.Read);

        _store.WriteSnapshot(BuildProject(), Metadata(original));

        Assert.Equal("unrelated file", File.ReadAllText(planted));
        Assert.Single(_store.ListSessions());
    }

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
    public void An_interrupted_snapshot_swap_keeps_the_previous_generations_metadata()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Relies on Windows file locking.");
        var original = Path.Combine(_originalsDir, "song.dcproj");
        var previous = BuildProject();
        previous.AudioFileName = "A.wav";
        var previousMetadata = Metadata(original);
        previousMetadata.OriginalAudioFileName = "A.wav";
        _store.WriteSnapshot(previous, previousMetadata);
        var next = BuildProject();
        next.AudioFileName = "B.wav";
        var nextMetadata = Metadata(original);
        nextMetadata.OriginalAudioFileName = "B.wav";

        using (new FileStream(_store.GetSnapshotPath(original), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<Exception>(() => _store.WriteSnapshot(next, nextMetadata));

        var restarted = new RecoverySnapshotStore(_store.RootDirectory);
        var recovered = Assert.Single(restarted.ListSessions());
        Assert.Equal("A.wav", restarted.LoadSnapshot(recovered.SnapshotPath).AudioFileName);
        Assert.Equal("A.wav", recovered.Metadata.OriginalAudioFileName);
        Assert.Equal(original, recovered.Metadata.OriginalProjectPath);
        restarted.WriteSnapshot(next, nextMetadata);
        recovered = Assert.Single(restarted.ListSessions());
        Assert.Equal("B.wav", restarted.LoadSnapshot(recovered.SnapshotPath).AudioFileName);
        Assert.Equal("B.wav", recovered.Metadata.OriginalAudioFileName);
    }

    [Fact]
    public void Metadata_for_different_snapshot_bytes_is_never_adopted()
    {
        var original = Path.Combine(_originalsDir, "song.dcproj");
        _store.WriteSnapshot(BuildProject(), Metadata(original));
        var other = BuildProject();
        other.AudioFileName = "unrelated.wav";
        new DotCharter.Services.Parsing.Rscp.RscpChartSerializer().Save(_store.GetSnapshotPath(original), other);

        Assert.Empty(_store.ListSessions());
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
    ///         shape, not a synthetic one.
    ///     </para>
    ///     <para>
    ///         #607: this used to be a reader thread hammering <c>ListSessions</c>
    ///         against 40 writes on the SAME store, whose <c>_sync</c> lock
    ///         serializes the two: no read could ever overlap a write, and the test
    ///         passed with a delete-denying read. Now a second store (the startup
    ///         enumeration's) is parked with the sidecar handle OPEN, and the
    ///         autosave's store publishes a snapshot at exactly that moment; a read
    ///         that denies delete refuses the sidecar's rename and the write fails.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ListSessions_DuringConcurrentWrites_NeverLosesTheOriginalProjectPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var original = Path.Combine(_originalsDir, "song.rscp");
        // Seed one complete snapshot: before the first write there is legitimately
        // no sidecar, and "untitled" is the correct answer for that. The same
        // project again later serializes to the same bytes, so the parked read's
        // OLD sidecar still describes the snapshot it then hashes.
        var project = BuildProject();
        _store.WriteSnapshot(project, Metadata(original));

        var enumeration = new RecoverySnapshotStore(_store.RootDirectory);
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reads = 0;
        enumeration.SidecarOpenForTesting = () =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            open.SetResult();
            BoundedWait.For(release, "the test to release the parked sidecar read");
        };
        var listing = Task.Run(enumeration.ListSessions, ct);

        try
        {
            await BoundedWait.ForAsync(Task.WhenAny(open.Task, listing), "the enumeration to open the sidecar", ct);
            Assert.False(listing.IsCompleted, "ListSessions returned without ever holding a sidecar open.");
            _store.WriteSnapshot(project, Metadata(original));
        }
        finally
        {
            release.Set();
        }

        var listed = Assert.Single(await BoundedWait.ForAsync(listing, "the parked enumeration to finish", ct));
        Assert.False(listed.IsUntitled, "the enumeration degraded a titled session to an untitled one");
        Assert.Equal(original, listed.Metadata.OriginalProjectPath);
        Assert.Equal(original, Assert.Single(_store.ListSessions()).Metadata.OriginalProjectPath);
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
    ///     A directory squatting on the sidecar's own file name makes the sidecar
    ///     swap fail deterministically, standing in for the crash.
    /// </summary>
    [Fact]
    public void WriteSnapshot_WhoseSidecarFails_LeavesNoUnnamedSnapshot()
    {
        var original = Path.Combine(_originalsDir, "song.rscp");
        var snapshotPath = _store.GetSnapshotPath(original);
        Directory.CreateDirectory(_store.RootDirectory);
        // AtomicFile.WriteAllText stages a uniquely named sibling and renames it over
        // the sidecar; a directory sitting on the sidecar's name makes that rename
        // fail (after the bounded retry), with the stage swept on the way out.
        Directory.CreateDirectory(Path.ChangeExtension(snapshotPath, null) + ".rscp.json");

        Assert.ThrowsAny<Exception>(() => _store.WriteSnapshot(BuildProject(), Metadata(original)));

        Assert.False(File.Exists(snapshotPath), "the snapshot must not be visible without its sidecar");
        Assert.Empty(_store.ListSessions());
        Assert.Empty(Directory.GetFiles(_store.RootDirectory, "*.tmp"));
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
