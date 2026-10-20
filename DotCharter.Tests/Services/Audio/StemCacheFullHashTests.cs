using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
/// #334: the stem cache key samples three 256 KB windows of the source audio, so a
/// same-length re-render with an edit between them (a DAW fix mid-song) kept serving
/// the old stems. The key stays sampled (changing it would strand every archive);
/// an archive now records the WHOLE file's hash and a hit is checked against it.
/// </summary>
public sealed class StemCacheFullHashTests : IDisposable
{
    private const int FourMb = 4 * 1024 * 1024;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "dc-stemfull-" + Guid.NewGuid().ToString("N"));

    public StemCacheFullHashTests() => Directory.CreateDirectory(_tmp);

    public void Dispose() => BestEffortDelete.Directory(_tmp);

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

    private string WriteAudio(string name, byte[] bytes)
    {
        var p = Path.Combine(_tmp, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static byte[] Song(byte fill) => Enumerable.Repeat(fill, FourMb).ToArray();

    /// <summary>The same length, one byte changed at 25 %: between the sampled windows.</summary>
    private static byte[] EditedAtAQuarter(byte[] original)
    {
        var edited = (byte[])original.Clone();
        edited[FourMb / 4] ^= 0xFF;
        return edited;
    }

    private string WriteStemWav(byte fill)
    {
        var p = Path.Combine(_tmp, $"stem-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(p, Enumerable.Repeat(fill, 256).ToArray());
        return p;
    }

    [Fact]
    public void TheSampledKey_MissesAMidFileEdit_ButTheFullHashSeesIt()
    {
        var song = Song(3);
        var a = WriteAudio("a.wav", song);
        var b = WriteAudio("b.wav", EditedAtAQuarter(song));

        Assert.Equal(StemAudioSignature.ComputeFile(a), StemAudioSignature.ComputeFile(b));
        Assert.NotEqual(StemAudioSignature.ComputeFullFileHash(a), StemAudioSignature.ComputeFullFileHash(b));
    }

    [Fact]
    public void TheFullHash_IsComputedOncePerFileVersion()
    {
        var song = Song(5);
        var path = WriteAudio("memo.wav", song);
        var first = StemAudioSignature.ComputeFullFileHash(path);
        var stamp = File.GetLastWriteTimeUtc(path);

        // Same length and same last-write time: the memo answers without reading.
        File.WriteAllBytes(path, EditedAtAQuarter(song));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(first, StemAudioSignature.ComputeFullFileHash(path));

        // A new version (the write time moved) is read again.
        File.SetLastWriteTimeUtc(path, stamp.AddSeconds(5));
        Assert.NotEqual(first, StemAudioSignature.ComputeFullFileHash(path));
    }

    [Fact]
    public async Task AHitOnAudioThatChangedBetweenTheSamples_IsAMiss()
    {
        var ct = TestContext.Current.CancellationToken;
        var song = Song(7);
        var audio = WriteAudio("song.wav", song);
        var id = StemCacheIdentity.ForLibrary("lib:FULLHASH");
        var store = new StemCacheStore(new CopyEncoder(), logger: null, rootOverride: Path.Combine(_tmp, "cache"));
        var sig = StemAudioSignature.ComputeFile(audio);
        await store.SaveAsync(StemCacheKey.Derive(id, sig), id, sig,
            [new StemTrack { Name = "drums", Path = WriteStemWav(1) }], "m", "v", ct,
            StemAudioSignature.ComputeFullFileHash(audio));

        var coordinator = new StemCacheCoordinator(store);
        Assert.NotNull(await coordinator.TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play1"), ct));

        File.WriteAllBytes(audio, EditedAtAQuarter(song));
        File.SetLastWriteTimeUtc(audio, File.GetLastWriteTimeUtc(audio).AddSeconds(5));

        Assert.Null(await coordinator.TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play2"), ct));
        Assert.True(File.Exists(store.ArchivePath(StemCacheKey.Derive(id, sig))), "the archive must be kept until replaced");
    }

    [Fact]
    public async Task AnArchiveWrittenBeforeTheFullHash_StillHits()
    {
        var ct = TestContext.Current.CancellationToken;
        var audio = WriteAudio("old.wav", Song(9));
        var id = StemCacheIdentity.ForLibrary("lib:LEGACY");
        var store = new StemCacheStore(new CopyEncoder(), logger: null, rootOverride: Path.Combine(_tmp, "cache"));
        var sig = StemAudioSignature.ComputeFile(audio);
        await store.SaveAsync(StemCacheKey.Derive(id, sig), id, sig,
            [new StemTrack { Name = "drums", Path = WriteStemWav(2) }], "m", "v", ct);

        var cached = await new StemCacheCoordinator(store).TryLoadCachedAsync(id, audio, Path.Combine(_tmp, "play"), ct);

        Assert.NotNull(cached);
    }
}
