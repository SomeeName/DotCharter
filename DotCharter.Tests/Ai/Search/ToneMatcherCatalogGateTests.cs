using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The catalog gate AT THE MATCHER'S BOUNDARY: the rigs that can be outside the
///     catalogue are the ones that did not come from the sampler — the caller's
///     PRIOR and the estimator's decoded chain — and until 2026-09-16 neither was
///     checked anywhere before it was rendered, scored and offered as a tone.
/// </summary>
public sealed class ToneMatcherCatalogGateTests
{
    /// <summary>The one-module chain the fake sampler draws and the fake renderer
    /// understands. A real catalogue module, so a sampled row is a legal rig.</summary>
    private const string ChainModule = "eq";

    private static DcRigPreset Chain() => new([
        new DcRigModule(ChainModule, new Dictionary<string, float>(StringComparer.Ordinal))
    ]);

    private sealed record Harness(ToneMatcher Matcher, MonoAudio Di, MonoAudio Target);

    private static async Task<Harness> BuildAsync(IRigEstimator? estimator = null)
    {
        var di = SearchFakes.Di(1.2);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var sim = new FakeSimilarity(extractor);
        var library = await RigLibrary.BuildAsync(
            di, new FakeRigSampler(ChainModule), renderer, extractor,
            new RigLibraryOptions(Count: 8, Seed: 4242), null, TestContext.Current.CancellationToken);

        var matcher = new ToneMatcher(
            renderer, sim, new FixedRigLibraryProvider(library), FakeLocalization.English(), estimator,
            new ToneMatcherOptions(
                MaxRenders: 40, RefineCandidates: 1,
                SelfDistanceWindow: TimeSpan.FromSeconds(0.5)));

        var target = await renderer.RenderAsync(di, Chain(), TestContext.Current.CancellationToken);
        return new Harness(matcher, di, target);
    }

    /// <summary>A prior naming gear this app does not have. The shape a language
    /// model produces when it names a real-world pedal the catalogue has no entry
    /// for.</summary>
    private static DcRigPreset UnknownModuleRig() => new([
        new DcRigModule("klon-centaur-gold", new Dictionary<string, float>(StringComparer.Ordinal))
    ]);

    [Fact]
    public async Task APriorWithAModuleTheCatalogueDoesNotHave_IsRejected_AndTheNoteNamesIt()
    {
        var h = await BuildAsync();

        var result = await h.Matcher.MatchAsync(
            new ToneMatchRequest(h.Target, h.Di, new ToneMatchPrior(UnknownModuleRig(), "ai:test"))
            { TargetIsRender = true },
            null, TestContext.Current.CancellationToken);

        var note = Assert.Single(result.Notes, n => n.StartsWith("rejected ", StringComparison.Ordinal));
        Assert.Contains("klon-centaur-gold", note, StringComparison.Ordinal);
        Assert.Contains("prior:ai:test", note, StringComparison.Ordinal);

        // Rejected, not clamped and not silently kept: no candidate carries it, and
        // the search still produced an answer from retrieval.
        Assert.DoesNotContain(result.Candidates,
            c => c.Rig.Modules.Any(m => m.ModuleTypeId == "klon-centaur-gold"));
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public async Task APriorWithAnOutOfRangeKnob_IsRejected_AndTheNoteNamesTheValue()
    {
        Assert.True(DcRigTargetCatalog.TryGetParam("eq", "midGainDb", out var spec));
        var rig = new DcRigPreset([
            new DcRigModule("eq", new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["midGainDb"] = spec.Max + 40f
            })
        ]);

        var h = await BuildAsync();
        var result = await h.Matcher.MatchAsync(
            new ToneMatchRequest(h.Target, h.Di, new ToneMatchPrior(rig, "ai:test")) { TargetIsRender = true },
            null, TestContext.Current.CancellationToken);

        var note = Assert.Single(result.Notes, n => n.StartsWith("rejected ", StringComparison.Ordinal));
        Assert.Contains("midGainDb", note, StringComparison.Ordinal);
        Assert.Contains("outside the catalogue range", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEstimatorGuessOutsideTheCatalogue_IsRejected_AndTheSearchStillAnswers()
    {
        var h = await BuildAsync(new UnknownModuleEstimator());

        var result = await h.Matcher.MatchAsync(
            new ToneMatchRequest(h.Target, h.Di, null) { TargetIsRender = true },
            null, TestContext.Current.CancellationToken);

        Assert.Contains(result.Notes, n => n.StartsWith("rejected estimator", StringComparison.Ordinal));
        Assert.NotEmpty(result.Candidates);
    }

    [Fact]
    public async Task EveryCandidateTheMatcherReturns_PassesTheCatalogGate()
    {
        // Including the refined ones: CMA-ES walks a normalised box and decodes back
        // through the catalogue's ranges, so a coordinate that escaped would land
        // here. The post-condition inside MatchAsync throws before it can.
        var h = await BuildAsync();

        var result = await h.Matcher.MatchAsync(
            new ToneMatchRequest(h.Target, h.Di, null) { TargetIsRender = true },
            null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, c =>
        {
            var fault = RigCatalogValidation.FirstFault(c.Rig);
            Assert.True(fault is null, $"{c.Origin}: {fault?.Message}");
        });
    }

    private sealed class UnknownModuleEstimator : IRigEstimator
    {
        public bool IsAvailable => true;

        public Task<RigEstimate> EstimateAsync(MonoAudio wet, CancellationToken ct) =>
            Task.FromResult(new RigEstimate(
                UnknownModuleRig(),
                new Dictionary<string, float>(StringComparer.Ordinal),
                0.5f,
                "fake-estimator"));
    }
}
