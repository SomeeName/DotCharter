using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>The bijection between a rig and a point in <c>[0,1]^n</c>. Everything
/// downstream (retrieval start points, CMA-ES, the recovered rig) is wrong in a way
/// that still LOOKS plausible if any of these fail.</summary>
public sealed class KnobVectorTests
{
    private static DcRigPreset Chain(params string[] types)
        => new(types.Select(t => new DcRigModule(t, new Dictionary<string, float>(StringComparer.Ordinal))).ToList());

    [Fact]
    public void RoundTrips_EveryContinuousParam()
    {
        var rig = new DcRigPreset([
            new DcRigModule("overdrive", new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["model"] = 2f, ["drive"] = 0.73f, ["tone"] = 0.21f, ["level"] = 0.44f
            }),
            new DcRigModule("delay", new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["timeMs"] = 412f, ["feedback"] = 0.61f, ["mix"] = 0.28f, ["toneHz"] = 3120f
            })
        ]);

        var layout = KnobLayout.For(rig);
        var back = layout.Decode(layout.Encode(rig));

        foreach (var c in layout.Coordinates)
        {
            var before = rig.Modules[c.ModuleIndex].Params.TryGetValue(c.ParamId, out var v) ? v : float.NaN;
            if (float.IsNaN(before)) continue;
            var after = back.Modules[c.ModuleIndex].Params[c.ParamId];
            // 1e-4 relative: the log coordinate goes through exp/log in float.
            Assert.True(Math.Abs(after - before) <= 1e-4f * Math.Max(1f, Math.Abs(before)),
                $"{c.ModuleTypeId}.{c.ParamId}: {before} -> {after}");
        }
    }

    [Fact]
    public void DiscreteParams_AreNotCoordinates_AndSurviveDecodeUntouched()
    {
        var rig = new DcRigPreset([
            new DcRigModule("overdrive", new Dictionary<string, float>(StringComparer.Ordinal)
            {
                ["model"] = 2f, ["drive"] = 0.5f
            })
        ]);

        var layout = KnobLayout.For(rig);
        Assert.DoesNotContain(layout.Coordinates, c => c.ParamId == "model");

        var moved = layout.Decode(Enumerable.Repeat(1f, layout.Count).ToArray());
        Assert.Equal(2f, moved.Modules[0].Params["model"]);
    }

    [Fact]
    public void NonSearchableIdentity_IsCarriedThrough()
    {
        var rig = new DcRigPreset([
            new DcRigModule("namamp", new Dictionary<string, float>(StringComparer.Ordinal), Bypassed: true)
            {
                StringParams = new Dictionary<string, string>(StringComparer.Ordinal) { ["modelPath"] = @"C:\a.nam" },
                Branch = 2
            }
        ]);

        var layout = KnobLayout.For(rig);
        var back = layout.Decode(new float[layout.Count]);

        Assert.True(back.Modules[0].Bypassed);
        Assert.Equal(2, back.Modules[0].Branch);
        Assert.Equal(@"C:\a.nam", back.Modules[0].StringParams!["modelPath"]);
    }

    [Fact]
    public void Coordinates_AreClampedIntoTheBox()
    {
        var layout = KnobLayout.For(Chain("overdrive"));
        var drive = layout.Coordinates.Single(c => c.ParamId == "drive");

        Assert.Equal(0f, drive.ToUnit(-5f));
        Assert.Equal(1f, drive.ToUnit(5f));
        Assert.Equal(drive.Min, drive.ToValue(-3f));
        Assert.Equal(drive.Max, drive.ToValue(9f));

        var big = layout.Decode(Enumerable.Repeat(42f, layout.Count).ToArray());
        foreach (var c in layout.Coordinates)
        {
            var v = big.Modules[c.ModuleIndex].Params[c.ParamId];
            Assert.InRange(v, c.Min, c.Max);
        }
    }

    [Fact]
    public void FrequencyParams_AreLogScaled_AndBipolarOnesAreNot()
    {
        var layout = KnobLayout.For(Chain("svf", "freqshift"));
        var cutoff = layout.Coordinates.Single(c => c.ParamId == "cutoffHz");
        Assert.True(cutoff.Logarithmic);
        // 20..20,000 Hz: the midpoint of the dial is the GEOMETRIC mean, not 10 kHz.
        Assert.InRange(cutoff.ToValue(0.5f), 600f, 640f);

        var shift = layout.Coordinates.SingleOrDefault(c => c.ParamId == "shiftHz");
        if (shift is not null) Assert.False(shift.Logarithmic); // bipolar: log is undefined
    }

    [Fact]
    public void TaperRule_MatchesTheUiKnobTaper()
    {
        // Services may not reference UI, so KnobParamKinds MIRRORS KnobMath.TaperFor.
        // This pins the two: for every continuous catalogue param, log <=> (loggable
        // && (frequency id || >= 2 decades)) — the exact predicate KnobMath uses.
        foreach (var (typeId, specs) in DcRigTargetCatalog.AllModules)
        {
            foreach (var spec in specs)
            {
                if (KnobParamKinds.IsDiscrete(typeId, spec)) continue;
                var loggable = spec.Min > 0f && spec.Max > spec.Min;
                var isFreq = spec.Id.EndsWith("Hz", StringComparison.Ordinal) ||
                             spec.Id.EndsWith("hz", StringComparison.Ordinal);
                var expected = loggable && (isFreq || Math.Log10(spec.Max / (double)spec.Min) >= 2.0);
                Assert.Equal(expected, KnobParamKinds.IsLogarithmic(spec));
            }
        }
    }

    [Fact]
    public void EveryCuratedDiscreteId_IsARealCatalogueParam()
    {
        // A curated list rots silently. Every id must still name at least one
        // catalogue param that the integral-range rule actually classifies discrete.
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (typeId, specs) in DcRigTargetCatalog.AllModules)
        {
            foreach (var spec in specs)
            {
                if (KnobParamKinds.IsDiscrete(typeId, spec))
                    live.Add(spec.Id);
            }
        }

        var dead = KnobParamKinds.DiscreteIds.Except(live).ToList();
        Assert.True(dead.Count == 0, "Curated discrete ids no longer in the catalogue: " + string.Join(", ", dead));
    }

    [Fact]
    public void UnknownModuleTypes_ContributeNothing_ButAreKept()
    {
        var rig = new DcRigPreset([
            new DcRigModule("not-a-module", new Dictionary<string, float>(StringComparer.Ordinal) { ["x"] = 1f }),
            new DcRigModule("gain", new Dictionary<string, float>(StringComparer.Ordinal))
        ]);

        var layout = KnobLayout.For(rig);
        Assert.All(layout.Coordinates, c => Assert.Equal("gain", c.ModuleTypeId));

        var back = layout.Decode(new float[layout.Count]);
        Assert.Equal(2, back.Modules.Count);
        Assert.Equal(1f, back.Modules[0].Params["x"]);
    }

    [Fact]
    public void UnspecifiedParams_ReadAsCatalogueDefaults()
    {
        var layout = KnobLayout.For(Chain("overdrive"));
        var encoded = layout.Encode();
        var back = layout.Decode(encoded);
        Assert.Equal(0.5f, back.Modules[0].Params["drive"], 4);
        Assert.Equal(0.7f, back.Modules[0].Params["level"], 4);
    }
}
