using DotCharter.Tone.UI.ViewModels.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     The driver's channel names reach every picker that lists channels: the
///     output pair in Settings ▸ Audio and the onboarding wizard (one set of rows),
///     the guitar-input jack in the same two places, and the rig footer's jack
///     picker. Each reads the names with its count, off the UI thread, and a
///     name that only repeats the number leaves the row numbered.
/// </summary>
/// <remarks>Joins the serialized culture collection for the reason
/// <c>AudioSettingsJackPickerTests</c> does.</remarks>
[Collection("LocalizationSingleton")]
public sealed class ChannelNamePickerTests
{
    private static readonly string[] AsioOutputs =
        ["Main L", "Main R", "Headphones L", "Headphones R", "Line 5", "Line 6"];

    private static readonly string[] AsioInputs = ["Mic 1", "Hi-Z", "Line 3", "Input channel 4"];

    [Fact]
    public async Task TheOutputPairProbe_LabelsTheRowsWithTheDriversNames()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AudioDriver = "WASAPI";
        rig.Config.AppConfig.AsioOutputChannelLeft = 2;
        rig.Config.AppConfig.AsioOutputChannelRight = 3;
        rig.Engine.WasapiOutputDevices = ["Speakers (Studio Interface)"];
        rig.Engine.OutputDeviceChannelCount = 6;
        rig.Engine.OutputChannelNames = AsioOutputs;

        rig.AudioSettings.Open();
        await rig.AudioSettings.DeviceProbe;
        await rig.AudioSettings.OutputCountProbe;

        var labels = rig.Ui.Read(() => rig.AudioSettings.OutputPairOptions.Select(o => o.Label).ToArray());
        Assert.Equal(["Out 1 + 2 · Main", "Out 3 + 4 · Headphones", "Out 5 + 6 · Line 5/6"], labels);
        var selected = rig.Ui.Read(() => rig.AudioSettings.OutputPairOptions[rig.AudioSettings.SelectedOutputPairIndex].Label);
        Assert.Equal("Out 3 + 4 · Headphones", selected);
    }

    /// <summary>A pick re-syncs the rows; the names must survive the rebuild rather than
    /// fall back to bare numbers until the next probe.</summary>
    [Fact]
    public async Task PickingAPair_KeepsTheNames()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AudioDriver = "WASAPI";
        rig.Engine.WasapiOutputDevices = ["Speakers (Studio Interface)"];
        rig.Engine.OutputDeviceChannelCount = 6;
        rig.Engine.OutputChannelNames = AsioOutputs;
        rig.AudioSettings.Open();
        await rig.AudioSettings.DeviceProbe;
        await rig.AudioSettings.OutputCountProbe;

        rig.Ui.Read(() =>
        {
            rig.AudioSettings.AsioOutputChannelLeft = 4;
            rig.AudioSettings.AsioOutputChannelRight = 5;
            return 0;
        });

        var selected = rig.Ui.Read(() => rig.AudioSettings.OutputPairOptions[rig.AudioSettings.SelectedOutputPairIndex].Label);
        Assert.Equal("Out 5 + 6 · Line 5/6", selected);
    }

    /// <summary>The input jacks of Settings and the wizard: the device load reads the names
    /// with the count, and a capture device chosen afterwards is read the same way.</summary>
    [Fact]
    public async Task TheJackRows_CarryTheDriversNames_OnLoadAndOnADeviceChange()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.EnableInput = true;
        rig.Config.AppConfig.AudioDriver = "WASAPI";
        rig.Config.AppConfig.InputDriver = "WASAPI";
        rig.Engine.WasapiInputDevices = ["Microphone (Realtek)", "Line In (Interface)"];
        rig.Engine.InputDeviceChannelCount = 4;
        rig.Engine.InputChannelNames = AsioInputs;

        rig.AudioSettings.Open();
        await rig.AudioSettings.DeviceProbe;
        var labels = rig.Ui.Read(() => rig.AudioSettings.InputJackOptions.Select(o => o.Label).ToArray());
        Assert.Equal(["Input 1 · Mic 1", "Input 2 · Hi-Z", "Input 3 · Line 3", "Input 4"], labels);

        rig.Engine.InputChannelNames = ["Guitar", "Bass"];
        rig.Engine.InputDeviceChannelCount = 2;
        rig.Ui.Read(() => rig.AudioSettings.SelectedWasapiInputIndex = 1);
        await rig.AudioSettings.JackCountProbe;

        labels = rig.Ui.Read(() => rig.AudioSettings.InputJackOptions.Select(o => o.Label).ToArray());
        Assert.Equal(["Input 1 · Guitar", "Input 2 · Bass"], labels);
    }

    [Fact]
    public void TheRigsJackPicker_CarriesTheDriversNames()
    {
        var audio = new FakeAudioPlaybackService { InputDeviceChannelCount = 4, InputChannelNames = AsioInputs };
        var config = new FakeConfigurationService();
        config.AppConfig.EnableInput = true;
        config.AppConfig.AudioDriver = "ASIO";
        config.AppConfig.AsioInputMono = true;
        config.AppConfig.AsioInputChannelLeft = 1;

        var vm = new LiveInputChannelViewModel(audio, config, FakeLocalization.English());

        Assert.Equal(["Input 1 · Mic 1", "Input 2 · Hi-Z", "Input 3 · Line 3", "Input 4"],
            vm.Options.Select(o => o.Label).ToArray());
        Assert.Equal("Input 2 · Hi-Z", vm.Selected!.Label);
    }
}
