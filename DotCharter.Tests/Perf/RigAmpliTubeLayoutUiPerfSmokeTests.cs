using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Glyph = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The AmpliTube 5 layout of the rig screen (2026-09-27): the selected gear BIG
///     on the STAGE at the top, the GEAR BAR under it (power, ▲ / ▼, the name, the
///     DETAILS toggle), the whole chain small in the ROUTING MAP at the foot with the
///     routing RAIL on its left, and the gear browser's two-column thumbnail GRID on
///     the right. Every case lays the real screen out in a real window and asserts
///     MEASURED geometry — a region whose styles failed still arranges, at size 0.
///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set,
///     each case writes a PNG for review.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigAmpliTubeLayoutUiPerfSmokeTests
{
    [Fact]
    public Task TheStage_TheGearBar_AndTheMap_StackTopToBottom() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            using var rig = Rig(catalogue, "compressor", "rat", "namamp", "cab", "delay");
            rig.SelectNodeAt(rig.Nodes.IndexOf(rig.Nodes.Single(n => n.ModuleTypeId == "rat")));
            var model = new AmpFxRigViewModel(rig);
            Show(model, 1920, 1080, (window, view) =>
            {
                var stage = Box(view, "RigDockBody", window);
                var bar = Box(view, "RigDockBar", window);
                var map = Box(view, "RigBoard", window);
                var rail = Box(view, "RigRoutingRail", window);
                var browser = Box(view, "RigGearPanel", window);
                var footer = Box(view, "RigFooter", window);
                foreach (var (region, box) in new[] { ("stage", stage), ("bar", bar), ("map", map), ("rail", rail), ("browser", browser), ("footer", footer) })
                    Assert.True(box.Width > 1 && box.Height > 1, $"the {region} measured {box}");

                // top to bottom: stage, gear bar, (divider), map, footer
                Assert.True(stage.Bottom <= bar.Top + 1, $"the gear bar {bar} is not under the stage {stage}");
                Assert.InRange(map.Top - bar.Bottom, 0, 8);
                Assert.True(map.Bottom <= footer.Top + 1, $"the map {map} runs into the footer {footer}");
                // AmpliTube's proportions: the stage is the biggest area, the map the foot
                var body = map.Bottom - stage.Top;
                Assert.InRange((stage.Height + bar.Height) / body, 0.52, 0.72);
                Assert.InRange(map.Height / body, 0.22, 0.45);
                Assert.True(stage.Height > map.Height * 1.3, $"stage {stage.Height:0} vs map {map.Height:0}");

                // the rail stands left of the map, the browser right of both
                Assert.True(rail.Right <= map.Left + 1 && rail.Top < map.Bottom && rail.Bottom > map.Top,
                    $"the routing rail {rail} is not beside the map {map}");
                Assert.True(browser.Left >= map.Right - 1 && browser.Left >= stage.Right - 1,
                    $"the gear browser {browser} is not the right column");
                var modes = view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("FireMapMode")).ToList();
                Assert.Equal(3, modes.Count);
                Assert.All(modes, m => Assert.True(m.Bounds.Height >= 32 && m.Bounds.Width >= 32, $"a routing mode button {m.Bounds}"));
                Assert.All(modes, m => Assert.NotNull(Assert.Single(m.GetVisualDescendants().OfType<Glyph>()).Data));
                Assert.True(modes[0].IsChecked, "a series rig lights Series");

                // the gear stands BIG on the stage, the map's picture is a thumbnail
                var big = view.GetVisualDescendants().OfType<RigFaceplateView>().Single(f => f.IsInteractive && f.IsEffectivelyVisible);
                var fit = big.GetVisualAncestors().OfType<RigScaleToFit>().First();
                Assert.True(big.Bounds.Height * fit.Scale > stage.Height * 0.6,
                    $"the selected gear is {big.Bounds.Height * fit.Scale:0} px on a {stage.Height:0} px stage — not filling it");
                Assert.InRange(model.BoardFaceHeight, RigBoardFaceSize.Min, RigBoardFaceSize.Max);
                Assert.True(big.Bounds.Height * fit.Scale > model.BoardFaceHeight * 3, "the stage is not bigger than the map");

                // the gear bar names the unit and powers it
                var power = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "RigDockEnabled");
                Assert.True(power.IsChecked == true && power.Bounds.Width >= 30, $"the power button {power.Bounds}");
                var name = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RigGearBarName");
                Assert.Equal("Rodent Distortion", name.Text, ignoreCase: true);
                Assert.True(name.Bounds.Width > 40, $"the gear bar's name {name.Bounds}");

                // POWER bypasses the unit — and the map's picture follows
                var rat = rig.SelectedNode!;
                Click(window, power);
                Assert.True(rat.Bypassed, "the gear bar's power did not bypass the unit");
                Click(window, power);
                Assert.False(rat.Bypassed);

                // ▼ steps to the next unit in signal order
                Click(window, view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RigDockNext"));
                Assert.Equal("namamp", rig.SelectedNode?.ModuleTypeId);

                Capture(window, "layout-1920-series.png");
            });
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheDetailsToggle_OpensTheUnitsExtrasBesideTheStage() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            using var rig = Rig(catalogue, "overdrive", "namamp", "cab");
            rig.SelectNodeAt(1);
            var model = new AmpFxRigViewModel(rig, new RigBoardFaceplateUiPerfSmokeTests.SeededLibrary(40));
            Show(model, 1920, 1080, (window, view) =>
            {
                var editor = Assert.Single(view.GetVisualDescendants().OfType<RigModuleEditorView>());
                var rack = editor.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RigEditorRack");
                Assert.False(model.IsDetailsOpen);
                Assert.False(rack.IsEffectivelyVisible, "the details column is up before anyone asked for it");
                var mapBefore = Box(view, "RigBoard", window);
                var stageBefore = Box(view, "RigDockBody", window);
                var big = editor.GetVisualDescendants().OfType<RigFaceplateView>().Single(f => f.IsInteractive && f.IsEffectivelyVisible);
                var faceBefore = big.TranslatePoint(new Point(big.Bounds.Width, 0), window)!.Value.X;

                var toggle = view.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "RigDetailsToggle");
                Assert.True(toggle.Bounds.Width > 40 && toggle.Bounds.Height >= 28, $"the DETAILS toggle {toggle.Bounds}");
                Click(window, toggle);
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

                Assert.True(model.IsDetailsOpen && toggle.IsChecked == true, "the DETAILS toggle did not open the panel");
                Assert.True(rack.IsEffectivelyVisible, "the details column did not appear");
                var column = Box(rack, window);
                var stage = Box(view, "RigDockBody", window);
                Assert.True(column.Width >= 300, $"the details column measured {column}");
                // beside the gear, inside the stage — never over the map or the gear bar
                Assert.True(column.Left >= faceBefore - column.Width, "the details column is not beside the gear");
                Assert.True(column.Top >= stage.Top - 1 && column.Bottom <= stage.Bottom + 1,
                    $"the details column {column} leaves the stage {stage}");
                Assert.True(column.Right <= stage.Right + 1, $"the details column {column} runs past the stage {stage}");
                // …and the map did not move (the stage row is content-independent)
                Assert.Equal(mapBefore, Box(view, "RigBoard", window));
                Assert.Equal(stageBefore, stage);

                // the gear gave the column room and is still whole on the stage
                big = editor.GetVisualDescendants().OfType<RigFaceplateView>().Single(f => f.IsInteractive && f.IsEffectivelyVisible);
                var faceRight = big.TranslatePoint(new Point(big.Bounds.Width, 0), window)!.Value.X;
                Assert.True(faceRight <= column.Left + 1, $"the gear ({faceRight:0}) runs under the details column ({column.Left:0})");

                // what is in it: the capture library (seeded — an empty one hides every fault)…
                var captures = editor.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "RigCaptureList");
                Assert.True(captures.IsEffectivelyVisible && captures.Bounds.Height > 100, $"capture list {captures.Bounds}");
                var rows = captures.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("FireListRow")).ToList();
                Assert.Equal(40, rows.Count);
                Assert.All(rows, r => Assert.True(Box(r, window).Right <= column.Right + 0.5, $"a capture row {Box(r, window)} spills out of {column}"));
                // …every exact value, the unit's controllers & MIDI
                Assert.True(editor.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "RigUnitValues").IsEffectivelyVisible);
                Assert.True(editor.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "RigEditorControllers").IsEffectivelyVisible);

                Capture(window, "layout-1920-nam-details.png");

                // stepping to the next unit keeps the panel open (a standing choice)
                Click(window, view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "RigDockNext"));
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                Assert.Equal("cab", rig.SelectedNode?.ModuleTypeId);
                Assert.True(rack.IsEffectivelyVisible, "the details column closed when the unit changed");

                Click(window, toggle);
                window.UpdateLayout();
                Assert.False(rack.IsEffectivelyVisible, "the DETAILS toggle did not close the panel");
            });
        }, TestContext.Current.CancellationToken);

    [Fact]
    public Task TheGearBrowser_IsATwoColumnGridOfThumbnails_UnderIconTabs() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            using var rig = Rig(catalogue, "overdrive");
            var model = new AmpFxRigViewModel(rig);
            Show(model, 1920, 1080, (window, view) =>
            {
                var tabs = view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("FireGearCatTab")).ToList();
                Assert.Equal(model.CategoryFilters.Count, tabs.Count);
                foreach (var tab in tabs)
                {
                    var glyph = Assert.Single(tab.GetVisualDescendants().OfType<Glyph>());
                    Assert.True(glyph.Data is not null, $"the '{((RigCategoryFilter)tab.DataContext!).Label}' tab drew no icon");
                    Assert.True(glyph.Bounds.Width > 8 && tab.Bounds.Width >= 24, $"a category tab {tab.Bounds} / glyph {glyph.Bounds}");
                    Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(tab) as string), "a category tab has no label to show");
                }
                // one row, every tab inside the browser
                var browser = Box(view, "RigGearPanel", window);
                Assert.Single(tabs.Select(t => Math.Round(Box(t, window).Top)).Distinct());
                Assert.All(tabs, t => Assert.True(Box(t, window).Right <= browser.Right + 0.5, $"a tab {Box(t, window)} is outside {browser}"));

                // the grid: two tiles a row, each the gear's own faceplate with its name
                var tiles = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("FireGearTile") && b.IsEffectivelyVisible).ToList();
                Assert.True(tiles.Count >= 8, $"only {tiles.Count} gear tiles realized");
                var rowsOfTwo = tiles.GroupBy(t => Math.Round(Box(t, window).Top)).Count(g => g.Count() == 2);
                Assert.True(rowsOfTwo >= 3, $"only {rowsOfTwo} rows hold two tiles side by side");
                foreach (var tile in tiles)
                {
                    var box = Box(tile, window);
                    Assert.True(box.Width > 100 && box.Height > 90 && box.Right <= browser.Right + 0.5, $"a gear tile {box}");
                    var face = Assert.Single(tile.GetVisualDescendants().OfType<RigFaceplateView>());
                    Assert.False(face.IsInteractive);
                    Assert.True(face.Bounds.Width > 20 && face.Bounds.Height > 20, $"{face.ModuleTypeId}: its thumbnail measured {face.Bounds}");
                    var name = tile.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("FireGearTileName"));
                    Assert.True(name.Bounds.Width > 20, $"{face.ModuleTypeId}: its name measured {name.Bounds}");
                }

                // the IN RIG chip (the overdrive is on the rig) meets neither the + nor the name
                var inRig = tiles.Single(t => ((RigGearItem)t.DataContext!).ModuleTypeId == "overdrive");
                var chip = Box(inRig.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("FireGearTileBadge")), window);
                var plus = Box(inRig.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("FireGearTileAdd")), window);
                var label = Box(inRig.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("FireGearTileName")), window);
                Assert.True(chip.Width > 10 && chip.Height > 8, $"the IN RIG chip measured {chip}");
                Assert.False(chip.Intersects(plus), $"the IN RIG chip {chip} runs under the + {plus}");
                Assert.False(chip.Intersects(label), $"the IN RIG chip {chip} runs over the name {label}");

                // a tile's + adds the unit to the rig
                var before = rig.Nodes.Count;
                var add = tiles[1].GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("FireGearTileAdd"));
                Click(window, add);
                Assert.Equal(before + 1, rig.Nodes.Count);

                // an icon tab filters the grid to its identity
                var amps = tabs.Single(t => ((RigCategoryFilter)t.DataContext!).Category == FireUnitCategory.Amps);
                Click(window, amps);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                Assert.All(model.GearRows, r => Assert.Equal(FireUnitCategory.Amps, r.Category));
                Assert.True(amps.IsChecked, "the Amps tab did not light");
            });
        }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(1920, 1080, 2, "layout-1920-dual.png")]
    [InlineData(1280, 800, 3, "layout-1280-triple.png")]
    public Task ASplitRig_FitsTheMap(int width, int height, int lanes, string file) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            using var rig = Rig(catalogue, "overdrive", "splitter", "delay", "chorus", "reverb", "cab");
            var merger = rig.Nodes.First(n => n.IsMerger);
            if (lanes == 3) rig.RoutingSplit3Command.Execute(null);
            // the region's lanes, one unit each (the cab stays after the merger)
            var lane = 1;
            foreach (var id in new[] { "delay", "chorus", "reverb" }.Take(lanes))
            {
                var node = rig.Nodes.Single(n => n.ModuleTypeId == id);
                Assert.True(rig.DropToRow(node, rig.Nodes.IndexOf(merger), lane++));
            }
            rig.SelectNodeAt(0);
            var model = new AmpFxRigViewModel(rig);
            Show(model, width, height, (window, view) =>
            {
                Assert.Equal(lanes, rig.RoutingBranches);
                var map = Box(view, "RigBoard", window);
                var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "RigBoardScroll");
                var host = view.GetVisualDescendants().OfType<LayoutTransformControl>().Single(h => h.Name == "RigBoardZoomHost");
                var path = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RigBoardPath");
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"map {map}, scroll {Box(scroll, window)} viewport {scroll.Viewport} extent {scroll.Extent} offset {scroll.Offset}, " +
                    $"host {Box(host, window)} scale {(host.LayoutTransform as Avalonia.Media.ScaleTransform)?.ScaleX:0.000}, path {path.Bounds} at {Box(path, window)}");
                // the fitted chain is CENTRED on the map and ends inside it: a
                // LayoutTransformControl scales its child's content but not an aligned
                // child's offset, which once drew a 0.5-scaled chain half a map right
                var scrollBox = Box(scroll, window);
                // where the path is DRAWN (the transform included), not the host's box,
                // which stretches to the scroller whatever the path does inside it
                var pathLeft = path.TranslatePoint(new Point(0, 0), window)!.Value.X;
                var pathRight = path.TranslatePoint(new Point(path.Bounds.Width, 0), window)!.Value.X;
                Assert.InRange((pathLeft + pathRight) / 2 - scrollBox.Center.X, -6, 6);
                Assert.True(pathLeft >= scrollBox.Left - 1 && pathRight <= scrollBox.Right + 1,
                    $"the chain is drawn from {pathLeft:0} to {pathRight:0} on a map from {scrollBox.Left:0} to {scrollBox.Right:0}");
                var speaker = view.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "RigMapSpeaker");
                var speakerEnd = speaker.TranslatePoint(new Point(speaker.Bounds.Width, speaker.Bounds.Height / 2), window)!.Value;
                Assert.True(speakerEnd.X <= scrollBox.Right, $"the map's speaker ends at {speakerEnd.X:0}, past the map ({scrollBox.Right:0})");
                // the whole region is ON the map: no scrollbar on either axis
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1,
                    $"the map scrolls sideways: extent {scroll.Extent.Width:0} in {scroll.Viewport.Width:0}");
                Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 1,
                    $"the map scrolls vertically: extent {scroll.Extent.Height:0} in {scroll.Viewport.Height:0}");

                var laneRows = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("FireLaneRow")).ToList();
                Assert.Equal(lanes, laneRows.Count);
                foreach (var row in laneRows)
                {
                    var box = Box(row, window);
                    Assert.True(box.Height > 20 && box.Top >= map.Top - 1 && box.Bottom <= map.Bottom + 1, $"a lane {box} outside the map {map}");
                }
                foreach (var card in view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("FireUnitCard")))
                {
                    var face = Assert.Single(card.GetVisualDescendants().OfType<RigFaceplateView>());
                    Assert.True(face.Bounds.Height >= RigBoardFaceSize.Min - 1, $"a map thumbnail {face.Bounds}");
                }
                // the rail lights the mode the rig is in
                var modes = view.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("FireMapMode")).ToList();
                Assert.True(modes[lanes - 1].IsChecked, $"the rail does not light the {lanes}-lane mode");

                // the stage still carries the selected gear, big
                var big = view.GetVisualDescendants().OfType<RigFaceplateView>().Single(f => f.IsInteractive && f.IsEffectivelyVisible);
                var fit = big.GetVisualAncestors().OfType<RigScaleToFit>().First();
                Assert.True(big.Bounds.Height * fit.Scale > 200, $"the stage's gear is {big.Bounds.Height * fit.Scale:0} px");
                Capture(window, file);
            });
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     The map's two transient chips (the armed insert point, the undo for a
    ///     removed unit) ride its caption row, clear of every unit, and measure; a unit whose face
    ///     does not carry every param (the mic-position unit) opens its DETAILS on
    ///     its own, because the stage alone could not reach them; the cabinet keeps
    ///     its own console on the stage.
    /// </summary>
    [Fact]
    public Task TheMapsChips_AFacelessUnit_AndTheCabinetConsole() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var catalogue = Catalogue();
            Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
            using var rig = Rig(catalogue, "overdrive", "namamp", "cab", "micpos", "delay", "reverb");
            rig.SelectNodeAt(0);
            var model = new AmpFxRigViewModel(rig);
            Show(model, 1600, 900, (window, view) =>
            {
                var map = Box(view, "RigBoard", window);

                // remove a unit: the undo chip comes up on the map
                rig.RemoveCommand.Execute(rig.Nodes.Single(n => n.ModuleTypeId == "reverb"));
                // arm a gap: the insert chip says where the next unit lands
                model.InsertBeforeCommand.Execute(rig.Nodes.Single(n => n.ModuleTypeId == "delay"));
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                foreach (var name in new[] { "RigUndoChip", "RigInsertChip" })
                {
                    var chip = Box(view, name, window);
                    // A pill is wider than it is tall. Not a pixel floor: the
                    // Japanese insert chip is 52 px wide, and a floor set by the
                    // English text failed it in the release's ja-JP layout pass.
                    Assert.True(chip.Width >= chip.Height && chip.Height > 16, $"{name} measured {chip}");
                    // in the map's caption row, under the chain — never over a unit
                    Assert.True(chip.Top >= map.Top + map.Height / 2 && chip.Bottom <= map.Bottom + 1 && chip.Right <= map.Right + 1,
                        $"{name} {chip} is not in the map's caption row {map}");
                    var caption = view.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "RigBoardCaption");
                    Assert.Contains(view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == name), caption.GetVisualDescendants());
                    foreach (var card in view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("FireUnitCard")))
                        Assert.False(Box(card, window).Intersects(chip), $"{name} {chip} covers a unit {Box(card, window)}");
                }
                Capture(window, "layout-1600-chips.png");

                // the mic-position unit: its own editor, the placement, fills the
                // stage with every param on it, so the details stay shut
                Assert.False(model.IsDetailsOpen);
                rig.SelectNodeAt(rig.Nodes.IndexOf(rig.Nodes.Single(n => n.ModuleTypeId == "micpos")));
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                Assert.False(model.DockUnit!.FaceMissesParams);
                Assert.False(model.IsDetailsOpen, "the mic placement opened a details column it does not need");
                var placement = view.GetVisualDescendants().OfType<RigMicPlacementView>().Single(v => v.IsEffectivelyVisible);
                var placed = Box(placement, window);
                Assert.True(placed.Width > 600 && placed.Height > 300, $"the mic placement measured {placed}");
                Capture(window, "layout-1600-micpos-placement.png");

                // the cabinet: its own console on the stage, not a faceplate
                rig.SelectNodeAt(rig.Nodes.IndexOf(rig.Nodes.Single(n => n.ModuleTypeId == "cab")));
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                var editor = Assert.Single(view.GetVisualDescendants().OfType<RigModuleEditorView>());
                Assert.IsType<DotCharter.Tone.UI.Views.PlayMode.RigEditors.RigCabinetEditorView>(editor.ActiveFamilyView);
                var console = Box(editor.ActiveFamilyView!, window);
                var stage = Box(view, "RigDockBody", window);
                Assert.True(console.Width > 300 && console.Height > 150, $"the cabinet console measured {console}");
                Assert.True(console.Top >= stage.Top - 1 && console.Bottom <= stage.Bottom + 1, $"the console {console} leaves the stage {stage}");
                Capture(window, "layout-1600-cab-console.png");
            });
        }, TestContext.Current.CancellationToken);

    // ---- helpers ---------------------------------------------------------------

    private static PedalboardViewModel Rig(IReadOnlyList<DspModuleType> catalogue, params string[] ids)
    {
        var chain = new FakeDspChainService(catalogue);
        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
        foreach (var id in ids) rig.AddGear(catalogue.Single(m => m.Id == id));
        return rig;
    }

    private static void Show(AmpFxRigViewModel model, int width, int height, Action<Window, AmpFxRigView> body)
    {
        var view = new AmpFxRigView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            for (var pass = 0; pass < 6; pass++) window.UpdateLayout();
            body(window, view);
        }
        finally
        {
            window.Close();
        }
    }

    private static Rect Box(Control root, string name, Window window) =>
        Box(root.GetVisualDescendants().OfType<Control>().Single(c => c.Name == name), window);

    private static Rect Box(Control control, Window window)
    {
        var origin = control.TranslatePoint(new Point(0, 0), window);
        Assert.True(origin is not null, $"{control.Name ?? control.GetType().Name} is not in the window");
        return new Rect(origin!.Value, control.Bounds.Size);
    }

    private static void Click(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.UpdateLayout();
    }

    private static IReadOnlyList<DspModuleType> Catalogue()
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        return metadata.Catalog;
    }

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(System.IO.Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
