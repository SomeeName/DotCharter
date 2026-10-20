using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.Services.Audio.ChartSynth;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tests.UI;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor.Inspector;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The chart synth's instrument pickers (#50, <see cref="ChartSynthInstrumentView" />):
///     constructed, attached and laid out on their own and inside Play Mode's
///     Amp/FX panel (the editor's Mixer card hosts the same view and is built by
///     <c>InspectorConstructionUiPerfSmokeTests</c>). Over a transport whose synth
///     has the native table's shape: the four pickers realize with items, the
///     coil split shows for the HSS and hides for a Telecaster, and every picker
///     stays inside its host at the panel's narrowest width. The song-tone row
///     (#66): "Only for this tone" while a song tone plays, then the line naming
///     the tone and "Use my instrument", each laid out inside the view.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ChartSynthInstrumentUiPerfSmokeTests
{
    private static readonly string[] Pickers =
        ["ChartSynthGuitarModelPicker", "ChartSynthGuitarPickupPicker", "ChartSynthBassModelPicker", "ChartSynthBassPickupPicker"];

    [Fact]
    public Task ThePickers_LayOut_AndFollowTheModel() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (transport, audio) = Transport();
            var view = new ChartSynthInstrumentView { DataContext = transport.ChartSynthInstrument };
            var window = Show(view, 420, 400);
            try
            {
                AssertMeasured(Find(view, "ChartSynthInstrumentRoot"), "the instrument pickers");
                foreach (var name in Pickers)
                {
                    var box = Assert.IsType<ComboBox>(Find(view, name));
                    AssertMeasured(box, name);
                    AssertInside(box, view, name);
                    Assert.True(box.ItemCount > 0, $"{name} has no items");
                    Assert.NotNull(box.SelectedItem);
                }
                Assert.Equal(ChartSynthModel.StratHss, Selected<ChartSynthModel>(view, "ChartSynthGuitarModelPicker"));
                Assert.Equal(5, Assert.IsType<ComboBox>(Find(view, "ChartSynthGuitarPickupPicker")).ItemCount);
                var split = Find(view, "ChartSynthCoilSplitToggle");
                Assert.True(split.IsEffectivelyVisible, "the HSS has a humbucker but no coil split shows");
                AssertMeasured(split, "the coil split");
                AssertMeasured(Find(view, "ChartSynthGuitarSoundText"), "the guitar's recorded / simulated line");

                // Picking a Telecaster in the COMBO reaches the service and reshapes the pickers.
                var model = Assert.IsType<ComboBox>(Find(view, "ChartSynthGuitarModelPicker"));
                model.SelectedItem = transport.ChartSynthInstrument.GuitarModels.Single(c => c.Value == ChartSynthModel.Telecaster);
                Settle(window);
                Assert.Equal(ChartSynthModel.Telecaster, audio.GuitarInstrument.Model);
                Assert.Equal(3, Assert.IsType<ComboBox>(Find(view, "ChartSynthGuitarPickupPicker")).ItemCount);
                Assert.False(split.IsEffectivelyVisible, "a Telecaster has nothing to split, yet the coil split shows");
                Capture(window, "chart-synth-instrument.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheAmpFxPanel_HostsThePickers() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (transport, _) = Transport();
            var panel = new AmpFxPanel { DataContext = transport };
            var window = Show(panel, 600, 1200);
            try
            {
                var host = Find(panel, "AmpFxChartSynthInstrument");
                Assert.Same(transport.ChartSynthInstrument, host.DataContext);
                AssertMeasured(host, "the instrument pickers in the Amp/FX panel");
                var section = Find(panel, "AmpFxInstrumentSection");
                foreach (var name in Pickers)
                {
                    AssertMeasured(Find(panel, name), name);
                    AssertInside(Find(panel, name), section, name);
                }
                Capture(window, "chart-synth-instrument-ampfx.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     #89: picking another PICKUP in the combo (bridge → neck on the default
    ///     Strat HSS) closed the dropdown and stayed on Bridge. Wired the way the app
    ///     is — through the instrument coordinator — and checked on BOTH sides: the
    ///     combo must keep showing the pick, and the synth must play it.
    /// </summary>
    [Fact]
    public Task PickingAnotherPickup_InTheCombo_Sticks() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "dotcharter-synth-pickup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var audio = new FakeChartSynthAudio();
            var config = new FakeConfigurationService();
            var profiles = new PlayerProfileService(FakeLocalization.English(), filePath: Path.Combine(root, "player-profiles.json"));
            using var instruments = new ChartSynthInstrumentCoordinator(audio, profiles,
                new ProfileChartSynthInstrumentStore(profiles), config,
                new SongToneEditStore(Path.Combine(root, SongToneEditStore.FolderName)));
            instruments.Start();
            var transport = new TransportViewModel(audio, new ProjectState(), config, FakeLocalization.English(), new OnboardingWizardRig.InlineDispatcher(), chartSynthInstruments: instruments);
            transport.ChartSynthInstrument.LoadInfoForTest();
            var view = new ChartSynthInstrumentView { DataContext = transport.ChartSynthInstrument };
            var window = Show(view, 420, 400);
            try
            {
                var pickup = Assert.IsType<ComboBox>(Find(view, "ChartSynthGuitarPickupPicker"));
                Assert.Equal(ChartSynthPickup.Bridge, Selected<ChartSynthPickup>(view, "ChartSynthGuitarPickupPicker"));

                // The user picks Neck from the OPEN dropdown's own items.
                pickup.IsDropDownOpen = true;
                Settle(window);
                pickup.SelectedItem = pickup.Items.Cast<ChartSynthChoice<ChartSynthPickup>>()
                    .Single(c => c.Value == ChartSynthPickup.Neck);
                pickup.IsDropDownOpen = false;
                Settle(window);

                Assert.Equal(ChartSynthPickup.Neck, instruments.Effective(bass: false).Pickup);
                Assert.Equal(ChartSynthPickup.Neck, Selected<ChartSynthPickup>(view, "ChartSynthGuitarPickupPicker"));
            }
            finally
            {
                window.Close();
                BestEffortDelete.Directory(root);
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheToneOverrideRow_AppearsWithASongTone_AndLaysOut() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "dotcharter-synth-row-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var audio = new FakeChartSynthAudio();
            var config = new FakeConfigurationService();
            var profiles = new PlayerProfileService(FakeLocalization.English(), filePath: Path.Combine(root, "player-profiles.json"));
            using var instruments = new ChartSynthInstrumentCoordinator(audio, profiles,
                new ProfileChartSynthInstrumentStore(profiles), config,
                new SongToneEditStore(Path.Combine(root, SongToneEditStore.FolderName)));
            instruments.Start();
            var transport = new TransportViewModel(audio, new ProjectState(), config, FakeLocalization.English(), new OnboardingWizardRig.InlineDispatcher(), chartSynthInstruments: instruments);
            transport.ChartSynthInstrument.LoadInfoForTest();
            var view = new ChartSynthInstrumentView { DataContext = transport.ChartSynthInstrument };
            var window = Show(view, 420, 400);
            try
            {
                var offer = Find(view, "ChartSynthOverrideToneButton");
                var row = Find(view, "ChartSynthToneOverrideRow");
                Assert.False(offer.IsEffectivelyVisible, "no song tone plays, yet the override is offered");
                Assert.False(row.IsEffectivelyVisible);

                instruments.OnSongTone("lib:CallMe", "clean", "A Long Clean Tone Name From The Package");
                Settle(window);
                Assert.True(offer.IsEffectivelyVisible, "a song tone plays but \"Only for this tone\" is hidden");
                AssertMeasured(offer, "the \"Only for this tone\" button");
                AssertInside(offer, view, "the \"Only for this tone\" button");

                ((Button)offer).Command!.Execute(null);
                Settle(window);
                Assert.False(offer.IsEffectivelyVisible);
                Assert.True(row.IsEffectivelyVisible, "the tone has its own instrument but the row does not say so");
                var text = Assert.IsType<TextBlock>(Find(view, "ChartSynthToneOverrideText"));
                Assert.Contains("A Long Clean Tone Name", text.Text, StringComparison.Ordinal);
                AssertMeasured(text, "the line naming the tone");
                var mine = Find(view, "ChartSynthUseMyInstrumentButton");
                AssertMeasured(mine, "\"Use my instrument\"");
                AssertInside(mine, view, "\"Use my instrument\"");
                Capture(window, "chart-synth-instrument-tone-override.png");

                ((Button)mine).Command!.Execute(null);
                Settle(window);
                Assert.False(row.IsEffectivelyVisible);
                Assert.True(offer.IsEffectivelyVisible);
                instruments.Flush();
            }
            finally
            {
                window.Close();
                BestEffortDelete.Directory(root);
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task WithNoSynth_ThePickersAreDisabled_NotLive() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly());
            var view = new ChartSynthInstrumentView { DataContext = transport.ChartSynthInstrument };
            var window = Show(view, 420, 300);
            try
            {
                Assert.False(Find(view, "ChartSynthInstrumentRoot").IsEnabled);
                Assert.Equal(0, Assert.IsType<ComboBox>(Find(view, "ChartSynthGuitarModelPicker")).ItemCount);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    private static (TransportViewModel Transport, FakeChartSynthAudio Audio) Transport()
    {
        var audio = new FakeChartSynthAudio();
        var transport = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.English());
        transport.ChartSynthInstrument.LoadInfoForTest();
        return (transport, audio);
    }

    private static T Selected<T>(Control root, string name) where T : struct
        => Assert.IsType<ChartSynthChoice<T>>(Assert.IsType<ComboBox>(Find(root, name)).SelectedItem).Value;

    private static Window Show(Control content, double width, double height)
    {
        var window = new Window
        {
            Width = width, Height = height, Content = content,
            Background = new SolidColorBrush(Color.FromRgb(0x08, 0x07, 0x0E))
        };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
    }

    private static Control Find(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"no control named {name} — the view never realized it");

    private static void AssertMeasured(Control control, string what) =>
        Assert.True(control.Bounds is { Width: > 1, Height: > 1 },
            $"{what} laid out as {control.Bounds} — it rendered nothing");

    private static void AssertInside(Control child, Control ancestor, string what)
    {
        var topLeft = child.TranslatePoint(default, ancestor);
        var bottomRight = child.TranslatePoint(new Point(child.Bounds.Width, child.Bounds.Height), ancestor);
        Assert.True(topLeft.HasValue && bottomRight.HasValue, $"{what} is not under its host");
        Assert.True(topLeft!.Value.X >= -1 && bottomRight!.Value.X <= ancestor.Bounds.Width + 1,
            $"{what} spans x={topLeft.Value.X:F0}..{bottomRight!.Value.X:F0} in a {ancestor.Bounds.Width:F0} px host");
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
