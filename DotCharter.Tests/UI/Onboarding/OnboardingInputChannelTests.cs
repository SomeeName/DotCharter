using DotCharter.Tone.Audio.Abstractions;
using DotCharter.UI.ViewModels.Onboarding;

namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     The first-run Input page on an interface whose guitar is not on input 1:
///     a picked jack is metered at once, "Find my guitar" picks the input that
///     moves and saves it through the fields Settings writes, on ASIO and WASAPI
///     alike, and a WASAPI device with two inputs or fewer says the other inputs
///     may be other capture devices.
/// </summary>
/// <remarks>Joins the serialized culture collection: the rig's wizard writes the
/// process-global localization culture (see <c>AudioSettingsJackPickerTests</c>).</remarks>
[Collection("LocalizationSingleton")]
public sealed class OnboardingInputChannelTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    };

    private static OnboardingWizardRig AsioRig(int jacks) => InputRig("ASIO", jacks);

    private static OnboardingWizardRig WasapiRig(int jacks) => InputRig("WASAPI", jacks);

    private static OnboardingWizardRig InputRig(string driver, int jacks)
    {
        var rig = new OnboardingWizardRig();
        rig.Config.AppConfig.EnableInput = true;
        rig.Config.AppConfig.AudioDriver = driver;
        rig.Config.AppConfig.InputDriver = driver;
        rig.Engine.InputDeviceChannelCount = jacks;
        rig.Vm.Open();
        rig.Advance(3);           // Library, Audio, then Input
        rig.SettleAudioProbe();
        Assert.True(rig.Vm.IsInput);
        rig.AudioSettings.RefreshInputJackOptions(jacks);
        return rig;
    }

    private static GuitarJackFinder Finder(OnboardingWizardRig rig, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(rig.Vm, FakeLocalization.English(), delay ?? NoWait);

    [Fact]
    public void PickingAJack_OnTheInputPage_AppliesItAtOnce()
    {
        var rig = AsioRig(jacks: 4);
        var applied = rig.Engine.AppliedConfigs.Count;

        rig.AudioSettings.SelectedInputJackIndex = 2;

        Assert.Equal(2, rig.Config.AppConfig.AsioInputChannelLeft);
        Assert.True(rig.Engine.AppliedConfigs.Count > applied, "the pick never reached the engine, so the meter kept reading the old jack");
        Assert.Equal(2, rig.Engine.AppliedConfigs[^1].AsioInputChannelLeft);
    }

    [Fact]
    public void PickingAJack_OnAnotherPage_WaitsForTheInputPagesApply()
    {
        var rig = AsioRig(jacks: 4);
        rig.Vm.Step = OnboardingStep.Audio;
        var applied = rig.Engine.AppliedConfigs.Count;

        rig.AudioSettings.SelectedInputJackIndex = 2;

        Assert.Equal(applied, rig.Engine.AppliedConfigs.Count);
    }

    [Fact]
    public async Task FindMyGuitar_PicksTheInputThatMoves_AndSavesItLikeSettings()
    {
        var rig = AsioRig(jacks: 20);
        rig.Engine.InputCaptureFor = c => c is { AsioInputChannelLeft: 5, AsioInputMono: true } ? [0.4f, -0.3f] : [0.0004f];
        var finder = Finder(rig);

        await finder.FindCommand.ExecuteAsync(null);

        Assert.Equal(5, rig.Config.AppConfig.AsioInputChannelLeft);
        Assert.True(rig.Config.AppConfig.AsioInputMono);
        Assert.Equal(5, rig.Engine.AppliedConfigs[^1].AsioInputChannelLeft);
        Assert.Equal(5, rig.AudioSettings.InputJackOptions[rig.AudioSettings.SelectedInputJackIndex].Jack);
        Assert.False(finder.HeardNothing);
        Assert.Contains(rig.AudioSettings.InputJackOptions[5].Label, finder.Status, StringComparison.Ordinal);
        Assert.False(finder.IsRunning);
    }

    [Fact]
    public async Task FindMyGuitar_HearingNothing_PutsTheJackBack_AndSaysSo()
    {
        var rig = AsioRig(jacks: 4);
        rig.AudioSettings.SelectedInputJackIndex = 1;
        rig.Engine.InputCaptureFor = _ => [];
        var finder = Finder(rig);

        await finder.FindCommand.ExecuteAsync(null);

        Assert.True(finder.HeardNothing);
        Assert.Equal(1, rig.Config.AppConfig.AsioInputChannelLeft);
        Assert.Equal("No input heard your guitar. Check the cable, the guitar's volume knob and the interface's gain, then try again.",
            finder.Status);
    }

    [Fact]
    public async Task FindMyGuitar_Cancelled_PutsTheJackBack()
    {
        var rig = AsioRig(jacks: 4);
        rig.AudioSettings.SelectedInputJackIndex = 3;
        using var cts = new CancellationTokenSource();
        var listens = 0;
        var finder = Finder(rig, (_, _) =>
        {
            if (++listens == 3) cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await finder.FindCommand.ExecuteAsync(cts.Token);

        Assert.Equal(3, rig.Config.AppConfig.AsioInputChannelLeft);
        Assert.Equal(string.Empty, finder.Status);
        Assert.False(finder.IsRunning);
    }

    [Fact]
    public void FindMyGuitar_NeedsInputAndSeveralJacks_OnEitherDriver()
    {
        var off = AsioRig(jacks: 4);
        off.AudioSettings.EnableInput = false;
        Assert.False(Finder(off).FindCommand.CanExecute(null));

        Assert.True(Finder(AsioRig(jacks: 4)).FindCommand.CanExecute(null));
        Assert.True(Finder(WasapiRig(jacks: 20)).FindCommand.CanExecute(null));
    }

    [Fact]
    public void AWasapiDevice_ListsEveryInput_AndPickingInputTwoWritesWhatSettingsWrites()
    {
        var rig = WasapiRig(jacks: 20);
        var audio = rig.AudioSettings;

        Assert.Equal(Enumerable.Range(0, 20).ToArray(), audio.InputJackOptions.Select(o => o.Jack).ToArray());
        audio.SelectedInputJackIndex = 1;

        Assert.Equal(1, rig.Config.AppConfig.AsioInputChannelLeft);
        var applied = rig.Engine.AppliedConfigs[^1];
        Assert.Equal("WASAPI", applied.AudioDriver);
        Assert.Equal(1, applied.AsioInputChannelLeft);
        // The engine opens jack 1 alone, and seat 0 reads it: one writer of the layout.
        Assert.Equal(0b10u, GuitarInputChannels.InputChannelMask(applied));
        Assert.Equal(LiveInputSeatLayout.FromInputConfig(rig.Config.AppConfig).Channels[0],
            LiveInputSeatLayout.FromInputConfig(applied).Channels[0]);
    }

    [Fact]
    public async Task FindMyGuitar_OnWasapi_PicksTheInputThatMoves()
    {
        var rig = WasapiRig(jacks: 20);
        rig.Engine.InputCaptureFor = c => c is { AsioInputChannelLeft: 7, AsioInputMono: true } ? [0.5f] : [0.0002f];

        await Finder(rig).FindCommand.ExecuteAsync(null);

        Assert.Equal(7, rig.Config.AppConfig.AsioInputChannelLeft);
        Assert.Equal(1u << 7, GuitarInputChannels.InputChannelMask(rig.Engine.AppliedConfigs[^1]));
    }

    [Fact]
    public async Task ChoosingAWasapiCaptureDevice_ReadsItsJackCount()
    {
        var rig = new OnboardingWizardRig();
        rig.Engine.WasapiInputDevices = ["Microphone (Realtek)", "Line In (UMC1820)"];
        rig.Engine.InputDeviceChannelCount = 20;
        rig.Vm.Open();
        rig.AudioSettings.Open();
        rig.SettleAudioProbe();
        rig.AudioSettings.SelectedDriver = "WASAPI";
        rig.AudioSettings.SelectedInputDriver = "WASAPI";
        while (rig.Engine.InputChannelCountProbes.TryDequeue(out _))
        {
        }

        rig.AudioSettings.SelectedWasapiInputIndex = rig.AudioSettings.WasapiInputDevicesList.IndexOf("Line In (UMC1820)");
        await rig.AudioSettings.JackCountProbe;

        Assert.Contains(("WASAPI", "Line In (UMC1820)"), rig.Engine.InputChannelCountProbes);
        Assert.Equal(20, rig.AudioSettings.InputDeviceJackCount);
    }

    [Theory]
    [InlineData(new[] { -70.0, -12.0, -30.0 }, 1)]
    [InlineData(new[] { -70.0, -50.0, double.NegativeInfinity }, null)]
    [InlineData(new[] { -44.0, -46.0 }, 0)]
    public void Loudest_IsTheTopPeakAboveTheFloor(double[] peaks, int? expected) =>
        Assert.Equal(expected, GuitarJackFinder.Loudest(peaks));

    [Fact]
    public void AWasapiDevice_WithTwoInputsOrFewer_SaysTheOthersMayBeOtherDevices()
    {
        var rig = new OnboardingWizardRig();
        var audio = rig.AudioSettings;
        audio.SelectedDriver = "WASAPI";
        audio.EnableInput = true;

        audio.RefreshInputJackOptions(2);
        Assert.True(audio.IsWasapiDevicesNoteShown);

        audio.RefreshInputJackOptions(20);
        Assert.False(audio.IsWasapiDevicesNoteShown);

        audio.RefreshInputJackOptions(2);
        audio.SelectedDriver = "ASIO";
        Assert.False(audio.IsWasapiDevicesNoteShown);
        audio.SelectedDriver = "WASAPI";
        audio.EnableInput = false;
        Assert.False(audio.IsWasapiDevicesNoteShown);
    }
}
