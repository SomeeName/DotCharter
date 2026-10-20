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
            rig.AudioSettings.AsioInputChannelLeft = 1;
            rig.AudioSettings.RefreshInputJackOptions(4);
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
            });
        }, CancellationToken.None);
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
}
