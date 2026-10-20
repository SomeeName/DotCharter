using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The rig's keyboard and right-click, through the REAL view: a click on a unit
///     puts the keys on the rig, Del / Ctrl+Z / Ctrl+Y / Ctrl+C / Ctrl+V / Ctrl+D /
///     Ctrl+B reach the rig's commands, and a right-click on a unit opens its menu
///     with the shortcuts printed beside the items. The commands' own rules are
///     <c>UI/RigHistoryTests</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigShortcutsUiPerfSmokeTests
{
    private static readonly DspModuleType Drive = new("overdrive", "Overdrive", [new("drive", "Drive", 0, 1, 0.5f)]);
    private static readonly DspModuleType Delay = new("delay", "Delay", [new("mix", "Mix", 0, 1, 0.3f)]);

    [Fact]
    public Task TheKeys_ReachTheRig_AfterAClickOnAUnit() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (rig, view, window, clock) = Open();
            using var _ = rig;
            try
            {
                Click(window, CardOf(view, "delay"));
                Assert.True(view.IsKeyboardFocusWithin, "a click on a unit did not put the keys on the rig");
                Assert.Equal("delay", rig.SelectedNode?.ModuleTypeId);

                Press(window, PhysicalKey.Delete, RawInputModifiers.None, clock);
                Assert.Equal(["overdrive"], Ids(rig));
                Press(window, PhysicalKey.Z, RawInputModifiers.Control, clock);
                Assert.Equal(["overdrive", "delay"], Ids(rig));
                Press(window, PhysicalKey.Y, RawInputModifiers.Control, clock);
                Assert.Equal(["overdrive"], Ids(rig));
                Press(window, PhysicalKey.Z, RawInputModifiers.Control | RawInputModifiers.Shift, clock);
                Assert.Equal(["overdrive"], Ids(rig)); // Ctrl+Shift+Z is redo too: nothing left to redo

                Press(window, PhysicalKey.Z, RawInputModifiers.Control, clock);
                Click(window, CardOf(view, "overdrive"));
                Press(window, PhysicalKey.C, RawInputModifiers.Control, clock);
                Press(window, PhysicalKey.V, RawInputModifiers.Control, clock);
                Assert.Equal(["overdrive", "overdrive", "delay"], Ids(rig));
                Press(window, PhysicalKey.D, RawInputModifiers.Control, clock);
                Assert.Equal(4, rig.Nodes.Count);
                Press(window, PhysicalKey.B, RawInputModifiers.Control, clock);
                Assert.True(rig.SelectedNode?.Bypassed, "Ctrl+B did not switch the selected unit off");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task ARightClickOnAUnit_OpensItsMenu_WithTheShortcuts() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var (rig, view, window, _) = Open();
            using var _ = rig;
            try
            {
                var card = CardOf(view, "overdrive");
                var at = Centre(card, window);
                window.MouseDown(at, MouseButton.Right);
                window.MouseUp(at, MouseButton.Right);
                window.UpdateLayout();

                var menu = view.UnitMenu;
                Assert.NotNull(menu);
                Assert.True(menu.IsOpen, "a right-click on a unit opened no menu");
                Assert.Equal("overdrive", rig.SelectedNode?.ModuleTypeId);
                var items = menu.Items.OfType<MenuItem>().ToList();
                Assert.Contains(items, i => Equals(i.InputGesture, RigUnitContextMenu.Copy));
                Assert.Contains(items, i => Equals(i.InputGesture, RigUnitContextMenu.Delete));
                Assert.Contains(items, i => Equals(i.InputGesture, RigUnitContextMenu.Undo));

                // its items ARE the rig's commands, pointed at that unit
                var remove = items.Single(i => Equals(i.InputGesture, RigUnitContextMenu.Delete));
                remove.Command!.Execute(remove.CommandParameter);
                Assert.Equal(["delay"], Ids(rig));
                menu.Close();
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);

    private sealed class Clock
    {
        public long Now = 1_000_000;
    }

    private static (PedalboardViewModel, AmpFxRigView, Window, Clock) Open()
    {
        var chain = new FakeDspChainService(new[] { Drive, Delay });
        var rig = TestRigBuilders.Pedalboard(chain, new DspTonePresetService(chain, new FakeConfigurationService()), LocalizationService.Instance);
        var clock = new Clock();
        rig.HistoryClock = () => clock.Now;
        rig.AddGear(Drive);
        rig.AddGear(Delay);
        clock.Now += 10_000;
        rig.SelectNodeAt(-1);
        var view = new AmpFxRigView { DataContext = new AmpFxRigViewModel(rig) };
        var window = new Window { Width = 1600, Height = 900, Content = view };
        window.Show();
        for (var pass = 0; pass < 6; pass++) window.UpdateLayout();
        return (rig, view, window, clock);
    }

    private static Border CardOf(Control view, string moduleTypeId) =>
        view.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("FireUnitCard") && b.IsEffectivelyVisible
                        && b.DataContext is PedalNodeViewModel { ModuleTypeId: var id } && id == moduleTypeId);

    private static Point Centre(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 3), window)!.Value;

    private static void Click(Window window, Control control)
    {
        var at = Centre(control, window);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.UpdateLayout();
    }

    /// <summary>One key press, as its own undo step (the clock moves past a burst).</summary>
    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers, Clock clock)
    {
        clock.Now += 10_000;
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
    }

    private static List<string> Ids(PedalboardViewModel rig) => rig.Nodes.Select(n => n.ModuleTypeId).ToList();
}
