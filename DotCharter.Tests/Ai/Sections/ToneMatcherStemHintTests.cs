using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tests.Ai.Search;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     <c>ToneMatchRequest.StemHint</c>: the sections lane's one change to the search
///     lane.
///     <para>
///         <b>The bias adds seeds; it never removes one.</b> A hint is knowledge about
///         the INSTRUMENT — the user picked "bass" in a dropdown — and not a
///         measurement of the tone, so it must not be able to discard the library row
///         that actually measured closest. The control case is the important one: with
///         no hint, nothing about the matcher changes, which is what lets this ship
///         without re-validating every existing search result.
///     </para>
/// </summary>
public sealed class ToneMatcherStemHintTests
{
    /// <summary>Alternates a bass-family chain with a guitar one, so a library built
    /// from it has a known population of each.</summary>
    private sealed class MixedSampler : IRigSampler
    {
        private int _n;

        public DcRigPreset Sample(Random rng, RigSamplingProfile profile)
        {
            var bass = _n++ % 2 == 0;
            var chain = bass ? new[] { "gain", "bassamp" } : ["gain", "overdrive"];
            return new FakeRigSampler(chain).Sample(rng, profile);
        }
    }

    private static bool IsBass(DcRigPreset rig) =>
        rig.Modules.Any(m => m.ModuleTypeId is "bassamp" or "bassod");

    private static async Task<(ToneMatcher Matcher, MonoAudio Di)> BuildAsync()
    {
        var di = SearchFakes.Di(1.2);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var similarity = new FakeSimilarity(extractor);
        var library = await RigLibrary.BuildAsync(
            di, new MixedSampler(), renderer, extractor,
            new RigLibraryOptions(Count: 48, Seed: 4242), null, TestContext.Current.CancellationToken);

        return (new ToneMatcher(
            renderer, similarity, new FixedRigLibraryProvider(library), FakeLocalization.English(), estimator: null,
            new ToneMatcherOptions(
                MaxRenders: 500, RefineCandidates: 0, SelfDistanceWindow: TimeSpan.FromSeconds(0.5))), di);
    }

    private static ToneMatchRequest Request(MonoAudio di, string? hint) =>
        new(di, di, Prior: null, Candidates: 40) { StemHint = hint };

    [Fact]
    public async Task ABassHintAddsBassFamilySeeds()
    {
        var (matcher, di) = await BuildAsync();

        var result = await matcher.MatchAsync(
            Request(di, ToneStems.Bass), null, TestContext.Current.CancellationToken);

        var seeded = result.Candidates.Where(c => c.Origin == "retrieval:bass").ToList();
        Assert.NotEmpty(seeded);
        Assert.All(seeded, c => Assert.True(IsBass(c.Rig)));
        Assert.Contains(result.Notes, n => n.StartsWith("bass hint:", StringComparison.Ordinal));
    }

    /// <summary>
    ///     …and the ordinary retrieval rows are all still there. If the hint had
    ///     filtered instead of added, this is the assertion that would go red.
    /// </summary>
    [Fact]
    public async Task ABassHintDoesNotRemoveTheOrdinaryRetrievalRows()
    {
        var (matcher, di) = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;

        var plain = await matcher.MatchAsync(Request(di, null), null, ct);
        var hinted = await matcher.MatchAsync(Request(di, ToneStems.Bass), null, ct);

        var plainRetrieval = plain.Candidates.Count(c => c.Origin == "retrieval");
        var hintedRetrieval = hinted.Candidates.Count(c => c.Origin == "retrieval");
        Assert.Equal(plainRetrieval, hintedRetrieval);
        Assert.True(hinted.Candidates.Count > plain.Candidates.Count);
    }

    [Fact]
    public async Task NoHintChangesNothing()
    {
        var (matcher, di) = await BuildAsync();
        var ct = TestContext.Current.CancellationToken;

        var none = await matcher.MatchAsync(Request(di, null), null, ct);
        var guitar = await matcher.MatchAsync(Request(di, ToneStems.Guitar), null, ct);

        Assert.DoesNotContain(none.Candidates, c => c.Origin.Contains("bass", StringComparison.Ordinal));
        Assert.DoesNotContain(guitar.Candidates, c => c.Origin.Contains("bass", StringComparison.Ordinal));
        Assert.DoesNotContain(none.Notes, n => n.StartsWith("bass hint:", StringComparison.Ordinal));
        Assert.Equal(
            none.Candidates.Select(c => (c.Origin, Math.Round(c.Distance, 9))),
            guitar.Candidates.Select(c => (c.Origin, Math.Round(c.Distance, 9))));
    }
}
