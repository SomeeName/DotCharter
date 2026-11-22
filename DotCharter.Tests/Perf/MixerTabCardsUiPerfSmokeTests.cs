using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Inspector design D's Mixer tab: a Stems / EQ / MIDI section strip like the Note tab's over one section's
///     cards. Through the real <see cref="InspectorView" /> over the app's own view models, at 480x370 and 530x370 in
///     English, German and Japanese: every strip entry is named, a click on it shows that section alone, and every
///     card of the section that is on screen lays out with a size inside the pane (no card pushed past its right
///     edge). Width-shaped, so this class is in the release's de-DE / ja-JP layout filter.
/// </summary>
[Collection("HeadlessUI")]
public sealed class MixerTabCardsUiPerfSmokeTests
{
    [Theory]
    [InlineData("en-US", 480)]
    [InlineData("en-US", 530)]
    [InlineData("de-DE", 480)]
    [InlineData("de-DE", 530)]
    [InlineData("ja-JP", 480)]
    [InlineData("ja-JP", 530)]
    public async Task EverySection_OpensAlone_AndItsCardsFitThePane(string culture, int width) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var vm = services.GetRequiredService<InspectorViewModel>();
            var localization = LocalizationService.Instance;
            var restore = localization.ConfiguredCulture;
            var previousTab = vm.SelectedTabIndex;
            var previousSection = vm.MixerSections.Section;
            Window? window = null;
            try
            {
                localization.SetConfiguredCulture(culture);
                var view = new InspectorView { DataContext = vm };
                window = NarrowWidthLayout.NewWindow(width, 370, view);
                window.Show();
                vm.SelectedTabIndex = InspectorViewModel.MixerTabIndex;
                NarrowWidthLayout.Settle(window);
                var mixer = view.GetVisualDescendants().OfType<MixerInspectorView>().Single();
                Assert.True(mixer.Bounds is { Width: > 1, Height: > 1 }, $"the Mixer tab laid out as {mixer.Bounds}");

                var entries = new (MixerSection Section, string Entry, string Body)[]
                {
                    (MixerSection.Stems, "MixerSectionStemsEntry", "MixerStemsBody"),
                    (MixerSection.Eq, "MixerSectionEqEntry", "MixerEqBody"),
                    (MixerSection.Midi, "MixerSectionMidiEntry", "MixerMidiBody")
                };
                foreach (var (section, entryName, bodyName) in entries)
                {
                    var entry = NarrowWidthLayout.Named<ToggleButton>(mixer, entryName);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(entry)), $"{entryName} has no accessible name");
                    Assert.True(entry.IsEffectivelyVisible && entry.Bounds is { Width: > 1, Height: > 1 }, $"{entryName} laid out as {entry.Bounds}");

                    // what a click on the entry does: the lit one unchecks itself locally first
                    entry.IsChecked = false;
                    entry.Command!.Execute(entry.CommandParameter);
                    NarrowWidthLayout.Settle(window);
                    NarrowWidthLayout.Capture(window, $"mixer-{section}-{width}x370-{culture}");

                    Assert.Equal(section, vm.MixerSections.Section);
                    Assert.True(entry.IsChecked == true, $"{entryName} is not lit for its own section");
                    foreach (var body in mixer.Bodies)
                        Assert.Equal(body.Name == bodyName, body.IsVisible);

                    var shown = mixer.Bodies.Single(b => b.Name == bodyName);
                    Assert.True(shown.Bounds is { Width: > 1, Height: > 1 }, $"{bodyName} laid out as {shown.Bounds} in {culture}");
                    var panel = shown as ColumnFlowPanel ?? shown.GetVisualDescendants().OfType<ColumnFlowPanel>().First();
                    Assert.NotEmpty(panel.ShownCards);
                    foreach (var card in panel.ShownCards)
                    {
                        Assert.True(card.Bounds is { Width: > 1, Height: > 1 }, $"{card.Name} laid out as {card.Bounds} in {culture}");
                        var right = card.TranslatePoint(new Point(card.Bounds.Width, 0), mixer)!.Value.X;
                        Assert.True(right <= mixer.Bounds.Width + 0.5,
                            $"{card.Name} ends at {right:0.#} px, past the {mixer.Bounds.Width:0.#} px Mixer tab ({culture} @{width})");
                    }
                }

                // Design D offers Stems on a song without stems too: it says why and offers "Separate stems".
                var stemMixer = services.GetRequiredService<StemMixerViewModel>();
                vm.MixerSections.ChooseCommand.Execute(MixerSection.Stems);
                NarrowWidthLayout.Settle(window);
                var noStems = NarrowWidthLayout.Named<StackPanel>(mixer, "StemNoStemsState");
                Assert.Equal(!stemMixer.HasStems, noStems.IsVisible);
                // A plain window, as a floated pane is (no shell DataContext): the button still holds the shell's
                // separation command, and so is disabled while a run is in flight.
                var separateButton = NarrowWidthLayout.Named<Button>(mixer, "StemSeparateButton");
                Assert.IsNotType<MainWindowViewModel>(window.DataContext);
                Assert.Same(vm.StemSeparation.SeparateStemsCommand, separateButton.Command);
                if (!stemMixer.HasStems)
                {
                    var separate = NarrowWidthLayout.Named<Button>(mixer, "StemSeparateButton");
                    Assert.True(separate.IsEffectivelyVisible && separate.Bounds is { Width: > 1, Height: > 1 }, $"Separate stems laid out as {separate.Bounds}");
                    NarrowWidthLayout.Capture(window, $"mixer-nostems-{width}x370-{culture}");
                }
            }
            finally
            {
                vm.MixerSections.ChooseCommand.Execute(previousSection);
                vm.SelectedTabIndex = previousTab;
                window?.Close();
                localization.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = localization.Culture;
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The EQ's plot is a real control on the live module: on the Response card it lays out with a size, and the
    ///     Band card's picker and the stem strips' resets are reachable controls with names.
    /// </summary>
    [Fact]
    public async Task TheEqCards_HostTheRealPlot_AndThePicker() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var vm = services.GetRequiredService<InspectorViewModel>();
            var previousTab = vm.SelectedTabIndex;
            var previousSection = vm.MixerSections.Section;
            var view = new InspectorView { DataContext = vm };
            var window = NarrowWidthLayout.NewWindow(1100, 450, view);
            try
            {
                window.Show();
                vm.SelectedTabIndex = InspectorViewModel.MixerTabIndex;
                vm.MixerSections.ChooseCommand.Execute(MixerSection.Eq);
                NarrowWidthLayout.Settle(window);
                var mixer = view.GetVisualDescendants().OfType<MixerInspectorView>().Single();
                var plot = mixer.GetVisualDescendants().OfType<ColumnFlowEqPlot>().Single();
                Assert.True(plot.IsEffectivelyVisible && plot.Bounds is { Width: > 1, Height: > 1 }, $"the EQ plot laid out as {plot.Bounds}");
                var picker = NarrowWidthLayout.Named<ComboBox>(mixer, "EqBandPicker");
                Assert.True(picker.IsEffectivelyVisible && picker.Bounds is { Width: > 1, Height: > 1 }, $"the band picker laid out as {picker.Bounds}");
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(picker)));
                Assert.Equal(3, NarrowWidthLayout.Named<ColumnFlowPanel>(mixer, "EqCards").ShownCards.Count);
            }
            finally
            {
                vm.MixerSections.ChooseCommand.Execute(previousSection);
                vm.SelectedTabIndex = previousTab;
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     Design D's Band card at the paged width, en/de/ja: the restored chip row (a colour dot and a select chip per
    ///     band), the typed Frequency / Gain / Q fields and Reset gain + Q lay out with a size inside the pane; Reset asks
    ///     on the card; Solo is off while its band is off. The analyser polls only while the Response card is on the
    ///     page (a paged-away card is still "visible" to the visibility watcher).
    /// </summary>
    [Theory]
    [InlineData("en-US", 480)]
    [InlineData("de-DE", 480)]
    [InlineData("ja-JP", 530)]
    public async Task TheBandCard_LaysOutItsChipsAndFields_AndTheAnalyserFollowsThePage(string culture, int width) =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var vm = services.GetRequiredService<InspectorViewModel>();
            var localization = LocalizationService.Instance;
            var restore = localization.ConfiguredCulture;
            var previousTab = vm.SelectedTabIndex;
            var previousSection = vm.MixerSections.Section;
            Window? window = null;
            try
            {
                localization.SetConfiguredCulture(culture);
                var view = new InspectorView { DataContext = vm };
                window = NarrowWidthLayout.NewWindow(width, 370, view);
                window.Show();
                vm.SelectedTabIndex = InspectorViewModel.MixerTabIndex;
                vm.MixerSections.ChooseCommand.Execute(MixerSection.Eq);
                NarrowWidthLayout.Settle(window);
                var mixer = view.GetVisualDescendants().OfType<MixerInspectorView>().Single();
                var eq = mixer.GetVisualDescendants().OfType<EqModuleView>().Single();
                var cards = NarrowWidthLayout.Named<ColumnFlowPanel>(eq, "EqCards");
                var response = NarrowWidthLayout.Named<ColumnFlowCard>(eq, "EqResponseCard");
                var band = NarrowWidthLayout.Named<ColumnFlowCard>(eq, "EqBandCard");

                cards.ShowPage(band);
                NarrowWidthLayout.Settle(window);
                Assert.Contains(band, cards.ShownCards);
                Assert.Equal(cards.ShownCards.Contains(response), eq.IsPollingSpectrum);
                var chips = eq.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Name == "EqChipSelect").ToList();
                Assert.Equal(6, chips.Count);
                var named = new List<Control>(chips);
                named.AddRange(eq.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "EqChipPower"));
                named.AddRange(new[] { "EqFreqField", "EqQField" }.Select(n => NarrowWidthLayout.Named<TextBox>(eq, n)));
                named.Add(NarrowWidthLayout.Named<Button>(eq, "EqBandReset"));
                foreach (var control in named)
                {
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{control.Name} laid out as {control.Bounds} ({culture} @{width})");
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), $"{control.Name} has no accessible name");
                    var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), mixer)!.Value.X;
                    Assert.True(right <= mixer.Bounds.Width + 0.5, $"{control.Name} ends at {right:0.#} px, past the {mixer.Bounds.Width:0.#} px Mixer tab ({culture} @{width})");
                }

                var module = vm.Transport.Eq;
                Assert.Equal(module.SelectedBand.IsEnabled, NarrowWidthLayout.Named<ToggleButton>(eq, "EqBandSolo").IsEnabled);
                if (module.IsLive)
                {
                    module.Edits.ResetCommand.Execute(null);
                    NarrowWidthLayout.Settle(window);
                    var ask = NarrowWidthLayout.Named<InspectorConfirmRow>(eq, "EqResetConfirm");
                    Assert.True(ask.Bounds is { Width: > 1, Height: > 1 }, $"the reset question laid out as {ask.Bounds}");
                    NarrowWidthLayout.Capture(window, $"mixer-eq-band-ask-{width}x370-{culture}");
                    module.Edits.ResetConfirm.Clear();
                }
                else
                {
                    NarrowWidthLayout.Capture(window, $"mixer-eq-band-{width}x370-{culture}");
                }

                cards.ShowPage(response);
                NarrowWidthLayout.Settle(window);
                Assert.True(eq.IsPollingSpectrum, "the analyser does not poll with the Response card on the page");
            }
            finally
            {
                vm.MixerSections.ChooseCommand.Execute(previousSection);
                vm.SelectedTabIndex = previousTab;
                window?.Close();
                localization.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = localization.Culture;
            }
        }, CancellationToken.None);
}
