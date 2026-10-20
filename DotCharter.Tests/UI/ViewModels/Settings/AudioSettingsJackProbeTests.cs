using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.UI.ViewModels.Settings;

/// <summary>
///     Opening Audio settings probes the jack count of the SELECTED input
///     driver, and only that one.
///     <para>
///         It always asked ASIO. On a WASAPI box that made the engine enumerate
///         a driver the session will never open — the same live device round
///         trip the whole background worker in <c>Open()</c> exists to keep off
///         the UI thread — and then rebuilt the guitar-jack picker from the 0 it
///         inevitably answered, so the row fell back to its generic list while
///         the chosen capture device's real channel count sat one call away.
///     </para>
/// </summary>
public sealed class AudioSettingsJackProbeTests
{
    /// <summary>THE regression: WASAPI input selected, so ASIO is not asked at
    /// all.</summary>
    [Fact]
    public async Task Open_OnWasapiInput_ProbesWasapiOnly()
    {
        var (vm, engine) = NewVm(inputDriver: "WASAPI");

        vm.Open();
        await vm.DeviceProbe;

        var probe = Assert.Single(engine.InputChannelCountProbes);
        Assert.Equal("WASAPI", probe.Driver);
        Assert.Equal("Line In (Focusrite)", probe.Device);
    }

    /// <summary>The control, and the load-bearing half: an ASIO box must still
    /// get its ASIO jack count, or the fix would simply be "never probe".</summary>
    [Fact]
    public async Task Open_OnAsioInput_ProbesAsioOnly()
    {
        var (vm, engine) = NewVm(inputDriver: "ASIO");

        vm.Open();
        await vm.DeviceProbe;

        var probe = Assert.Single(engine.InputChannelCountProbes);
        Assert.Equal("ASIO", probe.Driver);
        Assert.Equal("Focusrite USB ASIO", probe.Device);
    }

    /// <summary>…and the count that comes back is what the picker is built
    /// from, whichever driver answered it. A probe whose result went nowhere
    /// would satisfy both cases above.</summary>
    [Fact]
    public async Task TheProbedCount_IsWhatTheJackPickerOffers()
    {
        var (vm, engine) = NewVm(inputDriver: "WASAPI");
        engine.InputDeviceChannelCount = 6;

        vm.Open();
        await vm.DeviceProbe;

        Assert.Equal(6, vm.InputJackOptions.Count(o => o.Jack >= 0));
    }

    private static (AudioSettingsViewModel Vm, FakeAudioPlaybackService Engine) NewVm(string inputDriver)
    {
        var config = new FakeConfigurationService();
        var c = config.AppConfig;
        c.InputDriver = inputDriver;
        c.AsioInputDeviceName = "Focusrite USB ASIO";
        c.WasapiInputDeviceName = "Line In (Focusrite)";

        var engine = new FakeAudioPlaybackService();
        var latency = new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard();
        var vm = new AudioSettingsViewModel(
            engine, engine, config, new ProjectState(), new AudioSettingsWindows(latency), new GatedInlineDispatcher(), FakeLocalization.English());
        return (vm, engine);
    }
}
