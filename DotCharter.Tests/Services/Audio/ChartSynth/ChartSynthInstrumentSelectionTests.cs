using DotCharter.Tone.Audio.ChartSynth;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The instrument selection's pure rules (#50): the defaults, the config round
///     trip (by NAME, so a bad or renamed value falls back instead of swapping the
///     instrument), and the normalisation that keeps a pickup on its model.
/// </summary>
public sealed class ChartSynthInstrumentSelectionTests
{
    private static readonly ChartSynthModelInfo Tele = new(ChartSynthModel.Telecaster, false,
        [ChartSynthPickup.Neck, ChartSynthPickup.NeckBridge, ChartSynthPickup.Bridge], ChartSynthPickup.Bridge, false, true, [], []);

    [Fact]
    public void AFreshConfig_IsTheOwnersHss_AndAJazzBass()
    {
        var c = new RigConfig();
        Assert.Equal(new ChartSynthInstrument(ChartSynthModel.StratHss, ChartSynthPickup.Bridge, false), ChartSynthInstrumentSelection.FromConfig(c, false));
        Assert.Equal(new ChartSynthInstrument(ChartSynthModel.JazzBass, ChartSynthPickup.NeckBridge, false), ChartSynthInstrumentSelection.FromConfig(c, true));
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, ChartSynthInstrumentSelection.FromConfig(c, false));
    }

    [Theory]
    [InlineData(ChartSynthModel.LesPaul, ChartSynthPickup.Neck, true)]
    [InlineData(ChartSynthModel.StratSss, ChartSynthPickup.MiddleBridge, false)]
    [InlineData(ChartSynthModel.PjBass, ChartSynthPickup.Bridge, false)]
    [InlineData(ChartSynthModel.HollowBass, ChartSynthPickup.Neck, false)]
    public void ASelection_RoundTripsThroughTheConfig(ChartSynthModel model, ChartSynthPickup pickup, bool split)
    {
        var c = new RigConfig();
        var i = new ChartSynthInstrument(model, pickup, split);
        ChartSynthInstrumentSelection.WriteTo(c, i);
        Assert.Equal(i, ChartSynthInstrumentSelection.FromConfig(c, i.IsBass));
        // The OTHER family is untouched.
        Assert.Equal(ChartSynthInstrumentSelection.Default(!i.IsBass), ChartSynthInstrumentSelection.FromConfig(c, !i.IsBass));
        // Stored by name.
        Assert.Equal(model.ToString(), i.IsBass ? c.ChartSynthBassModel : c.ChartSynthGuitarModel);
    }

    [Theory]
    [InlineData("Banjo", "Bridge")]
    [InlineData("", "")]
    [InlineData("JazzBass", "NeckBridge")]   // a BASS model in the guitar's slot
    [InlineData("StratHss", "Headstock")]
    [InlineData("2", "4")]                   // numbers are not names
    public void Nonsense_FallsBackToTheDefault(string model, string pickup)
    {
        var c = new RigConfig { ChartSynthGuitarModel = model, ChartSynthGuitarPickup = pickup };
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, ChartSynthInstrumentSelection.FromConfig(c, bass: false));
    }

    [Fact]
    public void Normalize_KeepsThePickupOnTheModel_AndTheSplitWhereItMeansSomething()
    {
        var onStratPosition = new ChartSynthInstrument(ChartSynthModel.Telecaster, ChartSynthPickup.NeckMiddle, true);
        Assert.Equal(new ChartSynthInstrument(ChartSynthModel.Telecaster, ChartSynthPickup.Bridge, false),
            ChartSynthInstrumentSelection.Normalize(onStratPosition, bass: false, Tele));
        var fine = new ChartSynthInstrument(ChartSynthModel.Telecaster, ChartSynthPickup.Neck, false);
        Assert.Equal(fine, ChartSynthInstrumentSelection.Normalize(fine, bass: false, Tele));
        Assert.Equal(ChartSynthInstrumentSelection.DefaultBass,
            ChartSynthInstrumentSelection.Normalize(fine, bass: true, Tele));   // wrong family
    }
}
