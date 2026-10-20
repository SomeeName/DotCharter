using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The never-regenerate contract, exercised on the SAME seam the app calls
///     (<c>StemSeparationViewModel.SeparateStemsAsync</c> → <see cref="StemCacheCoordinator" />).
///
///     An earlier revision tested a <c>GetOrSeparateAsync</c> front door the app
///     never invoked, while the view model re-implemented the hit path inline.
///     The two drifted apart — they disagreed on corrupt-archive recovery — and
///     the suite stayed green throughout. Keep these tests pointed at what ships.
/// </summary>
public sealed class StemCacheCoordinatorTests : IDisposable
{
    private readonly string _tmp;

    public StemCacheCoordinatorTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "DotCharterStemCoordTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose() => BestEffortDelete.Directory(_tmp);

    /// <summary>Encoder that copies (no ffmpeg) but tags the archive as Opus.</summary>
    private sealed class CopyEncoder : IStemEncoder
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
            return Task.FromResult(destWavPath);
        }
    }

    /// <summary>Simulates "ffmpeg went missing after the archive was saved":
    /// every decode fails environmentally, exactly like
    /// <see cref="FfmpegStemEncoder.DecodeToWavAsync"/> without ffmpeg.</summary>
    private sealed class DecodeUnavailableEncoder : IStemEncoder
    {
        public StemCodec PreferredCodec => StemCodec.Opus;
        public bool IsExternalEncoderAvailable => false;

        public Task<string> EncodeAsync(string wavPath, string destNoExt, StemCodec target, CancellationToken ct = default)
        {
            var dest = destNoExt + "." + target.FileExtension();
            File.Copy(wavPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec c, string destWavPath, CancellationToken ct = default)
            => throw new StemDecodeUnavailableException(
                "Cache stem is stored as Opus but ffmpeg is not available to decode it.");
    }

    /// <summary>Simulates a full disk / a stem WAV still open in the JUCE mixer:
    /// expansion fails with a bare <see cref="IOException"/>, which is NOT
    /// corruption and must never cost the user their archive.</summary>
    private sealed class DiskFullEncoder : IStemEncoder
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
            => throw new IOException("There is not enough space on the disk.");
    }

    private string CacheRoot => Path.Combine(_tmp, "cache");

    private StemCacheStore NewStore() => new(new CopyEncoder(), logger: null, rootOverride: CacheRoot);

    private StemCacheStore NewStoreWithoutDecoder() => new(new DecodeUnavailableEncoder(), logger: null, rootOverride: CacheRoot);

    private StemCacheStore NewStoreOnAFullDisk() => new(new DiskFullEncoder(), logger: null, rootOverride: CacheRoot);

    private string WriteAudio(string name, byte fill, int len = 500)
    {
        var p = Path.Combine(_tmp, name);
        File.WriteAllBytes(p, Enumerable.Repeat(fill, len).ToArray());
        return p;
    }

    private string WriteStemWav(byte fill)
    {
        var p = Path.Combine(_tmp, $"stem-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(p, Enumerable.Repeat(fill, 256).ToArray());
        return p;
    }

    /// <summary>Puts a finished separation into the durable cache, exactly as the
    /// view model does after a successful run.</summary>
    private async Task SeedCacheAsync(StemCacheStore store, StemCacheIdentity id, string audioPath)
    {
        var sig = StemAudioSignature.ComputeFile(audioPath);
        var key = StemCacheKey.Derive(id, sig);
        var stems = new[]
        {
            new StemTrack { Name = "drums", Path = WriteStemWav(7) },
            new StemTrack { Name = "vocals", Path = WriteStemWav(9) },
        };
        await store.SaveAsync(key, id, sig, stems, "htdemucs-4s", "0.13.0", TestContext.Current.CancellationToken);
    }

    // ---- the contract -------------------------------------------------------

    [Fact]
    public async Task Nothing_cached_reports_a_miss_so_the_caller_separates()
    {
        var audio = WriteAudio("song.wav", 1);
        var coordinator = new StemCacheCoordinator(NewStore());

        var result = await coordinator.TryLoadCachedAsync(
            StemCacheIdentity.ForLibrary("lib:ABC"), audio, Path.Combine(_tmp, "play"),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    /// <summary>The headline requirement: a relaunch (a brand-new store and
    /// coordinator over the same cache root) serves the cached stems, so the long
    /// separation never runs a second time for the same song + audio.</summary>
    [Fact]
    public async Task Same_song_on_a_later_launch_is_served_from_cache()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        await SeedCacheAsync(NewStore(), id, audio);

        var coordinator = new StemCacheCoordinator(NewStore());
        var cached = await coordinator.TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(cached);
        Assert.Equal(2, cached!.Count);
        Assert.All(cached, t => Assert.True(File.Exists(t.Path)));
        Assert.Contains(cached, t => t.Name == "drums");
        Assert.Contains(cached, t => t.Name == "vocals");
    }

    [Fact]
    public async Task Changed_audio_busts_the_cache()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        await SeedCacheAsync(NewStore(), id, audio);

        File.WriteAllBytes(audio, Enumerable.Repeat((byte)2, 1024).ToArray()); // different content

        var coordinator = new StemCacheCoordinator(NewStore());
        var cached = await coordinator.TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play"),
            TestContext.Current.CancellationToken);

        Assert.Null(cached);
    }

    [Fact]
    public async Task Own_chart_keyed_by_audio_path_hits_on_reopen()
    {
        var audio = WriteAudio("mychart.wav", 3);
        await SeedCacheAsync(NewStore(), StemCacheIdentity.ForAudioFile(audio), audio);

        var coordinator = new StemCacheCoordinator(NewStore());
        var cached = await coordinator.TryLoadCachedAsync(
            StemCacheIdentity.ForAudioFile(audio), audio, Path.Combine(_tmp, "play"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(cached);
    }

    /// <summary>A corrupt archive must not wedge the user out of their stems
    /// forever: it is deleted and reported as a miss so a fresh run can proceed.
    /// The inline view-model copy only logged and left the bad archive in place.</summary>
    [Fact]
    public async Task Corrupt_archive_is_deleted_and_reported_as_a_miss()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        var store = NewStore();
        await SeedCacheAsync(store, id, audio);

        var archivePath = store.ArchivePath(StemCacheKey.Derive(id, StemAudioSignature.ComputeFile(audio)));
        Assert.True(File.Exists(archivePath));
        await File.WriteAllTextAsync(archivePath, "not a zip", TestContext.Current.CancellationToken);

        var coordinator = new StemCacheCoordinator(NewStore());
        var cached = await coordinator.TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play"),
            TestContext.Current.CancellationToken);

        Assert.Null(cached);
        Assert.False(File.Exists(archivePath), "the corrupt archive should have been deleted");
    }

    /// <summary>The same "never precious vs precious" line as the decode-outage
    /// case, on the arm that used to be the catch-all. An untyped IOException
    /// during expansion — a full disk, or a stem WAV still open in the JUCE mixer
    /// from the previous session — is a plain failure to READ, not proof the bytes
    /// are bad. Deleting there destroyed the only copy of a 40-minute separation
    /// and immediately started re-separating: the most direct violation of the
    /// never-regenerate invariant in the subsystem.</summary>
    [Fact]
    public async Task Disk_full_during_expansion_keeps_the_archive_and_reports_a_miss()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        var store = NewStore();
        await SeedCacheAsync(store, id, audio);

        var archivePath = store.ArchivePath(StemCacheKey.Derive(id, StemAudioSignature.ComputeFile(audio)));
        Assert.True(File.Exists(archivePath));

        var coordinator = new StemCacheCoordinator(NewStoreOnAFullDisk());
        var cached = await coordinator.TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play"),
            TestContext.Current.CancellationToken);

        Assert.Null(cached);
        Assert.True(File.Exists(archivePath),
            "a transient IO failure must never delete the never-regenerate archive");

        // ...and once the disk has room the same archive serves the stems, with no
        // re-separation.
        var hit = await new StemCacheCoordinator(NewStore())
            .TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play"), TestContext.Current.CancellationToken);
        Assert.Equal(2, hit!.Count);
    }

    /// <summary>The other half of the same line: an archive whose MANIFEST reads but
    /// whose stem entries do not is genuinely unusable, and must still be deleted so
    /// a fresh run can proceed. Guards against over-correcting the fix above into
    /// "never delete anything", which would wedge the user on a bad archive forever.</summary>
    [Fact]
    public async Task Archive_with_a_corrupt_stem_entry_is_still_deleted()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        var store = NewStore();
        await SeedCacheAsync(store, id, audio);

        var archivePath = store.ArchivePath(StemCacheKey.Derive(id, StemAudioSignature.ComputeFile(audio)));
        CorruptSecondLocalFileHeader(archivePath);

        // The manifest (entry 1) still reads, so this gets past the probe and fails
        // inside ExtractStems — the arm the catch-all used to own.
        Assert.NotNull(StemArchive.ReadManifest(archivePath));

        var cached = await new StemCacheCoordinator(store).TryLoadCachedAsync(
            id, audio, Path.Combine(_tmp, "play"), TestContext.Current.CancellationToken);

        Assert.Null(cached);
        Assert.False(File.Exists(archivePath), "a provably corrupt archive should have been deleted");
    }

    /// <summary>Breaks the local file header of the SECOND zip entry (the first stem;
    /// entry 1 is manifest.json, written first by <see cref="StemArchive.Write"/>), so
    /// the manifest still reads and only the payload is unusable.</summary>
    private static void CorruptSecondLocalFileHeader(string archivePath)
    {
        var bytes = File.ReadAllBytes(archivePath);
        ReadOnlySpan<byte> sig = [0x50, 0x4B, 0x03, 0x04]; // "PK\x03\x04"
        var first = bytes.AsSpan().IndexOf(sig);
        Assert.True(first >= 0);
        var second = bytes.AsSpan(first + 4).IndexOf(sig);
        Assert.True(second >= 0, "the archive must hold at least two entries");
        bytes[first + 4 + second + 2] = 0xFF; // wreck the signature, keep the offsets
        File.WriteAllBytes(archivePath, bytes);
    }

    // ---- the degenerate-signature guard -------------------------------------
    //
    // StemAudioSignature.ComputeFile swallows every exception and returns None, and
    // the File.Exists guard does NOT cover that: an ACL-denied, AV-quarantined or
    // exclusively-locked file exists and still fails to open. Keying anyway yields
    // "<basis>|b=0|h=" — a real, reachable cache entry. WaveformDiskCache has
    // refused to key on it since it was written ("no key means no caching"); the
    // stem path, whose artifacts are the ones that are NEVER regenerated, did not.

    /// <summary>The damage the guard prevents, in the order it actually happens:
    /// a transiently unreadable file once caused a degenerate archive to be written,
    /// and from then on EVERY unreadable-audio moment hits it — so a user who
    /// replaced the song's audio in place is served the OLD stems, defeating the
    /// content-signature invalidation the signature exists for.</summary>
    [Fact]
    public async Task Unreadable_audio_never_hits_a_degenerate_key_archive()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");

        // A poisoned archive left behind by an earlier degenerate save: keyed with
        // the "could not read the audio" signature, holding stems for audio that is
        // no longer what this song plays.
        var store = NewStore();
        var stale = new[] { new StemTrack { Name = "stale", Path = WriteStemWav(42) } };
        await store.SaveAsync(StemCacheKey.Derive(id, StemAudioSignature.None), id,
            StemAudioSignature.None, stale, "htdemucs-4s", "0.13.0", TestContext.Current.CancellationToken);

        // Hold the audio exclusively — File.Exists still true, ComputeFile still
        // fails, exactly like an AV scan or an ACL.
        using var locked = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(StemAudioSignature.ComputeFile(audio).IsEmpty);

        var cached = await new StemCacheCoordinator(NewStore()).TryLoadCachedAsync(
            id, audio, Path.Combine(_tmp, "play"), TestContext.Current.CancellationToken);

        Assert.Null(cached);
    }

    /// <summary>The read side must also not MISS a good archive and re-separate a
    /// song that is already cached — so once the audio is readable again the very
    /// same coordinator hits, with no separation in between.</summary>
    [Fact]
    public async Task Readable_audio_after_a_transient_lock_still_hits_the_real_archive()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        await SeedCacheAsync(NewStore(), id, audio);
        var playDir = Path.Combine(_tmp, "play");

        using (new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(await new StemCacheCoordinator(NewStore())
                .TryLoadCachedAsync(id, audio, playDir, TestContext.Current.CancellationToken));
        }

        var hit = await new StemCacheCoordinator(NewStore())
            .TryLoadCachedAsync(id, audio, playDir, TestContext.Current.CancellationToken);
        Assert.Equal(2, hit!.Count);
    }

    [Fact]
    public async Task Missing_input_audio_is_a_miss_not_a_throw()
    {
        var coordinator = new StemCacheCoordinator(NewStore());

        var cached = await coordinator.TryLoadCachedAsync(
            StemCacheIdentity.ForLibrary("lib:ABC"), Path.Combine(_tmp, "gone.wav"),
            Path.Combine(_tmp, "play"), TestContext.Current.CancellationToken);

        Assert.Null(cached);
    }

    /// <summary>The never-regenerate guarantee's sharpest edge: the archive is
    /// VALID but the decoder is environmentally unavailable (ffmpeg removed from
    /// PATH after the Opus archive was saved). That must be a plain miss — the
    /// durable archive must NOT be deleted, so it decodes fine once the tool is
    /// back. An earlier revision deleted it here, destroying the cached stems.</summary>
    [Fact]
    public async Task Environmental_decode_failure_keeps_the_archive_and_reports_a_miss()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        var store = NewStore();
        await SeedCacheAsync(store, id, audio);

        var archivePath = store.ArchivePath(StemCacheKey.Derive(id, StemAudioSignature.ComputeFile(audio)));
        Assert.True(File.Exists(archivePath));

        var coordinator = new StemCacheCoordinator(NewStoreWithoutDecoder());
        var cached = await coordinator.TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play"),
            TestContext.Current.CancellationToken);

        Assert.Null(cached);
        Assert.True(File.Exists(archivePath),
            "an environmental decode failure must never delete the valid never-regenerate archive");
    }

    /// <summary>A failed decode must not leak the temporary .encoded/ payload
    /// dir into the playback dir (ExtractAsync cleans it in a finally).</summary>
    [Fact]
    public async Task Environmental_decode_failure_cleans_up_the_encoded_temp_dir()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        await SeedCacheAsync(NewStore(), id, audio);

        var playDir = Path.Combine(_tmp, "play");
        var coordinator = new StemCacheCoordinator(NewStoreWithoutDecoder());
        await coordinator.TryLoadCachedAsync(id, audio, playDir, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(playDir, ".encoded")),
            "the extracted .encoded payload dir must be removed even when a decode throws");
    }

    /// <summary>After a decode-unavailable miss the archive survived, so the same
    /// coordinator serves the stems as soon as the decoder is back (ffmpeg
    /// reinstalled) — no re-separation.</summary>
    [Fact]
    public async Task Archive_kept_through_decode_outage_hits_once_the_decoder_returns()
    {
        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        await SeedCacheAsync(NewStore(), id, audio);

        var playDir = Path.Combine(_tmp, "play");
        var miss = await new StemCacheCoordinator(NewStoreWithoutDecoder())
            .TryLoadCachedAsync(id, audio, playDir, TestContext.Current.CancellationToken);
        Assert.Null(miss);

        var hit = await new StemCacheCoordinator(NewStore())
            .TryLoadCachedAsync(id, audio, playDir, TestContext.Current.CancellationToken);
        Assert.NotNull(hit);
        Assert.Equal(2, hit!.Count);
    }

    /// <summary>The "never throws for cache problems" contract, part two: a
    /// manifest probe that throws (e.g. UnauthorizedAccessException from an
    /// ACL-denied/AV-quarantined archive — outside ReadManifest's catch list)
    /// must surface as a miss, not an exception, and must not delete the archive.
    /// Windows-only: simulated with a real deny-read ACL on the archive file.</summary>
    [Fact]
    public async Task Acl_denied_archive_is_a_miss_not_a_throw_and_is_not_deleted()
    {
        // NOTE: this must stay a bare `return` — CA1416 (warnings are errors here)
        // only recognizes an OperatingSystem.IsWindows() early-return as the guard
        // for the Windows-only ACL APIs below; Assert.Skip does not satisfy it.
        if (!OperatingSystem.IsWindows()) return;

        var audio = WriteAudio("song.wav", 1);
        var id = StemCacheIdentity.ForLibrary("lib:ABC");
        var store = NewStore();
        await SeedCacheAsync(store, id, audio);

        var archivePath = store.ArchivePath(StemCacheKey.Derive(id, StemAudioSignature.ComputeFile(audio)));
        var file = new FileInfo(archivePath);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        // Deny ONLY ReadData (+ Delete): File.Exists still sees the file, but
        // ZipFile.OpenRead's FileStream throws UnauthorizedAccessException —
        // the exact escape route the guard exists for.
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            user,
            System.Security.AccessControl.FileSystemRights.ReadData | System.Security.AccessControl.FileSystemRights.Delete,
            System.Security.AccessControl.AccessControlType.Deny);

        var security = file.GetAccessControl();
        security.AddAccessRule(rule);
        file.SetAccessControl(security);
        try
        {
            var coordinator = new StemCacheCoordinator(NewStore());
            var cached = await coordinator.TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play"),
                TestContext.Current.CancellationToken);

            Assert.Null(cached);
        }
        finally
        {
            var restore = file.GetAccessControl();
            restore.RemoveAccessRule(rule);
            file.SetAccessControl(restore);
        }

        Assert.True(File.Exists(archivePath), "an unreadable-by-ACL archive must not be deleted");
    }
}
