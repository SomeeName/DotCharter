using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// An own-chart cache key must follow the AUDIO, not the audio's path.
///
/// <para>It used to fold the normalized full path into the basis
/// (<c>"file:" + NormalizePath(SourceLabel)</c>), so renaming the file, moving the
/// project folder, switching from a local path to a synced/OneDrive one, or opening
/// the same song from a copy all produced a different key for BYTE-IDENTICAL audio
/// — a cache miss, and minutes of separation paid again for stems already sitting
/// on disk. That is a direct weakening of "stems are NEVER regenerated"; the PSARC
/// half was immune only because it keys on <c>psarc:SongKey</c>.</para>
///
/// <para>Changing the basis alone would have STRANDED every archive already on
/// disk, which is the same data loss in a different shirt — so the second half of
/// this class is the migration: an archive filed under the superseded key is
/// ADOPTED (a same-directory rename, no re-encode) and then keeps serving, and it
/// survives the NEXT move too because the manifest's recorded key is accepted by
/// its signature half.</para>
/// </summary>
public sealed class StemCacheKeyPathIndependenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dc-stemkey-" + Guid.NewGuid().ToString("N"));

    public StemCacheKeyPathIndependenceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    private static readonly StemAudioSignature Sig = new(4096, "deadbeefcafe");

    // ---- the key ------------------------------------------------------------

    [Fact]
    public void The_same_bytes_under_two_different_paths_derive_the_same_key()
    {
        var a = StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(@"D:\Songs\Alpha\song.mp3"), Sig);
        var b = StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(@"C:\Users\me\OneDrive\renamed.mp3"), Sig);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a!.DirectoryToken, b!.DirectoryToken);
    }

    [Fact]
    public void Different_audio_still_keys_differently()
    {
        var a = StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(@"D:\a.mp3"), Sig);
        var b = StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(@"D:\a.mp3"), new StemAudioSignature(4096, "0000"));
        Assert.NotEqual(a!.DirectoryToken, b!.DirectoryToken);
    }

    [Fact]
    public void A_library_song_and_an_own_chart_of_the_same_audio_stay_apart()
    {
        var lib = StemCacheKey.TryDerive(StemCacheIdentity.ForLibrary("lib:ABC", @"D:\a.mp3"), Sig);
        var own = StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(@"D:\a.mp3"), Sig);
        Assert.NotEqual(lib!.DirectoryToken, own!.DirectoryToken);
    }

    /// <summary>The degenerate refusals are unchanged: no signature, or an identity
    /// that names nothing at all.</summary>
    [Fact]
    public void The_degenerate_guards_still_refuse()
    {
        Assert.Null(StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(@"D:\a.mp3"), StemAudioSignature.None));
        Assert.Null(StemCacheKey.TryDerive(StemCacheIdentity.ForAudioFile(""), Sig));
        Assert.Null(StemCacheKey.TryDerive(StemCacheIdentity.ForLibrary(""), Sig));
    }

    [Fact]
    public void The_superseded_key_is_the_old_path_folding_form_and_only_for_own_charts()
    {
        var id = StemCacheIdentity.ForAudioFile(@"D:\Songs\Alpha\song.mp3");
        var old = StemCacheKey.TryDeriveSuperseded(id, Sig);
        Assert.NotNull(old);
        Assert.StartsWith("file:D:/SONGS/ALPHA/SONG.MP3|b=", old!.Canonical, StringComparison.Ordinal);

        // It is path-dependent BY DESIGN — that is the whole point of superseding it.
        var moved = StemCacheKey.TryDeriveSuperseded(StemCacheIdentity.ForAudioFile(@"D:\Other\song.mp3"), Sig);
        Assert.NotEqual(old.DirectoryToken, moved!.DirectoryToken);

        // A library identity was never path-keyed, so it has no earlier form.
        Assert.Null(StemCacheKey.TryDeriveSuperseded(StemCacheIdentity.ForLibrary("lib:ABC", @"D:\a.mp3"), Sig));
        Assert.Null(StemCacheKey.TryDeriveSuperseded(StemCacheIdentity.ForAudioFile(""), Sig));
    }

    [Fact]
    public void An_archive_recording_the_superseded_key_of_the_same_audio_is_accepted()
    {
        var id = StemCacheIdentity.ForAudioFile(@"D:\Songs\Alpha\song.mp3");
        var current = StemCacheKey.TryDerive(id, Sig)!;
        var superseded = StemCacheKey.TryDeriveSuperseded(id, Sig)!;

        Assert.True(current.AcceptsRecordedKey(superseded.Canonical));
        Assert.True(current.AcceptsRecordedKey(current.Canonical));
        Assert.True(current.AcceptsRecordedKey(""));    // legacy archive, no field
        Assert.True(current.AcceptsRecordedKey(null));

        // …and the acceptance is by SIGNATURE, so a path-keyed archive of DIFFERENT
        // audio is still a miss — this must not become "any file: key will do".
        var otherAudio = StemCacheKey.TryDeriveSuperseded(id, new StemAudioSignature(4096, "0000"))!;
        Assert.False(current.AcceptsRecordedKey(otherAudio.Canonical));

        // A library key never adopts anything.
        var lib = StemCacheKey.TryDerive(StemCacheIdentity.ForLibrary("lib:ABC"), Sig)!;
        Assert.False(lib.AcceptsRecordedKey(superseded.Canonical));
    }

    // ---- the migration ------------------------------------------------------

    private StemCacheStore Store() => new(rootOverride: _root);

    [Fact]
    public void MovingAudioBeforeTheFirstMigrationStillFindsTheLegacyArchive()
    {
        var store = Store();
        var oldIdentity = StemCacheIdentity.ForAudioFile(@"D:\old\song.wav");
        var movedIdentity = StemCacheIdentity.ForAudioFile(@"E:\new\renamed.wav");
        var oldKey = StemCacheKey.TryDeriveSuperseded(oldIdentity, Sig)!;
        var current = StemCacheKey.TryDerive(movedIdentity, Sig)!;
        StemArchive.Write(store.ArchivePath(oldKey), ManifestFor(oldKey), []);
        var original = File.ReadAllBytes(store.ArchivePath(oldKey));

        Assert.True(store.TryAdoptSupersededArchive(current,
            StemCacheKey.TryDeriveSuperseded(movedIdentity, Sig)!));
        Assert.True(store.TryGetManifest(current, out _));
        Assert.Equal(original, File.ReadAllBytes(store.ArchivePath(current)));
    }

    private static StemCacheManifest ManifestFor(StemCacheKey key) => new()
    {
        Key = key.Canonical,
        Stems = [new StemManifestEntry { Name = "drums", FileName = "drums.wav" }],
    };

    [Fact]
    public void An_archive_filed_under_the_old_key_is_adopted_not_re_separated()
    {
        var store = Store();
        var id = StemCacheIdentity.ForAudioFile(@"D:\Songs\Alpha\song.mp3");
        var superseded = StemCacheKey.TryDeriveSuperseded(id, Sig)!;
        var current = StemCacheKey.TryDerive(id, Sig)!;

        StemArchive.Write(store.ArchivePath(superseded), ManifestFor(superseded), []);
        Assert.False(store.TryGetManifest(current, out _)); // not findable yet

        Assert.True(store.TryAdoptSupersededArchive(current, superseded));

        Assert.False(File.Exists(store.ArchivePath(superseded)));
        Assert.True(File.Exists(store.ArchivePath(current)));
        // Found AND accepted: the archive still records the old canonical key.
        Assert.True(store.TryGetManifest(current, out var read));
        Assert.Equal(superseded.Canonical, read.Key);
    }

    /// <summary>The point of the whole change: once adopted, moving the audio again
    /// must NOT lose the archive.</summary>
    [Fact]
    public void After_adoption_moving_the_audio_again_still_hits()
    {
        var store = Store();
        var before = StemCacheIdentity.ForAudioFile(@"D:\Songs\Alpha\song.mp3");
        var superseded = StemCacheKey.TryDeriveSuperseded(before, Sig)!;
        StemArchive.Write(store.ArchivePath(superseded), ManifestFor(superseded), []);
        store.TryAdoptSupersededArchive(StemCacheKey.TryDerive(before, Sig)!, superseded);

        var after = StemCacheIdentity.ForAudioFile(@"E:\Backup\Music\completely different name.mp3");
        Assert.True(store.TryGetManifest(StemCacheKey.TryDerive(after, Sig)!, out _));
    }

    [Fact]
    public void Adoption_never_overwrites_an_archive_the_new_key_already_has()
    {
        var store = Store();
        var id = StemCacheIdentity.ForAudioFile(@"D:\Songs\Alpha\song.mp3");
        var superseded = StemCacheKey.TryDeriveSuperseded(id, Sig)!;
        var current = StemCacheKey.TryDerive(id, Sig)!;

        StemArchive.Write(store.ArchivePath(superseded), ManifestFor(superseded), []);
        StemArchive.Write(store.ArchivePath(current), ManifestFor(current), []);

        Assert.False(store.TryAdoptSupersededArchive(current, superseded));
        // The old one is left for the user to clear knowingly, never destroyed here.
        Assert.True(File.Exists(store.ArchivePath(superseded)));
        Assert.True(store.TryGetManifest(current, out var read));
        Assert.Equal(current.Canonical, read.Key);
    }

    /// <summary>End to end through the front door: stems saved while the audio sat
    /// at one path are served after it is renamed and moved.</summary>
    [Fact]
    public async Task A_renamed_and_moved_audio_file_still_hits_its_cached_stems()
    {
        var ct = TestContext.Current.CancellationToken;
        var cache = Path.Combine(_root, "cache");
        var store = new StemCacheStore(new CopyEncoder(), logger: null, rootOverride: cache);
        var coordinator = new StemCacheCoordinator(store);

        var pathA = Path.Combine(_root, "Album", "01 - song.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(pathA)!);
        await File.WriteAllBytesAsync(pathA, FakeWav(512), ct);

        var idA = StemCacheIdentity.ForAudioFile(pathA);
        var sigA = StemAudioSignature.ComputeFile(pathA);
        var stem = Path.Combine(_root, "drums.wav");
        await File.WriteAllBytesAsync(stem, FakeWav(256), ct);
        await store.SaveAsync(StemCacheKey.TryDerive(idA, sigA)!, idA, sigA,
            [new StemTrack { Name = "drums", Path = stem }], "m", "1.0", ct);

        // The user renames the file and moves it to a different folder.
        var pathB = Path.Combine(_root, "Sorted", "Song (remaster).wav");
        Directory.CreateDirectory(Path.GetDirectoryName(pathB)!);
        File.Move(pathA, pathB);

        var hit = await coordinator.TryLoadCachedAsync(
            StemCacheIdentity.ForAudioFile(pathB), pathB, Path.Combine(_root, "playback"), ct);

        Assert.NotNull(hit);
        Assert.Single(hit!);
        Assert.Equal("drums", hit[0].Name);
    }

    private static byte[] FakeWav(int payload)
    {
        var b = new byte[44 + payload];
        "RIFF"u8.CopyTo(b);
        "WAVE"u8.CopyTo(b.AsSpan(8));
        for (var i = 44; i < b.Length; i++) b[i] = (byte)(i % 251);
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
