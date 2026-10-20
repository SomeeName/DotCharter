using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The CATALOG half of W5a: what the matcher hands out has to be gear this app
///     actually has, with every knob inside the range the catalogue declares — and a
///     rig that fails is REJECTED and NAMED, never clamped into range and never
///     dropped without a word.
/// </summary>
public sealed class RigCatalogValidationTests
{
    private static DcRigModule M(string type, params (string Id, float Value)[] ps) =>
        new(type, ps.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal));

    [Fact]
    public void AModuleTheCatalogueDoesNotHave_IsRejected_AndTheFaultNamesIt()
    {
        var rig = new DcRigPreset([M("amp"), M("tubescreamer9000"), M("cab")]);

        var fault = RigCatalogValidation.FirstFault(rig);

        Assert.NotNull(fault);
        Assert.Equal(1, fault!.ModuleIndex);
        Assert.Equal("tubescreamer9000", fault.ModuleTypeId);
        Assert.Null(fault.ParamId);
        Assert.Contains("tubescreamer9000", fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKnobOutsideItsDeclaredRange_IsRejected_AndTheFaultNamesTheValueAndTheRange()
    {
        Assert.True(DcRigTargetCatalog.TryGetParam("overdrive", "drive", out var spec));
        var rig = new DcRigPreset([M("overdrive", ("drive", spec.Max + 5f)), M("amp")]);

        var fault = RigCatalogValidation.FirstFault(rig);

        Assert.NotNull(fault);
        Assert.Equal("overdrive", fault!.ModuleTypeId);
        Assert.Equal("drive", fault.ParamId);
        Assert.Contains("drive", fault.Message, StringComparison.Ordinal);
        Assert.Contains(spec.Max.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            fault.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANaNKnob_IsRejected_ratherThanPassingAsNotOutOfRange()
    {
        // A NaN compares false against both bounds, so "value < Min || value > Max"
        // alone would wave it through — and it reaches the engine as a param the
        // module silently refuses.
        var rig = new DcRigPreset([M("overdrive", ("drive", float.NaN))]);

        var fault = RigCatalogValidation.FirstFault(rig);

        Assert.NotNull(fault);
        Assert.Equal("drive", fault!.ParamId);
    }

    [Fact]
    public void AParamTheModuleDoesNotHave_IsRejected()
    {
        var rig = new DcRigPreset([M("amp", ("sparkle", 0.5f))]);

        var fault = RigCatalogValidation.FirstFault(rig);

        Assert.NotNull(fault);
        Assert.Equal("sparkle", fault!.ParamId);
    }

    [Fact]
    public void ARigDrawnFromTheSampler_AlwaysPasses()
    {
        // The guard against the guard, and the reason retrieval is not what breaks:
        // a sampler draw is built out of the catalogue's own specs, so 500 of them
        // must produce zero faults. A validator that rejected everything would pass
        // every test above and fail this one.
        var sampler = new RigSampler();
        var rng = new Random(20260916);
        var profile = new RigSamplingProfile();
        for (var i = 0; i < 500; i++)
        {
            var rig = sampler.Sample(rng, profile);
            var fault = RigCatalogValidation.FirstFault(rig);
            Assert.True(fault is null,
                $"draw {i} ({string.Join("->", rig.Modules.Select(m => m.ModuleTypeId))}): {fault?.Message}");
        }
    }

    [Fact]
    public void AnEmptyRig_Passes_AndANullRigDoesNot()
    {
        // An empty chain is a legal (if useless) rig — the plausibility prior is what
        // marks it down, not this. A null is a programming error and says so.
        Assert.True(RigCatalogValidation.IsValid(new DcRigPreset([])));
        Assert.False(RigCatalogValidation.IsValid(null));
    }
}
