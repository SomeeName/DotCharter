using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>The stem folder's exclusion: only a sharing violation means busy, a lock that
/// cannot be opened never hides stems, and a publisher waits a bounded time for a probe.
/// Every wait is the injected delay, so no case sleeps.</summary>
public sealed class StemFolderLockTests : IDisposable
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int AccessDenied = unchecked((int)0x80070005);

    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter-stemlock-").FullName;
    private readonly StemCacheIdentity _identity = StemCacheIdentity.ForLibrary("lib:LOCK");
    private int _decodes;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string Destination => Directory.CreateDirectory(Path.Combine(_root, "stems")).FullName;
    private string LockFile => Path.Combine(Destination, ".stemcache-lock");
    private string Pending => Path.Combine(Destination, StemCacheExpansion.PendingFileName);

    public void Dispose()
    {
        if (File.Exists(LockFile)) File.SetAttributes(LockFile, FileAttributes.Normal);
        BestEffortDelete.Directory(_root);
    }

    [Fact]
    public void Only_a_sharing_or_lock_violation_reads_as_busy()
    {
        using (HoldLock())
        {
            var held = Assert.Throws<IOException>(() => StemCacheExpansion.Acquire(Destination));
            Assert.Equal(SharingViolation, held.HResult);
            Assert.Null(StemCacheExpansion.TryAcquire(Destination, out var busy));
            Assert.Null(busy);
        }

        MakeLockUnopenable();
        Assert.Null(StemCacheExpansion.TryAcquire(Destination, out var denied));
        Assert.IsType<UnauthorizedAccessException>(denied);
        Assert.True(StemCacheExpansion.IsBusy(new IOException("lock", LockViolation)));
        Assert.False(StemCacheExpansion.IsBusy(new IOException("denied", AccessDenied)));
        Assert.False(StemCacheExpansion.IsBusy(new UnauthorizedAccessException()));
    }

    [Fact]
    public async Task A_lock_file_that_cannot_be_opened_still_shows_the_folders_stems()
    {
        await ExpandAsync();
        MakeLockUnopenable();
        var before = Snapshot();
        var logger = new FakeAppLogger();

        Assert.Equal(2, Probe(logger).Recovered.Count);
        Assert.Equal(2, Probe(logger).Recovered.Count);

        Assert.Equal(before, Snapshot());
        var warning = Assert.Single(logger.Snapshot(), entry => entry.Level == "WARN");
        Assert.Contains(nameof(UnauthorizedAccessException), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public async Task An_unopenable_lock_keeps_a_pending_publication_hidden_and_deletes_nothing()
    {
        var key = await ExpandAsync();
        // A complete commit whose pending record a probe HOLDING the lock validates and clears.
        File.WriteAllText(Pending, key.DirectoryToken);
        MakeLockUnopenable();
        var before = Snapshot();

        Assert.Empty(Probe().Recovered);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task An_unopenable_lock_never_turns_cached_stems_into_a_separation_request()
    {
        var key = await ExpandAsync();
        MakeLockUnopenable();
        var delays = 0;
        var store = Store((_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });
        var archive = File.ReadAllBytes(store.ArchivePath(key));
        var before = Snapshot();
        var coordinator = new StemCacheCoordinator(store);
        var decoded = _decodes;

        // The complete expansion is checked without the exclusion and served as it is.
        var served = await coordinator.TryLoadCachedAsync(_identity, Audio(), Destination, Ct);
        Assert.NotNull(served);
        Assert.Equal(decoded, _decodes);

        // The separation run refuses to regenerate while HasArchive holds, and the mixer
        // still finds the WAVs: nothing reads as "no stems, separate again".
        Assert.Equal(0, delays);
        Assert.True(coordinator.HasArchive(_identity, Audio()));
        Assert.Equal(archive, File.ReadAllBytes(store.ArchivePath(key)));
        Assert.Equal(before, Snapshot());
        Assert.Equal(2, Probe().Recovered.Count);
    }

    [Fact]
    public async Task A_cached_load_waits_for_a_probe_holding_the_folder_then_serves_the_expanded_set()
    {
        var key = await ExpandAsync();
        var decoded = _decodes;
        var holder = HoldLock();
        var delays = 0;
        var store = Store((wait, _) =>
        {
            Assert.Equal(StemCacheExpansion.BusyRetryDelay, wait);
            if (++delays == 3) holder.Dispose();
            return Task.CompletedTask;
        });

        var tracks = await store.ExtractAsync(key, Destination, Ct);

        Assert.Equal(3, delays);
        Assert.Equal(2, tracks.Count);
        Assert.Equal(decoded, _decodes);
    }

    [Fact]
    public async Task A_folder_that_stays_busy_fails_the_cached_load_after_the_bound_and_touches_nothing()
    {
        var key = await ExpandAsync();
        var decoded = _decodes;
        var before = Snapshot();
        using var holder = HoldLock();
        var delays = 0;
        var store = Store((_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        var failure = await Assert.ThrowsAsync<IOException>(() => store.ExtractAsync(key, Destination, Ct));

        Assert.Equal(SharingViolation, failure.HResult);
        Assert.Equal(StemCacheExpansion.BusyRetries, delays);
        Assert.Equal(decoded, _decodes);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task A_publication_waits_for_a_probe_that_took_the_folder_during_the_decode()
    {
        var key = await SeedAsync();
        var gate = new Lock();
        FileStream? holder = null;
        var delays = 0;
        var store = Store((_, _) =>
        {
            if (++delays == 3) holder!.Dispose();
            return Task.CompletedTask;
        }, () =>
        {
            lock (gate) holder ??= HoldLock();
        });

        var tracks = await store.ExtractAsync(key, Destination, Ct);

        Assert.Equal(3, delays);
        Assert.Equal(2, tracks.Count);
        Assert.False(File.Exists(Pending));
        Assert.Equal(2, Probe().Recovered.Count);
    }

    [Fact]
    public async Task A_folder_that_stays_busy_fails_the_publication_after_the_bound_and_leaves_no_partial_set()
    {
        var key = await SeedAsync();
        var gate = new Lock();
        FileStream? holder = null;
        var delays = 0;
        var store = Store((_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        }, () =>
        {
            lock (gate) holder ??= HoldLock();
        });

        try
        {
            var failure = await Assert.ThrowsAsync<IOException>(() => store.ExtractAsync(key, Destination, Ct));

            Assert.Equal(SharingViolation, failure.HResult);
            Assert.Equal(StemCacheExpansion.BusyRetries, delays);
            Assert.Empty(Directory.EnumerateFiles(Destination, "*.wav"));
            // The started publication's record stays: the folder reads as hidden until a
            // publication completes, never as a set a probe may adopt.
            Assert.True(File.Exists(Pending));
            Assert.Empty(Directory.EnumerateDirectories(_root, "stems.restore-*"));
            Assert.True(File.Exists(store.ArchivePath(key)));
        }
        finally
        {
            holder?.Dispose();
        }

        Assert.Equal(2, (await Store().ExtractAsync(key, Destination, Ct)).Count);
        Assert.Equal(2, Probe().Recovered.Count);
    }

    /// <summary>#656 row 1: a copied stem folder keeps the lock file's read-only
    /// attribute. The exclusion still opens (read access, unshared), so it still excludes
    /// and a publication works.</summary>
    [Fact]
    public async Task A_read_only_lock_file_still_excludes_and_lets_a_publication_through()
    {
        var key = await SeedAsync();
        MakeLockFileReadOnly();

        using (var held = StemCacheExpansion.TryAcquire(Destination, out var failure))
        {
            Assert.Null(failure);
            Assert.NotNull(held);
            Assert.Null(StemCacheExpansion.TryAcquire(Destination, out var busy));
            Assert.Null(busy);
        }

        Assert.Equal(2, (await Store().ExtractAsync(key, Destination, Ct)).Count);
        Assert.Equal(2, Probe().Recovered.Count);
    }

    /// <summary>#656 row 2: a folder whose exclusion cannot be opened at all, holding a
    /// complete expansion of this archive, is served without the exclusion.</summary>
    [Fact]
    public async Task An_unopenable_lock_serves_a_complete_expansion_without_decoding()
    {
        var key = await ExpandAsync();
        var decoded = _decodes;
        MakeLockUnopenable();
        var before = Snapshot();

        var tracks = await Store().ExtractAsync(key, Destination, Ct);

        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, track => Assert.True(File.Exists(track.Path)));
        Assert.Equal(decoded, _decodes);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>#656 row 2, the other half: without the exclusion a REAL publication
    /// still fails, and the folder is left as it was.</summary>
    [Fact]
    public async Task An_unopenable_lock_still_fails_a_real_publication()
    {
        var key = await SeedAsync();
        MakeLockUnopenable();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Store().ExtractAsync(key, Destination, Ct));

        Assert.Empty(Directory.EnumerateFiles(Destination, "*.wav"));
    }

    /// <summary>#656 row 3: the old completion marker goes before the decode, and the
    /// new set is published after it. A probe in between adopted the OLD WAVs.</summary>
    [Fact]
    public async Task A_probe_during_the_decode_never_adopts_the_set_being_replaced()
    {
        await ExpandAsync();
        var otherIdentity = StemCacheIdentity.ForLibrary("lib:OTHER");
        var signature = StemAudioSignature.ComputeFile(Audio());
        var other = StemCacheKey.Derive(otherIdentity, signature);
        StemTrack[] stems = [.. new[] { "bass", "other" }.Select(name =>
        {
            var path = Path.Combine(_root, name + ".wav");
            File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)(name.Length + 10), 256)]);
            return new StemTrack { Name = name, Path = path };
        })];
        await Store().SaveAsync(other, otherIdentity, signature, stems, "model", "1.0", Ct);
        // One probe at a time: two at once would see each other as busy.
        var gate = new Lock();
        var seen = new List<int>();
        var store = Store(onDecode: () =>
        {
            lock (gate) seen.Add(Probe().Recovered.Count);
        });

        var tracks = await store.ExtractAsync(other, Destination, Ct);

        Assert.Equal(2, tracks.Count);
        Assert.NotEmpty(seen);
        Assert.All(seen, recovered => Assert.Equal(0, recovered));
        Assert.NotEmpty(Probe().Recovered);
    }

    /// <summary>A decode that fails after the fast path recorded the publication as started
    /// leaves its pending record behind. Neither probe adopts the folder meanwhile, and the
    /// next load restores the set from the archive, clears the record and separates nothing.</summary>
    [Fact]
    public async Task A_failed_decode_stays_hidden_and_the_next_restore_from_the_archive_completes()
    {
        await ExpandAsync();
        var otherIdentity = StemCacheIdentity.ForLibrary("lib:OTHER");
        var signature = StemAudioSignature.ComputeFile(Audio());
        var other = StemCacheKey.Derive(otherIdentity, signature);
        StemTrack[] stems = [.. new[] { "bass", "other" }.Select(name =>
        {
            var path = Path.Combine(_root, name + ".wav");
            File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)(name.Length + 10), 256)]);
            return new StemTrack { Name = name, Path = path };
        })];
        var store = Store();
        await store.SaveAsync(other, otherIdentity, signature, stems, "model", "1.0", Ct);
        var archive = File.ReadAllBytes(store.ArchivePath(other));
        var failing = Store(onDecode: () => throw new IOException("decoder failed"));

        await Assert.ThrowsAsync<IOException>(() => failing.ExtractAsync(other, Destination, Ct));

        // The pending record is left, and the previous set's WAVs are still on disk.
        Assert.True(File.Exists(Pending));
        Assert.Equal(2, Directory.EnumerateFiles(Destination, "*.wav").Count());
        Assert.Empty(Probe().Recovered);
        MakeLockUnopenable();
        Assert.Empty(Probe().Recovered);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ExtractAsync(other, Destination, Ct));
        Directory.Delete(LockFile);

        var coordinator = new StemCacheCoordinator(store);
        var served = await coordinator.TryLoadCachedAsync(otherIdentity, Audio(), Destination, Ct);

        Assert.NotNull(served);
        Assert.Equal(["bass", "other"], served.Select(track => track.Name));
        Assert.All(served, track => Assert.Equal((byte)(track.Name.Length + 10), File.ReadAllBytes(track.Path)[0]));
        Assert.False(File.Exists(Pending));
        Assert.True(coordinator.HasArchive(otherIdentity, Audio()));
        Assert.Equal(archive, File.ReadAllBytes(store.ArchivePath(other)));
        // The folder is readable again; the replaced set's WAVs stay beside the new ones.
        Assert.Superset(new HashSet<string> { "bass", "other" }, Probe().Recovered.Select(r => r.Name).ToHashSet());
    }

    private StemCacheStore Store(Func<TimeSpan, CancellationToken, Task>? delay = null, Action? onDecode = null)
        => new(new CopyEncoder(() =>
        {
            Interlocked.Increment(ref _decodes);
            onDecode?.Invoke();
        }), logger: null, rootOverride: Path.Combine(_root, "cache"), lockRetryDelay: delay);

    private string Audio()
    {
        var path = Path.Combine(_root, "song.wav");
        if (!File.Exists(path)) File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)1, 500)]);
        return path;
    }

    /// <summary>A two-stem archive in the durable cache, nothing expanded yet.</summary>
    private async Task<StemCacheKey> SeedAsync()
    {
        var signature = StemAudioSignature.ComputeFile(Audio());
        var key = StemCacheKey.Derive(_identity, signature);
        StemTrack[] stems = [.. new[] { "drums", "vocals" }.Select(name =>
        {
            var path = Path.Combine(_root, name + ".wav");
            File.WriteAllBytes(path, [.. Enumerable.Repeat((byte)name.Length, 256)]);
            return new StemTrack { Name = name, Path = path };
        })];
        await Store().SaveAsync(key, _identity, signature, stems, "model", "1.0", Ct);
        return key;
    }

    /// <summary>The archive, expanded into the stem folder by a completed publication.</summary>
    private async Task<StemCacheKey> ExpandAsync()
    {
        var key = await SeedAsync();
        Assert.Equal(2, (await Store().ExtractAsync(key, Destination, Ct)).Count);
        return key;
    }

    private FileStream HoldLock()
        => File.Open(LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>A FOLDER where the lock file belongs: no handle of either access opens
    /// it, as in a stem folder without write access and without a lock file. A
    /// read-only lock FILE opens (for reading, unshared), see the read-only cases.</summary>
    private void MakeLockUnopenable()
    {
        if (File.Exists(LockFile)) File.Delete(LockFile);
        Directory.CreateDirectory(LockFile);
    }

    private void MakeLockFileReadOnly()
    {
        if (!File.Exists(LockFile)) File.WriteAllBytes(LockFile, []);
        File.SetAttributes(LockFile, FileAttributes.ReadOnly);
    }

    private StemProbe Probe(IAppLogger? logger = null) => StemFolderProbe.Probe(_root, _root, [], false, null, logger);

    /// <summary>Every file in the stem folder with its bytes; the lock file by name only,
    /// because a held one cannot be read.</summary>
    private string[] Snapshot()
        => [.. Directory.EnumerateFiles(Destination).Order(StringComparer.Ordinal).Select(path =>
            Path.GetFileName(path) + ":" + (path == LockFile ? "" : Convert.ToHexString(File.ReadAllBytes(path))))];

    private sealed class CopyEncoder(Action onDecode) : IStemEncoder
    {
        public StemCodec PreferredCodec => StemCodec.Opus;
        public bool IsExternalEncoderAvailable => true;

        public Task<string> EncodeAsync(string wavPath, string destNoExt, StemCodec target, CancellationToken ct = default)
        {
            var dest = destNoExt + "." + target.FileExtension();
            File.Copy(wavPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec c, string destWavPath, CancellationToken ct = default)
        {
            File.Copy(encodedPath, destWavPath, overwrite: true);
            onDecode();
            return Task.FromResult(destWavPath);
        }
    }
}
