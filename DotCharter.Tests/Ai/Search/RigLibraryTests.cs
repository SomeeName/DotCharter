using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;

namespace DotCharter.Tests.Ai.Search;

/// <summary>The retrieval half: build, query, persist.</summary>
public sealed class RigLibraryTests
{
    private static async Task<(RigLibrary Library, FakeRigRenderer Renderer, FakeSimilarity Sim, MonoAudio Medley)>
        BuildAsync(int count = 64)
    {
        var medley = SearchFakes.Di(1.0);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var lib = await RigLibrary.BuildAsync(
            medley, new FakeRigSampler("gain", "overdrive"), renderer, extractor,
            new RigLibraryOptions(Count: count, Seed: 1234), null, CancellationToken.None);
        return (lib, renderer, new FakeSimilarity(extractor), medley);
    }

    [Fact]
    public async Task ARenderOfALibraryRig_RetrievesThatRigFirst()
    {
        var (lib, renderer, sim, medley) = await BuildAsync();
        var target = lib.Entries[17].Rig;
        var wet = await renderer.RenderAsync(medley, target, CancellationToken.None);

        var hits = lib.Query(sim.Extractor.Extract(wet), 5, sim);

        Assert.Equal(17, hits[0].Index);
        Assert.True(hits[0].Distance <= hits[1].Distance);
    }

    [Fact]
    public async Task ReportsItsBuildCostInRenders()
    {
        var (lib, renderer, _, _) = await BuildAsync(32);
        Assert.Equal(32, lib.RenderCount);
        Assert.Equal(32, renderer.Renders);
        Assert.Equal(32, lib.Count);
    }

    [Fact]
    public async Task SurvivesARoundTripThroughTheCacheFile()
    {
        var (lib, _, sim, _) = await BuildAsync(24);
        var path = Path.Combine(Path.GetTempPath(), $"dc-riglib-{Guid.NewGuid():N}.bin");
        try
        {
            lib.Save(path, 24);
            var back = RigLibrary.TryLoad(path, sim.Extractor.Id, 24, 1234, lib.DiFingerprint);

            Assert.NotNull(back);
            Assert.Equal(lib.Count, back!.Count);
            Assert.Equal(0, back.RenderCount); // a cached library cost no renders
            for (var i = 0; i < lib.Count; i++)
            {
                Assert.Equal(lib.Entries[i].Features, back.Entries[i].Features);
                Assert.Equal(lib.Entries[i].Rig.Modules.Count, back.Entries[i].Rig.Modules.Count);
                Assert.Equal(lib.Entries[i].Rig.Modules[1].Params["drive"],
                    back.Entries[i].Rig.Modules[1].Params["drive"], 5);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ACacheForAnotherKey_IsRefused_NotMisread()
    {
        var (lib, _, sim, _) = await BuildAsync(16);
        var path = Path.Combine(Path.GetTempPath(), $"dc-riglib-{Guid.NewGuid():N}.bin");
        try
        {
            lib.Save(path, 16);
            var di = lib.DiFingerprint;
            Assert.Null(RigLibrary.TryLoad(path, "another-extractor", 16, 1234, di));
            Assert.Null(RigLibrary.TryLoad(path, sim.Extractor.Id, 17, 1234, di));
            Assert.Null(RigLibrary.TryLoad(path, sim.Extractor.Id, 16, 999, di));

            // …and the fourth part of the key, which was missing until 2026-09-12: a
            // library built over ANOTHER dry take describes a different signal, and
            // loading it produces entirely plausible distances and the wrong rigs.
            Assert.Null(RigLibrary.TryLoad(
                path, sim.Extractor.Id, 16, 1234, RigLibrary.Fingerprint(SearchFakes.Di(1.0, seed: 77))));

            // A truncated / garbage file is a cache miss, never a throw.
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);
            Assert.Null(RigLibrary.TryLoad(path, sim.Extractor.Id, 16, 1234, di));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AQueryFromAnotherExtractor_IsRejected()
    {
        var (lib, _, _, _) = await BuildAsync(8);
        var other = new OtherExtractor();
        var ex = Assert.Throws<InvalidOperationException>(
            () => lib.Query(new ToneFeatures([0f], other.Id), 3, new FakeSimilarity(other)));
        Assert.Contains("extractor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCachePathCarriesTheWholeKey()
    {
        var p = RigLibrary.DefaultCachePath("afx-rep/v2", 4000, 20260912, "deadbeef");
        Assert.EndsWith("library-afx-rep_v2-4000-20260912-deadbeef.bin", p);
        Assert.Contains(Path.Combine("DotCharter", "ai-match"), p);
    }

    /// <summary>
    ///     The DI's identity is part of the key, and it is derived from the SAMPLES.
    ///     <para>
    ///         Two takes of the same length at the same rate are a different signal,
    ///         and the generated medley has no path to key on — so a length, a rate or
    ///         a file name would all have collided silently. The eval lane had grown a
    ///         workaround for exactly this in its caller (refusing to build a library
    ///         when <c>DOTCHARTER_AUDIO_EVAL_DI</c> is set), which is a bug in the
    ///         library answered in the caller.
    ///     </para>
    /// </summary>
    [Fact]
    public void TwoDifferentDis_HaveDifferentFingerprintsAndThereforeDifferentCaches()
    {
        var a = SearchFakes.Di(1.0);
        var b = SearchFakes.Di(1.0, seed: 77);

        Assert.Equal(a.Samples.Length, b.Samples.Length);
        Assert.Equal(a.SampleRate, b.SampleRate);
        Assert.NotEqual(RigLibrary.Fingerprint(a), RigLibrary.Fingerprint(b));

        // …and stable: the same take fingerprints the same, or every run rebuilds.
        Assert.Equal(RigLibrary.Fingerprint(a), RigLibrary.Fingerprint(SearchFakes.Di(1.0)));

        Assert.NotEqual(
            RigLibrary.DefaultCachePath("x", 96, 1, RigLibrary.Fingerprint(a)),
            RigLibrary.DefaultCachePath("x", 96, 1, RigLibrary.Fingerprint(b)));
    }

    /// <summary>A library remembers which DI it was built over, so the identity in
    /// the header is the library's own answer and not one the caller supplies twice.
    /// </summary>
    [Fact]
    public async Task ALibraryKnowsWhichDiItWasBuiltOver()
    {
        var (lib, _, _, medley) = await BuildAsync(8);
        Assert.Equal(RigLibrary.Fingerprint(medley), lib.DiFingerprint);
    }

    private sealed class OtherExtractor : IToneFeatureExtractor
    {
        public string Id => "other-v1";
        public ToneFeatures Extract(MonoAudio audio) => new([0f], Id);
    }
}
