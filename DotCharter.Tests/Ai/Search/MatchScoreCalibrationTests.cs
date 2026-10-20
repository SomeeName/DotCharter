using System.Globalization;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     <b>The score, calibrated at both ends, against real feature vectors.</b>
///     <para>
///         Both fixtures under <c>Ai/Search/Fixtures/</c> are real
///         <see cref="MatchResultStore" /> artefacts captured from the REAL pipeline
///         (the device-less JUCE engine, <c>ToneFeatureExtractor</c>, the shipped
///         4,000-row retrieval library) and then committed. They carry the target's,
///         the reference rig's and every candidate's FEATURE VECTOR, so this class
///         re-runs <see cref="MatchScale.Score" /> from scratch in the default gate —
///         no audio, no engine, milliseconds. Compact one-line JSON: they are data,
///         not something to read by eye.
///     </para>
///     <list type="bullet">
///         <item>
///             <b><c>polkadot-sakasma-90s-guitar.json</c> — the NEGATIVE case.</b> The
///             owner's own track, 90–120 s of the separated guitar (the span the
///             chart's tone-change-free fallback picks from a playhead inside the
///             guitar part), matched by the real matcher. The tones it returned were
///             plainly wrong and the OLD formula reported 100 % for them.
///         </item>
///         <item>
///             <b><c>known-rig-render.json</c> — the POSITIVE case.</b> A target that
///             IS a render of a known rig through this pipeline, scored against that
///             same rig, against a deliberately wrong rig (<c>acpre</c> alone) and
///             against the reference rig itself.
///         </item>
///     </list>
/// </summary>
public sealed class MatchScoreCalibrationTests
{
    /// <summary>Nothing the search returned on the owner's track may read as a good
    /// match. 50 is generous on purpose — the point is that the old formula said
    /// 100 for the same vectors, not that 46 is the "right" number.</summary>
    private const int WrongTonesCeiling = 50;

    private static MatchArtifact Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Ai", "Search", "Fixtures", name);
        var artifact = MatchArtifact.TryRead(path);
        Assert.True(artifact is not null, "fixture not deployed: " + path);
        Assert.Equal(ToneFeatureExtractor.ExtractorId, artifact!.ExtractorId);
        Assert.NotNull(artifact.TargetFeatures);
        Assert.NotNull(artifact.ReferenceFeatures);
        return artifact;
    }

    private static (ToneFeatures Target, ToneFeatures Reference, ToneSimilarity Similarity) Anchors(
        MatchArtifact a) => (
        new ToneFeatures(a.TargetFeatures!, a.ExtractorId),
        new ToneFeatures(a.ReferenceFeatures!, a.ExtractorId),
        new ToneSimilarity());

    private static MatchScore Rescore(MatchArtifact a, MatchArtifactCandidate c)
    {
        var (target, reference, similarity) = Anchors(a);
        Assert.NotNull(c.Features);
        return MatchScale.Score(
            target, new ToneFeatures(c.Features!, a.ExtractorId), reference, similarity, a.SelfDistance);
    }

    // ---------------------------------------------------------- the NEGATIVE end

    [Fact]
    public void ThePolkadotMatch_ScoresLow_OnEveryCandidateItReturned()
    {
        var artifact = Load("polkadot-sakasma-90s-guitar.json");
        Assert.NotEmpty(artifact.Candidates);

        var report = new List<string>();
        foreach (var candidate in artifact.Candidates)
        {
            var score = Rescore(artifact, candidate);
            report.Add(string.Create(CultureInfo.InvariantCulture,
                $"{candidate.Origin} d={score.Distance:0.0000} -> {score.Percent}%"));
            Assert.True(score.IsRated, $"{candidate.Origin}: unrated ({score.UnratedReason})");
            Assert.True(score.Percent <= WrongTonesCeiling,
                $"{candidate.Origin} scored {score.Percent} %, over the {WrongTonesCeiling} % ceiling for a " +
                $"match the owner reported as plainly wrong. All: {string.Join(", ", report)}");
        }
    }

    [Fact]
    public void TheOldFormula_WouldHaveReportedFullMarks_ForThoseSameVectors()
    {
        // The regression this class exists for, in one assertion: `100 x floor /
        // max(d, floor)` saturates whenever a candidate's distance is below the
        // target's self-distance, and on this real target two of the five are.
        var artifact = Load("polkadot-sakasma-90s-guitar.json");
        var floor = artifact.SelfDistance;

        var saturated = artifact.Candidates.Count(c => c.Distance <= floor);
        Assert.True(saturated >= 2,
            $"only {saturated} of {artifact.Candidates.Count} candidates sit below the self-distance " +
            $"{floor:0.0000} — this fixture no longer demonstrates the bug it was captured for");

        foreach (var candidate in artifact.Candidates.Where(c => c.Distance <= floor))
        {
            var old = (int)Math.Round(Math.Clamp(100.0 * floor / Math.Max(candidate.Distance, floor), 0, 100));
            Assert.Equal(100, old);
            Assert.True(Rescore(artifact, candidate).Percent <= WrongTonesCeiling);
        }
    }

    [Fact]
    public void ThePolkadotBreakdown_NamesTheBlockTheCandidateLostOn()
    {
        // No headline number without the breakdown behind it: the worst block has to
        // be identified, and the reference rig's own contribution has to be there to
        // read it against.
        var artifact = Load("polkadot-sakasma-90s-guitar.json");
        var score = Rescore(artifact, artifact.Candidates[0]);

        var worst = score.WorstBlocks(3);
        Assert.Equal(3, worst.Count);
        Assert.All(worst, b => Assert.False(double.IsNaN(b.ReferenceContribution)));
        Assert.Equal("dynamics", worst[0].Name);
        Assert.InRange(score.Blocks.Sum(b => b.Share), 0.999, 1.001);
    }

    [Fact]
    public void OnThePolkadotTarget_TheSelfDistanceIsReportedAsTheCaveatItIs()
    {
        // The old 100 % anchor, now demoted to a reported caveat: the top candidate
        // is INSIDE the target's own variation, which is exactly why the old formula
        // saturated — and saying so is different from scoring it 100.
        var artifact = Load("polkadot-sakasma-90s-guitar.json");
        var score = Rescore(artifact, artifact.Candidates[0]);

        Assert.NotNull(score.SelfDistancePercent);
        Assert.True(score.WithinTargetNoise,
            $"the top candidate ({score.Percent} %) is not flagged as inside the target's own variation " +
            $"({score.SelfDistancePercent} %)");
        Assert.True(score.Percent < 100);
    }

    // ---------------------------------------------------------- the POSITIVE end

    [Fact]
    public void AKnownRig_AgainstItsOwnRender_ScoresFullMarks()
    {
        var artifact = Load("known-rig-render.json");
        var exact = artifact.Candidates.Single(c => c.Origin == "known-rig-exact");

        var score = Rescore(artifact, exact);

        Assert.Equal(100, score.Percent);
        Assert.Equal(0.0, score.Distance, 6);
    }

    [Fact]
    public void ADeliberatelyWrongRig_ScoresZero_OnTheSameTarget()
    {
        var artifact = Load("known-rig-render.json");
        var wrong = artifact.Candidates.Single(c => c.Origin == "deliberately-wrong-acpre");

        var score = Rescore(artifact, wrong);

        Assert.Equal(0, score.Percent);
        Assert.True(score.Distance > score.ReferenceDistance,
            $"the wrong rig ({score.Distance:0.0000}) is closer than the information-free reference " +
            $"({score.ReferenceDistance:0.0000}) — it is not a wrong-enough fixture");
    }

    [Fact]
    public void TheReferenceRig_ScoresExactlyZero_ByConstruction()
    {
        // The cheapest non-vacuity check there is: 0 % IS the reference rig, so a
        // scorer that drifted off its own anchor fails here first.
        var artifact = Load("known-rig-render.json");
        var reference = artifact.Candidates.Single(c => c.Origin == "reference-rig");

        Assert.Equal(0, Rescore(artifact, reference).Percent);
    }

    [Fact]
    public void OnTheKnownRigTarget_TheSelfDistanceRunsBACKWARDS()
    {
        // Why the self-distance could not be rescued as the 100 % anchor: on this
        // target the information-free rig is more than twice as close as the target's
        // own two windows are to each other, because those windows differ in CONTENT.
        // A scale with that as its good end is inverted.
        var artifact = Load("known-rig-render.json");

        Assert.True(artifact.SelfDistance > artifact.ReferenceDistance,
            $"self {artifact.SelfDistance:0.0000} vs reference {artifact.ReferenceDistance:0.0000} — " +
            "this fixture no longer demonstrates the inversion it was captured for");
    }

    // ------------------------------------------------------------ reproducibility

    [Fact]
    public void TheSameVectors_ScoreIdentically_EveryTime()
    {
        // The score is a pure function of three vectors, so it cannot depend on the
        // render budget, the wall clock or the CMA-ES seed.
        foreach (var name in new[] { "polkadot-sakasma-90s-guitar.json", "known-rig-render.json" })
        {
            var artifact = Load(name);
            foreach (var candidate in artifact.Candidates)
            {
                var a = Rescore(artifact, candidate);
                var b = Rescore(artifact, candidate);
                Assert.Equal(a.Percent, b.Percent);
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Distance), BitConverter.DoubleToInt64Bits(b.Distance));
                Assert.Equal(
                    a.Blocks.Select(x => BitConverter.DoubleToInt64Bits(x.Contribution)),
                    b.Blocks.Select(x => BitConverter.DoubleToInt64Bits(x.Contribution)));
            }
        }
    }

    [Fact]
    public void TheScoreDoesNotDependOnHowManyCandidatesTheRunAfforded()
    {
        // The budget decides WHICH candidates exist; it may not move what a given one
        // scores. Re-scoring one candidate on its own must equal its score inside the
        // full set.
        var artifact = Load("polkadot-sakasma-90s-guitar.json");
        var (target, reference, similarity) = Anchors(artifact);

        foreach (var candidate in artifact.Candidates)
        {
            var alone = MatchScale.Score(
                target, new ToneFeatures(candidate.Features!, artifact.ExtractorId), reference, similarity,
                artifact.SelfDistance);
            Assert.Equal(candidate.Percent, alone.Percent);
        }
    }

    [Fact]
    public void ThePersistedPercentages_MatchWhatTheScorerProducesToday()
    {
        // The fixtures carry the percentages the run reported. If the scorer changes,
        // this is the case that says so — and whoever changes it re-captures the
        // fixtures and re-states the calibration numbers, rather than editing a
        // threshold until the build goes green.
        foreach (var name in new[] { "polkadot-sakasma-90s-guitar.json", "known-rig-render.json" })
        {
            var artifact = Load(name);
            foreach (var candidate in artifact.Candidates)
                Assert.Equal(candidate.Percent, Rescore(artifact, candidate).Percent);
        }
    }
}
