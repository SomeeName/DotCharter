using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     <see cref="ToneMatchRequest.TargetIsRender" />: the raw-audio term is carried
///     only when the caller rendered the target itself.
///     <para>
///         The similarity here returns <b>zero for everything</b>, so whatever is
///         left in a reported distance IS the raw-audio term. That is what makes the
///         two cases distinguishable at all — with a real metric the term is 15 % of
///         a number that moves for a dozen other reasons, and a test over it would
///         assert nothing.
///     </para>
/// </summary>
public sealed class ToneMatcherTargetKindTests
{
    private sealed class ZeroSimilarity(IToneFeatureExtractor extractor) : IToneSimilarity
    {
        public IToneFeatureExtractor Extractor { get; } = extractor;
        public double Distance(ToneFeatures a, ToneFeatures b) => 0.0;
    }

    private static async Task<ToneMatchResult> RunAsync(bool targetIsRender)
    {
        var di = SearchFakes.Di(1.2);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var library = await RigLibrary.BuildAsync(
            di, new FakeRigSampler("eq"), renderer, extractor,
            new RigLibraryOptions(Count: 4, Seed: 99), null, TestContext.Current.CancellationToken);

        var matcher = new ToneMatcher(
            renderer, new ZeroSimilarity(extractor), new FixedRigLibraryProvider(library), FakeLocalization.English(), null,
            new ToneMatcherOptions(
                RetrievalTopK: 2, RefineCandidates: 0, MaxRenders: 4,
                SelfDistanceWindow: TimeSpan.FromSeconds(0.5),
                UsePlausibility: false));

        // A DIFFERENT take for the target, which is the whole point: a stem is not a
        // render of the caller's DI, and the multi-scale loss is sample-aligned.
        var target = await renderer.RenderAsync(SearchFakes.Di(1.2, seed: 31), new DcRigPreset([
            new DcRigModule("eq", new Dictionary<string, float>(StringComparer.Ordinal))
        ]), TestContext.Current.CancellationToken);

        return await matcher.MatchAsync(
            new ToneMatchRequest(target, di, null, Candidates: 8) { TargetIsRender = targetIsRender },
            null, TestContext.Current.CancellationToken);
    }

    /// <summary>The default — and the case the panel, the set matcher and the corpus
    /// eval are all in.</summary>
    [Fact]
    public async Task AStemTarget_CarriesNoRawAudioTerm()
    {
        var result = await RunAsync(targetIsRender: false);

        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, c => Assert.Equal(0.0, c.Distance, 9));
        Assert.Equal(0.0, result.TargetSelfDistanceFloor, 9);
    }

    /// <summary>…and the opt-in still carries it, or the flag would be a way to
    /// delete the term rather than to place it correctly.</summary>
    [Fact]
    public async Task ARenderTarget_StillCarriesIt()
    {
        var result = await RunAsync(targetIsRender: true);

        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, c => Assert.True(c.Distance > 0.0,
            $"the raw-audio term vanished for a declared render: {c.Distance:E3}"));
        Assert.True(result.TargetSelfDistanceFloor > 0.0,
            "the floor must carry the term on exactly the runs a candidate does, or the two are not " +
            "in the same space and the UI's percentage is against a different scale.");
    }

    /// <summary><see cref="ToneMatchRequest" /> is an ADDITIVE contract: the
    /// positional constructor must keep meaning what it meant, and the default must
    /// be the stem case — the one every production caller is in.</summary>
    [Fact]
    public void TheDefault_IsTheStemCase()
    {
        var silence = new MonoAudio(new float[16], 16_000);
        Assert.False(new ToneMatchRequest(silence, silence, null).TargetIsRender);
    }
}
