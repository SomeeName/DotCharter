using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     What the distance must HEAR, in the order the match lane depends on it.
///     <para>
///         <b>Every assertion here is a RATIO, never an absolute threshold.</b> The
///         numbers this metric produces move whenever a weight is retuned, and a suite
///         pinned to "distance &lt; 0.42" would either fail on every tuning change or be
///         loosened until it asserted nothing. What must not move is the ORDER and the
///         MARGIN: a different rig is worth much more than a different riff, and if
///         that inequality ever inverts the search lane will confidently return the
///         wrong tone.
///     </para>
/// </summary>
public sealed class ToneSimilarityTests
{
    private static readonly ToneSimilarity Similarity = new();

    private static ToneFeatures Features(MonoAudio audio) => Similarity.Extractor.Extract(audio);

    [Fact]
    public void SameClip_IsExactlyZero()
    {
        var clip = ManagedToneStages.Fuzz(ReferenceDi.Generate());
        Assert.Equal(0.0, Similarity.Distance(Features(clip), Features(clip)));
    }

    /// <summary>
    ///     The headline property, and the one the whole feature rests on: the same
    ///     rig on two DIFFERENT performances must be much closer than two different
    ///     rigs on the same performance. The target of a real match is a stem of a
    ///     riff nobody will ever play into the app, so if this ratio is not
    ///     comfortably above 1 the metric is measuring the notes.
    /// </summary>
    [Fact]
    public void ContentInvariance_ADifferentRiffCostsFarLessThanADifferentRig()
    {
        var riffA = ReferenceDi.Generate();
        var riffB = ReferenceDi.Generate(7);

        var sameRigDifferentRiff = Similarity.Distance(
            Features(ManagedToneStages.Fuzz(riffA)),
            Features(ManagedToneStages.Fuzz(riffB)));

        var differentRigSameRiff = Similarity.Distance(
            Features(ManagedToneStages.Fuzz(riffA)),
            Features(ManagedToneStages.Clean(riffA)));

        // Measured 2026-09-12 on these stimuli: 1.56 for tonefx-v2. The bar is BELOW that on
        // purpose — the number worth defending is that a rig change costs clearly more
        // than a riff change, and it is nowhere near the order of magnitude a trained
        // timbre embedding would give. That gap is a real limitation of a hand-built
        // metric and it is written down in docs/agents/testing/audio-eval.md rather
        // than hidden behind a comfortable threshold.
        var ratio = differentRigSameRiff / sameRigDifferentRiff;
        Assert.True(ratio > 1.35,
            $"a rig change is only {ratio:F2}x a riff change (rig {differentRigSameRiff:F4}, " +
            $"riff {sameRigDifferentRiff:F4}). The metric is hearing the notes, and a search over it " +
            "will return whichever rig happens to suit the reference DI's phrase.");
    }

    /// <summary>
    ///     …and the same inequality stated the other way round, which is the form the
    ///     search lane actually relies on: the fuzz rendered over a different riff
    ///     must still be the nearest neighbour of the fuzz, not the clean amp on the
    ///     same riff.
    /// </summary>
    [Fact]
    public void NearestNeighbour_IsTheRigAndNotTheRiff()
    {
        var riffA = ReferenceDi.Generate();
        var riffB = ReferenceDi.Generate(11);
        var target = Features(ManagedToneStages.Fuzz(riffA));

        var sameRig = Similarity.Distance(target, Features(ManagedToneStages.Fuzz(riffB)));
        var sameRiff = Similarity.Distance(target, Features(ManagedToneStages.Clean(riffA)));

        Assert.True(sameRig < sameRiff,
            $"the fuzz on another riff ({sameRig:F4}) is further from the target than a clean amp on " +
            $"the SAME riff ({sameRiff:F4}) — retrieval over this metric returns the riff.");
    }

    /// <summary>
    ///     A 3 dB level change is a real difference and a small one. Zero would mean
    ///     the metric cannot tell a hot rig from a quiet one (level is part of a tone
    ///     answer); a large value would mean every candidate is graded on gain, which
    ///     is the easiest thing in the chain to fix afterwards.
    /// </summary>
    [Fact]
    public void ThreeDecibels_IsSmallButNotZero()
    {
        var clip = ManagedToneStages.Clean(ReferenceDi.Generate());
        var louder = ManagedToneStages.Gain(clip, 3.0);

        var levelDistance = Similarity.Distance(Features(clip), Features(louder));
        var toneDistance = Similarity.Distance(
            Features(clip), Features(ManagedToneStages.Fuzz(ReferenceDi.Generate())));

        Assert.True(levelDistance > 0, "a 3 dB gain change registered as no difference at all.");
        Assert.True(levelDistance < toneDistance / 5,
            $"3 dB of gain cost {levelDistance:F4} against a tone change of {toneDistance:F4} — level " +
            "is dominating the metric.");
    }

    /// <summary>
    ///     <b>A delay must be audible to the metric.</b> The search lane found this the
    ///     hard way: it reported a confident match, far below the floor, to a rig with
    ///     the wrong delay entirely — because a signal plus a delayed copy of itself
    ///     has almost the same long-term spectrum as the signal, and every block except
    ///     the modulation one is an average over frames.
    ///     <para>
    ///         The comparison here is deliberately ADVERSARIAL: the delayed clip is
    ///         level-matched, so the only evidence is the envelope's periodicity.
    ///     </para>
    /// </summary>
    [Fact]
    public void ADelay_IsAudibleToTheMetric()
    {
        var dry = ManagedToneStages.Clean(ReferenceDi.Generate());
        var delayed = ManagedToneStages.Delay(dry, TimeSpan.FromMilliseconds(380), 0.45);

        var distance = Similarity.Distance(Features(dry), Features(delayed));
        var modulationShare = Similarity
            .Explain(Features(dry), Features(delayed))
            .Single(p => p.Block == "modulation").Contribution / (distance * distance);

        var gain = Similarity.Distance(
            Features(dry), Features(ManagedToneStages.Gain(dry, 3.0)));

        Assert.True(distance > gain * 2,
            $"a 380 ms delay at 45 % feedback moved the vector by {distance:F4}, no more than a level " +
            $"change ({gain:F4}) — the metric cannot hear a delay and a search over it will put the " +
            "feedback anywhere.");
        Assert.True(modulationShare > 0.1,
            $"only {modulationShare:P0} of the delay's distance came from the modulation block; the " +
            "block that exists to hear a delay is not the one hearing it.");
    }

    /// <summary>
    ///     …and the same test's non-vacuity: two delays with DIFFERENT times must be
    ///     further apart than either is from itself. A block that merely detects "some
    ///     periodicity" would pass the test above and still leave the time
    ///     unidentifiable.
    /// </summary>
    [Fact]
    public void TwoDelayTimes_AreDistinguishable()
    {
        var dry = ManagedToneStages.Clean(ReferenceDi.Generate());
        var shortDelay = ManagedToneStages.Delay(dry, TimeSpan.FromMilliseconds(120), 0.45);
        var longDelay = ManagedToneStages.Delay(dry, TimeSpan.FromMilliseconds(600), 0.45);

        var between = Similarity.Distance(Features(shortDelay), Features(longDelay));
        var same = Similarity.Distance(
            Features(shortDelay), Features(ManagedToneStages.Delay(dry, TimeSpan.FromMilliseconds(120), 0.45)));

        Assert.Equal(0.0, same, 9);
        Assert.True(between > 0.02,
            $"a 120 ms delay and a 600 ms one are {between:F4} apart — the delay TIME is not identifiable.");
    }

    [Fact]
    public void VectorsFromDifferentExtractors_AreRefusedRatherThanCompared()
    {
        var mine = Features(ReferenceDi.Generate());
        var alien = new ToneFeatures(mine.Vector, "tonefx-v99");

        var ex = Assert.Throws<ArgumentException>(() => Similarity.Distance(mine, alien));
        Assert.Contains("tonefx-v99", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The block breakdown must add up to the distance it explains, or a report
    ///     that says "most of the error is in the drive" is decoration.
    /// </summary>
    [Fact]
    public void Explain_SumsToTheSquaredDistance()
    {
        var a = Features(ManagedToneStages.Clean(ReferenceDi.Generate()));
        var b = Features(ManagedToneStages.Fuzz(ReferenceDi.Generate(3)));

        var distance = Similarity.Distance(a, b);
        var sum = Similarity.Explain(a, b).Sum(p => p.Contribution);

        Assert.Equal(distance * distance, sum, 9);
    }

    /// <summary>
    ///     The raw-audio loss is a different animal and its test says so: it must
    ///     separate two rigs on the same take, and it must NOT be used across takes —
    ///     which it demonstrates by rating the same rig on a different riff as worse
    ///     than a different rig on the same riff. That is not a bug; it is the reason
    ///     <see cref="MultiScaleSpectralDistance" /> is documented as refinement-only.
    /// </summary>
    [Fact]
    public void MultiScaleSpectral_SeparatesRigsOnOneTakeAndIsMeaninglessAcrossTakes()
    {
        var loss = new MultiScaleSpectralDistance();
        var riffA = ReferenceDi.Generate();
        var riffB = ReferenceDi.Generate(5);

        var clean = ManagedToneStages.Clean(riffA);
        Assert.Equal(0.0, loss.Distance(clean, clean), 9);

        var sameTake = loss.Distance(clean, ManagedToneStages.Fuzz(riffA));
        var acrossTakes = loss.Distance(clean, ManagedToneStages.Clean(riffB));

        Assert.True(sameTake > 0, "the loss could not tell a clean amp from a fuzz on the same take.");
        Assert.True(acrossTakes > sameTake,
            $"the same rig on another riff scored {acrossTakes:F3}, BETTER than a different rig on the " +
            $"same riff ({sameTake:F3}) — if that ever holds, this loss would be safe across takes and " +
            "the documentation saying it is not would need rewriting.");
    }
}
