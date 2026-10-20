// The Amp & FX rig's gear browser for assistive tech (#857): a row of two tiles is presentation only, each tile its own named, focusable item.

using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    /// <summary>
    ///     <b>The gear browser's ROW is a layout trick; the TILE is the item (#857).</b> A screen reader named every
    ///     row after its data's type name and one click-able row stood for two gear models. Each realized row's
    ///     ListBoxItem is therefore Raw and not a tab stop, and each tile is a focusable Button whose automation
    ///     name is the gear's display name, and Enter / Space picks it exactly as a click does.
    /// </summary>
    [Fact]
    public async Task TheGearBrowser_EveryTileIsANamedFocusableItem_AndKeyboardPicksIt()
    {
        var catalogue = Enumerable.Range(0, 40)
            .Select(i => new DspModuleType($"gear{i:D3}", $"Gear {i:D3}",
            [
                new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f),
                new DspParamInfo("level", "Level", 0f, 1f, 0.5f)
            ]))
            .ToList();

        await WithView((view, vm) =>
        {
            var chain = new FakeDspChainService(catalogue);
            var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            vm.ToneRig.UseRigForTests(rig);
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();
            view.UpdateLayout();

            var palette = Find<ListBox>(view, "RigGearGroups");
            var items = palette.GetRealizedContainers().OfType<ListBoxItem>().ToList();
            Assert.NotEmpty(items); // anti-vacuity

            foreach (var item in items)
            {
                Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(item));
                Assert.False(item.Focusable, "a gear ROW is a tab stop");
                Assert.False(ControlAutomationPeer.CreatePeerForElement(item).IsControlElement(),
                    "a gear ROW is announced to screen readers");
            }

            var tiles = palette.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("FireGearTile")).ToList();
            Assert.True(tiles.Count >= 2, $"{tiles.Count} tiles realized");
            foreach (var tile in tiles)
            {
                var gear = Assert.IsType<RigGearItem>(tile.DataContext);
                var peer = ControlAutomationPeer.CreatePeerForElement(tile);
                Assert.True(peer.IsControlElement());
                Assert.True(tile.Focusable && peer.IsKeyboardFocusable(), $"{gear.DisplayName}: tile is not focusable");
                Assert.Equal(gear.DisplayName, peer.GetName());
                Assert.False(string.IsNullOrWhiteSpace(peer.GetName()));
            }

            // Keyboard: focus the SECOND tile of the first row and press Enter, then Space on another.
            var second = tiles[1];
            Assert.True(second.Focus(), "the tile took no focus");
            Assert.Null(vm.ToneRig.AmpFxRig!.SelectedGear);
            second.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            second.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
            Assert.Same(second.DataContext, vm.ToneRig.AmpFxRig.SelectedGear);

            var third = tiles[2];
            third.Focus();
            third.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            third.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space });
            Assert.Same(third.DataContext, vm.ToneRig.AmpFxRig.SelectedGear);
            Assert.False(((RigGearItem)second.DataContext!).IsSelected);
        });
    }
}
