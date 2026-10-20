# `Controls/Dsp/` — the tone-rig widgets and the pedal FACES

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).

> **⚠ THE DRAWN HALF IS HISTORY — verified against the tree 2026-08-08.**
> `Controls/Dsp/` is now **13 files and every one of them is a PURE helper**:
> `DspControlText`, `DspModelGrouping`, `DspModuleVisuals`, `GearCuration`,
> `GearRailFilter`, `GearThumbnailMath`, `KnobMath`, `NamCaptureNaming`,
> `PedalDragMath`, `PresetBrowserFilter`, `RigTunerMath`, `SplitRegionLayout`,
> `ToneCompareSlots`. **There is not a single custom-drawn control left in the
> folder.** These, all described below in the present tense, are DELETED:
> `RotaryKnob`, `PedalEnclosure`, `CableLayer`/`CableMath`, `RigNoise`,
> `RigTextures`, `RigTunerStrobe`, `TreadleControl`, `GearThumbnail`,
> `SignalChainCanvas`/`ChainPainter`, `SplitBoardPanel`, `SignalFlowLayout`,
> `SignalFlowTag` — and with them the tests this file names as their guards
> (`RigNoiseTests`, `RigTextureBakeTests`, `SignalFlowLayoutTests`,
> `PedalboardMaterialTokenTests`). `KnobStyle` survives as the enum
> `DspModuleVisuals.KnobStyleFor` returns; since 2026-09-27 its NAME picks the
> knob-cap sprite a faceplate wears (see below) — no drawing code reads it.
>
> **Still true:** every pure-helper bullet, and `PedalboardVisualsTests` really
> does gate all catalogue ids for a family + a pairwise-unique accent — that one
> is in the default gate and is not vacuous.
>
> The live faces are `RigEditorFaces` (folded per unit in `RigDockUnit`) inside
> `Views/PlayMode/RigModuleEditorView.axaml`, guarded by `UI/AmpFxRigEditorTests` and
> `UI/PedalboardSkinReachabilityTests`. See
> [../fire/amp-fx-rig-screen.md](../fire/amp-fx-rig-screen.md).

## Generated faceplates: the knob in the picture is the knob you turn

Every native module has a photoreal FACEPLATE (`Tone.UI/Assets/Rig/Faces/*.webp`,
transparent, cropped to the unit) drawn with a BARE NUT, its label and its printed
scale where each control goes — and no knob caps. The app puts the live control on
each nut, so the preview, the board and the editor show the same knobs.

- **Data:** `Assets/Rig/faceplates.json` → `Controls/Dsp/RigFaceplates` (`RigFaceplate`,
  `RigFaceControl`: param, kind, centre x/y and nut diameter as FRACTIONS of the image,
  plus the param's range and default). Written by `tools/rig-art/import-faceplates.py`
  from the generated art — never by hand. The script cuts each unit out of its
  background, finds the nuts (a dark-hole template, a solid-bolt template and silver
  blobs, each gated on "is it chrome" and on the control's expected SIZE — without the
  size gate the letters of the label beside a knob won), fits the spec layout onto them
  and assigns by minimum total distance. Controls it cannot read (model windows, most
  toggles, a face with a vignette background) are measured by hand in
  `tools/rig-art/faceplate-overrides.json`. **Its review sheets are the check** — a
  wrong match is a knob that turns the wrong param and every earlier "ok" run had some.
- **Stored sharp (2026-09-27):** faces are kept at the generated art's own
  resolution (`FACE_MAX_WIDTH`: up to 1024 px for a stompbox, 1600 for a head or rack)
  and both `FaceSurface` and `FireKnob` draw with `BitmapInterpolationMode.HighQuality`.
  The stage draws the selected gear BIG, on high-DPI screens too (up to
  `FireRigFaceMaxScale` 2.0: a face fills a 4K/200 % dock at ~1800 physical px, more
  than it stores); at the old 560 / 900 px the printed scales were upscaled and blurred.
  **Never store a face smaller than `faceplates.json` records** —
  `UI/RigArtEncodingTests` checks every face's pixel size against it.
- **Shipped LOSSY (#407, 2026-10-07):** the importer writes a LOSSLESS master and
  `tools/rig-art/compress-faces.py` re-encodes every lossless face in place with the
  pinned cwebp (libwebp 1.6.0): quality 90, `-m 6`, **`-sharp_yuv`**, alpha lossless,
  no metadata, full resolution. Run it after every import; an already-lossy face is
  skipped. Lossy WebP is 4:2:0 at every quality, and the plain RGB→YUV conversion
  smeared coloured print (the NAM amp's red scales, 32 dB, the reason the faces were
  lossless from 2026-09-27); sharp YUV keeps those edges (worst chroma PSNR 29.8 →
  32.6 dB over the 100 faces). 146 MB → 29 MB; `--measure` reports per-face
  PSNR/SSIM against the masters. `UI/RigArtEncodingTests` fails on a VP8L (lossless)
  WebP under `Assets/Rig/`, a face over 768 KB or a folder over 36 MB.
- **Selectors point at their printed labels:** the art puts a selector's labels on
  four sides or round a whole circle, so the even −135..+135° sweep pointed between
  them (the miku octave, owner). `tools/rig-art/selector-angles.json` holds one angle
  per value (measured by eye; `preview-faces.py` renders every face as the app draws it
  to check), the importer copies it into each control's `angles`, and `FireKnob.StepAngles`
  / the preview pass turn the cap to `KnobMath.AngleForStep`. Every face control's
  tooltip ("Gain: 6.2") opens at once (`RigFaceplateView.InstantTip`), since the printed
  scales are small. Where detection put a control off its printed nut (a cap left of
  its nut, a toggle on its lever, a footswitch area off the switch — 102 of them on 45
  faces, found by rendering every face at full size), `tools/rig-art/face-corrections.json`
  corrects it in FACE coordinates after detection. Pinned by `RigFaceplateCatalogTests.EverySelector_PointsAtItsPrintedLabels`
  and `RigFaceplateUiPerfSmokeTests.ASelector_PointsAtThePrintedLabelForEachValue`.
  Pinned by `RigFaceplateCatalogTests.EveryFace_IsStoredSharpEnoughForTheStage` (all
  100 old faces fail it) and the filter check in `RigFaceplateUiPerfSmokeTests`.
- **Caps:** `Assets/Rig/Caps/` — one sprite per knob style (`knob-<style>.webp`, pointer
  at 12 o'clock) plus the shared toggle / selector / LED / footswitch sprites.
  `RigFaceplate.KnobCapDiameter` draws every knob on one face at ONE size, 1.6× the
  face's typical nut.
- **Controls:** `Controls/Fire/RigFaceplateView` (a Panel: the picture on a private
  `FaceSurface` child, the controls on their nuts). Interactive, each knob is a
  `FireKnob` in cap-sprite mode (`CapFile`: the sprite turns with the value; every
  gesture is the same knob), a toggle is `RigFaceToggle`, a model window a
  `Button.FireQuiet` with the labels in a flyout. Not interactive (`IsInteractive=False`)
  it has NO children and draws every cap turned to its live value in one pass — the
  board tile and the gear library (`ModuleTypeId` alone: each knob at its default).
  A module with no face draws its old illustration.
- **The AmpliTube 5 layout (2026-09-27, design C).** `AmpFxRigView`'s body
  (`RigFlatBody`) is three rows, top to bottom:
  - **The STAGE** (`RigWorkspace` → `RigDock` → `RigDockBody`), the biggest
    area: the selected gear BIG — `RigModuleEditorView` opens the unit's family
    view, and for every unit with a faceplate that is `RigPedalEditorView`'s
    `RigFaceplateFit` → interactive `RigFaceplateView` (live knobs; a floor unit's
    printed FOOTSWITCH is its bypass — the importer finds it, `find_footswitch`,
    override key `_footswitch`, into `RigFaceplate.Footswitch`; the view puts a
    transparent `ToggleButton.FireFaceFootswitch` there, which must STRETCH or the
    theme shrinks it to its 4 px border). `RigFaceplateFit.MinScale` is 0.2 (the
    labels are in the picture, so a short stage shrinks the face rather than
    cutting it off), and its `RigEditorScroll` is `Auto` + no auto-hide. The
    cabinet and routing consoles stay their own family views on the stage; the
    family views remain the fallback for a module with no face
    (`RigDockUnit.EditorFamilyFor`). The stage row's height is ABSOLUTE
    (`AmpFxRigView.SyncWorkspaceRow`): the dragged divider
    (`RigWorkspaceViewModel.Height`, ceiling 1600) or, until then,
    `AmpFxRigView.StageShare` (62 %) of the body — content-independent, so a
    selection never moves the map.
  - **The GEAR BAR** (`RigDockBar`, `Border.FireDockBar.FireGearBar`) under the
    stage: POWER (`RigDockEnabled`, a `ToggleButton.FirePowerButton` on
    `RigDockUnit.IsUnitEnabled`), ▲ / ▼ (`RigDockPrev` / `RigDockNext`, the chain in
    signal order), the lane breadcrumb and the name (`RigGearBarName`), then the
    branch choice inside a split (`RigDockBranch`), the ··· actions (duplicate,
    replace, undo, remove), HIDE (`RigDockToggle`) and **DETAILS**
    (`RigDetailsToggle` → `AmpFxRigViewModel.IsDetailsOpen`, pushed onto
    `RigDockUnit.ShowsDetails`). DETAILS opens `RigModuleEditorView`'s right column
    (`RigEditorRack`, scroller `RigEditorRackScroll`) BESIDE the gear, inside the
    stage: `RigDetailsHead`, then for the pedal family `RigPedalDetailsView` →
    `RigPedalAside` (the
    capture card + NAM library search / list `RigCaptureList`, every exact value
    `RigUnitValues`, lyrics `RigDockLyrics`, the meters, the footnote), then the
    unit status panel (`RigUnitPanel`) and `RigEditorControllers`. It is a
    standing choice (stepping units keeps it open); a unit whose FACEPLATE does
    not carry every param (`RigDockUnit.FaceMissesParams`, pedal family only) opens
    it on selection. `micpos` no longer does: it has its own editor (below). An editor hosted on its own keeps `ShowsDetails`' default (true).
  - **The ROUTING MAP** (`RigSignalStrip` wraps it): the routing RAIL
    (`RigRoutingRail`: `ToggleButton.FireMapMode` Series / Dual / Triple as
    `IconRigSeries|Dual|Triple` glyphs with their names as tooltips, and Add at
    its foot) left of `RigBoard` — the whole chain small: every unit a
    non-interactive `RigFaceplateView` over a one-line name in a `FireUnitCard`
    (the selected one's `FireUnitArtFrame` in amber), the main path in the serial
    lane token, lanes in theirs around the split region (`RigFan` elbows), IN at
    the left, `RigMapSpeaker` after OUT. Its caption row (`RigBoardCaption`,
    fixed 34 px) carries the routing summary, the insert and undo chips
    (`RigInsertChip` / `RigUndoChip`, `.FireMapChip` — never over the chain), the
    hint, the zoom cluster and Fit. Drag, insert dots and lane mixers are
    unchanged. The face height is `AmpFxRigViewModel.BoardFaceHeight`
    (`RigBoardFaceSize.For`, Min 56 / Max 120, shared between a split's lanes;
    width limit via `RigBoardFaceSize.NextWidthLimit`), and
    `AmpFxRigView.BoardFitScale` fits the chain to the map's WIDTH (floor
    `MinZoom`) and its HEIGHT (floor `MapHeightFitFloor` 0.35). **The centring is
    `RigBoardZoomHost`'s, not `RigBoardPath`'s** — a `LayoutTransformControl`
    scales its child's content but not an aligned child's offset, and a centred
    path under a 0.5 fit drew half a map to the right.
  - **The GEAR BROWSER** (`RigGearPanel`, right column over body and footer):
    Browse / Favourites (`RigGearTabs`), the category ICON tabs (`RigGearFilters`,
    `ToggleButton.FireGearCatTab`, glyph `RigCategoryFilter.IconKey` →
    `IconRigCat*` through `Converters/FireResourceConverter`, label as tooltip),
    the search, then a two-column GRID: `RigGearGroups` is a virtualized
    `ListBox` over `AmpFxRigViewModel.GearTilePairs` (`GearRows` two to a row),
    each tile `RigGearTileTemplate` (its own dictionary,
    `DotCharter.Tone.UI/Views/PlayMode/RigGearTileTemplate.axaml`, merged by the
    view) — a `FireGearTileButton`-themed `FireGearTile` button with the
    faceplate preview (`RigFaceplateView ModuleTypeId=…`), IN RIG, the star and
    the + (`FireGearTileAdd`, to the armed gap or the add-to lane); a tile is also
    the drag source onto the map (`RigBoardDragController` finds its
    `RigGearItem`). Add-to lane and Add to rig stay at the foot.
- **Measure trap:** the view reports its NATURAL size (capped at
  `RigFaceplateView.NaturalHeightCap`) and never grows past it — `RigScaleToFit` measures
  twice, the second time with a scaled-up width, and a face that filled that came out
  three times the dock.
- **Guards:** `UI/RigFaceplateCatalogTests` (default gate: every face and cap ships,
  controls on the face, no two on one nut, every stompbox's footswitch clear of its
  knobs), `UI/RigBoardFaceSizeTests` (the height / width arithmetic and the family
  rule), `Perf/RigBoardFaceplateUiPerfSmokeTests` (small preview gear on the chain, a
  click opens it big with grabbable knobs, a stomp bypasses, split lanes share the
  height, the NAM details with a seeded capture library),
  `Perf/RigAmpliTubeLayoutUiPerfSmokeTests` (stage over gear bar over map with the
  rail and browser in place and in proportion, power and ▼ work, DETAILS opens
  beside the gear without moving the map, the icon tabs and the two-column tile
  grid, a Dual / Triple split fits and is CENTRED on the map at 1920×1080 and
  1280×800, the chips in the caption row, `micpos` opening its placement with the
  details shut, the cab console on the stage; PNGs with the capture env vars) and `Perf/RigFaceplateUiPerfSmokeTests`
  (the editor places every control on its nut and turning one moves the param; every
  face names only real params; with `DOTCHARTER_UI_SWEEP_DUMP=1` +
  `DOTCHARTER_RIG_CAPTURE_DIR` it writes PNGs).

## Mic placement, bypass dimming, undo and the unit menu

- **Mic placement** is the `micpos` unit's editor family (`RigEditorFamily.MicPlacement`
  → `RigEditors/RigMicEditorView` → `Views/PlayMode/RigMicPlacementView`, view model
  `RigMicPlacementViewModel`, built by `RigDockUnit.Mic.cs`). The speaker front
  (`Controls/Fire/RigMicStage`: drag to place, wheel = distance, Shift fine, arrows /
  PgUp / PgDn) sets `position` (cap → edge) and a bearing; the side view
  (`RigMicSideView`) is the distance ruler (`distanceCm` 0–30); one mic card per
  mic the node's `mic` range offers (`ToggleButton.FireMicCard`, five since the
  tube condenser of #75, one row) picks `mic`; angle (`angleDeg`, the capsule
  turned off axis — NOT the drawn `BearingDegrees`) / room / second mic / level
  are knobs. The
  pure geometry is `Controls/Dsp/MicPlacementMath`. The CABINET editor shows the
  mic that follows it (`RigDockUnit.CabMic`, found by `MicAfterCab`, stopping at the
  next cab) on its speaker, or offers `AddCabMicCommand` (inserts `micpos` straight
  after the cab). The old `RigMicPad` / `RigCabinetMicViewModel` are gone;
  `CabinetFilterMath` keeps only the filter curve. Guards: `UI/RigMicPlacementTests`,
  `Perf/RigCabinetEditorUiPerfSmokeTests`.
- **A bypassed unit DIMS, its knobs never vanish.** `FireKnob` with a cap sprite
  stays at opacity 1 and draws an opaque cap plus a shade ellipse; only the
  capless knob fades. A translucent cap let the chrome nut shine through and read
  as a missing knob (owner). `RigFaceplateView` dims the face bitmap only.
  Guard: the luminance check in `Perf/RigBoardFaceplateUiPerfSmokeTests`.
- **Undo / redo** (`PedalboardViewModel.History.cs`, pure core `RigHistory<T>`) keeps
  whole-rig snapshots (`BuildCurrentRig`), coalesces a burst (< 600 ms, a knob drag)
  into one step, and restores through the same verbatim `ApplyToPlayer` the A/B
  compare uses. Every card's params, string params and bypass feed it
  (`TrackEdits`); a board rebuild notes an edit; `ResyncAll` and the constructor
  reset it. **Two rules the tests pin:** a card's native write lands BEFORE its
  `Value` notification (the history reads the chain), and a note that changed
  nothing opens no burst (else the real edit inside its window is lost).
- **Clipboard + shortcuts:** Copy / Cut / Paste (after the selection, on its lane),
  DuplicateSelected, ToggleSelectedBypass, DeleteSelected. The gestures live once in
  `Views/PlayMode/RigUnitContextMenu` (Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z, Ctrl+C/X/V,
  Ctrl+D, Del, Ctrl+B); `AmpFxRigView.SyncKeyBindings` binds them and a click
  anywhere in the rig focuses the view. A right-click on a map card opens
  `RigUnitContextMenu.Build` (`AmpFxRigView.UnitMenu`, one at a time) with the same
  commands and their gestures printed. Guards: `UI/RigHistoryTests`,
  `Perf/RigShortcutsUiPerfSmokeTests`.

## `Controls/Dsp/` — the VST-style tone-rig widgets (a sub-bullet of `Controls/` in the Layout map)

  - `Controls/Dsp/` — the VST-style tone-rig widgets driving the Pedalboard overlay: `RotaryKnob` (reusable custom-drawn dial in NINE `KnobStyle` hardware families — one per module family so the board never reads as recolours of one template: `Aluminium` machined default (routing/utility), `Brass` knurled brass pointer knob with an etched 0-10 shoulder scale for amp HEADS (the dial that belongs through an anodised panel; takes NO accent), `ChickenHead` blade-on-bakelite for cabs, `Skirted` black 0–10 numbered ring for drives, `Cream` vintage ivory for modulation, `KnurledRubber` matte-black ribbed dome for dynamics, `PointerBar` dark puck with a bright accent spoke for time/ambient, `ChipPointer` two-tone knob with a bold accent chip for pitch, `TopHat` fluted metal top-hat for eq/filter — all sharing the glowing accent value-arc, the broad diagonal `RimSweep` lacquer + crisp `DrawSpecularGleam` hotspot (both in the SHARED path — they are the light, not the hardware), small-caps caption + value readout at the `FontSizeXs`/`FontSizeSm` tokens whose colours follow the PANEL (`CaptionColor`/`ReadoutColor`/`ReadoutPlateColor` — dark etched ink on the heads' brass, the cool default everywhere else), and a 0–10 display option for normalized 0..1 params (`TenScaleDisplay`, display-only); vertical-drag (**Shift = 10× finer**, `FineDragMultiplier`)/wheel to adjust, **double-click OR F2/Enter opens a precise inline numeric editor** (a child `TextBox` over the readout — select-all, Enter commits clamped, Esc/lost-focus cancels; entries are in DISPLAY units via the pure `KnobMath.TryParseEntry`/`ValueFromDisplay`), **Ctrl+double-click and the right-click context menu reset to Default** (`Pedalboard_ResetKnob`); binds a DSP param's Value/Min/Max/Default; rotation geometry + the display + entry mapping in the pure `KnobMath`), `DspModuleVisuals` (pure module-id→`DspModuleCategory`+neon accent/body colour + `KnobStyleFor` + `SkinFor` + amp-vs-stompbox split; ALL 39 catalogue ids carry a deliberate family + their OWN accent — `PedalboardVisualsTests` gates coverage against `DcRigTargetCatalog.ModuleTypeIds` and pairwise accent uniqueness, so a new native module without a mapping fails the suite; unknown ids fall back to `Utility`; also the pure amp/cab face-badge param map `BadgeParamId` (amp `voicing` / cab `model`) — the badge TEXT now comes from the ABI-v24 `ValueLabels` (see the model-picker bullet below), not the legacy `BadgeResourceKey` 0–6 map (kept + still tested, but no longer consumed by the badge), `SplitRegionLayout` (pure ABI-v23 split/parallel BOARD layout mirroring the native routing contract EXACTLY — positional region = first non-bypassed `splitter` → next non-bypassed `merger`, branch→row `clamp(max(tag,1),1..N)`, bypassed-splitter collapse, missing-merger auto-merge flag, no-nesting, + `RowForY` drop-to-row picker; `SplitRegionLayoutTests`), `PedalDragMath` (pure: `ComputeDropIndex` nearest-slot reorder + `ComputeInsertIndex` N+1-gap rail-drop insertion), `CableLayer` + `CableMath` (custom-drawn patch cables UNDER the pedals — sagging cubic Beziers IN→pedal→…→OUT with plug ferrules, segments leaving a bypassed pedal dimmed; the view pushes jack positions via `Update`, which compare-gates so it only invalidates on a real layout/drag/chain/bypass change; the view's VM/node subscriptions unhook in `OnDetachedFromVisualTree`), `PresetBrowserFilter` (pure category/search slice of the tone catalogue → `PresetFilterKind` All/Category/User, PLUS `Matches(name, categoryLabel, query)` — the pure item filter behind the searchable preset dropdown), `GearRailFilter` + `GearThumbnailMath` (pure filter + miniature silhouette pick/`KindFor` — promotes amp-FAMILY modules without a specialized board skin, i.e. `namamp`, onto the wide chassis — + face layout behind the gear rail), `SignalFlowLayout` (pure chain-view geometry + hit tests: `Compute`/`ComputeChain`/`HitTestChain` — the legacy compact-strip layouts, still used for the tuner-glyph zone — PLUS the **chain-BUILDER canvas** math `ComputeBuilder` (gear-card boxes, rounded-elbow cable polylines incl. a split region's fan-out/rejoin and EMPTY-branch passthroughs, numbered branch markers, narrow junction boxes), `HitTestBuilder`, `HitTestBuilderTuner` and `ComputeBuilderDrop` (drag → flat chain slot + branch row); Avalonia-free throughout — cable points are `ChainPoint`, never `Point`), `SignalFlowTag` (pure module-id → 2–3 letter chip code, AMP/CAB/CMP/… incl. `SPL`/`MRG` junctions, curated map + first-3-letters fallback), `GearCuration` (the ONE curated quality-ordering seam — `RankOf(moduleTypeId)`, amps/cabs → drives → time/mod → utility, unmapped last; consumed by `GearRailViewModel` sorting AND reserved for the parallel preset-curation seam), `RigTunerMath` (pure nearest-note/cents/needle/strobe-wheel display math). All the pure helpers are unit-tested (`PedalboardVisualsTests` incl. `KnobMath.TryParseEntry`/`ValueFromDisplay`, `PresetBrowserFilterTests`, `GearRailTests`, `GearCurationTests`, `SignalFlowLayoutTests` incl. `SignalFlowTag`, `RigTunerMathTests`; no Avalonia instance). **Baked hardware textures (Art v3):** `RigNoise` (pure, tileable value-noise/fBm + domain warp — per-axis integer frequencies so every texture tiles seamlessly; `RigNoiseTests` pins determinism/range/tiling) feeds `RigTextures`, which BAKES the procedural materials ONCE per app lifetime and registers them as tiled `ImageBrush`es in `Application.Resources` (`EnsureRegistered`, idempotent, called from `PedalboardView`'s ctor; a no-op headless). **That bake is NEVER on the UI thread**: 13 tiles x 65,536 pixels is ~25 M `RigNoise` evaluations, and `PedalboardView` is constructed inside the startup prewarm drain, which budgets one `Background` dispatcher turn per surface for a LAYOUT pass. So `BakeAll()` returns PLAIN BGRA BYTES (no Avalonia type -> legal on a worker) under `Task.Run`, and only the cheap `WriteableBitmap` wrap + `Resources[key] =` assignment is posted back via `Dispatcher.UIThread.InvokeAsync(..., Background)`; a `Task` field makes a second caller await the first bake instead of re-baking. The XAML binds `{DynamicResource}` and `PedalEnclosure.CoatBrush` retries a missing key per render, so the late assignment costs nothing. Regression: `RigTextureBakeTests` under `Rig*Brush` keys: warm wood board, black/brown/cream tolex, brushed aluminium/steel, grille cloth (2 colourways), cream plastic. **The board floor is the QUIET dark rubber mat `RigBoardTexture`** (owner call: the wood floor was tried and reverted — the board is understated, the GEAR is the star; the `RigWoodBrush` bake stays registered but nothing consumes it, and swapping that ONE token at the floor Border is the whole plank-floor switch — see pedalboard-visual-language.md §8). **The ROOM is lit** (§2.5 of the visual language): under the rails and pedals sit a warm overhead `PedalRoomKeyLight`, the `PedalRoomFloorRamp` turning the floor away from it and the deep `PedalRoomInsetShadow` standing in for walls — the floor obeys the same lamp the gear does, which is what stops a correctly-shaded board reading as gear on a swatch. The board is built into a **console**: the header rides a raised `PedalConsoleLip` and the chain builder is routed into a `PedalConsoleWell` (the ONE well material on the board — the graphic-EQ fader bank uses it too, so two slots at the same depth cannot disagree), with the gear-rail drawer head on the same lip. Console furniture is panel-grey on the same licence the junction card has. Plus a radial vignette + brushed-steel rails with end screws; the brushed-aluminium bake is now the stompbox's orange-peel grain (drawn INSIDE `PedalEnclosure`, ~0.11 opacity); amp/cab bodies wear the tolex texture. NEVER baked per frame; theme-agnostic (labels keep their own contrast). **The die-cast enclosure (`PedalEnclosure`, gear-realism pass)** is the custom-drawn art behind EVERY stompbox-family skin (stompbox / graphiceq / wah): the cast side wall + relic rim, the painted top face (base gradient → per-family coat grain → specular sweep → radial edge wear → the casting's INNER WALLS: light spilling in off the left wall, shadow off the right, a hard lit top lip and a soft occlusion out of the foot — what makes the face a cavity rather than a printed plate), the roll-over bevel, corner hardware and — at width ≥116 px — the two 1/4" jack sockets with cable stubs on the TOP edge (hex dress nut + bored socket), all derived from `BodyColor`/`Accent`. **Its `Category` styled property picks a per-family `ChassisFinish` (`DspModuleVisuals.ChassisFinishFor`) — the whole family-identity recipe (`RecipeFor`, pure switch) in one place:** which baked coat texture + at what opacity (drive `RigMetalFlakeBrush` sparkle, dynamics `RigHammertoneBrush` wrinkle, time/eq `RigAnodizedBrush` vertical brush, pitch `RigCarbonBrush` twill, modulation gloss-only enamel, filter/utility the default `RigBrushedAluminiumBrush`), how glossy the specular sweep reads, the corner fastener (Phillips / hex bolt / dome rivet / none), and an edge accent (extruded side rails for anodized, gloss band for sparkle/enamel, embossed control-plate frame for hammer, corner chamfer lines for carbon). Coats are cached per resource key; the finish decorations draw only on a size/property change, never per frame. The finish→knob PAIR is unique per enclosure family (`PedalboardVisualsTests.Stompbox_families_have_distinct_finish_plus_knob_pairs`). It is `IsHitTestVisible=false` (the board drag + every knob/switch above it keep working); the whole brush set is cached process-wide per body colour and the geometry is rebuilt ONLY on a size change — never per frame. Custom-drawn siblings: `GearThumbnail` (a pure-drawn MINIATURE of a module's real skin family drawn with its `KnobStyle` hardware, for the gear-rail cards + the drag ghost — brushes rebuilt only on module-id change, never per frame), `SignalChainCanvas` (the AmpliTube-style CHAIN BUILDER at the board's foot — see the chain-builder bullet under the pedalboard section: a `Panel` whose first child is a hit-test-invisible `ChainPainter` drawing thick amber cables / card plates / junction glyphs / IN-jack + OUT-speaker glyphs UNDER real `GearThumbnail` children, laid out by `SignalFlowLayout.ComputeBuilder`; click = select + scroll, drag = reorder/re-branch (`NodeDropped` → the VM's `DropToRow`/`MoveTo`), double-click = bypass; talks to VMs through its `ISignalFlowNode` seam — `ModuleTypeId`, `ParamCount` (the thumbnail's knob count), `Branch`/`EnabledBranches`), `SplitBoardPanel` (the board's items panel: lays the flat `Nodes` containers out in the ABI-v23 split geometry via `SplitRegionLayout` — linear pedals in one bottom row, a region fanned into N stacked branch rows between the junctions; exposes `RegionBounds`/`RowForPoint` so the drag is region-aware; keeps every pedal binding + zoom/scroll + reorder drag intact), `RigTunerStrobe` (property-driven strobe band, no own timer). The pedals themselves are metadata-driven XAML skins in `PedalboardView`, picked by `DspModuleVisuals.SkinFor` → `PedalSkin`: the generic stompbox (the fallback that covers ANY future module): a **portrait die-cast box** — the enclosure ART is the custom-drawn `PedalEnclosure` (see below) and the XAML layers the interactive hardware over it: the screen-printed brand + model name, a recessed `Border.ControlPlate` holding up to THREE big dials across the face (`WrapPanel`, `PedalNodeViewModel.KnobRowWidth` = `clamp(knobs,1,3) × 72 + 2`, so a 1-knob pedal stays narrow and tall and a 4+-knob pedal wraps to a second row), then the LED jewel and the chrome footswitch (`Border.SwitchCollar` dress-nut ring under `ToggleButton.Footswitch`) on the centreline. `Border.Stompbox` is MinWidth 136 / MinHeight 248 (real pedal faces are ~1:1.8 portrait) and carries only the drop shadow / selection ring / hover+drag transforms — its Background is Transparent, the amp-head face (**cloth front over an anodised brass panel** — `Border.AmpGrille`, an orthogonal warp/weft `PedalGrilleBasketweave` on a cream-beaded frame carrying the house brand silkscreened big, over `Border.AmpPanel`, a five-stop `PedalBrassPlateBrush` under the vertical `PedalPanelBrushing` grain and the diagonal sheen wash — holding the model plate, live model/voicing badge (`BadgeText`, follows the knob), power lamp with a soft warm halo AND the dial row bolted straight through it (no recess: a head's pots go through the panel, and the dials carry `KnobStyle.Brass` with the dark etched `PedalBrassInk` legend), plus a recessed **jack-plate strip** beside the bypass; both heads), the cab's speaker-grille face (the SAME brass name strip the heads wear — with `PedalStudRow` fixing studs — over the SAME `PedalGrilleBasketweave` cloth on a cream-beaded frame; a head and a cab ship together, so they share both. It stays a BAFFLE not a front: driver ellipses behind the weave, the cloth sagging into the opening, plus a compact knob row + the same badge + the same jack-plate strip) — both amp + cab wrap in a programmatic 6-stop tolex gradient (`BuildTolexBrush`) with a radial edge vignette, UNDER a baked tolex-texture overlay (Art v3, see the `RigTextures` bullet below — black for amps, brown for cabs) + cream piping + metal corner protectors + a top carry-handle hint (moulded strap on chrome end-cups, drawn under the faceplate) on the amp head and recessed side-handle cups on the cab for material depth; the amp/cab model NAME is screen-printed above a house-brand wordmark (`PedalNodeViewModel.BrandWordmark`, a non-localized logotype) in a per-family type treatment (`DspModuleVisuals.TypefaceFor`/`LetterSpacingFor` → `NameFontStyle`/`NameLetterSpacing`: block-industrial tracking for amps/drives, rounded for mod/time, italic "script" for cleans); the graphiceq fader bank (every band a vertical `Slider.EqBand` from `DspParamInfo`) and the wah's `TreadleControl` rocker (drives its "position" param; other params stay knobs). Specialized skins are metadata-guarded in `PedalNodeViewModel` (wah without a "position" param or an empty graphiceq degrade to the stompbox). Chrome tokens `RigScrewShadow`/`RigRecess`/`RigBevelHighlight`/`AmpFacePlate`/`AmpPowerLamp` (rig-accent group, fixed brand colours). Knobs/footswitch mark their pointer events handled so a press on them never starts a board drag.
    - **Shared hardware primitives + the `Pedal*` material palette (visual pass).**
      A physical part that appears on more than one face is declared ONCE, in
      `PedalboardView`'s own `UserControl.Resources`, and instantiated through a
      hit-test-invisible `ContentControl` (`Template=` for pure decoration,
      `ContentTemplate=` + `Content="{Binding}"` where it needs the node VM):
      `PedalLedTemplate` (stompbox / graphiceq / wah), `PedalChassisWrapTemplate`
      + `PedalChassisVignetteTemplate` + `PedalJackPlateTemplate` (all three
      covered chassis — amp head, NEURAL amp head, cab: tolex, cream piping cord,
      corner protectors, bevel, screws, bored sockets),
      `PedalCarryHandleTemplate` + `PedalPowerLampTemplate` (the two HEADS only —
      a cab is lifted by side cups and has no power lamp),
      `PedalBadgeTemplate` (amp voicing + cab model — the neural head has its own
      capture plate instead), `PedalSpeakerTemplate` (cab),
      `PedalCapturePlateTemplate` + `PedalAmpPanelKnobTemplate` (neural head).
      They are
      referenced with `{StaticResource}` from the SAME document, so there is no
      cross-file resolution to get wrong. **An unresolved `{StaticResource}` on a
      `Template`/`ContentTemplate` is as SILENT as an unresolved
      `DynamicResource`** — the build passes, the property keeps its default and
      the part simply is not drawn; verified 2026-07. **The gate that used to
      catch it went with the rack** — `PedalboardMaterialTokenTests` no longer
      exists. What covers an unresolved key today is the UI sweep
      (`Perf/UiSweep.cs`, which reports it as an ERROR) plus
      `UI/ResourceTokenTypeTests` for the token-TYPE half; neither is a
      per-view key-existence lint, so the hazard below is real and unguarded for
      any NEW rig markup. It was never the headless construction test.
      **Param surfaces must NOT be extracted**
      — `PedalboardSkinReachabilityTests` requires each face's
      `ItemsSource="{Binding KnobParams|ModelSelectors|…}"` to be a DESCENDANT of
      the element bound to that skin's `IsVisible` flag, so only DECORATION moves.
      Colours come from the **`Pedal*` block in `Styles/Resources.axaml`**: fixed
      MATERIAL constants (shade/sheen/chrome ladders, ink, tolex/piping, sockets,
      driver, emitters, a two-layer contact+ambient elevation ladder), deliberately
      NOT in `ThemeTokens` — like `Rig*`/`Medal*` they never enter a theme preset,
      a community pack or the theme-switch completeness gate. The ONE semantic
      exception is the neural-amp status pill (`PedalStatus*`, Success/Danger at
      brush Opacity). **Every gradient lives in `Styles/Resources.axaml`, never
      inline in a template** — a `DynamicResource` on a control property is proven
      here, one inside a nested `GradientStop`/`Effect` is not, and an unresolved
      `DynamicResource` fails SILENTLY (the property keeps its default), so it
      ships as an invisible brush rather than an exception. `PedalboardMaterialTokenTests`
      used to gate all of it — zero hex literals in the view, every referenced key
      defined somewhere (incl. the `RigTextures` keys baked in at runtime), and each
      shared template still used by the expected number of faces — **and it is
      deleted.** The live rig's nearest equivalent is
      `UI/AmpFxRigScreenTests.NoValueOnTheScreen_IsATypedLiteral`, which is the
      hex-literal half only. The rulebook behind the
      values is the RETIRED `pedalboard-visual-language.md` — see the note at the
      top of this file for how to read it.
    - **The rig's real runtime gate is
      `Perf/InspectorConstructionUiPerfSmokeTests.AmpFxRig_realizesEveryUnitCardAndEveryRegion`,
      not `AmpFxRigView_builds`** (the rack-era pair was
      `PedalboardBoard_realizes_every_pedal_skin` / `PedalboardView_builds`;
      both are gone). Every pedal face lives in the items
      `ItemTemplate`, which is only built once a NODE is realized — so
      constructing the view proves nothing about the faces or the shared hardware
      templates they pull in. That test builds one module per `PedalSkin` over a
      `FakeDspChainService`, shows a window, lays out for real, and asserts each
      container measures to something (a face that fails to render arranges at
      size 0, which is exactly how a silently invisible skin looks) plus that the
      head and stompbox families keep their PROPORTIONS (BOTH heads are pinned since they share one panel structure; 2.3–2.6 : 1 letterbox /
      ~1 : 1.8 portrait — a knob added to a face silently stretches it). Headless
      session ⇒ outside the default gate; run it explicitly after any face change:
      `dotnet test --filter-class "*InspectorConstructionUiPerfSmokeTests"`.
    - **The neural amp head (`PedalSkin.NeuralAmp`, `namamp`).** A HEAD and only a
      head: the `cab` module already owns the speaker baffle, and a NAM capture
      models an amp, not a speaker — so this face must never grow a grille; it
      stands on the rails BESIDE a cab. It reuses the amp head's whole chassis
      (tolex wrap, piping, corner protectors, carry strap, brushed faceplate,
      power lamp, recessed jack plate, vignette); what differs is the faceplate,
      where the **capture identifies itself**: the pure `NamCaptureNaming` resolves
      `modelPath` → model name + author + a format/size chip, preferring the local
      library's manifest sidecar (`INamCaptureLibrary`, indexed ONCE per `Sync` on
      `PedalboardViewModel` — `GetAll` is a directory scan plus a JSON read per
      capture) and falling back to the DE-SLUGGED file name. A capture picked off
      disk has no sidecar, and the AUTHOR is the one field never guessed. The
      plate IS the file picker, so param marshalling is unchanged, and it renders
      over `{Binding StringParams}` so a future string param cannot go invisible.
      `PedalNodeViewModel.BrandWordmark` says "DOTCHARTER NEURAL" here — two
      black-tolex heads on one board need something to tell them apart. Nine dials
      ride ONE row on the recessed control panel at a 68×108 cell
      (`PedalAmpPanelKnobTemplate`) rather than the stompbox's 72×108: a head's
      controls are a row across a wide machined panel, not three big dials on a
      small box. **The panel `WrapPanel`'s `MaxWidth` is load-bearing** — the board
      measures a pedal UNCONSTRAINED, so without it a future 14-param namamp would
      grow a chassis wider than the whole rack. The capture-status pill
      (`Border.NamStatus` + `.namWarn`, driven by `NamHasWarning`) lives on this
      face only; it was REMOVED from the stompbox face, whose only consumer namamp
      was. The power lamp reports power (bypass), never model state — the pill is
      the information channel, the lamp is the emitter.
