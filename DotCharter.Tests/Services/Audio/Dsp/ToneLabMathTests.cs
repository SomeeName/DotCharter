using DotCharter.Tone.Audio.Dsp.Loudness;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Spectrum;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
///     The pure halves of the tone lab (#299): the level-match gain math, the node
///     spectrum's pre/post chain selection and the spectrum itself.
/// </summary>
public sealed class ToneLabMathTests
{
    private static DcRigModule M(string id, float? gainDb = null, bool bypassed = false) =>
        new(id, gainDb is { } g ? new Dictionary<string, float> { [CompareLevelTrim.GainParamId] = g } : [], bypassed);

    // ── level match ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-20.0, -26.0, 6.0)]     // B quieter: raised to A
    [InlineData(-20.0, -14.5, -5.5)]    // B louder: cut to A
    [InlineData(-10.0, -40.0, 12.0)]    // clamped at +MaxTrimDb
    [InlineData(-40.0, -10.0, -12.0)]   // clamped at -MaxTrimDb
    [InlineData(-20.0, -20.05, 0.0)]    // under the just-noticeable step
    public void TrimDb_BringsIncomingToReference_Clamped(double reference, double incoming, double expected)
        => Assert.Equal(expected, CompareLevelTrim.TrimDb(reference, incoming), 6);

    [Fact]
    public void TrimDb_IsZero_WhenALevelIsUnknown()
    {
        Assert.Equal(0, CompareLevelTrim.TrimDb(null, -20));
        Assert.Equal(0, CompareLevelTrim.TrimDb(-20, null));
        Assert.Equal(0, CompareLevelTrim.TrimDb(double.NegativeInfinity, -20));
        Assert.Equal(0, CompareLevelTrim.TrimDb(-20, double.NaN));
    }

    [Fact]
    public void Apply_StaysInTheGainModulesRange_AndReportsWhatLanded()
    {
        Assert.Equal((5.0, 5.0), CompareLevelTrim.Apply(0, 5));
        // 20 + 12 would be 32: the module stops at +24, and only +4 landed.
        Assert.Equal((24.0, 4.0), CompareLevelTrim.Apply(20, 12));
        Assert.Equal((-24.0, -2.0), CompareLevelTrim.Apply(-22, -6));
    }

    [Fact]
    public void EnsureTrailingGain_AppendsOnlyWhenTheRigHasNone()
    {
        var bare = new DcRigPreset([M("amp")]);
        var withGain = CompareLevelTrim.EnsureTrailingGain(bare);
        Assert.Equal(["amp", "gain"], withGain.Modules.Select(m => m.ModuleTypeId));
        Assert.Equal(0f, withGain.Modules[^1].Params[CompareLevelTrim.GainParamId]);

        var already = new DcRigPreset([M("amp"), M("gain", -3)]);
        Assert.Same(already, CompareLevelTrim.EnsureTrailingGain(already));

        // A BYPASSED trailing gain plays nothing, so a trim cannot ride on it.
        var bypassed = new DcRigPreset([M("amp"), M("gain", 0, bypassed: true)]);
        Assert.Equal(3, CompareLevelTrim.EnsureTrailingGain(bypassed).Modules.Count);
    }

    [Fact]
    public void Strip_TakesTheTrimBackOff_SoItNeverCompounds()
    {
        var trimmed = new[] { M("amp"), M("gain", 4.5f) };
        var stripped = CompareLevelTrim.Strip(trimmed, 6);
        Assert.Equal(-1.5f, stripped[^1].Params[CompareLevelTrim.GainParamId], 4);
        Assert.Same(trimmed[0], stripped[0]);
        Assert.Same(trimmed, CompareLevelTrim.Strip(trimmed, 0));
        // The player removed the trailing gain: the trim went with it.
        var noGain = new[] { M("amp") };
        Assert.Same(noGain, CompareLevelTrim.Strip(noGain, 6));
    }

    // ── spectrum pre/post selection ──────────────────────────────────────────

    [Fact]
    public void Select_Pre_IsEverythingBefore_Post_IncludesTheUnit()
    {
        var chain = new[] { M("compressor"), M("overdrive"), M("amp"), M("cab") };
        Assert.Equal(["compressor", "overdrive"], NodeSpectrumChain.Select(chain, 2, NodeSpectrumTap.Pre)!.Select(m => m.ModuleTypeId));
        Assert.Equal(["compressor", "overdrive", "amp"], NodeSpectrumChain.Select(chain, 2, NodeSpectrumTap.Post)!.Select(m => m.ModuleTypeId));
        // The first unit's PRE is the dry input.
        Assert.Empty(NodeSpectrumChain.Select(chain, 0, NodeSpectrumTap.Pre)!);
        Assert.Null(NodeSpectrumChain.Select(chain, 4, NodeSpectrumTap.Post));
        Assert.Null(NodeSpectrumChain.Select(chain, -1, NodeSpectrumTap.Pre));
    }

    [Fact]
    public void Select_RefusesAUnitInsideASplit_AndTheFansThemselves()
    {
        var chain = new[] { M("amp"), M("splitter"), M("delay"), M("reverb"), M("merger"), M("cab") };
        Assert.NotNull(NodeSpectrumChain.Select(chain, 0, NodeSpectrumTap.Post));
        for (var i = 1; i <= 4; i++) Assert.Null(NodeSpectrumChain.Select(chain, i, NodeSpectrumTap.Post));
        // after the merger the chain is one path again
        Assert.Equal(5, NodeSpectrumChain.Select(chain, 5, NodeSpectrumTap.Pre)!.Count);
    }

    // ── the spectrum ─────────────────────────────────────────────────────────

    [Fact]
    public void Spectrum_PutsASineInTheBandThatHoldsIt()
    {
        const int rate = 48_000;
        const int bands = 30;
        var sine = new float[rate];
        for (var i = 0; i < sine.Length; i++) sine[i] = 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
        var spectrum = MagnitudeSpectrum.Compute(sine, rate, bands);
        var loudest = Array.IndexOf(spectrum, spectrum.Max());
        Assert.InRange(1000, MagnitudeSpectrum.BandEdgeHz(loudest, bands), MagnitudeSpectrum.BandEdgeHz(loudest + 1, bands));
        // a 0.5 sine is about -6 dBFS in its band, and the far bands are far below it
        Assert.InRange(spectrum[loudest], -9, -3);
        Assert.True(spectrum[0] < spectrum[loudest] - 60);
        Assert.All(MagnitudeSpectrum.Compute(new float[100], rate, bands), db => Assert.Equal(MagnitudeSpectrum.FloorDb, db));
    }
}
