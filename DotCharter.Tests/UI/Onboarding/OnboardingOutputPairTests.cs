namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     The first-run Audio output page's output pair: a picked pair is saved
///     through the fields Settings writes, reaches the engine, and the test click
///     that follows names it — so the user hears which pair is chosen.
/// </summary>
/// <remarks>Joins the serialized culture collection: the rig's wizard writes the
/// process-global localization culture (see <c>AudioSettingsJackPickerTests</c>).</remarks>
[Collection("LocalizationSingleton")]
public sealed class OnboardingOutputPairTests
{
    private static OnboardingWizardRig AudioPageRig(string driver, int outputs)
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AudioDriver = driver;
        rig.Engine.OutputDeviceChannelCount = outputs;
        rig.Vm.Open();
        rig.Advance(2);           // Library, then Audio
        Assert.True(rig.Vm.IsAudio);
        rig.AudioSettings.RefreshOutputPairOptions(outputs);
        return rig;
    }

    [Theory]
    [InlineData("WASAPI")]
    [InlineData("ASIO")]
    public void ThePairPickedOnTheAudioPage_IsPlayedBackByTheTestClick_AndSaved(string driver)
    {
        var rig = AudioPageRig(driver, outputs: 6);
        var applied = rig.Engine.AppliedConfigs.Count;

        rig.AudioSettings.SelectedOutputPairIndex = 2;
        rig.Vm.TestAudioCommand.Execute(null);

        Assert.Equal((4, 5), (rig.Config.AppConfig.AsioOutputChannelLeft, rig.Config.AppConfig.AsioOutputChannelRight));
        Assert.True(rig.Engine.AppliedConfigs.Count > applied, "the pick never reached the engine, so the click played on the old pair");
        Assert.Equal((4, 5), (rig.Engine.AppliedConfigs[^1].AsioOutputChannelLeft, rig.Engine.AppliedConfigs[^1].AsioOutputChannelRight));
        Assert.Contains(2, rig.Engine.ImmediateClicks);
        Assert.False(rig.Vm.AudioTestFailed);
        Assert.Contains("Out 5 + 6", rig.Vm.AudioTestStatus, StringComparison.Ordinal);
    }

    /// <summary>The same pick made in Settings and in the wizard writes the same config.</summary>
    [Fact]
    public void SettingsAndTheWizard_WriteTheSameConfig_ForTheSamePair()
    {
        var wizard = AudioPageRig("WASAPI", outputs: 8);
        wizard.AudioSettings.SelectedOutputPairIndex = 3;
        wizard.Vm.TestAudioCommand.Execute(null);

        var settings = new OnboardingWizardRig();
        settings.Engine.OutputDeviceChannelCount = 8;
        settings.AudioSettings.Open();
        settings.SettleAudioProbe();
        settings.AudioSettings.RefreshOutputPairOptions(8);
        settings.AudioSettings.SelectedOutputPairIndex = 3;
        settings.AudioSettings.SaveCommand.Execute(null);

        Assert.Equal((6, 7), (wizard.Config.AppConfig.AsioOutputChannelLeft, wizard.Config.AppConfig.AsioOutputChannelRight));
        Assert.Equal(
            (wizard.Config.AppConfig.AsioOutputChannelLeft, wizard.Config.AppConfig.AsioOutputChannelRight),
            (settings.Config.AppConfig.AsioOutputChannelLeft, settings.Config.AppConfig.AsioOutputChannelRight));
    }

    /// <summary>Leaving the Audio page with Next saves the pick too, even if the click was never played.</summary>
    [Fact]
    public void Next_SavesThePickedPair()
    {
        var rig = AudioPageRig("WASAPI", outputs: 4);

        rig.AudioSettings.SelectedOutputPairIndex = 1;
        rig.Advance();

        Assert.Equal((2, 3), (rig.Config.AppConfig.AsioOutputChannelLeft, rig.Config.AppConfig.AsioOutputChannelRight));
    }
}
