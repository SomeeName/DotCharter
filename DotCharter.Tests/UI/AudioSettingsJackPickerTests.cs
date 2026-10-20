using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.UI;

/// <summary>
///     Settings ▸ Audio's guitar-jack picker (and the onboarding wizard's, which
///     binds the same rows). The row IS the left box: picking a row writes
///     <c>AsioInputChannelLeft</c>, an external write to the box moves the row,
///     and Save persists exactly the field it always did.
/// </summary>
/// <remarks>Joins the serialized culture collection: the rig drives
/// <c>OnboardingWizardViewModel.CommitWelcome</c>, which writes the
/// process-global <c>LocalizationService.Instance</c> through
/// <c>SetConfiguredCulture</c>. A culture write through PRODUCT code races
/// exactly as a direct one does — see <c>CultureCollectionLintTests</c>.</remarks>
[Collection("LocalizationSingleton")]
public sealed class AudioSettingsJackPickerTests
{
    [Fact]
    public void Refresh_ListsTheDevicesJacks_WithTheLeftBoxSelected()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.AsioInputChannelLeft = 1;
        audio.AsioInputChannelRight = 0;
        audio.RefreshInputJackOptions(4);

        Assert.Equal([0, 1, 2, 3], audio.InputJackOptions.Select(o => o.Jack).ToArray());
        Assert.Equal(1, audio.SelectedInputJackIndex);
        Assert.Equal(GuitarInputChannels.JackLabel(FakeLocalization.English(), 1), audio.InputJackOptions[1].Label);
    }

    [Fact]
    public void PickingARow_WritesTheLeftBox_AndSwapsACollidingRightBox()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.AsioInputMono = false;
        audio.AsioInputChannelLeft = 0;
        audio.AsioInputChannelRight = 1;
        audio.RefreshInputJackOptions(2);

        audio.SelectedInputJackIndex = 1;

        Assert.Equal(1, audio.AsioInputChannelLeft);
        Assert.Equal(0, audio.AsioInputChannelRight);
    }

    [Fact]
    public void WritingTheLeftBox_MovesTheRow_AndGrowsTheListForAnUnlistedJack()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.RefreshInputJackOptions(2);

        audio.AsioInputChannelLeft = 1;
        Assert.Equal(1, audio.SelectedInputJackIndex);

        audio.AsioInputChannelLeft = 7;
        Assert.Contains(audio.InputJackOptions, o => o.Jack == 7);
        Assert.Equal(7, audio.InputJackOptions[audio.SelectedInputJackIndex].Jack);
    }

    [Fact]
    public void Save_PersistsThePickedJack_ThroughTheSameField_AndReappliesTheEngine()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.EnableInput = true;
        rig.Config.AppConfig.AudioDriver = "ASIO";
        var audio = rig.AudioSettings;
        audio.Open();
        // The InlineDispatcher's lock covers the probe's APPLY against anything
        // that also goes through the dispatcher — and nothing below does. Open()
        // enumerates devices in a Task.Run and posts an apply that Clear()s and
        // refills four bound ObservableCollections and rewrites the selected
        // indexes; RefreshInputJackOptions, the row pick and Save all run
        // straight on the test thread. So the probe landed in the middle of
        // Save's indexing under load, either throwing out of it or overwriting
        // the jack this test had just picked. This test CAN wait, and the rig's
        // own rule is that anything which can, does.
        rig.SettleAudioProbe();
        audio.RefreshInputJackOptions(4);

        audio.SelectedInputJackIndex = 2;
        audio.SaveCommand.Execute(null);

        Assert.Equal(2, rig.Config.AppConfig.AsioInputChannelLeft);
        Assert.Contains(rig.Engine.AppliedConfigs, c => c.AsioInputChannelLeft == 2);
    }

    [Fact]
    public void Open_SeedsTheRowsBeforeTheDeviceProbeReturns()
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.AsioInputChannelLeft = 3;
        rig.AudioSettings.Open();
        // The synchronous half: never a blank row while the USB enumeration runs.
        // Read through the rig's dispatcher — the probe applies its result inline
        // on the POOL, so a bare enumeration here raced its Clear/refill and
        // threw "Collection was modified" in the gate.
        var (count, selectedJack) = rig.Ui.Read(() => (
            rig.AudioSettings.InputJackOptions.Count,
            rig.AudioSettings.InputJackOptions[rig.AudioSettings.SelectedInputJackIndex].Jack));
        Assert.NotEqual(0, count);
        Assert.Equal(3, selectedJack);
    }
}
