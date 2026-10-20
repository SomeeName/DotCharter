using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Audio.Waveform;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     0.14 P4: the generate-once waveform disk cache. The contract mirrors
///     the stem cache: same song + same audio bytes hits forever, changed
///     audio can never serve stale peaks, and every failure mode is a MISS —
///     never a throw, never a wrong pyramid.
/// </summary>
public sealed class WaveformDiskCacheTests : IDisposable
{
    private readonly string _root;
    private readonly string _audioDir;

    public WaveformDiskCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dc-wavecache-" + Guid.NewGuid().ToString("N"));
        _audioDir = Path.Combine(_root, "audio");
        Directory.CreateDirectory(_audioDir);
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    private WaveformDiskCache CreateCache() => new(logger: null, root: Path.Combine(_root, "cache"));

    private string WriteAudio(string name, int seed = 1, int bytes = 4096)
    {
        var path = Path.Combine(_audioDir, name);
        var data = new byte[bytes];
        var rng = new Random(seed);
        rng.NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    // Default to the width the live generator really uses: an entry at any
    // other resolution is a deliberate MISS (see the resolution-guard test).
    private static WaveformSamples MakeSamples(int baseBlocks,
        double blockMs = WaveformSamples.GenerationBlockMs)
    {
        var peaks = new float[baseBlocks];
        for (var i = 0; i < baseBlocks; i++) peaks[i] = (i % 100) / 100f;
        return WaveformSamples.FromBasePeaks(peaks, blockMs);
    }

    [Fact]
    public void RoundTrip_SameIdentityAndAudio_HitsWithIdenticalPyramid()
    {
        var cache = CreateCache();
        var audio = WriteAudio("song.wav");
        var identity = StemCacheIdentity.ForAudioFile(audio);
        var original = MakeSamples(1000);

        cache.Save(identity, audio, original);
        var loaded = cache.TryLoad(identity, audio);

        Assert.NotNull(loaded);
        Assert.NotSame(original, loaded); // consumers compare by reference — must be fresh
        Assert.Equal(original.BaseBlockMs, loaded!.BaseBlockMs);
        Assert.Equal(original.Mipmaps.Length, loaded.Mipmaps.Length);
        for (var level = 0; level < original.Mipmaps.Length; level++)
            Assert.Equal(original.Mipmaps[level], loaded.Mipmaps[level]);
    }

    [Fact]
    public void ChangedAudioBytes_IsAMiss()
    {
        var cache = CreateCache();
        var audio = WriteAudio("song.wav", seed: 1);
        var identity = StemCacheIdentity.ForAudioFile(audio);
        cache.Save(identity, audio, MakeSamples(500));

        // Same path + identity, different content — the signature changes,
        // so the old entry must not be served.
        WriteAudio("song.wav", seed: 2);
        Assert.Null(cache.TryLoad(identity, audio));
    }

    [Fact]
    public void LibraryIdentity_SharesEntriesAcrossPathsForTheSameSong()
    {
        var cache = CreateCache();
        var audioA = WriteAudio("copy-a.wav", seed: 7);
        var audioB = WriteAudio("copy-b.wav", seed: 7); // identical bytes
        var identity = StemCacheIdentity.ForLibrary("lib:SongKey|Charter|1", audioA);

        cache.Save(identity, audioA, MakeSamples(256));

        var identityB = StemCacheIdentity.ForLibrary("lib:SongKey|Charter|1", audioB);
        Assert.NotNull(cache.TryLoad(identityB, audioB));
    }

    [Fact]
    public void CorruptFile_IsAMissAndIsDeleted()
    {
        var cache = CreateCache();
        var audio = WriteAudio("song.wav");
        var identity = StemCacheIdentity.ForAudioFile(audio);
        cache.Save(identity, audio, MakeSamples(128));

        var entry = Directory.GetFiles(Path.Combine(_root, "cache")).Single();
        File.WriteAllBytes(entry, [1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Null(cache.TryLoad(identity, audio));
        Assert.False(File.Exists(entry), "a corrupt entry must be deleted so it cannot miss forever");
    }

    [Fact]
    public void TruncatedPayload_IsAMissAndIsDeleted()
    {
        var cache = CreateCache();
        var audio = WriteAudio("song.wav");
        var identity = StemCacheIdentity.ForAudioFile(audio);
        cache.Save(identity, audio, MakeSamples(1024));

        var entry = Directory.GetFiles(Path.Combine(_root, "cache")).Single();
        var bytes = File.ReadAllBytes(entry);
        File.WriteAllBytes(entry, bytes.AsSpan(0, bytes.Length / 2).ToArray());

        Assert.Null(cache.TryLoad(identity, audio));
        Assert.False(File.Exists(entry));
    }

    [Fact]
    public void AnEntryAtTheOldCoarseResolution_IsAMissAndIsDeleted()
    {
        // The v0.35 pyramid resolution change. The file format is unchanged and
        // carries its own blockMs, so a warm 20 ms cache would load PERFECTLY
        // and pin the user to the coarse peaks forever — the exact "looks
        // low-res" symptom being fixed. Reading it must be a miss (so the open
        // regenerates) AND the entry must go (so it cannot miss forever).
        var cache = CreateCache();
        var audio = WriteAudio("song.wav");
        var identity = StemCacheIdentity.ForAudioFile(audio);
        cache.Save(identity, audio, MakeSamples(1000, blockMs: 20.0));

        var entry = Directory.GetFiles(Path.Combine(_root, "cache")).Single();

        Assert.Null(cache.TryLoad(identity, audio));
        Assert.False(File.Exists(entry),
            "a stale-resolution entry must be deleted so the regenerated one can replace it");

        // …and a re-save at the current resolution hits normally.
        cache.Save(identity, audio, MakeSamples(1000));
        var reloaded = cache.TryLoad(identity, audio);
        Assert.NotNull(reloaded);
        Assert.Equal(WaveformSamples.GenerationBlockMs, reloaded!.BaseBlockMs);
    }

    [Fact]
    public void MissingAudio_IsAMiss_NoThrow()
    {
        var cache = CreateCache();
        var identity = StemCacheIdentity.ForAudioFile("Z:\\does\\not\\exist.wav");
        Assert.Null(cache.TryLoad(identity, "Z:\\does\\not\\exist.wav"));
    }

    [Fact]
    public void EmptySamples_AreNotSaved()
    {
        var cache = CreateCache();
        var audio = WriteAudio("song.wav");
        var identity = StemCacheIdentity.ForAudioFile(audio);

        cache.Save(identity, audio, WaveformSamples.Empty);

        Assert.False(Directory.Exists(Path.Combine(_root, "cache")) &&
                     Directory.GetFiles(Path.Combine(_root, "cache")).Length > 0);
    }

    [Fact]
    public void FromBasePeaks_MatchesTheHistoricInlinePyramid()
    {
        // The disk cache persists only level 0 and rebuilds the pyramid —
        // this pins the halving rule the live generator used inline before
        // the extraction (max-of-pair, odd tail carries, stop at <=32).
        var peaks = new float[100];
        var rng = new Random(42);
        for (var i = 0; i < peaks.Length; i++) peaks[i] = (float)rng.NextDouble();

        var samples = WaveformSamples.FromBasePeaks(peaks, 20.0);

        Assert.Equal(20.0, samples.BaseBlockMs);
        Assert.Same(peaks, samples.Mipmaps[0]);
        for (var level = 1; level < samples.Mipmaps.Length; level++)
        {
            var prev = samples.Mipmaps[level - 1];
            var cur = samples.Mipmaps[level];
            Assert.Equal((prev.Length + 1) / 2, cur.Length);
            for (var i = 0; i < cur.Length; i++)
            {
                var a = prev[2 * i];
                var b = 2 * i + 1 < prev.Length ? prev[2 * i + 1] : a;
                Assert.Equal(Math.Max(a, b), cur[i]);
            }
        }

        Assert.True(samples.Mipmaps[^1].Length <= 32);
        Assert.True(samples.Mipmaps.Length > 1);
    }
}
