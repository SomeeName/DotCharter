using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The plausibility prior: a board a player would have built scores 0, and the
///     exact chains the first end-to-end eval gamed the metric with score enough to
///     lose the ranking they won.
///     <para>
///         <b>Every expectation here is stated against a REAL measured pair</b> —
///         <c>octaver→klon→tapesat</c> at 0.4909 against the authored
///         <c>optocomp→amp→cab→eq→chorus→springreverb→gain</c> at 0.6953
///         (2026-09-12) — rather than against a threshold chosen to be passable.
///     </para>
/// </summary>
public sealed class RigPlausibilityTests
{
    private static DcRigPreset Rig(params string[] typeIds) =>
        new([.. typeIds.Select(id => new DcRigModule(id, new Dictionary<string, float>(StringComparer.Ordinal)))]);

    /// <summary>The four authored Rocksmith rigs the eval's <c>rs-truth</c> candidate
    /// used. The prior must not mark down the ground truth — a penalty that punished
    /// the professional's board would be the same mistake in the other
    /// direction.</summary>
    public static TheoryData<string[]> AuthoredRigs()
    {
        var data = new TheoryData<string[]>
        {
            (["optocomp", "amp", "cab", "eq", "chorus", "springreverb", "gain"]),
            (["octfuzz", "graphiceq", "amp", "cab", "eq", "platereverb", "gain"]),
            (["optocomp", "amp", "cab", "eq", "hallreverb", "gain"]),
            (["acousticsim", "optocomp", "graphiceq", "amp", "cab", "eq", "gain"]),
            (["overdrive", "amp", "cab", "delay"])
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(AuthoredRigs))]
    public void ABoardAPlayerBuilt_ScoresAtMostTheDuplicateFamilyCap(string[] chain)
    {
        var penalty = RigPlausibility.Penalty(Rig(chain), ToneStems.Guitar);

        // Two of these carry a `graphiceq` AND an `eq`, which the sampler would not
        // draw — that is the ONLY fault the ground truth is allowed to have, and it
        // is capped low on purpose.
        Assert.True(penalty <= RigPlausibility.DuplicateFamilyCap,
            $"an authored rig ({string.Join("→", chain)}) was marked down by {penalty:0.00}: " +
            string.Join(", ", RigPlausibility.Explain(Rig(chain), ToneStems.Guitar)
                .Select(f => $"{f.Code} {f.Weight:0.00}")));
    }

    /// <summary>
    ///     The headline case, stated as the ARITHMETIC that has to hold rather than as
    ///     a bare number: at the matcher's default weight the gamed chain's penalised
    ///     distance must exceed the authored rig's unpenalised one.
    /// </summary>
    [Fact]
    public void TheGamedChain_LosesTheRankingItWonInTheFirstEval()
    {
        var gamed = Rig("octaver", "klon", "tapesat");
        var authored = Rig("optocomp", "amp", "cab", "eq", "chorus", "springreverb", "gain");
        const double weight = 0.6; // ToneMatcherOptions.PlausibilityWeight's default.

        const double gamedDistance = 0.4909;   // measured 2026-09-12
        const double authoredDistance = 0.6953;

        var gamedRanked = gamedDistance * (1 + weight * RigPlausibility.Penalty(gamed, ToneStems.Guitar));
        var authoredRanked = authoredDistance * (1 + weight * RigPlausibility.Penalty(authored, ToneStems.Guitar));

        Assert.True(gamedRanked > authoredRanked,
            $"octaver→klon→tapesat still ranks ahead of the authored rig: {gamedRanked:0.0000} vs " +
            $"{authoredRanked:0.0000}. The penalty is {RigPlausibility.Penalty(gamed, ToneStems.Guitar):0.00}; " +
            "it needs to be worth more than the 1.42x distance gap the eval measured.");
    }

    [Fact]
    public void NoAmpAndNoCab_IsTheHeaviestFault()
    {
        var faults = RigPlausibility.Explain(Rig("octaver", "klon", "tapesat"), ToneStems.Guitar);

        Assert.Contains(faults, f => f.Code == "no-amp-no-cab");
        Assert.Equal(RigPlausibility.NoAmpNoCabWeight, RigPlausibility.Penalty(Rig("octaver", "klon", "tapesat")), 6);
    }

    /// <summary>A cab sim with no amp is a REAL way to record — the sampler draws it
    /// on purpose — so it must cost much less than no cabinet at all.</summary>
    [Fact]
    public void ACabWithNoAmp_CostsFarLessThanNoCabAtAll()
    {
        var withCab = RigPlausibility.Penalty(Rig("klon", "cab"), ToneStems.Guitar);
        var without = RigPlausibility.Penalty(Rig("klon", "tapesat"), ToneStems.Guitar);

        Assert.True(withCab > 0, "a cab with no amp is unusual and must not score zero.");
        Assert.True(withCab < without / 2,
            $"no-amp-with-cab ({withCab:0.00}) is not clearly cheaper than no-amp-no-cab ({without:0.00}).");
    }

    [Fact]
    public void ASpeakerAmpWithNoCabinet_IsMarkedDown_ButAPreampIsNot()
    {
        Assert.True(RigPlausibility.Penalty(Rig("overdrive", "amp")) > 0,
            "a speaker amp with no cabinet is the most recognisable 'never plugged in' sound there is.");

        // `acpre` and `tubepre` are SPEAKER-LESS preamps — the sampler's DiTypeAmps —
        // so a rig that ends at one is complete.
        Assert.Equal(0.0, RigPlausibility.Penalty(Rig("optocomp", "acpre", "reverb")), 6);
    }

    [Fact]
    public void ABassPart_WantsBassGearAndNotAGuitarAmp()
    {
        var guitarRig = RigPlausibility.Penalty(Rig("overdrive", "amp", "cab"), ToneStems.Bass);
        var bassRig = RigPlausibility.Penalty(Rig("compressor", "bassamp", "cab"), ToneStems.Bass);

        Assert.Equal(0.0, bassRig, 6);
        Assert.True(guitarRig >= RigPlausibility.GuitarAmpOnBassWeight,
            $"a guitar amp on a bass part scored only {guitarRig:0.00}.");
    }

    /// <summary>The same guitar chain on a PIANO stem is scored on structure alone:
    /// a piano through a rig carries no amp expectation, and inventing one would
    /// make every keyboard section's answer a guitar board.</summary>
    [Fact]
    public void APianoStem_IsScoredOnStructureAlone()
    {
        Assert.Equal(0.0, RigPlausibility.Penalty(Rig("octaver", "klon", "tapesat"), ToneStems.Piano), 6);
        Assert.True(RigPlausibility.Penalty(Rig("amp", "amp", "cab"), ToneStems.Piano) > 0,
            "two amplifiers is a structural fault on any stem.");
    }

    [Theory]
    // Shapes the sampler cannot produce, one per rule.
    [InlineData("two-amps", new[] { "amp", "bassamp", "cab" })]
    [InlineData("mic-without-cab", new[] { "amp", "micpos" })]
    [InlineData("cab-before-amp", new[] { "cab", "amp" })]
    [InlineData("unsamplable", new[] { "amp", "cab", "looper" })]
    [InlineData("over-length", new[]
    {
        "compressor", "overdrive", "amp", "cab", "eq", "chorus", "delay", "reverb", "gain"
    })]
    public void EachShapeTheSamplerCannotDraw_NamesItsOwnFault(string code, string[] chain)
    {
        var faults = RigPlausibility.Explain(Rig(chain), ToneStems.Guitar);

        Assert.Contains(faults, f => f.Code == code);
        Assert.True(RigPlausibility.Penalty(Rig(chain), ToneStems.Guitar) > 0);
    }

    private static (int Clean, Dictionary<string, int> Codes) Draw(string kind, int n, int seed)
    {
        var sampler = new RigSampler();
        var rng = new Random(seed);
        var profile = new RigSamplingProfile { Kind = kind };
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var clean = 0;
        for (var i = 0; i < n; i++)
        {
            // No stem hint: the library is drawn from the MIXED archetype, so a bass
            // draw must not be judged as a guitar part.
            var rig = sampler.Sample(rng, profile);
            if (RigPlausibility.Penalty(rig) <= 0.0) clean++;
            foreach (var f in RigPlausibility.Explain(rig))
                codes[f.Code] = codes.GetValueOrDefault(f.Code) + 1;
        }

        return (clean, codes);
    }

    /// <summary>
    ///     <b>The derivation lint.</b> Five of the faults restate rules the sampler
    ///     enforces ABSOLUTELY — never two amps, never a mic without a cab, never a
    ///     cab in front of its amp, never a module outside the vocabulary, never a
    ///     chain past the length prior's support. If the prior and the sampler ever
    ///     disagreed about one of those, this fails; a thousand draws and not one may
    ///     trip.
    ///     <para>
    ///         Non-vacuous by construction: <see cref="EachShapeTheSamplerCannotDraw_NamesItsOwnFault" />
    ///         shows every one of these codes firing on a hand-built chain.
    ///     </para>
    /// </summary>
    [Fact]
    public void NoSampledRig_EverTripsAStructuralFault()
    {
        string[] structural = ["two-amps", "mic-without-cab", "cab-before-amp", "unsamplable", "over-length"];
        var (_, codes) = Draw(RigProfileKinds.Mixed, 1000, 20260912);

        var tripped = structural.Where(c => codes.ContainsKey(c)).Select(c => $"{c} × {codes[c]}").ToList();
        Assert.True(tripped.Count == 0,
            "the prior and the sampler disagree about a rule the sampler enforces absolutely: " +
            string.Join(", ", tripped));
    }

    /// <summary>
    ///     <b>The amp/cab penalty tracks the ARCHETYPE, which is the whole claim.</b>
    ///     It is not a rejection of the library: a <c>crunch</c> draw is almost always
    ///     clean, because that archetype puts an amp and a cab in ~85 % of its rigs.
    ///     A <c>direct</c> draw is almost always marked down, because that archetype
    ///     is pedals-to-the-desk by definition — and a pedals-to-the-desk rig IS the
    ///     wrong answer for a played guitar part unless it is measurably much closer.
    ///     <para>
    ///         Measured 2026-09-12 with these seeds: crunch 84 %, highgain 88 %,
    ///         clean 79 %, bass 71 %, ambient 60 %, direct 14 % clean; over the shipping
    ///         <c>mixed</c> profile, 540 of 1,000 draws score zero and the remaining
    ///         460 are 280 no-amp-no-cab, 102 no-cab, 75 no-amp, 12 duplicate-family.
    ///         Re-measure rather than carrying those forward.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheAmpAndCabPenalty_TracksTheArchetypeRatherThanRejectingTheLibrary()
    {
        const int n = 400;
        var crunch = Draw(RigProfileKinds.Crunch, n, 11).Clean / (double)n;
        var highGain = Draw(RigProfileKinds.HighGain, n, 12).Clean / (double)n;
        var direct = Draw(RigProfileKinds.Direct, n, 13).Clean / (double)n;

        Assert.True(crunch > 0.70, $"only {crunch:P0} of `crunch` draws are plausible — the prior is " +
                                   "marking down the archetype that most obviously builds a real board.");
        Assert.True(highGain > 0.70, $"only {highGain:P0} of `highgain` draws are plausible.");
        Assert.True(direct < 0.40,
            $"{direct:P0} of `direct` draws score zero — the prior is not charging for the amp-less " +
            "archetype at all, which is the one the search kept winning with.");
    }
}
