using DotCharter.Presentation.Screens.Settings;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.UI.ViewModels.Settings;

/// <summary>
///     The audio page's Save must write the MIDI output device the DROPDOWN
///     shows, and nothing else.
///     <para>
///         <c>Open()</c> fills <c>MidiOutputDevices</c> from a background probe
///         and marshals it back through <see cref="IUiDispatcher" />. Save used to
///         re-run <c>WinMMMidiOutput.EnumerateDevices()</c> and index THAT list
///         with the selected row — so a Save that ran before the probe landed saw
///         row 0 against an empty dropdown and wrote the machine's first real MIDI
///         entry (the MIDI Mapper, id -1) over the user's configured device. The
///         first-run wizard does exactly that: "Play test click" and "Apply input"
///         are Open-then-Save in one gesture.
///     </para>
///     <para>
///         The dispatcher here never runs what is posted to it, which is precisely
///         the state a real UI thread is in while the probe is still on the pool —
///         deterministically, with no clock and no sockets.
///     </para>
/// </summary>
public sealed class AudioSettingsMidiSaveTests
{
    [Fact]
    public void Save_BeforeTheDeviceProbeLands_KeepsTheConfiguredMidiDevice()
    {
        var (vm, config) = NewVm();
        config.AppConfig.MidiOutputDevice = 3;
        config.AppConfig.MidiOutputDeviceName = "loopMIDI Port";

        vm.Open();                       // the probe's PostNormal is never drained
        Assert.Empty(vm.MidiOutputDevices);

        vm.SaveCommand.Execute(null);

        Assert.Equal(3, config.AppConfig.MidiOutputDevice);
        Assert.Equal("loopMIDI Port", config.AppConfig.MidiOutputDeviceName);
    }

    /// <summary>The other half: once the dropdown HAS rows, Save still writes the
    /// picked one. Without this the fix above could be "never write MIDI at all".</summary>
    [Fact]
    public void Save_AfterTheProbeLanded_WritesThePickedRow()
    {
        var (vm, config) = NewVm();
        vm.Open();

        // What the probe's marshalled continuation does: rows, then a selection.
        vm.MidiOutputDevices.Add("Default MIDI Mapper");
        vm.MidiOutputDevices.Add("Microsoft GS Wavetable Synth");
        vm.MidiOutputDevices.Add("loopMIDI Port");
        SetDeviceIds(vm, -1, 0, 1);
        vm.SelectedMidiOutputIndex = 2;

        vm.SaveCommand.Execute(null);

        Assert.Equal(1, config.AppConfig.MidiOutputDevice);
        Assert.Equal("loopMIDI Port", config.AppConfig.MidiOutputDeviceName);
    }

    /// <summary>Row 0 IS a choice — the MIDI Mapper — and Save must write it.</summary>
    [Fact]
    public void Save_AfterTheProbeLanded_WritesTheMapperRowAsMinusOne()
    {
        var (vm, config) = NewVm();
        config.AppConfig.MidiOutputDevice = 3;
        config.AppConfig.MidiOutputDeviceName = "loopMIDI Port";
        vm.Open();

        vm.MidiOutputDevices.Add("Default MIDI Mapper");
        SetDeviceIds(vm, -1);
        vm.SelectedMidiOutputIndex = 0;

        vm.SaveCommand.Execute(null);

        Assert.Equal(-1, config.AppConfig.MidiOutputDevice);
        Assert.Equal("Default MIDI Mapper", config.AppConfig.MidiOutputDeviceName);
    }

    private static void SetDeviceIds(AudioSettingsViewModel vm, params int[] ids)
    {
        var field = typeof(AudioSettingsViewModel)
            .GetField("_midiOutputDeviceIds",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        var list = (List<int>)field!.GetValue(vm)!;
        list.Clear();
        list.AddRange(ids);
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
    /// yet — posts are recorded and never run.</summary>
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
