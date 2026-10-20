using System.Xml.Linq;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.Converters;

namespace DotCharter.Tests.UI;

/// <summary>
///     Default-gate guards for the Amp &amp; FX rig's <b>module editor</b> and its
///     <b>signal path</b> — the two things §6.8 shipped without and the owner
///     reported as "selecting an amp doesn't open the nice view that the web has"
///     and "the html has structure this doesn't".
///
///     <para>
///         Everything here is either PURE (the face fold, the card-width ladder,
///         the insert-point arithmetic, the dock projection over a fake chain) or
///         a SOURCE-STRUCTURE assertion over the real XAML parsed as XML — the
///         same split <see cref="AmpFxRigScreenTests" /> uses, and for the same
///         reason: booting the app session for a visual-tree walk is the teardown
///         trap the default gate excludes. The runtime half is
///         <c>Perf/PlayModeScreensUiPerfSmokeTests.RigScreen_*</c>.
///     </para>
/// </summary>
public sealed class AmpFxRigEditorTests
{
    // ══ The editor FACE fold ═════════════════════════════════════════════════

    /// <summary>
    ///     The mockup opens a different editor per unit kind and switches on the
    ///     unit's <b>id</b> — a per-module table, which is the second catalogue
    ///     §7 exists to forbid. The fold here is on <see cref="PedalSkin" />,
    ///     which the rack already derives from module metadata, so it must be
    ///     TOTAL: a skin with no face would render an editor with no controls.
    /// </summary>
    [Fact]
    public void EverySkin_FoldsOntoAnEditorFace()
    {
        var faces = Enum.GetValues<RigEditorFace>();
        foreach (var skin in Enum.GetValues<PedalSkin>())
            Assert.Contains(RigEditorFaces.For(skin), faces);
    }

    /// <summary>
    ///     The three folds that are DECISIONS rather than renames. Each one
    ///     changes which editor a whole family of gear opens, so each is pinned
    ///     on its own.
    /// </summary>
    [Theory]
    [InlineData(PedalSkin.AmpHead, RigEditorFace.Amp)]
    [InlineData(PedalSkin.NeuralAmp, RigEditorFace.Amp)]
    [InlineData(PedalSkin.Cabinet, RigEditorFace.Cabinet)]
    // A namamp holding a capture of a PEDAL is a pedal, not a second amp — the
    // rack already draws it on a stompbox chassis, and the editor follows.
    [InlineData(PedalSkin.NeuralPedal, RigEditorFace.Generic)]
    [InlineData(PedalSkin.Stompbox, RigEditorFace.Generic)]
    [InlineData(PedalSkin.GraphicEq, RigEditorFace.Generic)]
    [InlineData(PedalSkin.Wah, RigEditorFace.Generic)]
    public void TheFoldedSkins_KeepTheFaceTheyWereGiven(PedalSkin skin, RigEditorFace face)
        => Assert.Equal(face, RigEditorFaces.For(skin));

    // ══ The dock's projection over a real chain ══════════════════════════════

    /// <summary>
    ///     An amp's editor takes its MODEL RAIL from the unit's own labelled
    ///     param — never a list written on this screen — and the knob row is the
    ///     unit's own <c>KnobParams</c>. Together with the extra selectors that
    ///     accounts for EVERY param the module has: a face that silently drops
    ///     one is a control the player can no longer reach.
    /// </summary>
    [Fact]
    public void TheAmpFace_ReachesEveryParamTheModuleHas()
    {
        var (rig, chain) = Rig();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "amp"));
        var dock = new RigDockUnit(rig.Nodes[0]);

        Assert.Equal(RigEditorFace.Amp, dock.Face);
        Assert.True(dock.HasModelRail);
        Assert.Equal("voicing", dock.ModelRail!.ParamId);
        Assert.False(dock.HasExtraSelectors);

        // knobs + the rail + the extras == every param, each exactly once
        Assert.Equal(
            rig.Nodes[0].Params.Count,
            dock.PrimaryKnobs.Count + dock.SecondaryKnobs.Count + 1 + dock.ExtraSelectors.Count);
    }

    /// <summary>A cabinet gets the cab face, its model rail, and its IR column —
    /// which is the SAME column an amp uses for a capture, named for the job.</summary>
    [Fact]
    public void TheCabFace_NamesItsFileColumnForWhatTheUnitLoads()
    {
        var (rig, chain) = Rig();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "cab"));
        var cab = new RigDockUnit(rig.Nodes[0]);

        Assert.Equal(RigEditorFace.Cabinet, cab.Face);
        Assert.Equal("Rig_Ir", cab.FileColumnTitleKey);
        // …and the capture LIST is an amp thing: a cabinet has no capture
        // library, so its column must not carry the "nothing downloaded" note.
        Assert.False(cab.HasCaptureColumn);
        Assert.False(cab.ShowsEmptyCaptureNote);

        rig.AddGear(chain.Catalog.Single(t => t.Id == "amp"));
        Assert.Equal("Rig_Capture", new RigDockUnit(rig.Nodes[1]).FileColumnTitleKey);
    }

    /// <summary>
    ///     A unit with no adjustable parameter at all still opens an editor —
    ///     with a sentence saying so. An editor that renders wordless is the dead
    ///     panel this layer's empty-state rule exists to remove.
    /// </summary>
    [Fact]
    public void AUnitWithNoControls_StillHasSomethingToSay()
    {
        var (rig, chain) = Rig();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "reverb"));
        var dock = new RigDockUnit(rig.Nodes[0]);

        Assert.Equal(RigEditorFace.Generic, dock.Face);
        Assert.False(dock.HasModelRail);
        Assert.True(dock.Node.HasParamControls); // reverb has two
        Assert.False(dock.HasFileParam);
    }

    // ══ The board's insertion point ══════════════════════════════════════════

    /// <summary>
    ///     Arming a gap and then adding a unit seats it THERE. Before this the
    ///     gear panel could only append, so the only way to put a boost in front
    ///     of the amp was to add it at the end and drag it back.
    /// </summary>
    [Fact]
    public void AddingGearAtAnArmedGap_SeatsTheUnitThere()
    {
        var (rig, chain) = Rig();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "amp"));
        rig.AddGear(chain.Catalog.Single(t => t.Id == "cab"));
        var screen = new AmpFxRigViewModel(rig);

        // arm the gap BEFORE the amp
        screen.InsertBeforeCommand.Execute(screen.Chain[0]);
        Assert.True(screen.HasInsertPoint);

        var overdrive = screen.GearGroups.SelectMany(g => g.Items)
            .First(i => i.Type.Id == "overdrive");
        screen.AddGearCommand.Execute(overdrive);

        Assert.Equal(["Overdrive", "Amp", "Cabinet"], rig.Nodes.Select(n => n.DisplayName));
        // …and the point is SPENT, so the next add appends again.
        Assert.False(screen.HasInsertPoint);
    }

    /// <summary>With no gap armed the gear panel appends, exactly as it did
    /// before — the insert point is additive, never a mode.</summary>
    [Fact]
    public void WithNoGapArmed_AddingGearStillAppends()
    {
        var (rig, chain) = Rig();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "amp"));
        var screen = new AmpFxRigViewModel(rig);

        var reverb = screen.GearGroups.SelectMany(g => g.Items).First(i => i.Type.Id == "reverb");
        screen.AddGearCommand.Execute(reverb);

        Assert.Equal(["Amp", "Reverb"], rig.Nodes.Select(n => n.DisplayName));
    }

    /// <summary>Clicking the SAME gap again disarms it — a toggle, so there is
    /// always a way back to "append" without adding something.</summary>
    [Fact]
    public void ClickingTheSameGapTwice_DisarmsIt()
    {
        var (rig, chain) = Rig();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "amp"));
        var screen = new AmpFxRigViewModel(rig);

        screen.InsertBeforeCommand.Execute(screen.Chain[0]);
        Assert.True(screen.HasInsertPoint);
        screen.InsertBeforeCommand.Execute(screen.Chain[0]);
        Assert.False(screen.HasInsertPoint);

        screen.InsertAtEndCommand.Execute(null);
        Assert.True(screen.HasInsertPoint);
        screen.ClearInsertPointCommand.Execute(null);
        Assert.False(screen.HasInsertPoint);
    }

    // ══ The signal strip's separators ════════════════════════════════════════

    /// <summary>
    ///     The mockup draws a <c>→</c> BETWEEN chips. An <c>ItemsControl</c> has
    ///     no "between items" slot, so each chip but the first carries the arrow
    ///     ahead of itself — and the projection has to be rebuilt with the chain,
    ///     or a removed first unit leaves a leading arrow pointing at nothing.
    /// </summary>
    [Fact]
    public void OnlyTheFirstSignalChip_HasNoArrowBeforeIt()
    {
        var (rig, chain) = Rig();
        rig.AddGear(chain.Catalog.Single(t => t.Id == "overdrive"));
        rig.AddGear(chain.Catalog.Single(t => t.Id == "amp"));
        rig.AddGear(chain.Catalog.Single(t => t.Id == "cab"));
        var screen = new AmpFxRigViewModel(rig);

        Assert.Equal(3, screen.SignalChips.Count);
        Assert.False(screen.SignalChips[0].ShowsArrow);
        Assert.All(screen.SignalChips.Skip(1), c => Assert.True(c.ShowsArrow));

        rig.RemoveCommand.Execute(rig.Nodes[0]);
        Assert.Equal(2, screen.SignalChips.Count);
        Assert.False(screen.SignalChips[0].ShowsArrow);
        Assert.Equal("Amp", screen.SignalChips[0].Node.DisplayName);
    }

    // ══ The card-width ladder ════════════════════════════════════════════════

    /// <summary>
    ///     A card is sized by how many knobs its unit has. Getting the ladder
    ///     wrong is invisible in the source and obvious in a render: one column
    ///     short and the last knobs wrap onto a row the card is not tall enough
    ///     for, so they are simply not there. Both faults shipped once during
    ///     this change and were caught in a capture, not by a test.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]   // a one-knob boost still needs a head wide enough to name it
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 5)]
    [InlineData(14, 5)]  // the ladder tops out: past five columns a card is a panel
    public void TheCardWidthLadder_GivesEachKnobCountItsColumns(int knobs, int columns)
        => Assert.Equal(columns, FireUnitCardWidthConverter.ColumnsFor(knobs));

    /// <summary>Every card is at least as wide as its columns need, and the
    /// ladder never goes backwards.</summary>
    [Fact]
    public void TheCardWidth_FitsItsColumnsAndNeverShrinks()
    {
        var previous = 0.0;
        for (var knobs = 0; knobs <= 16; knobs++)
        {
            var width = FireUnitCardWidthConverter.WidthFor(knobs);
            Assert.True(width >= previous, $"{knobs} knobs is narrower than {knobs - 1}");
            Assert.True(
                width >= FireUnitCardWidthConverter.ColumnsFor(knobs) * FireUnitCardWidthConverter.KnobCellWidth,
                $"{knobs} knobs: {width} px cannot hold its own columns");
            previous = width;
        }
    }

    // ══ The markup ═══════════════════════════════════════════════════════════

    /// <summary>
    ///     <b>The editor invents nothing.</b> Every control in the dock body
    ///     binds a param list off the unit — <c>PrimaryKnobs</c>, <c>SecondaryKnobs</c>,
    ///     <c>ModelRail.Options</c>, <c>ExtraSelectors</c>, <c>Captures</c> — so
    ///     the mockup's BRIGHT/DEEP rockers, its OUTPUT VU, its TUBES lamp, its
    ///     mic-placement pad and its rows of "advanced" chips (SIDECHAIN, DETECT,
    ///     CLIPPING DIODE, LFO WAVE, TEMPO SYNC) cannot creep in: none of them is
    ///     a param the module catalogue has, and there is no other source here to
    ///     hang one off.
    /// </summary>
    [Fact]
    public void EveryItemsSourceInTheDock_IsARealParamListOffTheUnit()
    {
        // The editor is a host over one file per family; read them all.
        var dock = RigEditorViews.Elements(TestPaths.RepoRoot()).ToList();

        // `Options` covers both spellings — the rail's own list and the one
        // inside an ExtraSelectors row's template — and it is a
        // `PedalModelSelectorViewModel`'s projection of the ABI's value labels
        // either way, never a list typed here. `FileParam.LibraryChoices` is the
        // param's own filtered view of the local capture library (the
        // "From library…" dropdown) — still the unit's data, not a table of
        // the editor's.
        // The five SPECIALISED families add their own projections (v0.63,
        // `RigDockUnit.Families.cs`) — every one of them is looked up BY ID off
        // the same node, and `MoreKnobs`/`MoreChoices` is what the unit has that
        // its layout did not name, so none of them can be a table either.
        string[] allowed =
        [
            "PrimaryKnobs", "SecondaryKnobs", "ExtraSelectors", "Options", "Captures",
            "FileParam.LibraryChoices", "CabinetSlots", "Knobs",
            // the pedal enclosure's grid: the node's params in catalogue order,
            // labelled ones as the node's own pickers (RigDockUnit.Enclosure)
            "EnclosureControls",
            // the cabinet's voicing row: the blend followed by the face knobs
            "CabVoicingKnobs",
            // the routing console's strips are the board's live lanes (RigBoardLane)
            "Routing.Lanes",
            // the specialised racks' sections and rows (RigDockUnit.Families)
            "GraphicEqBands", "ParametricSections", "DynamicsSections",
            "ExpressionChoices", "ExpressionKnobs", "VocalMainKnobs", "VocalArticulation",
            "LyricParams", "MoreKnobs", "MoreChoices",
            // the shared rack's UNIT panel: the node's own transport / trigger
            // params (RigParamRoles), the STATES of a transport, and the three
            // ABI-v34 sidechain sources. Every one of them is projected off the
            // node - the dock still owns no parameter of its own.
            "TransportParams", "MomentaryParams", "States", "Node.SidechainOptions",
            // the mic placement's four mic cards: the `micpos` node's own `mic`
            // param, one card per value (RigMicPlacementViewModel)
            "Mics"
        ];

        var sources = dock
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "ItemsSource")
            .Select(a => a.Value)
            .ToList();

        Assert.True(sources.Count >= 4, $"only {sources.Count} item sources in the module editor");
        foreach (var source in sources)
        {
            Assert.True(
                allowed.Any(a => source.Contains(a, StringComparison.Ordinal)),
                $"'{source}' is not one of the unit's own lists — the editor grew a table of its own");
        }
    }

    /// <summary>
    ///     The board draws a real signal PATH: two jacks, cable between the
    ///     units, and an insert dot in every gap. The dots are the only way to
    ///     add a unit anywhere but the end of the chain, so each one must be a
    ///     Button on a real command — a Border that looks like a button is the
    ///     fault this layer already fixed once on the calendar's plan queue.
    ///     <para>
    ///         Three dot/connector templates shared by serial and lane tiles:
    ///         the SHARED unit template's leading gap (one template, resolved
    ///         by the serial run and the lanes alike), the lane's own trailing
    ///         gap and the chain's trailing gap. An empty lane's affordance is
    ///         the full-size <c>FireLaneDropZone</c>, not a bare dot.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheBoard_DrawsARealSignalPath()
    {
        var board = ScreenElement("RigBoard");

        int WithClass(string cls) => board.DescendantsAndSelf().Count(e =>
            e.Attribute("Classes")?.Value.Split(' ').Contains(cls) == true);

        Assert.Equal(2, WithClass("FireJack"));       // IN and OUT
        // the wires: three fixed leads (IN, and either side of the region) and
        // one inside each of the three connectors. OUT has no lead of its own
        // since 2026-09-08: its connector runs the whole way to the jack so
        // the arrowhead lands on the ring (RigBoardWiresUiPerfSmokeTests).
        Assert.Equal(6, board.DescendantsAndSelf().Count(e => e.Name.LocalName == "RigWire"));
        Assert.Equal(2, WithClass("FireConnector"));  // shared unit gap · chain tail
        Assert.Equal(1, WithClass("FireLaneTail"));   // the lane's stretched gap into its mixer

        var dots = board.DescendantsAndSelf()
            .Where(e => e.Attribute("Classes")?.Value.Split(' ').Contains("FireInsertDot") == true)
            .ToList();
        Assert.Equal(3, dots.Count);
        foreach (var dot in dots)
        {
            Assert.Equal("Button", dot.Name.LocalName);
            var command = dot.Attribute("Command")?.Value;
            Assert.True(command != null && command.StartsWith("{Binding", StringComparison.Ordinal),
                "an insert dot draws a + and does nothing");
        }
    }

    /// <summary>
    ///     The split region's markup really is the parallel-lane visual: the
    ///     region panel, the two fan buses, per-lane stubs and badge, and the
    ///     lane items bound to the LANE fold (<c>BoardSegments</c> →
    ///     <c>Lanes</c> → <c>Items</c>) — never a second flat chain.
    /// </summary>
    [Fact]
    public void TheBoard_DrawsTheSplitRegionAsLanes()
    {
        var board = ScreenElement("RigBoardItems");

        int WithClass(string cls) => board.DescendantsAndSelf().Count(e =>
            e.Attribute("Classes")?.Value.Split(' ').Contains(cls) == true);

        Assert.Equal(1, WithClass("FireSplitRegion"));
        Assert.Equal(2, WithClass("FireLaneBus"));   // the split fan and the merge fan
        Assert.Equal(2, WithClass("FireLaneStub"));  // one per lane, both edges
        Assert.Equal(1, WithClass("FireLaneChip"));    // "Lane A" in the lane colour
        Assert.Equal(1, WithClass("FireLaneRow"));
        Assert.Equal(2, WithClass("FireJunctionNode")); // the Split and Merge nodes at the region's edges
        Assert.Equal(1, WithClass("FireMixerNode"));    // the lane's "Mixer A" node
        // …and the empty-lane affordance is a full-size drop zone, visible
        // exactly while the lane has nothing on it.
        Assert.Equal(1, WithClass("FireLaneDropZone"));
        var dropZone = board.DescendantsAndSelf().Single(e =>
            e.Attribute("Classes")?.Value.Split(' ').Contains("FireLaneDropZone") == true);
        Assert.Equal("Button", dropZone.Name.LocalName);
        Assert.Equal("{Binding IsEmpty}", dropZone.Attribute("IsVisible")?.Value);

        // The lanes hold the SAME node as the serial run: neither run carries an
        // ItemTemplate of its own — both resolve the board's one typed
        // PedalNodeViewModel template. Separate templates would let serial and lane interactions drift.
        var itemsRuns = board.DescendantsAndSelf()
            .Where(e => e.Attributes().Any(a =>
                a.Name.LocalName == "ItemsSource" && a.Value == "{Binding Items}"))
            .ToList();
        Assert.All(itemsRuns, run => Assert.DoesNotContain(run.Elements(),
            e => e.Name.LocalName == "ItemsControl.ItemTemplate"));

        var sources = board.DescendantsAndSelf()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "ItemsSource")
            .Select(a => a.Value)
            .ToList();
        Assert.Contains("{Binding BoardSegments}", sources);
        Assert.Contains("{Binding Lanes}", sources);
        // Two runs bind Items: the serial segment's and the lane's.
        Assert.Equal(2, sources.Count(s => s == "{Binding Items}"));
        // The flat chain is the signal strip's projection, not the board's.
        Assert.DoesNotContain("{Binding Chain}", sources);
    }

    /// <summary>
    ///     A gear row adds through the SCREEN's own command, not the rail's. The
    ///     rail's command appends and knows nothing about the armed gap, so a row
    ///     wired straight to it would draw the insert dots, arm them, and then
    ///     ignore them — the shape of bug that is invisible until someone tries
    ///     the feature.
    /// </summary>
    [Fact]
    public void EveryGearRow_AddsThroughTheScreensOwnCommand()
    {
        // The browser is a GRID of tiles (the AmpliTube layout): each row of the
        // list holds two, drawn by ONE template in the view's resources.
        var grid = ScreenElement("RigGearGroups");
        var slots = grid.Descendants().Where(e => e.Name.LocalName == "ContentControl").ToList();
        Assert.Equal(2, slots.Count);
        Assert.All(slots, slot => Assert.Equal("{StaticResource RigGearTileTemplate}", slot.Attribute("ContentTemplate")?.Value));
        // The template is its own dictionary since #240, merged by the view.
        Assert.Contains("RigGearTileTemplate.axaml", File.ReadAllText(ViewPath()), StringComparison.Ordinal);
        var template = XDocument.Load(TileTemplatePath()).Descendants().Single(e =>
            e.Name.LocalName == "DataTemplate"
            && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "RigGearTileTemplate"));
        var rows = template.Descendants()
            .Where(e => e.Name.LocalName == "Button"
                        && e.Attribute("Classes")?.Value.Split(' ').Contains("FireGearTile") == true)
            .ToList();

        Assert.Single(rows);
        foreach (var row in rows)
        {
            // the row highlights the unit (the design's list); its + adds
            Assert.Equal("{Binding $parent[UserControl].((vm:AmpFxRigViewModel)DataContext).SelectGearCommand}", row.Attribute("Command")?.Value);
            Assert.Equal("{Binding}", row.Attribute("CommandParameter")?.Value);
            var add = Assert.Single(row.Descendants(), e =>
                e.Name.LocalName == "Button" && e.Attribute("Classes")?.Value.Contains("FireGearAdd") == true);
            Assert.Equal("{Binding AddCommand}", add.Attribute("Command")?.Value);
            // …and it is handed the ROW, because the screen's command needs the
            // item to reach both the card and the rail's own add command.
            Assert.Equal("{Binding}", add.Attribute("CommandParameter")?.Value);
        }
    }

    /// <summary>
    ///     <b>A file param needs a way BACK.</b> "Choose…" can only go forwards
    ///     onto another file, so a NAM capture or a user IR loaded once could
    ///     never be taken off again and the unit could never return to its
    ///     built-in default — <c>PedalStringParamViewModel.Clear</c> existed for
    ///     exactly that and was bound by nothing, which is why it sat in
    ///     <see cref="RelayCommandReachabilityTests" />'s known-unreachable list.
    ///
    ///     <para>
    ///         The selected editor carries it beside its file picker; compact chain
    ///         tiles delegate file editing to that one surface. And it is
    ///         icon-only, so it needs a real <c>AutomationProperties.Name</c> as
    ///         well as the tooltip — a tooltip is announced on HOVER, the one
    ///         gesture the user who needs the name does not have.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("RigDockFileClear", "{Binding FileParam.HasValue}", "{Binding FileParam.ClearCommand}")]
    [InlineData("RigAmpFileClear", "{Binding FileParam.HasValue}", "{Binding FileParam.ClearCommand}")]
    public void EveryFileParamFace_CarriesAClearBesideItsPicker(
        string name, string visible, string command)
    {
        var clear = RigEditorViews.Elements(TestPaths.RepoRoot()).Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.Equal("Button", clear.Name.LocalName);
        Assert.Equal(command, clear.Attribute("Command")?.Value);

        // Only while there IS a file to clear — on an empty plate it would be a
        // control that reverts a default to itself.
        Assert.Equal(visible, clear.Attribute("IsVisible")?.Value);

        // Icon-only ⇒ BOTH names, off the SAME key, in all three resx files.
        const string key = "{localization:Loc Rig_ClearFile}";
        Assert.Equal(key, AttributeEndingIn(clear, "AutomationProperties.Name"));
        Assert.Equal(key, AttributeEndingIn(clear, "ToolTip.Tip"));
        foreach (var resx in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            Assert.Contains("\"Rig_ClearFile\"",
                File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", resx)),
                StringComparison.Ordinal);
        }

        // …and it really sits beside the picker, in the same load strip.
        Assert.Contains(clear.Parent!.Descendants(), e =>
            e.Attribute("Command")?.Value.EndsWith("PickFileCommand}", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void TheDock_HostsTheExtractedEditorForItsSelectedUnit()
    {
        var body = ScreenElement("RigDockBody");
        Assert.Contains(body.DescendantsAndSelf(), e => e.Attribute("Content")?.Value == "{Binding DockUnit}");
        Assert.Single(body.Descendants(), e => e.Name.LocalName == "RigModuleEditorView");
        Assert.Equal("vm:RigDockUnit", EditorRoot().Attributes().Single(a => a.Name.LocalName == "DataType").Value);
        // …and every family view the host can open is a view over the same unit.
        var paths = RigEditorViews.Paths(TestPaths.RepoRoot());
        Assert.True(paths.Count >= 2, "no family views under RigEditors/");
        foreach (var (path, doc) in RigEditorViews.Documents(TestPaths.RepoRoot()))
        {
            Assert.True(doc.Root!.Attributes().Single(a => a.Name.LocalName == "DataType").Value == "vm:RigDockUnit",
                Path.GetFileName(path) + " is not a view over RigDockUnit");
        }

        Assert.Contains(EditorRoot().Descendants(), e => e.Name.LocalName == "RigEditorTemplateSelector");
    }

    private static XElement EditorRoot() => XDocument.Load(RigEditorViews.HostPath(TestPaths.RepoRoot())).Root!;

    // ---- helpers -----------------------------------------------------------

    /// <summary>An attached-property attribute by its unqualified spelling —
    /// <c>AutomationProperties.Name</c> parses as a plain local name, but a
    /// namespace prefix in the file would change that, so match on the tail.</summary>
    private static string? AttributeEndingIn(XElement element, string suffix)
        => element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.EndsWith(suffix, StringComparison.Ordinal))
            ?.Value;

    private static (PedalboardViewModel Rig, FakeDspChainService Chain) Rig()
    {
        var chain = new FakeDspChainService();
        return (TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English()), chain);
    }

    private static XElement ScreenElement(string name)
    {
        var element = XDocument.Load(ViewPath()).Descendants().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.True(element != null, $"'{name}' is not in AmpFxRigView.axaml");
        return WithLaneTemplateInlined(element!);
    }

    /// <summary>
    ///     The split region's lane is its own dictionary since #240
    ///     (<c>RigBoardLaneTemplate.axaml</c>, merged by the view) and the lanes'
    ///     <c>ItemsControl</c> names it as its <c>ItemTemplate</c>. Put the template
    ///     back under that control, so the board reads as the one tree it renders.
    /// </summary>
    private static XElement WithLaneTemplateInlined(XElement element)
    {
        const string reference = "{StaticResource RigBoardLaneTemplate}";
        var copy = new XElement(element);
        var hosts = copy.DescendantsAndSelf().Where(e => e.Attribute("ItemTemplate")?.Value == reference).ToList();
        if (hosts.Count == 0) return copy;

        Assert.Contains("RigBoardLaneTemplate.axaml", File.ReadAllText(ViewPath()), StringComparison.Ordinal);
        var template = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode",
            "RigBoardLaneTemplate.axaml")).Descendants().Single(e =>
            e.Name.LocalName == "DataTemplate" && e.Attributes().Any(a => a.Name.LocalName == "Key" && a.Value == "RigBoardLaneTemplate"));
        foreach (var host in hosts) host.Add(new XElement(template));
        return copy;
    }

    private static string TileTemplatePath() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "RigGearTileTemplate.axaml");

    private static string ViewPath() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "AmpFxRigView.axaml");
}
