# The board: drag reflow, split/parallel routing, arrange geometry, rack scroll

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).

> **⚠ MOSTLY HISTORY — verified against the tree 2026-08-08.** Everything below
> about the BOARD CONTROL was written against the rack v0.43 deleted, and the
> present tense is wrong. These do not exist anywhere in the repo:
> `SplitBoardPanel`, `BoardArrangeMath`, `BoardDragGeometry`, `BoardDropZone`,
> `PedalboardView.axaml(.cs)`, and the four tests this file names as their
> guards — `PedalDragReflowTests`, `BoardDragGeometryTests`,
> `BoardArrangeMathTests`, `PedalboardBoardLayoutUiPerfSmokeTests`,
> `PedalboardBoardStaleLayoutUiPerfSmokeTests`. Do not go looking for them and
> do not "restore" them.
>
> **What is still true and still load-bearing:**
> - Both **routing INVARIANTS** — exactly ONE junction pair (`AnySplitter`, not
>   the first non-bypassed splitter; `EnsureClosingMerger` reuses; `RoutingSerial`
>   sweeps every junction) and *the routing properties are a LAYOUT input*. They
>   live on `PedalboardViewModel.Routing.cs`, not on any view, and
>   `UI/ViewModels/PlayMode/PedalboardSplitViewModelTests` is their real guard.
> - The pure `SplitRegionLayout` (`Controls/Dsp/SplitRegionLayout.cs`,
>   `SplitRegionLayoutTests`) and the pure `PedalDragMath`
>   (`Controls/Dsp/PedalDragMath.cs`) both survive the rack.
> - `GearRailTests` still gates that every `DcRigTargetCatalog.ModuleTypeIds` id
>   yields a drawable face.
>
> The live surface's drag is `RigBoardDragAndToneBrowserTests` over
> `Views/PlayMode/AmpFxRigView.axaml`. Read
> [../fire/amp-fx-rig-screen.md](../fire/amp-fx-rig-screen.md) for it.

- `Controls/Dsp/` — continued from [widgets-and-faces.md](widgets-and-faces.md):
    - **Drag = LIVE REFLOW (AmpliTube parity, 0.16).** `SplitBoardPanel` owns it;
      `PedalboardView.axaml.cs` only feeds it pointer events and commits the drop.
      Grab a pedal (past a 6 px threshold, so a click stays a click) and it leaves
      the LAYOUT FLOW: it floats under the pointer, **anchored at the grab offset**
      (never snaps to the cursor), lifted by a spring-driven scale on the
      CONTAINER's `RenderTransform` (`PedalDragMath.LiftScale` — stompbox 1.04,
      **wide amp/cab CAPPED at 1.015** so the big chassis doesn't balloon) plus the
      `DragLift` class for the deeper floating shadow. Meanwhile the panel re-runs
      `SplitRegionLayout` over a VIRTUAL order with the pedal moved to the hovered
      slot, so the neighbours' post-drop rects become their spring targets and they
      **FLOW ASIDE to open a real gap** (`BoardDropZone` paints the hole + the
      targeted branch row). **Nothing is committed while dragging** — the old code
      called `MoveTo` on every pointer move, which re-realized containers and made
      the drag snap; now the view reads `DropIndex`/`DropBranch` ONCE on release and
      calls `MoveTo` / `DropToRow` (or `AddGearAt` for a rail drop), then hands the
      float position back via `SettleFrom` so the pedal EASES into its slot. Esc, or
      a release off the rack, skips the commit and the same spring carries
      everything home. Auto-scroll near the rack edges still applies.
      - *Anti-thrash:* slots resolve against the **rest centres** — the layout with
        the dragged pedal removed, captured ONCE at drag start (react-beautiful-dnd's
        displacement model), so the neighbours you see moving can never feed back
        into the decision — plus a `SlotHysteresisPx` band
        (`PedalDragMath.ResolveRowSlot`). Never resolve against live/animated rects.
      - *The clock:* one self-contained `DispatcherTimer` (~60 fps) inside the panel,
        started on demand and **STOPPED the moment every spring is at rest** (and on
        detach). This is the sanctioned exception to the no-style-animations rule on
        hideable surfaces (a style animation's clock keeps ticking while hidden;
        this one cannot exist while idle). Keep it that way — don't add XAML
        transitions to the board containers.
      - Pure math (`DragSpring`, `ResolveRowSlot`, `LiftScale`) is unit-tested in
        `PedalDragReflowTests` (convergence, no overshoot, stalled-frame clamp,
        zero flips under boundary jitter).
      - *Two rects, never mix them up (`BoardDragGeometry`, pure +
        `BoardDragGeometryTests`).* A dragged container keeps its ARRANGED slot
        (that is what makes the neighbours reflow around the gap) and only
        RENDERS elsewhere, on its `RenderTransform`. So its `Bounds` is the
        LANDING slot, **not** where the chassis is painted. Anything that must
        follow the pedal on screen reads `SplitBoardPanel.LiveRects()` /
        `LiveRect(child)` (arranged slot + reflow spring + lift scale about the
        bottom-centre origin — the pure `BoardDragGeometry.LiftedRect`);
        anything that must follow the DROP (the landing zone) reads the arranged
        rect. Reading `Bounds` for the cables is what left orphan plug ferrules
        at stale jack positions.
      - *The floating pedal must not be clipped.* `BoardItems` (the items
        control) and the zoom `LayoutTransformControl` are explicitly
        `ClipToBounds="False"` — both clip by default and sliced the lifted
        chassis in half when it floated outside the zoomed content box. The rack
        `ScrollViewer` (and the board `Border`) still clip, so the pedal cannot
        escape the board; it just floats freely INSIDE it (auto-scroll carries
        the drag past the edges). Don't re-add a clip anywhere between the item
        container and the rack scroller.
      - *Cables during a drag:* `PedalboardView.RecomputeCables` routes
        `BoardDragGeometry.BuildCables` over the panel's LIVE rects in
        left-to-right VISUAL order, so the leads stay plugged into the pedal in
        flight and swap over a neighbour exactly when it visually passes it;
        degenerate (unlaid-out) rects are skipped instead of routed to.
      - *The landing zone* (`BoardDropZone`) is a soft accent-tinted filled slot
        with rounded edges (`BoardDragGeometry.SlotRect`) — deliberately NOT a
        dashed outline, and tinted with the app **accent token**, never the
        dragged module's body colour (a red amp painted a red dashed box that
        read as an error).
    - **Split / parallel chains (ABI v23).** The `splitter`/`merger` routing
      modules open a 2–3 branch parallel region; `PedalboardViewModel` projects
      `Nodes` into `BoardColumns` (linear pedal / junction / fanned region) via
      the pure `SplitRegionLayout` and re-lays after every structural change
      (add/remove/move/branch/junction-bypass/`enabledBranches` — the junction VM
      raises `StructureChanged`). `PedalNodeViewModel` carries `Branch`/
      `IsSplitter`/`IsMerger`/`EnabledBranches`. The board renders through
      `SplitBoardPanel` (not a StackPanel), so a split region fans body pedals
      into stacked branch rows between full-height junctions. **Drop-to-row:**
      the board drag is region-aware (`SplitBoardPanel.RowForPoint`) — dragging
      into another branch row highlights that row and REFLOWS within it, and the
      drop calls `PedalboardViewModel.DropToRow(node, index, branch)`, which does
      `MoveNode` + `SetNodeBranch` ATOMICALLY (both native, then one re-lay). A
      rail drop onto a branch row is `AddGearAt` + the same `DropToRow` tag.
      Adding a splitter
      auto-pairs a `merger` right after it (click + drop paths, `AddGear`/
      `AddGearAt`); a live region with no merger shows an `AutoMergeActive` header
      chip. `BuildCurrentRig` captures the branch tag. Junction cards carry
      branch-explaining tooltips (`GearCardViewModel.Tooltip`). VM logic tested in
      `PedalboardSplitViewModelTests`.
      - **INVARIANT — exactly ONE junction pair.** Region ownership is keyed on
        ANY splitter in the chain (`PedalboardViewModel.AnySplitter`), NOT on the
        first non-bypassed one: a bypassed splitter still OCCUPIES the chain, and
        treating it as "no region" made the routing toolbar build a SECOND
        splitter+merger pair (owner bug, 0.16). Split re-widths the existing
        splitter (re-arming a bypassed one); `EnsureClosingMerger` REUSES any
        merger the chain already carries and only adds one when there is none;
        serial (`RoutingSerial`) sweeps EVERY junction, so no leftover can be
        doubled up on next time. Never add a junction without going through those
        two seams.
      - **INVARIANT — the routing properties are a LAYOUT input, so both surfaces
        WATCH them.** `SplitBoardPanel` and `SignalChainCanvas` compute their
        geometry from each node's `Branch` / `EnabledBranches` / `Bypassed`
        (everything `SplitRegionLayout` reads). All three change WITHOUT touching
        the `Nodes` collection — a routing 2↔3 re-width, a junction bypass, a
        drop-to-row that stays in the same chain slot — so a collection-change-only
        subscription leaves the board painting the OLD routing (owner bug, 0.16;
        the canvas also caches its layout by SIZE, which a routing change doesn't
        move). The panel watches its containers' bound node VMs
        (`SplitBoardPanel.SyncNodeWatch`, unhooked in `OnDetachedFromVisualTree`)
        and the canvas widens its per-node `PropertyChanged` filter — both
        `InvalidateMeasure`. Any NEW routing-relevant node property must be added
        to BOTH filters. Pinned end-to-end by
        `PedalboardBoardStaleLayoutUiPerfSmokeTests` (headless app session ⇒ out of
        the default gate; run it explicitly).
      - **Drag lifecycle — a lost capture must END the drag.** The board/rail drags
        capture the pointer, and `PointerCaptureLost` is a DIRECT event, so
        `PedalboardView` handles it on the capturing element (`BoardItems` for the
        board drag, the view itself for the rail drag) and on detach. Without it a
        stolen capture (a popup, window deactivation, a re-realized container)
        leaves the pedal lifted AND wedges `SplitBoardPanel`'s ~60 fps reflow clock
        on forever (`OnTick` only parks once the drag ends). The release handlers
        clear their drag flags BEFORE `Capture(null)`, so a normal drop's capture
        loss finds nothing armed and never cancels its own commit — keep that order.
        Esc-cancel hands the capture back explicitly (`_capturedPointer`).
    - **Board arrange geometry — the visual contract (`BoardArrangeMath`, pure).**
      "Everything stands on rails; branch rows stack UPWARD from the baseline."
      A row's height is the tallest item IN THAT ROW (never a board-wide max — one
      248 px portrait stompbox used to inflate every band and launch the branch
      rows off the top of the rack); rows stack with a constant `RowGap` and the
      LAST branch row shares the linear pedals' baseline; items are BOTTOM-ALIGNED
      at their own measured height, so a ~150 px wide amp and a 248 px stompbox
      stand on the same rail; a branch row with NO pedals reserves only a SLIM lane
      (`EmptyRowHeight`, not the tall baseline — an empty branch borrowing the 248 px
      baseline ballooned the board to ~2× height for any split with an empty branch
      and made the full-height junctions tower over a void, the owner's "detached /
      disordered board"; a drag still grows the target row to the dragged pedal);
      a junction spans the FULL board height (it used to be
      vertically centred in the band — it floated mid-air, giving the board its
      diagonal "staircase"). `SplitBoardPanel` is only the Control-side adapter
      (measure → `BoardItem`s → `BoardArrangeMath.Compute` → arrange); rows are
      NON-uniform, so `RowForPoint`/`DropRowBand` read the returned
      `RowTops`/`RowHeights` bands (`BoardArrangeMath.RowForY`) instead of dividing
      the region evenly. Pure tests: `BoardArrangeMathTests`; an end-to-end proof
      through the real XAML lives in `PedalboardBoardLayoutUiPerfSmokeTests`
      (headless app session ⇒ out of the default gate, run it explicitly).
    - **Junction skin (`PedalSkin.Junction`).** `splitter`/`merger` render as a
      COMPACT full-height patchbay card (`Border.JunctionCard`, 84 px wide,
      `VerticalAlignment=Stretch` via `Panel.BoardItem.JunctionItem`): the
      `GearThumbnail` fork glyph, the branch-count switch (routes through the SAME
      `RoutingSplit2/3Command` as the toolbar), the module's params in a small
      popup and a compact bypass — never a portrait stompbox.
      `GearThumbKind.Junction` gives the routing ids a real thumbnail face, and
      `SignalChainCanvas` builds a `GearThumbnail` for EVERY node (a routing module
      the LAYOUT draws as a plain pedal — a bypassed splitter, a stray merger — used
      to render as an empty grey plate); the layout, not the module id, decides
      which thumbnails collapse to a junction glyph. `GearRailTests` gates that
      every `DcRigTargetCatalog.ModuleTypeIds` id yields a drawable face.
    - **Rack scroll + zoom.** The chain sits in a horizontal `ScrollViewer` (`x:Name="RackScroll"`, pedals overflow right; vertical Auto so a zoomed-up pedal never clips; **`HorizontalScrollBarVisibility="Visible"` + `AllowAutoHide="False"`** so the thin track stays put instead of appearing only on hover) wrapping a `LayoutTransformControl` whose `ScaleTransform` binds `PedalboardViewModel.Zoom` (0.5..1.5, `MinZoom`/`MaxZoom`), so zoom + horizontal scroll cooperate. A **plain mouse wheel over empty rack space scrolls the rack HORIZONTALLY** (`OnRackWheel`, bubble/not-handledEventsToo so a wheel over a knob still adjusts the knob; **Shift = faster**), and a **board/rail drag auto-scrolls the rack** when the pointer nears either edge (`AutoScrollRackDuringDrag`). The board is grounded: the rack `Border`'s background is the dark rubber-mat tile (`RigBoardTexture`) under a radial vignette — our own synthesized output, no external assets, and inside the zoomed content a `Panel` (`BoardRow`) layers two full-width `RigRail` mounting-rail bars UNDER the bottom-aligned pedal row (`VerticalAlignment="Bottom"` on the transform control, items panel and each pedal) so pedals stand on the rails instead of floating centered. Zoom has a −/slider/+ toolbar and persists as `AppConfig.PedalboardZoom` via the optional `IConfigurationService` ctor arg (null in tests = cosmetic no-op). The overlay host in `PlayModeView.axaml` is a LARGE near-fullscreen card (a 3×3 star grid ≈92% of the window, floored 1100×760, capped to the window) — unlike the small centered overlays; `PedalboardView` owns its own scroll so no outer `ScrollViewer` wraps it.
