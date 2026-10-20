using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
/// The curated gear-quality ordering seam (<see cref="GearCuration"/>) the gear
/// rail sorts by — the same pure API the preset-curation seam will reuse. No
/// Avalonia app instance required.
/// </summary>
public class GearCurationTests
{
    [Fact]
    public void Amps_and_cabs_rank_first()
    {
        // 0.36: the neural amp leads the rail — it is the one with a real amp
        // face, and the modelled amp is presented as "Legacy Amp".
        Assert.Equal(0, GearCuration.RankOf("namamp"));
        Assert.True(GearCuration.RankOf("namamp") < GearCuration.RankOf("amp"));
        Assert.True(GearCuration.RankOf("amp") < GearCuration.RankOf("cab"));
        // namamp/amp/cab all sort ahead of any drive.
        Assert.True(GearCuration.RankOf("cab") < GearCuration.RankOf("overdrive"));
    }

    [Fact]
    public void Drives_rank_ahead_of_time_and_modulation()
    {
        Assert.True(GearCuration.RankOf("overdrive") < GearCuration.RankOf("delay"));
        Assert.True(GearCuration.RankOf("distortion") < GearCuration.RankOf("chorus"));
    }

    [Fact]
    public void Utility_dynamics_rank_after_colour_gear()
    {
        Assert.True(GearCuration.RankOf("gain") > GearCuration.RankOf("reverb"));
        Assert.True(GearCuration.RankOf("noisereducer") > GearCuration.RankOf("phaser"));
    }

    [Fact]
    public void Unknown_modules_fall_to_the_fallback_rank()
    {
        Assert.Equal(GearCuration.Fallback, GearCuration.RankOf("something-native-added-later"));
        Assert.Equal(GearCuration.Fallback, GearCuration.RankOf(null));
        // A curated module always ranks ahead of an unmapped one.
        Assert.True(GearCuration.RankOf("amp") < GearCuration.RankOf("mystery"));
    }

    [Fact]
    public void Every_catalogue_module_has_a_curated_rank()
    {
        // A new native module dropping in unranked would just sort last (the
        // Fallback), never crash — but the flagship families should all be
        // placed. Guard that the whole shipped catalogue is deliberately ranked.
        foreach (var id in DcRigTargetCatalog.ModuleTypeIds)
        {
            Assert.True(GearCuration.RankOf(id) < GearCuration.Fallback,
                $"catalogue module '{id}' has no curated rank");
        }
    }
}
