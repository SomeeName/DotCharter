using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     Non-vacuity tests for <see cref="ToneDesignScorer" />. Every one of them
///     pins a number the scorer must produce for a chain whose relationship to the
///     truth is known by construction — a scorer nobody has watched disagree is a
///     scorer nobody should trust, and this one is the whole measuring instrument
///     for the feature.
///     <para>Pure and offline: the default gate runs all of it.</para>
/// </summary>
public sealed class ToneDesignScorerTests
{
    // ------------------------------------------------------------------ weights

    [Fact]
    public void Weights_SumToOne() =>
        // Total is documented as a percentage. If these stop summing to 1 it
        // silently stops being one, and every historical number becomes a lie.
        Assert.Equal(1.0, ToneScoreWeights.Sum, 9);

    // ------------------------------------------------------- the anchors: 100, 0

    [Fact]
    public void TruthAgainstItself_ScoresExactlyOneHundred()
    {
        foreach (var entry in ToneEvalCorpus.Fixtures)
        {
            var score = ToneDesignScorer.Score(entry.Truth, entry.Truth);
            Assert.Equal(100.0, score.Total, 9);
            Assert.Equal(1.0, score.FamilyScore, 9);
            Assert.Equal(1.0, score.ModuleScore, 9);
            Assert.Equal(1.0, score.KnobScore, 9);
            Assert.Equal(1.0, score.OrderScore, 9);
        }
    }

    [Fact]
    public void AnEmptyChain_ScoresBelowTen()
    {
        var empty = new DcRigPreset(Array.Empty<DcRigModule>());
        foreach (var entry in ToneEvalCorpus.Fixtures)
        {
            var score = ToneDesignScorer.Score(empty, entry.Truth);
            Assert.True(score.Total < 10.0,
                $"an empty chain scored {score.Total:F1} against {entry.SongTitle}/{entry.ToneName}; " +
                "an empty rig must not collect a default component anywhere.");
        }
    }

    [Fact]
    public void AChainOfNothingButBypassedModules_IsTheEmptyChain()
    {
        // Canonicalize drops bypassed modules: ten pedals with ten off is not a tone.
        var truth = ToneEvalCorpus.Fixtures[0].Truth;
        var allOff = new DcRigPreset(truth.Modules.Select(m => m with { Bypassed = true }).ToList());

        Assert.True(ToneDesignScorer.Score(allOff, truth).Total < 10.0);
    }

    // ------------------------------------------------- right families, wrong knobs

    [Fact]
    public void RightModulesEveryKnobAtTheOppositeEnd_ScoresSeventyFive()
    {
        // Built so every component is known exactly: same single module (family 1,
        // module 1, order 1 with one common family) and every param at the far end
        // of its declared range (knob 0). 0.40 + 0.25 + 0 + 0.10 = 0.75.
        var truth = AtEnd(DcRigTargetCatalog.Overdrive, atMax: false);
        var designed = AtEnd(DcRigTargetCatalog.Overdrive, atMax: true);

        var score = ToneDesignScorer.Score(designed, truth);

        Assert.Equal(1.0, score.FamilyScore, 9);
        Assert.Equal(1.0, score.ModuleScore, 9);
        Assert.Equal(0.0, score.KnobScore, 9);
        Assert.Equal(1.0, score.OrderScore, 9);
        Assert.Equal(75.0, score.Total, 9);
        Assert.True(score.ComparedParams > 0, "no param was compared — the knob 0 is vacuous.");
    }

    [Fact]
    public void RightFamiliesWrongKnobs_LandsStrictlyBetweenTheAnchors()
    {
        foreach (var entry in ToneEvalCorpus.Fixtures)
        {
            var mirrored = MirrorEveryKnob(ToneDesignScorer.Canonicalize(entry.Truth));
            var score = ToneDesignScorer.Score(mirrored, entry.Truth);

            Assert.True(score.Total is > 10.0 and < 100.0,
                $"{entry.SongTitle}/{entry.ToneName} mirrored scored {score.Total:F1}; " +
                "a right-shaped chain with wrong knobs must be neither the empty " +
                "chain nor a perfect answer.");
            Assert.Equal(1.0, score.FamilyScore, 9);
            Assert.True(score.KnobScore < 1.0);
        }
    }

    // --------------------------------------------------------------- knob gradient

    [Fact]
    public void AKnobTenPercentOff_ScoresHigherThanOneFiftyPercentOff()
    {
        var truth = AtEnd(DcRigTargetCatalog.Overdrive, atMax: false);
        var close = AtFraction(DcRigTargetCatalog.Overdrive, 0.10f);
        var far = AtFraction(DcRigTargetCatalog.Overdrive, 0.50f);

        var closeScore = ToneDesignScorer.Score(close, truth);
        var farScore = ToneDesignScorer.Score(far, truth);

        Assert.Equal(0.90, closeScore.KnobScore, 6);
        Assert.Equal(0.50, farScore.KnobScore, 6);
        Assert.True(closeScore.Total > farScore.Total,
            $"10 % off scored {closeScore.Total:F2}, 50 % off scored {farScore.Total:F2} — " +
            "the knob component is not monotone in the error.");
    }

    // ------------------------------------------------------------- wrong families

    [Fact]
    public void SwappingTheDriveForAModulation_LowersFamilyAndModule()
    {
        var truth = Chain(DcRigTargetCatalog.Overdrive, DcRigTargetCatalog.Amp, DcRigTargetCatalog.Cab);
        var swapped = Chain(DcRigTargetCatalog.Phaser, DcRigTargetCatalog.Amp, DcRigTargetCatalog.Cab);

        var baseline = ToneDesignScorer.Score(truth, truth);
        var score = ToneDesignScorer.Score(swapped, truth);

        Assert.True(score.FamilyScore < baseline.FamilyScore,
            $"family stayed at {score.FamilyScore:F2} after a drive became a phaser.");
        Assert.True(score.ModuleScore < baseline.ModuleScore,
            $"module stayed at {score.ModuleScore:F2} after a drive became a phaser.");
        Assert.True(score.Total < baseline.Total);

        Assert.Contains(ToneFamily.Drive, score.TruthFamilies);
        Assert.DoesNotContain(ToneFamily.Drive, score.DesignedFamilies);
        Assert.Contains(ToneFamily.Modulation, score.DesignedFamilies);
    }

    [Fact]
    public void TheOrderComponent_SeesAReversedChain()
    {
        var truth = Chain(DcRigTargetCatalog.Overdrive, DcRigTargetCatalog.Amp, DcRigTargetCatalog.Cab);
        var reversed = Chain(DcRigTargetCatalog.Cab, DcRigTargetCatalog.Amp, DcRigTargetCatalog.Overdrive);

        var score = ToneDesignScorer.Score(reversed, truth);

        // Same modules, same knobs (all catalogue defaults) — only the order moved.
        Assert.Equal(1.0, score.FamilyScore, 9);
        Assert.Equal(1.0, score.ModuleScore, 9);
        Assert.Equal(0.0, score.OrderScore, 9);
        Assert.Equal(90.0, score.Total, 9);
    }

    // -------------------------------------------------------------- canonical form

    [Fact]
    public void TheLoudnessNormalizersTrailingGain_IsNotAFreeDynamicsFamily()
    {
        // Every converted RS tone ends in one. A designed chain that does not
        // invent a master volume must not be marked down for it.
        var withMaster = new DcRigPreset(new[]
        {
            new DcRigModule(DcRigTargetCatalog.Amp, Empty()),
            new DcRigModule(DcRigTargetCatalog.Gain, new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["gainDb"] = -6f
            })
        });

        var canonical = ToneDesignScorer.Canonicalize(withMaster);

        Assert.Equal(new[] { DcRigTargetCatalog.Amp }, canonical.Modules.Select(m => m.ModuleTypeId));
        Assert.DoesNotContain(ToneFamily.Dynamics,
            ToneDesignScorer.FamilySequence(canonical.Modules.Select(m => m.ModuleTypeId)));
    }

    [Fact]
    public void AMidChainBoostGain_Survives()
    {
        // Only the LAST module counts as the master — the normalizer's own rule.
        var rig = new DcRigPreset(new[]
        {
            new DcRigModule(DcRigTargetCatalog.Gain, Empty()),
            new DcRigModule(DcRigTargetCatalog.Amp, Empty())
        });

        Assert.Contains(DcRigTargetCatalog.Gain,
            ToneDesignScorer.Canonicalize(rig).Modules.Select(m => m.ModuleTypeId));
    }

    [Fact]
    public void EveryConvertedFixture_CarriesANonEmptyCanonicalChain()
    {
        foreach (var entry in ToneEvalCorpus.Fixtures)
            Assert.NotEmpty(ToneDesignScorer.Canonicalize(entry.Truth).Modules);
    }

    // ------------------------------------------------------------------- families

    [Theory]
    [InlineData("overdrive", ToneFamily.Drive)]
    [InlineData("bigmuff", ToneFamily.Drive)]
    [InlineData("amp", ToneFamily.Amp)]
    [InlineData("cab", ToneFamily.Cab)]
    [InlineData("compressor", ToneFamily.Dynamics)]
    [InlineData("chorus", ToneFamily.Modulation)]
    [InlineData("phaser", ToneFamily.Modulation)]
    [InlineData("delay", ToneFamily.Delay)]
    [InlineData("tapedelay", ToneFamily.Delay)]
    [InlineData("reversedelay", ToneFamily.Delay)] // does NOT contain "reverb"
    [InlineData("reverb", ToneFamily.Reverb)]
    [InlineData("platereverb", ToneFamily.Reverb)]
    [InlineData("shimmer", ToneFamily.Reverb)]
    [InlineData("octaver", ToneFamily.Pitch)]
    [InlineData("wah", ToneFamily.Filter)]
    [InlineData("eq", ToneFamily.Eq)]
    [InlineData("splitter", ToneFamily.Routing)]
    public void FamilyOf_MapsTheCatalogue(string moduleTypeId, ToneFamily expected)
        => Assert.Equal(expected, ToneFamilies.Of(moduleTypeId));

    [Fact]
    public void DelayAndReverb_AreDifferentFamilies() =>
        // The one refinement this harness makes over DspModuleVisuals' Time bucket.
        // If it ever collapses back, a slapback and a hall score identically.
        Assert.NotEqual(ToneFamilies.Of("delay"), ToneFamilies.Of("reverb"));

    [Fact]
    public void EveryCatalogueModuleId_HasAFamily()
    {
        var unmapped = DcRigTargetCatalog.ModuleTypeIds
            .Where(id => ToneFamilies.Of(id) == ToneFamily.Utility)
            .ToList();

        Assert.True(unmapped.Count == 0,
            "these catalogue ids fall through to Utility and so score nothing: " +
            string.Join(", ", unmapped));
    }

    [Fact]
    public void RoutingAndUtility_AreOutOfTheFamilyScore()
    {
        Assert.False(ToneFamilies.IsScored(ToneFamily.Routing));
        Assert.False(ToneFamilies.IsScored(ToneFamily.Utility));
        Assert.Equal(ToneFamilies.Scored.Count, ToneFamilies.Scored.Distinct().Count());
        Assert.All(ToneFamilies.Scored, f => Assert.True(ToneFamilies.IsScored(f)));
    }

    // -------------------------------------------------------------------- helpers

    private static Dictionary<string, float> Empty() => new(StringComparer.Ordinal);

    private static DcRigPreset Chain(params string[] moduleTypeIds)
        => new(moduleTypeIds.Select(id => new DcRigModule(id, Empty())).ToList());

    private static DcRigPreset AtEnd(string moduleTypeId, bool atMax)
        => Single(moduleTypeId, spec => atMax ? spec.Max : spec.Min);

    private static DcRigPreset AtFraction(string moduleTypeId, float fraction)
        => Single(moduleTypeId, spec => spec.Min + fraction * (spec.Max - spec.Min));

    private static DcRigPreset Single(
        string moduleTypeId, Func<DcRigTargetCatalog.ParamSpec, float> value)
    {
        var specs = DcRigTargetCatalog.AllModules
            .First(m => m.ModuleTypeId == moduleTypeId).Params;
        var values = Empty();
        foreach (var spec in specs) values[spec.Id] = value(spec);
        return new DcRigPreset(new[] { new DcRigModule(moduleTypeId, values) });
    }

    /// <summary>Every knob pushed to whichever end of its range is FURTHER from
    /// the truth's value, so the knob component collapses toward 0 while the
    /// module set is untouched.</summary>
    private static DcRigPreset MirrorEveryKnob(DcRigPreset preset)
    {
        var modules = new List<DcRigModule>(preset.Modules.Count);
        foreach (var module in preset.Modules)
        {
            var values = Empty();
            foreach (var (id, current) in module.Params)
            {
                if (!DcRigTargetCatalog.TryGetParam(module.ModuleTypeId, id, out var spec))
                {
                    values[id] = current;
                    continue;
                }

                values[id] = current - spec.Min >= spec.Max - current ? spec.Min : spec.Max;
            }

            modules.Add(module with { Params = values });
        }

        return new DcRigPreset(modules);
    }
}
