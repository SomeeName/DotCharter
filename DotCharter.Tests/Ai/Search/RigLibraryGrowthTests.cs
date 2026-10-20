using System.Text.Json;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The library that GROWS (2026-09-14): the append-only v3 file, the resumable
///     builder, and the provider policy that never blocks a match on more than
///     the small library.
///     <para>
///         The owner opened "Match from audio" on v0.55.0, read "Building tone
///         library: 1/4000 rigs rendered" — ~2.8 hours of CPU with nothing else
///         on the line — and, rightly, gave up. Every case here is one of the
///         properties that stop that from being possible again.
///     </para>
/// </summary>
public sealed class RigLibraryGrowthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dc-riggrow-{Guid.NewGuid():N}");

    public RigLibraryGrowthTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string File(string name) => Path.Combine(_dir, name);

    private static readonly MonoAudio Medley = SearchFakes.Di(1.0);

    private static RigLibraryBuilder Builder(
        string path, FakeRigRenderer renderer, int count = 8, int seed = 1234,
        IRigSampler? sampler = null, IToneFeatureExtractor? extractor = null) =>
        new(Medley, sampler ?? new FakeRigSampler("gain", "overdrive"), renderer,
            extractor ?? new FakeFeatureExtractor(), new RigLibraryOptions(Count: count, Seed: seed), path);

    private static string Json(DcRigPreset rig) => JsonSerializer.Serialize(rig);

    // ---- the v3 file ------------------------------------------------------------

    /// <summary>A file with fewer rows than K loads as a PARTIAL library — the
    /// whole point of the format — and a row a crash cut short is dropped, not
    /// reported and not fatal.</summary>
    [Fact]
    public async Task APartialFileLoads_AndATornLastRowIsDropped()
    {
        var path = File("partial.bin");
        var renderer = new FakeRigRenderer();
        var builder = Builder(path, renderer, count: 8);
        await builder.GrowAsync(5, null, CancellationToken.None);

        var partial = RigLibrary.TryLoad(path, new FakeFeatureExtractor().Id, 8, 1234, builder.DiFingerprint);
        Assert.NotNull(partial);
        Assert.Equal(5, partial!.Count);
        Assert.Equal(8, partial.TargetCount);
        Assert.False(partial.IsComplete);

        // Tear the last row: chop a few bytes off the end.
        var bytes = await System.IO.File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllBytesAsync(path, bytes[..^7], TestContext.Current.CancellationToken);

        var torn = RigLibrary.TryLoad(path, new FakeFeatureExtractor().Id, 8, 1234, builder.DiFingerprint, out var validEnd);
        Assert.NotNull(torn);
        Assert.Equal(4, torn!.Count);
        Assert.True(validEnd > 0 && validEnd < bytes.Length - 7,
            $"validEnd {validEnd} must point at the end of row 4, inside the {bytes.Length - 7} bytes left");
        for (var i = 0; i < 4; i++)
            Assert.Equal(Json(partial.Entries[i].Rig), Json(torn.Entries[i].Rig));
    }

    /// <summary>
    ///     Resuming continues at the last complete row — including over a torn
    ///     one — and the result is byte-for-byte THE SAME library a one-shot build
    ///     produces: same rigs in the same order, same features. That identity is
    ///     what lets a library rendered on the owner's box stand in for a local one.
    /// </summary>
    [Fact]
    public async Task ResumingContinuesAtTheLastCompleteRow_AndIsTheSameLibraryAsOneShot()
    {
        var path = File("resume.bin");
        var extractor = new FakeFeatureExtractor();

        var first = new FakeRigRenderer();
        await Builder(path, first, count: 8).GrowAsync(3, null, CancellationToken.None);
        Assert.Equal(3, first.Renders);

        // A crash mid-row: the file ends in half a row.
        var bytes = await System.IO.File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllBytesAsync(path, [.. bytes, 1, 2, 3], TestContext.Current.CancellationToken);

        var second = new FakeRigRenderer();
        var resumed = Builder(path, second, count: 8);
        Assert.True(resumed.ResumedFromDisk);
        Assert.Equal(3, resumed.Rows);
        await resumed.GrowAsync(8, null, CancellationToken.None);
        Assert.Equal(5, second.Renders); // NOT 8: the first three were kept
        Assert.True(resumed.IsComplete);

        var grown = RigLibrary.TryLoad(path, extractor.Id, 8, 1234, resumed.DiFingerprint);
        var oneShot = await RigLibrary.BuildAsync(
            Medley, new FakeRigSampler("gain", "overdrive"), new FakeRigRenderer(), extractor,
            new RigLibraryOptions(Count: 8, Seed: 1234), null, CancellationToken.None);

        Assert.NotNull(grown);
        Assert.True(grown!.IsComplete);
        Assert.Equal(oneShot.Count, grown.Count);
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(Json(oneShot.Entries[i].Rig), Json(grown.Entries[i].Rig));
            Assert.Equal(oneShot.Entries[i].Features, grown.Entries[i].Features);
        }
    }

    /// <summary>A file whose rows do not re-draw from the seed (a changed sampler
    /// or catalogue) is started over, never appended to — two libraries in one
    /// file would produce plausible distances and the wrong rigs.</summary>
    [Fact]
    public async Task AFileThatDoesNotReDrawFromTheSeed_IsStartedOver()
    {
        var path = File("redraw.bin");
        await Builder(path, new FakeRigRenderer(), sampler: new FakeRigSampler("gain", "overdrive"))
            .GrowAsync(3, null, CancellationToken.None);

        var other = Builder(path, new FakeRigRenderer(), sampler: new FakeRigSampler("svf"));
        Assert.False(other.ResumedFromDisk);
        Assert.Equal(0, other.Rows);

        // …and the same key with the same sampler DOES resume, so the case above
        // is about the rigs and not about the file simply being reopened.
        await Builder(path, new FakeRigRenderer(), sampler: new FakeRigSampler("svf"))
            .GrowAsync(2, null, CancellationToken.None);
        Assert.True(Builder(path, new FakeRigRenderer(), sampler: new FakeRigSampler("svf")).ResumedFromDisk);
    }

    /// <summary>Another key on disk (here: another seed) is a fresh file.</summary>
    [Fact]
    public async Task AnotherKeyOnDisk_IsAFreshFile()
    {
        var path = File("key.bin");
        await Builder(path, new FakeRigRenderer(), seed: 1).GrowAsync(3, null, CancellationToken.None);

        var b = Builder(path, new FakeRigRenderer(), seed: 2);
        Assert.False(b.ResumedFromDisk);
        Assert.Equal(0, b.Rows);
        Assert.Null(RigLibrary.TryLoad(path, new FakeFeatureExtractor().Id, 8, 1, b.DiFingerprint));
    }

    /// <summary>Cancellation stops BETWEEN renders and keeps what was rendered.</summary>
    [Fact]
    public async Task CancellationKeepsTheRowsAlreadyRendered()
    {
        var path = File("cancel.bin");
        var renderer = new FakeRigRenderer();
        var builder = Builder(path, renderer, count: 20);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.GrowAsync(
            20, new LineSink(_ => { if (builder.Rows >= 4) cts.Cancel(); }), cts.Token));

        Assert.Equal(4, builder.Rows);
        Assert.Equal(4, Builder(path, new FakeRigRenderer(), count: 20).Rows);
    }

    // ---- the provider policy ------------------------------------------------------

    private RigLibraryProvider Provider(
        IRigRenderer renderer, int count, int firstMatch, bool background,
        Func<string?>? installed = null, string? cache = null) =>
        new(_ => Task.FromResult(Medley),
            new FakeRigSampler("gain", "overdrive"), renderer, new FakeFeatureExtractor(),
            new RigLibraryOptions(Count: count, Seed: 1234, FirstMatchCount: firstMatch),
            installedLibrary: installed,
            cachePath: cache ?? File("cache.bin"),
            growInBackground: background);

    /// <summary>
    ///     <b>The first match waits for the SMALL library and nothing more.</b>
    ///     K = 40, first-match 6: the first call renders six rows, reports each
    ///     with an ETA, and returns a six-row library; a second call renders
    ///     nothing. (With the grower off, so the count is exact.)
    /// </summary>
    [Fact]
    public async Task TheFirstMatchRendersTheSmallLibraryOnly()
    {
        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 6, background: false);
        var lines = new List<string>();

        var lib = await provider.GetAsync(new LineSink(lines.Add), CancellationToken.None);

        Assert.Equal(6, lib.Count);
        Assert.Equal(40, lib.TargetCount);
        Assert.False(lib.IsComplete);
        Assert.Equal(6, renderer.Renders);
        Assert.Contains(lines, l => l.StartsWith("Building tone library: 1/6", StringComparison.Ordinal));
        Assert.Contains(lines, l => l == "Building tone library: 6/6");
        Assert.Contains(lines, l => l.Contains("~", StringComparison.Ordinal) && l.EndsWith(" left", StringComparison.Ordinal));
        Assert.Contains("Tone library: 6/40 rigs (growing in the background)", lines);

        var again = await provider.GetAsync(null, CancellationToken.None);
        Assert.Equal(6, again.Count);
        Assert.Equal(6, renderer.Renders);
    }

    /// <summary>A second provider over the same cache file (an app restart) finds
    /// the rows and renders nothing for the first match.</summary>
    [Fact]
    public async Task AnAppRestartFindsTheRowsOnDisk()
    {
        var one = new FakeRigRenderer();
        using (var provider = Provider(one, count: 40, firstMatch: 6, background: false))
            await provider.GetAsync(null, CancellationToken.None);

        var two = new FakeRigRenderer();
        using var restarted = Provider(two, count: 40, firstMatch: 6, background: false);
        var lib = await restarted.GetAsync(null, CancellationToken.None);

        Assert.Equal(6, lib.Count);
        Assert.Equal(0, two.Renders);
    }

    /// <summary>
    ///     After the first match the library grows on its own thread, at
    ///     BelowNormal, on the renderer's synchronous path; the next match sees
    ///     every row it has produced so far, and eventually all of K.
    /// </summary>
    [Fact]
    public async Task TheLibraryGrowsInTheBackground_AtBelowNormal_AndLaterMatchesSeeIt()
    {
        var renderer = new PriorityRecordingRenderer();
        using var provider = Provider(renderer, count: 20, firstMatch: 4, background: true);

        var first = await provider.GetAsync(null, CancellationToken.None);
        Assert.Equal(4, first.Count);

        await WaitUntilAsync(() => !provider.IsGrowing, TimeSpan.FromSeconds(30));

        var later = await provider.GetAsync(null, CancellationToken.None);
        Assert.Equal(20, later.Count);
        Assert.True(later.IsComplete);
        Assert.Equal(20, renderer.Priorities.Count);
        Assert.All(renderer.Priorities.Skip(4), p => Assert.Equal(ThreadPriority.BelowNormal, p));
        Assert.All(renderer.Priorities.Take(4), p => Assert.NotEqual(ThreadPriority.BelowNormal, p));
        Assert.All(renderer.OnCallerThread.Skip(4), Assert.True);
    }

    /// <summary>
    ///     <b>Growth is held while a match runs.</b> One offline engine, serialised
    ///     renders: a library row in flight would queue in front of every one of
    ///     the match's renders. With the hold taken the grower parks; releasing it
    ///     lets it finish.
    /// </summary>
    [Fact]
    public async Task GrowthPausesWhileAMatchHoldsIt()
    {
        var renderer = new PriorityRecordingRenderer();
        using var provider = Provider(renderer, count: 12, firstMatch: 3, background: true);

        using (provider.PauseGrowth())
        {
            await provider.GetAsync(null, CancellationToken.None);
            Assert.True(provider.IsGrowing, "the grower starts (parked) even under a hold");
            // Deliberately generous: a parked grower cannot render however long
            // this waits, and a grower that was NOT parked renders 9 rows in well
            // under this on the fake.
            await Task.Delay(400, TestContext.Current.CancellationToken);
            Assert.Equal(3, renderer.Priorities.Count);
        }

        await WaitUntilAsync(() => !provider.IsGrowing, TimeSpan.FromSeconds(30));
        Assert.Equal(12, renderer.Priorities.Count);
    }

    /// <summary>
    ///     A SHIPPED library for the key is used whole and the local build is not
    ///     started: zero renders. That is what the release asset buys every user.
    /// </summary>
    [Fact]
    public async Task AnInstalledLibraryForTheKey_IsUsedWithoutRendering()
    {
        var installedPath = File(Path.Combine("rig-library", "shipped.bin"));
        Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
        var shipped = await RigLibrary.BuildAsync(
            Medley, new FakeRigSampler("gain", "overdrive"), new FakeRigRenderer(), new FakeFeatureExtractor(),
            new RigLibraryOptions(Count: 40, Seed: 1234), null, CancellationToken.None);
        shipped.Save(installedPath, 40);

        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 6, background: false,
            installed: () => installedPath);
        var lines = new List<string>();

        var lib = await provider.GetAsync(new LineSink(lines.Add), CancellationToken.None);

        Assert.Equal(40, lib.Count);
        Assert.True(lib.IsComplete);
        Assert.Equal(0, renderer.Renders);
        Assert.Contains("Tone library: 40 rigs from cache", lines);
    }

    /// <summary>…and a shipped library for ANOTHER DI, or an incomplete one, is
    /// ignored: the local small-first path runs. The fingerprint is the guard.</summary>
    [Fact]
    public async Task AnInstalledLibraryForAnotherDi_IsIgnored()
    {
        var installedPath = File("other-di.bin");
        var other = await RigLibrary.BuildAsync(
            SearchFakes.Di(1.0, seed: 77), new FakeRigSampler("gain", "overdrive"), new FakeRigRenderer(),
            new FakeFeatureExtractor(), new RigLibraryOptions(Count: 40, Seed: 1234), null, CancellationToken.None);
        other.Save(installedPath, 40);

        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 6, background: false,
            installed: () => installedPath);

        var lib = await provider.GetAsync(null, CancellationToken.None);

        Assert.Equal(6, lib.Count);
        Assert.Equal(6, renderer.Renders);
    }

    /// <summary>A library installed AFTER the local build started is picked up by
    /// the next match — the settings page can install it mid-session.</summary>
    [Fact]
    public async Task ALibraryInstalledMidSession_IsPickedUpByTheNextMatch()
    {
        var installedPath = File("late.bin");
        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 6, background: false,
            installed: () => installedPath);

        Assert.Equal(6, (await provider.GetAsync(null, CancellationToken.None)).Count);

        var shipped = await RigLibrary.BuildAsync(
            Medley, new FakeRigSampler("gain", "overdrive"), new FakeRigRenderer(), new FakeFeatureExtractor(),
            new RigLibraryOptions(Count: 40, Seed: 1234), null, CancellationToken.None);
        shipped.Save(installedPath, 40);

        Assert.Equal(40, (await provider.GetAsync(null, CancellationToken.None)).Count);
        Assert.Equal(6, renderer.Renders);
    }

    /// <summary>Cancelling the first match's wait keeps the rows rendered so far,
    /// and the next attempt continues from them.</summary>
    [Fact]
    public async Task CancellingTheFirstMatch_KeepsWhatWasRendered()
    {
        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 10, background: false);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(
            new LineSink(_ => { if (renderer.Renders >= 4) cts.Cancel(); }), cts.Token));
        var rendered = renderer.Renders;
        Assert.InRange(rendered, 4, 9);

        var lib = await provider.GetAsync(null, CancellationToken.None);
        Assert.Equal(10, lib.Count);
        Assert.Equal(10, renderer.Renders);
    }

    // ---- the one-time import of a v2 file (2026-09-14) ------------------------------

    private static readonly string Fp = RigLibrary.Fingerprint(Medley);
    private const string Ext = "fake-logband24mod32-v1"; // FakeFeatureExtractor.Id, name-safe as is

    /// <summary>The 2026-09-12 format: row count + dimension in the header, no markers.</summary>
    private static void WriteV2(string path, string extractorId, int k, int seed, string di,
        IReadOnlyList<RigLibraryEntry> rows)
    {
        using var fs = System.IO.File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write(0x4C495244u);
        w.Write(2);
        w.Write(extractorId);
        w.Write(k);
        w.Write(seed);
        w.Write(di);
        w.Write(rows.Count);
        w.Write(rows.Count > 0 ? rows[0].Features.Length : 0);
        foreach (var e in rows)
        {
            w.Write(JsonSerializer.Serialize(e.Rig));
            w.Write(e.Features.Length);
            foreach (var f in e.Features) w.Write(f);
        }
    }

    private static Task<RigLibrary> Rows(int count, int seed = 1234) => RigLibrary.BuildAsync(
        Medley, new FakeRigSampler("gain", "overdrive"), new FakeRigRenderer(), new FakeFeatureExtractor(),
        new RigLibraryOptions(Count: count, Seed: seed), null, CancellationToken.None);

    /// <summary>
    ///     <b>The owner's box already held a rendered v2 library for the bundled
    ///     DI</b> (K = 96, the eval work), and a v3 provider that called it a
    ///     cache miss would have rendered the same rows again. A v2 file for the
    ///     same key is adopted BEFORE anything is rendered: three verification
    ///     renders, not ninety-six — and the adopted rows are on disk in the v3
    ///     file, so a restart does not import twice.
    /// </summary>
    [Fact]
    public async Task ALegacyV2LibraryForTheSameKey_IsImportedBeforeAnythingIsRendered()
    {
        var legacy = await Rows(10);
        WriteV2(File($"library-{Ext}-10-1234-{Fp}.bin"), Ext, 10, 1234, Fp, legacy.Entries);
        var cache = File($"library-{Ext}-40-1234-{Fp}.bin");

        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 6, background: false, cache: cache);
        var lines = new List<string>();

        var lib = await provider.GetAsync(new LineSink(lines.Add), CancellationToken.None);

        Assert.Equal(10, lib.Count);
        Assert.Equal(RigLibraryBuilder.LegacyVerificationRenders, renderer.Renders);
        Assert.Contains("Tone library: checking an older library (10 rigs)…", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Building tone library", StringComparison.Ordinal));
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(Json(legacy.Entries[i].Rig), Json(lib.Entries[i].Rig));
            Assert.Equal(legacy.Entries[i].Features, lib.Entries[i].Features);
        }

        // On disk in the v3 file, so the next process finds them without importing.
        var again = new FakeRigRenderer();
        using var restarted = Provider(again, count: 40, firstMatch: 6, background: false, cache: cache);
        Assert.Equal(10, (await restarted.GetAsync(null, CancellationToken.None)).Count);
        Assert.Equal(0, again.Renders);
        Assert.True(System.IO.File.Exists(File($"library-{Ext}-10-1234-{Fp}.bin")), "the v2 file is left where it was");
    }

    /// <summary>A file whose HEADER names another DI is refused even when its
    /// name matches, and an un-fingerprinted v1-era file is never opened at all:
    /// its DI is unknown.</summary>
    [Fact]
    public async Task ALegacyFileForAnotherDi_OrWithoutAFingerprint_IsNotImported()
    {
        var legacy = await Rows(10);
        WriteV2(File($"library-{Ext}-10-1234-{Fp}.bin"), Ext, 10, 1234, "deadbeef", legacy.Entries);
        await System.IO.File.WriteAllBytesAsync(File($"library-{Ext}-96-1234.bin"), [1, 2, 3, 4],
            TestContext.Current.CancellationToken);

        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 6, background: false,
            cache: File($"library-{Ext}-40-1234-{Fp}.bin"));

        var lib = await provider.GetAsync(null, CancellationToken.None);

        Assert.Equal(6, lib.Count);
        Assert.Equal(6, renderer.Renders);
    }

    /// <summary>Rows that do not re-draw from the seed are refused on STRUCTURE,
    /// before a single verification render is spent.</summary>
    [Fact]
    public async Task ALegacyFileWhoseRigsDoNotReDraw_IsRefusedWithoutRendering()
    {
        var otherSeed = await Rows(10, seed: 999);
        WriteV2(File($"library-{Ext}-10-1234-{Fp}.bin"), Ext, 10, 1234, Fp, otherSeed.Entries);

        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 6, background: false,
            cache: File($"library-{Ext}-40-1234-{Fp}.bin"));

        var lib = await provider.GetAsync(null, CancellationToken.None);

        Assert.Equal(6, lib.Count);
        Assert.Equal(6, renderer.Renders);
    }

    /// <summary>The right rigs with features that do NOT come back from a
    /// re-render (another engine build, another extractor behind the same id)
    /// are refused on the verification renders, and the small build runs.</summary>
    [Fact]
    public async Task ALegacyFileWhoseFeaturesDoNotReRender_IsRefused()
    {
        var legacy = await Rows(10);
        var wrong = legacy.Entries.Select(e => e with { Features = new float[e.Features.Length] }).ToList();
        WriteV2(File($"library-{Ext}-10-1234-{Fp}.bin"), Ext, 10, 1234, Fp, wrong);

        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 40, firstMatch: 6, background: false,
            cache: File($"library-{Ext}-40-1234-{Fp}.bin"));

        var lib = await provider.GetAsync(null, CancellationToken.None);

        Assert.Equal(6, lib.Count);
        // One verification render refused it (the first probe), then the six real ones.
        Assert.Equal(1 + 6, renderer.Renders);
    }

    /// <summary>A v2 file LARGER than K is imported up to K, and the largest
    /// matching file wins over a smaller one.</summary>
    [Fact]
    public async Task TheLargestLegacyFileWins_AndIsCappedAtK()
    {
        var small = await Rows(4);
        var big = await Rows(12);
        WriteV2(File($"library-{Ext}-4-1234-{Fp}.bin"), Ext, 4, 1234, Fp, small.Entries);
        WriteV2(File($"library-{Ext}-12-1234-{Fp}.bin"), Ext, 12, 1234, Fp, big.Entries);

        var renderer = new FakeRigRenderer();
        using var provider = Provider(renderer, count: 8, firstMatch: 6, background: false,
            cache: File($"library-{Ext}-8-1234-{Fp}.bin"));

        var lib = await provider.GetAsync(null, CancellationToken.None);

        Assert.Equal(8, lib.Count);
        Assert.True(lib.IsComplete);
        Assert.Equal(RigLibraryBuilder.LegacyVerificationRenders, renderer.Renders);
    }

    // ---- the matcher takes the hold -------------------------------------------------

    /// <summary>The matcher holds growth for the WHOLE search and releases it
    /// after — pinned on the seam, because a hold taken around retrieval alone
    /// would leave the 48 refinement renders contending.</summary>
    [Fact]
    public async Task TheMatcherHoldsGrowthForTheWholeSearch()
    {
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var library = await RigLibrary.BuildAsync(
            Medley, new FakeRigSampler("gain", "overdrive"), renderer, extractor,
            new RigLibraryOptions(Count: 8, Seed: 1234), null, CancellationToken.None);
        var provider = new HoldCountingProvider(library);
        var matcher = new ToneMatcher(renderer, new FakeSimilarity(extractor), provider,
            FakeLocalization.English(),
            options: new ToneMatcherOptions(MaxRenders: 6, RetrievalTopK: 2, RefineCandidates: 1,
                SelfDistanceWindow: TimeSpan.FromSeconds(0.25)));
        var target = await renderer.RenderAsync(Medley, library.Entries[2].Rig, CancellationToken.None);

        var heldDuringRenders = new List<bool>();
        renderer.OnRender = () => heldDuringRenders.Add(provider.Held);
        await matcher.MatchAsync(new ToneMatchRequest(target, Medley, null) { TargetIsRender = true },
            null, CancellationToken.None);

        Assert.Equal(1, provider.Holds);
        Assert.False(provider.Held);
        Assert.NotEmpty(heldDuringRenders);
        Assert.All(heldDuringRenders, Assert.True);
    }

    // ---- helpers ------------------------------------------------------------------

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the background grower");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private sealed class LineSink(Action<string> onReport) : IProgress<string>
    {
        public void Report(string value) => onReport(value);
    }

    /// <summary>The fake renderer with a synchronous face, recording the priority
    /// of the thread each render ran on and whether it was the caller's.</summary>
    private sealed class PriorityRecordingRenderer : ISynchronousRigRenderer
    {
        private readonly FakeRigRenderer _inner = new();
        private readonly Lock _gate = new();

        public List<ThreadPriority> Priorities { get; } = [];
        public List<bool> OnCallerThread { get; } = [];

        public MonoAudio Render(MonoAudio di, DcRigPreset rig, CancellationToken ct)
        {
            lock (_gate)
            {
                Priorities.Add(Thread.CurrentThread.Priority);
                OnCallerThread.Add(true);
            }

            return _inner.RenderAsync(di, rig, ct).GetAwaiter().GetResult();
        }

        public Task<MonoAudio> RenderAsync(MonoAudio di, DcRigPreset rig, CancellationToken ct)
        {
            lock (_gate)
            {
                Priorities.Add(Thread.CurrentThread.Priority);
                OnCallerThread.Add(false);
            }

            return _inner.RenderAsync(di, rig, ct);
        }
    }

    private sealed class HoldCountingProvider(RigLibrary library) : IRigLibraryProvider
    {
        public int Holds { get; private set; }
        public bool Held { get; private set; }

        public Task<RigLibrary> GetAsync(IProgress<string>? status, CancellationToken ct) =>
            Task.FromResult(library);

        public IDisposable PauseGrowth()
        {
            Holds++;
            Held = true;
            return new Release(this);
        }

        private sealed class Release(HoldCountingProvider owner) : IDisposable
        {
            public void Dispose() => owner.Held = false;
        }
    }
}
