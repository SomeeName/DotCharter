using DotCharter.Presentation.Screens.Settings;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.UI.ViewModels.Settings;

/// <summary>
///     The same fault as <see cref="AudioSettingsMidiSaveTests" /> one row up: the
///     OUTPUT, INPUT and streaming-mirror device that Save writes are derived from
///     the dropdown rows <c>Open()</c> fills on a BACKGROUND probe, and Save did
///     not ask whether they had arrived.
///     <para>
///         Before the probe lands, every one of those `Selected…Index` properties
///         is still its initial 0 over an EMPTY list, so Save wrote index 0 with an
///         empty device NAME — and `ResolveIndex` reads that back as "the first
///         device", moving the user's audio onto whatever enumerates first. The
///         first-run wizard makes it the ordinary case: "Play test click" and
///         "Apply input" are Open-then-Save in one click.
///     </para>
/// </summary>
public sealed class AudioSettingsDeviceSaveTests
{
    [Fact]
    public void Save_BeforeTheDeviceProbeLands_KeepsEveryConfiguredDevice()
    {
        var (vm, config) = NewVm();
        var c = config.AppConfig;
        c.WasapiOutputDevice = 4;
        c.WasapiOutputDeviceName = "Focusrite USB (Speakers)";
        c.AsioOutputDevice = 2;
        c.AsioOutputDeviceName = "Focusrite USB ASIO";
        c.WasapiInputDevice = 7;
        c.WasapiInputDeviceName = "Line In (Focusrite)";
        c.AsioInputDevice = 1;
        c.AsioInputDeviceName = "Focusrite USB ASIO";
        c.StreamingMirrorDevice = 3;
        c.StreamingMirrorDeviceName = "VoiceMeeter Input";

        vm.Open();                        // the probe's PostNormal is never drained
        Assert.False(vm.DeviceListsLoaded);

        vm.SaveCommand.Execute(null);

        var after = config.AppConfig;
        Assert.Equal(4, after.WasapiOutputDevice);
        Assert.Equal("Focusrite USB (Speakers)", after.WasapiOutputDeviceName);
        Assert.Equal(2, after.AsioOutputDevice);
        Assert.Equal("Focusrite USB ASIO", after.AsioOutputDeviceName);
        Assert.Equal(7, after.WasapiInputDevice);
        Assert.Equal("Line In (Focusrite)", after.WasapiInputDeviceName);
        Assert.Equal(1, after.AsioInputDevice);
        Assert.Equal(3, after.StreamingMirrorDevice);
        Assert.Equal("VoiceMeeter Input", after.StreamingMirrorDeviceName);
    }

    /// <summary>The non-vacuous half: once the rows are real, Save writes the
    /// picked one. Without this the fix could be "never write a device".</summary>
    [Fact]
    public void Save_AfterTheProbeLanded_WritesThePickedDevices()
    {
        var (vm, config) = NewVm();
        vm.Open();

        // What the probe's marshalled continuation does.
        vm.WasapiDevices.Add("Speakers");
        vm.WasapiDevices.Add("Focusrite USB (Speakers)");
        vm.WasapiInputDevicesList.Add("Line In (Focusrite)");
        vm.StreamingMirrorDevices.Add("VoiceMeeter Input");
        SetLoaded(vm);
        vm.SelectedWasapiOutputIndex = 1;
        vm.SelectedWasapiInputIndex = 0;
        vm.SelectedStreamingMirrorIndex = 0;

        vm.SaveCommand.Execute(null);

        var after = config.AppConfig;
        Assert.Equal(1, after.WasapiOutputDevice);
        Assert.Equal("Focusrite USB (Speakers)", after.WasapiOutputDeviceName);
        Assert.Equal(0, after.WasapiInputDevice);
        Assert.Equal("Line In (Focusrite)", after.WasapiInputDeviceName);
        Assert.Equal(0, after.StreamingMirrorDevice);
        Assert.Equal("VoiceMeeter Input", after.StreamingMirrorDeviceName);
    }

    private static void SetLoaded(AudioSettingsViewModel vm)
    {
        var prop = typeof(AudioSettingsViewModel).GetProperty(
            nameof(AudioSettingsViewModel.DeviceListsLoaded),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        Assert.NotNull(prop);
        prop!.SetValue(vm, true);
    }

    private static (AudioSettingsViewModel Vm, FakeConfigurationService Config) NewVm()
    {
        var config = new FakeConfigurationService();
        var engine = new FakeAudioPlaybackService();
        var latency = new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard();
        var vm = new AudioSettingsViewModel(
            engine, engine, config, new ProjectState(), new AudioSettingsWindows(latency), new NeverDrainedDispatcher(), FakeLocalization.English());
        return (vm, config);
    }

    /// <summary>A UI thread that has not got round to the probe's continuation
    /// yet — posts are dropped, never run.</summary>
    private sealed class NeverDrainedDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) { }
        public void PostNormal(Action action) { }
        public Task InvokeAsync(Action action) => Task.CompletedTask;
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(default(T)!);
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => Task.FromResult(default(T)!);
    }
}
