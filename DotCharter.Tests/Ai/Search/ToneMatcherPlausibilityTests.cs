using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     <see cref="RigPlausibility" /> folded into <see cref="ToneMatcher" />'s
///     ranking: the arithmetic, the option, and the one property the whole change
///     exists for — a no-amp/no-cab rig cannot take a guitar part from an amp rig it
///     merely TIES with on the metric.
///     <para>
///         The similarity here is CONSTANT on purpose. With every candidate at the
///         same audio distance the only thing that can move the ranking is the
///         plausibility scale, so the test reads the factor off the reported
///         distances instead of hoping a real metric happens to separate them.
///     </para>
/// </summary>
public sealed class ToneMatcherPlausibilityTests
{
    private const double FlatDistance = 1.0;

    /// <summary>Every pair of tones is exactly <see cref="FlatDistance" /> apart, so
    /// the audio cannot break a tie and the ranking is the plausibility scale
    /// alone.</summary>
    private sealed class FlatSimilarity(IToneFeatureExtractor extractor) : IToneSimilarity
    {
        public IToneFeatureExtractor Extractor { get; } = extractor;
        public double Distance(ToneFeatures a, ToneFeatures b) => FlatDistance;
    }

    /// <summary>Alternates two chains: a bare EQ (no amp, no cab) and the same EQ
    /// into an amp and a cab. The fake renderer models neither <c>amp</c> nor
    /// <c>cab</c>, so the two render the same audio — which is the point.</summary>
    private sealed class TwoChainSampler : IRigSampler
    {
        private int _n;

        public DcRigPreset Sample(Random rng, RigSamplingProfile profile)
        {
            var chain = _n++ % 2 == 0 ? new[] { "eq" } : ["eq", "amp", "cab"];
            return new DcRigPreset([
                .. chain.Select(id => new DcRigModule(id, new Dictionary<string, float>(StringComparer.Ordinal)))
            ]);
        }
    }

    private static async Task<ToneMatchResult> RunAsync(bool usePlausibility)
    {
        var di = SearchFakes.Di(1.2);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var library = await RigLibrary.BuildAsync(
            di, new TwoChainSampler(), renderer, extractor,
            new RigLibraryOptions(Count: 2, Seed: 1), null, TestContext.Current.CancellationToken);

        var matcher = new ToneMatcher(
            renderer, new FlatSimilarity(extractor), new FixedRigLibraryProvider(library), FakeLocalization.English(), null,
            new ToneMatcherOptions(
                RetrievalTopK: 2,
                RefineCandidates: 0,
                MaxRenders: 8,
                SpectralWeight: 0.0,
                SelfDistanceWindow: TimeSpan.FromSeconds(0.5),
                UsePlausibility: usePlausibility));

        var target = await renderer.RenderAsync(di, new DcRigPreset([
            new DcRigModule("eq", new Dictionary<string, float>(StringComparer.Ordinal))
        ]), TestContext.Current.CancellationToken);

        return await matcher.MatchAsync(
            new ToneMatchRequest(target, di, null, Candidates: 8) { StemHint = ToneStems.Guitar },
            null, TestContext.Current.CancellationToken);
    }

    private static bool HasAmp(DcRigPreset rig) => rig.Modules.Any(m => m.ModuleTypeId == "amp");

    [Fact]
    public async Task AtTheSameDistance_TheAmpRigWins()
    {
        var result = await RunAsync(usePlausibility: true);

        Assert.Equal(2, result.Candidates.Count);
        Assert.True(HasAmp(result.Candidates[0].Rig),
            "a bare EQ took a guitar part from an identically-sounding amp rig: " +
            string.Join(" / ", result.Candidates.Select(c =>
                $"{string.Join("→", c.Rig.Modules.Select(m => m.ModuleTypeId))} {c.RankingDistance:0.000}")));
    }

    /// <summary>
    ///     The factor itself, read off the reported numbers — and the SPLIT that goes
    ///     with it: the prior moves <see cref="ToneMatchCandidate.RankingDistance" />
    ///     and never <see cref="ToneMatchCandidate.Distance" />, which is the number
    ///     that shares a scale with the self-distance floor.
    ///     <para>
    ///         This is what makes the test non-vacuous: drop the scale from
    ///         <c>Candidate</c> and both ranking numbers become 1.0.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheRankingDistance_IsMeasuredTimesOnePlusWeightTimesPenalty()
    {
        var result = await RunAsync(usePlausibility: true);

        var amped = result.Candidates.Single(c => HasAmp(c.Rig));
        var bare = result.Candidates.Single(c => !HasAmp(c.Rig));

        Assert.Equal(FlatDistance, amped.Distance, 6);
        Assert.Equal(FlatDistance, amped.RankingDistance, 6);
        Assert.Equal(0.0, amped.PlausibilityPenalty, 6);

        // The MEASUREMENT is untouched — the bare EQ really does sound the same.
        Assert.Equal(FlatDistance, bare.Distance, 6);
        // 0.6 (the default weight) × 0.85 (no-amp-no-cab) = 1.51×.
        Assert.Equal(RigPlausibility.NoAmpNoCabWeight, bare.PlausibilityPenalty, 6);
        Assert.Equal(FlatDistance * (1 + 0.6 * RigPlausibility.NoAmpNoCabWeight), bare.RankingDistance, 6);
    }

    [Fact]
    public async Task TheNote_SaysWhichCandidatesWereMarkedDownAndWhy()
    {
        var result = await RunAsync(usePlausibility: true);

        Assert.Contains(result.Notes, n =>
            n.Contains("plausibility: 1 of 2", StringComparison.Ordinal) &&
            n.Contains("no-amp-no-cab", StringComparison.Ordinal));
    }

    /// <summary>Switched off, the ranking is byte-for-byte the pre-2026-09-12 one —
    /// which is what an A/B of the change is run with, and what keeps this an option
    /// rather than a silent rewrite of everyone's results.</summary>
    [Fact]
    public async Task SwitchedOff_NothingIsScaledAndNoNoteIsAdded()
    {
        var result = await RunAsync(usePlausibility: false);

        Assert.All(result.Candidates, c =>
        {
            Assert.Equal(FlatDistance, c.Distance, 6);
            Assert.Equal(FlatDistance, c.RankingDistance, 6);
            Assert.Equal(0.0, c.PlausibilityPenalty, 6);
        });
        Assert.DoesNotContain(result.Notes, n => n.Contains("plausibility", StringComparison.Ordinal));
    }
}
