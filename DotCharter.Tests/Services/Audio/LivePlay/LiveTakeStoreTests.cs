using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.LivePlay.Takes;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class LiveTakeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
    private readonly AppConfig _config = new() { LivePlayTakeRecorderEnabled = true };

    private LiveTakeStore NewStore(IStemEncoder? encoder = null, IPracticeProfileService? practice = null,
        IAppLogger? logger = null)
        => new(_dir, encoder ?? new FakeTakeEncoder(available: false), () => _config, practice, logger);

    // ---------- start ----------

    [Fact]
    public void Disabled_ByDefault_NoRecorderStarts()
    {
        _config.LivePlayTakeRecorderEnabled = false;
        Assert.False(NewStore().IsEnabled);
        Assert.Null(NewStore().TryStartTake(48000));
    }

    [Fact]
    public void Enabled_StartsRecorder_InTakesDirectory()
    {
        using var recorder = NewStore().TryStartTake(48000);
        Assert.NotNull(recorder);
        Assert.StartsWith(_dir, recorder!.FilePath);
        Assert.EndsWith(".wav", recorder.FilePath);
    }

    [Fact]
    public void NoInputSampleRate_NoRecorder()
        => Assert.Null(NewStore().TryStartTake(0));

    // ---------- finalize ----------

    [Fact]
    public async Task Finalize_WithoutFfmpeg_KeepsWav()
    {
        var store = NewStore();
        var recorder = store.TryStartTake(48000)!;
        recorder.Write(new float[4800]);

        var name = await store.FinalizeTakeAsync(recorder);

        Assert.NotNull(name);
        Assert.EndsWith(".wav", name);
        Assert.True(File.Exists(Path.Combine(_dir, name!)));
    }

    [Fact]
    public async Task Finalize_WithFfmpeg_EncodesOpus_AndDeletesWav()
    {
        var store = NewStore(new FakeTakeEncoder(available: true));
        var recorder = store.TryStartTake(48000)!;
        var wavPath = recorder.FilePath;
        recorder.Write(new float[4800]);

        var name = await store.FinalizeTakeAsync(recorder);

        Assert.NotNull(name);
        Assert.EndsWith(".opus", name);
        Assert.True(File.Exists(Path.Combine(_dir, name!)));
        Assert.False(File.Exists(wavPath));
    }

    [Fact]
    public async Task Finalize_EncoderFallsBackToWav_KeepsWav()
    {
        // ffmpeg present but the encode fails → the seam returns the WAV copy.
        var store = NewStore(new FakeTakeEncoder(available: true, failEncode: true));
        var recorder = store.TryStartTake(48000)!;
        recorder.Write(new float[4800]);

        var name = await store.FinalizeTakeAsync(recorder);

        Assert.NotNull(name);
        Assert.EndsWith(".wav", name);
        Assert.True(File.Exists(Path.Combine(_dir, name!)));
    }

    [Fact]
    public async Task Finalize_EmptyTake_ReturnsNull_AndDeletesFile()
    {
        var store = NewStore();
        var recorder = store.TryStartTake(48000)!;
        var path = recorder.FilePath;

        var name = await store.FinalizeTakeAsync(recorder);

        Assert.Null(name);
        Assert.False(File.Exists(path));
    }

    // ---------- cleanup policy (pure) ----------

    private static LiveTakeStore.TakeFileInfo File_(string name, long size, int ageDays)
        => new(name, size, DateTime.UtcNow.AddDays(-ageDays));

    [Fact]
    public void SelectDeletions_RemovesOrphans_KeepsReferenced()
    {
        var files = new[] { File_("kept.opus", 10, 1), File_("orphan.wav", 10, 2) };
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kept.opus" };

        var deletions = LiveTakeStore.SelectDeletions(files, referenced, maxTotalBytes: 1000);

        Assert.Equal(["orphan.wav"], deletions);
    }

    [Fact]
    public void SelectDeletions_OverCap_DeletesOldestReferencedFirst()
    {
        var files = new[]
        {
            File_("newest.opus", 600, ageDays: 0),
            File_("middle.opus", 600, ageDays: 1),
            File_("oldest.opus", 600, ageDays: 2),
        };
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "newest.opus", "middle.opus", "oldest.opus" };

        var deletions = LiveTakeStore.SelectDeletions(files, referenced, maxTotalBytes: 1300);

        // 1800 total → dropping the oldest (600) reaches 1200 ≤ 1300.
        Assert.Equal(["oldest.opus"], deletions);
    }

    [Fact]
    public void SelectDeletions_NothingReferenced_ButFilesPresent_DeletesNothing()
    {
        // "No take is referenced" is indistinguishable here from "the store that
        // names them could not be read", and the two outcomes are a few stale
        // files versus the player's whole listen-back library.
        var files = new[] { File_("take-1.wav", 100, 1), File_("take-2.wav", 100, 2) };

        Assert.Empty(LiveTakeStore.SelectDeletions(files,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), maxTotalBytes: 10));
    }

    [Fact]
    public void SelectDeletions_UnderCap_NoReferencedDeleted()
    {
        var files = new[] { File_("a.opus", 100, 0), File_("b.opus", 100, 1) };
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.opus", "b.opus" };

        Assert.Empty(LiveTakeStore.SelectDeletions(files, referenced, maxTotalBytes: 1000));
    }

    // ---------- cleanup end-to-end against the practice store ----------

    [Fact]
    public async Task Cleanup_DeletesOrphans_KeepsSessionReferencedTakes()
    {
        var practice = new PracticeProfileService(
            logger: null, filePath: Path.Combine(_dir, "practice-profiles.json"));
        practice.Record("song", "lead",
            new LivePlaySessionResult(100, 0.9, 9, 1, 20, 5, 30, 100, 0.5));
        Assert.True(practice.AttachTakeFile("song", "lead", "referenced.wav"));

        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "referenced.wav"), new byte[10],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "orphan.wav"), new byte[10],
            TestContext.Current.CancellationToken);

        await NewStore(practice: practice).CleanupAsync();

        Assert.True(File.Exists(Path.Combine(_dir, "referenced.wav")));
        Assert.False(File.Exists(Path.Combine(_dir, "orphan.wav")));
    }

    /// <summary>
    ///     The sweep deletes on the strength of what the practice store does NOT
    ///     mention. A store that could not be READ hands back an empty snapshot, so
    ///     every take — starred included, the star being consulted only by the
    ///     cap-eviction pass — would read as orphaned, on a sweep that runs after
    ///     every run and on every take delete.
    /// </summary>
    [Fact]
    public async Task Cleanup_DegradedPracticeStore_DeletesNothing_AndSaysSo()
    {
        var storePath = Path.Combine(_dir, "practice-profiles.json");
        var practice = new PracticeProfileService(logger: null, filePath: storePath);
        practice.Record("song", "lead", new LivePlaySessionResult(100, 0.9, 9, 1, 20, 5, 30, 100, 0.5));
        Assert.True(practice.AttachTakeFile("song", "lead", "referenced.wav"));
        practice.InvalidateCache(); // force the next read to hit the file

        await File.WriteAllBytesAsync(Path.Combine(_dir, "referenced.wav"), new byte[10],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "orphan.wav"), new byte[10],
            TestContext.Current.CancellationToken);

        var logger = new FakeAppLogger();
        // FileShare.None: exactly the antivirus / cloud-sync / backup hold the
        // degraded path exists for. AtomicFile.ReadAllText is denied and throws.
        using (new FileStream(storePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(practice.IsStoreAuthoritative);
            await NewStore(practice: practice, logger: logger).CleanupAsync();
        }

        Assert.True(File.Exists(Path.Combine(_dir, "referenced.wav")));
        Assert.True(File.Exists(Path.Combine(_dir, "orphan.wav")));
        // Only CleanupAsync's early return logs this — the pure-function guard is
        // silent, so this pins the two halves independently.
        Assert.Contains(logger.Entries,
            e => e.Level == "WARN" && e.Message.Contains("Take cleanup skipped", StringComparison.Ordinal));

        // …and it heals: once the hold is gone the store reads again and the
        // sweep resumes deleting exactly what it did before.
        Assert.True(practice.IsStoreAuthoritative);
        await NewStore(practice: practice).CleanupAsync();
        Assert.True(File.Exists(Path.Combine(_dir, "referenced.wav")));
        Assert.False(File.Exists(Path.Combine(_dir, "orphan.wav")));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>Fake ffmpeg seam: mirrors <see cref="FfmpegStemEncoder"/>'s
    /// contract (encode to the requested codec, or fall back to a WAV copy).</summary>
    private sealed class FakeTakeEncoder(bool available, bool failEncode = false) : IStemEncoder
    {
        public StemCodec PreferredCodec => available ? StemCodec.Opus : StemCodec.Wav;
        public bool IsExternalEncoderAvailable => available;

        public Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec codec,
            CancellationToken ct = default)
        {
            if (!available || failEncode || codec == StemCodec.Wav)
            {
                var wavDest = destPathWithoutExtension + ".wav";
                if (!string.Equals(wavPath, wavDest, StringComparison.OrdinalIgnoreCase))
                    File.Copy(wavPath, wavDest, overwrite: true);
                return Task.FromResult(wavDest);
            }

            var dest = destPathWithoutExtension + "." + codec.FileExtension();
            File.WriteAllBytes(dest, [1, 2, 3]);
            return Task.FromResult(dest);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec codec, string destWavPath,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
