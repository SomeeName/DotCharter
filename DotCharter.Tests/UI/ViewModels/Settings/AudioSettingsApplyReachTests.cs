using DotCharter.Tone.Audio.Midi;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.UI.ViewModels.Settings;

/// <summary>
///     Saving the Audio tab has to APPLY what it saved. Three rows did not: the
///     output/input/mirror device NAMES, the MIDI output device, and the two
///     manual timing trims. All three were written to <c>appconfig.json</c> and
///     then ignored by the running app until the next launch, because
///     <c>AudioSettingsViewModel.Save</c> only calls
///     <c>IAudioPlaybackService.ApplyAudioConfiguration</c> when its own
///     <c>HasRoutingChanged</c> says something moved — and that check compared
///     device INDEXES while the engine opens devices by NAME, never looked at the
///     MIDI device at all, and (correctly) does not cover the delay trims, which
///     had no other route to the service either.
/// </summary>
public sealed class AudioSettingsApplyReachTests
{
    /// <summary>
    ///     The ordinary case: a saved device that has been unplugged. <c>Open</c>
    ///     cannot match the stale NAME, falls back to the saved INDEX, and the row
    ///     lands on a different device at the same row number — so the Save that
    ///     finally records the right name changed no index and, before the fix,
    ///     restarted nothing. The engine went on playing through whatever the
    ///     startup fallback had opened for the rest of the session.
    /// </summary>
    [Fact]
    public void Save_ThatChangesTheDeviceNameButNotItsIndex_RestartsTheEngine()
    {
        var (vm, config, engine) = NewVm();
        PinDeviceRows(config);
        config.AppConfig.WasapiOutputDeviceName = "Headset (unplugged)";

        vm.Open();
        // What the probe's marshalled continuation does: real rows, and the
        // stale name resolving to row 0.
        vm.WasapiDevices.Add("Speakers (Realtek)");
        SetLoaded(vm);
        vm.SelectedWasapiOutputIndex = 0;
        engine.AppliedConfigs.Clear();

        vm.SaveCommand.Execute(null);

        Assert.Equal("Speakers (Realtek)", config.AppConfig.WasapiOutputDeviceName);
        Assert.Equal(0, config.AppConfig.WasapiOutputDevice);
        Assert.True(engine.AppliedConfigs.Count > 0,
            "The persisted output device changed, so the engine must be re-applied onto it.");
    }

    /// <summary>The same hole on the capture side and on the streaming mirror —
    /// both are resolved by NAME by the service, and neither name was compared.</summary>
    [Fact]
    public void Save_ThatChangesTheInputOrMirrorNameOnly_RestartsTheEngine()
    {
        foreach (var mirrorOnly in new[] { false, true })
        {
            var (vm, config, engine) = NewVm();
            PinDeviceRows(config);
            config.AppConfig.WasapiInputDeviceName = "Old interface";
            config.AppConfig.StreamingMirrorDevice = 0;
            config.AppConfig.StreamingMirrorDeviceName = "Old cable";

            vm.Open();
            vm.WasapiInputDevicesList.Add(mirrorOnly ? "Old interface" : "Focusrite USB");
            vm.StreamingMirrorDevices.Add(mirrorOnly ? "VB-Cable" : "Old cable");
            SetLoaded(vm);
            vm.SelectedWasapiInputIndex = 0;
            vm.SelectedStreamingMirrorIndex = 0;
            engine.AppliedConfigs.Clear();

            vm.SaveCommand.Execute(null);

            Assert.True(engine.AppliedConfigs.Count > 0,
                mirrorOnly
                    ? "A mirror device change must reach ApplyAudioConfiguration."
                    : "An input device change must reach ApplyAudioConfiguration.");
        }
    }

    /// <summary>
    ///     <c>ApplyMidiDevice</c> is reached from
    ///     <c>JuceAudioPlaybackService.ApplyAudioConfigurationCore</c> and from
    ///     nowhere else, so a Save whose only change was the MIDI output port left
    ///     the previous port open for the rest of the session.
    /// </summary>
    [Fact]
    public async Task Save_ThatOnlyChangesTheMidiOutput_RestartsTheEngine()
    {
        var midi = new List<MidiOutputDeviceInfo>
        {
            new(-1, "MIDI Mapper"),
            new(0, "Microsoft GS Wavetable Synth"),
            new(1, "loopMIDI Port"),
        };
        var (vm, config, engine) = NewVm(midi, new InlineDispatcher());
        PinDeviceRows(config);
        config.AppConfig.MidiOutputDevice = -1;

        vm.Open();
        await vm.DeviceProbe;
        engine.AppliedConfigs.Clear();

        vm.SelectedMidiOutputIndex = 2;
        vm.SaveCommand.Execute(null);

        Assert.Equal(1, config.AppConfig.MidiOutputDevice);
        Assert.Equal("loopMIDI Port", config.AppConfig.MidiOutputDeviceName);
        Assert.True(engine.AppliedConfigs.Count > 0,
            "Picking another MIDI output must (re)open it, not only write it to the config file.");
    }

    /// <summary>
    ///     The non-vacuous half. Without it the fix could be "always re-apply",
    ///     which is what this guard exists to prevent: two engine restarts in
    ///     quick succession on the UI thread can fast-fail the process inside the
    ///     native driver-close path (STATUS_STACK_BUFFER_OVERRUN 0xC0000409).
    /// </summary>
    [Fact]
    public void Save_WithNothingRoutingRelatedChanged_DoesNotRestartTheEngine()
    {
        var (vm, config, engine) = NewVm();
        PinDeviceRows(config);
        config.AppConfig.WasapiOutputDeviceName = "Speakers (Realtek)";

        vm.Open();
        vm.WasapiDevices.Add("Speakers (Realtek)");
        SetLoaded(vm);
        vm.SelectedWasapiOutputIndex = 0;
        engine.AppliedConfigs.Clear();

        vm.SaveCommand.Execute(null);

        Assert.Empty(engine.AppliedConfigs);
    }

    /// <summary>
    ///     The two manual timing trims are engine properties the playhead
    ///     compensation and the MIDI scheduler read LIVE, and
    ///     <c>ApplyAudioConfigurationCore</c> was their only writer — so a Save
    ///     whose only change was one of these numbers did nothing at all until the
    ///     app was restarted. They are pushed straight at the service now, which
    ///     also means they must NOT drag a device change along with them.
    /// </summary>
    [Fact]
    public void Save_PushesTheDelayTrims_WithoutRestartingTheEngine()
    {
        var (vm, config, engine) = NewVm();
        PinDeviceRows(config);
        vm.Open();
        engine.AudioDelayMs = 0;
        engine.MidiOffsetMs = 0;
        engine.AppliedConfigs.Clear();

        vm.AudioDelayMs = 12.5;
        vm.MidiDelayMs = -4.0;
        vm.SaveCommand.Execute(null);

        Assert.Equal(12.5, engine.AudioDelayMs);
        Assert.Equal(-4.0, engine.MidiOffsetMs);
        Assert.Equal(12.5, config.AppConfig.AudioDelayMs);
        Assert.Equal(-4.0, config.AppConfig.MidiDelayMs);
        Assert.Empty(engine.AppliedConfigs);
    }

    /// <summary>
    ///     Parks every device ROW where an already-probed page would leave it, so
    ///     each test below moves exactly one thing.
    ///     <para>
    ///         Without this the tests would pass VACUOUSLY: three of the four
    ///         index fields default to <c>-1</c> in <c>AppConfig</c> while the view
    ///         model's <c>Selected…Index</c> properties default to <c>0</c>, so any
    ///         Save over a forced <c>DeviceListsLoaded</c> already writes a real
    ///         index change and re-applies the config on its own.
    ///     </para>
    /// </summary>
    private static void PinDeviceRows(FakeConfigurationService config)
    {
        var c = config.AppConfig;
        c.WasapiOutputDevice = 0;
        c.AsioOutputDevice = 0;
        c.WasapiInputDevice = 0;
        c.AsioInputDevice = 0;
        c.StreamingMirrorDevice = -1;
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

    private static (AudioSettingsViewModel Vm, FakeConfigurationService Config, FakeAudioPlaybackService Engine)
        NewVm(IReadOnlyList<MidiOutputDeviceInfo>? midi = null, IUiDispatcher? ui = null)
    {
        var config = new FakeConfigurationService();
        var engine = new FakeAudioPlaybackService { MidiOutputs = midi ?? [] };
        var latency = new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard();
        var vm = new AudioSettingsViewModel(
            engine, engine, config, new ProjectState(), new AudioSettingsWindows(latency),
            ui ?? new NeverDrainedDispatcher(), FakeLocalization.English());
        return (vm, config, engine);
    }

    /// <summary>A UI thread that has not got round to the probe's continuation
    /// yet — posts are dropped, never run. The device rows are then stamped by
    /// hand, exactly as <c>AudioSettingsDeviceSaveTests</c> does.</summary>
    private sealed class NeverDrainedDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) { }
        public void PostNormal(Action action) { }
        public Task InvokeAsync(Action action) => Task.CompletedTask;
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(default(T)!);
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => Task.FromResult(default(T)!);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}
