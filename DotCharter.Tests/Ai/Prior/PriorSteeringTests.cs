using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tests.Ai.Search;

namespace DotCharter.Tests.Ai.Prior;

/// <summary>
///     What the prior actually DOES to the search, over the search lane's own fakes
///     (no engine, no DLL, no audio device).
///     <para>
///         Three claims, three tests: the prior steers RETRIEVAL (its family choice
///         changes which library rows are shortlisted at all), it seeds CMA-ES from
///         its own knob vector with a wider sigma, and it is INERT when absent — the
///         pure-audio path is the plain top-k it always was.
///     </para>
/// </summary>
public sealed class PriorSteeringTests
{
    private static readonly string[] EqChain = ["eq"];
    private static readonly string[] DriveChain = ["overdrive", "delay"];

    /// <summary>Draws half the library as one chain shape and half as another, so
    /// "which family did retrieval pick" is a question with a visible answer. The
    /// real sampler draws both and much more; this is the same shape, small enough
    /// to reason about exhaustively.</summary>
    private sealed class TwoShapeSampler : IRigSampler
    {
        private int _n;

        public DcRigPreset Sample(Random rng, RigSamplingProfile profile)
        {
            var chain = _n++ % 2 == 0 ? EqChain : DriveChain;
            var modules = chain
                .Select(t => new DcRigModule(t, new Dictionary<string, float>(StringComparer.Ordinal)))
                .ToList();
            var layout = KnobLayout.For(new DcRigPreset(modules));
            var v = new float[layout.Count];
            for (var i = 0; i < v.Length; i++) v[i] = (float)rng.NextDouble();
            return layout.Decode(v);
        }
    }

    private sealed record Fixture(
        RigLibrary Library,
        FakeRigRenderer Renderer,
        FakeSimilarity Similarity,
        MonoAudio Di,
        MonoAudio Target,
        ToneFeatures TargetFeatures);

    /// <summary>A library of 24 rigs — exactly the default pool
    /// (<c>RetrievalTopK</c> 4 × <c>PriorRetrievalPoolFactor</c> 6) — so "was the row
    /// in the pool" is never the reason a steering assertion passes or fails. The
    /// target is a render of library row 0, an <c>eq</c> rig, which is what makes
    /// the unbiased top-k all-<c>eq</c> (asserted, not assumed).</summary>
    private static async Task<Fixture> BuildAsync()
    {
        var di = SearchFakes.Di(1.2);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var sim = new FakeSimilarity(extractor);
        var lib = await RigLibrary.BuildAsync(
            di, new TwoShapeSampler(), renderer, extractor,
            new RigLibraryOptions(Count: 24, Seed: 909), null, CancellationToken.None);

        var target = await renderer.RenderAsync(di, lib.Entries[0].Rig, CancellationToken.None);
        return new Fixture(lib, renderer, sim, di, target, extractor.Extract(target));
    }

    private static ToneMatcher Matcher(Fixture f, ToneMatcherOptions options)
        => new(f.Renderer, f.Similarity, new FixedRigLibraryProvider(f.Library), FakeLocalization.English(), null, options);

    private static ToneMatchRequest Request(Fixture f, ToneMatchPrior? prior, int candidates = 32)
        => new(f.Target, f.Di, prior, candidates);

    private static IReadOnlyList<string> Chain(DcRigPreset rig)
        => rig.Modules.Select(m => m.ModuleTypeId).ToList();

    private static ToneMatchPrior DrivePrior()
        => new(
            new DcRigPreset(DriveChain
                .Select(t => new DcRigModule(t, new Dictionary<string, float>(StringComparer.Ordinal)))
                .ToList()),
            "ai:claude");

    // ---- (1) retrieval -------------------------------------------------------

    /// <summary>
    ///     THE steering test. The target is an <c>eq</c> render, so pure feature
    ///     distance shortlists <c>eq</c> rows (asserted as a precondition). Hand the
    ///     matcher a prior whose chain is <c>overdrive → delay</c> and the shortlist
    ///     changes families — the rows it pulls in are marked
    ///     <c>retrieval:prior-seeded</c>, which is the visible evidence that the
    ///     model's knowledge, not the audio alone, chose what to render.
    /// </summary>
    [Fact]
    public async Task APriorPullsItsOwnFamiliesIntoTheShortlist()
    {
        var f = await BuildAsync();
        var options = new ToneMatcherOptions(
            RetrievalTopK: 4, RefineCandidates: 0, MaxRenders: 64, PriorFamilyWeight: 1.0);

        // Precondition: without a prior, retrieval is all eq.
        var plain = f.Library.Query(f.TargetFeatures, 4, f.Similarity);
        Assert.All(plain, hit => Assert.Equal(EqChain, Chain(hit.Rig)));

        var result = await Matcher(f, options).MatchAsync(
            Request(f, DrivePrior()), null, CancellationToken.None);

        var seeded = result.Candidates
            .Where(c => c.Origin == ToneMatchOrigins.PriorSeededRetrieval)
            .ToList();
        Assert.NotEmpty(seeded);
        Assert.All(seeded, c => Assert.Equal(DriveChain, Chain(c.Rig)));
        Assert.Contains(result.Notes, n => n.Contains("prior steered retrieval", StringComparison.Ordinal));
    }

    /// <summary>The bias is on RANKING only: a shortlisted row is still rendered and
    /// scored on audio, so the shortlist never grows and the render budget is the
    /// same one a pure-audio match spends.</summary>
    [Fact]
    public async Task SteeringDoesNotGrowTheShortlistOrTheRenderCount()
    {
        var f = await BuildAsync();
        var options = new ToneMatcherOptions(
            RetrievalTopK: 4, RefineCandidates: 0, MaxRenders: 64, PriorFamilyWeight: 1.0);

        var before = f.Renderer.Renders;
        await Matcher(f, options).MatchAsync(Request(f, null), null, CancellationToken.None);
        var withoutPrior = f.Renderer.Renders - before;

        before = f.Renderer.Renders;
        await Matcher(f, options).MatchAsync(Request(f, DrivePrior()), null, CancellationToken.None);
        var withPrior = f.Renderer.Renders - before;

        // One extra render, and exactly one: the prior's own candidate. Both totals
        // include the scale's 0 % anchor render (W5b), which is taken once per run
        // whether or not there is a prior — so the DIFFERENCE is what this pins.
        Assert.Equal(5, withoutPrior);
        Assert.Equal(6, withPrior);
        Assert.Equal(1, withPrior - withoutPrior);
    }

    /// <summary><c>PriorRetrievalPoolFactor</c> 1 turns steering off without turning
    /// the prior off — the prior is still candidate zero and still seeds CMA-ES.
    /// A knob that claims to disable one mechanism must disable exactly that
    /// one.</summary>
    [Fact]
    public async Task PoolFactorOne_DisablesSteeringButKeepsThePriorAsACandidate()
    {
        var f = await BuildAsync();
        var options = new ToneMatcherOptions(
            RetrievalTopK: 4, RefineCandidates: 0, MaxRenders: 64,
            PriorFamilyWeight: 1.0, PriorRetrievalPoolFactor: 1);

        var result = await Matcher(f, options).MatchAsync(
            Request(f, DrivePrior()), null, CancellationToken.None);

        Assert.DoesNotContain(result.Candidates, c => c.Origin == ToneMatchOrigins.PriorSeededRetrieval);
        Assert.Contains(result.Candidates, c => ToneMatchOrigins.IsPriorRig(c.Origin));
    }

    // ---- (2) CMA-ES ----------------------------------------------------------

    /// <summary>
    ///     The prior's own candidate is refined FROM ITS OWN KNOB VECTOR: the
    ///     optimiser starts where the model put it and improves on it, which is what
    ///     "knowledge before measurement" means once the audio arrives. The prior
    ///     here has the right CHAIN and deliberately wrong KNOBS — the case the whole
    ///     lane exists for.
    /// </summary>
    [Fact]
    public async Task ThePriorsOwnCandidateIsRefinedFromItsOwnKnobs()
    {
        var f = await BuildAsync();
        var hidden = f.Library.Entries[0].Rig;

        // Same chain as the target, knobs pushed far off — a plausible guess.
        var layout = KnobLayout.For(hidden);
        var v = layout.Encode();
        for (var i = 0; i < v.Length; i++) v[i] = Math.Clamp(v[i] > 0.5f ? v[i] - 0.35f : v[i] + 0.35f, 0f, 1f);
        var prior = new ToneMatchPrior(layout.Decode(v), "ai:claude");

        var result = await Matcher(f, new ToneMatcherOptions(
                RetrievalTopK: 2, RefineCandidates: 3, MaxRenders: 300))
            .MatchAsync(Request(f, prior), null, CancellationToken.None);

        var refined = result.Candidates.SingleOrDefault(
            c => ToneMatchOrigins.IsPriorRig(c.Origin) && ToneMatchOrigins.WasRefined(c.Origin));
        Assert.NotNull(refined);
        Assert.Equal("prior:ai:claude+cmaes", refined!.Origin);

        var unrefined = result.Candidates.Single(c => c.Origin == "prior:ai:claude");
        Assert.True(refined.Distance < unrefined.Distance,
            $"refinement made the prior worse: {refined.Distance} vs {unrefined.Distance}");
    }

    /// <summary>A guess deserves more exploration than a measurement: the prior's
    /// refinement runs at <c>PriorInitialSigma</c>, and the run says so in its notes
    /// rather than leaving it to be inferred from the answer.</summary>
    [Fact]
    public async Task ThePriorIsRefinedWithTheWiderSigma_AndSaysSo()
    {
        var f = await BuildAsync();
        var options = new ToneMatcherOptions(
            RetrievalTopK: 2, RefineCandidates: 3, MaxRenders: 120,
            InitialSigma: 0.10, PriorInitialSigma: 0.55);

        var result = await Matcher(f, options).MatchAsync(
            Request(f, new ToneMatchPrior(f.Library.Entries[2].Rig, "ai:claude")),
            null, CancellationToken.None);

        Assert.Contains(result.Notes, n => n.Contains("seeded from the prior (sigma 0.55)", StringComparison.Ordinal));
        Assert.True(new ToneMatcherOptions().PriorInitialSigma > new ToneMatcherOptions().InitialSigma);
    }

    // ---- (3) the pure-audio path is untouched --------------------------------

    /// <summary>
    ///     The pin on "a null prior changes nothing". With no prior the shortlist is
    ///     the plain top-k of the library, in the library's own ranking, every
    ///     candidate origin is <c>retrieval</c>, and nothing in the result mentions a
    ///     prior. Break the guard in <c>ToneMatcher.RetrievalSeeds</c> — drop the
    ///     <c>prior is null</c> arm — and this fails on the origins.
    /// </summary>
    [Fact]
    public async Task NoPrior_ShortlistIsThePlainTopK()
    {
        var f = await BuildAsync();
        var options = new ToneMatcherOptions(RetrievalTopK: 4, RefineCandidates: 0, MaxRenders: 64);

        var result = await Matcher(f, options).MatchAsync(Request(f, null), null, CancellationToken.None);

        var expected = f.Library.Query(f.TargetFeatures, 4, f.Similarity)
            .Select(h => h.Rig)
            .ToHashSet();
        Assert.Equal(4, result.Candidates.Count);
        Assert.All(result.Candidates, c => Assert.Equal(ToneMatchOrigins.Retrieval, c.Origin));
        Assert.All(result.Candidates, c => Assert.Contains(c.Rig, expected));
        Assert.DoesNotContain(result.Notes, n => n.Contains("prior", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The floor and the note format a UI reads are unchanged by any of
    /// this — a regression here would be silent, because every number still looks
    /// like a number.</summary>
    [Fact]
    public async Task NoPrior_StillReportsTheSelfDistanceFloorAndTheRenderCount()
    {
        var f = await BuildAsync();
        var result = await Matcher(f, new ToneMatcherOptions(
                RetrievalTopK: 3, RefineCandidates: 0, MaxRenders: 16,
                SelfDistanceWindow: TimeSpan.FromSeconds(0.4)))
            .MatchAsync(Request(f, null), null, CancellationToken.None);

        Assert.True(result.TargetSelfDistanceFloor > 0.0);
        // Three seeds plus the scale's one anchor render (W5b).
        Assert.Contains(result.Notes, n => n.Contains("4 renders in", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.StartsWith("scale:", StringComparison.Ordinal));
    }
}
