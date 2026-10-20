using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
/// Liveness gate for the <c>sustainerKeys</c> override axis (compressor-vs-
/// sustainer steering, 0.16 2.2). The other axes are covered by
/// <see cref="RsGearOverridesLivenessTests"/> — this suite only guards what
/// that one predates.
/// </summary>
public sealed class RsGearOverridesTests
{
    private static readonly RsGearCatalog Catalog = RsGearCatalog.Shared;
    private static readonly RsGearOverrides Overrides = RsGearOverrides.Shared;

    [Fact]
    public void SustainerKeys_LoadedNonEmpty() =>
        // Guards the loader: a garbled embedded resource degrades to Empty,
        // which would pass the per-key liveness check below vacuously.
        Assert.NotEmpty(Overrides.SustainerKeys);

    [Fact]
    public void EverySustainerKey_ResolvesToACataloguePedal()
    {
        foreach (var key in Overrides.SustainerKeys)
        {
            Assert.True(Catalog.TryGetGear(key, out var gear),
                $"sustainerKeys key '{key}' is not in the gear catalogue");
            Assert.Equal("Pedals", gear!.Type);
            Assert.True(Overrides.IsForcedSustainer(key));
        }
    }

    [Fact]
    public void SustainerKeys_DoNotOverlapTheDriveRoutingSets()
    {
        foreach (var key in Overrides.SustainerKeys)
        {
            Assert.False(Overrides.IsForcedDistortion(key),
                $"'{key}' claims both sustainer and forced-distortion routing");
            Assert.False(Overrides.IsForcedOverdrive(key),
                $"'{key}' claims both sustainer and forced-overdrive routing");
            Assert.False(Overrides.TryGetTargetModule(key, out _),
                $"'{key}' claims both sustainer and targetModule routing");
        }
    }
}
