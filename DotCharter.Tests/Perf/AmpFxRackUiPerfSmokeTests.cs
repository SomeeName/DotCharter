using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.RigLab;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.Editor;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.PlayMode;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.Views.Shell.Popovers;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The shared plugin rack (<see cref="VstRackView" />) in every host that
///     draws the guitar VST3 chain — DotCharter's Amp/FX panel on its own, Play
///     Mode's Amp/FX overlay, the Tools popover, and RigLab's Plugins tab — over a
///     REALISTIC chain: four plugins, one name far longer than a row, one still
///     loading, one bypassed, and two seats (so every row carries a seat picker).
/// </summary>
/// <remarks>
///     <para>
///         An empty chain lays out beautifully and proves nothing about a row:
///         the overlap and truncation bugs this rack could have (a seat picker
///         shoving the GUI button out of a 420 px popover, a long name pushing
///         the pill off the card) only exist with rows in it. Each case asserts
///         the rows REALIZED, measured non-zero and stayed inside their host.
///     </para>
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///         <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, each host is also written as a
///         PNG for a design review; the assertions run either way.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class AmpFxRackUiPerfSmokeTests
{
    private static readonly (string Name, bool Bypass, bool Loading)[] Plugins =
    [
        ("TS-9 Tube Screamer", false, false),
        ("Neural DSP Archetype: Plini X — Clean Channel Edition", false, false),
        ("Valhalla VintageVerb", true, false),
        ("FabFilter Pro-Q 3", false, true)
    ];

    private static readonly RigSeatOption[] TwoSeats = [new(0, "Player 1"), new(1, "Player 2")];

    [Fact]
    public Task TheAmpFxPanel_LaysOutAFullChain_OverAFreshTransport() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), NewState(),
                new FakeConfigurationService(), FakeLocalization.KeysOnly(), null, new FakeAppLogger());
            using var _ = Seed(transport);

            var panel = new AmpFxPanel { DataContext = transport };
            var window = Show(panel, 600, 1000);
            try
            {
                var sections = panel.GetVisualDescendants().OfType<Border>()
                    .Where(b => b.Classes.Contains("FirePanel")).ToList();
                Assert.Equal(3, sections.Count);
                Assert.All(sections, s => AssertMeasured(s, "an Amp/FX section card"));

                AssertRack(Find(panel, "AmpFxVstRack"), panel, "the Amp/FX panel");

                // The per-seat drop tune (ABI v31): offered with two seats, beside
                // the semitone picker, both inside the card.
                var seat = Find(panel, "DropTuneSeatPicker");
                Assert.True(seat.IsEffectivelyVisible, "two seats, and the drop-tune seat picker is hidden");
                AssertMeasured(seat, "the drop-tune seat picker");
                AssertInside(seat, Find(panel, "AmpFxDropTuneSection"), "the drop-tune seat picker");
                AssertInside(Find(panel, "AmpFxDropTunePicker"), Find(panel, "AmpFxDropTuneSection"), "the drop-tune picker");

                // The same value the rig footer's pedal drives.
                transport.InputPitchShiftSemis = -2;
                window.UpdateLayout();
                Assert.Equal(-2, Assert.IsType<ComboBox>(Find(panel, "AmpFxDropTunePicker")).SelectedItem);

                Capture(window, "ampfx-panel.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task PlayModesOverlay_HostsThePanel_InAFireModal() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            using var _ = Seed(shell.Transport);

            var overlay = new AmpFxOverlayView { DataContext = shell.PlayMode };
            var window = new Window { Width = 1600, Height = 1000, DataContext = shell, Content = overlay };
            window.Show();
            try
            {
                Settle(window);
                var card = Find(overlay, "AmpFxOverlayCard");
                AssertMeasured(card, "the overlay card");
                Assert.True(card.Classes.Contains("FireOverlayCard"), "the overlay card is not the Fire modal card");
                AssertMeasured(Find(overlay, "AmpFxOverlayClose"), "the overlay's close button");

                var panel = Assert.IsType<AmpFxPanel>(Find(overlay, "AmpFxOverlayPanel"));
                Assert.Same(shell.Transport, panel.DataContext);
                AssertRack(Find(panel, "AmpFxVstRack"), card, "Play Mode's Amp/FX overlay");

                Capture(window, "ampfx-overlay.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheToolsPopover_HostsTheSameRack() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = Shell();
            using var _ = Seed(shell.Transport);

            // Through a real FlyoutPresenter at the popover's 420 px, which is
            // what the app puts it in (see PopoverPixelUiPerfSmokeTests).
            var view = new ToolsPopoverView { DataContext = shell };
            var presenter = new FlyoutPresenter { Content = view };
            var host = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x08, 0x07, 0x0E)),
                Padding = new Thickness(24),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Child = presenter
            };
            var window = new Window
            {
                Width = 468, Height = 1100, Content = host,
                Background = new SolidColorBrush(Color.FromRgb(0x08, 0x07, 0x0E))
            };
            window.Show();
            try
            {
                Settle(window);
                // Trimmed to the card, so the capture is the popover.
                host.Measure(new Size(468, double.PositiveInfinity));
                window.Height = Math.Ceiling(host.DesiredSize.Height);
                Settle(window);
                var popover = view.GetSelfAndVisualDescendants().OfType<DotCharter.Tone.UI.Controls.FirePopover>().First();
                Assert.Equal(420, popover.Bounds.Width, 1);
                AssertRack(Find(view, "ToolsVstRack"), popover, "the Tools popover");
                Capture(window, "tools-popover.png");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task RigLabsPluginsTab_DrawsTheSharedRack() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "ampfx-rack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using var config = new RigLabConfigService(Path.Combine(dir, RigLabConfigService.FileName), TimeSpan.Zero);
            using var rack = new VstRackViewModel(new FakeAudioPlaybackService(), config, new GatedInlineDispatcher());
            var seats = new ObservableCollection<RigSeatOption>(TwoSeats);
            foreach (var slot in NewSlots(0)) { rack.Chain.Add(slot); slot.AttachSeats(seats); }

            var view = new PluginsView { DataContext = rack };
            var window = Show(view, 1440, 900);
            try
            {
                AssertRack(Find(view, "RigLabVstRack"), view, "RigLab's Plugins tab");
                Capture(window, "riglab-plugins-rack.png");
            }
            finally
            {
                window.Close();
                BestEffortDelete.Directory(dir);
            }
        }, TestContext.Current.CancellationToken);

    // ── the rack's own assertions, the same in every host ──────────────────

    private static void AssertRack(Control rack, Control host, string where)
    {
        Assert.IsType<VstRackView>(rack);
        AssertMeasured(rack, $"the rack in {where}");

        var rows = rack.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("FirePopoverSlot")).ToList();
        Assert.True(rows.Count == Plugins.Length,
            $"{where}: {rows.Count} chain rows realized for {Plugins.Length} plugins");

        foreach (var row in rows)
        {
            AssertMeasured(row, $"a chain row in {where}");
            AssertInside(row, host, $"a chain row in {where}");

            // Every cell of the row is inside the ROW: a seat picker or a long
            // name that pushed the pill or GUI off the edge still "measures".
            var name = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("FirePopoverSlotName"));
            Assert.True(name.Bounds.Width > 40, $"{where}: a plugin name was squeezed to {name.Bounds.Width:F0} px");
            var seat = row.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "VstRackSeatPicker");
            Assert.True(seat.IsEffectivelyVisible, $"{where}: two seats, and a row's seat picker is hidden");
            AssertMeasured(seat, $"a row's seat picker in {where}");
            AssertInside(seat, row, $"a row's seat picker in {where}");
            var pill = row.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("FirePopoverStatePill"));
            AssertInside(pill, row, $"a row's ON/BYPASS pill in {where}");
            var gui = row.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("FirePopoverIcon"));
            AssertInside(gui, row, $"a row's GUI button in {where}");

            // The remove is on screen WITHOUT a hover. A remove that appeared on
            // pointer-over pushed GUI aside and took its place under the pointer,
            // so a click aimed at GUI removed the plugin (owner, 2026-09-29).
            var remove = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("FirePopoverRemove"));
            Assert.True(remove.IsEffectivelyVisible, $"{where}: a row's remove button only appears on hover");
            AssertMeasured(remove, $"a row's remove button in {where}");
            AssertInside(remove, row, $"a row's remove button in {where}");
            Assert.True(remove.TranslatePoint(new Point(0, 0), row)!.Value.X
                        >= gui.TranslatePoint(new Point(gui.Bounds.Width, 0), row)!.Value.X - 0.5,
                $"{where}: the remove button overlaps the GUI button");
        }

        var loading = rows.Count(r => r.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.Classes.Contains("FirePopoverMeta") && t.IsEffectivelyVisible));
        Assert.True(loading == 1, $"{where}: {loading} rows say 'loading…', one plugin is loading");

        AssertMeasured(Find(rack, "VstRackAddButton"), $"the add button in {where}");
        Assert.False(Find(rack, "VstRackEmpty").IsEffectivelyVisible, $"{where}: the empty well shows over a full chain");
    }

    // ── fixtures ──────────────────────────────────────────────────────────

    /// <summary>Puts <see cref="Plugins" /> in the transport's chain with two
    /// seats, and hands back what undoes it — the shell's transport is a DI
    /// singleton other headless cases share.</summary>
    private static IDisposable Seed(TransportViewModel transport)
    {
        var before = transport.GuitarVstChain.ToList();
        var seatsBefore = transport.VstSeats.ToList();
        transport.GuitarVstChain.Clear();
        foreach (var slot in NewSlots(0)) transport.GuitarVstChain.Add(slot);
        transport.SetVstSeats(TwoSeats);
        return new Undo(() =>
        {
            transport.GuitarVstChain.Clear();
            foreach (var slot in before) transport.GuitarVstChain.Add(slot);
            transport.SetVstSeats(seatsBefore);
        });
    }

    private static IEnumerable<VstChainSlotViewModel> NewSlots(int start) =>
        Plugins.Select((p, i) => new VstChainSlotViewModel(Guid.NewGuid(), p.Name,
            $@"C:\Program Files\Common Files\VST3\{p.Name.Replace(':', ' ')}.vst3", p.Bypass, player: i % 2)
        {
            IsLoading = p.Loading,
            DisplayIndex = start + i + 1
        }).ToList();

    private static MainWindowViewModel Shell() =>
        (DotCharter.UI.App.Services ?? throw new InvalidOperationException("the headless app session did not boot."))
        .GetRequiredService<MainWindowViewModel>();

    private static ProjectState NewState()
    {
        var state = new ProjectState();
        var project = new ChartProject();
        var lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        lead.Levels.Add(new ChartLevel());
        project.Arrangements.Add(lead);
        state.ActiveProject = project;
        state.ActiveArrangement = lead;
        return state;
    }

    private static Window Show(Control content, double width, double height)
    {
        // The Fire ground, so a capture shows the panel on what it sits on
        // rather than on the headless window's white.
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

    /// <summary>Bounds are parent-relative, so this translates into the
    /// ancestor's space before comparing (1 px for hairline borders).</summary>
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
        var culture = Environment.GetEnvironmentVariable("DOTCHARTER_HEADLESS_CULTURE");
        var name = string.IsNullOrWhiteSpace(culture) ? file : Path.GetFileNameWithoutExtension(file) + "." + culture + ".png";
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, name), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
