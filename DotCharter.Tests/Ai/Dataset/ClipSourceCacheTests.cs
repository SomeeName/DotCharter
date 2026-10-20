using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tools.RigDataset;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Ai.Dataset;

/// <summary>
///     The dataset renderer's DI/bed read cache is bounded by BYTES.
///     <para>
///         It used to be bounded by ENTRY COUNT — <c>if (_cache.Count > 4_096)
///         _cache.Clear()</c> — over entries that are one decoded 5 s mono clip
///         each (240,000 samples, 960 KB). That is a ~3.9 GB ceiling per render
///         process and <c>--threads 4</c> runs four of them: the overnight 100k
///         render reached 9.5 and 10.3 GB of private bytes per child after 140
///         minutes. Measured on a 400-clip probe over the real 4,234-file pool
///         (2026-09-13): 114 MB → 680 MB in 350 clips, a clean 1.62 MB/clip —
///         one whole DI file per clip, because the pool is drawn uniformly and
///         almost every draw was a first visit. The same probe over a 20-file
///         pool that FITS was flat at 83–133 MB, which is what identified the
///         cache rather than the engine, the rig or the WAV writer.
///     </para>
///     <para>
///         These are pure-file tests: no engine, no device, no corpus. They run
///         everywhere <c>RigDatasetRenderTests</c> skips.
///     </para>
/// </summary>
public sealed class ClipSourceCacheTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dc-clipsource-" + Guid.NewGuid().ToString("N"));

    public ClipSourceCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>Writes <paramref name="files" /> mono WAVs of <paramref name="frames" />
    /// samples each and returns their decoded size in bytes.</summary>
    private long MakePool(int files, int frames)
    {
        for (var f = 0; f < files; f++)
        {
            var x = new float[frames];
            for (var i = 0; i < frames; i++) x[i] = 0.2f * MathF.Sin(2f * MathF.PI * (110f + f) * i / Rate);
            using var w = new WavWriter(Path.Combine(_root, $"clip{f:D3}.wav"), Rate, 1, WavSampleFormat.Pcm16);
            w.Write(x);
        }

        return (long)frames * sizeof(float);
    }

    [Fact]
    public void ReadCache_IsBoundedByBytes_NotByEntryCount()
    {
        const int files = 40;
        var perFile = MakePool(files, frames: Rate / 4); // 12,000 samples = 48 KB decoded
        var budget = perFile * 5;

        var pool = ClipSource.Scan(_root, budget);
        Assert.Equal(files, pool.Count);

        foreach (var rel in pool.Relative)
        {
            Assert.Equal(Rate / 4, pool.Read(rel).Length);
            // Asserted on EVERY read, not only at the end: a cache that overshoots
            // and then trims is still the peak that killed the render.
            Assert.True(pool.CachedBytes <= budget,
                $"cache held {pool.CachedBytes} bytes against a {budget}-byte budget after '{rel}'.");
        }

        // …and it really is holding something: a bound met by caching NOTHING
        // would pass the line above while making every draw a re-decode.
        Assert.InRange(pool.CachedCount, 1, 5);
        Assert.True(pool.CachedBytes > 0);
    }

    /// <summary>
    ///     The render's own shape: a pool far larger than the budget, drawn at
    ///     random, thousands of times. Retention must be flat in the number of
    ///     DRAWS — that is the property the count bound did not have.
    ///     <para>
    ///         Asserted on <see cref="ClipSource.CachedBytes" /> rather than on
    ///         <c>GC.GetTotalMemory</c> deliberately: the gate runs collections in
    ///         parallel, so a process-wide heap delta measures whatever else was
    ///         allocating. This number is exact and is the thing that was wrong.
    ///     </para>
    /// </summary>
    [Fact]
    public void ADrawFromAPoolLargerThanTheBudget_RetainsNothingExtraPerDraw()
    {
        const int files = 40;
        var perFile = MakePool(files, frames: Rate / 4);
        var budget = perFile * 4;
        var poolBytes = perFile * files;

        var pool = ClipSource.Scan(_root, budget);
        var rng = new Random(20260913);
        var peak = 0L;
        for (var draw = 0; draw < 4_000; draw++)
        {
            _ = pool.Read(pool.Relative[rng.Next(pool.Count)]);
            peak = Math.Max(peak, pool.CachedBytes);
        }

        // The count-bounded cache held the WHOLE pool after ~200 draws (4,096
        // entries is 100× this pool) and never released it.
        Assert.True(peak <= budget, $"peak retention {peak} bytes against a {budget}-byte budget "
                                    + $"(the whole pool is {poolBytes} bytes).");
        Assert.True(pool.CachedCount <= 4);
    }

    [Fact]
    public void TheCacheIsLru_SoTheMostRecentlyReadSurvives()
    {
        var perFile = MakePool(files: 8, frames: Rate / 4);
        var pool = ClipSource.Scan(_root, perFile * 2);

        var first = pool.Relative[0];
        var a0 = pool.Read(first);
        Assert.Same(a0, pool.Read(first)); // a hit hands back the same array

        // Three more files through a two-file cache evicts it…
        for (var i = 1; i < 4; i++) _ = pool.Read(pool.Relative[i]);
        var a1 = pool.Read(first);
        Assert.NotSame(a0, a1);
        Assert.Same(a1, pool.Read(first));

        // …but a file that keeps being READ is not evicted, which is the whole
        // difference between an LRU and a ring: `first` is the oldest INSERTION
        // at every step below and survives all of them.
        for (var i = 4; i < 8; i++)
        {
            _ = pool.Read(pool.Relative[i]);
            Assert.Same(a1, pool.Read(first));
        }

        Assert.True(pool.CachedBytes <= perFile * 2);
    }

    [Fact]
    public void AFileBiggerThanTheWholeBudget_IsReturnedButNotCached()
    {
        var perFile = MakePool(files: 2, frames: Rate);
        var pool = ClipSource.Scan(_root, perFile / 2);

        var x = pool.Read(pool.Relative[0]);
        Assert.Equal(Rate, x.Length);
        Assert.Equal(0, pool.CachedBytes);
        Assert.Equal(0, pool.CachedCount);
    }

    [Fact]
    public void TheDefaultBudgetIsTheOneTheRendererShips() =>
        // A render process runs one of these per pool and --threads N runs N
        // processes, so the default is a per-process figure that has to be
        // multiplied before it is judged. Pinned so a future "let's cache more"
        // is a deliberate edit with this arithmetic in front of it.
        Assert.Equal(192L * 1024 * 1024, ClipSource.DefaultCacheBudgetBytes);
}
