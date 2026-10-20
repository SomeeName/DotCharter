using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The scale's own rules: the formula's fixed points, and the five ways it
///     refuses to produce a number. <see cref="MatchScoreCalibrationTests" /> is the
///     other half — these are synthetic vectors, those are real ones.
/// </summary>
public sealed class MatchScaleTests
{
    private const string Id = ToneFeatureExtractor.ExtractorId;

    /// <summary>
    ///     A vector whose every element is <c>Base + offset</c>, so a distance is
    ///     arithmetic rather than a measurement: the metric is an L2 over the
    ///     per-element differences, so twice the offset is twice the distance.
    ///     <para>
    ///         <b><see cref="Base" /> is non-zero on purpose.</b> An all-zero vector is
    ///         the DEGENERATE one the extractor answers for silence, and the scale
    ///         refuses to rate a target in that state — a fixture built on
    ///         <c>Flat(0)</c> would come back <c>block-gated</c> and every case here
    ///         would be asserting the guard instead of the formula.
    ///     </para>
    /// </summary>
    private const float Base = 0.2f;

    private static ToneFeatures Flat(float offset)
    {
        var x = new float[ToneFeatureLayout.Length];
        for (var i = 0; i < x.Length; i++) x[i] = Base + offset;
        return new ToneFeatures(x, Id);
    }

    private static readonly ToneSimilarity Metric = new();

    [Fact]
    public void ACandidateIdenticalToTheTarget_IsOneHundred()
    {
        var target = Flat(0.0f);
        var score = MatchScale.Score(target, Flat(0.0f), Flat(1.0f), Metric, selfDistance: 0.2);

        Assert.Equal(100, score.Percent);
        Assert.Equal(0.0, score.Distance, 9);
        Assert.True(score.IsRated);
    }

    [Fact]
    public void ACandidateAtTheReferenceRigsDistance_IsZero()
    {
        var target = Flat(0.0f);
        var reference = Flat(1.0f);

        Assert.Equal(0, MatchScale.Score(target, reference, reference, Metric, 0.2).Percent);
    }

    [Fact]
    public void ACandidateWorseThanTheReference_ClampsToZero_RatherThanGoingNegative()
    {
        var score = MatchScale.Score(Flat(0.0f), Flat(4.0f), Flat(1.0f), Metric, 0.2);

        Assert.Equal(0, score.Percent);
        Assert.True(score.Distance > score.ReferenceDistance);
    }

    [Fact]
    public void HalfwayToTheReference_IsFifty() =>
        // The formula in one line: 1 - d/ref. A flat 0.5 vector against a flat 0 target
        // is exactly half the distance of a flat 1.0 one.
        Assert.Equal(50, MatchScale.Score(Flat(0.0f), Flat(0.5f), Flat(1.0f), Metric, 0.2).Percent);

    // ------------------------------------------------------------------- unrated

    [Fact]
    public void WithNoReferenceRender_ItIsUnrated_AndNotZero()
    {
        var score = MatchScale.Score(Flat(0.0f), Flat(0.5f), null, Metric, 0.2);

        Assert.Null(score.Percent);
        Assert.Equal(MatchUnrated.NoReference, score.UnratedReason);
        Assert.False(score.IsRated);
    }

    [Fact]
    public void WhenTheReferenceRigIsAlreadyOnTopOfTheTarget_ItIsUnrated()
    {
        // The scale would be a point: every candidate rounds to the same number.
        var score = MatchScale.Score(Flat(0.0f), Flat(0.0001f), Flat(0.0001f), Metric, 0.2);

        Assert.Equal(MatchUnrated.ReferenceTooClose, score.UnratedReason);
    }

    [Fact]
    public void ATargetTooShortToMeasure_IsUnrated()
    {
        var score = MatchScale.Score(Flat(0.0f), Flat(0.5f), Flat(1.0f), Metric, 0.2, targetTooShort: true);

        Assert.Equal(MatchUnrated.TargetTooShort, score.UnratedReason);
    }

    [Fact]
    public void AnAllGatedTargetBlock_IsUnrated_AndTheBlockSaysSo()
    {
        // The degenerate vector the extractor answers for silence: every element
        // outside the level pair is exactly zero. It produces a perfectly comparable
        // distance about nothing, which is the W5a failure one level up.
        var silent = new float[ToneFeatureLayout.Length];
        silent[ToneFeatureLayout.LevelOffset] = -2f;
        var score = MatchScale.Score(
            new ToneFeatures(silent, Id), Flat(0.5f), Flat(1.0f), Metric, 0.2);

        Assert.Equal(MatchUnrated.BlockGated, score.UnratedReason);
        Assert.Contains(score.Blocks, b => b.TargetGated && b.Name == "bandMean");
        Assert.DoesNotContain(score.Blocks, b => b.TargetGated && b.Name == "level");
    }

    [Fact]
    public void ARealTarget_HasNoGatedBlock()
    {
        // The guard against the guard: if a legitimate target could trip the gated
        // rule, every real match would come back unrated and every case above would
        // still pass. Real feature vectors from the committed fixture.
        var path = Path.Combine(
            AppContext.BaseDirectory, "Ai", "Search", "Fixtures", "polkadot-sakasma-90s-guitar.json");
        var artifact = DotCharter.Services.Ai.Match.Search.MatchArtifact.TryRead(path);
        Assert.NotNull(artifact);

        var score = MatchScale.Score(
            new ToneFeatures(artifact!.TargetFeatures!, artifact.ExtractorId),
            new ToneFeatures(artifact.Candidates[0].Features!, artifact.ExtractorId),
            new ToneFeatures(artifact.ReferenceFeatures!, artifact.ExtractorId),
            Metric, artifact.SelfDistance);

        Assert.DoesNotContain(score.Blocks, b => b.TargetGated);
        Assert.True(score.IsRated);
    }

    // ------------------------------------------------------------- the caveat

    [Fact]
    public void WithNoSelfDistance_ThereIsStillAScore_ButNoCaveat()
    {
        // W5b: a missing self-distance used to mean a meaningless percentage. It is
        // no longer part of the scale, so the score stands and only the caveat is
        // absent.
        var score = MatchScale.Score(Flat(0.0f), Flat(0.5f), Flat(1.0f), Metric, double.NaN);

        Assert.Equal(50, score.Percent);
        Assert.Null(score.SelfDistancePercent);
        Assert.False(score.WithinTargetNoise);
    }

    [Fact]
    public void ACandidateInsideTheTargetsOwnVariation_IsFlagged()
    {
        // self 0.5 of the reference distance => the caveat sits at 50 %.
        var score = MatchScale.Score(Flat(0.0f), Flat(0.25f), Flat(1.0f), Metric, selfDistance: 0.5 * 1.0 * Metric.Distance(Flat(0.0f), Flat(1.0f)));

        Assert.Equal(50, score.SelfDistancePercent);
        Assert.Equal(75, score.Percent);
        Assert.True(score.WithinTargetNoise);
    }

    // --------------------------------------------------------------- breakdown

    [Fact]
    public void TheBreakdownSumsToOne_AndCarriesTheReferencesOwnContribution()
    {
        var score = MatchScale.Score(Flat(0.0f), Flat(0.5f), Flat(1.0f), Metric, 0.2);

        Assert.Equal(ToneFeatureLayout.Blocks.Length, score.Blocks.Count);
        Assert.InRange(score.Blocks.Sum(b => b.Share), 0.999, 1.001);
        Assert.All(score.Blocks, b => Assert.False(double.IsNaN(b.ReferenceContribution)));
        // Every block is four times better than the reference here (half the delta,
        // squared), so nothing is worse than it.
        Assert.Empty(score.BlocksWorseThanReference());
    }

    [Fact]
    public void TheBreakdownIsTheSameSumAsTheMetric()
    {
        // One implementation: ToneSimilarity.Explain and the score's blocks must never
        // be two different arithmetics, or a headline and its "why" disagree.
        var a = Flat(0.1f);
        var b = Flat(0.7f);
        var explained = Metric.Explain(a, b);
        var blocks = MatchScale.Score(a, b, Flat(1.0f), Metric, 0.2).Blocks;

        for (var i = 0; i < blocks.Count; i++)
        {
            Assert.Equal(explained[i].Block, blocks[i].Name);
            Assert.Equal(explained[i].Contribution, blocks[i].Contribution, 12);
        }

        Assert.Equal(Math.Sqrt(explained.Sum(p => p.Contribution)), Metric.Distance(a, b), 9);
    }

    [Fact]
    public void TheReferenceRigIsTheInformationFreeChain()
    {
        // Named, so a change to it is a deliberate change to the 0 % anchor rather
        // than an accident inside a refactor.
        Assert.Equal(
            new[] { "overdrive", "amp", "cab" },
            MatchScale.ReferenceRig().Modules.Select(m => m.ModuleTypeId).ToArray());
        Assert.All(MatchScale.ReferenceRig().Modules, m => Assert.Empty(m.Params));
    }
}
