// The Amp & FX rig screen: its six regions with every unit template realized, its split-chain lanes, and its empty-chain states.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    /// <summary>A chain unit is its own gear, small — a picture, never a hit target —
    /// at the height the board gives every face (<c>AmpFxRigViewModel.BoardFaceHeight</c>),
    /// over its name. The selected one opens big in the dock.</summary>
    private static void AssertBoardFace(Border card, double faceHeight)
    {
        var face = Assert.Single(card.GetVisualDescendants().OfType<DotCharter.Tone.UI.Controls.Fire.RigFaceplateView>());
        Assert.True(!face.IsInteractive && !face.IsHitTestVisible, "the chain's picture is a live control");
        Assert.Equal(faceHeight, face.Bounds.Height, 1);
        Assert.True(face.Bounds.Width > 1, $"a board face laid out as {face.Bounds}");
    }

    // ══ Amp & FX rig (§6.8) ══════════════════════════════════════════════════

    /// <summary>
    ///     The rig's six regions compose, and — the part that matters — the three
    ///     ITEM TEMPLATES realize with real items behind them: the board card,
    ///     the gear row and the selected editor's param surface. Chrome
    ///     measures whether or not the templates are sound; a template only
    ///     builds once an item exists, which is why this hands the screen a real
    ///     chain instead of leaving it unbound.
    /// </summary>
    [Fact]
    public async Task RigScreen_composesItsSixRegions_AndRealizesEveryUnitTemplate()
    {
        await WithView((view, vm) =>
        {
            var chain = new FakeDspChainService();
            var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            // A card of each shape the board has to draw: plain knobs, a named
            // model picker (the amp's voicing), and a second picker (the cab).
            rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"));
            rig.AddGear(chain.Catalog.Single(t => t.Id == "amp"));
            rig.AddGear(chain.Catalog.Single(t => t.Id == "cab"));
            rig.SelectNodeAt(1);

            vm.ToneRig.UseRigForTests(rig);
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();

            foreach (var name in new[]
                     {
                         "RigScreenRoot", "RigHeader", "RigInputMeter",
                         // `RigSkinStrip` is gone with the FLAT/RACK switch —
                         // one pedalboard, one skin. `RigPresetWell` (a bare
                         // ComboBox) is gone too: the header now carries the
                         // mockup's TONE PILL, which is the same control as the
                         // drawer toggle (`RigBrowseButton`) and prints the
                         // applied tone's name and its EDITED badge.
                         "RigSceneStrip", "RigMasterBypass", "RigCloseButton",
                         "RigPresetPrev", "RigPresetNext", "RigBrowseButton",
                         "RigSaveButton",
                         "RigFlatBody", "RigSignalStrip", "RigAddUnit", "RigBoard",
                         "RigBoardItems", "RigInsertEnd", "RigDock", "RigDockBar",
                         "RigDockBody",
                         "RigGearPanel", "RigGearSearch", "RigGearFilters", "RigGearGroups",
                         "RigAddToRig",
                         "RigFooter", "RigRoutingStrip", "RigTunerMeter",
                         "RigInputChannelPicker", "RigInputChannelCombo"
                     })
            {
                AssertLaidOut(view, name);
            }

            // The footer's GUITAR INPUT picker over a service-less screen is
            // disabled with its reason (a capability the app cannot honour must
            // never look live) - and over an ASIO config with a real jack count
            // it is enabled with one row per jack and the configured jack
            // selected. Both faces lay out; the second is the owner's case.
            var combo = (ComboBox)Find(view, "RigInputChannelCombo");
            Assert.False(combo.IsEnabled, "a picker with no engine behind it must ship disabled");
            var audio = new FakeAudioPlaybackService { InputDeviceChannelCount = 4 };
            var config = new FakeConfigurationService();
            config.AppConfig.EnableInput = true;
            config.AppConfig.AudioDriver = "ASIO";
            config.AppConfig.AsioInputChannelLeft = 1;
            vm.ToneRig.AmpFxRig!.InputChannel = new DotCharter.Tone.UI.ViewModels.Settings.LiveInputChannelViewModel(audio, config, LocalizationService.Instance);
            view.UpdateLayout();
            AssertLaidOut(view, "RigInputChannelCombo");
            Assert.True(combo.IsEnabled, "an ASIO input with a known jack count must be pickable from the rig");
            Assert.Equal(4, combo.ItemCount);
            Assert.Equal(1, combo.SelectedIndex);

            // The BOARD: one card per chain node, each measured, each carrying
            // the unit identity that decides its colour.
            var cards = Find(view, "RigBoardItems").GetVisualDescendants()
                .OfType<Border>().Where(b => b.Classes.Contains("FireUnitCard")).ToList();
            Assert.Equal(3, cards.Count);
            Assert.All(cards, c => Assert.True(c.Bounds.Width > 1 && c.Bounds.Height > 1,
                $"a unit card laid out as {c.Bounds}"));
            Assert.Equal(FireUnitCategory.Drive, FireUnitState.GetCategory(cards[0]));
            Assert.Equal(FireUnitCategory.Amps, FireUnitState.GetCategory(cards[1]));
            Assert.Equal(FireUnitCategory.Cabinets, FireUnitState.GetCategory(cards[2]));

            // The chain: every unit its own gear, small; selecting one opens it
            // big in the dock.
            Assert.All(cards, c => AssertBoardFace(c, vm.ToneRig.AmpFxRig!.BoardFaceHeight));
            var window = (Window)TopLevel.GetTopLevel(view)!;
            var selectPoint = cards[2].TranslatePoint(new Point(cards[2].Bounds.Width / 2, 8), window)!.Value;
            window.MouseDown(selectPoint, MouseButton.Left);
            window.MouseUp(selectPoint, MouseButton.Left);
            view.UpdateLayout();
            Assert.Same(rig.Nodes[2], rig.SelectedNode);
            Assert.Same(rig.Nodes[2], vm.ToneRig.AmpFxRig!.DockUnit!.Node);
            rig.SelectNodeAt(1);

            // Remove and bypass live in the dock (the big gear's footswitch and the
            // Enabled switch), so a card carries no buttons at all.
            Assert.All(cards, c => Assert.Empty(c.GetVisualDescendants().OfType<Button>()));
            Assert.All(cards, c => Assert.Single(c.GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("FireUnitLed") && b.IsVisible));

            var filters = Find(view, "RigGearFilters").GetVisualDescendants()
                .OfType<ToggleButton>().ToList();
            Assert.True(filters.Count >= 2, $"only {filters.Count} gear filters");
            Assert.All(filters, b => Assert.True(b.Command?.CanExecute(b.CommandParameter) == true,
                "a gear category filter does not reach FilterCategoryCommand"));

            // The GEAR PANEL: rows off the real catalogue, in DisplayOrder.
            // The panel VIRTUALIZES, so the realized rows are the ones the
            // viewport holds — here the whole six-module fake catalogue fits, so
            // every id must still be reachable, and no row may be realized that
            // the catalogue does not have. The CEILING at 100+ modules is
            // `TheGearPalette_RealizesAViewportsWorthOfRows_NotTheWholeCatalogue`
            // below; this one is the correctness half.
            // The browser is a GRID now (the AmpliTube layout): two tiles a row,
            // so a row that holds only its left tile hides its right one.
            var rows = Find(view, "RigGearGroups").GetVisualDescendants()
                .OfType<Button>().Where(b => b.Classes.Contains("FireGearTile") && b.IsEffectivelyVisible).ToList();
            Assert.Equal(chain.Catalog.Count, rows.Count);
            Assert.All(rows, r => Assert.True(r.Bounds.Width > 1 && r.Bounds.Height > 1,
                $"a gear tile laid out as {r.Bounds}"));
            Assert.Equal(chain.Catalog.Count, vm.ToneRig.AmpFxRig!.GearRows.Count);
            Assert.Equal(
                vm.ToneRig.AmpFxRig.GearGroups.SelectMany(g => g.Items).Select(i => i.ModuleTypeId).ToArray(),
                vm.ToneRig.AmpFxRig.GearRows.Select(i => i.ModuleTypeId).ToArray());

            // The DOCK shows the SELECTED unit and every one of its params.
            // Each param renders as a knob OR a named picker — both templates
            // exist in the tree and exactly one of the pair is VISIBLE, which is
            // what "every param has exactly one control" means here.
            Assert.Equal("Amp", vm.ToneRig.AmpFxRig!.DockUnit!.DisplayName);
            Assert.Equal(RigEditorFace.Amp, vm.ToneRig.AmpFxRig.DockUnit.Face);
            // A SECOND layout pass. The editor is five templates deep
            // (ContentControl → ContentTemplate → face panel → ScrollViewer →
            // ItemsControl → item template) and a nested `ContentPresenter`
            // builds its child on ITS OWN measure, which lands in the pass after
            // the one that created it. A real window renders continuously and
            // never notices; a test that lays out exactly once sees empty
            // presenters and no knobs at all.
            view.UpdateLayout();
            var dockBody = Find(view, "RigDockBody");
            // A unit with a faceplate opens BIG as that face; its exact-value list
            // (folded, in the DETAILS column since the AmpliTube layout) is where
            // every param is reachable in one place — open both.
            vm.ToneRig.AmpFxRig.IsDetailsOpen = true;
            for (var pass = 0; pass < 3; pass++) view.UpdateLayout();
            if (dockBody.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.Name == "RigUnitValues") is { } valueList)
            {
                valueList.IsExpanded = true;
                for (var pass = 0; pass < 3; pass++) view.UpdateLayout();
            }
            // IsEffectivelyVisible, not IsVisible: hiding a parent does not
            // write the property on its children, so the un-chosen half of each
            // pair still reports IsVisible=true.
            var dockKnobs = dockBody.GetVisualDescendants().OfType<FireKnob>()
                .Where(k => k.IsEffectivelyVisible && k.DataContext is PedalParamViewModel).ToList();
            var dockPickers = dockBody.GetVisualDescendants().OfType<ComboBox>()
                .Where(c => c.IsEffectivelyVisible && c.DataContext is PedalModelSelectorViewModel).ToList();
            // EVERY param of the selected unit is reachable in the editor, and
            // each one exactly once: a knob if it is continuous, a picker if the
            // ABI gave it value labels. That equality is what stops the rich
            // face quietly dropping a control the card used to show.
            Assert.Equal(vm.ToneRig.AmpFxRig.DockUnit.Node.Params.Count, dockKnobs.Count + dockPickers.Count);
            // …and the amp's ONE named param really renders as one picker.
            Assert.Single(dockPickers);
            Assert.True(vm.ToneRig.AmpFxRig.DockUnit.HasModelRail);

            // The dock collapses to its BAR when nothing is selected — the
            // mockup reserves 34 px for the bar and only opens the 244 px body
            // for a unit. A body kept up with "nothing selected" centred in it
            // spends a third of the screen saying nothing.
            Assert.True(vm.ToneRig.AmpFxRig.IsDockBodyVisible);
            rig.SelectedNode = null;
            view.UpdateLayout();
            Assert.False(vm.ToneRig.AmpFxRig.IsDockBodyVisible);
            Assert.False(Find(view, "RigDockBody").IsVisible);
            AssertLaidOut(view, "RigDockBar");
            rig.SelectNodeAt(1);
            view.UpdateLayout();

            // THE SIGNAL PATH. A row of cards is a shelf; the jacks, the cable
            // and the insert dots are what make it a path — and the dots are
            // real buttons on a real command, not decoration.
            var board = Find(view, "RigBoard");
            var jacks = board.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("FireJack")).ToList();
            Assert.Equal(2, jacks.Count); // IN and OUT
            Assert.All(jacks, j => Assert.True(j.Bounds.Width > 1 && j.Bounds.Height > 1));

            var inserts = board.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("FireInsertDot")).ToList();
            // one gap before each card + the trailing one
            Assert.Equal(cards.Count + 1, inserts.Count);
            Assert.All(inserts, b => Assert.True(b.Command?.CanExecute(b.CommandParameter) == true,
                "an insert dot is not wired to a command"));

            // Arming a gap and adding a unit really seats it THERE.
            Assert.False(vm.ToneRig.AmpFxRig.HasInsertPoint);
            inserts[1].Command!.Execute(inserts[1].CommandParameter);
            Assert.True(vm.ToneRig.AmpFxRig.HasInsertPoint);
            var gearItem = vm.ToneRig.AmpFxRig.GearGroups
                .SelectMany(g => g.Items).First(i => i.Type.Id == "reverb");
            gearItem.AddCommand.Execute(gearItem);
            view.UpdateLayout();
            Assert.Equal("Reverb", rig.Nodes[1].DisplayName);
            Assert.False(vm.ToneRig.AmpFxRig.HasInsertPoint); // spent
            rig.RemoveCommand.Execute(rig.Nodes[1]);
            view.UpdateLayout();

            // The strip's → separators: one per chip but the first.
            Assert.Equal(cards.Count, vm.ToneRig.AmpFxRig.SignalChips.Count);
            Assert.False(vm.ToneRig.AmpFxRig.SignalChips[0].ShowsArrow);
            Assert.All(vm.ToneRig.AmpFxRig.SignalChips.Skip(1), c => Assert.True(c.ShowsArrow));

            // BYPASS is applied by the inherited pair; the card fades and its
            // state dot goes out. The switch itself lives in the editor dock.
            //
            // Re-queried: the insert exercise above added and removed a node, so
            // the board rebuilt its containers and the `cards` captured earlier
            // are detached Borders nothing writes to any more. An attached
            // property read off one of those reports the default and the
            // assertion fails for a reason that has nothing to do with bypass.
            cards = Find(view, "RigBoardItems").GetVisualDescendants()
                .OfType<Border>().Where(b => b.Classes.Contains("FireUnitCard")).ToList();
            rig.Nodes[0].Bypassed = true;
            view.UpdateLayout();
            Assert.True(FireUnitState.GetIsBypassed(cards[0]));
            Assert.Contains("bypassed", cards[0].Classes);
            // the picture fades itself (FireUnitState, inherited from the card)
            Assert.True(DotCharter.Tone.UI.Controls.Fire.FireUnitState.IsInactive(
                cards[0].GetVisualDescendants().OfType<DotCharter.Tone.UI.Controls.Fire.RigFaceplateView>().Single()));
            Assert.Empty(cards[0].GetVisualDescendants().OfType<ToggleButton>());
            var dimLed = Assert.Single(cards[0].GetVisualDescendants().OfType<Border>(),
                b => b.Classes.Contains("FireUnitLed") && b.IsVisible);
            Assert.Contains("Dim", dimLed.Classes);
            rig.SelectNodeAt(0);
            view.UpdateLayout();
            // the gear bar's POWER button: lit while the unit is in the signal
            var bypassSwitch = Find(view, "RigDockEnabled") as ToggleButton;
            Assert.NotNull(bypassSwitch);
            Assert.False(bypassSwitch.IsChecked);

            // The board is unconditional now — the FLAT/RACK skin switch and the
            // classic rack behind it are gone, because two skins over one chain
            // read as two pedalboards.
            AssertLaidOut(view, "RigFlatBody");

            // The TONES MODE (W4): the rack's browser, on this board, as a mode
            // that REPLACES the canvas body rather than an overlay over it — so
            // `RigFlatBody` is the thing that is NOT laid out while it is up,
            // and the read-only chain strip is what says which rig the tone is
            // about to land on.
            rig.ToggleBrowserCommand.Execute(null);
            view.UpdateLayout();
            AssertLaidOut(view, "RigTonesMode");
            AssertLaidOut(view, "RigChainStrip");
            AssertLaidOut(view, "RigToneBrowser");
            AssertLaidOut(view, "RigTonesScroll");
            AssertLaidOut(view, "RigBrowserRail");
            AssertLaidOut(view, "RigBrowserCards");
            Assert.False(Find(view, "RigFlatBody").IsEffectivelyVisible,
                "the canvas body is still drawn inside the Tones mode");
            rig.ToggleBrowserCommand.Execute(null);
            view.UpdateLayout();
            AssertLaidOut(view, "RigFlatBody");

            vm.ToneRig.IsPedalboardOpen = false; // ClosePedalboardCommand is gone (0.58) — nothing bound it
            view.UpdateLayout();
        });
    }

    /// <summary>
    ///     <b>The gear palette is WINDOWED: it realizes a viewport's worth of
    ///     rows, never the catalogue.</b>
    ///
    ///     <para>
    ///         Every gate on this surface was a FLOOR — "at least two filters",
    ///         "not empty", "a row has non-degenerate bounds" — so a catalogue
    ///         that grows cannot fail any of them, and the JuceSharp 0.9 gear
    ///         wave grew it 57 → 100. Unvirtualized, that is ~680 → ~1,200
    ///         realized elements, all of it paid on the prewarm drain (the rig
    ///         screen is force-realized at <c>PlayModeView.HiddenSurfaces</c>)
    ///         and paid AGAIN, on the UI thread, on every tab click, favourite
    ///         toggle and coalesced keystroke. Sixty preset cards on this same
    ///         screen were already enough for the owner to report lag.
    ///     </para>
    ///     <para>
    ///         <b>Counted in ELEMENTS, never milliseconds</b> — the repo rule.
    ///         The ceiling is derived from the LAYOUT, not written down: a
    ///         viewport's worth of rows at the row height the panel actually
    ///         laid out, plus a generous buffer for the panel's realization
    ///         margin. What makes it a real assertion is the catalogue behind
    ///         it: 160 modules, far more than any viewport can hold, so an
    ///         unvirtualized panel fails by the count it realizes and not by a
    ///         threshold anyone tuned.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheGearPalette_RealizesAViewportsWorthOfRows_NotTheWholeCatalogue()
    {
        // Deliberately larger than the shipping catalogue (100 after the 0.9
        // gear wave) AND than any plausible viewport: the point is the RATIO.
        var catalogue = Enumerable.Range(0, 160)
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
            // Two passes: the palette's containers are built by the virtualizing
            // panel off the viewport the FIRST pass establishes.
            view.UpdateLayout();
            view.UpdateLayout();

            var palette = Find<ItemsControl>(view, "RigGearGroups");
            Assert.Equal(160, vm.ToneRig.AmpFxRig!.GearRows.Count);

            var realized = palette.GetRealizedContainers().ToList();
            Assert.NotEmpty(realized); // anti-vacuity: a panel that built nothing proves nothing
            // The browser is a GRID of two tiles a row (the AmpliTube layout); the
            // virtualized unit is the ROW (a RigGearTilePair container), so the
            // ceiling counts rows and the tiles are at most two per row.
            var rows = palette.GetVisualDescendants().OfType<Grid>()
                .Where(g => g.Classes.Contains("FireGearTileRow")).ToList();
            var tiles = palette.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("FireGearTile")).ToList();
            Assert.NotEmpty(rows);
            Assert.NotEmpty(tiles);

            var scroller = palette.GetVisualDescendants().OfType<ScrollViewer>().First();
            var rowHeight = rows.Max(r => r.Bounds.Height);
            Assert.True(rowHeight > 1, $"a gear row laid out {rowHeight:0.#} px tall");

            // The viewport's worth, plus eight rows of slack for the panel's own
            // realization margin and whatever partial rows straddle the edges.
            var viewportRows = (int)Math.Ceiling(scroller.Viewport.Height / rowHeight);
            var ceiling = viewportRows + 8;

            var descendants = palette.GetVisualDescendants().Count();
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"gear palette: {catalogue.Count} modules, viewport {scroller.Viewport.Height:0.#} px / " +
                $"row {rowHeight:0.#} px = {viewportRows} rows, realized containers {realized.Count}, " +
                $"realized rows {rows.Count} / tiles {tiles.Count} (ceiling {ceiling}), total realized descendants {descendants}");

            Assert.True(rows.Count <= ceiling,
                $"the gear palette realized {rows.Count} of {(catalogue.Count + 1) / 2} rows against a viewport " +
                $"that holds {viewportRows} ({scroller.Viewport.Height:0.#} px / {rowHeight:0.#} px) - " +
                "it is not virtualizing, so both the prewarm drain and every search keystroke now " +
                "cost one tile template per CATALOGUE entry");
            Assert.True(tiles.Count <= ceiling * 2, $"{tiles.Count} tiles realized over {rows.Count} rows");
            Assert.True(realized.Count <= ceiling,
                $"{realized.Count} realized containers against a {viewportRows}-row viewport");

            // ...and the whole subtree with it. A row is two tiles of ~20 visuals
            // plus its container chrome; 80 a row is slack, and still an order of
            // magnitude under what an unvirtualized 160-module grid would carry.
            Assert.True(descendants <= ceiling * 80,
                $"the gear palette carries {descendants} realized descendants over {rows.Count} rows " +
                $"- more than the {ceiling * 80} a windowed list can account for");
        });
    }

    /// <summary>
    ///     <b>The tone library resizes, and the height sticks.</b> Owner request
    ///     (v0.48.5): "I want the tone library resizable in height, like a
    ///     GridSplitter or something."
    ///
    ///     <para>
    ///         Four things have to be true and the first three are invisible when
    ///         they break — a splitter over a row nothing sizes, or a height
    ///         committed to a config nobody reads back, both LOOK like a working
    ///         drag right up until the next launch:
    ///         <list type="number">
    ///             <item>the persisted height is on the row when the drawer
    ///             opens (not the shipped default);</item>
    ///             <item>a real drag on <c>RigBrowserSplitter</c> MOVES that row
    ///             — this is what a bare `Height="480"` on the drawer Border
    ///             silently defeated;</item>
    ///             <item>the new height reaches <c>AppConfig</c>;</item>
    ///             <item>a REBUILT rig over the same config comes back at it.</item>
    ///         </list>
    ///     </para>
    ///
    ///     <para>
    ///         The drag is raised as the <c>Thumb</c> routed events rather than
    ///         synthesized pointer input: <c>GridSplitter</c> is a Thumb and its
    ///         class handlers are the whole resize path, so this exercises the
    ///         real <c>MoveSplitter</c> arithmetic (including the row's
    ///         Min/MaxHeight) without needing a headless pointer capture.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task RigToneBrowser_isResizable_andItsHeightSurvivesARebuild()
    {
        const double persisted = 360;
        // the workspace (the STAGE) is ABOVE the divider in the AmpliTube layout,
        // so pulling the divider DOWN is what grows it
        const double dragDown = 80;

        await WithView((view, vm) =>
        {
            var config = new FakeConfigurationService();
            config.AppConfig.RigWorkspaceHeight = persisted;

            var chain = new FakeDspChainService();
            var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance, config);
            vm.ToneRig.UseRigForTests(rig);
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();

            // ── 1. the persisted height is on the ROW, not the shipped default
            rig.ToggleBrowserCommand.Execute(null);
            view.UpdateLayout();
            AssertLaidOut(view, "RigToneBrowser");
            AssertLaidOut(view, "RigBrowserSplitter");

            var body = (Grid)Find(view, "RigFlatBody");
            var row = body.RowDefinitions[0];
            Assert.True(row.Height.IsAbsolute,
                $"the workspace row is {row.Height} — an Auto row is not something a splitter can restore, "
                + "and an Auto row is what made the routing canvas resize (W1)");
            Assert.Equal(persisted, row.Height.Value, 1);
            Assert.Equal(RigWorkspaceViewModel.MinHeight, row.MinHeight);
            Assert.Equal(RigWorkspaceViewModel.MaxHeight, row.MaxHeight);

            // The map below it is the `*` row that gives way, and it is still
            // on screen — the whole point of its floor.
            AssertLaidOut(view, "RigBoard");

            // ── 2. a real drag moves that row
            var splitter = (GridSplitter)Find(view, "RigBrowserSplitter");
            splitter.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragStartedEvent });
            splitter.RaiseEvent(new VectorEventArgs
            {
                RoutedEvent = Thumb.DragDeltaEvent, Vector = new Vector(0, dragDown)
            });
            splitter.RaiseEvent(new VectorEventArgs
            {
                RoutedEvent = Thumb.DragCompletedEvent, Vector = new Vector(0, dragDown)
            });
            view.UpdateLayout();

            var dragged = row.Height.Value;
            Assert.True(dragged > persisted + 40,
                $"the drag left the workspace row at {dragged} — the splitter is not resizing it");

            // ── 3. …and reaches the config
            Assert.NotNull(config.AppConfig.RigWorkspaceHeight);
            Assert.Equal(dragged, config.AppConfig.RigWorkspaceHeight!.Value, 1);
            Assert.Equal(dragged, rig.Workspace.Height, 1);

            // ── 4. a rebuilt rig over the same config restores it. This is the
            // half a "resize" ships without and nobody notices until relaunch.
            var rebuilt = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance, config);
            Assert.Equal(dragged, rebuilt.Workspace.Height, 1);

            vm.ToneRig.UseRigForTests(rebuilt);
            rebuilt.ToggleBrowserCommand.Execute(null);
            view.UpdateLayout();

            var rebuiltRow = ((Grid)Find(view, "RigFlatBody")).RowDefinitions[0];
            Assert.True(rebuiltRow.Height.IsAbsolute);
            Assert.Equal(dragged, rebuiltRow.Height.Value, 1);

            // ── 5. closing the drawer KEEPS the height, and that is the W1
            // inversion. It used to hand the row back to a responsive `2.2*`
            // (or Auto for a collapsed dock), which is precisely what made the
            // routing canvas above resize when the editor opened or closed.
            // One host, one absolute height, in every state.
            rebuilt.ToggleBrowserCommand.Execute(null);
            view.UpdateLayout();
            var closedRow = ((Grid)Find(view, "RigFlatBody")).RowDefinitions[0];
            Assert.True(closedRow.Height.IsAbsolute,
                "the workspace row went back to sizing itself — the canvas will move again");
            Assert.Equal(dragged, closedRow.Height.Value, 1);

            vm.ToneRig.IsPedalboardOpen = false;
            view.UpdateLayout();
        });
    }

    /// <summary>
    ///     The rig screen REPORTS ITS OWN LIVENESS, so the footer tuner actually
    ///     reads.
    ///
    ///     <para>
    ///         <b>The bug this exists to end (found 2026-08-15).</b>
    ///         <c>RigTunerViewModel.SetSurfaceLive</c> had no product caller at
    ///         all: the live-surface set was permanently EMPTY, so
    ///         <c>SyncRunning</c> could never start the 66 ms poll and the
    ///         footer's <c>FireTunerMeter</c> needle never moved once. The gating
    ///         contract itself was pinned and green the whole time
    ///         (<c>PedalboardAuditRegressionTests
    ///         .Rig_tuner_polls_only_while_open_and_a_host_surface_is_live</c>) —
    ///         a passing contract test over a caller that did not exist, which is
    ///         exactly why nothing went red.
    ///     </para>
    ///     <para>
    ///         Driven through the REAL navigation, not by calling the method: the
    ///         missing half was the view's, so a test that reports liveness itself
    ///         would re-create the same vacuum. <c>IsPolling</c> is the seam
    ///         because it is the thing the needle depends on — hence the real
    ///         <c>FakeAudioPlaybackService</c> behind the rig (a null audio
    ///         service can never poll, and the assertion would pass for the wrong
    ///         reason).
    ///     </para>
    /// </summary>
    [Fact]
    public Task RigScreen_ReportsItsOwnLiveness_SoTheFooterTunerActuallyReads() =>
        WithView((view, vm) =>
        {
            var rig = TestRigBuilders.Pedalboard(
                new FakeDspChainService(), new FakeDspTonePresetService(), LocalizationService.Instance,
                audio: new FakeAudioPlaybackService());
            var tuner = rig.RigTuner;

            // NOT opened here. The poll gates on IsOpen as well as on a live
            // host, and the only other opener in the product is selecting a
            // Pitch-family pedal — so a test that opened the panel itself would
            // pass over a footer needle that never moves on any rig without one.
            // The screen draws the tuner permanently: showing the rig must open
            // it. Nothing polls before that.
            Assert.False(tuner.IsOpen, "the tuner is open before any host showed it");
            Assert.False(tuner.IsPolling,
                "the tuner polls with no host surface live — the gate is gone");

            vm.ToneRig.UseRigForTests(rig);
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();

            Assert.True(tuner.IsOpen,
                "the rig screen is on and the footer tuner is not OPEN — AmpFxRigView reports "
                + "liveness but never opens the panel, so the needle only moves after a Pitch pedal is selected");
            Assert.True(tuner.IsPolling,
                "the rig screen is on and the tuner still does not poll — AmpFxRigView never "
                + "called SetSurfaceLive, so the footer needle can never move");

            // …and it stands down the moment the screen goes away, so nothing
            // reads the input capture ring behind a hidden overlay.
            vm.ToneRig.IsPedalboardOpen = false;
            view.UpdateLayout();
            Assert.False(tuner.IsPolling,
                "the rig screen is hidden and the tuner is still polling the audio engine");

            // Re-show: the report has to survive a round trip, not fire once.
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();
            Assert.True(tuner.IsPolling, "the tuner did not come back with the screen");

            tuner.Close();
            vm.ToneRig.IsPedalboardOpen = false;
            view.UpdateLayout();
        });

    /// <summary>
    ///     A SPLIT chain folds the board into lanes and merges back — the v8
    ///     mockup's split panel over the real routing. The lane templates only
    ///     build when a split segment exists, so the six-regions case above
    ///     never touches them: the region frame, the fan buses/stubs, the lane
    ///     badges and the COMPACT cards all have to measure here, and every
    ///     insert dot inside a lane has to reach a real command (the
    ///     nested-ItemsControl ancestor-cast trap renders perfectly and does
    ///     nothing).
    /// </summary>
    [Fact]
    public async Task RigScreen_withASplitChain_FoldsTheBoardIntoLanes()
    {
        await WithView((view, vm) =>
        {
            var chain = new FakeDspChainService(RigSplitCatalog());
            var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"));
            rig.AddGear(chain.Catalog.Single(t => t.Id == "splitter")); // auto-pairs the merger
            var delay = rig.AddGear(chain.Catalog.Single(t => t.Id == "delay"))!;
            var reverb = rig.AddGear(chain.Catalog.Single(t => t.Id == "reverb"))!;
            var merger = rig.Nodes.First(n => n.IsMerger);
            Assert.True(rig.DropToRow(delay, rig.Nodes.IndexOf(merger), 1));
            Assert.True(rig.DropToRow(reverb, rig.Nodes.IndexOf(merger), 2));

            vm.ToneRig.UseRigForTests(rig);
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();

            var board = Find(view, "RigBoardItems");

            var region = board.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("FireSplitRegion"));
            Assert.True(region.Bounds is { Width: > 1, Height: > 1 },
                $"the split region laid out as {region.Bounds}");

            // Two lanes, stacked A above B, each measured.
            var lanes = board.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("FireLaneRow")).ToList();
            Assert.Equal(2, lanes.Count);
            Assert.All(lanes, l => Assert.True(l.Bounds is { Width: > 1, Height: > 1 },
                $"a lane laid out as {l.Bounds}"));
            Assert.True(
                lanes[0].TranslatePoint(new Point(0, 0), region)!.Value.Y <
                lanes[1].TranslatePoint(new Point(0, 0), region)!.Value.Y,
                "lane A does not stack above lane B");

            // Lanes share the board's height between them, from the same template.
            var laneCards = lanes.SelectMany(l => l.GetVisualDescendants().OfType<Border>())
                .Where(b => b.Classes.Contains("FireUnitCard")).ToList();
            Assert.Equal(2, laneCards.Count);
            var serialCard = board.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("FireUnitCard") && !laneCards.Contains(b));
            // On a split board every face is the lane height, in and out of a lane.
            var laneFace = vm.ToneRig.AmpFxRig!.BoardFaceHeight;
            AssertBoardFace(serialCard, laneFace);
            Assert.All(laneCards, c => AssertBoardFace(c, laneFace));

            // Each lane ends in its mixer node: a pan knob and M/S.
            var mixers = lanes.SelectMany(l => l.GetVisualDescendants().OfType<Border>())
                .Where(b => b.Classes.Contains("FireMixerNode")).ToList();
            Assert.Equal(2, mixers.Count);
            Assert.All(mixers, m =>
            {
                Assert.True(m.Bounds is { Width: > 1, Height: > 1 }, $"a mixer node laid out as {m.Bounds}");
                Assert.Single(m.GetVisualDescendants().OfType<FireKnob>());
                Assert.Equal(2, m.GetVisualDescendants().OfType<ToggleButton>().Count());
            });

            // The fan really draws: two buses, one stub per lane at BOTH edges,
            // and the lane chips.
            var fans = board.GetVisualDescendants().OfType<RigFan>()
                .Where(f => f.Classes.Contains("FireLaneBus")).ToList();
            Assert.Equal(2, fans.Count);
            // the split fan curves out to both lanes, the merge fan back from both
            Assert.All(fans, f => Assert.Equal(2, f.Anchors().Count));
            Assert.All(fans, f => Assert.True(f.Bounds.Height > 1, $"a fan laid out as {f.Bounds}"));
            Assert.Equal(4, board.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("FireLaneStub")));
            var chips = board.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("FireLaneChip"))
                .Select(b => b.GetVisualDescendants().OfType<TextBlock>().Single().Text ?? "")
                .ToList();
            var expectedChips = vm.ToneRig.AmpFxRig!.BoardSegments.OfType<RigSplitSegment>().Single()
                .Lanes.Select(l => l.ChipLabel).ToList();
            Assert.Equal(expectedChips, chips);

            // Every insert dot inside a lane resolves a live command…
            var laneDots = lanes.SelectMany(l => l.GetVisualDescendants().OfType<Button>())
                .Where(b => b.Classes.Contains("FireInsertDot")).ToList();
            Assert.Equal(4, laneDots.Count); // per-unit gap + trailing gap, per lane
            Assert.All(laneDots, d => Assert.True(d.Command?.CanExecute(d.CommandParameter) == true,
                "a lane insert dot is not wired to a command"));

            // …and arming lane B's TRAILING gap carries branch 2, so the next
            // add lands in that lane rather than at the right slot in lane A.
            var laneBTail = lanes[1].GetVisualDescendants().OfType<Button>()
                .Last(b => b.Classes.Contains("FireInsertDot"));
            laneBTail.Command!.Execute(laneBTail.CommandParameter);
            Assert.True(vm.ToneRig.AmpFxRig!.HasInsertPoint);
            Assert.Equal(2, vm.ToneRig.AmpFxRig.InsertBranch);
            vm.ToneRig.AmpFxRig.ClearInsertPointCommand.Execute(null);

            // The region has no header of its own: SERIES in the routing header
            // dissolves it, and the Split / Merge nodes open their console.
            var junctions = region.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("FireJunctionNode")).ToList();
            Assert.Equal(2, junctions.Count);
            Assert.All(junctions, j => Assert.True(j.Command?.CanExecute(j.CommandParameter) == true,
                "a junction node does not reach SelectUnitCommand"));

            vm.ToneRig.IsPedalboardOpen = false;
            view.UpdateLayout();
        });
    }

    /// <summary>
    ///     The whole drag, driven through REAL pointer events: press a serial
    ///     card, drag it over lane B, release — and the unit re-branches into
    ///     that lane. This is the gesture the owner reached for on the shipped
    ///     compact panel and could not make ("you cant even drag and drop stuff
    ///     in it"): the first lane fold scoped a drag to the run it started in,
    ///     so a cross-run drop was a silent no-op. Pointer events are the only
    ///     honest way to prove the hit-test targeting
    ///     (<c>RigBoardDragController.TargetRunAt</c>) resolves a LANE — the
    ///     pure gap arithmetic is pinned in
    ///     <c>RigBoardDragAndToneBrowserTests</c>, but arithmetic over a lane
    ///     nothing can point at was exactly the shipped fault.
    /// </summary>
    [Fact]
    public async Task RigScreen_dragsASerialCardIntoALane_AndItReBranches()
    {
        await WithView((view, vm) =>
        {
            var chain = new FakeDspChainService(RigSplitCatalog());
            var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            var od = rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"))!;
            rig.AddGear(chain.Catalog.Single(t => t.Id == "splitter"));
            var delay = rig.AddGear(chain.Catalog.Single(t => t.Id == "delay"))!;
            var reverb = rig.AddGear(chain.Catalog.Single(t => t.Id == "reverb"))!;
            var merger = rig.Nodes.First(n => n.IsMerger);
            Assert.True(rig.DropToRow(delay, rig.Nodes.IndexOf(merger), 1));
            Assert.True(rig.DropToRow(reverb, rig.Nodes.IndexOf(merger), 2));

            // Keep the entire drag path on screen: zoom to the persisted minimum and fold
            // the dock away so the whole split region sits inside the viewport —
            // a pointer cannot press what is scrolled out of view.
            rig.Zoom = PedalboardViewModel.MinZoom;
            vm.ToneRig.UseRigForTests(rig);
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();
            vm.ToneRig.AmpFxRig!.IsDockOpen = false;
            view.UpdateLayout();

            var window = (Window)TopLevel.GetTopLevel(view)!;
            var board = Find(view, "RigBoardItems");

            Control CardOf(PedalNodeViewModel node) =>
                board.GetVisualDescendants().OfType<Border>()
                    .Single(b => b.Classes.Contains("FireUnitCard")
                                 && ReferenceEquals(b.DataContext, node));

            // Press on the serial overdrive card's HEAD strip (no interactive
            // control there), then travel past the 4 px threshold.
            var odCard = CardOf(od);
            var press = odCard.TranslatePoint(new Point(odCard.Bounds.Width / 2, 8), window)!.Value;
            window.MouseDown(press, MouseButton.Left);
            window.MouseMove(press + new Point(12, 0));

            // …to lane B: just past the reverb card's right edge, vertically on
            // the card — inside the lane row, in the tail gap.
            var reverbCard = CardOf(reverb);
            var target = reverbCard.TranslatePoint(
                new Point(reverbCard.Bounds.Width + 6, reverbCard.Bounds.Height / 2), window)!.Value;
            window.MouseMove(target);
            window.MouseUp(target, MouseButton.Left);
            view.UpdateLayout();

            // The unit really re-branched into lane B, after reverb.
            Assert.Equal(2, od.Branch);
            var split = vm.ToneRig.AmpFxRig.BoardSegments.OfType<RigSplitSegment>().Single();
            Assert.Equal([reverb, od], split.Lanes[1].Items.ToArray());
            Assert.Same(delay, Assert.Single(split.Lanes[0].Items));

            vm.ToneRig.IsPedalboardOpen = false;
            view.UpdateLayout();
        });
    }

    /// <summary>
    ///     Renders the rig with a DUAL chain to a PNG for proportion review —
    ///     the compact-chip split panel looked fine in markup and was rejected
    ///     on sight, so the lane rework's proportions get an eyeball artifact.
    ///     Runs only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (captures need real
    ///     Skia drawing); the capture is a review aid, never the assertion —
    ///     the sized/lane assertions live in the split-chain smoke above.
    /// </summary>
    [Fact]
    public async Task LocalOnly_RigScreen_capturesADualChainFrame()
    {
        Assert.SkipWhen(!UiSweepRunner.CaptureEnabled,
            $"set {UiSweepRunner.CaptureEnvVar}=1 — captures need real Skia drawing");

        await WithView((view, vm) =>
        {
            var chain = new FakeDspChainService(RigSplitCatalog());
            var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
            rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"));
            rig.AddGear(chain.Catalog.Single(t => t.Id == "splitter"));
            var delay = rig.AddGear(chain.Catalog.Single(t => t.Id == "delay"))!;
            var reverb = rig.AddGear(chain.Catalog.Single(t => t.Id == "reverb"))!;
            var merger = rig.Nodes.First(n => n.IsMerger);
            Assert.True(rig.DropToRow(delay, rig.Nodes.IndexOf(merger), 1));
            Assert.True(rig.DropToRow(reverb, rig.Nodes.IndexOf(merger), 2));

            vm.ToneRig.UseRigForTests(rig);
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();

            var window = (Window)TopLevel.GetTopLevel(view)!;
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            var dir = Path.Combine(AppContext.BaseDirectory, "TestResults", "rig-capture");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "dual-chain.png");
            frame!.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

            vm.ToneRig.IsPedalboardOpen = false;
            view.UpdateLayout();
        });
    }

    /// <summary>The routing catalogue the split cases need — the default fake
    /// catalogue carries no splitter/merger on purpose.</summary>
    private static IReadOnlyList<DspModuleType> RigSplitCatalog() =>
    [
        new DspModuleType("overdrive", "Overdrive", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("reverb", "Reverb", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new DspModuleType("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f)]),
    ];

    /// <summary>
    ///     The TONE3000 capture panel, which the rail's last entry swaps in for
    ///     the card grid.
    ///
    ///     <para>
    ///         <b>This is the test that was missing.</b> The panel lived in
    ///         <c>PedalboardView.axaml</c> and went with that file when v0.43
    ///         lifted the rig here; the view model, its services and its unit
    ///         tests all survived, so nothing went red. The rail row kept being
    ///         built and kept opening an EMPTY GRID — <c>PresetBrowserFilter</c>
    ///         yields no cards for that kind BY DESIGN, because a panel was meant
    ///         to take the column — and "the feature is gone" was indistinguishable
    ///         from "you have no captures". Restored in 0.57.
    ///     </para>
    ///     <para>
    ///         It asserts the panel MEASURED, not merely that it exists: a subtree
    ///         whose styles failed still arranges, it just has no size, and that is
    ///         the shape every silent XAML fault in this repo has taken.
    ///     </para>
    /// </summary>
    [Fact]
    public Task RigToneBrowser_swapsTheCardGridForTheTone3000CapturePanel() =>
        WithView((view, vm) =>
        {
            vm.ToneRig.OpenPedalboardCommand.Execute(null);
            view.UpdateLayout();
            var rig = vm.ToneRig.Pedalboard!;
            rig.ToggleBrowserCommand.Execute(null);
            view.UpdateLayout();

            var browser = rig.Browser;
            var nam = browser.Rail.FirstOrDefault(
                r => r.Kind == DotCharter.Tone.UI.Controls.Dsp.PresetFilterKind.NamCaptures);
            Assert.True(nam != null,
                "the NAM Captures rail entry is gone — either the TONE3000 services stopped being wired " +
                "in this session, or the rail stopped offering the row. Both make this test vacuous.");

            // The cards are showing right up until the row is picked.
            Assert.True(browser.ShowCards || browser.ShowNoCards);
            Assert.False(browser.IsNamCapturesSelected);

            browser.SelectedRail = nam;
            view.UpdateLayout();

            // The grid AND its empty state stand down — without the second one the
            // preset "no cards here" message draws on top of the capture panel,
            // which is exactly what the missing panel looked like.
            Assert.True(browser.IsNamCapturesSelected);
            Assert.False(browser.ShowCards);
            Assert.False(browser.ShowNoCards);

            // …and the panel really laid out. Online view is the default.
            AssertLaidOut(view, "NamOnlineTab");
            AssertLaidOut(view, "NamLibraryTab");
            AssertLaidOut(view, "NamSearchBox");
            AssertLaidOut(view, "NamSearchButton");

            // The library half is behind its own tab, so drive it too — an
            // unmeasured template is exactly how this feature disappeared.
            browser.NamBrowser!.ShowLibraryCommand.Execute(null);
            view.UpdateLayout();
            Assert.True(browser.NamBrowser.IsLibraryView);
            AssertLaidOut(view, "NamLibraryTab");

            browser.NamBrowser.ShowOnlineCommand.Execute(null);
            view.UpdateLayout();
            Assert.True(browser.NamBrowser.IsOnlineView);

            // Back to a preset row and the grid returns.
            browser.SelectedRail = browser.Rail[0];
            view.UpdateLayout();
            Assert.False(browser.IsNamCapturesSelected);
            AssertLaidOut(view, "RigBrowserCards");

            rig.ToggleBrowserCommand.Execute(null);
            view.UpdateLayout();
            vm.ToneRig.IsPedalboardOpen = false; // ClosePedalboardCommand is gone (0.58) — nothing bound it
            view.UpdateLayout();
        });

    /// <summary>
    ///     The EMPTY board — a chain with nothing on it — still draws its empty
    ///     state and its dock. That branch's templates are never built by the
    ///     populated case above, and an empty state that renders wordless is the
    ///     dead panel §4 of the spec exists to remove.
    /// </summary>
    [Fact]
    public async Task RigScreen_withAnEmptyChain_DrawsItsEmptyStates()
    {
        await WithView((view, vm) =>
        {
            var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), LocalizationService.Instance);
            vm.ToneRig.UseRigForTests(rig);
            vm.ToneRig.IsPedalboardOpen = true;
            view.UpdateLayout();

            Assert.False(vm.ToneRig.AmpFxRig!.HasChain);
            Assert.False(vm.ToneRig.AmpFxRig.HasDockUnit);
            AssertLaidOut(view, "RigBoardEmpty");
            AssertLaidOut(view, "RigGearGroups");

            // A rig nobody has applied a preset to is NOT "edited".
            Assert.False(vm.ToneRig.AmpFxRig.IsEdited);
            Assert.False(Find(view, "RigEditedBadge").IsVisible);

            vm.ToneRig.IsPedalboardOpen = false; // ClosePedalboardCommand is gone (0.58) — nothing bound it
            view.UpdateLayout();
        });
    }
}
