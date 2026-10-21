using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     The stores that hold everything a player would hate to lose — practice
///     profiles (every score, best, streak and phrase-mastery record), the profile
///     registry, AppConfig, keybinds, custom stem models — all persisted with a bare
///     <c>File.WriteAllText</c>, which TRUNCATES the target before it writes. A crash
///     in that window leaves an empty or half-written file and the previous contents
///     are gone.
///
///     <para>The correct pattern already existed in this repo three times
///     (CheckpointService, RecoverySnapshotStore, NamCaptureLibrary) — these four
///     simply never adopted it.</para>
/// </summary>
[CollectionDefinition("SerialDiskIo", DisableParallelization = true)]
public sealed class SerialDiskIoCollection;

// Serialized: the concurrent-reader torn-file test races real disk I/O and
// flakes under the full suite's parallel load (grew structural once the suite
// passed ~4,600 tests, 2026-07-17). It has always passed isolated.
[Collection("SerialDiskIo")]
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-atomic-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string Path_(string name) => System.IO.Path.Combine(_dir, name);

    [Fact]
    public void WritesTheContent()
    {
        var p = Path_("a.json");
        AtomicFile.WriteAllText(p, "hello");
        Assert.Equal("hello", File.ReadAllText(p));
    }

    [Fact]
    public void CreatesTheDirectory()
    {
        var p = System.IO.Path.Combine(_dir, "nested", "deeper", "a.json");
        AtomicFile.WriteAllText(p, "x");
        Assert.True(File.Exists(p));
    }

    [Fact]
    public void ReplacesAnExistingFile()
    {
        var p = Path_("a.json");
        AtomicFile.WriteAllText(p, "old");
        AtomicFile.WriteAllText(p, "new");
        Assert.Equal("new", File.ReadAllText(p));
    }

    /// <summary>
    ///     THE POINT. Simulate the crash: the temp file exists with new (or garbage,
    ///     or truncated) content, but the rename never happened. The ORIGINAL must be
    ///     completely intact — that is the entire difference between this and
    ///     File.WriteAllText, which would already have destroyed it.
    /// </summary>
    [Fact]
    public void ACrashBeforeTheRename_LeavesTheOriginalIntact()
    {
        var p = Path_("scores.json");
        AtomicFile.WriteAllText(p, "EVERY SCORE THE PLAYER HAS");

        // The write died partway through the temp file.
        File.WriteAllText(p + AtomicFile.TempSuffix, "half-writt");

        // The real file has not been touched.
        Assert.Equal("EVERY SCORE THE PLAYER HAS", File.ReadAllText(p));
    }

    /// <summary>No temp file is left behind on the happy path — a stray .tmp next to
    /// every store would be its own kind of mess.</summary>
    [Fact]
    public void LeavesNoTempFileBehind()
    {
        var p = Path_("a.json");
        AtomicFile.WriteAllText(p, "x");
        Assert.False(File.Exists(p + AtomicFile.TempSuffix));
        Assert.Single(Directory.GetFiles(_dir));
    }

    /// <summary>
    ///     The stage is staged under a name nobody can know in advance and is created
    ///     exclusively. A file (or link) another process planted at the old predictable
    ///     <c>&lt;target&gt;.tmp</c> was truncated and written THROUGH, and then renamed
    ///     over the target; now it is neither opened nor touched.
    /// </summary>
    [Fact]
    public void ASquatterOnThePredictableTempName_IsNeitherWrittenThroughNorMoved()
    {
        var p = Path_("a.json");
        Directory.CreateDirectory(_dir);
        var squatter = p + AtomicFile.TempSuffix;
        File.WriteAllText(squatter, "SQUATTER");

        AtomicFile.WriteAllText(p, "real");

        Assert.Equal("real", File.ReadAllText(p));
        Assert.Equal("SQUATTER", File.ReadAllText(squatter));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void ASquatterOnThePredictableTempName_IsLeftAloneByTheBytesWriterToo()
    {
        var p = Path_("a.bin");
        Directory.CreateDirectory(_dir);
        var squatter = p + AtomicFile.TempSuffix;
        File.WriteAllText(squatter, "SQUATTER");

        AtomicFile.WriteAllBytes(p, [1, 2, 3]);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(p));
        Assert.Equal("SQUATTER", File.ReadAllText(squatter));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    /// <summary>
    ///     …and none is left behind on the SAD path either, which is the half
    ///     that was missing. A rename that exhausts the retry budget — an
    ///     antivirus or indexer holding the destination without sharing delete —
    ///     used to leave the staged file parked next to the real one forever:
    ///     <c>MySong.rscp.saving</c> beside <c>MySong.rscp</c>, a file that looks
    ///     like a newer save, is not one, and that nothing in the app ever reads,
    ///     cleans up, or mentions. Every failed save added another.
    ///     <para>The failure must still be reported, and the ORIGINAL must still
    ///     be whole — that is the whole point of the class.</para>
    /// </summary>
    [Fact]
    public void AFailedSwap_ReportsAndLeavesNoStagedFileBehind()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows rename semantics.");

        var p = Path_("project.rscp");
        AtomicFile.WriteAllText(p, "THE PROJECT");

        // A holder that does NOT share delete blocks both renames for the whole
        // bounded retry (WindowsPosixRename_IsRefusedByAHolderThatDoesNotShareDelete).
        using var held = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.ThrowsAny<Exception>(() => AtomicFile.WriteAllText(p, "THE NEXT PROJECT"));

        Assert.False(File.Exists(p + AtomicFile.TempSuffix), "the stage was orphaned");
        Assert.Single(Directory.GetFiles(_dir));
        using (var reader = new StreamReader(held))
            Assert.Equal("THE PROJECT", reader.ReadToEnd());
    }

    /// <summary>The same sweep for the direct callers — the project save stages
    /// a <c>&lt;path&gt;.saving</c> of its own and swaps it through here.</summary>
    [Fact]
    public void ReplaceWithRetry_ThatGivesUp_SweepsTheStageItWasHanded()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows rename semantics.");

        var p = Path_("song.rscp");
        var staged = p + ".saving";
        AtomicFile.WriteAllText(p, "OLD");
        File.WriteAllText(staged, "NEW");

        using var held = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.ThrowsAny<Exception>(() => AtomicFile.ReplaceWithRetry(staged, p));
        Assert.False(File.Exists(staged));
    }

    /// <summary>A reader must never observe a partially-written file. The rename is
    /// what guarantees this; hammering it is a smoke test that the swap really is a
    /// swap and not a truncate-then-fill.
    /// <para>The reader uses <see cref="AtomicFile.ReadAllText"/> — the library's
    /// FileShare.Delete read counterpart (v0.31). The old <c>File.ReadAllText</c>
    /// hammer denied delete, and under full-suite CPU load its zero-gap open/close
    /// loop starved the writer's rename past ANY bounded retry (the documented
    /// flake — the writer threw after its full backoff budget). The torn-file
    /// proof is unchanged: this reader hammers just as hard and must still only
    /// ever see one of the two whole files; a truncate-then-fill would show it
    /// torn content exactly the same.</para>
    /// <para>2026-08-15: it flaked AGAIN under a full parallel gate, twice, and
    /// both times the failure was the WRITER's <c>UnauthorizedAccessException</c>
    /// out of <c>ReplaceWithRetry</c> — never a torn read. The share-delete reader
    /// had not actually let <c>MoveFileEx</c> through (it is denied by any open
    /// handle); it had only made the retry's gaps more likely. The writer now
    /// renames with POSIX semantics first, which a share-delete handle DOES let
    /// through — <see cref="ReplaceWithRetry_SucceedsWhileAShareDeleteReaderHoldsTheDestination"/>
    /// is the deterministic version of this race.</para></summary>
    [Fact]
    public async Task AConcurrentReader_NeverSeesATornFile()
    {
        var p = Path_("hot.json");
        var a = new string('a', 20_000);
        var b = new string('b', 20_000);
        AtomicFile.WriteAllText(p, a);

        using var cts = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                string seen;
                try { seen = AtomicFile.ReadAllText(p); }
                catch (IOException) { continue; }   // transient sharing during the swap
                // Whatever we saw, it must be ONE of the two whole files.
                Assert.True(seen == a || seen == b, $"torn read: {seen.Length} bytes");
            }
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 60; i++) AtomicFile.WriteAllText(p, i % 2 == 0 ? b : a);
        cts.Cancel();
        await reader;
    }

    /// <summary>
    ///     THE FLAKE'S ROOT, made deterministic. The torn-file test above failed
    ///     twice under a full parallel gate — never with a torn read, always with
    ///     <c>UnauthorizedAccessException: Access to the path is denied</c> out of
    ///     <c>ReplaceWithRetry</c>: on Windows <c>MoveFileEx</c> is denied by ANY
    ///     open handle on the destination, share-delete or not, so the writer only
    ///     ever won in the gaps between the reader's open/close, and under CPU
    ///     starvation the gap failed to arrive inside the 430 ms retry budget.
    ///     <para>
    ///         So this holds a <c>FileShare.ReadWrite | Delete</c> handle on the
    ///         destination for the WHOLE call and requires the swap to succeed
    ///         anyway — which only the POSIX-semantics rename
    ///         (<see cref="WindowsPosixRename" />) can do — while the held handle
    ///         goes on reading the OLD bytes whole and the name reads the NEW ones.
    ///         With the classic rename alone this throws after the full backoff.
    ///     </para>
    /// </summary>
    [Fact]
    public void ReplaceWithRetry_SucceedsWhileAShareDeleteReaderHoldsTheDestination()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows rename semantics.");

        var p = Path_("hot.json");
        AtomicFile.WriteAllText(p, "OLD-CONTENT");
        File.WriteAllText(p + AtomicFile.TempSuffix, "NEW-CONTENT");

        using var held = new FileStream(p, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        AtomicFile.ReplaceWithRetry(p + AtomicFile.TempSuffix, p);
        TestContext.Current.TestOutputHelper?.WriteLine($"swap under a held reader: {sw.Elapsed.TotalMilliseconds:0.#} ms");

        using (var reader = new StreamReader(held))
            Assert.Equal("OLD-CONTENT", reader.ReadToEnd()); // the held handle: the old file, whole
        Assert.Equal("NEW-CONTENT", AtomicFile.ReadAllText(p)); // the name: the new file
        Assert.False(File.Exists(p + AtomicFile.TempSuffix));
    }

    /// <summary>
    ///     A holder that did NOT share delete — the antivirus / indexer / plain
    ///     <c>File.ReadAllText</c> case — is still a real block, and that is what
    ///     the bounded retry is for. Pinned so nobody reads the test above as
    ///     "the rename cannot fail any more" and deletes the retry.
    /// </summary>
    [Fact]
    public void WindowsPosixRename_IsRefusedByAHolderThatDoesNotShareDelete()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows rename semantics.");

        var p = Path_("held.json");
        AtomicFile.WriteAllText(p, "OLD");
        File.WriteAllText(p + AtomicFile.TempSuffix, "NEW");

        using var held = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.False(WindowsPosixRename.TryReplace(p + AtomicFile.TempSuffix, p));
        Assert.True(File.Exists(p + AtomicFile.TempSuffix)); // nothing moved
    }

    /// <summary>False, never a throw: the caller falls back to the classic rename,
    /// which produces the exception the retry loop knows how to classify.</summary>
    [Fact]
    public void WindowsPosixRename_WithAMissingSource_ReturnsFalseInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        Assert.False(WindowsPosixRename.TryReplace(Path_("nope.tmp"), Path_("nope.json")));
    }

    [Fact]
    public void Bytes_RoundTrip()
    {
        var p = Path_("a.bin");
        var data = new byte[] { 1, 2, 3, 250, 0, 77 };
        AtomicFile.WriteAllBytes(p, data);
        Assert.Equal(data, File.ReadAllBytes(p));
    }

    /// <summary>
    ///     The durability half of the pattern, exposed for writers that stream a
    ///     large artifact into the temp file through some other API (a zip, an
    ///     encoder) and therefore cannot go through <c>WriteAllText</c>. Flushing
    ///     must not disturb the contents — it opens the file for ReadWrite purely
    ///     to issue <c>FlushFileBuffers</c>.
    /// </summary>
    [Fact]
    public void FlushFile_ForcesTheBytesDownWithoutChangingThem()
    {
        Directory.CreateDirectory(_dir);
        var p = Path_("staged.bin");
        var data = new byte[4096];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
        File.WriteAllBytes(p, data);

        AtomicFile.FlushFile(p);

        Assert.Equal(data, File.ReadAllBytes(p));
    }

    /// <summary>Staged-then-swap end to end: flush the temp file, then rename it in.</summary>
    [Fact]
    public void FlushFile_ThenReplaceWithRetry_IsTheStagedSwapPattern()
    {
        Directory.CreateDirectory(_dir);
        var target = Path_("target.bin");
        File.WriteAllBytes(target, [9, 9, 9]);
        var temp = target + AtomicFile.TempSuffix;
        File.WriteAllBytes(temp, [1, 2, 3, 4]);

        AtomicFile.FlushFile(temp);
        AtomicFile.ReplaceWithRetry(temp, target);

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(target));
        Assert.False(File.Exists(temp));
    }

    /// <summary>A missing file is a caller bug, not something to swallow.</summary>
    [Fact]
    public void FlushFile_ThrowsWhenTheFileIsNotThere()
    {
        Directory.CreateDirectory(_dir);
        Assert.Throws<FileNotFoundException>(() => AtomicFile.FlushFile(Path_("nope.bin")));
    }

    /// <summary>
    ///     Nothing that writes DURABLE user data may swap a staged file in with a
    ///     bare <see cref="File.Move(string,string)" />.
    ///     <para>
    ///         On Windows that rename FAILS if anything currently has the
    ///         destination open — an antivirus or indexer scan of the file just
    ///         replaced is the realistic case — and the
    ///         <see cref="IOException" />/<see cref="UnauthorizedAccessException" />
    ///         escapes into whatever user action asked for the write.
    ///         <see cref="AtomicFile.ReplaceWithRetry" /> is the same swap with 16
    ///         bounded-exponential attempts (~430 ms) in front of it, and it is
    ///         what every other stage-then-swap in the app already used —
    ///         `CheckpointService.Create` was the one that did not, so a scan in
    ///         that window threw out of a user-initiated "create checkpoint" and
    ///         left the .tmp orphaned.
    ///     </para>
    ///     <para>
    ///         Scoped to the two folders that own durable user state (checkpoints,
    ///         recovery, config, backups). Caches and downloads elsewhere in the
    ///         solution rename freely by design: a lost cache entry is a miss.
    ///     </para>
    /// </summary>
    [Fact]
    public void NoDurableStateWriter_SwapsAStagedFileWithABareFileMove()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var scanned = 0;
        var routed = 0;

        foreach (var folder in new[]
                 {
                     Path.Combine("DotCharter.Services", "State"),
                     Path.Combine("DotCharter.Services", "Infrastructure"),
                     Path.Combine("DotCharter.Tone", "Infrastructure")
                 })
        {
            var dir = Path.Combine(root, folder);
            Assert.True(Directory.Exists(dir), $"missing {dir}");

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // AtomicFile IS the sanctioned File.Move.
                if (Path.GetFileName(file) == "AtomicFile.cs") continue;

                scanned++;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i];
                    if (code.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    if (code.Contains("File.Move(", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {code.Trim()}");
                    if (code.Contains("AtomicFile.ReplaceWithRetry(", StringComparison.Ordinal)) routed++;
                }
            }
        }

        Assert.True(scanned >= 10, $"only {scanned} source files scanned — the sweep is looking in the wrong place.");
        Assert.True(routed >= 1, "no staged swap found at all — this guard has stopped guarding anything.");
        Assert.True(offenders.Count == 0,
            "A durable-state writer swaps a staged file with a bare File.Move. On Windows that rename fails " +
            "while anything holds the destination open (AV, indexer) and the throw escapes the user's action " +
            "— use AtomicFile.ReplaceWithRetry:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    ///     A file that <see cref="AtomicFile" /> WRITES must be read back through
    ///     <see cref="AtomicFile.ReadAllText" /> / <see cref="AtomicFile.ReadAllBytes" />.
    ///     <para>
    ///         A plain <c>File.ReadAllText</c> takes a share mode that DENIES
    ///         delete, which is precisely the save/load collision the write-side
    ///         rename retry exists to absorb: the reader's open handle blocks the
    ///         writer's rename, and past the retry budget the save is logged and
    ///         dropped. The counterpart opens with
    ///         <c>FileShare.ReadWrite | FileShare.Delete</c>, so the rename
    ///         proceeds while the handle keeps reading the OLD content in full —
    ///         the sharing mode is the structural fix and the retry only the
    ///         fallback for readers we do not own.
    ///     </para>
    ///     <para>
    ///         Per FILE, because a type that writes its own store through
    ///         <c>AtomicFile</c> and then reads it with a bare <c>File.Read*</c>
    ///         is the shape every one of these took — nine of them, from the app
    ///         config and keybinds to the profile registry, the practice plan and
    ///         all three DPAPI token stores.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryAtomicFileWriter_ReadsItsOwnFileThroughAtomicFileToo()
    {
        var root = TestPaths.RepoRoot();
        var offenders = new List<string>();
        var writers = 0;

        foreach (var project in new[] { "DotCharter.Services", "DotCharter.Tone", "DotCharter.UI" })
        {
            foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (Path.GetFileName(file) == "AtomicFile.cs") continue;

                var lines = File.ReadAllLines(file);
                if (!lines.Any(l => l.Contains("AtomicFile.WriteAll", StringComparison.Ordinal))) continue;
                writers++;

                for (var i = 0; i < lines.Length; i++)
                {
                    var code = lines[i];
                    if (code.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    if (code.Contains("AtomicFile.ReadAll", StringComparison.Ordinal)) continue;
                    if (code.Contains("File.ReadAllText(", StringComparison.Ordinal) ||
                        code.Contains("File.ReadAllBytes(", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {code.Trim()}");
                    }
                }
            }
        }

        Assert.True(writers >= 10, $"only {writers} AtomicFile writers found — the sweep stopped sweeping.");
        Assert.True(offenders.Count == 0,
            "A type writes its store through AtomicFile and reads it back with a plain File.Read*, whose " +
            "share mode denies delete and blocks the writer's rename — use AtomicFile.ReadAllText / " +
            "ReadAllBytes:\n  " + string.Join("\n  ", offenders));
    }
}
