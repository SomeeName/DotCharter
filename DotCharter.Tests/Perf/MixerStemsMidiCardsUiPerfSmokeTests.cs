using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using JuceSharp.AudioIo;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Inspector design D's Mixer ▸ Stems with a song that HAS stems, and the MIDI cards' resets: through the real
///     <see cref="InspectorView" /> over the app's own view models at 480x370 and 530x370 in English, German and
///     Japanese. Every Stems card lays out inside the pane (the Playback source on the Levels card), Reset levels asks
///     on its card before it acts, and each MIDI card's Reset shows its question row with a size. Width-shaped, so
///     this class is in the release's de-DE / ja-JP layout filter.
/// </summary>
[Collection("HeadlessUI")]
public sealed class MixerStemsMidiCardsUiPerfSmokeTests
{
    [Theory]
    [InlineData("en-US", 480)]
    [InlineData("en-US", 530)]
    [InlineData("de-DE", 480)]
    [InlineData("de-DE", 530)]
    [InlineData("ja-JP", 480)]
    [InlineData("ja-JP", 530)]
    public async Task TheStemsCards_AndTheMidiResets_LayOutAndAskFirst(string culture, int width) =>
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var vm = services.GetRequiredService<InspectorViewModel>();
            var stemMixer = services.GetRequiredService<StemMixerViewModel>();
            var state = services.GetRequiredService<ProjectState>();
            var localization = LocalizationService.Instance;
            var restore = localization.ConfiguredCulture;
            var previousTab = vm.SelectedTabIndex;
            var previousSection = vm.MixerSections.Section;
            var previousProject = state.ActiveProject;
            var dir = Directory.CreateTempSubdirectory("dotcharter-mixer-stems-ui-").FullName;
            Window? window = null;
            try
            {
                localization.SetConfiguredCulture(culture);
                NarrowWidthLayout.SeedSong(state);
                var project = state.ActiveProject!;
                project.Stems.Clear();
                foreach (var name in new[] { "drums", "bass", "vocals", "other" })
                {
                    var path = Path.Combine(dir, name + ".wav");
                    var quiet = new float[4410];
                    using (var writer = new WavWriter(path, 44100, 2, WavSampleFormat.Pcm16))
                    {
                        writer.WritePlanar([quiet, quiet]);
                    }

                    project.Stems.Add(new Stem { Name = name, Path = path, Volume = 1.0 });
                }

                stemMixer.Rebuild();
                var view = new InspectorView { DataContext = vm };
                window = NarrowWidthLayout.NewWindow(width, 370, view);
                window.Show();
                vm.SelectedTabIndex = InspectorViewModel.MixerTabIndex;
                vm.MixerSections.ChooseCommand.Execute(MixerSection.Stems);
                await WaitFor(window, () => stemMixer is { HasStems: true, IsRebuilding: false } && stemMixer.Tracks.Count >= 4,
                    "the mixer never loaded the seeded stems");
                var mixer = view.GetVisualDescendants().OfType<MixerInspectorView>().Single();

                // Stems: every card of the section fits; the Playback source and the strips are on the Levels card.
                var stems = NarrowWidthLayout.Named<ColumnFlowPanel>(mixer, "MixerStemsBody");
                Assert.False(NarrowWidthLayout.Named<StackPanel>(mixer, "StemNoStemsState").IsVisible);
                var source = NarrowWidthLayout.Named<ComboBox>(mixer, "StemPlaybackSource");
                ShowPageOf(source);
                NarrowWidthLayout.Settle(window);
                AssertFits(mixer, stems, culture, width);
                Assert.True(source.IsEnabled && source.Bounds is { Width: > 1, Height: > 1 }, $"the Playback source laid out as {source.Bounds}");
                Assert.Equal(StemPlaybackSource.Stems, ((StemPlaybackChoice)source.SelectedItem!).Source);
                NarrowWidthLayout.Capture(window, $"mixer-stems-{width}x370-{culture}");

                // Reset levels asks on its card first, and the faders stay until the answer.
                stemMixer.Tracks[0].VolumeLinear = 1.5;
                var reset = NarrowWidthLayout.Named<Button>(mixer, "StemResetLevels");
                reset.Command!.Execute(null);
                NarrowWidthLayout.Settle(window);
                AssertAsks(mixer, "StemResetLevelsConfirm");
                Assert.Equal(1.5, stemMixer.Tracks[0].VolumeLinear);
                NarrowWidthLayout.Capture(window, $"mixer-stems-reset-ask-{width}x370-{culture}");
                stemMixer.ResetLevelsConfirm.CancelCommand.Execute(null);

                // Design D's mini waveform on the Playback and roles card, with its caption.
                var strip = NarrowWidthLayout.Named<ColumnFlowStemWaveform>(mixer, "StemWaveform");
                ShowPageOf(strip);
                NarrowWidthLayout.Settle(window);
                Assert.True(strip.IsEffectivelyVisible && strip.Bounds is { Width: > 1, Height: > 1 }, $"the stem waveform laid out as {strip.Bounds}");
                Assert.False(string.IsNullOrWhiteSpace(NarrowWidthLayout.Named<TextBlock>(mixer, "StemWaveformCaption").Text));
                NarrowWidthLayout.Capture(window, $"mixer-stems-roles-{width}x370-{culture}");

                // The section's own cards: page through each one and check it fits.
                foreach (var card in stems.Children.OfType<ColumnFlowCard>())
                {
                    stems.ShowPage(card);
                    NarrowWidthLayout.Settle(window);
                    AssertFits(mixer, stems, culture, width);
                }

                // MIDI: each card's Reset asks on its card.
                vm.MixerSections.ChooseCommand.Execute(MixerSection.Midi);
                NarrowWidthLayout.Settle(window);
                var midi = NarrowWidthLayout.Named<ColumnFlowPanel>(mixer, "MixerMidiBody");
                var transport = services.GetRequiredService<TransportViewModel>();
                foreach (var (button, confirm, cancel) in new (string, string, Action)[]
                         {
                             ("MidiResetInstrument", "MidiResetInstrumentConfirm", () => transport.ChartSynthInstrument.ResetConfirm.Clear()),
                             ("MidiResetRouting", "MidiResetRoutingConfirm", () => transport.ResetRoutingConfirm.Clear())
                         })
                {
                    var control = NarrowWidthLayout.Named<Button>(mixer, button);
                    midi.ShowPage(control.GetVisualAncestors().OfType<ColumnFlowCard>().First());
                    NarrowWidthLayout.Settle(window);
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{button} laid out as {control.Bounds}");
                    control.Command!.Execute(null);
                    NarrowWidthLayout.Settle(window);
                    AssertAsks(mixer, confirm);
                    AssertFits(mixer, midi, culture, width);
                    NarrowWidthLayout.Capture(window, $"mixer-midi-{button}-{width}x370-{culture}");
                    cancel();
                }

                // Without the synth, Routing and Play style stay on screen, disabled, with the reason.
                Assert.Equal(transport.CanPlayChart, NarrowWidthLayout.Named<StackPanel>(mixer, "MidiRoutingRows").IsEnabled);
                Assert.Equal(!transport.CanPlayChart, NarrowWidthLayout.Named<TextBlock>(mixer, "MidiRoutingUnavailable").IsVisible);
                Assert.Equal(transport.CanPlayChart, NarrowWidthLayout.Named<Panel>(mixer, "MidiPlayStyleRows").IsEnabled);
            }
            finally
            {
                vm.MixerSections.ChooseCommand.Execute(previousSection);
                vm.SelectedTabIndex = previousTab;
                window?.Close();
                state.ActiveProject = previousProject;
                stemMixer.Rebuild();
                localization.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = localization.Culture;
            }
        }, CancellationToken.None);

    private static void ShowPageOf(Control control) =>
        control.GetVisualAncestors().OfType<ColumnFlowPanel>().First()
            .ShowPage(control.GetVisualAncestors().OfType<ColumnFlowCard>().First());

    private static void AssertAsks(MixerInspectorView mixer, string name)
    {
        var row = NarrowWidthLayout.Named<InspectorConfirmRow>(mixer, name);
        Assert.True(row.IsEffectivelyVisible && row.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {row.Bounds}");
        var yes = NarrowWidthLayout.Named<Button>(row, "ConfirmYes");
        Assert.True(yes.Bounds is { Width: > 1, Height: > 1 }, $"{name}'s Yes laid out as {yes.Bounds}");
        var sentence = NarrowWidthLayout.Named<TextBlock>(row, "ConfirmSentence");
        Assert.False(string.IsNullOrWhiteSpace(sentence.Text), $"{name} asks nothing");
    }

    private static void AssertFits(MixerInspectorView mixer, ColumnFlowPanel panel, string culture, int width)
    {
        Assert.NotEmpty(panel.ShownCards);
        foreach (var card in panel.ShownCards)
        {
            Assert.True(card.Bounds is { Width: > 1, Height: > 1 }, $"{card.Name} laid out as {card.Bounds} in {culture}");
            var right = card.TranslatePoint(new Point(card.Bounds.Width, 0), mixer)!.Value.X;
            Assert.True(right <= mixer.Bounds.Width + 0.5, $"{card.Name} ends at {right:0.#} px, past the {mixer.Bounds.Width:0.#} px Mixer tab ({culture} @{width})");
        }
    }

    private static async Task WaitFor(Window window, Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"Deadlock detector: {what}.");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        NarrowWidthLayout.Settle(window);
    }
}
