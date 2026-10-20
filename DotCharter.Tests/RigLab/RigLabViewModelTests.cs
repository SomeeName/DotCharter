using DotCharter.RigLab;
using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.Settings;

namespace DotCharter.Tests.RigLab;

/// <summary>
///     RigLab's two setup tabs, against the shared audio fake: every change
///     reaches the config file AND the engine, and the VST chain the engine hears
///     is the one the tab shows.
/// </summary>
public sealed class RigLabViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "riglab-vm-" + Guid.NewGuid().ToString("N"));
    private readonly RigLabConfigService _config;
    private readonly FakeAudioPlaybackService _audio = new();
    private readonly GatedInlineDispatcher _ui = new();

    public RigLabViewModelTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new RigLabConfigService(Path.Combine(_dir, RigLabConfigService.FileName), TimeSpan.Zero);
    }

    public void Dispose()
    {
        _config.Dispose();
        BestEffortDelete.Directory(_dir);
    }

    private static Task Inline(Func<Task> work) => work();

    private AudioSettingsViewModel Settings() =>
        new(_audio, _config, _ui, new LiveInputChannelViewModel(_audio, _config, FakeLocalization.English(), _ui), FakeLocalization.English(), Inline);

    private VstRackViewModel Rack() => new(_audio, _config, _ui, logger: null, worker: Inline);

    // ---- Audio -----------------------------------------------------------------

    [Fact]
    public void PickingABufferSize_IsSavedAndReappliedToTheEngine()
    {
        var vm = Settings();
        var applied = _audio.AppliedConfigs.Count;

        vm.SelectedBufferSize = vm.BufferSizes.Single(b => b.Value == "128");

        Assert.Equal(128, _config.Load().AudioBufferFrames);
        Assert.True(_audio.AppliedConfigs.Count > applied, "the engine was not re-opened with the new buffer");
        Assert.Equal(128, _audio.AppliedConfigs[^1].AudioBufferFrames);
    }

    [Fact]
    public void SwitchingLiveInputOff_IsSavedAndReapplied()
    {
        var vm = Settings();
        Assert.True(vm.EnableInput);

        vm.EnableInput = false;

        Assert.False(_config.Load().EnableInput);
        Assert.False(_audio.AppliedConfigs[^1].EnableInput);
    }

    [Fact]
    public void ChoosingAsio_ShowsTheControlPanel_AndHidesTheSeparateInputDevice()
    {
        var vm = Settings();
        Assert.False(vm.IsAsio);
        Assert.True(vm.HasSeparateInputDevice);

        vm.SelectedDriver = vm.Drivers.Single(d => d.Value == AudioSettingsViewModel.Asio);

        Assert.True(vm.IsAsio);
        Assert.False(vm.HasSeparateInputDevice);
        Assert.Equal("ASIO", _config.Load().AudioDriver);
    }

    /// <summary>
    ///     Owner report (2026-09-26): switching to ASIO showed no output devices
    ///     until the tab was left and re-entered. The list is read off the live
    ///     engine, and the first cut read it alongside the re-open instead of
    ///     after it.
    /// </summary>
    [Fact]
    public void SwitchingToAsio_ListsItsDevices_AfterTheEngineReopens()
    {
        _audio.AsioDevicesInstalled = ["Focusrite USB ASIO"];
        var vm = Settings();

        vm.SelectedDriver = vm.Drivers.Single(d => d.Value == AudioSettingsViewModel.Asio);

        Assert.Equal(["Focusrite USB ASIO"], vm.OutputDevices);
        Assert.Equal("Focusrite USB ASIO", vm.SelectedOutputDevice);
        // …and the engine runs on the device the picker shows.
        Assert.Equal("Focusrite USB ASIO", _config.Load().AsioOutputDeviceName);
        Assert.Equal("Focusrite USB ASIO", _audio.AppliedConfigs[^1].AsioOutputDeviceName);
    }

    [Fact]
    public void TheStatusLine_SaysWhenTheEngineIsDown_AndWhatItNegotiatedWhenUp()
    {
        var vm = Settings();

        _audio.ActiveSampleRate = 0;
        vm.RefreshStatus();
        Assert.False(vm.IsEngineRunning);
        Assert.Equal(FakeLocalization.English()["RigLab_Audio_EngineDown"], vm.StatusText);

        _audio.ActiveSampleRate = 48000;
        _audio.ActualBufferFrames = 256;
        vm.RefreshStatus();
        Assert.True(vm.IsEngineRunning);
        Assert.Contains("256", vm.StatusText);
    }

    [Fact]
    public void TheAsioControlPanel_ParentsToTheWindowTheViewHandedOver()
    {
        var vm = Settings();
        vm.WindowHandle = () => new IntPtr(0x1234);

        vm.OpenAsioControlPanelCommand.Execute(null);

        Assert.Equal(new IntPtr(0x1234), Assert.Single(_audio.AsioControlPanelParents));
    }

    // ---- Plugins ---------------------------------------------------------------

    private static VstPluginInfo Plugin(string name) =>
        new() { Name = name, PluginPath = $@"C:\VST3\{name}.vst3", IsEffect = true };

    [Fact]
    public async Task Restore_ScansAndPutsTheSavedChainBackOnTheEngine()
    {
        _config.Update(c => c.GuitarVstChain.Add(new VstChainEntry { PluginPath = @"C:\VST3\Chorus.vst3" }));
        _audio.VstPluginsOnDisk = [Plugin("Chorus"), Plugin("Delay")];
        using var rack = Rack();

        await rack.RestoreAsync();

        Assert.True(_audio.IsVstEffectEnabled);
        Assert.Equal(@"C:\VST3\Chorus.vst3", Assert.Single(Assert.Single(_audio.PushedGuitarChains)).PluginPath);
        Assert.Equal("Chorus", Assert.Single(rack.Chain).PluginName);
        Assert.Equal(2, rack.AvailablePlugins.Count);
    }

    [Fact]
    public async Task AddingMovingAndRemoving_PushTheWholeChain_AndPersistIt()
    {
        _audio.VstPluginsOnDisk = [Plugin("Chorus"), Plugin("Delay")];
        using var rack = Rack();
        await rack.RestoreAsync();

        rack.AddSelection = rack.AvailablePlugins.Single(p => p.Name == "Chorus");
        rack.AddSelection = rack.AvailablePlugins.Single(p => p.Name == "Delay");
        Assert.Null(rack.AddSelection);
        Assert.Equal(["Chorus", "Delay"], _audio.PushedGuitarChains[^1].Select(e => Path.GetFileNameWithoutExtension(e.PluginPath)));

        Assert.False(rack.MoveSlot(1, 1));
        Assert.False(rack.MoveSlot(0, 2));
        Assert.True(rack.MoveSlot(1, 0));
        Assert.Equal(["Delay", "Chorus"], _audio.PushedGuitarChains[^1].Select(e => Path.GetFileNameWithoutExtension(e.PluginPath)));
        Assert.Equal([1, 2], rack.Chain.Select(s => s.DisplayIndex));

        rack.Chain[0].RemoveCommand.Execute(null);
        Assert.Equal("Chorus", Path.GetFileNameWithoutExtension(Assert.Single(_audio.PushedGuitarChains[^1]).PluginPath));
        Assert.Equal(@"C:\VST3\Chorus.vst3", Assert.Single(_config.Load().GuitarVstChain).PluginPath);
        Assert.False(rack.IsEmpty);
    }

    [Fact]
    public async Task Instruments_AreNotOfferedForTheInputChain()
    {
        _audio.VstPluginsOnDisk = [Plugin("Chorus"), new VstPluginInfo { Name = "Synth", PluginPath = @"C:\VST3\Synth.vst3", IsInstrument = true }];
        using var rack = Rack();

        await rack.RestoreAsync();

        Assert.Equal("Chorus", Assert.Single(rack.AvailablePlugins).Name);
    }

    [Fact]
    public async Task AFailedScan_LeavesAnEmptyList_NotACrash()
    {
        _audio.ThrowOnScanVstPlugins = true;
        using var rack = Rack();

        await rack.RestoreAsync();

        Assert.Empty(rack.AvailablePlugins);
        Assert.True(rack.IsEmpty);
    }

    // ---- Input meter -----------------------------------------------------------

    [Fact]
    public void ThePeak_IsTheLargestAbsoluteSample_OverTheSamplesRead()
    {
        float[] window = [0.1f, -0.8f, 0.3f, 0.95f];

        Assert.Equal(0.8f, InputPeakMeter.Peak(window, 3));
        Assert.Equal(0.95f, InputPeakMeter.Peak(window, 4));
        Assert.Equal(0f, InputPeakMeter.Peak(window, 0));
        Assert.Equal(0.95f, InputPeakMeter.Peak(window, 99));
    }
}
