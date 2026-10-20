using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// #426: a library song's stems are keyed on its SOURCE <c>.wem</c>, not on the
/// playable WAV decoded from it. The WAV is evicted by the 8 GiB PSARC audio cache
/// and decoded again — now natively, a few LSBs away from the ww2ogg + NVorbis WAV
/// every earlier build keyed on — so a WAV key would orphan the song's
/// <c>.dcstems</c> and the next Separate would re-separate (stems are NEVER
/// regenerated). An archive saved under the old WAV key is ADOPTED onto the WEM key
/// while the WAV it was keyed on is still on disk: by the lookup for its own song,
/// and (#431, ww2ogg + NVorbis gone, so nothing can rebuild the old WAV any more)
/// by <see cref="WemKeyAdoption"/>'s one-time startup pass over every such archive.
/// </summary>
public sealed class StemCacheWemKeyTests : IDisposable
{
    private const string SongId = "lib:SONGKEY|Charter|1";

    private readonly string _root = Directory.CreateTempSubdirectory("dc-stem-wemkey-").FullName;
    private readonly string _songDir;
    private readonly string _wem;
    private readonly string _wav;

    public StemCacheWemKeyTests()
    {
        _songDir = Directory.CreateDirectory(Path.Combine(_root, "PsarcAudioCache", "song_ABC_123")).FullName;
        _wem = Path.Combine(_songDir, "72717684.wem");
        _wav = Path.ChangeExtension(_wem, ".wav");
        File.WriteAllBytes(_wem, Bytes(900_000, seed: 7));
        File.WriteAllBytes(_wav, FakeWav(1_000_000, seed: 1));
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    private StemCacheIdentity Library => StemCacheIdentity.ForLibrary(SongId, _wav);

    // ---- which file is signed ------------------------------------------------

    [Fact]
    public void A_library_song_with_its_wem_beside_the_wav_is_keyed_on_the_wem()
    {
        var source = StemCacheSource.TryResolve(Library, _wav)!;

        Assert.True(source.SignsSourceWem);
        Assert.Equal(_wem, source.SignedPath);
        Assert.Equal(_wav, source.PlayablePath);
        Assert.Equal(StemAudioSignature.ComputeFile(_wem), source.Signature);
        // The stem cache hashes the identity in its current spelling (lib:, #898).
        Assert.StartsWith("lib:SONGKEY|Charter|1|wem|b=900000|h=", source.Key.Canonical, StringComparison.Ordinal);

        // A re-decoded WAV (different bytes) does not move the key; a changed WEM does.
        File.WriteAllBytes(_wav, FakeWav(1_000_000, seed: 2));
        Assert.Equal(source.Key, StemCacheSource.TryResolve(Library, _wav)!.Key);
        File.WriteAllBytes(_wem, Bytes(900_000, seed: 8));
        Assert.NotEqual(source.Key, StemCacheSource.TryResolve(Library, _wav)!.Key);
    }

    [Fact]
    public void An_own_chart_is_keyed_on_its_audio_exactly_as_before_even_with_a_wem_beside_it()
    {
        var own = StemCacheIdentity.ForAudioFile(_wav);
        var source = StemCacheSource.TryResolve(own, _wav)!;

        Assert.False(source.SignsSourceWem);
        Assert.Equal(StemCacheKey.TryDerive(own, StemAudioSignature.ComputeFile(_wav)), source.Key);
        Assert.Equal(_wav, source.SignedPath);
    }

    [Fact]
    public void A_library_song_without_a_wem_beside_its_audio_keeps_the_playable_key()
    {
        File.Delete(_wem);
        var source = StemCacheSource.TryResolve(Library, _wav)!;

        Assert.False(source.SignsSourceWem);
        Assert.Equal(StemCacheKey.TryDerive(Library, StemAudioSignature.ComputeFile(_wav)), source.Key);
    }

    [Fact]
    public void A_wem_key_accepts_only_its_own_song_in_the_playable_form()
    {
        var wemKey = StemCacheKey.TryDeriveForWem(Library, new StemAudioSignature(10, "aa"))!;
        var mine = StemCacheKey.TryDerive(Library, new StemAudioSignature(20, "bb"))!;
        var otherVersion = StemCacheKey.TryDerive(StemCacheIdentity.ForLibrary(SongId + "0"), new StemAudioSignature(20, "bb"))!;
        var ownChart = StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(_wav), new StemAudioSignature(20, "bb"))!;

        Assert.True(wemKey.AcceptsRecordedKey(mine.Canonical));
        Assert.False(wemKey.AcceptsRecordedKey(otherVersion.Canonical));
        Assert.False(wemKey.AcceptsRecordedKey(ownChart.Canonical));
        Assert.Null(StemCacheKey.TryDeriveForWem(StemCacheIdentity.ForAudioFile(_wav), new StemAudioSignature(10, "aa")));
        Assert.Null(StemCacheKey.TryDeriveForWem(Library, StemAudioSignature.None));
    }

    // ---- the migration, through the front door ---------------------------------

    /// <summary>The upgrade case: the archive was keyed on the WAV that is still on
    /// disk. It is adopted, and from then on survives the WAV being re-decoded.</summary>
    [Fact]
    public async Task A_legacy_wav_keyed_archive_is_still_found_after_the_wav_is_regenerated_with_different_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var legacy = await SaveUnderPlayableKeyAsync(store, ct);

        var coordinator = new StemCacheCoordinator(store);
        Assert.NotNull(await coordinator.TryLoadCachedAsync(Library, _wav, Playback("1"), ct));

        var wemKey = StemCacheSource.TryResolve(Library, _wav)!.Key;
        Assert.True(File.Exists(store.ArchivePath(wemKey)));
        Assert.False(File.Exists(store.ArchivePath(legacy)));

        // Evicted and decoded again: same length, different samples, newer mtime.
        File.WriteAllBytes(_wav, FakeWav(1_000_000, seed: 2));
        File.SetLastWriteTimeUtc(_wav, DateTime.UtcNow.AddMinutes(5));

        var hit = await coordinator.TryLoadCachedAsync(Library, _wav, Playback("2"), ct);
        Assert.NotNull(hit);
        Assert.Equal("drums", Assert.Single(hit!).Name);
    }

    // ---- the startup pass (#431) ---------------------------------------------------

    /// <summary>The upgrade, before any lookup: the pass adopts the WAV-keyed archive
    /// while its WAV is still on disk, and the WAV can then be re-decoded natively
    /// (different bytes) with the archive still found.</summary>
    [Fact]
    public async Task The_startup_pass_adopts_a_wav_keyed_archive_so_a_native_re_decode_still_hits()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var legacy = await SaveUnderPlayableKeyAsync(store, ct);

        Assert.Equal(1, WemKeyAdoption.AdoptAll(store, AudioCacheRoot, logger: null, ct));

        var wemKey = StemCacheSource.TryResolve(Library, _wav)!.Key;
        Assert.True(File.Exists(store.ArchivePath(wemKey)));
        Assert.False(File.Exists(store.ArchivePath(legacy)));

        // Evicted and decoded again natively: same length, different samples.
        File.WriteAllBytes(_wav, FakeWav(1_000_000, seed: 2));
        File.SetLastWriteTimeUtc(_wav, DateTime.UtcNow.AddMinutes(5));
        var hit = await new StemCacheCoordinator(store).TryLoadCachedAsync(Library, _wav, Playback("1"), ct);
        Assert.NotNull(hit);
        Assert.Equal("drums", Assert.Single(hit!).Name);

        // Idempotent: nothing is left to adopt.
        Assert.Equal(0, WemKeyAdoption.AdoptAll(store, AudioCacheRoot, logger: null, ct));
    }

    /// <summary>No proof, no move: a WAV that is no longer the bytes the archive was
    /// keyed on (re-decoded before the pass ran) leaves the archive where it is.</summary>
    [Fact]
    public async Task The_startup_pass_leaves_an_archive_whose_wav_no_longer_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var legacy = await SaveUnderPlayableKeyAsync(store, ct);
        File.WriteAllBytes(_wav, FakeWav(1_000_000, seed: 2));

        Assert.Equal(0, WemKeyAdoption.AdoptAll(store, AudioCacheRoot, logger: null, ct));

        Assert.True(File.Exists(store.ArchivePath(legacy)));
        Assert.False(File.Exists(store.ArchivePath(StemCacheSource.TryResolve(Library, _wav)!.Key)));
    }

    /// <summary>The same sampled bytes but a different file between the windows is
    /// not proof either when the archive recorded a full hash (#334).</summary>
    [Fact]
    public async Task The_startup_pass_checks_the_recorded_full_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var legacy = await SaveUnderPlayableKeyAsync(store, ct);
        var bytes = File.ReadAllBytes(_wav);
        bytes[300_000] ^= 0xFF; // between the 0 and middle 256 KB windows of a 1,000,044-byte file
        File.WriteAllBytes(_wav, bytes);

        Assert.Equal(0, WemKeyAdoption.AdoptAll(store, AudioCacheRoot, logger: null, ct));
        Assert.True(File.Exists(store.ArchivePath(legacy)));
    }

    /// <summary>An archive of ANOTHER song with byte-identical audio is not this
    /// song's: the pass re-keys an archive only under its own logical id.</summary>
    [Fact]
    public async Task The_startup_pass_rekeys_an_archive_under_its_own_song_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var other = StemCacheIdentity.ForLibrary(SongId + "0", _wav);
        var signature = StemAudioSignature.ComputeFile(_wav);
        var otherKey = StemCacheKey.TryDerive(other, signature)!;
        await store.SaveAsync(otherKey, other, signature, [Stem()], "m", "1.0", ct, StemAudioSignature.ComputeFullFileHash(_wav));

        Assert.Equal(1, WemKeyAdoption.AdoptAll(store, AudioCacheRoot, logger: null, ct));

        Assert.True(File.Exists(store.ArchivePath(StemCacheSource.TryResolve(other, _wav)!.Key)));
        Assert.False(File.Exists(store.ArchivePath(StemCacheSource.TryResolve(Library, _wav)!.Key)));
    }

    /// <summary>Once per stem cache: the first run marks the cache, the second does
    /// nothing even when there would be work.</summary>
    [Fact]
    public async Task The_startup_pass_runs_once_per_stem_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        Directory.CreateDirectory(store.Root);
        Assert.Equal(0, WemKeyAdoption.RunOnce(store, AudioCacheRoot, logger: null, ct));
        Assert.True(File.Exists(Path.Combine(store.Root, WemKeyAdoption.DoneMarkerFileName)));

        var legacy = await SaveUnderPlayableKeyAsync(store, ct);
        Assert.Equal(-1, WemKeyAdoption.RunOnce(store, AudioCacheRoot, logger: null, ct));
        Assert.True(File.Exists(store.ArchivePath(legacy)));
    }

    /// <summary>A new archive is keyed on the WEM, records the WEM's hashes, and a
    /// re-decoded WAV still hits it.</summary>
    [Fact]
    public async Task A_new_archive_is_keyed_on_the_wem_and_survives_a_re_decoded_wav()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var source = StemCacheSource.TryResolve(Library, _wav)!;
        await store.SaveAsync(source.Key, Library, source.Signature, [Stem()], "m", "1.0", ct,
            StemAudioSignature.ComputeFullFileHash(source.SignedPath));

        Assert.True(store.TryGetManifest(source.Key, out var manifest));
        Assert.Equal(900_000, manifest.SourceBytes);
        Assert.Equal(StemAudioSignature.ComputeFullFileHash(_wem), manifest.SourceFullHash);

        File.WriteAllBytes(_wav, FakeWav(1_000_000, seed: 2));
        var coordinator = new StemCacheCoordinator(store);
        Assert.NotNull(await coordinator.TryLoadCachedAsync(Library, _wav, Playback("1"), ct));
    }

    /// <summary>The WEM's full hash still guards a WEM-keyed archive (#334): the
    /// same-length WEM with different bytes between the sampled windows is a miss.</summary>
    [Fact]
    public async Task A_wem_keyed_archive_misses_when_the_wem_changed_between_the_sampled_windows()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var source = StemCacheSource.TryResolve(Library, _wav)!;
        await store.SaveAsync(source.Key, Library, source.Signature, [Stem()], "m", "1.0", ct,
            StemAudioSignature.ComputeFullFileHash(source.SignedPath));

        var bytes = File.ReadAllBytes(_wem);
        bytes[300_000] ^= 0xFF; // outside the 0 / middle / tail 256 KB windows
        File.WriteAllBytes(_wem, bytes);
        File.SetLastWriteTimeUtc(_wem, DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(source.Key, StemCacheSource.TryResolve(Library, _wav)!.Key);

        var coordinator = new StemCacheCoordinator(store);
        Assert.Null(await coordinator.TryLoadCachedAsync(Library, _wav, Playback("1"), ct));
    }

    // ---- helpers -----------------------------------------------------------------

    private StemCacheStore Store() =>
        new(new CopyEncoder(), logger: null, rootOverride: Path.Combine(_root, "StemCache"));

    private string Playback(string name) => Path.Combine(_root, "playback-" + name);

    private string AudioCacheRoot => Path.Combine(_root, "PsarcAudioCache");

    private StemTrack Stem()
    {
        var path = Path.Combine(_root, "drums.wav");
        if (!File.Exists(path)) File.WriteAllBytes(path, FakeWav(256, seed: 9));
        return new StemTrack { Name = "drums", Path = path };
    }

    /// <summary>Saves the way every build before #426 did: keyed on the playable WAV.</summary>
    private async Task<StemCacheKey> SaveUnderPlayableKeyAsync(StemCacheStore store, CancellationToken ct)
    {
        var signature = StemAudioSignature.ComputeFile(_wav);
        var key = StemCacheKey.TryDerive(Library, signature)!;
        await store.SaveAsync(key, Library, signature, [Stem()], "m", "1.0", ct,
            StemAudioSignature.ComputeFullFileHash(_wav));
        Assert.True(File.Exists(store.ArchivePath(key)));
        return key;
    }

    private static byte[] Bytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static byte[] FakeWav(int payload, int seed)
    {
        var b = Bytes(44 + payload, seed);
        "RIFF"u8.CopyTo(b);
        "WAVE"u8.CopyTo(b.AsSpan(8));
        return b;
    }

    private sealed class CopyEncoder : IStemEncoder
    {
        public StemCodec PreferredCodec => StemCodec.Wav;
        public bool IsExternalEncoderAvailable => false;

        public Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec target,
            CancellationToken ct = default)
        {
            var dest = destPathWithoutExtension + "." + target.FileExtension();
            File.Copy(wavPath, dest, overwrite: true);
            return Task.FromResult(dest);
        }

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec c, string destWavPath,
            CancellationToken ct = default)
        {
            File.Copy(encodedPath, destWavPath, overwrite: true);
            return Task.FromResult(destWavPath);
        }
    }
}
