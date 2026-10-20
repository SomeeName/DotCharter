# Amp & FX rig + tone rig (UI router)

> **v0.43 — there is ONE rig surface now.** The retired rack
> (`PedalboardView.axaml`) is deleted, and with it its `Pedal*` material tokens,
> its visual-language guide, and fifteen `Controls/Dsp/` controls nothing else
> drew (the enclosure, the dial, the treadle, the split board, the cable layer
> and their geometry). The live surface is the Fire
> **`Views/PlayMode/AmpFxRigView.axaml`** over `AmpFxRigViewModel`, hosted by
> BOTH `PlayModeView` and the chart editor's overlay in `MainWindow.axaml`.
>
> **Why the rack outlived its replacement for so long:** the Fire rig was 1,259
> lines of markup nested inside `PlayModeView`, and markup nested in another
> screen is not a type a second host can name — so the editor's overlay had
> nothing else to host and the ViewLocator went on resolving the old view from
> the old view model. The app looked like it shipped two pedalboards because,
> from the editor, it did.
>
> **`docs/agents/ui/pedalboard-visual-language.md` went with it** — it is the
> rack's material/lighting rulebook (its `R1`/`R3` rules and `§8` are still
> cited from `Styles/Resources.axaml`, `Views/Shell/ShellStyles.axaml`,
> and `Views/PlayMode/PlayModeView.axaml`, because
> the lit-surface grammar outlived the rack). It was deleted in commit
> `f9af5bc0`; read it with
> `git show f9af5bc0^:docs/agents/ui/pedalboard-visual-language.md`.
>
> Everything below about the CHAIN — `PedalboardViewModel`, airspace and the
> freeze-frame, split/parallel routing invariants, NAM captures, the preset
> browser, the rig tuner, param marshalling — is still current: those live on
> the shared singleton, not on either view. **Anything describing pedal FACES,
> skins, enclosures, knob rendering or the split BOARD is history**; the Fire
> module editor has one face and folds a unit onto `RigEditorFaces.For(skin)`
> for its chrome only. For the live layer read
> [fire/amp-fx-rig-screen.md](fire/amp-fx-rig-screen.md).

**Read this before touching any board card, gear panel, chain builder,
junction/routing surface, or DSP param binding** — it carries the airspace,
hit-testing, drag-geometry and param-marshalling rules.

**This file is a ROUTER.** What stays here is the ONE-rig rule that decides
where a new capability goes; the per-surface detail is in
[`pedalboard/`](pedalboard/), one file per row of the table at the bottom.

**`PedalboardViewModel.cs` is THIRTEEN files, not one 188 KB read** (ten hold the class itself; `.Node`, `.Params` and `.BoardColumns` hold the card, param and column types) — open the one
that owns your change rather than the root:

| File | What lives there |
|---|---|
| `PedalboardViewModel.cs` | The injected services + constructor, rack zoom, the panel toggles, master bypass, `Sync` / `ResyncAll` and `RebuildBoardColumns`, pedal selection. **`Sync` is INCREMENTAL since 0.59** — it diffs the chain by native NodeId, keeps a surviving node's `PedalNodeViewModel` (mirroring params/bypass/branch from the service, never re-pushing to native) and keeps the selection while its node survives, so an add inflates ONE card and a tone switch reuses the shared modules. The full-teardown path is `ResyncAll`; a neural amp whose `modelPath` changed rebuilds its ONE card (skin + faceplate resolve at construction). Guarded by `PedalboardIncrementalSyncTests`. **It also re-asserts the master bypass** — see the bullet below |
| `.Seats.cs` | The SEAT picker (0.57; dormant since couch co-op was removed 2026-09-19 — nothing publishes a second seat) — `SetSeats`, the repoint + `ResyncAll()` (**never the incremental `Sync`** — node ids are per-chain and the reused card would keep editing the seat it was built against). See the seat note at the bottom of this file |
| `.Routing.cs` | Split/parallel routing: `RoutingSerial`/`Split2`/`Split3`, `AnySplitter`, `EnsureClosingMerger` |
| `.ChainEdits.cs` | `AddGear*` / `Duplicate` / `Replace` / `Remove` (+ ONE level of undo: `LastRemoved` / `CanUndoRemove` / `UndoRemove`) / `MoveUp`/`MoveDown`/`MoveTo` / `DropToRow`. Duplicate and Replace both go through `AddGearAt` FIRST and only then copy the unit (params, string params, bypass, branch) or remove the old node, so a refused native call leaves the board as it was; a routing JUNCTION is neither duplicated nor replaced, and is never snapshotted for undo (half a splitter/merger pair put back alone opens a region with no close). The undo snapshot is dropped by `ResyncAll` and by a preset apply — it holds a chain INDEX read off a board that is gone |
| `.NeuralAmp.cs` | The bounded namamp status poll + the capture-manifest lookup |
| `.Presets.cs` | `BuildCurrentRig` (`DcRigSnapshot.FromChain` — WITH the scenes), `OnPresetApplied`, the A/B compare slots |
| `.History.cs` | Undo / redo over whole-rig snapshots (`RigHistory`, `RigEditState.IsSameState`; `CommitHistoryStep` for a discrete gesture) and the unit clipboard |
| `.Scenes.cs` | SCENES A–H: `SceneSlots`, `SelectSceneAt` / `RenameScene` / `StoreScene` / `ClearScene`, `RefreshScenes` — see [pedalboard/scenes-and-setlists.md](pedalboard/scenes-and-setlists.md) |
| `.Spillover.cs` | The rig-wide spillover switch (`Spillover`, `CanToggleSpillover`, `SpilloverTip`) — written to every seat's chain, and its availability is re-raised on engine attach |
| `.Node.cs` | `PedalNodeViewModel` — one pedal card |
| `.BoardColumns.cs` | `BoardColumnViewModel` / `PedalColumnViewModel` / `RegionColumnViewModel` / `RegionRowViewModel`. `RebuildBoardColumns` also raises **`BoardStructureChanged`** — one signal per finished structural gesture, which is what the Fire screen's lane fold (`AmpFxRigViewModel.BoardSegments`, its own partial `AmpFxRigViewModel.BoardSegments.cs`) hangs off — see [fire/amp-fx-rig-screen.md](fire/amp-fx-rig-screen.md) § "The board folds into LANES" |
| `.Params.cs` | `PedalParamViewModel` + `PedalStringParamViewModel` |
| `.MidiControls.cs` | Shared MIDI input bridge and owned control disposal; `HandleMidiControl` (the scene CC, then the knob assignments) and `HandleMidiProgram` (setlist song n / own tone n) |

**A self-contained cluster leaves as a COLLABORATOR, not a partial**
(`Infrastructure/SplitTypePartialRatchetTests` pins the partial set; it may only shrink).
`RigWorkspaceViewModel` (the stage/map divider's persisted height, exposed as
`Workspace`; bind `Rig.Workspace.Height`) left that way. The collaborators that
were already their own view models — `Setlists` (`RigSetlistsViewModel`, see
[pedalboard/scenes-and-setlists.md](pedalboard/scenes-and-setlists.md)),
`Controls` (`RigMacroBankViewModel`), `Tempo` (`RigTempoViewModel`),
`ModuleMeters` / `OutputMeters` (`RigNodeMetersViewModel`), `BoardMeters`
(`RigBoardMetersViewModel`) — are declared in the
ROOT beside `RigTuner` and `Workspace`; their one-property partials are gone.

The partials that STAY share too much with the rest of the class to leave
without seams that would reorder the sync / engine-attach paths (counted over
comment-stripped source, 2026-10-03: members it USES from the root and siblings
+ members of it the others use):

| Partial | Shared | Why it stays |
|---|---|---|
| `.ChainEdits` | 11 + 9 | Every structural edit: `Nodes`, `CreateNodeVm`, `RebuildBoardColumns`, `SelectedNode`, the routing junctions; its undo snapshot is dropped by `ResyncAll` and the preset apply |
| `.History` | 10 + 5 | `Sync`, `BuildCurrentRig`, the chain edits' snapshot/restore; its `_applyingHistory` / `NoteEdit` / `CommitHistoryStep` are written from the root's `RebuildBoardColumns`, Scenes and Presets |
| `.Scenes` | 10 + 4 | Switches through `Browser`, master bypass, `_applyingHistory`, `Sync` and `CommitHistoryStep`; `RefreshScenes` and `_sceneAfterApply` are part of `Sync` / `ResyncAll` |
| `.Routing` | 8 + 4 | Adds/moves/removes junctions through ChainEdits; `NotifyRoutingMode` runs inside `RebuildBoardColumns` |
| `.Presets` | 7 + 3 | `BuildCurrentRig` / `OnPresetApplied` (→ `Sync`, deferred scene, undo-snapshot drop) are the browser's seams, built in the ctor |
| `.NeuralAmp` | 6 + 6 | Owns `_captureIndex`, reset by `Sync` / `ResyncAll`, and the per-card callbacks `CreateNodeVm` hands out |
| `.Seats` | 5 + 1 | REPOINTS `_dsp` and moves the `EngineAttached` subscription (`_onEngineAttached`) before `ResyncAll` — the seat/engine rebind order |
| `.MidiControls` | 11 + 0 | The glue between `Controls`, `Setlists`, `Browser`, `SelectSceneAt` and the preset service; `RigMidiInputViewModel.BindRigScope` takes the whole board; it also holds the class's `Dispose` |
| `.Spillover` | 3 + 1 | Writes every seat's chain (`_chains`), and `RaiseSpilloverAvailability` is the engine-attach signal (`OnEngineAttachedOnUi`) that `AmpFxRigViewModel` re-checks the re-amp recorder on |

Shared fields, the constructor and the owned collaborators stay in the ROOT, so "where does this come from" is
one look. Nothing else about the type changed in that split — every member moved
verbatim.

## The rig had two skins (§6.8) — the rack was retired in v0.43

**There is ONE board: `RigScreenRoot` in `PlayModeView.axaml`** (the owner's
`Amp and FX Rig.dc.html` on the Fire layer). §7 left "flat · textured · user
preference" open and 0.49 shipped both behind a `FLAT | RACK` switch; the owner
reported that as the app still having two pedalboards, and the switch and the
textured skin were removed. **Do not re-add a skin toggle** — every rig feature
then has to be built twice, and the two that mattered most (drag-to-reorder and
the tone browser) each ended up on exactly one of them.

**v0.43 finished that: `PedalboardView` — what was LEFT of the textured rack —
is deleted, along with its `Pedal*` material tokens, its visual-language guide
and fifteen `Controls/Dsp/` controls (enclosure, dial, treadle, split board,
cable layer and their geometry) that only it drew.** The rig was extracted into
its own view, `Views/PlayMode/AmpFxRigView.axaml`, precisely so both hosts can
NAME it: `PlayModeView` and the chart editor's overlay in `MainWindow.axaml`
now resolve the same type. **Each host owns its OWN `AmpFxRigViewModel`** —
separate screen state (docked unit, gear filter) over one shared chain — and the
screen's close button raises `CloseRequested` rather than knowing which host it
is in.

**Every surface drives the `PedalboardViewModel` SINGLETON, so they are the same
chain, the same knobs, the same presets and the same tuner** — a knob moved on
one is moved on the other because there is only one managed mirror of one native
chain. Consequences for anything you change here:

- **A new capability belongs on the VIEW MODEL, not in either view.** Put it in
  one view and the other silently lacks it.
- **The Fire skin renders `PedalNodeViewModel.KnobParams` / `Params` too**, so
  the metadata-driven param rule below is now load-bearing twice over. Neither
  view may grow a per-module knob table — `UI/AmpFxRigParameterSetTests` fails
  if one does.
- The two properties the Fire skin added to `PedalNodeViewModel` are pure folds
  of what was already there: `FireCategory` / `FireTokenKey` (via the pure
  `Controls/Fire/FireRigCategory`, eleven engineering families → the spec's
  eight fixed identities) and `FireCategoryLabel`. `PedalParamViewModel.DisplayText`
  is the readout, and it goes through the SAME `KnobMath.DisplayValue` +
  `DspControlText.FormatValue` pair `RotaryKnob` draws with — so the two skins
  cannot print different numbers for one param.
- **The gear LIBRARY has two tabs and a target lane (0.63).** Starred gear is
  `AppConfig.RigFavoriteGear` — per INSTALL, not per profile — owned by
  `GearRailViewModel` (`IsFavorite` / `ToggleFavoriteCommand` /
  `ShowFavoritesOnly`, filtered through the pure
  `GearRailFilter.MatchesFavorites`), and the rail raises `FavoritesChanged` so
  the Fire screen re-reads the rows' stars IN PLACE rather than re-inflating the
  whole list (~57 row templates when that was written; the catalogue is 100 since
  the JuceSharp 0.9 gear wave, and the list itself is windowed now — see the gear
  panel bullet below). `AmpFxRigViewModel.GearLibraryTab` (Browse / Favourites) is the
  only writer of `ShowFavoritesOnly`. `AmpFxRigViewModel.AddTargetLane`
  (`AddTargetLanes`, in its own `AmpFxRigViewModel.GearLibrary.cs` partial) is
  where a plain gear-panel click lands when NO insert gap is armed: null = the
  end of the chain, 1..3 = a lane of the LIVE region, seated through `DropToRow`
  at the same slot `InsertIntoLane` computes. An armed gap always wins, only
  lanes the board runs are offered, and a target lane that stops existing falls
  back to the main chain. `UI/RigGearLibraryTests`.
- **The board is the tone-rig design's graph** (2026-09-07): one node template
  for serial and lane runs — artwork, name and a state dot, 108×90; a serial
  chain uses the 124×148 card with a summary line (`PedalNodeViewModel.BoardSummary`,
  `Border.FireUnitCard.series`). Nodes carry NO buttons: bypass and remove
  live in the editor dock. Each lane ends in a `FireMixerNode` (level, pan
  knob, M/S over `RigLaneMixerViewModel`). Selecting a node opens
  `Views/PlayMode/RigModuleEditorView.axaml`, which exposes every parameter.
  Serial↔lane, lane↔lane and gear-rail→run drops retain `DropToRow` / `MoveTo` /
  `AddGearAt`; empty lanes retain `FireLaneDropZone`. `UI/RigBoardGraphTests`
  covers the caption, mixer labels, summary, output meter, add-to picker and
  the gear list's highlight.
- **The gear panel is the design's list** (2026-09-07): "Gear · N modules",
  Browse/Favourites, search, a four-column category grid
  (the gear browser's category tabs, `FireGearCatTabToggle`), rows with the thumbnail, the name over
  "AMPS · 9 controls" (the IN RIG chip in front of it when the unit is in the
  rig), the star and a `+` (`Button.FireGearAdd`, the screen's
  `AddGearCommand`). Clicking a ROW highlights it (`SelectGearCommand`,
  `RigGearItem.IsSelected`) for the outlined "Add to rig" button
  (`AddSelectedGearCommand`) under the add-to picker; a press that travels is
  still the drag onto the board. The NAM-capture CTA left the panel — the amp
  editor's own "Load capture…" is the way in. The list runs in the
  prototype's order (`FireRigCategory.DisplayOrder`: amps, cabs, drive,
  dynamics, EQ, mod, delay/reverb, utility).
- **…and it is VIRTUALIZED, which is why it is FLAT** (2026-09-12). It was a
  `ScrollViewer` over an `ItemsControl` of groups, each group an inner
  `ItemsControl` of rows — and an items panel nested inside another one cannot
  virtualize: the inner control is measured with an unbounded height, so its
  viewport IS its content and every row realizes. The groups drew no HEADER
  (they only ever decided the order), so `AmpFxRigView.axaml`'s `RigGearGroups`
  is now one `ListBox.FireList` over `AmpFxRigViewModel.GearTilePairs` — the
  flat, DisplayOrder-ordered `GearRows` (the same `RigGearItem` instances
  `GearGroups` holds) two to a row, since the AmpliTube layout draws the browser
  as a two-column grid of faceplate tiles (`RigGearTileTemplate`) — on the
  default `VirtualizingStackPanel`. `GearGroups`
  stays: the badge and star refreshes and `UI/RigGearLibraryTests` walk it.
  **A `ListBox` and not a re-templated `ItemsControl`**, for the reason the tone
  browser's rail is one: only a `ListBox` brings its own scroller, and it is the
  scroller that feeds the panel a viewport. Measured headless over a synthetic
  160-module catalogue at a 569 px viewport: **8 realized containers / 178
  realized descendants, against 160 / 2,914 unvirtualized**. The catalogue is
  the reason — 57 → 100 would have taken the list from ~680 to ~1,200 elements,
  built on the prewarm drain (the rig screen is force-realized at
  `PlayModeView.HiddenSurfaces`) and rebuilt on the UI thread on every tab
  click, favourite toggle and coalesced keystroke, inside the ≤150 ms switch
  budget. `RebuildGearGroups` is ONE pass over the cards for the same reason —
  it re-scanned them once per identity. Ceiling pinned by
  `Perf/PlayModeScreensUiPerfSmokeTests
  .TheGearPalette_RealizesAViewportsWorthOfRows_NotTheWholeCatalogue` (derived
  from the laid-out viewport and row height, never a wall-clock number), the
  single pass by `UI/AmpFxRigParameterSetTests
  .RebuildGearGroups_ReadsTheCatalogueOnce_NotOncePerCategory`.
  **The prewarm reaches those rows only because the drain SETTLES each part**
  (#234): a direct prewarm `Measure` gives the panel no viewport, so it built
  one row and the first open built the rest. And a rig `Render` override asks
  for its art through `RigArtworkStore.GetForRender(file, width, this)`, never
  `Get` — a hidden tile's decode then goes to the pool instead of a render tick.
  Both in [hidden-surface-prewarm.md](rendering-perf/startup-and-prewarm/hidden-surface-prewarm.md).
- **Side-by-side with the prototype:** `Perf/RigDesignCompareUiPerfSmokeTests`
  builds the prototype's default rig in each of its capture states at its
  1586×1000 viewport and, with the capture env vars set, writes
  `app-<state>.png` next to the prototype's own `html-<state>.png` for a
  like-for-like check. The hardware knob is the design's 64 px dial and a
  pedal enclosure may scale to `RigArtwork.MinEditorScale` (0.5) to fit.
- **Selecting a unit swaps a DataContext, not a view** (2026-09-07):
  `RigModuleEditorView` builds each family view once and keeps it attached
  (hidden, DataContext null) — a warm selection measured 10–50 ms in the
  headless probe (`Perf/RigSelectionLatencyUiPerfSmokeTests`, numbers logged,
  never asserted) against 60–125 ms when every click inflated a view.
  Detail: [fire/amp-fx-rig-screen.md](fire/amp-fx-rig-screen.md).
- **SELECTING A BLOCK ALWAYS OPENS ITS EDITOR** (W3, 2026-09-16). There is no
  state in which a block is selected and its editor is not showing:
  `AmpFxRigViewModel.RevealSelectedEditor` clears `IsDrawerExpanded` (so the tone
  drawer falls back to its stub) and sets `IsDockOpen` (so a previous HIDE is not
  a mode the next pick inherits). It is driven from TWO signals and needs both —
  `PedalboardViewModel.SelectedNode`'s change notification for every path that
  MOVES the selection, and the new `PedalboardViewModel.NodeSelectionRequested`
  event for a click on the block that was ALREADY selected, which raises no
  change at all. That second case is not a corner: the rig auto-selects an amp
  when it opens, so the first block a player clicks is routinely that one.
  **Do not "fix" a future variant in a command handler** — a per-path fix is one
  the next path misses, which is how the reported bug survived.
  `UI/RigSelectionInvariantTests` (every entry point, plus a one-writer-each
  source lint) and `Perf/RigSelectionOpensTheEditorUiPerfSmokeTests` (every
  family, measured, with the browser open).
- **…and a NEW unit opens at the top of its editor.** The host keeps every
  family view attached, so `RigModuleEditorView.ScrollNewUnitIntoView` resets
  `RigEditorRackScroll` (and the face's `RigEditorScroll`) when the node changes
  and leaves them alone on a re-select. The RACK is the half that can really
  carry a stale offset — the faces have been folded into the row by
  `RigScaleToFit` since lane E, so their extent fits their viewport (measured:
  391 px of 391) and their offset is already zero.

**NO DEAD BUTTONS.** Every `Button` / `ToggleButton` in `AmpFxRigView.axaml` and
`RigModuleEditorView.axaml` must raise a command that reflection can resolve on the view model that scope
carries (or a `Click` handler the code-behind declares, or a two-way `IsChecked`
write) — `AmpFxRigNoDeadButtonsTests` in the default gate. The rule and the five
controls that failed it in v0.48.5 (the SCENE strip's A/B pair, SWITCH, "Copy to
both", the footer tuner) are in
[fire/amp-fx-rig-screen.md](fire/amp-fx-rig-screen.md) § "No dead buttons". The
lint cannot see a `Border` dressed as a segment item, which is exactly how two
of those five got shipped — that half is still yours.

**The footer picks the guitar's INPUT JACK (2026-08-30).** `RigInputChannelPicker`
/ `RigInputChannelCombo` bind `AmpFxRigViewModel.InputChannel`
(`ViewModels/Settings/LiveInputChannelViewModel`, a DI transient the two rig
hosts pass in; a service-less screen gets one that reports itself unavailable).
It follows `Rig.SelectedSeat`, applies at once through `ApplyAudioConfiguration`
— the same `AppConfig.AsioInputChannel*` fields Settings ▸ Audio writes, and
the one live-seat-baseline writer — and ships DISABLED with its reason in the
tooltip on WASAPI and with input off. The shared helper and the calibration
half of the same owner report are in
`docs/agents/services/audio-engine.md` § the live seat count.

Details, the §7 parameter-set corrections and the capture loop that found the
two layout faults: [fire/amp-fx-rig-screen.md](fire/amp-fx-rig-screen.md).

## Where the detail lives

**Read the STATUS column before you trust a row.** Three of these files were
written against the retired rack and were never rewritten when it was deleted —
they describe controls that no longer exist, in the present tense. They are kept
because the RULES in them (the two routing invariants, the drag anti-thrash
model, the param-marshalling contract) are still the rules; the CONTROLS they
name are not still the controls. Re-verified 2026-08-08.

| Read this when… | Status | File |
|---|---|---|
| you are binding a DSP param or touching the gear rail: string params + file pickers, the gear drawer, "browse by model", `ValueLabels` selectors (**every skin must render `ModelSelectors`**) and ABI-v27 step snapping | current, but its `PedalboardView` references mean `AmpFxRigView` | [pedalboard/gear-rail-and-params.md](pedalboard/gear-rail-and-params.md) |
| you are touching the **AI "Design a tone" panel** (the designer column of the Tones view), or **Settings ▸ AI** — the seams it reuses (apply / save / restore), its five states, the collapsed-body layout rule, and the key that is never read back | current | [pedalboard/ai-tone-design.md](pedalboard/ai-tone-design.md) |
| you are touching **scenes (A–H), setlists or MIDI program change / the scene CC** — where scenes live (`DcRigPreset.Scenes`, `IDspChainService.Scenes`), why a switch never adds a node, why `FromChain` and not `FromNodes`, the scene bar, the Setlists rail panel, the performance keys | current | [pedalboard/scenes-and-setlists.md](pedalboard/scenes-and-setlists.md) |
| you are touching tone presets, the Tones view's library (`RigTonesView`: head, sidebar, cards), the TONE3000 / NAM capture browser (**its panel was missing v0.43→0.57 — read the note at the top of that file**), the legacy filter or A/B compare | current | [pedalboard/presets-and-nam-captures.md](pedalboard/presets-and-nam-captures.md) |
| you are opening the rig from the CHART EDITOR — the overlay sizing, the DI singleton, the airspace freeze-frame, the three owners of the chain, the entry points | current — the OVERLAY is; it hosts `AmpFxRigView`, not the `PedalboardView` the body still names | [pedalboard/editor-host.md](pedalboard/editor-host.md) |
| you are touching a pedal FACE, a `KnobStyle`, `DspModuleVisuals`, a shared `Pedal*Template` or the neural-amp head — i.e. anything drawn | **HISTORY** — every DRAWN control in it (`RotaryKnob`, `PedalEnclosure`, `CableLayer`, `RigNoise`/`RigTextures`, `RigTunerStrobe`, `TreadleControl`, `GearThumbnail`, `SignalFlowLayout`) is deleted. `Controls/Dsp/` is now 13 PURE helper files and nothing else. The pure-helper bullets are still true | [pedalboard/widgets-and-faces.md](pedalboard/widgets-and-faces.md) |
| you are touching the board itself: the live-reflow drag (**two rects, never mix them up**), split/parallel routing and its two INVARIANTS, the junction skin, or the rack's scroll/zoom | **HISTORY** — `SplitBoardPanel`, `BoardArrangeMath`, `BoardDragGeometry` and `BoardDropZone` are all deleted. The two ROUTING invariants still hold on `PedalboardViewModel` | [pedalboard/board-drag-and-routing.md](pedalboard/board-drag-and-routing.md) |
| you are touching the routing-mode toolbar or the rig tuner panel | **HISTORY** — `SignalChainCanvas` and `SignalFlowLayout` are deleted; `RigTunerMath` survives | [pedalboard/chain-builder-and-tuner.md](pedalboard/chain-builder-and-tuner.md) |
| you are reading code or comments that reference the RETIRED rack's seven-region shell (`PedalboardView.axaml`, its regions and its `Inert` rule) | **HISTORY**, and says so in its own title | [pedalboard/rack-shell.md](pedalboard/rack-shell.md) |
| the **seat picker** — which PLAYER's rig this surface is editing (ABI v28+; dormant, see the note below) | current | `ViewModels/PlayMode/PedalboardViewModel.Seats.cs` + the note below |
| the rig **tuner's concert pitch** — `RigTunerViewModel.ReferenceHz` / `ReferenceLabel` over `AppConfig.TunerReferenceHz` (A4, default 440, clamped 415..466 by `RigTunerMath.ClampReference`). Every mapping member of `RigTunerMath` has a reference overload and the 440 ones DELEGATE to them, so there is one grid; changing it drops the stale reading exactly as a seat change does. `UI/RigTunerReferenceTests` | current | `ViewModels/PlayMode/RigTunerViewModel.cs` |

**The rig can be pointed at any SEAT (0.57).** Every seat has had its own
chain, input channel and VST attach since ABI v28 — `IPlayerDspChains.For(n)`,
`dca_engine_dsp_*_for_player`, `dca_plugin_attach_to_input_for_player`.
**Dormant since 2026-09-19:** the only publisher of a multi-seat list was couch
co-op's run controller, removed with co-op (restore from tag
`pre-cut-2026-09`), so `SetSeats`/`SetVstSeats` have no product caller and the
pickers stay hidden on their single seat. Wiring them to the "two guitars" input
setting is the obvious next owner. What did not
exist was any way to LOOK at a seat past 0: `PedalboardViewModel` held
`IDspChainService`, which DI resolves as `IPlayerDspChains.For(0)`.

- **A seat switch is a repoint plus a `ResyncAll()`** — every `PedalNodeViewModel`
  is handed the chain by `CreateNodeVm`, so a rebuild re-binds all of them.
  **Nothing is copied between seats**; each keeps the rig it had, which is the
  point of per-player rigs. (`ResyncAll`, not the incremental `Sync`: node ids are
  only unique per chain, so the id-keyed diff would false-match across seats —
  pinned by `PedalboardIncrementalSyncTests.ASeatRepoint_RebuildsEveryViewModel`.)
- **…and `ResyncAll` must also drop everything this surface remembers ABOUT A
  BOARD, which this bullet used to claim the rebuild covered.** It did not, and
  both survivors addressed the wrong guitar. The MASTER BYPASS is an armed
  gesture keyed by native node id (`_bypassRestore`), and ids are only unique per
  chain — both seats start at 1 — so it stayed lit over an un-bypassed board and
  the next press wrote seat 0's bypass pattern onto seat 1's chain, un-bypassing
  pedals its player had switched out. The A/B COMPARE SLOTS are two snapshots of
  one board, so SWITCH after a repoint applied the previous seat's whole rig to
  the seat on screen. `ResyncAll` now clears both (`ToneCompareSlots.Reset`, NOT
  `SeedFrom` — a seed would make the first press apply a rig identical to the live
  one, which is what the fork rule returns null to avoid); the same applies on a
  PROFILE switch, which is the other caller. Pinned by `UI/RigSeatSelectionTests.TheMasterBypass_DoesNotFollowTheRigToAnotherSeat`
  and `.TheCompareSlots_DoNotCarryOneSeatsRigOntoAnother`, both asserted against
  the CHAINS and the per-seat applies.
- **…and so is "Load into rig" in the NAM capture browser (0.62).** It was
  handed the chain INSTANCE the pedalboard was constructed with, and the picker
  works by repointing that field, so it was permanently seat 0: a capture loaded
  while player 2's board was on screen added a neural amp to player 1's rig and
  rebuilt the board of the seat on screen, which shows nothing.
  `NamCaptureBrowserViewModel` takes a `Func<IDspChainService>` now and
  `PedalboardViewModel` passes `() => _dsp`. **Anything else handed a chain here
  needs the same treatment** — a seat index or a selector, never an instance.
  Pinned by `RigSeatSelectionTests.LoadingACaptureIntoTheRig_LandsOnTheSelectedSeat`.
- **The MASTER BYPASS is an armed gesture, and `Sync` re-asserts it (0.62).**
  It means "the rig is off", but `DcRigApplier` writes the incoming rig's own
  (un-bypassed) flags onto every node it reuses or adds — so a preset apply, an
  A/B compare push or a capture load with the rig switched OFF brought the sound
  back behind a lit lamp, and the next press then "restored" node ids belonging
  to a board that no longer existed. `Sync` now re-bypasses the live chain and
  PRUNES `_bypassRestore` to surviving ids, and `AddGearCore` bypasses a pedal
  added while armed (deliberately NOT into the restore set, so turning the rig
  back on switches it in). `ResyncAll` still DROPS the armed state — there the
  board belongs to another seat or profile. `UI/RigMasterBypassTests`.
- **The board follows an ENGINE REBUILD (0.62).** `PedalboardViewModel`
  subscribes to `IDspChainService.EngineAttached` and POSTS a `ResyncAll`; the
  subscription moves with a seat repoint. The board is a DI singleton reconciled
  only when a host opens the rig, so with the rig already on screen a device
  change / buffer-size change / watchdog recovery left every card bound to a
  node id the new engine never issued — knob writes silently refused, Remove and
  Move addressing nothing. **Not `NodesChanged`**: that also fires for this
  board's own edits (`ToneSessionService`'s doc comment claimed the board
  rebuilt from it, and nothing ever did). `UI/PedalboardEngineRebuildTests`.
- **The preset BROWSER is an edit on this surface too (0.61).** A card click
  applies through `IDspTonePresetService.ApplyToPlayer(SelectedPlayer, …)` — the
  browser is handed `() => SelectedPlayer` — where it used the seat-less `Apply`
  and dressed seat 0 under player 2's board, the same wrong-guitar shape the
  SCENE strip's compare had until 0.59. Pinned by
  `RigSeatSelectionTests.ApplyingAPresetFromTheBrowser_LandsOnTheSelectedSeat`,
  against the service call, not the browser's active-name property.
- **The seat list is pushed IN** (`SetSeats`), never pulled. This view model is
  a DI singleton shared with the chart editor, and a session dependency would
  make it unconstructable everywhere there is no session.
- **Only seats that really got a chain are offered.** `For` CLAMPS an
  out-of-range index into range, so a seat past `EngineRigCount` would silently
  edit somebody else's tone while the picker named a different player.
- The picker is hidden with one seat, and `Stop` collapses it back. Guarded by
  `UI/RigSeatSelectionTests`, which asserts against the CHAINS rather than the
  picker's own state.

**The VST slot rows carry the SAME seat picker (0.58).** `VstChainEntry.Player`,
`dca_plugin_attach_to_input_for_player` and `ApplyGuitarVstChainCore`'s unlocked
reseat had all existed since ABI v28 with no surface able to SAY which seat a
plugin belongs to — so every slot anyone added went on seat 0 and player 2 could
not put a plugin on their own rig at all.

- **`VstChainSlotViewModel.SelectedSeat` is the authoring seam**; `Player` is no
  longer get-only. The rows render it in the ONE shared plugin rack
  (`DotCharter.Tone.UI/Views/PlayMode/VstRackView.axaml`, since 2026-09-27), which
  every chain surface hosts — the Tools popover, `Views/Shell/AmpFxPanel.axaml`
  (DotCharter, over `TransportViewModel.VstRack` → `TransportVstRack`) and
  RigLab's Plugins tab (over `VstRackViewModel`). Both implement
  `ViewModels/Editor/IVstRack`; the per-slot verbs stay on `VstChainSlotViewModel`,
  and the transport keeps its chain logic (the adapter only forwards).
- **It is the SAME `RigSeatOption` list** the rig's picker uses
  (`TransportViewModel.SetVstSeats` beside `PedalboardViewModel.SetSeats`; a
  future publisher must call both). A second seat model would be a second answer to
  "which seats exist" and the two would disagree the first time a roster changed.
  `TransportViewModel.VstSeats` is mutated in place, never replaced — every slot
  holds a reference to it.
- **A seat change is an engine RESEAT plus a persist**, not a UI flag:
  `PushGuitarChainToEngine` is what makes `ApplyGuitarVstChainCore` detach the
  plugin and re-attach it to the other player's input chain, and a seat that only
  lived until the next launch is not an assignment.
- **A roster ARRIVING never raises `PlayerChanged`** (`AttachSeats` suppresses
  it) — echoing one would unload/reload every plugin on every seat publish for a
  change nobody made.
- **Collapsing back to single player must NOT rewrite a slot's seat to 0.** The
  persisted chain still says player 2, and silently re-seating it here is exactly
  the data loss `UI/VstChainSeatTests` was written for.
- Guarded by `UI/VstChainSeatPickerTests`, which asserts against the ENGINE PUSH
  and the PERSISTED config — never the picker's selection. Its harness supplies
  an inline `IUiDispatcher`: with the default one the VST restore never runs, so
  `_vstRestoreComplete` stays false and every `PersistGuitarChain` is a silent
  no-op that still passes the engine-push half.

**…and so does the DROP-TUNE pedal (ABI v31).** Same list again
(`DropTuneSeatPicker` in `AmpFxPanel.axaml`, over `TransportViewModel.VstSeats`),
because the pedal sits in the same panel as the VST rows and two seat models
would be two answers to "which seats exist". The panel's semitone picker offers
`RigDropTuneStep.AllSemitones` (+12…−12), the same list as the rig footer's Drop
pedal, which drives the same `InputPitchShiftSemis`. Until v31 the engine ran ONE CSVD
shifter ahead of the per-player split, so dropping one guitar dropped both.

- **`TransportViewModel.InputPitchShiftSemis` addresses `DropTunePlayer`**, and
  the readout follows the picker — a combo showing seat 0's setting over seat 1's
  guitar is the same lie as detuning the wrong one.
- **Only SEAT 0 is persisted.** `AppConfig.InputPitchShiftSemis` is one integer
  and the higher seats belong to a roster, not to the install; writing them there
  would restore a second player's drop tune onto the next SOLO session. The extra
  seats live in `PlayerInputRoutingState` for the run, which is what carries them
  across an engine rebuild.
- Guarded by `UI/DropTuneSeatTests`, which asserts against the ROUTING CALLS —
  which seat the engine was told to detune — never the picker's own property.
