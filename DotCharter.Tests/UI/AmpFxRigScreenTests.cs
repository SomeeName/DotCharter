using System.Xml.Linq;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     Default-gate guards for the Amp &amp; FX rig screen (§6.8,
///     <c>Amp and FX Rig.dc.html</c>) — <c>RigScreenRoot</c> in
///     <c>AmpFxRigView.axaml</c>.
///
///     <para>
///         Everything here is PURE (the identity fold, the EDITED comparison) or
///         a SOURCE-STRUCTURE assertion over the real XAML parsed as XML, the
///         same split <c>LaunchScreenTests</c> uses: booting the app session for
///         a visual-tree walk is the teardown trap the default gate excludes. The
///         runtime half — construct, attach, lay out, measure — is
///         <c>Perf/PlayModeScreensUiPerfSmokeTests.RigScreen_composesItsSixRegions</c>.
///     </para>
///
///     <para>
///         The PARAMETER-SET half of §6.8 is its own suite,
///         <see cref="AmpFxRigParameterSetTests" />.
///     </para>
/// </summary>
public sealed class AmpFxRigScreenTests
{
    // ══ The eight fixed identities ═══════════════════════════════════════════

    /// <summary>
    ///     Every one of the rig's ELEVEN engineering families lands on one of the
    ///     spec's EIGHT identities, and the fold is total — a module family with
    ///     no identity would render in the default violet and read as a routing
    ///     node.
    /// </summary>
    [Fact]
    public void EveryModuleFamily_FoldsOntoOneOfTheEightFixedIdentities()
    {
        var families = Enum.GetValues<DspModuleCategory>();
        Assert.True(families.Length >= 11, $"only {families.Length} families — the enum shrank");

        foreach (var family in families)
        {
            var identity = FireRigCategory.For(family);
            Assert.Contains(identity, FireRigCategory.DisplayOrder);
        }

        // …and all eight are reachable, so the gear panel can never print a
        // filter chip that matches nothing.
        var reached = families.Select(FireRigCategory.For).Distinct().ToList();
        Assert.Equal(FireRigCategory.DisplayOrder.Count, reached.Count);
    }

    /// <summary>
    ///     The folds that are DECISIONS rather than one-to-one renames. Pinned
    ///     individually because each one is a judgement someone could reasonably
    ///     make differently, and changing it silently recolours a whole family of
    ///     gear on the board.
    /// </summary>
    [Theory]
    [InlineData(DspModuleCategory.Filter, FireUnitCategory.Eq)]
    [InlineData(DspModuleCategory.Pitch, FireUnitCategory.Modulation)]
    [InlineData(DspModuleCategory.Routing, FireUnitCategory.UtilityRouting)]
    [InlineData(DspModuleCategory.Utility, FireUnitCategory.UtilityRouting)]
    [InlineData(DspModuleCategory.Amp, FireUnitCategory.Amps)]
    [InlineData(DspModuleCategory.Cab, FireUnitCategory.Cabinets)]
    public void TheFoldedFamilies_KeepTheIdentityTheyWereGiven(
        DspModuleCategory family, FireUnitCategory identity)
        => Assert.Equal(identity, FireRigCategory.For(family));

    /// <summary>
    ///     Every real module in the catalogue resolves to an identity that has a
    ///     declared colour token. A module whose identity had no token would
    ///     paint grey (<c>FireUnitState.ColorFrom</c>'s deliberate fallback) —
    ///     visibly wrong, but only if someone looks.
    /// </summary>
    [Fact]
    public void EveryCatalogueModule_ReachesADeclaredIdentityToken()
    {
        var ids = DcRigTargetCatalog.ModuleTypeIds;
        Assert.True(ids.Count >= 50, $"the catalogue reports {ids.Count} modules");

        foreach (var id in ids)
        {
            var key = FireUnitState.TokenKeyFor(FireRigCategory.ForModule(id));
            Assert.StartsWith("FireUnit", key, StringComparison.Ordinal);
        }
    }

    // ══ The EDITED badge ═════════════════════════════════════════════════════

    /// <summary>
    ///     A rig identical to its preset is NOT edited — including when the two
    ///     snapshots are different objects, which they always are.
    ///     <c>DcRigPreset</c> is a record over a LIST, so record equality
    ///     compares references and would call every rig edited forever; that is
    ///     the bug <see cref="RigEditState" /> exists to prevent.
    /// </summary>
    [Fact]
    public void AnUntouchedRig_IsNotEdited()
    {
        var applied = Rig(("amp", 0.5f), ("cab", 1f));
        var live = Rig(("amp", 0.5f), ("cab", 1f));

        Assert.NotSame(applied, live);
        Assert.False(RigEditState.IsEdited(live, applied));
    }

    /// <summary>Every shape of change a preset can carry flips the badge.</summary>
    [Fact]
    public void EveryKindOfChange_FlipsTheBadge()
    {
        var applied = Rig(("amp", 0.5f), ("cab", 1f));

        // a knob moved
        Assert.True(RigEditState.IsEdited(Rig(("amp", 0.7f), ("cab", 1f)), applied));
        // a unit removed
        Assert.True(RigEditState.IsEdited(Rig(("amp", 0.5f)), applied));
        // a unit added
        Assert.True(RigEditState.IsEdited(
            Rig(("amp", 0.5f), ("cab", 1f), ("reverb", 0.3f)), applied));
        // the order changed
        Assert.True(RigEditState.IsEdited(Rig(("cab", 1f), ("amp", 0.5f)), applied));
        // a unit bypassed
        Assert.True(RigEditState.IsEdited(
            new DcRigPreset([
                new DcRigModule("amp", new Dictionary<string, float> { ["gain"] = 0.5f }, true),
                new DcRigModule("cab", new Dictionary<string, float> { ["gain"] = 1f })
            ]),
            applied));
        // a string param (a NAM capture / an IR path) changed
        Assert.True(RigEditState.IsEdited(
            new DcRigPreset([
                new DcRigModule("amp", new Dictionary<string, float> { ["gain"] = 0.5f })
                {
                    StringParams = new Dictionary<string, string> { ["modelPath"] = "x.nam" }
                },
                new DcRigModule("cab", new Dictionary<string, float> { ["gain"] = 1f })
            ]),
            applied));
        // a branch tag changed
        Assert.True(RigEditState.IsEdited(
            new DcRigPreset([
                new DcRigModule("amp", new Dictionary<string, float> { ["gain"] = 0.5f }) { Branch = 2 },
                new DcRigModule("cab", new Dictionary<string, float> { ["gain"] = 1f })
            ]),
            applied));
    }

    /// <summary>
    ///     With no preset applied, an EMPTY board is not "edited" — that is the
    ///     state a fresh engine starts in, and badging it would put EDITED on the
    ///     screen before the player has touched anything.
    /// </summary>
    [Fact]
    public void AnEmptyBoardWithNoPreset_IsNotEdited()
    {
        Assert.False(RigEditState.IsEdited(new DcRigPreset([]), null));
        Assert.False(RigEditState.IsEdited(null, null));
        Assert.True(RigEditState.IsEdited(Rig(("amp", 0.5f)), null));
    }

    private static DcRigPreset Rig(params (string Id, float Gain)[] modules)
        => new(modules
            .Select(m => new DcRigModule(m.Id, new Dictionary<string, float> { ["gain"] = m.Gain }))
            .ToList());

    // ══ The markup ═══════════════════════════════════════════════════════════

    /// <summary>
    ///     The spec's six regions are all present and named, so the layout smoke
    ///     test has something to look up and a renamed region cannot silently
    ///     stop being measured.
    /// </summary>
    [Fact]
    public void TheScreen_DeclaresAllSixRegions()
    {
        var root = ScreenRoot("RigScreenRoot");
        string[] regions =
        [
            "RigHeader", "RigSignalStrip", "RigBoard", "RigDock", "RigGearPanel", "RigFooter"
        ];

        var named = root.DescendantsAndSelf()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "Name")
            .Select(a => a.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(named.Count >= 10, $"only {named.Count} named elements under the rig root");
        foreach (var region in regions) Assert.Contains(region, named);
    }

    /// <summary>
    ///     <b>No stub data.</b> Every TEXT the screen prints is either a
    ///     localization key or a binding — never a literal string an agent typed
    ///     in.
    ///     <para>
    ///         The allowed exceptions are MARKS, not data: the two compare-slot
    ///         letters, the close glyph, the add sign, and the ▲▼ of the
    ///         previous/next-tone steppers. A mark names a direction or a shape
    ///         and says nothing about the rig, so localizing it would only give
    ///         a translator a chance to break an arrow.
    ///     </para>
    /// </summary>
    [Fact]
    public void NoValueOnTheScreen_IsATypedLiteral()
    {
        var texts = RigElements()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName is "Text" or "Content" or "PlaceholderText")
            .Select(a => a.Value.Trim())
            .Where(v => v.Length > 0)
            .ToList();

        Assert.True(texts.Count >= 25, $"only {texts.Count} text values found — the walk found nothing");

        // "→" joins them: it is the signal strip's separator between two chips,
        // a direction and not a value, exactly like the steppers' arrows.
        // "−" is the zoom cluster's minus — the other half of its "+".
        // "←" is the editor header's previous-module arrow, "/" its breadcrumb
        // separator, "···" its action-menu mark — shapes, not values.
        string[] allowedMarks = ["A", "B", "✕", "×", "+", "−", "▲", "▼", "→", "←", "/", "···", "★", "⛶"];
        foreach (var text in texts)
        {
            if (allowedMarks.Contains(text)) continue;
            Assert.True(
                text.StartsWith("{localization:Loc", StringComparison.Ordinal)
                || text.StartsWith("{Binding", StringComparison.Ordinal)
                || text.StartsWith("{DynamicResource", StringComparison.Ordinal),
                $"'{text}' is a literal on the rig screen — every value comes from the real chain");
        }
    }

    /// <summary>
    ///     The knobs bind <c>KnobParams</c> / <c>Params</c> — the catalogue's own
    ///     list. A <c>FireKnob</c> whose <c>Label</c> were a literal would be a
    ///     hand-written parameter set creeping back in, which is exactly what
    ///     §7 corrected.
    /// </summary>
    [Fact]
    public void EveryKnobOnTheScreen_IsMaterializedFromTheCatalogue()
    {
        var knobs = RigElements()
            .Where(e => e.Name.LocalName == "FireKnob")
            .ToList();

        // One or more templates per editor family view (the host opens one view
        // per family: pedal, amp, cabinet, routing and the five specialised
        // racks); what matters is that every one of them binds the catalogue.
        Assert.True(knobs.Count >= 7, $"only {knobs.Count} FireKnob templates across the editor views");

        foreach (var knob in knobs)
        {
            foreach (var name in new[] { "Value", "Minimum", "Maximum", "Label", "ValueText" })
            {
                var attr = knob.Attribute(name);
                Assert.True(attr != null, $"a FireKnob has no {name}");
                Assert.StartsWith("{Binding", attr!.Value, StringComparison.Ordinal);
            }
        }

        // …and the two ItemsSources they hang off are the real param lists.
        var sources = RigElements()
            .SelectMany(e => e.Attributes())
            .Where(a => a.Name.LocalName == "ItemsSource")
            .Select(a => a.Value)
            .ToList();
        Assert.Contains("{Binding PrimaryKnobs}", sources);
        Assert.Contains("{Binding SecondaryKnobs}", sources);
        Assert.Contains("{Binding Knobs}", sources);
    }

    /// <summary>
    ///     Bypass is set ONCE per card, as the inherited attached pair, and never
    ///     applied by halves. Every element that carries
    ///     <c>FireUnitState.Category</c> under this screen must be a card/chip
    ///     root that also carries the bypass — or be the gear list, where a row
    ///     is not a chain node and has no bypass to show.
    /// </summary>
    [Fact]
    public void TheUnitIdentity_IsSetAsAPairOnTheCardRoot()
    {
        var withCategory = RigElements()
            .Where(e => e.Attributes().Any(a => a.Name.LocalName.EndsWith("Category", StringComparison.Ordinal)
                                                && a.Name.ToString().Contains("FireUnitState", StringComparison.Ordinal)))
            .ToList();

        Assert.True(withCategory.Count >= 3,
            $"only {withCategory.Count} elements carry a unit identity");

        foreach (var element in withCategory)
        {
            var category = element.Attributes()
                .First(a => a.Name.ToString().Contains("FireUnitState.Category", StringComparison.Ordinal));
            Assert.StartsWith("{Binding", category.Value, StringComparison.Ordinal);
        }

        var card = Assert.Single(withCategory, e => e.Attribute("Classes")?.Value.Split(' ').Contains("FireUnitCard") == true);
        Assert.Equal("{Binding Bypassed}", card.Attributes().Single(a => a.Name.ToString().Contains("FireUnitState.IsBypassed", StringComparison.Ordinal)).Value);
    }

    /// <summary>
    ///     The selected module editor's file column carries the "From library…" dropdown over the local
    ///     capture library, so an already-downloaded capture/IR never has to go
    ///     back through the OS file picker. Each one binds the SAME
    ///     <c>SelectedLibraryChoice</c> the view model mirrors on commit, hides
    ///     when the library offers nothing for that param, and is named for
    ///     automation (a placeholder is not a name).
    /// </summary>
    [Fact]
    public void TheSelectedEditor_OffersTheLibraryDropdown()
    {
        var combos = RigElements()
            .Where(e => e.Name.LocalName == "ComboBox")
            .Where(e => (e.Attribute("ItemsSource")?.Value ?? string.Empty)
                .Contains("LibraryChoices", StringComparison.Ordinal))
            .ToList();

        // File editing belongs to the selected module: one dropdown per family
        // view that loads a file (the pedal editor and the amp face), never on the board.
        Assert.NotEmpty(combos);

        foreach (var combo in combos)
        {
            // Exact bindings, not Contains — "SelectedLibraryChoiceTypo" must fail.
            Assert.Matches(@"^\{Binding (FileParam\.)?LibraryChoices\}$",
                combo.Attribute("ItemsSource")?.Value ?? string.Empty);
            Assert.Matches(@"^\{Binding (FileParam\.)?SelectedLibraryChoice\}$",
                combo.Attribute("SelectedItem")?.Value ?? string.Empty);
            // Shown only while there is no searchable capture LIST beside the gear
            // (RigDockUnit.ShowsLibraryCombo = HasLibraryChoices && !HasCaptures):
            // the same library twice on one face is what the owner flagged (2026-09-08).
            Assert.Equal("{Binding ShowsLibraryCombo}", combo.Attribute("IsVisible")?.Value);
            Assert.Equal("{localization:Loc Rig_FromLibrary}",
                combo.Attribute("PlaceholderText")?.Value);
            Assert.Equal("{localization:Loc Rig_FromLibrary}",
                combo.Attributes().FirstOrDefault(a =>
                    a.Name.LocalName == "AutomationProperties.Name")?.Value);
        }
    }

    [Fact]
    public void MetersReadRealInputAndSelectedModuleTelemetry()
    {
        var bindings = RigElements().Where(e => e.Name.LocalName == "FireLevelMeter")
            .Select(e => e.Attribute("LevelDb")?.Value).ToArray();
        // each board card's output (#299), footer IN, footer OUT (the last unit's
        // output), then the editor's module in/out pair.
        Assert.Equal(
            new[] { "{Binding BoardOutputDb}", "{Binding InputLevelDb}", "{Binding Rig.OutputMeters.OutputDb}", "{Binding InputDb}", "{Binding OutputDb}" },
            bindings);
    }

    // ══ The save dialog ══════════════════════════════════════════════════════

    /// <summary>
    ///     The "Save tone" dialog is opened from TWO places — the header's Save
    ///     button and the drawer's "Save as new…" — and it snapshots the LIVE
    ///     RIG (<c>ConfirmSave</c> calls <c>_captureRig()</c>), not anything the
    ///     drawer has selected. Its first home was inside the drawer's subtree,
    ///     so the header button with the drawer CLOSED flipped
    ///     <c>IsSaveDialogOpen</c> on a dialog inside an invisible panel:
    ///     nothing appeared, Esc silently ate a keypress closing it, and an
    ///     un-cancelled dialog sat waiting for the next drawer open. It is a
    ///     screen-level modal now and must stay outside the drawer.
    /// </summary>
    [Fact]
    public void TheSaveDialog_IsAScreenModal_VisibleWithTheDrawerClosed()
    {
        var dialog = ScreenRoot("RigBrowserSaveDialog");

        Assert.DoesNotContain(dialog.Ancestors(), e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RigToneBrowser"));

        // Its visibility is the browser's own flag reached from the SCREEN's
        // context — never gated on the drawer being open.
        Assert.Equal("{Binding Rig.Browser.IsSaveDialogOpen}",
            dialog.Attribute("IsVisible")?.Value);
    }

    // ══ The tone drawer's interior ═══════════════════════════════════════════

    /// <summary>
    ///     The drawer pads its interior ONCE and its rail is the dense taxonomy
    ///     variant. It shipped 352 px tall with <c>FirePadPanel</c> applied
    ///     twice (<c>FirePanel</c>'s own 22 plus a duplicate on the inner
    ///     border) and data-list row insets on a 12-row FIXED rail — which left
    ///     a ~160 px body showing three of twelve categories behind an
    ///     auto-hiding scrollbar. That is the owner's "scrolling through the
    ///     type of tones is really bad", and this pins all three halves of the
    ///     fix.
    ///
    ///     <para>
    ///         Since v0.48.5 the height is the ROW's, not the Border's: the
    ///         drawer must carry NO fixed <c>Height</c>, or the splitter would
    ///         resize a row the drawer then overflowed. Since W1 that row is the
    ///         WORKSPACE's — the drawer and the module editor share it — so the
    ///         floor is <see cref="RigWorkspaceViewModel.MinHeight" />,
    ///         which is what keeps a drag from re-creating the starved body.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheDrawer_PadsOnce_AndKeepsTheWholeRailOnScreen()
    {
        var drawer = ScreenRoot("RigToneBrowser");
        Assert.Null(drawer.Attribute("Height"));
        // The drawer lives in the Tones mode now, and the workspace below the
        // board holds the unit inspector: the board is the editor, so the
        // default gives the board the height and the inspector a usable row.
        Assert.True(RigWorkspaceViewModel.DefaultHeight >= 340,
            "the workspace shrank below a usable inspector row");
        Assert.True(RigWorkspaceViewModel.MinHeight >= 260,
            "the resize floor would let a drag starve the drawer's body");

        var inner = drawer.Elements().Single();
        Assert.Null(inner.Attribute("Padding"));

        var rail = drawer.Descendants().Single(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RigBrowserRail"));
        Assert.Contains("DenseRows", rail.Attribute("Classes")?.Value, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <b>The tone library is height-resizable</b> (owner, v0.48.5: "I want
    ///     the tone library resizable in height, like a GridSplitter or
    ///     something"). Four source facts make that work and each one is silent
    ///     when it breaks:
    ///     <list type="number">
    ///         <item>a <c>GridSplitter</c> exists, in the row above the drawer;</item>
    ///         <item>it resizes ROWS with <c>PreviousAndNext</c> — the default
    ///         <c>BasedOnAlignment</c> over a stretched splitter is the same
    ///         thing, but stating it survives someone giving it an alignment;</item>
    ///         <item>it is gated on the drawer being open, because with the
    ///         module dock in that row (Auto-sized by its content) a live handle
    ///         would appear to do nothing;</item>
    ///         <item>the BOARD row keeps a <c>MinHeight</c> — the half of the
    ///         resize guarantee that lives in the markup.</item>
    ///     </list>
    /// </summary>
    [Fact]
    public void TheToneLibrary_IsResizable_ByASplitterAboveIt()
    {
        var splitter = ScreenRoot("RigBrowserSplitter");
        Assert.Equal("GridSplitter", splitter.Name.LocalName);
        Assert.Equal("Rows", splitter.Attribute("ResizeDirection")?.Value);
        Assert.Equal("PreviousAndNext", splitter.Attribute("ResizeBehavior")?.Value);
        // ALWAYS LIVE since W1: the workspace below it always has a height, so
        // the `IsSplitterVisible` gate this used to assert is gone. A divider
        // that comes and goes was a symptom of the row collapsing to nothing,
        // which is what moved the routing canvas.
        Assert.Null(splitter.Attribute("IsVisible"));

        // It sits in the row directly above the WORKSPACE's, in the same grid —
        // the drawer is inside that workspace now, not a sibling of the splitter.
        var workspace = ScreenRoot("RigWorkspace");
        // ScreenRoot re-loads the document per call, so the two parents are
        // equal by NAME, never by reference.
        Assert.Equal("RigFlatBody", ParentName(splitter));
        Assert.Equal("RigFlatBody", ParentName(workspace));
        // W4: the tone surface left the workspace for its own MODE — which is
        // its own VIEW since 2026-09-27, hosted as RigTonesMode in the screen.
        Assert.Equal("RigTonesLayout", ParentName(ScreenRoot("RigToneBrowser")));
        Assert.Equal("RigTonesView", ScreenRoot("RigTonesMode").Name.LocalName);
        Assert.Equal("RigWorkspace", ParentName(ScreenRoot("RigDock")));
        var splitterRow = int.Parse(splitter.Attribute("Grid.Row")!.Value);
        var workspaceRow = int.Parse(workspace.Attribute("Grid.Row")!.Value);
        // The AmpliTube layout (2026-09-27): the workspace — the STAGE and its gear
        // bar — is on TOP and the routing map below, so the divider sits directly
        // UNDER the workspace and directly above the map.
        Assert.Equal(workspaceRow + 1, splitterRow);
        var mapRow = splitterRow + 1;
        Assert.Equal(mapRow, int.Parse(ScreenRoot("RigSignalStrip").Attribute("Grid.Row")!.Value));
        Assert.Equal("RigFlatBody", ParentName(ScreenRoot("RigSignalStrip")));

        // The map row's floor: the workspace may never take the map's last
        // pixels, because the map is where the chain is read and rearranged.
        var rows = splitter.Parent!.Elements()
            .Single(e => e.Name.LocalName == "Grid.RowDefinitions").Elements().ToList();
        Assert.True(rows.Count > mapRow, "the map's row definition is missing");
        Assert.True(double.Parse(rows[mapRow].Attribute("MinHeight")!.Value) >= 120,
            "the map row lost its MinHeight — a drag can now squeeze it to nothing");
        Assert.Equal("*", rows[mapRow].Attribute("Height")?.Value);
    }

    // ══ Board zoom + horizontal scroll ═══════════════════════════════════════

    /// <summary>
    ///     A chain longer than the viewport must be reachable: the board's
    ///     ScrollViewer scrolls horizontally, and the persisted rack zoom
    ///     (<c>PedalboardViewModel.Zoom</c>, <c>AppConfig.PedalboardZoom</c> —
    ///     which the Fire screen shipped WITHOUT wiring) scales the row through
    ///     the <c>LayoutTransformControl</c> the code-behind pushes into. The
    ///     transform is code-pushed on purpose: a <c>{Binding}</c> inside a
    ///     <c>Transform</c> has no DataContext to inherit and resolves nothing,
    ///     silently.
    /// </summary>
    [Fact]
    public void TheBoard_ScrollsHorizontally_AndHostsTheZoomTransform()
    {
        var scroll = ScreenRoot("RigBoardScroll");
        Assert.Equal("ScrollViewer", scroll.Name.LocalName);
        // `Auto` + no auto-hide since W1. The half that mattered in the v8 pass
        // is AllowAutoHide=False — a bar that IS needed must stay solid instead
        // of fading to 0.2 and reading as "the rig ends here". `Visible`
        // reserved the track under every chain, including the ones the fit
        // (AmpFxRigView.BoardFitScale) now keeps inside the canvas.
        // The styled track is `ScrollViewer.FireBoardScroll` in FireRigBoard.axaml —
        // scoped, so the app-wide fading chrome is untouched.
        Assert.Equal("Auto", scroll.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Equal("False", scroll.Attribute("AllowAutoHide")?.Value);
        Assert.Contains("FireBoardScroll",
            scroll.Attribute("Classes")?.Value.Split(' ') ?? []);

        var zoomHost = scroll.DescendantsAndSelf().SingleOrDefault(e =>
            e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RigBoardZoomHost"));
        Assert.True(zoomHost != null, "RigBoardZoomHost left the board's ScrollViewer");
        Assert.Equal("LayoutTransformControl", zoomHost!.Name.LocalName);
    }

    /// <summary>
    ///     The footer's zoom cluster drives the view model's own clamped,
    ///     persisted commands — the members must exist AND be bound, or the
    ///     cluster is three buttons that cannot be pressed (the
    ///     <c>CoopRigApplier</c> shape).
    /// </summary>
    [Theory]
    [InlineData("Rig.ZoomInCommand")]
    [InlineData("Rig.ZoomOutCommand")]
    [InlineData("Rig.ZoomResetCommand")]
    [InlineData("Rig.ZoomPercentLabel")]
    public void TheHeaderZoomCluster_BindsTheRealZoomMembers(string bindingPath)
    {
        // The design puts zoom in the routing header, not the footer.
        var header = ScreenRoot("RigSignalStrip");
        Assert.Contains(header.DescendantsAndSelf().SelectMany(e => e.Attributes()),
            a => a.Value.Contains(bindingPath, StringComparison.Ordinal));

        var member = bindingPath["Rig.".Length..].Replace("Command", "");
        Assert.True(
            typeof(PedalboardViewModel).GetProperty(bindingPath["Rig.".Length..]) != null
            || typeof(PedalboardViewModel).GetMethod(member) != null,
            $"PedalboardViewModel.{bindingPath["Rig.".Length..]} is gone — the header binding is a silent no-op");
    }

    /// <summary>
    ///     <b>The rig's value controls and its two custom-drawn METERS carry an
    ///     accessible name.</b> <c>UI/AccessibleNameTests</c> is the repo-wide
    ///     guard, but its unconditional <c>ComboBox</c>/<c>Slider</c>/
    ///     <c>ToggleSwitch</c> half is scoped to <c>Views/Settings</c> and its
    ///     icon-button half only looks at controls whose FACE is a glyph — so
    ///     three combo boxes, three text boxes and both meters on this screen
    ///     announced as <c>ComboBox ""</c> while rendering perfectly.
    ///
    ///     <para>
    ///         The two meters are the sharpest case: a custom-drawn
    ///         <c>Control</c> has no content a peer could fall back on, and the
    ///         "IN"/"TUNER" eyebrow beside each is a SIBLING <c>TextBlock</c>,
    ///         which names nothing. And a <c>PlaceholderText</c> is not a name
    ///         either — it is gone the moment there is any text in the box.
    ///     </para>
    ///
    ///     <para>
    ///         Scoped to this screen on purpose: the repo-wide backlog is a
    ///         different (and much larger) change, tracked in
    ///         <c>docs/agents/ui/misc-surfaces/diagnostics-rtl-scale-palette.md</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryValueControlAndMeterOnTheRig_CarriesAnAccessibleName()
    {
        string[] needsAName =
        [
            "ComboBox", "Slider", "ToggleSwitch", "TextBox", "ListBox",
            "FireLevelMeter", "FireTunerMeter"
        ];

        var offenders = RigElements()
            .Where(e => needsAName.Contains(e.Name.LocalName, StringComparer.Ordinal))
            // A `<ComboBox.ItemTemplate>` is a property element, not a control.
            .Where(e => !e.Name.LocalName.Contains('.', StringComparison.Ordinal))
            .Where(e => !e.Attributes().Any(a =>
                a.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy"))
            .Select(e => $"{e.Name.LocalName} at line " +
                         $"{((System.Xml.IXmlLineInfo)e).LineNumber}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "AmpFxRigView controls with no accessible name — each announces as " +
            $"'<type> \"\"':{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", offenders)}");
    }

    /// <summary>
    ///     The rig's code-behind REPORTS ITS SURFACE LIVENESS to the tuner.
    ///
    ///     <para>
    ///         <b>Found 2026-08-15:</b> <c>RigTunerViewModel.SetSurfaceLive</c>
    ///         had no product caller anywhere, so the live-surface set was
    ///         permanently empty, the 66 ms poll could never start and the
    ///         footer's <c>FireTunerMeter</c> needle never moved. The gating
    ///         contract was pinned and green throughout
    ///         (<c>PedalboardAuditRegressionTests
    ///         .Rig_tuner_polls_only_while_open_and_a_host_surface_is_live</c>) —
    ///         a passing contract test over a caller that did not exist.
    ///     </para>
    ///     <para>
    ///         It must go through <c>EffectiveVisibilityWatcher</c>, never a
    ///         <c>change.Property.Name == nameof(IsEffectivelyVisible)</c> branch:
    ///         that property is a computed CLR property in Avalonia 12 and raises
    ///         nothing, which is the dead-code shape the watcher exists to end.
    ///         Comments are stripped first — a <c>see cref</c> is exactly as inert
    ///         as no call at all.
    ///     </para>
    ///     <para>
    ///         The RUNTIME half — navigate to the screen, assert the poll starts
    ///         and stands down again — is
    ///         <c>Perf/PlayModeScreensUiPerfSmokeTests
    ///         .RigScreen_ReportsItsOwnLiveness_SoTheFooterTunerActuallyReads</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheRigCodeBehind_ReportsItsSurfaceLivenessToTheTuner()
    {
        var source = SourceText.StripCommentLines(File.ReadAllText(CodeBehindPath()));

        Assert.Contains("SetSurfaceLive", source, StringComparison.Ordinal);
        Assert.Contains("new EffectiveVisibilityWatcher", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(IsEffectivelyVisible)", source, StringComparison.Ordinal);

        // …and it OPENS the tuner when live. The poll gates on IsOpen as well as
        // on a live host, and the only other opener is selecting a Pitch-family
        // pedal — so liveness alone left the footer needle still on every rig
        // without one. The footer draws the tuner permanently: live ⇒ open.
        Assert.Contains("_liveTuner.Open()", source, StringComparison.Ordinal);
    }

    // Read every actual view root — the screen, the editor host, every editor
    // family view and the meters; the host's child type is not XML inclusion.
    private static IEnumerable<XElement> RigElements()
    {
        var paths = new List<string>
        {
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "AmpFxRigView.axaml"),
            TonesViewPath(),
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "RigNodeMetersView.axaml")
        };
        paths.AddRange(RigEditorViews.Paths(TestPaths.RepoRoot()));
        var primaryKnobs = false;
        foreach (var path in paths)
        {
            var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
            Assert.NotNull(doc.Root);
            primaryKnobs |= doc.Descendants().Any(e => e.Attribute("ItemsSource")?.Value == "{Binding PrimaryKnobs}");
            foreach (var element in doc.Root!.DescendantsAndSelf()) yield return element;
        }
        Assert.True(primaryKnobs, "no editor view renders PrimaryKnobs");
    }

    private static string CodeBehindPath() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "AmpFxRigView.axaml.cs");

    private static string? ParentName(XElement e) =>
        e.Parent?.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;

    /// <summary>A named element of the rig SCREEN — the rig view or its Tones
    /// view (its own file since 2026-09-27), which together are the screen.</summary>
    private static XElement ScreenRoot(string name)
    {
        var root = new[] { ViewPath(), TonesViewPath() }
            .SelectMany(p => XDocument.Load(p).Descendants())
            .SingleOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
        Assert.True(root != null, $"'{name}' is not in AmpFxRigView.axaml or RigTonesView.axaml");
        return root!;
    }

    private static string TonesViewPath() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "RigTonesView.axaml");

    /// <summary>
    ///     The rig's own view since v0.43. It was 1,259 lines inline in
    ///     <c>PlayModeView.axaml</c> before that, which is exactly why the chart
    ///     editor kept opening the retired rack instead: markup nested in
    ///     another screen is not a thing a second host can name.
    /// </summary>
    private static string ViewPath() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Views", "PlayMode", "AmpFxRigView.axaml");
}
