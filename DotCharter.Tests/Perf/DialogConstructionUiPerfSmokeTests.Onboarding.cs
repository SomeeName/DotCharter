// DialogConstructionUiPerfSmokeTests.Onboarding: the first-run wizard seeded with its REAL view model, walked page by page — every page measures, the meter clock runs on the Input page only, and the primary flips to Finish on Done.

using Avalonia.Controls;
using DotCharter.Localization;
using DotCharter.UI.ViewModels.Onboarding;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>
    ///     The grammar cases lay the wizard out with NO DataContext, which shows
    ///     every page at once and proves the markup. This one seeds the real view
    ///     model (over the in-memory rig) and opens it the product way, so what
    ///     is measured is each page AS THE USER SEES IT: exactly one page
    ///     visible, its controls sized, the head caption counting up, the input
    ///     meter's <c>DispatcherTimer</c> running on the Input page and parked
    ///     everywhere else, and one primary that reads Next until the last page.
    /// </summary>
    [Fact]
    public async Task OnboardingWizard_EveryPageMeasures_AndTheMeterClockRunsOnlyOnInput()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            var view = new OnboardingWizardView { DataContext = rig.Vm };
            rig.Vm.Open();

            WithLaidOutDialog(view, () =>
            {
                var pageBodies = Descendants<StackPanel>(view)
                    .Where(p => p.Parent is StackPanel parent && parent.Parent is ScrollViewer)
                    .ToList();
                Assert.True(pageBodies.Count == OnboardingStepMachine.Order.Count,
                    $"expected one page StackPanel per step, found {pageBodies.Count}");

                foreach (var step in OnboardingStepMachine.Order)
                {
                    Assert.Equal(step, rig.Vm.Step);
                    // A page flipping IsVisible invalidates ITSELF and queues on
                    // the root's layout manager; re-measuring the view by hand
                    // with an unchanged constraint is a no-op for the parts that
                    // did not invalidate, so run the root's pass instead.
                    view.UpdateLayout();

                    var visible = pageBodies.Where(p => p.IsEffectivelyVisible).ToList();
                    Assert.True(visible.Count == 1,
                        $"on {step} {visible.Count} pages are visible — exactly one must be");
                    Assert.True(visible[0].Bounds.Width > 1 && visible[0].Bounds.Height > 1,
                        $"the {step} page laid out {visible[0].Bounds} — it measured nothing");

                    var caption = Descendants<TextBlock>(view).First(t => t.Classes.Contains("DialogContext"));
                    Assert.Contains((OnboardingStepMachine.Order.ToList().IndexOf(step) + 1).ToString(),
                        caption.Text ?? string.Empty);

                    Assert.Equal(step == OnboardingStep.Input, view.IsMeterRunning);

                    var primaries = Descendants<Button>(view)
                        .Where(b => b.Classes.Contains("Primary") && b.IsEffectivelyVisible).ToList();
                    Assert.Single(primaries);
                    Assert.True(primaries[0].Bounds.Width > 1, $"the primary on {step} rendered nothing");
                    var finish = LocalizationService.Instance["Onboarding_Finish"];
                    Assert.Equal(step == OnboardingStep.Done, string.Equals(primaries[0].Content as string, finish, StringComparison.Ordinal));

                    if (step != OnboardingStep.Done) rig.Vm.PrimaryCommand.Execute(null);
                }

                rig.Vm.PrimaryCommand.Execute(null);
                view.UpdateLayout();
                Assert.False(view.IsMeterRunning, "closing the wizard must park the meter clock");
                Assert.True(rig.Config.AppConfig.OnboardingCompleted);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The Input page's GUITAR JACK row (2026-08-30): over an ASIO config it
    ///     lays out with one row per jack and the configured jack selected, and
    ///     it reads the SAME rows Settings does. A hidden-by-binding row that
    ///     never measured is exactly how the page shipped without one.
    /// </summary>
    [Fact]
    public async Task OnboardingWizard_InputPage_OffersTheGuitarJack_OnAsio()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            rig.Config.AppConfig.EnableInput = true;
            rig.Config.AppConfig.AudioDriver = "ASIO";
            rig.Config.AppConfig.AsioInputChannelLeft = 1;
            var view = new OnboardingWizardView { DataContext = rig.Vm };
            rig.Vm.Open();
            // What the Audio page's own open does (driver + rows), without its
            // thread-pool device probe racing the layout below.
            rig.AudioSettings.SelectedDriver = "ASIO";
            rig.AudioSettings.EnableInput = true;
            rig.AudioSettings.AsioInputChannelLeft = 1;
            // The ASIO driver names its jacks; the rows carry the names after the number.
            rig.AudioSettings.RefreshInputJackOptions(4, ["Mic 1", "Hi-Z", "Line 3", "Line 4"]);
            rig.Vm.Step = OnboardingStep.Input;

            WithLaidOutDialog(view, () =>
            {
                view.UpdateLayout();
                var combo = Descendants<ComboBox>(view).Single(c => c.Name == "OnboardingInputJack");
                Assert.True(combo.IsEffectivelyVisible, "the jack row must show on ASIO");
                Assert.True(combo.Bounds.Width > 1 && combo.Bounds.Height > 1,
                    $"the jack picker laid out {combo.Bounds} - it measured nothing");
                Assert.True(combo.ItemCount >= 2, $"only {combo.ItemCount} jack rows");
                Assert.Equal(1, rig.AudioSettings.InputJackOptions[combo.SelectedIndex].Jack);
                Assert.Equal("Input 2 · Hi-Z", combo.SelectedItem?.ToString());

                var find = Descendants<Button>(view).Single(b => b.Name == "OnboardingFindGuitar");
                Assert.True(find.IsEffectivelyVisible && find.IsEffectivelyEnabled, "Find my guitar must be offered on ASIO with several inputs");
                Assert.True(find.Bounds is { Width: > 1, Height: > 1 }, $"Find my guitar laid out {find.Bounds}");
                Assert.False(Descendants<Border>(view).Single(b => b.Name == "OnboardingWasapiDevicesNote").IsEffectivelyVisible);
                CaptureOnboarding(view, "onboarding-input-asio.png");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A WASAPI capture device reporting 20 inputs: the engine masks WASAPI
    ///     inputs too, so the jack row lists all 20 and Find my guitar is offered,
    ///     and the separate-devices note stays hidden. The same page on a
    ///     two-input WASAPI device shows the note instead.
    /// </summary>
    [Fact]
    public async Task OnboardingWizard_InputPage_OnAManyInputWasapiDevice_OffersEveryJack()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            rig.Config.AppConfig.EnableInput = true;
            rig.Config.AppConfig.InputDriver = "WASAPI";
            rig.Config.AppConfig.WasapiInputDeviceName = "Line In (UMC1820)";
            rig.Engine.WasapiInputDevices = ["Line In (UMC1820)"];
            var view = new OnboardingWizardView { DataContext = rig.Vm };
            rig.Vm.Open();
            rig.AudioSettings.Open();
            rig.SettleAudioProbe();
            rig.AudioSettings.RefreshInputJackOptions(20);
            rig.Vm.Step = OnboardingStep.Input;

            WithLaidOutDialog(view, () =>
            {
                view.UpdateLayout();
                Assert.Equal("WASAPI", rig.AudioSettings.SelectedDriver);
                var combo = Descendants<ComboBox>(view).Single(c => c.Name == "OnboardingInputJack");
                Assert.True(combo.IsEffectivelyVisible, "the jack row must show on WASAPI");
                Assert.True(combo.Bounds is { Width: > 1, Height: > 1 }, $"the jack picker laid out {combo.Bounds}");
                Assert.Equal(20, combo.ItemCount);
                var find = Descendants<Button>(view).Single(b => b.Name == "OnboardingFindGuitar");
                Assert.True(find.IsEffectivelyVisible && find.IsEffectivelyEnabled, "Find my guitar must be offered on a 20-input WASAPI device");
                Assert.True(find.Bounds is { Width: > 1, Height: > 1 }, $"Find my guitar laid out {find.Bounds}");
                var note = Descendants<Border>(view).Single(b => b.Name == "OnboardingWasapiDevicesNote");
                Assert.False(note.IsEffectivelyVisible, "a device listing all 20 inputs needs no separate-devices note");
                CaptureOnboarding(view, "onboarding-input-wasapi-20.png");

                rig.AudioSettings.RefreshInputJackOptions(2);
                view.UpdateLayout();
                Assert.True(note.IsEffectivelyVisible, "a two-input WASAPI device must say other inputs may be other capture devices");
                Assert.True(note.Bounds is { Width: > 1, Height: > 1 }, $"the note laid out {note.Bounds}");
                CaptureOnboarding(view, "onboarding-input-wasapi-2.png");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The Audio output page's OUTPUT PAIR row: it lays out on WASAPI and on
    ///     ASIO alike with one row per pair of the device, the configured pair
    ///     selected and every row named, and a pick made in the page reaches the
    ///     view model. A row that stayed ASIO-only, or whose combo collapsed,
    ///     would build clean and measure nothing.
    /// </summary>
    [Theory]
    [InlineData("WASAPI")]
    [InlineData("ASIO")]
    public async Task OnboardingWizard_AudioPage_OffersTheOutputPair_OnEveryDriver(string driver)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            var config = rig.Config.AppConfig;
            config.AudioDriver = driver;
            config.AudioSampleRate = 48000;
            config.AudioBufferFrames = 256;
            config.WasapiOutputDeviceName = "Speakers (Realtek(R) Audio)";
            config.AsioOutputDeviceName = "Focusrite USB ASIO";
            config.AsioOutputChannelLeft = 2;
            config.AsioOutputChannelRight = 3;
            rig.Engine.WasapiOutputDevices = ["Speakers (Realtek(R) Audio)"];
            rig.Engine.AsioDevicesInstalled = ["Focusrite USB ASIO"];
            rig.Engine.AppliedConfigs.Add(new AppConfig { AudioDriver = driver });
            rig.Engine.OutputDeviceChannelCount = 6;
            // An ASIO driver names its outputs; JUCE's WASAPI device names every one
            // "Output channel N", which leaves the rows numbered.
            rig.Engine.OutputChannelNames = driver == "ASIO"
                ? ["Main L", "Main R", "Headphones L", "Headphones R", "Line 5", "Line 6"]
                : [.. Enumerable.Range(1, 6).Select(i => $"Output channel {i}")];
            rig.Vm.Open();
            // Walk to the Audio page the way a user does, and let its device probe land BEFORE the
            // view binds: the probe rewrites the bound lists on a pool thread.
            rig.Advance(2);
            Assert.True(rig.Vm.IsAudio);
            var view = new OnboardingWizardView { DataContext = rig.Vm };

            WithLaidOutDialog(view, () =>
            {
                view.UpdateLayout();
                var combo = Descendants<ComboBox>(view).Single(c => c.Name == "OnboardingOutputPair");
                // The fixture names every device, so no combo on the page may render blank.
                Assert.All(Descendants<ComboBox>(view).Where(c => c.IsEffectivelyVisible),
                    c => Assert.NotNull(c.SelectedItem));
                Assert.True(combo.IsEffectivelyVisible, $"the output pair row must show on {driver}");
                Assert.True(combo.Bounds is { Width: > 1, Height: > 1 }, $"the output pair picker laid out {combo.Bounds}");
                Assert.Equal(3, combo.ItemCount);
                Assert.Equal(1, combo.SelectedIndex);
                Assert.Equal(driver == "ASIO" ? "Out 3 + 4 · Headphones" : "Out 3 + 4", combo.SelectedItem?.ToString());
                CaptureOnboarding(view, $"onboarding-audio-{driver.ToLowerInvariant()}.png");

                combo.SelectedIndex = 2;
                Assert.Equal((4, 5), (rig.AudioSettings.AsioOutputChannelLeft, rig.AudioSettings.AsioOutputChannelRight));
            });
        }, CancellationToken.None);
    }

    private static void CaptureOnboarding(Control view, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir) || TopLevel.GetTopLevel(view) is not Window window) return;
        Directory.CreateDirectory(dir);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    /// <summary>
    ///     The latency wizard's judging step shows WHICH channel it listens on
    ///     and lets the player change it: over a two-channel ASIO config the
    ///     picker lays out with both rows and the guitar's seat selected.
    /// </summary>
    [Fact]
    public async Task LatencyWizard_JudgingStep_ShowsTheListeningChannelPicker()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            rig.Config.AppConfig.EnableInput = true;
            rig.Config.AppConfig.AudioDriver = "ASIO";
            rig.Config.AppConfig.AsioInputMono = false;
            var wizard = new DotCharter.Presentation.Screens.Dialogs.LatencyWizardViewModel(
                new DotCharter.Services.Audio.Calibration.CalibrationAudio(rig.Engine), rig.Config,
                new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeDevice(), LocalizationService.Instance,
                rig.Logger, new TestUiDispatcher());
            var view = new LatencyCalibrationWizardView { DataContext = wizard };
            wizard.GoToJudgingCommand.Execute(null);

            WithLaidOutDialog(view, () =>
            {
                view.UpdateLayout();
                var combo = Descendants<ComboBox>(view).Single(c => c.Name == "LatencyListeningChannel");
                Assert.True(combo.IsEffectivelyVisible, "the channel picker must show with channels open");
                Assert.True(combo.Bounds.Width > 1 && combo.Bounds.Height > 1,
                    $"the channel picker laid out {combo.Bounds} - it measured nothing");
                Assert.Equal(2, combo.ItemCount);
                Assert.Equal(wizard.SelectedListeningChannelIndex, combo.SelectedIndex);
                Assert.True(combo.IsEnabled, "not measuring => the channel may be changed");
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The Welcome page's String order: a charter sees both cards with their
    ///     six-string previews, sized; picking Tab reaches the view model; a
    ///     player does not see the choice.
    /// </summary>
    [Fact]
    public async Task OnboardingWelcome_TheStringOrderCards_MeasureAndPick()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            var view = new OnboardingWizardView { DataContext = rig.Vm };
            rig.Vm.Open();
            rig.Vm.IsCharter = true;

            WithLaidOutDialog(view, () =>
            {
                view.UpdateLayout();
                var group = Descendants<StackPanel>(view).Single(p => p.Name == "OnboardingStringOrder");
                Assert.True(group.IsEffectivelyVisible && group.Bounds.Height > 1, $"the String order laid out {group.Bounds}");
                var cards = Descendants<RadioButton>(view)
                    .Where(r => r.Name is "OnboardingStringOrderRocksmith" or "OnboardingStringOrderTab").ToList();
                Assert.Equal(2, cards.Count);
                foreach (var card in cards)
                    Assert.True(card.Bounds.Width > 1 && card.Bounds.Height > 1, $"{card.Name} laid out {card.Bounds}");
                var previews = Descendants<TextBlock>(view).Where(t => t.Classes.Contains("OnboardingStringPreview")).ToList();
                Assert.Equal(2, previews.Count);
                Assert.All(previews, t => Assert.True(t.Bounds.Height > 40, $"a string preview laid out {t.Bounds}"));
                NarrowWidthLayout.Capture((Window)TopLevel.GetTopLevel(view)!, "onboarding-string-order");

                cards.Single(c => c.Name == "OnboardingStringOrderTab").IsChecked = true;
                Assert.True(rig.Vm.IsTabStringOrder);

                rig.Vm.IsCharter = false;
                view.UpdateLayout();
                Assert.False(group.IsEffectivelyVisible, "a player is not asked how the editor's timeline reads");
            });
        }, CancellationToken.None);
    }
}
