// The modal Play Mode raises over a screen: the in-game settings modal (every section, its search, its meter polling).

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    /// <summary>
    ///     The settings modal (§6.5), section by section. Six panes plus the
    ///     search results are `IsVisible`-gated siblings inside ONE gated
    ///     overlay, so a fault on any of them is invisible until the user opens
    ///     that section — and a pane whose styles failed still ARRANGES, it just
    ///     has no size. Every one is switched to, laid out and measured.
    ///     <para>The view model here has no <c>AudioSettingsViewModel</c> and no
    ///     shell view model above it, so the Devices pane takes its no-engine
    ///     branch and every `$parent[Window]` path is unresolvable. Both must
    ///     DEGRADE rather than throw, which is exactly what this asserts.</para>
    /// </summary>
    [Fact]
    public async Task SettingsModal_laysOutEverySection_andSearchReplacesThePanes()
    {
        await WithView((view, vm) =>
        {
            vm.Settings.OpenGameplaySettingsCommand.Execute(null);
            view.UpdateLayout();

            // The three regions.
            AssertLaidOut(view, "GameplaySettingsOverlayCard");
            AssertLaidOut(view, "GameSettingsNav");
            AssertLaidOut(view, "GameSettingsResetButton");
            AssertLaidOut(view, "GameSettingsFullSettingsLink");

            var card = Find(view, "GameplaySettingsOverlayCard");
            Assert.Equal(1020, card.Bounds.Width, 1);
            Assert.Equal(660, card.Bounds.Height, 1);

            // The scrim is OPAQUE — a translucent one lets the app bleed
            // through, which shipped once.
            var scrim = card.GetVisualAncestors().OfType<Panel>()
                .First(p => p.Name == "GameplaySettingsOverlay")
                .GetVisualDescendants().OfType<Border>()
                .First(b => b.Classes.Contains("FireOverlayScrim"));
            Assert.NotNull(scrim.Background);
            Assert.True(scrim.Bounds.Width > 1 && scrim.Bounds.Height > 1);

            // Every section pane, one switch at a time.
            var panes = PlayModeView.GameplaySettingsPaneNames
                .Where(n => n != "GameSettingsResultsPane").ToList();
            for (var section = 0; section < panes.Count; section++)
            {
                vm.Settings.GameplaySettingsSection = section;
                view.UpdateLayout();

                AssertLaidOut(view, panes[section]);
                for (var other = 0; other < panes.Count; other++)
                {
                    if (other != section)
                    {
                        Assert.False(Find(view, panes[other]).IsVisible,
                            $"'{panes[other]}' is visible while section {section} is selected — " +
                            "two panes are stacked on top of each other");
                    }
                }

                // The header follows the rail rather than being restated per
                // pane, so it must never go blank on a switch.
                Assert.False(string.IsNullOrWhiteSpace(vm.Settings.GameplaySettingsSectionTitle));
                Assert.False(string.IsNullOrWhiteSpace(vm.Settings.GameplaySettingsSectionSubtitle));
            }

            // Audio: the live meter and the count-in segment really measure.
            vm.Settings.GameplaySettingsSection = 0;
            view.UpdateLayout();
            AssertLaidOut(view, "GameSettingsInputMeter");
            AssertLaidOut(view, "GameSettingsCountIn");

            // Devices with no audio service: the no-engine branch, not a row of
            // empty pickers.
            vm.Settings.GameplaySettingsSection = 1;
            view.UpdateLayout();
            AssertLaidOut(view, "GameSettingsDevicesPane");
            AssertLaidOut(view, "GameSettingsEngineSummary");

            // Search REPLACES the panes. The term is DERIVED from the running
            // culture's own catalogue, not the literal "volume" this used to
            // hard-code: `GameplaySettingsCatalog.Search` matches the LOCALIZED
            // title/description/section, so an English needle finds nothing
            // under ja-JP and the whole assertion below inverted — the results
            // pane was correctly hidden and the test read it as a broken pane.
            // (Found 2026-08-28 running the suite with
            // DOTCHARTER_HEADLESS_CULTURE=ja-JP.)
            var firstRowTitle = LocalizationService.Instance[GameplaySettingsCatalog.All[0].TitleKey];
            Assert.False(string.IsNullOrWhiteSpace(firstRowTitle),
                "the first catalogue row has no localized title — the search term below would be empty");
            vm.Settings.GameplaySettingsQuery = firstRowTitle;
            // Typing coalesces into a Background-priority pass (P1 #16c); a
            // keystroke's results land on the next dispatcher turn.
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            view.UpdateLayout();
            AssertLaidOut(view, "GameSettingsResultsPane");
            Assert.NotEmpty(vm.Settings.GameplaySettingsResults);
            // `IsVisible` is the LOCAL value; the panes are hidden wholesale by
            // their parent while a query is active, which only
            // `IsEffectivelyVisible` sees.
            Assert.All(PlayModeView.GameplaySettingsPaneNames.Where(n => n != "GameSettingsResultsPane"),
                n => Assert.False(Find(view, n).IsEffectivelyVisible,
                    $"'{n}' is still visible while a search is active — the results paint over live controls"));

            // …and "matched nothing" is its own state with the action that fixes it.
            vm.Settings.GameplaySettingsQuery = "qzxjvw";
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            view.UpdateLayout();
            AssertLaidOut(view, "GameSettingsNoResults");
            Assert.Empty(vm.Settings.GameplaySettingsResults);

            vm.Settings.ClearGameplaySettingsSearchCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            view.UpdateLayout();
            AssertLaidOut(view, "GameSettingsDevicesPane");

            // Every action in the modal DOES something. A RadioButton counts
            // through its command; nothing here is decoration.
            var overlay = card.GetVisualAncestors().OfType<Panel>()
                .First(p => p.Name == "GameplaySettingsOverlay");
            // Exact type only: a Slider's PART_DecreaseButton and a ScrollBar's
            // arrows are RepeatButtons that a template owns, not actions a
            // screen authored.
            foreach (var button in overlay.GetVisualDescendants().OfType<Button>()
                         .Where(b => b.GetType() == typeof(Button))
                         .Where(b => !(b.Name ?? "").StartsWith("PART_", StringComparison.Ordinal)))
            {
                Assert.True(button.Command != null,
                    $"a button in the settings modal ('{button.Name ?? button.Content?.ToString()}') has no command");
            }

            foreach (var radio in overlay.GetVisualDescendants().OfType<RadioButton>())
            {
                Assert.True(radio.Command != null,
                    "a rail row / segment in the settings modal switches nothing — that is the exact " +
                    "fault the front door's practice queue shipped with");
            }

            vm.Settings.CloseGameplaySettingsCommand.Execute(null);
            view.UpdateLayout();
        });
    }

    /// <summary>
    ///     The modal's live meter clock: it ticks while the modal is open and
    ///     STOPS when it closes. A Play Mode surface may not leave a clock
    ///     running behind the editor, and this one reads the audio capture ring
    ///     on every tick. Asserted on the poll COUNTER rather than on wall time
    ///     — the view's timer is driven here by calling the same entry point it
    ///     calls, so nothing in the default path is a sleep.
    ///     <para>The CLOCK itself is the view's, and since #658 the open flag it
    ///     follows is announced on <c>PlayModeViewModel.Settings</c>, not on Play
    ///     Mode — so the view must listen THERE. Asserted on the timer's state,
    ///     never on a tick.</para>
    /// </summary>
    [Fact]
    public async Task SettingsModal_pollsItsMetersOnlyWhileOpen()
    {
        await WithView((view, vm) =>
        {
            Assert.False(view.IsMeterClockRunning, "the meter clock runs with the modal closed");
            vm.Settings.OpenGameplaySettingsCommand.Execute(null);
            view.UpdateLayout();
            Assert.True(view.IsMeterClockRunning,
                "the modal opened and the view's meter clock did not start — it is not listening to Settings");

            // Opening polls once so the meter and the chip are populated in the
            // FIRST frame rather than 50 ms later.
            var afterOpen = vm.Settings.GameplaySettingsMeterPolls;
            Assert.True(afterOpen >= 1, "opening the modal did not seed the meters");

            vm.Settings.PollGameplaySettingsMeters();
            Assert.Equal(afterOpen + 1, vm.Settings.GameplaySettingsMeterPolls);

            vm.Settings.CloseGameplaySettingsCommand.Execute(null);
            view.UpdateLayout();
            Assert.False(view.IsMeterClockRunning, "the meter clock outlived the modal");

            var afterClose = vm.Settings.GameplaySettingsMeterPolls;
            vm.Settings.PollGameplaySettingsMeters();
            Assert.Equal(afterClose, vm.Settings.GameplaySettingsMeterPolls);
        });
    }
}
