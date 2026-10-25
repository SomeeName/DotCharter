using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Settings ▸ Audio's output-pair picker (and the onboarding wizard's, which
///     binds the same rows). The row IS the left and right output channels:
///     picking a row writes <c>AsioOutputChannelLeft</c> / <c>Right</c>, an
///     external write to either moves the row, and Save persists exactly the
///     fields it always did.
/// </summary>
/// <remarks>Joins the serialized culture collection for the reason
/// <c>AudioSettingsJackPickerTests</c> does: the rig drives product code that
/// writes the process-global localization culture.</remarks>
[Collection("LocalizationSingleton")]
public sealed class AudioSettingsOutputPairPickerTests
{
    [Fact]
    public void Refresh_ListsHalfTheDevicesOutputs_WithTheConfiguredPairSelected()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.AsioOutputChannelLeft = 2;
        audio.AsioOutputChannelRight = 3;
        audio.RefreshOutputPairOptions(8);

        Assert.Equal(4, audio.OutputPairOptions.Count);
        Assert.Equal(1, audio.SelectedOutputPairIndex);
        Assert.Equal("Out 3 + 4", audio.OutputPairOptions[audio.SelectedOutputPairIndex].Label);
        Assert.Equal(8, audio.OutputDeviceChannelCount);
    }

    [Fact]
    public void PickingARow_WritesBothOutputChannels_WithoutResettingTheList()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.AsioOutputChannelRight = 1;
        audio.RefreshOutputPairOptions(8);

        audio.SelectedOutputPairIndex = 3;

        Assert.Equal(6, audio.AsioOutputChannelLeft);
        Assert.Equal(7, audio.AsioOutputChannelRight);
        Assert.Equal(4, audio.OutputPairOptions.Count);
        Assert.Equal(3, audio.SelectedOutputPairIndex);
    }

    [Fact]
    public void AMonoRow_WritesOneChannelTwice()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.AsioOutputChannelRight = 1;
        audio.RefreshOutputPairOptions(5);

        audio.SelectedOutputPairIndex = 2;

        Assert.Equal((4, 4), (audio.AsioOutputChannelLeft, audio.AsioOutputChannelRight));
    }

    [Fact]
    public void WritingTheChannels_MovesTheRow_AndGrowsTheListForAnUnlistedPair()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.AsioOutputChannelRight = 1;
        audio.RefreshOutputPairOptions(4);

        audio.AsioOutputChannelLeft = 2;
        audio.AsioOutputChannelRight = 3;
        Assert.Equal(1, audio.SelectedOutputPairIndex);

        audio.AsioOutputChannelLeft = 1;
        audio.AsioOutputChannelRight = 2;
        var shown = audio.OutputPairOptions[audio.SelectedOutputPairIndex];
        Assert.Equal((1, 2), (shown.Left, shown.Right));
        Assert.Equal(3, audio.OutputPairOptions.Count);
    }

    [Fact]
    public void Save_PersistsThePickedPair_ThroughTheSameFields_AndReappliesTheEngine()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.Open();
        // Open() enumerates devices in a Task.Run and posts an apply that rebuilds
        // the bound collections; Save and the pick below run on the test thread, so
        // wait for the probe first (see AudioSettingsJackPickerTests).
        rig.SettleAudioProbe();
        audio.RefreshOutputPairOptions(6);

        audio.SelectedOutputPairIndex = 2;
        audio.SaveCommand.Execute(null);

        Assert.Equal((4, 5), (rig.Config.AppConfig.AsioOutputChannelLeft, rig.Config.AppConfig.AsioOutputChannelRight));
        Assert.Contains(rig.Engine.AppliedConfigs, c => c is { AsioOutputChannelLeft: 4, AsioOutputChannelRight: 5 });
    }

    /// <summary>The footer's "N unsaved changes" counts the pick through the two channels it writes.</summary>
    [Fact]
    public void PickingADifferentPair_IsCountedAsUnsaved_OnBothChannels()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.Open();
        rig.SettleAudioProbe();
        audio.RefreshOutputPairOptions(8);
        Assert.Empty(audio.UnsavedSettings);

        audio.SelectedOutputPairIndex = 2;

        Assert.Contains(nameof(audio.AsioOutputChannelLeft), audio.UnsavedSettings);
        Assert.Contains(nameof(audio.AsioOutputChannelRight), audio.UnsavedSettings);
    }

    [Fact]
    public void Open_SeedsTheRowsBeforeTheDeviceProbeReturns()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AsioOutputChannelLeft = 4;
        rig.Config.AppConfig.AsioOutputChannelRight = 5;
        rig.AudioSettings.Open();
        // The synchronous half: never a blank row while the USB enumeration runs.
        var (count, selected) = rig.Ui.Read(() => (
            rig.AudioSettings.OutputPairOptions.Count,
            rig.AudioSettings.OutputPairOptions[rig.AudioSettings.SelectedOutputPairIndex]));
        Assert.NotEqual(0, count);
        Assert.Equal((4, 5), (selected.Left, selected.Right));
    }

    /// <summary>The device probe asks for the SELECTED output driver's playback device only, and
    /// the count that comes back is what the picker is built from. A probe whose result went
    /// nowhere would satisfy the first half alone.</summary>
    [Fact]
    public async Task Open_OnWasapi_ProbesTheSelectedPlaybackDevice_AndBuildsThePickerFromItsCount()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AudioDriver = "WASAPI";
        rig.Config.AppConfig.WasapiOutputDeviceName = "Speakers (Scarlett 18i20)";
        rig.Engine.WasapiOutputDevices = ["Speakers (Realtek)", "Speakers (Scarlett 18i20)"];
        rig.Engine.OutputDeviceChannelCount = 10;

        rig.AudioSettings.Open();
        await rig.AudioSettings.DeviceProbe;
        await rig.AudioSettings.OutputCountProbe;

        var probe = Assert.Single(rig.Engine.OutputChannelCountProbes);
        Assert.Equal(("WASAPI", "Speakers (Scarlett 18i20)"), (probe.Driver, probe.Device));
        Assert.Equal(5, rig.Ui.Read(() => rig.AudioSettings.OutputPairOptions.Count));
    }

    /// <summary>Another playback device means another count: choosing it rebuilds the rows.</summary>
    [Fact]
    public async Task ChoosingAnotherPlaybackDevice_RebuildsThePairRows()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AudioDriver = "WASAPI";
        rig.Engine.WasapiOutputDevices = ["Speakers (Realtek)", "Speakers (Scarlett 18i20)"];
        rig.Engine.OutputDeviceChannelCount = 2;
        rig.AudioSettings.Open();
        await rig.AudioSettings.DeviceProbe;
        await rig.AudioSettings.OutputCountProbe;
        Assert.Equal(1, rig.Ui.Read(() => rig.AudioSettings.OutputPairOptions.Count));

        rig.Engine.OutputDeviceChannelCount = 18;
        rig.AudioSettings.SelectedWasapiOutputIndex = 1;
        await rig.AudioSettings.OutputCountProbe;

        Assert.Equal(9, rig.Ui.Read(() => rig.AudioSettings.OutputPairOptions.Count));
        Assert.Equal(("WASAPI", "Speakers (Scarlett 18i20)"), rig.Engine.OutputChannelCountProbes.Last());
    }

    /// <summary>A count the engine could not report leaves the generic list rather than emptying the picker.</summary>
    [Fact]
    public async Task AnUnknownCount_KeepsTheGenericRows()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AudioDriver = "WASAPI";
        rig.Engine.WasapiOutputDevices = ["Speakers (Realtek)"];
        rig.Engine.OutputDeviceChannelCount = 0;

        rig.AudioSettings.Open();
        await rig.AudioSettings.DeviceProbe;
        await rig.AudioSettings.OutputCountProbe;

        Assert.Equal(OutputChannelPairs.FallbackOutputCount / 2, rig.Ui.Read(() => rig.AudioSettings.OutputPairOptions.Count));
    }
}
