using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tests.UI;
using DotCharter.Tone.Audio.Vst;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Mixer ▸ MIDI Routing card's two design-D rows (#751, <see cref="ChartSoundRoutingView" />): Sound source and
///     Route instrument, through the real <see cref="InspectorView" /> at 530x370, 480x370 and 1100x450 in English,
///     German and Japanese. Both rows measure non-zero inside the pane and no segment's label overruns its segment. The
///     headless app has no synth, so the card is then staged over a transport on a fake synth with two seats: Song
///     tones (Route instrument disabled with its reason), My rig (live) and a loaded, sandboxed instrument; each state
///     shows exactly the reasons the view model gives and lays out the same way. Width-shaped, so this class is in the
///     release's de-DE / ja-JP layout filter.
/// </summary>
[Collection("HeadlessUI")]
public sealed class MixerSoundRoutingUiPerfSmokeTests
{
    private static readonly string[] SourceSegments =
        ["ChartSoundSourceSongTones", "ChartSoundSourceOwnRig", "ChartSoundSourceMyInstrument", "ChartSoundSourceInstrumentRig"];

    private static readonly string[] RouteSegments = ["ChartSoundRouteGuitar", "ChartSoundRouteBass", "ChartSoundRouteBoth"];

    [Theory]
    [InlineData("en-US", 530, 370)]
    [InlineData("en-US", 480, 370)]
    [InlineData("en-US", 1100, 450)]
    [InlineData("de-DE", 530, 370)]
    [InlineData("de-DE", 480, 370)]
    [InlineData("de-DE", 1100, 450)]
    [InlineData("ja-JP", 530, 370)]
    [InlineData("ja-JP", 480, 370)]
    [InlineData("ja-JP", 1100, 450)]
    public async Task SoundSource_AndRouteInstrument_LayOut_AndShowTheirReasons(string culture, int width, int height) =>
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
                window = NarrowWidthLayout.NewWindow(width, height, view);
                window.Show();
                vm.SelectedTabIndex = InspectorViewModel.MixerTabIndex;
                vm.MixerSections.ChooseCommand.Execute(MixerSection.Midi);
                NarrowWidthLayout.Settle(window);
                var mixer = view.GetVisualDescendants().OfType<MixerInspectorView>().Single();
                var routing = NarrowWidthLayout.Named<ChartSoundRoutingView>(mixer, "ChartSoundRouting");
                var card = routing.GetVisualAncestors().OfType<ColumnFlowCard>().First();
                NarrowWidthLayout.Named<ColumnFlowPanel>(mixer, "MixerMidiBody").ShowPage(card);
                NarrowWidthLayout.Settle(window);
                var where = $"{culture} @{width}x{height}";
                AssertRows(mixer, routing, where);
                NarrowWidthLayout.Capture(window, $"mixer-routing-{width}x{height}-{culture}");

                // Staged: the same view over a transport whose synth can play, with a bass seat.
                var audio = new FakeChartSynthAudio { TwoSeats = true };
                var live = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(),
                    services.GetRequiredService<ILocalizationService>());
                NarrowWidthLayout.Named<StackPanel>(mixer, "MidiRoutingRows").IsEnabled = true;
                routing.DataContext = live.SoundRouting;
                foreach (var (state, stage) in new (string, Action)[]
                         {
                             ("song-tones", () => { }),
                             ("own-rig", () => live.SoundRouting.IsOwnRig = true),
                             ("sandboxed-instrument", () =>
                             {
                                 audio.Sandboxed = true;
                                 live.SelectedInstrumentVst = new VstPluginInfo { Name = "Instrument", PluginPath = @"C:\VST3\Instrument.vst3" };
                                 live.RefreshChartSynthAvailability();
                             })
                         })
                {
                    stage();
                    NarrowWidthLayout.Settle(window);
                    AssertRows(mixer, routing, $"{state} {where}");
                    AssertState(routing, live.SoundRouting, $"{state} {where}");
                    NarrowWidthLayout.Capture(window, $"mixer-routing-{state}-{width}x{height}-{culture}");
                }

                Assert.True(live.SoundRouting.CanUseMyInstrument);
                Assert.False(live.SoundRouting.CanUseInstrumentThroughRig);
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

    private static void AssertRows(MixerInspectorView mixer, ChartSoundRoutingView routing, string where)
    {
        var card = routing.GetVisualAncestors().OfType<ColumnFlowCard>().First();
        var cardRight = card.TranslatePoint(new Point(card.Bounds.Width, 0), mixer)!.Value.X;
        Assert.True(cardRight <= mixer.Bounds.Width + 0.5, $"the Routing card ends at {cardRight:0.#} px, past the {mixer.Bounds.Width:0.#} px Mixer ({where})");
        foreach (var row in new[] { "ChartSoundSourceRow", "ChartSoundRouteRow" })
        {
            var panel = NarrowWidthLayout.Named<StackPanel>(routing, row);
            Assert.True(panel.IsEffectivelyVisible && panel.Bounds is { Width: > 1, Height: > 1 }, $"{row} laid out as {panel.Bounds} ({where})");
        }

        foreach (var name in SourceSegments.Concat(RouteSegments))
        {
            var segment = NarrowWidthLayout.Named<RadioButton>(routing, name);
            Assert.True(segment.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {segment.Bounds} ({where})");
            foreach (var label in segment.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && !string.IsNullOrEmpty(t.Text)))
            {
                var right = label.TranslatePoint(new Point(label.Bounds.Width, 0), segment)!.Value.X;
                var left = label.TranslatePoint(new Point(0, 0), segment)!.Value.X;
                NarrowWidthLayout.Report($"{name} '{label.Text}' {left:0.#}..{right:0.#} of {segment.Bounds.Width:0.#} ({where})");
                Assert.True(left >= -0.5 && right <= segment.Bounds.Width + 0.5,
                    $"{name}'s label '{label.Text}' spans {left:0.#}..{right:0.#} px in a {segment.Bounds.Width:0.#} px segment ({where})");
            }
        }
    }

    private static void AssertState(ChartSoundRoutingView routing, ChartSoundRoutingViewModel model, string where)
    {
        Assert.Equal(model.CanUseMyInstrument, NarrowWidthLayout.Named<RadioButton>(routing, "ChartSoundSourceMyInstrument").IsEnabled);
        Assert.Equal(model.CanUseInstrumentThroughRig, NarrowWidthLayout.Named<RadioButton>(routing, "ChartSoundSourceInstrumentRig").IsEnabled);
        Assert.Equal(model.CanChooseSeatRoute, NarrowWidthLayout.Named<UniformGrid>(routing, "ChartSoundRouteChoices").IsEnabled);
        foreach (var (name, reason) in new[]
                 {
                     ("ChartSoundSourceReason", model.SourceUnavailableReason),
                     ("ChartSoundRouteReason", model.SeatRouteUnavailableReason)
                 })
        {
            var line = NarrowWidthLayout.Named<TextBlock>(routing, name);
            Assert.Equal(reason.Length > 0, line.IsVisible);
            if (reason.Length == 0) continue;
            Assert.Equal(reason, line.Text);
            Assert.True(line.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {line.Bounds} ({where})");
        }
    }
}
