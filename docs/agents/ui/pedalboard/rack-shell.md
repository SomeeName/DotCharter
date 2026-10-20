# The retired rack SHELL — seven regions, not one board

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).

## The rig SHELL — seven regions, not one board

**The layout is the design.** Two earlier passes restyled the gear (amp heads,
castings, knobs, the floor, a console lip) and the owner's verdict was that the
surface still looked like the default arrangement with nicer parts on it. The
board is now a **rack**: four stacked panels, no gutters, with the gear browser
as a full-height drawer down the right.

`PedalboardView.axaml`'s root stack is `RowDefinitions="Auto,*,Auto,Auto"`:

| # | Region | `x:Name` anchor | Backed by |
|---|---|---|---|
| 1 | **Command bar** — folder, preset well + ▲▼, SV/EX/✕, A/B + COPY A→B, undo/redo, BYPASS, wordmark, TUNER, LOOP, GEAR, TONEBANK, menu | `CommandBar` | `PresetBrowserViewModel`, `Seed`/`ToggleCompareCommand`, `ToggleMasterBypassCommand`, `RigTuner`, `ToggleGearRailCommand` |
| 2 | **The stage** — the lit room, rails, `SplitBoardPanel`, drag/zoom | `RackArea` | unchanged |
| 3 | **Slot strip** — one named cell per module, power dot + ▲▼ | `SlotStripPanel` / `SlotStrip` | `Nodes`, `ToggleBypassCommand`, `Move{Up,Down}Command`, `SelectNodeCommand` |
| 4 | **Routing panel** — SERIES / DUAL / TRIPLE / DI as a vertical stack | `RoutingModes` | `Routing{Serial,Split2,Split3}Command` |
| 5 | **Signal-flow graph + split-lane mixer** | `ChainCanvas` / `LaneMixer` | `SignalChainCanvas`; lanes from `SplitLanes` (the live region's `RegionRowViewModel`s) |
| 6 | **Gear browser** — search, STOMP/AMP/CAB/RACK, TYPE/COLLECTION, CHARACTER chips, 2-col grid | `GearBrowser`, `GearKindTabs`, `GearCharacterChips`, `GearCards` | `GearRailViewModel` (+ `KindTabs`/`SelectKindCommand`) |
| 7 | **Transport bar** | `TransportBar` | **nothing — see below** |

Rules this shell adds:

- **A control with no service behind it carries the `Inert` class** — dimmed and
  `IsHitTestVisible=False` — and is named in its region's XAML comment. The
  transport panel additionally carries the `Border.InertStamp` "TRANSPORT
  OFFLINE" marker. The current inert set is: undo/redo, LOOP, TONEBANK + lamp,
  the hamburger, the DI routing mode, a lane's LVL/PAN/S/Ø, the gear browser's
  COLLECTION toggle, and the ENTIRE transport bar except its TUNER cap. **A
  fake moving meter is worse than an omitted one**; if you wire one of these
  up, delete its `Inert` class and its entry here in the same change.
- **The chain builder moved OUT of the stage** onto its own graph-paper panel
  (region 4/5). The stage's inner grid is `RowDefinitions="Auto,*"` now — a
  rack's wiring is not part of the stage.
- **The rack chrome is one material set**, `PedalRack*` in `Styles/Resources.axaml`
  (face + grain, button + its two shadows, well + scanline + sheen, seam, ink,
  lamps, rail face) plus `PedalGraphPaper*` and `PedalVu*`. The command bar, the
  slot strip and the transport bar are the SAME object seen three times, so they
  share them. `Button` has **no `BoxShadow` property** — a cap's depth goes on
  `Button.X /template/ ContentPresenter`.
- **Region 6's form tabs default to NO filter.** `GearRailViewModel.SelectedKind`
  starts null and the tabs toggle (`SelectKindCommand`), because a `ListBox`
  cannot express "every form" and opening on STOMP would bury the amps and cabs
  `GearCuration` deliberately ranks first.
- **The gate the RACK had is gone with the rack.** The live equivalent, over the
  six regions `AmpFxRigView` declares (not the rack's seven), is
  `Perf/InspectorConstructionUiPerfSmokeTests.AmpFxRig_realizesEveryUnitCardAndEveryRegion`
  — it looks each region up by name and asserts it measured non-zero, because a
  region that fails to render still arranges, it just has no size — plus
  `UI/AmpFxRigScreenTests.TheScreen_DeclaresAllSixRegions` in the default gate.
  Add a row to both when you add a region.
