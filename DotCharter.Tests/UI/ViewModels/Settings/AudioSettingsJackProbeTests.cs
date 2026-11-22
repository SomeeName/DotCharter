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

    /// <summary>
    ///     A slow read of capture device A that lands AFTER device B was picked
    ///     must not list A's jacks for B: the newer device's probe wins whatever
    ///     order the two reads finish in.
    /// </summary>
    [Fact]
    public async Task ALateProbeOfTheDeviceLeft_DoesNotOverwriteTheCurrentDevicesJacks()
    {
        var config = new FakeConfigurationService();
        var engine = new FakeAudioPlaybackService { InputDeviceChannelCount = 6 };
        var ui = new HeldPostsDispatcher();
        var vm = new AudioSettingsViewModel(engine, engine, config, new ProjectState(),
            new AudioSettingsWindows(new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard()), ui, FakeLocalization.English());
        vm.AsioInputDevicesList.Add("Interface A");
        vm.AsioInputDevicesList.Add("Interface B");
        vm.SelectedAsioInputIndex = -1;

        vm.SelectedAsioInputIndex = 0;            // device A: six jacks
        await vm.JackCountProbe;
        engine.InputDeviceChannelCount = 3;
        vm.SelectedAsioInputIndex = 1;            // device B: three jacks
        await vm.JackCountProbe;
        await ui.WaitForHeldAsync(2);

        ui.RunNewestFirst();                      // B's read lands, then A's late one

        Assert.Equal(3, vm.InputDeviceJackCount);
    }

    /// <summary>
    ///     WASAPI input with an ASIO capture device listed too: filling the ASIO row while the rows
    ///     load must not read that device, and no read started before the rows' own jack count may
    ///     land after it.
    /// </summary>
    [Fact]
    public async Task Open_OnWasapiInput_TheListedAsioDevice_DoesNotReplaceTheWasapiJacks()
    {
        var config = new FakeConfigurationService();
        var c = config.AppConfig;
        c.InputDriver = "WASAPI";
        c.AsioInputDeviceName = "Focusrite USB ASIO";
        c.WasapiInputDeviceName = "Line In (Focusrite)";
        var engine = new FakeAudioPlaybackService();
        engine.WasapiInputDevices.Add("Line In (Focusrite)");
        engine.AsioInputDevicesInstalled.Add("Focusrite USB ASIO");
        engine.InputDeviceChannelCountByDriver["WASAPI"] = 2;
        engine.InputDeviceChannelCountByDriver["ASIO"] = 8;
        var ui = new HeldPostsDispatcher();
        var vm = NewVm(engine, config, ui);

        vm.Open();
        await vm.DeviceProbe;
        ui.RunNewestFirst();                      // the rows land with the WASAPI device's own jack count
        await vm.JackCountProbe;
        ui.RunNewestFirst();                      // then whatever read the rows started

        Assert.Equal(2, vm.InputDeviceJackCount);
        Assert.DoesNotContain(engine.InputChannelCountProbes, p => p.Driver == "ASIO");
    }

    /// <summary>A read of the ASIO device still running when the input driver turns to WASAPI is
    /// dropped: the WASAPI device's jacks are listed, whichever read lands last.</summary>
    [Fact]
    public async Task SwitchingTheInputDriver_DropsTheOldDriversLateRead()
    {
        var engine = new FakeAudioPlaybackService();
        engine.InputDeviceChannelCountByDriver["ASIO"] = 8;
        engine.InputDeviceChannelCountByDriver["WASAPI"] = 2;
        var ui = new HeldPostsDispatcher();
        var vm = NewVm(engine, new FakeConfigurationService(), ui);
        vm.SelectedInputDriver = "ASIO";
        vm.AsioInputDevicesList.Add("Interface A");
        vm.WasapiInputDevicesList.Add("Line In");
        vm.SelectedWasapiInputIndex = 0;          // not the selected driver: nothing is read
        vm.SelectedAsioInputIndex = 0;            // the ASIO read starts
        await vm.JackCountProbe;

        vm.SelectedInputDriver = "WASAPI";
        await vm.JackCountProbe;
        ui.RunNewestFirst();                      // the WASAPI read, then the late ASIO one

        Assert.Equal(2, vm.InputDeviceJackCount);
    }

    private static AudioSettingsViewModel NewVm(FakeAudioPlaybackService engine, FakeConfigurationService config, HeldPostsDispatcher ui) =>
        new(engine, engine, config, new ProjectState(),
            new AudioSettingsWindows(new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard()), ui, FakeLocalization.English());

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
