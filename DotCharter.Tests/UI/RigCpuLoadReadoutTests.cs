using DotCharter.Localization;
using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig footer's CPU readout (JuceSharp #29, ABI v46): it prints the engine's
///     MEASURED callback load and nothing else — a dash and the reason without an
///     engine — and it asks the engine at most 4 times a second off the view's 20 Hz
///     meter clock.
/// </summary>
public sealed class RigCpuLoadReadoutTests
{
    private static readonly ILocalizationService Loc = FakeLocalization.English();

    [Fact]
    public void ALoad_PrintsTheAverage_AndTheTooltipCarriesThePeak()
    {
        var cpu = new RigCpuLoadViewModel(() => new AudioCallbackLoad(0.144, 0.31), Loc);
        cpu.Refresh();

        Assert.Equal("14%", cpu.ValueText);
        Assert.False(cpu.IsHigh);
        Assert.Equal(Loc.Format("Rig_CpuTip", 14, 31), cpu.ToolTip);
    }

    [Fact]
    public void NoEngine_PrintsADash_AndSaysWhy()
    {
        var cpu = new RigCpuLoadViewModel(() => null, Loc);
        cpu.Refresh();

        Assert.Equal("–", cpu.ValueText);
        Assert.False(cpu.IsHigh);
        Assert.Equal(Loc["Rig_CpuNoEngine"], cpu.ToolTip);
    }

    [Theory]
    [InlineData(0.79, false)]
    [InlineData(0.80, true)]
    [InlineData(1.4, true)]
    public void APeakNearTheDeadline_Warns(double peak, bool high)
    {
        var cpu = new RigCpuLoadViewModel(() => new AudioCallbackLoad(0.2, peak), Loc);
        cpu.Refresh();
        Assert.Equal(high, cpu.IsHigh);
    }

    [Theory]
    [InlineData(-0.5, "0%")]
    [InlineData(double.NaN, "0%")]
    [InlineData(0.996, "99+")]
    [InlineData(25.0, "99+")]
    public void ANonsenseLoad_StaysInsideTheFixedCell(double average, string text)
    {
        var cpu = new RigCpuLoadViewModel(() => new AudioCallbackLoad(average, average), Loc);
        cpu.Refresh();
        Assert.Equal(text, cpu.ValueText);
    }

    /// <summary>The meter clock ticks at 20 Hz; the engine is read on the first tick
    /// and then every fifth one — 4 Hz.</summary>
    [Fact]
    public void TheMeterClock_ReadsTheEngine_AtAQuarterOfItsRate()
    {
        var reads = 0;
        var cpu = new RigCpuLoadViewModel(() => { reads++; return new AudioCallbackLoad(0.1, 0.1); }, Loc);

        cpu.Tick();
        Assert.Equal(1, reads);
        for (var i = 1; i < 20; i++) cpu.Tick();
        Assert.Equal(20 / RigCpuLoadViewModel.TicksPerRead, reads);
        Assert.Equal(reads, cpu.Reads);
    }

    [Fact]
    public void TheRigScreen_HasNoReadout_WithoutAnEngineReader_AndPollsItsReaderWithTheMeters()
    {
        var dsp = new FakeDspChainService([]);
        using var rig = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), Loc);

        Assert.Null(new AmpFxRigViewModel(rig).CpuLoad);

        var load = new AudioCallbackLoad(0.5, 0.9);
        var screen = new AmpFxRigViewModel(rig) { CallbackLoadProvider = () => load };
        Assert.NotNull(screen.CpuLoad);
        screen.PollMeters();
        Assert.Equal("50%", screen.CpuLoad!.ValueText);
        Assert.True(screen.CpuLoad.IsHigh);
    }

    /// <summary>A device seam with no engine behind it — the default every fake and
    /// the null transport inherit — reads as "nothing measured", never as 0 %.</summary>
    [Fact]
    public void ADeviceSeamWithoutAnEngine_ReadsNothing()
    {
        using var audio = new NullAudioPlaybackService();
        var read = RigCpuLoadViewModel.Over(audio);
        Assert.Null(read());
    }
}
