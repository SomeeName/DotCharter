# Fire — the Amp & FX rig screen (§6.8) and §7's corrected parameter sets

Part of the Fire design system. Router: [../fire-design-system.md](../fire-design-system.md).

---

**THE AMP & FX RIG is on the layer (§6.8, 0.49) — the LAST mockup screen.**
`RigScreenRoot` in `PlayModeView.axaml`, built from `Amp and FX Rig.dc.html`:
the spec's six regions at its own heights — header 56 · routing header 52 ·
board (flex, scrolls) · module editor dock 278 · footer (min 52, WRAPS on a
narrow window), with the gear panel `FireRigGearPanelWidth` (320, 420 above the
wide breakpoint — the mockup drew 336) down the right. Since the tone-rig
design (2026-09-07) the routing header carries the "Routing" title, the
Series/Dual/Triple strip (`Border.FireModeStrip` of `ToggleButton.FireModeButton`
— the design's 85×34 mixed-case buttons, the active one filled; the header's
Scene A/B strip wears the same with `.Scene`), and on the right the zoom
cluster, Fit and Add; the footer carries the
tuner, reference pitch, tempo, the input jack picker with the INPUT meter and
CLIP lamp, and an OUTPUT meter that reads the LAST unit's output
(`PedalboardViewModel.OutputMeters`) — the only output the engine meters, so it
never claims to be a master meter.

**EVERY DYNAMIC READOUT IN THE FOOTER SITS IN A FIXED COLUMN, and a `FireRowDesc`
in it carries `TextWrapping="NoWrap"`** (2026-09-12; owner: "the footer jumps
around while the tuner reads"). The strip is still a `WrapPanel` — that is what
drops a cluster onto a second line on a narrow window — but each CLUSTER is now
one wrap item that is internally a `Grid` with fixed `ColumnDefinitions`:
`RigTunerStrip` (`Auto,40,Auto,150,Auto,Auto`), `RigTempoStrip`
(`Auto,84,Auto,Auto`) and `RigInputChannelPicker` (`Auto,110,Auto,Auto,44`).
Three faults, all of them width, and all three shipped together:
`Rig.RigTuner.NoteText` swings 24 px ("–") to 36 px ("A#10"); `RigTunerCents` and
`RigTunerStatus` are MUTUALLY EXCLUSIVE and 32 px against 150, so they share ONE
150 px cell (`RigTunerReadout`) instead of trading places and shoving everything
right of them; and `TextBlock.FireRowDesc` sets `TextWrapping="Wrap"`, so the
`Tuner_NoInput` hint wrapped to THREE lines and made the strip 80 px tall instead
of 52 — 28 px taken straight out of the module editor's row. The CLIP lamp's
column is reserved (41 px of tag in 44) because it comes and goes at AUDIO rate;
the tempo "Mixed" flag's is NOT, because it follows the selection and reserving
its widest face (66 px in de-DE) pushed the OUTPUT cluster onto a second line at
1920. **Size a new cell from the widest value it can print, measured in the real
face** — `Perf/RigFooterStabilityUiPerfSmokeTests` renders both tuner states and
compares X, and re-measures the note and cents cells rather than trusting these
numbers.

Six things it settled:

1. **It stayed an OVERLAY (`IsPedalboardOpen`), not a `PlayModeScreen`.** The
   rig opens from the front door's FREE PLAY, the Get Ready footer AND the pause
   menu, and two of those are STATES you cannot navigate back to by name.
   Keeping the flag also keeps the Esc walk-up working untouched.
2. **ONE RIG, ONE SKIN, ONE DOOR.** §7's open decision on the rig skin was
   "flat · textured · user preference" and 0.49 shipped BOTH behind a
   `FLAT | RACK` switch. The owner read that as the app still having two
   pedalboards — fairly, because a control that redraws one chain in another
   idiom is indistinguishable from a second surface, and it doubled the
   maintenance of every rig feature (drag-reorder and the tone browser were each
   present on exactly one of the two). The switch, `IsRigClassicSkin`,
   `IsRigFireSkin`, their commands and the `Rig_Skin_*` strings are gone.
   **Do not re-add a skin toggle.**
   - **The board's TONE BROWSER IS A MODE (`RigTonesMode`, W4, 2026-09-16),
     not a drawer.** It was a drawer at the board's foot for four releases and
     the measurement is why it stopped being one: at 1600×900 the drawer left
     the tone designer's scroller a **140 px viewport for 487 px of content**,
     so the prompt field ALONE did not fit — let alone the prompt, Design,
     "From the recording" and "Match by section", which are one feature. The
     mode takes the whole body below the header; the routing canvas is replaced
     by a read-only chain strip (`RigChainStrip`) so the player still sees what
     the tone applies to, and the gear rail is column 1 of the outer grid and
     never moves.
     - **It is `PedalboardViewModel.IsBrowserOpen` under a name that says what
       it does** — `AmpFxRigViewModel.IsTonesMode`. Deliberately not a second
       flag: the header's preset pill, the Esc walk-up's middle rung and both
       hosts' `TryCloseOverlay` already drive that one property, so the mode
       inherited every door the drawer had and none of them had to learn a new
       word. It is still not a `PlayModeScreen` value and must not become one:
       it is about the board behind it. Esc unstacks save-modal → mode → board,
       in that order (`TryCloseOverlay`), and `PlayOverlayFactsTests` pins it.
     - **`RigTonesMode` is a SIBLING of `RigFlatBody` in one cell, not a
       re-layout of it**, and that is what makes "leaving the mode returns the
       canvas and the editor exactly as left" true by construction rather than
       by restoring state: nothing inside the canvas body is touched while the
       mode is up. `RigFlatBody` is gated `!IsTonesMode`; the mode is gated
       `IsTonesMode`.
     - **Selecting a block LEAVES the mode** (`RevealSelectedEditor` clears
       `_rig.IsBrowserOpen`) — W1's last-gesture rule and W3's selecting-wins
       rule, one structure up. The editor is not on screen at all while the
       mode is up, so "a block is selected and its editor is not showing" is
       still impossible.
     - **TWO SCROLL REGIONS, SIDE BY SIDE, NEVER ONE INSIDE THE OTHER
       (2026-09-27): `RigTonesScroll` (the card grid) and `ToneDesignScroll`
       (the designer's thread).** W4 had cut five nested scrollers to one;
       the redesign made the designer a column of its own with its composer
       pinned under its thread, which needs its own scroller — a sibling, not
       a nesting. `AmpFxRigView.CapToneDesign` and its two floor constants
       stay gone.
     - **It is its own VIEW since 2026-09-27: `Views/PlayMode/RigTonesView.axaml`**
       (690 lines lifted out of `AmpFxRigView`), hosted once as
       `<views:RigTonesView x:Name="RigTonesMode" IsVisible="{Binding IsTonesMode}" />`.
       Its names live in its own name scope — find them in the visual tree,
       not with the rig view's `FindControl` — and, being a UserControl, its
       content is not in the visual tree until it is first measured (the
       prewarm realizes it first, see below).
     - Layout: `RigTonesLayout` is `Auto,*` — the chain strip, then
       `RigToneBrowser` (`FirePanel`, `ClipToBounds`) whose
       `RigToneBrowserBody` is `Auto,*`: the HEAD (title and count in a block
       as wide as the sidebar, so the search starts on the grid's left edge;
       the search, capped; then right-aligned the selected USER tone's `···`
       menu — Overwrite, Delete —, Save as new…, the "AI designer" toggle and
       ✕), then `RigTonesColumns`: the library (`RigBrowserBody`: the fixed
       `RigTonesSidebar` — the category rail with per-row counts under the
       search, and the natural home of a setlist section — beside
       `RigTonesScroll`) and the designer's column (`RigToneDesignPanel`,
       `FireRigToneDesignerWidth` capped by `RigTonesSizing.DesignerWidth`).
     Pinned by `Perf/RigTonesModeUiPerfSmokeTests` (the four design controls
     on screen together at all three sizes, every tone card description fully
     readable, enter/leave restoring the canvas and the editor, the grid and
     the thread as the only two scroll regions, and the capture dump) and
     `Perf/RigTonesRedesignUiPerfSmokeTests` (the head and the composer never
     move, the head fits 1280 px in German, a whole card at 1280 with the
     designer open, the thread follows a new answer, the Edited tag, the
     prewarm).
   - **THE ROUTING CANVAS IS FIXED AND NEVER RESIZES (W1, 2026-09-16), and that
     is the reason for every structural rule below.** Row 3 of `RigFlatBody`
     used to be `Auto` and hold EITHER the drawer OR the module dock
     (`RigDock`), so selecting a block pushed the 244+ px editor body into it
     and the `*`-sized board SHRANK under the user: the picture of the signal
     path changed size every time the editor opened or closed, while the gear
     rail in column 1 sat still. That row is ONE host now — `RigWorkspace`,
     whose height is an absolute pixel value written by
     `AmpFxRigView.SyncWorkspaceRow` WHATEVER is inside it — and both the drawer
     (its row 0) and the dock (its row 1) live in it.
     - **The workspace holds the module editor ALONE since W4**, and it is
       still a fixed-height host whose height never depends on its content —
       which is the whole of W1 and what keeps the canvas above invariant.
       `RigWorkspace` is `RowDefinitions="*"` with `RigDock` in row 0;
       `SyncWorkspaceRows` (the one-`*`-one-`Auto` chooser) is gone with the
       drawer it chose between, and so is the `2.2*` share the editor had before
       W1. `IsDockBodyVisible` is `IsDockOpen && HasDockUnit` — the
       `!IsDrawerBodyVisible` term went with the drawer.
     - **An EMPTY workspace is an empty STATE, not bare sunken space** (W4).
       W1's price was that with nothing selected the row was 34 px of dock bar
       over a blank rectangle. `RigDockEmpty` fills the same rectangle with
       `Rig_TonesMode_EmptyTitle` / `_EmptyBody` and says what to do next. It is
       only reachable on an empty chain — `EnsureEditorSelection` auto-selects
       on a populated one — which is the state a new player meets.
     - **SELECTING A BLOCK WINS, and that is stronger than "the last gesture"**
       (W3, 2026-09-16). `AmpFxRigViewModel.RevealSelectedEditor` clears
       `IsDrawerExpanded` AND sets `IsDockOpen`, so neither the drawer nor a
       previous HIDE can leave a selected block without its editor. HIDE is
       therefore MOMENTARY — it collapses the unit you are looking at, and the
       next pick re-opens it. It is driven from `SelectedNode`'s change
       notification *and* from `PedalboardViewModel.NodeSelectionRequested`,
       because a click on the block that is already selected raises no change and
       the rig auto-selects an amp on open, so that click is the common one.
       `UI/RigSelectionInvariantTests`,
       `Perf/RigSelectionOpensTheEditorUiPerfSmokeTests`.
     - **There are no header STUBS any more** (W4). `ToneDesignPanel.IsStubbed`
       and the `IsDockBodyVisible` gates on `RigBrowserBody` / `RigBrowserFoot`
       existed only because the tone surface shared this host with the editor.
       They do not share a host, so neither collapses and nothing has to
       remember the user's own `IsExpanded` around a collapse.
     - **The divider is ALWAYS live.** `IsSplitterVisible` is gone: the
       workspace always has a height, so a divider that comes and goes was only
       ever a symptom of the row collapsing.
   - **The workspace is USER-RESIZABLE and the height belongs to the RIG, not
     the view.** `RigBrowserSplitter` (row 2, the app-wide `GridSplitter` style,
     `ResizeDirection=Rows` + `PreviousAndNext`) drags row 1 (`*`, the canvas)
     against row 3. Three rules:
     - **No fixed `Height` on the drawer Border.** It had one (480) until
       v0.48.5; a splitter that moves a row the drawer then overflows looks
       broken in a way nothing measures — every container still arranges.
     - The pixel height lives in `RigWorkspaceViewModel.Height` (the rig's
       `PedalboardViewModel.Workspace`), clamped to
       `RigWorkspaceViewModel.MinHeight`/`MaxHeight` (260/1600) on set AND on load, and persisted as
       `AppConfig.RigWorkspaceHeight` (nullable: null is "never dragged", not
       zero; `AppConfig.RigToneBrowserHeight` is read once as the pre-W1 seed and
       never written, and `RigDockHeight` is gone). It is on the RIG because both
       hosts are the same control over the one singleton — a height held in the
       view would be two heights depending on which host you opened last.
     - **Until it has been dragged (`RigWorkspaceViewModel.HasUserHeight` is false) the height
       is the breakpointed `FireRigWorkspaceHeight` token — 480, and 860 above
       the wide breakpoint — read by `AmpFxRigView.DefaultWorkspaceRowHeight`.**
       A flat constant is content-independent and also RESOLUTION-independent,
       which is the fault lane E exists to catch: with one,
       `UiResolutionAuditUiPerfSmokeTests.TheRigEditorFaces_FitTheDockRow` went
       red for NINE of the ten families at 3840x2160 (cabinet 0.75, amp 0.96 —
       design size in a corner of a row that never grew). A step at a breakpoint
       is not a share of the parent and not a content-sized row, so it breaks
       neither W1 rule. 860 and not 760 is measured: the cabinet console is the
       tallest face at 516 px and only reached 1.29 at 760.
     - `AmpFxRigView.SyncWorkspaceRow` is the only writer of the row, and it
       writes an ABSOLUTE length plus the row's Min/MaxHeight (which is what
       bounds the DRAG) in every state. There is no automatic size to fall back
       to any more: sizing that row to its content is the bug.
       `CommitWorkspaceHeight` runs on `DragCompleted` (a `GridSplitter` is a
       `Thumb`), `ResetWorkspaceHeight` on double-click returns
       the default row height (`RigWorkspaceViewModel.DefaultHeight`, 540, without a laid-out body), and
       `ClampWorkspaceRow` shrinks — never grows, never writes back — so a small
       window cannot lose the user's chosen height.
     Pinned by `Perf/RigCanvasOwnershipUiPerfSmokeTests` (the canvas and the gear
     rail measure the same with the editor open and closed, and across a drawer
     open, at 1600×900 · 1920×1080 · 2560×1440),
     `Perf/PlayModeScreensUiPerfSmokeTests.RigToneBrowser_isResizable_andItsHeightSurvivesARebuild`
     (restore → real drag → persist → rebuild),
     `AmpFxRigScreenTests.TheToneLibrary_IsResizable_ByASplitterAboveIt`,
     `AmpFxRigDockBarTests.TheWorkspace_GivesTheRoomToWhicheverHalfWasAskedForLast`
     and `UI/ViewModels/PlayMode/RigWorkspaceViewModelTests`.
   - **`RigToneBrowser` still CLIPS (`ClipToBounds="True"`)** for the reason
     `RigDockBody` does: a `Border` does not clip, so anything that asks for
     more than its row would be drawn out of it and over the footer's tuner
     strip (owner, 2026-09-14). Containment is structural; `RigTonesScroll` and
     `ToneDesignScroll` (side by side) are what make the contained parts reachable. **The code-computed cap is gone**
     — `CapToneDesign`, `MinBrowserBodyHeight` and `MinToneDesignHeight` existed
     because the panel sat in an `Auto` row inside a fixed one, and the mode's
     scroller is the `*` row. Everything inside it is
     `ScrollViewer.*ScrollBarVisibility="Disabled"` on both axes with no
     `MaxHeight` — the match list, `ToneSectionsPanel`'s candidate list, the
     category rail and the two TONE3000 lists — so they grow and the one
     scroller carries them. NOT scaled: it is a form. Detail and the guard in
     [../pedalboard/ai-tone-design.md](../pedalboard/ai-tone-design.md) § "The
     layout rule this surface adds".
   - Drag-to-reorder is `Views/PlayMode/RigBoardDragController`. The grab handle
     is the tile's HEAD; parameter knobs now live in the selected editor.
     `PointerCaptureLost` BUBBLES: the controller
     scopes its own cancel with a `_capturing` flag, because the card Button
     shedding its capture as the drag takes it would otherwise tear the session
     down one frame after it started, silently.
3. **A FIXED-HEIGHT BAR MUST NOT WEAR `FirePadHeader`, and the failure is
   silent.** `FirePadHeader` spends 36 px of vertical inset, so a
   `Border.FireHeader` pinned to `Height="56"` hands its content 20 px — a
   segmented control needs ~33, its captions measured **0 × 0**, and SCENE,
   SCENE and SERIES/DUAL/TRIPLE rendered as empty pills. No warning, no
   exception, and every container still measured non-zero so the layout smoke
   test passed. `FirePadBar` (26,0) exists for exactly this; the old `Border.FireTopBar`
   had always inlined it, which is why it never had the fault. **Caught only by
   putting the render next to the mockup** — see the capture note below.
4. **The identity fold is ONE pure function**, `Controls/Fire/FireRigCategory`:
   the rig's ELEVEN engineering families (`DspModuleCategory`) onto the spec's
   EIGHT fixed identities. Three folds are decisions and are pinned one by one
   (Filter→EQ, Pitch→Modulation — the spec's own wording for that identity is
   "chorus / phaser / flanger / **detune**" — Routing+Utility→Utility/Routing).
   A per-module id list on the screen would have been a SECOND catalogue.
5. **"EDITED" is a COMPARISON, not a flag** (`ViewModels/PlayMode/RigEditState`).
   A flag has to be raised by every mutation path — add, remove, move, bypass,
   knob, model picker, IR path, branch tag, master bypass, A/B — and is wrong the
   first time one is added without it. Comparing the snapshot the save path
   already builds against the applied preset's cannot miss a path. Note
   `DcRigPreset` is a record over a LIST, so record equality compares
   REFERENCES: two snapshots of an identical rig are never `==`, which is the
   whole reason that type exists.
6. **No knob on this screen is written by hand** — the board renders
   `PedalNodeViewModel.KnobParams`, materialized from the native module
   catalogue. That is what makes §7 (below) structural instead of a correction
   list.

Guarded by the default-gate `UI/AmpFxRigParameterSetTests` (the §7 table, 43
cases) and `UI/AmpFxRigScreenTests` (16: the identity fold, the EDITED
comparison, and source lints for the six regions, the typed-literal ban, the
knob provenance, the bypass pair and real meter bindings) plus
`Perf/PlayModeScreensUiPerfSmokeTests.RigScreen_*` (two: every unit template
realized over a real chain, and the empty-chain branch).

### §7 — the CORRECTED rig parameter sets

The mockup's `UNITS` table hand-writes each card's knobs, and several of them
name a parameter the DSP does not have. **A knob for a parameter the DSP does
not have is a lie on screen: it turns and nothing happens.** Twelve phantoms
were dropped and thirteen real parameters restored; the table, with the DSP
evidence for each row, is `DotCharter.Tests/UI/AmpFxRigParameterSetTests`
(`MockupUnits`). The headline three:

| Mockup drew | The DSP has | Evidence |
|---|---|---|
| Neural Amp **Sag** | nine params, no sag and no wet/dry mix | `NamAmpModule.cpp` `namAmpTypeInfo()` — the amp FACE (`cores/dcdsp_ampface.h`). A capture REPLACES the amp; there is no seam to blend it into |
| Cabinet **Room / Distance / Air** | `model` · `mix` · `lowCutHz` · `highCutHz`, plus dual-IR blend/gain/polarity/alignment and `outputDb` | `DspModuleFactory.cpp` `"cab"`. IR alignment is a delay in milliseconds, not a simulated microphone position. |
| Detune Doubler **Width / Tone**, and an LFO | `detuneCents` · `spread` · `delayMs` · `mix` | `dcdsp_doubler.h`: "the voices are CONSTANT-RATIO granular detuners, **not LFO vibrato**" |

### …and in 0.51 §6.8 was brought to the mockup's STRUCTURE

The screen had the six regions and the right vocabulary and still did not look
like the design, because three structural things were missing and one editor was
a stub. The owner: *"the amp fx rig is still lacking feature, design and
structure/layout that the html has"* and *"selecting an amp or such doesn't open
the nice view that the web has"*. The port is **native Avalonia only** — the
mockup and `Web/Rig/*` are reference, and nothing here touches the WebView
experiment.

**1. The module editor is now a FACE per unit kind, and the fold is on
`PedalSkin`.** `ViewModels/PlayMode/RigEditorFaces.For` → Amp / Cabinet /
Generic; `RigDockUnit` is the selected editor's binding root. The
mockup's own `moduleEditor()` switches on the unit's **id**, which is the second
catalogue §7 forbids; the skin is a fold the rack already derives from module
metadata, so a new native module lands on the right face with nothing here to
update. A face is a lit `Border.FireFacePanel` carrying an engraved name plate
(name + the unit's own MODEL rail; its old `FireFacePlate` class went unused and was
deleted with #242) over the whole knob row at 82 px, and
a 250 px right-hand column for **the file this unit loads** — CAPTURE on a neural
amp (with the REAL `INamCaptureLibrary` list, click to load) and IMPULSE RESPONSE
on a cabinet. One column, named for the job; the capture *list* and the capture
*status* stay amp-only.

**2. The board draws a signal PATH, not a shelf.** `Border.FireJack` +
`TextBlock.FireJackLabel` at both ends, a `RigWire` (a 2 px lead with the
design's arrowhead, coloured by the inherited `FireUnitState.LaneToken` — lane
rows set their lane's token; the BOARD row `Grid x:Name="RigBoardPath"` sets
`SerialLaneToken` — lane A's on a series board, the accent around a split
region — and the serial run, both leads, both jacks' ring + pin and the OUT
connector all inherit it, so the path is ONE colour from jack to jack) between,
and a `Button.FireInsertDot` in every gap that arms
`AmpFxRigViewModel.InsertAt`; the dot rests hidden and shows on hover or focus
(the design draws a bare arrow), and a lane's tail (`Panel.FireLaneTail`)
stretches to the lane's mixer node so the mixers stand in a column — the
gear panel could only APPEND before, so the only way to put a boost in front of
the amp was to add it at the end and drag it back. **The connector lives INSIDE
the item container**: `RigBoardDragController` reads the items panel's children
as its lane and decides by item CENTRES, so a uniform prefix moves every centre
equally and the insertion maths is untouched. A separate connector `ItemsControl`
would have doubled the lane. **The path is one line with no gaps, and the
"Lane A" caption is a row ABOVE it** (`RigSerialLaneLabel`, row 0 of
`RigBoardPath`), never a row inside the serial segment — that shipped on
2026-09-07 and put the run's connectors 8 px below the jacks' leads in a
different colour, with the OUT arrowhead 16 px short of its jack and a bare lead
running past it ("arrows in rig view not nicely connected"). The jack wells are
44 wide around a 30 px ring, so `RigJackIn`/`RigJackOut` carry a −7 margin on
the wire side, and the trailing connector `RigLeadOut` is 60 px (the IN side's
16 + 44) so its arrowhead lands ON the ring. `Perf/RigBoardWiresUiPerfSmokeTests`
pins centre line, adjacency and colour for both a series board and a split one.

**2b. The editor is gear on the left, the rack on the right.** `RigModuleEditorView`
hosts the family view in its left column and a 340 px side rack
(`Border.FireEditorRack`) with the performance macros, the control assignment
editor and the MIDI input — shared by every family, so no family view carries
them any more and nothing is stretched across the dock. The rack's three cards
wear `FireFacePanel.Rack` (the violet card plate, accent eyebrows) and the
assignment editor is a FORM (stretched pickers, Start | End on one row,
`NumericUpDown.FireNumeric`), not the WrapPanel staircase it was until
2026-09-08.

**Gear fills UP TO its design width and no further (2026-09-08).** The owner's
complaint was "so much space wasted and stretched": a 1600 px dock had the
amp head at its 830 px content width with the secondary row 260 px short of
it and the SIGNAL card wrapped underneath, while the graphic EQ stretched its
ten bands 150 px apart. Two shapes, one rule each:
- *A face that is designed to be wide* (the amp) sits in a Grid column whose
  `ColumnDefinition` carries `MaxWidth="1180"` — star width up to the cap, and
  the leftover stays to the right. The head, the model bar and the secondary
  row (`Grid` `Auto,*`: the knob cards, then the SIGNAL card taking the rest)
  therefore end on ONE right edge, and the capture library
  (`RigCapturePanel`) uses the room beside the gear instead of being an
  expander under it. `RigAmpFaceUiPerfSmokeTests` pins the cap, the shared
  right edge and that no card wrapped.
- *A face with a natural size* (the graphic and parametric EQs, the dynamics
  rack, a treadle) is `HorizontalAlignment="Left" VerticalAlignment="Top"` at
  that size. A `UniformGrid Rows=1` under a stretched panel is how the EQ came
  to be spread across the dock; the parametric EQ and dynamics racks were
  stretched panels with 38 px compact dials in the top-left corner.
- *A PEDAL is the third shape*: `RigScaleToFit` now scales UP as well as down
  (`RigArtwork.MaxEditorScale` 1.35, the prototype's `clamp(44px,5.7vh,86px)`
  knobs growing with the viewport), so the enclosure fills a tall dock, and
  `RigPedalEditorView` is two columns — the enclosure, then a 320 px aside
  (`RigPedalAside`) with the file strip, the searchable library, the SIGNAL
  card, the lyrics and the drive hint. Stacked under a small centred pedal
  they left 600 px of empty dock either side (owner's second round).
- *A ONE-control pedal keeps the two-column enclosure but lays its knob in ONE
  column at the big dial* (`RigDockUnit.EnclosureGridColumns` /
  `EnclosureDialSize` = `SingleDial` 96, the design's `.instrument-single`;
  `EnclosureColumns`/`EnclosureNaturalWidth` are unchanged because the eyebrow
  and name need the width). Clean Boost's lone Gain sat in the left cell of an
  empty pair ("clean boost looks kinda weird", 2026-09-08).
  `RigDockUnitEnclosureTests` + `RigPedalEnclosureUiPerfSmokeTests.ALoneKnob…`.
- *The footer prints WHY the jack picker is grey* (`RigInputChannelReason`,
  `LiveInputChannelViewModel.Hint`, visible when `!IsAvailable`): input off, or
  WASAPI (no input mask to pick from). The reason lived in the tooltip alone and
  an empty disabled combo beside a live tuner read as "input selector doesn't
  work anymore". The Tempo box is disabled with no value when no delay in the
  chain supports sync (`RigTempoViewModel.HasTargets`) — by design, tooltip
  `Rig_TempoHint`.
- *The capture library is a searchable card* (`RigDockUnit.CaptureSearch.cs`:
  `CaptureFilter` → `FilteredCaptures`, reconciled in place; the pure rule is
  `MatchingCaptures`, `RigCaptureSearchTests`) beside the head; the model
  bar's "From library" combo hides while the list is shown
  (`ShowsLibraryCombo`), so a capture is never offered twice. **Its width is
  the dock's leftover** (`RigAmpFaceView.CaptureWidthFor`: host − 1180 − 18,
  floor 300, set from the columns' `SizeChanged` — two star columns would split
  a narrow dock evenly and shrink the head; `RigAmpFaceLayoutTests`) and its
  height is bound to the gear column's `Bounds.Height`, so the two blocks end
  together instead of "a head, a floating card and a gap".
- *The tone drawer is PREWARMED by name* (owner: "pressing this causes
  lag/short freeze"). `RigToneBrowser` is a hidden root INSIDE the rig
  overlay, and the drain stops at the first hidden root, so the overlay's
  prewarm never reached it and the first click on the tone pill laid out
  every preset card. Both hosts list it after the overlay:
  `PlayModeView.NestedScreenBodyNames` and `MainWindowPrewarm`'s
  `PedalboardOverlay` block. **Since the Tones mode became its own view
  (2026-09-27) the list is `RigTonesMode`, `RigToneBrowser`,
  `RigToneDesignPanel`, IN THAT ORDER**: a UserControl's template is not applied
  until it is measured, so the drawer is not in the visual tree at all until the
  view is realized. `PlayModeDeferredScreenUiPerfSmokeTests` asserts the order
  and `RigTonesRedesignUiPerfSmokeTests.ThePrewarm_RealizesTheCardGrid_…` that
  the cards are realized with the mode closed. **The cards are laid out three per
  drain turn** (`ChunkedWrapPanel ChunkSize="3"`, #253) — the browser was one
  1.1-1.9 s turn per rig instance — and there are TWO instances, each prewarmed
  once: the editor's overlay and Play Mode's are separate `AmpFxRigView`s
  (`UI/ChunkedPanelTests.TheToneBrowser_IsListedOncePerRigInstance`). The grid
  stays NON-virtualized (owner: a virtualized one lags on scroll). The
  pill itself wears the header's field surface with the accent edge — on the
  near-black chrome fill it read as "a hole in the header".
- *Four small faults from the owner's screenshots (2026-09-08):* the tone
  pill is a FIXED 260 px (a long name trims inside it; it used to push Save
  across the bar); the drag ghost is the node CARD — `FireDragSession
  .GhostPartOf` ghosts the descendant wearing `FireDragGhost` instead of the
  item container, which also holds the lane wire and its arrowhead
  (`RigHardwareUiPerfSmokeTests` pins it); the routing console's mode strip
  wears `FireModeButton.Compact` (60 px) inside its 220 px subpanel, declared
  AFTER the base style because **Avalonia applies styles in declaration order,
  not by specificity** — declared before it, the setter lost and the third
  button showed no word; and the gear row's IN RIG chip rides the kind line
  under the name (`RigGearRowTests`) — it shared the +'s column once (a module
  can be in the rig AND addable again, and the + drew over the chip's first
  letter), then had a column of its own beside the star and the +, which left
  the NAME about 80 px ("Neural A…", 2026-09-24).
  **That one was first "fixed" by spacing the STAR, on a misread crop, and
  the owner had to send it back** ("the overlap still exists"): crop the
  capture at 3× and LOOK at which glyph overlaps before naming the fix.
- *Reset and focus* (owner's third round): a knob resets on double-click OR
  right-click (`FireKnob.OnPointerPressed`), a fader carrying
  `fire:FireReset.Default` resets the same two ways (`Controls/Fire/FireReset`,
  attached so the faders stay stock `Slider`s), and the hint says so in all
  three languages. The knob's focus ring draws for KEYBOARD or programmatic
  focus only — a click focuses through `Focus(NavigationMethod.Pointer)` and
  draws no ring (`FireKnob.ShowsFocusRing`,
  `TheFocusRing_DrawsForKeyboardFocus_NotForAClick`); the pale rectangle round
  a knob someone had just turned read as "a weird white border".
- *Every control on the header and the routing strip is
  `FireHeaderControlHeight` (36)* — the tone pill, Save, the mode strips, Copy
  to both, BYPASS, the cog (`Button.FireHeaderIconButton`, framed like the
  close), the close, the zoom cluster, Fit and Add; the tone ▲▼ is a framed
  stepper (`Button.FireQuiet.Stepper`). The 40/35/34/28 mix read as
  "questionable".

**The gear's own detail follows the prototype's CSS, not the layout** — the
owner: "it's just the details on the gear itself". `FireKnob.Unit` prints the
unit beside the value plate and `PedalParamViewModel.Caption` drops it from
the label (`DspControlText.SplitUnit`, whitelist — "Feedback (BBD)" stays
whole; `RigParamCaptionTests`); `FireKnob.DialSize` gives the amp's secondary
cards a bare 62 px dial and the rack four 52 px ones; the enclosure carries
the design's double bevel (`Border.FireEnclosureBevel` + `.Step`); every
family's footswitch is the chromed dome (`FireHardwareSwitchCap`); the EQ and
treadle faders are `Slider.FireEqFader` — a 7 px recessed rail with a 25×15
cap whose stripe is the Slider's `Foreground` (set it to the family accent in
the view; the base theme's circular thumb is what "what is that slider" was
about); a fader's readout sits on a `Border.FireEqValuePlate`.

**2c. The header is the design's top bar** (2026-09-08): the waveform brand
mark (`Path.FireBrandMark`, `IconRigBrand`) and the title, PRESET as a 215×40
dropdown trigger (`FireTonePill` — still `RigBrowseButton`, the drawer's door),
▲▼, an outlined Save (`Button.FireOutlineButton`), SCENE A|B, "Copy to both"
as a text button with the cube icon (`Button.FireTextButton`, `IconRigCopy`),
an outlined bold BYPASS, and a cog (`RigHeaderMenu`) whose flyout holds SWITCH
(`RigCompareButton`), Export tone and Import tone as MenuItems — the button lint
walks that menu's items by name.

**2d. The tone browser's head, rail and cards** — "Tones N" (the count a
fixed-width tag) in a block as wide as the sidebar, the search (`IconRigSearch`,
capped at 340 px, starting on the grid's left edge), a fixed
`FireRigToneSidebarWidth` sidebar holding the category rail
(`ListBox.FireList.ToneRail`, the active row filled with a bright bar on its
left; each row's COUNT under the current search on the right, a row that holds
nothing dimmed — `Border.FireListRow.FireToneRailEmpty` — never hidden, so the
rail does not change length while you type), and cards that read top to
bottom: category and origin tags (`Mine` for the player's own, `Edited` on the
tone that is playing when the live rig has moved from it), the name, the whole
description, and the chain — a strip of one bar per unit in its family's Fire
colour (`PresetModuleGlyph.TokenKey`) over the units spelled out in the
catalogue's words (`PresetCardViewModel.ChainSummary`, two lines, the rest a
tooltip). The empty grid says WHY (`PresetBrowserViewModel.EmptyText`: the
search, an empty My Tones, or an empty slice) and offers Clear search.

**Three things about that line changed in W4, and each was a reported fault:**

- **"YOUR SOUND STARTS HERE" is GONE.** A decorative eyebrow carrying no
  information cost a line of the smallest supported window and told the reader
  nothing the title did not.
- **The card GROWS with its description.** It drew `MaxLines="2"
  TextTrimming="CharacterEllipsis"` in a fixed 136 px box, so every factory tone
  ended in an ellipsis mid-word ("around a transparen…"). Width and MIN height
  are the `FireRigToneCardWidth` / `FireRigToneCardMinHeight` tokens — MIN,
  because the floor keeps a one-line description's card level with its
  neighbours and a long one grows past it. `RigTonesModeUiPerfSmokeTests
  .EveryToneCardDescription_IsFullyReadable` asserts no `TextTrimming` cut, no
  `MaxLines` clamp and that each block was given the height it asked for.
- **"Save as new…" is the chamfered primary and the body face.** Overwrite and
  Delete act on the selected USER tone, so they are NOT RENDERED rather than
  greyed for anything else — and since 2026-09-27 they are the two rows of a
  `···` menu (`RigBrowserToneMenu`) rather than two buttons: at 1280 px in
  German two more captions pushed the designer toggle and ✕ out of the head.

**3. The chain is the design's graph; the selected editor owns the controls.**
One shared node template: artwork, name and a state dot in the unit colour,
108×90 (`Grid.FireUnitLayout`); a serial chain binds `.series` on every card
and gets the 124×148 card with `BoardSummary` under the name. A node carries
no buttons — bypass fades the card (`.bypassed`) and dims the dot; bypass and
remove live in the editor dock. Clicking selects the node and opens
`RigModuleEditorView` in the dock. Continuous
parameters use `RigDockUnit.PrimaryKnobs` and `SecondaryKnobs`
(the amp's resonance/input/output); cabinets render their secondary controls
through `CabinetSlots.Knobs` and `Blend` instead. Model choices,
extra selectors, capture/file loading and clearing, and lyrics editing remain in
that editor. Both files are scanned by the parameter, accessibility and dead-button
guards. Width-ladder helpers no longer define chain geometry.

**4. The gear panel spans the footer row.** In the mockup the gear column runs
from under the header to the floor and the footer stops at its left edge. Ours
spanned the board row only, with a full-width footer underneath — so the tuner
and the routing strip rendered UNDER the gear panel in whatever width was left.
`Grid.RowSpan="2"` on `RigGearPanel`, `Grid.Column="0"` on `RigFooter`.

**…and its WIDTH is the `FireRigGearPanelWidth` token, never a literal** (lane E,
2026-09-13). It is the rig's context rail and it halved as a share of the screen
exactly as the front door's two did: a literal `Width="320"` is 16.7 % of a 1920
window and 8.3 % of a 3840 one. 320 / 420 above the wide breakpoint = 10.9 %,
the 1440p share. Everything else on the screen already took the rest of the
window and is now asserted to — measured at 3840×2160 (Wide) the body column is
3384 px and `RigBoard`, `RigDockBody` and `RigFooter` each arrange at 3382. The
palette's virtualization is untouched by the width change
(`PlayModeScreensUiPerfSmokeTests`' row-count case still passes); the rail and
the three stretched regions are pinned by
`Perf/UiResolutionAuditUiPerfSmokeTests.TheRigScreenPanels_UseTheWindow`.

**5. THE ANCESTOR-CAST TRAP, and it had shipped.** Four commands reached their
handler through `$parent[ItemsControl].((vm:AmpFxRigViewModel)DataContext)`. The
ancestor `ItemsControl`'s DataContext is the **PlayModeViewModel** — the screen
never repoints it — so the cast matched nothing, the binding produced no command,
and the card's ✕, the signal chip's select and the gear panel's category filters
**drew perfectly and did nothing**. No warning, no exception, every container
measured. Cast to `PlayModeViewModel` and go through `.ToneRig.AmpFxRig`;
`RigScreen_composesItsSixRegions_…` now asserts every button in every item
template resolves a command that CanExecute.

The header's `Button.FireTonePill` remains the drawer toggle
(`x:Name="RigBrowseButton"`). The strip now carries title, routing, add, fit and
the armed insertion state; it no longer duplicates the chain as signal chips.
Gear rows retain category artwork and an add button even when `IN RIG` is shown:
that tag is informational, and another instance can serve another lane.

**Two test-only traps this found.** A `ContentPresenter` nested five templates
deep builds its child on ITS OWN measure, which lands in the pass *after* the one
that created it — a smoke test that lays out exactly once sees empty presenters
and no knobs, while a real window renders continuously and never notices. And an
`ItemsControl` container captured before an add/remove is a DETACHED Border: an
attached-property read off one reports the default, which looks like the property
regressing.

Guarded by the default-gate `UI/AmpFxRigEditorTests` (the skin→face fold, the
dock's projection over a real chain, the insert-point arithmetic, the card-width
ladder, plus source lints that every `ItemsSource` in the dock is one of the
unit's own lists and that the board really draws jacks / cable / wired insert
dots) on top of the existing `UI/AmpFxRigScreenTests` and
`UI/AmpFxRigParameterSetTests`.

### The chain mirrors are RECONCILED, never cleared

`AmpFxRigViewModel.RefreshChain` used to `Clear()` + refill `Chain` and
`SignalChips` on every `Nodes.CollectionChanged`, and rebuild the whole
`GearGroups` list with it. Both board ItemsControls are non-virtualized, so
every add/remove/preset-apply re-inflated EVERY `FireUnitCard` (twice — each
host owns its own screen VM over the one rig singleton), and the cleared
collection reset `RigBoardScroll`'s extent, snapping a scrolled board back to
the chain start. Together with `PedalboardViewModel.Sync` going incremental the
measured cost of "add one pedal" fell ~290 ms → ~25 ms (Debug, headless, real
view attached). Rules that keep it that way:

- `RefreshChain` diffs `Chain`/`SignalChips` against `Rig.Nodes` (remove gone /
  move survivors / insert new; a chip is REPLACED only when its node or its
  "first" flag changed). Never reintroduce a `Clear()` here.
- The gear list's STRUCTURE depends only on the catalogue and the active
  filter; a chain change goes through `RefreshGearBadges`, which writes
  `RigGearItem.IsInRig` (now observable) in place. `RebuildGearGroups` is for
  the filter and the rail-cards event only. `IsInRig` is informational: `CanAdd`
  follows the rail command's availability, so an already-used amp or cabinet can
  be added again for another serial position or parallel lane.
- The dock keeps its `RigDockUnit` while the docked node is still on the board;
  it is torn down only when the node is gone or the selection changes.

Guarded by the default-gate `UI/AmpFxRigChainMirrorTests` (no `Reset` event,
chip/row/dock instance survival, in-place badges) and structurally by
`Perf/RigSyncTimingUiPerfSmokeTests`, which attaches the real view, logs the
timings and asserts container survival counts across add / remove / a
five-shared-modules tone switch.

### The board folds into LANES

The owner's v8 mockup draws a split as a recessed panel whose lanes stack vertically and
merge back, and the flat board could not ("it should split and let above and
below have units and then merge back"). The board now renders
**`AmpFxRigViewModel.BoardSegments`** — `RigSerialSegment` (a horizontal run of
compact tiles) and `RigSplitSegment` (the parallel region) — folded
through the SAME pure `SplitRegionLayout` the native routing mirrors through.
Rules that keep it honest:

- **The fold re-runs on `PedalboardViewModel.BoardStructureChanged`** (raised at
  the end of every `RebuildBoardColumns`), NOT on the node collection: a branch
  re-tag, a 2↔3 re-width and a junction bypass change the lanes without
  touching `Nodes` — the routing properties are a LAYOUT input (the 0.16 bug).
- **Reconciled, never cleared** — the same no-Reset / instance-survival contract
  as the chain mirrors. `AmpFxRigBoardLaneTests` is the guard (fold membership,
  lane survival across re-tags/re-widths, serial collapse, auto-merge, the
  lane-armed insert gap); proven non-vacuous by a deliberate `Clear()`.
- **Serial and lane runs share one node template.** On a split board every
  node is the 108×90 graph node; the serial chain alone uses the taller card.
  Both inherit the same typed `PedalNodeViewModel` template, retaining
  selection, drag and insert behaviour. `TheBoard_DrawsTheSplitRegionAsLanes`
  pins the shared template; the split Perf smoke checks the dimensions, the
  mixer nodes and the junction nodes. Empty lanes retain
  `Button.FireLaneDropZone`, which arms the gap and accepts drops.
- **Every lane ends in its mixer node** (`Border.FireMixerNode`: "Mixer A",
  level slider with its dB, a bare 26 px pan `FireKnob` (`Dense="True"` — no
  caption, no readout; the pan text sits under it) materialised from the
  merger's own pan param, M and S). With no explicit merger the node is
  disabled and prints why (`Rig_LaneNeedsMerger`).
- **The splitter/merger NODES render as the region's frame** (two `RigFan`s
  wearing `FireLaneBus` draw the design's ELBOW wires — the prototype's own
  routine: horizontal, a 12 px quarter-turn, vertical, a quarter-turn,
  horizontal, an arrowhead — — one per lane in the
  lane's colour, out of Split and into Merge — off each lane row's invisible
  `FireLaneStub` anchor, whose Background carries the lane colour the fan
  reads; the Split / Merge `FireJunctionNode`s carry the design's fork glyph
  (`Path.FireJunctionGlyph`) and open their console; there is no region
  header — SERIES in the routing header dissolves it), never as cards — but only the LIVE pair: a bypassed
  or stray junction still draws as an ordinary card, exactly as
  `SplitRegionLayout` says. Lane colours are the fixed `FireLaneA/B/C`
  positional tokens (the mockup's PATH_COLORS).
- **The insertion gap is lane-aware.** `InsertBranch` rides beside `InsertAt`;
  a dot inside a lane arms the lane's branch and `AddGear` seats through
  `DropToRow` (move + tag, atomic), so a unit added into lane B cannot land at
  the right slot in lane A. Every lane also ends in its own trailing dot — an
  empty lane has nothing else to click.
- **Drag targets whichever RUN the pointer is over — cross-run drops
  re-branch.** The first cut scoped a drag to the run it started in, and that
  scoping was the rejection: dragging a card INTO a lane is the whole gesture
  the owner reached for. `RigBoardDragController` resolves the target per
  move by hit-test (`TargetRunAt`: walk up from the hit to the first
  `RigBoardLane` / `RigSerialSegment` DataContext, then find that run's own
  items panel by ItemsSource identity), with the ORIGIN run as the forgiving
  fallback for card drags. The gap→flat-chain mapping is three pure
  functions, all pinned in `RigBoardDragAndToneBrowserTests`:
  `TargetFlatIndex` (same-run, with the own-gap no-op — run items are NOT
  contiguous in the chain), `TargetFlatIndexInto` (cross-run: no own-gap
  no-op, vacated-slot adjustment, empty-lane fallback = the region's body
  start via `AmpFxRigViewModel.BodyStartOf`) and `InsertFlatIndex` (the
  gear-rail drop: RAW slot, the new node comes from the tail). A lane drop
  commits `DropToRow` (move + branch tag, atomic); a serial drop `MoveTo`; a
  GEAR row dragged from the rail commits `AddGearAt` + the branch tag — the
  gear list is the controller's second drag source (`OnGearPointerPressed`;
  a press that never travels stays the row's own append click). Junctions
  never enter a lane by drag (`DraggingAJunction`). The pointer-driven proof
  is `RigScreen_dragsASerialCardIntoALane_AndItReBranches` (Perf); the
  VM-seam proof is `AmpFxRigBoardLaneTests.DroppingAcrossRuns_…`. The dock's
  branch control stays as the keyboard/no-drag path.
- **The templates are NESTED ItemsControls now, so `$parent[ItemsControl]` is
  the run — whose DataContext is the SEGMENT.** Every command in a board
  template goes through `$parent[UserControl]` instead; the ancestor-cast trap
  above is why. `RigScreen_withASplitChain_FoldsTheBoardIntoLanes` (Perf)
  realizes a split chain and asserts every lane dot resolves a live command,
  the lanes stack A-above-B and the fan/badges measure;
  `TheBoard_DrawsTheSplitRegionAsLanes` is the source lint (proven by breaking
  a class name).
- The board's `HasChain` subtree is a **deliberate `KnownColdSurfaces` entry**
  (`PrewarmCoverageTests`): it is gated by DATA, hidden exactly when the chain
  is empty — a prewarm of that state would lay out nothing but the jacks.
- **The signal path takes the LANE colour** (tone-rig design, 2026-09-07):
  `FireLaneA/B/C` are the design's amber, magenta and cyan, a series run draws
  as lane A, the trunk around a split region keeps the accent, and the selected
  node's edge is amber (`FireWarning`). The jacks (`FireJack`, `FireJackPin`,
  `FireJackLabel`) stay accent.
- **The header's tone pill is the mockup's LCD strip** — same
  `RigBrowseButton`, same command; the face became the recessed near-black
  plate with a "PRESET" eyebrow, a hairline and the name in bright mono
  (`FireTonePill` restyle), because a lit accent pill read as an ACTION and
  this is the rig's one piece of standing state.
- **The chain is FITTED to the canvas, not scrolled sideways** (W1,
  2026-09-16). `AmpFxRigView.BoardFitScale` multiplies the persisted `Rig.Zoom`
  into `RigBoardZoomHost`'s transform by
  `min(1, canvasWidth / RigBoardPath.Bounds.Width)`, floored at
  `PedalboardViewModel.MinZoom` — the same mechanism the Fit button drives,
  applied continuously. It reads the path's LOGICAL width (a
  `LayoutTransformControl` keeps its child's bounds in logical pixels, and the
  path is content-sized), never the ScrollViewer's EXTENT, which would be a
  feedback loop. Six blocks therefore produce no horizontal bar at all, and
  twelve are fitted rather than cut off.
- **The board's scrollbars do not FADE, and horizontal is `Auto`**
  (`RigBoardScroll` = `Auto`/`Auto` + `AllowAutoHide=False` + class
  `FireBoardScroll`, styled in `Views/PlayMode/AmpFxRigStyles.axaml` — scoped to the
  rig since #242): the app-wide chrome fades
  scrollbars to 0.2 opacity at rest, which on a chain running past the edge read
  as "the rig ends here". `Visible` was the first answer and reserved a
  permanent empty track under every chain that fits; `AllowAutoHide=False` is
  the half that mattered. Scoped by class — never restyle `Chrome.axaml` for
  this. Pinned by `TheBoard_ScrollsHorizontally_AndHostsTheZoomTransform` and
  `Perf/RigCanvasOwnershipUiPerfSmokeTests`.

### No dead buttons — every control in this screen must raise something

**Pinned by `AmpFxRigNoDeadButtonsTests` (default gate).** It parses
`AmpFxRigView.axaml` and `RigModuleEditorView.axaml` as XML and, for every `Button` / `ToggleButton` /
`RepeatButton`, walks the `Command` binding path over the REAL view-model types
by reflection: the path must land on an `ICommand`. A `{Binding}` discard, a
misspelt member, a path that stops on a non-command, a `Click` handler the
code-behind does not declare and a button with none of those all fail it.
A second fact resolves every path scoped to `AmpFxRigViewModel` on a live
instance, so a command that is *null* at runtime fails too. Comments cannot
satisfy either: `XDocument` yields a comment as an `XComment`, never as an
element or attribute.

Four binding forms are understood, and the lint asserts each one is still in
use, so a change that stops matching them fails loudly instead of passing
vacuously: a direct path against the nearest ancestor-or-self `x:DataType`, a
`$parent[…].((vm:Type)DataContext).Path` cast, a `#Other.Command` element alias,
and a `Click` handler. A fifth verdict exists because the board needs it: a
`ToggleButton` with no command is accepted when its `IsChecked` binds a
**writable** property two-way (the per-unit bypass foot), and refused when that
binding is `Mode=OneWay`.

**The owner bug it ends (v0.48.5): "several buttons in the Amp & FX rig have no
function: A/B, Copy, Both, Switch — Tuner too."** All five are the header's
SCENE strip and the footer's tuner, and none of them was a missing feature:

- **A and B were `Border`s.** They wore `FireSegmentItem` — the same class the
  routing header's SERIES/DUAL/TRIPLE strip gives to real `ToggleButton`s — so the pair
  the whole strip is named after looked pressable and was not. They are
  `ToggleButton`s over `SelectCompareSlotCommand` now, `CommandParameter` `"A"`
  / `"B"`, and jumping to a named slot is the primary A/B gesture.
- **SWITCH was gated `IsEnabled="{Binding Rig.CanCompare}"`,** which is false on
  every rig nobody had pressed "Copy to both" on first — i.e. in the state every
  player meets the screen in. **The empty slot is a FORK now**
  (`ToneCompareSlots.SelectSlot`): the target starts as a copy of what is
  playing, so the press always lands and nothing audible is lost. Nothing in the
  strip carries an `IsEnabled` any more, and
  `NothingInTheSceneStrip_IsUnpressable` fails if one comes back.
- **The compare push went to seat 0 for ever.** It used `Apply`, while the rig's
  seat picker repoints `_dsp` — so on player 2's board, SWITCH compared player
  1's tone. It is `ApplyToPlayer(SelectedPlayer, …)`;
  `RigCompareSceneTests.TheComparePush_LandsOnTheSelectedSeat_NotSeatZero` is the
  pin. **`ApplyToPlayer` still has no other caller in the UI** — the preset
  browser applies to seat 0 too, which is the same bug one surface over and is
  NOT fixed.
- **The footer tuner never drew its reason.** `RigTunerViewModel` words every
  blank state it can be in (`Tuner_NoInput`, `Tuner_Listening`,
  `Tuner_SeatNotLive`) and the footer printed the note, the meter and the cents
  and nothing else, so with no input armed it was a dash and a still needle.
  `RigTunerStatus` draws `StatusHint` whenever `HasReading` is false, and the
  constructor seeds it for the one state the poll can never report (no transport
  ⇒ the poll never runs). `RigTunerStatusTests` is the pin. The tuner itself was
  never broken — it has polled since `AmpFxRigView` started reporting its
  surface liveness.
- **`RigTunerViewModel.Toggle()` is deleted.** There is no tuner PANEL to
  toggle — the footer draws the needle permanently — so neither the generated
  command nor the method had a caller. It had sat in
  `RelayCommandReachabilityTests.KnownUnreachable` for three releases behind an
  excuse ("its `SetSurfaceLive` is called by no view either") that had been
  false since 2026-08-15.

**Write a new rig control as a button with a command, or do not draw it.** A
`Border` that looks like a segment item is the cheapest way to break the app's
oldest rule — a capability the app cannot honour must never look live — and the
lint above cannot see one, because a `Border` is not a button. That half stays a
human judgement.

### No nested scroll regions, anywhere in this screen

**A scroller inside a scroller is a defect here** (W1): the wheel lands on
whichever one the pointer is over and the outer extent lies about how much
content there is, while both containers arrange perfectly and nothing warns.
Two guards, because they see different things:

- `Perf/RigScrollRegionsUiPerfSmokeTests.NoScrollRegionInTheRig_SitsInsideAnother`
  walks the laid-out visual tree in every state that realizes a different
  subtree and prints the whole inventory. A `TextBox`'s own template scroller is
  excluded by name — a text field scrolling its text is not a region of this
  screen — and that exclusion is load-bearing: `NumericUpDown.FireNumeric` in
  the editor rack brings one whose bars are `Auto`.
- `UI/RigScrollRegionSourceTests` reads the same thirteen views as XML in the
  DEFAULT gate, because the runtime walk can only see what the headless fixture
  realizes: the designer's match list needs a configured AI provider, the amp's
  capture library a populated NAM library. `ToneDesignPanel` and
  `ToneSectionsPanel` are declared as hosted INSIDE `ToneDesignScroll` (the core
  panel's thread, since 2026-09-27; `RigToneDesignScroll` before W4), so any
  region in either is nested wherever it sits in the file.

**The allow-list is EMPTY as of W4 (2026-09-16), and it stays a list so the next
nested scroller has somewhere visible to be refused.** It carried two entries —
the searchable capture library in `RigAmpFaceView`'s second column and the one
in `RigPedalEditorView`'s 320 px aside, each an anonymous `ScrollViewer` inside
that family view's own `RigEditorScroll`. Both are gone: the card's height cap
went with the scroller, so the library grows and the family view's own scroller
carries it. `RigAmpFaceView`'s capture card keeps the gear column's height as a
**`MinHeight`** rather than a `Height`, so the two blocks still end together
when the library is short and the card grows when it is not.
**That list only shrinks**, each entry is asserted to still BE nested, and
`EveryW4Exemption_IsStillANestedScrollRegion` now also asserts the list is
empty.

### The module editor does NOT render behind the footer — it is CUT by it

The owner reported it as a z-order fault on 2026-09-12 and it is not one, so do
not go looking for a `ZIndex`: `RigDockBody` is inside row 1 of the screen's root
`Grid RowDefinitions="Auto,*,Auto"` and `RigFooter` is row 2, and a sweep of all
100 catalogue modules at 1920×1080 and 1600×900 finds **zero** painted pixels of
the editor subtree inside the strip's box
(`Perf/RigFooterStabilityUiPerfSmokeTests.TheModuleEditor_NeverPaintsIntoTheFooter`,
which asserts NON-INTERSECTION for exactly that reason). What the owner sees is
the **cut**: the editor is routinely taller than the row it is given — measured
401 px of content in a 335 px viewport for 57 of the 100 modules, 490 for the
NAM/limiter/shaper family, and the RACK is 854 px — and Avalonia AUTO-HIDES a
scrollbar, so the last row of knobs was sliced off exactly at the strip's top
edge with nothing on screen saying the content continued. Two halves hold it now
and both must stay:

- **Every family view's root scroller is `x:Name="RigEditorScroll"` with
  `VerticalScrollBarVisibility="Visible" AllowAutoHide="False"`**, and so is
  `RigModuleEditorView`'s rack (`RigEditorRackScroll`) — the treatment
  `ScrollViewer.FireBoardScroll` already had, and that `RigCabinetEditorView` and
  `RigRoutingConsoleView` had found for themselves while the other seven sat on
  `Auto`. It has to be set PER VIEW: `ScrollViewer.AllowAutoHide` does **not**
  inherit down the tree (measured — setting it on `RigDockBody` left every
  `RigEditorScroll` still reading true), which is exactly the kind of one-place
  fix that looks applied and is not.
- **`RigDockBody` carries `ClipToBounds="True"`**, which makes the containment
  structural rather than a side effect of every family happening to have a
  scroller at its root. A family view without one overflows the Border (a
  `Border` does not clip) into row 2, where the footer — a LATER sibling in the
  same Grid — paints over it, i.e. it would make the reported bug real.

**Both halves still stand and neither may be removed — but since the section
below they are the FLOOR, not the answer.** Making the cut legible is not making
it go away; scaling the face into the row is, and that is what the resolution
audit's lane E did on 2026-09-13.

### Every family FACE scales into the dock row

`RigPedalEditorView`'s enclosure had done this since 2026-09-08 and the other
eight family views had not: each was authored at one design size and dropped
into a row that is neither. Measured at 1920×1080 and 3840×2160 (Wide), with the
board at its minimum and the dock at its `2.2*`:

The row is 1562×552 at 1080p and 3382×1294 at 4K, so it grows 5.07× in area
while the face used to keep its design HEIGHT at both. "laid out → drawn" is the
face's logical box against the row and then its box after the transform; the
gap between the two is the magnification, and `ratio` is drawn-@4K over
drawn-@1080p.

| Face | logical box @1080p | laid out → drawn @1080p | @4K Wide | ratio |
|---|---|---|---|---|
| `RigAmpFaceView` | 926×402 | 43.2 % → 66.9 % | 13.6 % → 54.5 % | 0.81 |
| `RigCabinetEditorView` | 1187×516 | 71.0 % → 67.1 % | 17.5 % → 69.9 % | 1.04 |
| `RigRoutingConsoleView` | 1422×416 | 97.4 % → 64.0 % | 19.6 % → 78.6 % | 1.23 |
| `RigGraphicEqView` | 779×430 | 38.8 % → 67.1 % | 7.7 % → 58.4 % | 0.87 |
| `RigParametricEqView` | 1290×287 | 42.9 % → 34.1 % | 8.5 % → 39.0 % | 1.14 |
| `RigExpressionView` | 965×289 | 32.3 % → 46.2 % | 9.8 % → 39.1 % | 0.85 |
| `RigVocalView` | 973×372 | 42.0 % → 59.0 % | 12.6 % → 50.4 % | 0.85 |
| `RigDynamicsView` | 1017×368 | 43.4 % → 55.5 % | 8.6 % → 50.0 % | 0.90 |
| `RigPedalEditorView` | 352×420 | 17.1 % → 24.1 % | 3.4 % → 13.7 % | 0.57 |

**Two of them were genuinely CUT at 1080p and the audit says which**: the
cabinet console and the routing console are taller than a 1920×1080 dock row
before scaling, and every one of the nine scroller extents is inside its
viewport after it. Every face is scaled 2.00 at 4K — i.e. the ceiling, not the
row, is what binds there.

Each root scroller's content is now wrapped in one
`fire:RigScaleToFit x:Name="RigFaceFit"` — the pedal's is still
`RigEnclosureFit`, because there the scaler wraps the ENCLOSURE and the 320 px
"beside the pedal" column stands outside it. Three things about that wrapper are
load-bearing:

- **`TargetHeight` must be `{Binding #RigEditorScroll.Viewport.Height}`.** A
  `ScrollViewer` offers its content infinite height, so the vertical fit would
  never engage and every face would sit at the ceiling — measured with the
  binding removed: `RigGraphicEqView` 581 px tall in a 508 px row, i.e. exactly
  the cut this change removes. The WIDTH needs no such binding, but only because
  every `RigEditorScroll` is now `HorizontalScrollBarVisibility="Disabled"`
  (five of the nine were `Auto`, which hands the content infinite width too).
- **`MaxScale` is the breakpointed `FireRigFaceMaxScale` token** (1.35 → 2.0
  above the wide breakpoint), not `RigArtwork.MaxEditorScale`. The C# default is
  still `MaxEditorScale`, so a host that sets nothing is byte-for-byte what it
  was.
- **`HorizontalAlignment="Stretch" VerticalAlignment="Top"`, and `Stretch` is not
  the obvious half.** The SCALE comes from the child's natural size
  (`Child.Measure(Size.Infinity)`), but the child still has to be ARRANGED at the
  slot the transform leaves it — `Left` arranges it at `natural × scale`, and a
  face with a star column then collapses to its content. Measured on
  `RigRoutingConsoleView` with `Left`: all six lane sliders "too narrow to use"
  (`Perf/RigRoutingConsoleUiPerfSmokeTests`). A `LayoutTransformControl` keeps
  its child's `Bounds` in LOGICAL pixels either way, which is why the audit reads
  the SCALER's transformed box.
- **A face whose own layout reads its arranged width needs checking.**
  `RigAmpFaceView.CaptureWidthFor` runs from `SizeChanged` and splits the host
  between the head (cap 1180) and the capture library (floor 300); under the
  scaler the host is `dock / scale`, so the capture card sits at its floor at
  1080p and the head comes out a pixel over its own cap from the one-frame lag.
  Drawn, the two are within ~20 px of the pre-scaler layout, so this is a
  logical-units shift and not a visible one — but the assertions in
  `Perf/RigAmpFaceUiPerfSmokeTests` had to be restated against that slot instead
  of against literals, and a new face with the same shape will need the same.

**The 4K/1080p ratio lands at 0.81–1.23 for the eight faces and 0.57 for the
pedal, not a flat 1.00, and the gap is arithmetic rather than a shortfall.** The
row grows 5.07× in area while a face capped at 2.0 grows 4× — and at 1080p most
faces are already scaled above 1, so the 4K end runs out of ceiling first. The
pedal is further off because its enclosure is a fixed-aspect PICTURE beside an
unscaled 320 px column: it can only grow until it is as tall as the row.
Raising `FireRigFaceMaxScale` past 2.0 would buy the rest and spend the texture
pack's raster artwork (`RigArtworkImage`, .webp drawn at 17–64 px) doing it.

Pinned by `Perf/UiResolutionAuditUiPerfSmokeTests.TheRigEditorFaces_FitTheDockRow`
(nine families × two resolutions: the face fits the row, the scroller needs no
scrollbar, the scale grew past the old ceiling, and a per-family coverage floor).

### The module editor is a HOST over one view per FAMILY

`Views/PlayMode/RigModuleEditorView.axaml` builds nothing itself: it hands the
selected `RigDockUnit` to `RigEditors/RigEditorTemplateSelector.BuildFor`, which
switches on `RigDockUnit.Family` (`RigArtwork.FamilyFor`, the design's per-module
dispatch folded onto the module id). **Five families have a hardware layout of
their own; the other four still open `RigPedalEditorView`, and that fallback
stays.**

| Family | View | Modules |
|---|---|---|
| `GraphicEq` | `RigGraphicEqView` | `graphiceq` |
| `ParametricEq` | `RigParametricEqView` | `eq` |
| `Expression` | `RigExpressionView` | `wah` · `whammy` · `swell` |
| `Vocal` | `RigVocalView` | `miku` · `mikustomp` |
| `Dynamics` | `RigDynamicsView` | `compressor` · `gate` · `noisereducer` |

- **A layout arranges controls; it never declares them.** Every list a family
  view binds is a projection in `ViewModels/PlayMode/RigDockUnit.Families.cs`
  (`GraphicEqBands`, `ParametricSections`, `DynamicsSections`,
  `ExpressionPosition` / `ExpressionChoices` / `ExpressionKnobs`,
  `VocalMainKnobs` / `VocalArticulation` / `VocalPattern` / `VocalOctave`,
  `LyricParams`), each looked up BY ID against the live node. **What a layout
  does not name lands in `MoreKnobs` / `MoreChoices`**, which every one of the
  five views draws in a trailing "MORE" grid — so a module that gains a param in
  the native catalogue tomorrow stays fully editable with nothing here to update.
  `UI/RigFamilyEditorTests` asserts the set equation (projections ==
  `KnobParams` ∪ `ModelSelectors` ∪ the lyric strings, no duplicates, same
  instances) for all ten modules, and that each view really binds its own
  projections. `Perf/RigFamilyEditorsUiPerfSmokeTests` opens each one through the
  real host and measures it.
- **The treadle plate is decoration; the fader is the control**, and both are the
  one `position` param. Two controls over two copies of a value is the shape that
  makes a pedal disagree with itself.
- **The vocal editor renders EVERY `IsLyrics` string**, not `LyricsParam` alone:
  the stomp carries three phrase slots, and a one-field editor drops two of them
  and then overwrites them on Save.
- **Section and eyebrow captions are resx keys** (`Rig_Editor_*`), resolved by
  `RigDockUnit.SectionLabel` / `RigParamSection.Title` — a rack never prints text
  the markup typed, which is what `AmpFxRigScreenTests
  .NoValueOnTheScreen_IsATypedLiteral` reads the family views for.
- **The five accents are brushes in `Styles/Fire/FireRigEditors.axaml`**
  (`FireRigAccentEq` / `Wah` / `Whammy` / `Vocal` / `Dynamics`), never a hex in a
  view; the expression editor picks between two of them by KEY through
  `FireTokenBrushConverter`, the same way a unit's category colour travels.
- `Controls/Fire/FireEqContour` draws the graphic EQ's gain curve from the SAME
  param instances the faders write and subscribes to each, so the curve follows
  the hardware. A converter over the list cannot: the list never changes, only
  the values in it do.

### Live controls added with the compact editor

- **Cabinet A/B:** `RigDockUnit.CabinetSlots` retains each real `irPath`/`irPathB`
  parameter, gain, polarity and alignment. A without a file uses the selected
  built-in model. Missing B means full A at every blend setting; B audio controls
  and blend are disabled until B has a file, while its picker stays enabled.
  Clearing B restores that full-A behavior. The two slots are not simulated mics.
- **Delay:** native `sync`, `bpm`, `division` and `trails` join the existing delay
  parameters. Sync disables the manual `timeMs` editor with an explanation;
  turning sync off restores that stored manual value. Trails retains wet decay
  on module bypass. Both opt-ins default off.
- **Lane mixer:** `RigLaneMixerViewModel` binds the explicit merger's
  `levelN`, `panN`, `muteN` and `soloN`; the board's `FireMixerNode` is its face. Mute/solo are persisted native switches,
  not edits to effect bypass or remembered level values. An implicit merge has
  no adjustable mixer; missing parameter capability is exposed as unavailable.
- **Assignments and MIDI:** `RigMacroBankViewModel` exposes four macros over
  persisted module-local parameter assignments, including reversed ranges and
  channel-specific CC mappings. `RigControlAssignmentEditorViewModel` edits and
  learns those assignments. `RigMidiInputViewModel` connects the selected hardware
  input asynchronously, coalesces CC delivery, and drops obsolete device generations
  and pre-learning queued messages. These are working parameter controls, not
  decorative macro knobs; the services guide describes persistence and delivery.
- **Tempo bank:** lazy `PedalboardViewModel.Tempo` projects BPM from live delay
  targets on the edited seat. No targets means no invented tempo; differing
  targets expose mixed values. Tap uses monotonic intervals, rejects bounce below
  150 ms and restarts after more than three seconds. BPM writes leave each delay's
  sync choice unchanged, including when all delays currently use manual time.

Atomic module replacement/duplication and a calibrated DSP response plot remain
unimplemented. Adding another instance from the gear rail already works; that is
not a clone of a configured module. Historical mockup-gap lists are not a
capability catalog: inspect current native metadata and bindings before describing a control as missing.

### Render the mockup and put the two side by side — the markup will not tell you

Both faults §6.8 shipped with were invisible in the source and obvious in a
capture: the crushed segment captions (3 above) and knob readouts clipped off
the bottom of a too-short dial cell. Neither fails a bounds assertion, because
the CONTAINERS measure fine. The loop that found them, with no app launch:

- **For the EDITORS, use the layout harness first (2026-09-08):**
  `Perf/RigEditorCaptureUiPerfSmokeTests` renders one family editor per case
  over the fake chain — no engine, no board, no header — inside `RigDockBody`'s
  frame at the owner's dock width (1700×720; two consts at the top) and, with
  `DOTCHARTER_UI_SWEEP_DUMP=1` + `DOTCHARTER_RIG_CAPTURE_DIR=<dir>`, writes
  `editor-<module>.png`. Twelve seconds a round, and it is what found the
  clipped "Hz", the overflowing rack buttons and the violet fader stripe on
  the green EQ (it asserts the stripe is the Slider's accent). Crop a PNG with
  PowerShell `System.Drawing` and Read it; the whole-screen compare below is
  for the board, and its 1586 px viewport is the NARROW case.
- `pwsh tools/design/Capture-Mockup.ps1 -Name 'Amp'` renders the mockup.
- For the APP side, a throwaway `*UiPerfSmokeTests` case builds the view in the
  headless session and calls `window.CaptureRenderedFrame()` — which returns
  **null** until `HeadlessAppHelper.BuildAvaloniaApp`'s
  `UseHeadlessDrawing` is flipped to `false` for the run. Flip it back.


### Selected-module meters

`RigNodeMetersViewModel` leases `IDspMeterSource` while at least one rig host is
visible. The existing 50 ms screen tick reads the selected node's input/output
peaks; changing seats releases the previous source. Zero is silence, values over
unity remain positive dBFS, and missing telemetry shows an unavailable hint.
These are module peaks, not the mixed song/master output. `RigNodeMetersTests`
covers visible-host ownership, seat changes, rebuild availability and stale values;
`RigHardwareUiPerfSmokeTests` constructs and lays out the editor meter view.
