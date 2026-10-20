# Gear rail, model pickers and DSP param binding

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).

- `Controls/Dsp/` — continued from [widgets-and-faces.md](widgets-and-faces.md):
    - **String-valued params (ABI v22) on pedal cards.** `PedalNodeViewModel`
      builds `StringParams` (`PedalStringParamViewModel`) from
      `DspModuleType.StringParams`, seeded from the node's `StringValues` and
      rendered by the shared `PedalStringParamTemplate` on the stompbox + cab
      skins. A NON-empty `FileFilter` (namamp `modelPath`, cab/acousticsim
      `irPath`) → a compact **file-picker row** (button shows the file name, opens
      `IFileStorageService.OpenFilePickerAsync` filtered on the `;`-split
      wildcards; a ✕ clears via `""` = revert-to-default). **That ✕ went missing
      with the rack and came back in 0.59** — `AmpFxRigView` drew Choose and
      nothing else, so `ClearCommand` sat in
      `RelayCommandReachabilityTests.KnownUnreachable` and a plate loaded once
      could never return to the module's built-in default. It is now on BOTH
      file-param faces (`RigCardFileClear` on the board card's load strip,
      `RigDockFileClear` in the dock's capture column — the dock is a second
      view of one unit), visible only while `HasValue`, and it goes through the
      same `Commit` the picker does, so a native reject of the clear leaves the
      file loaded instead of lying. Guarded by
      `AmpFxRigEditorTests.EveryFileParamFace_CarriesAClearBesideItsPicker` and
      `RigLibraryChoiceTests.Clearing_PushesTheEmptyValue_…`. An empty filter (miku
      `phrases`) → a **Lyrics… popup**: a multiline `TextBox` + a live
      `SyllableHint` mirroring the native tokenizer (`CountSyllables`:
      whitespace/comma/hyphen/pipe/semicolon split, 64 cap) that pushes on Save.
      All live-write via `SetNodeString` (managed value advances only on native
      success). Manually-set strings survive Save→re-apply because
      `BuildCurrentRig` now carries `StringValues` into `DcRigModule.StringParams`.
    - **The "From library…" dropdown sits under the Choose row on BOTH
      file strips** (the card's file strip — its `FireLoadStrip` style was worn by
      nothing and went with #242 — and the module dock's file
      column — one `SelectedLibraryChoice`, so they cannot disagree).
      `PedalboardViewModel.ListLibraryChoices(fileFilter)` projects the local
      `INamCaptureLibrary` into `NamCaptureChoice(Title, FilePath)` rows,
      **filtered by the PARAM's `FileFilter` extensions**
      (`RigDockUnit.ExtensionsFrom`, pure): a namamp offers `.nam`/`.aidax`, a
      cab's `irPath` only the `.wav` IRs — the library holds both, and a `.nam`
      pushed into a cab is a silent native reject. Choices are materialized in
      the `PedalStringParamViewModel` ctor (cards rebuild on every `Sync`, which
      invalidates the shared `_captureIndex`, so staleness is bounded); selecting
      one goes through the SAME `LoadFile`→`Commit` path the picker uses, the
      selection mirrors back on commit (null for an off-library file), and a
      native reject reverts the selection so the dropdown never lies. The dock's
      capture COLUMN is likewise filtered per face now (`RigDockUnit.LoadCaptures`
      runs for ANY `FileParam`; it was amp-only and unfiltered before), while `HasCaptureColumn` /
      `ShowsEmptyCaptureNote` (the NAM status text + the "nothing downloaded"
      note) deliberately stay amp-only. Pinned by `RigLibraryChoiceTests` +
      `AmpFxRigScreenTests.BothFileStrips_OfferTheLibraryDropdown`.
    - **FOUR tables dress a module, and all four are filled for the 0.9 gear
      wave.** A native module id has to appear in `DcRigTargetCatalog`
      (the param mirror), `DspModuleVisuals` (category + a pairwise-distinct
      accent), `GearCuration` (the curated rank) and `RigArtwork`'s
      illustrations (its own file under `DotCharter.UI/Assets/Rig/Art/`) — each
      with its own coverage test sweeping the LIVE native catalogue. JuceSharp
      0.9 took that catalogue 57 → 100 on 2026-09-12; the submodule bump filled
      the first three for all 43 new modules and the artwork wave filled the
      fourth — 43 `Illustrations` rows, 43 `Finishes` pairs and 43 new `.webp`
      files. Do not close a future gap by pointing a new id at the fallback
      sprite: `RigArtwork.HasOwnIllustration` means what it says, and modules
      sharing the merger sprite are a board nobody can read.
      `RigArtworkTests.EveryNativeModule_HasItsOwnIllustration` is the gate. **Since 2026-09-27 a fifth dresses it: a generated FACEPLATE** (`Assets/Rig/Faces/`, `faceplates.json`) — the board, the gear library and the pedal editor draw it with live knobs, and the illustration is only the fallback; see [widgets-and-faces.md](widgets-and-faces.md) § Generated faceplates. A new module needs one there too (`Perf/RigFaceplateUiPerfSmokeTests.EveryFaceplate_MatchesItsModulesParams` names the gap).
      **Those 43 files are PROCEDURAL PLACEHOLDERS, not the house art**, and
      that is the one thing to know before touching them: the original 57
      illustrations are photoreal renders of hardware, the 43 are stylised flat
      panels drawn by `tools/rig-art/generate-gear-art.py` (Pillow; one spec row
      per module — family, coat/accent, knob count, one distinctive glyph). They
      match the existing set in the mechanical respects only — opaque RGB VP8
      lossy on the same near-black `#0f0e13` field, 18–51 KB, and
      stomp / amp-head / cab / rack / treadle silhouettes — and **the owner
      decides what hand-drawn art replaces them**. Replacing one is a file drop,
      not a code change: only the basename in `Illustrations` is load-bearing,
      and the script's `--only <id>` regenerates a single sprite if a
      placeholder needs a tweak first. If a replacement arrives in a different
      colourway, move its `Finishes` pair with it, so the enclosure the editor
      draws and the sprite on the board stay the same pedal.
    - **Gear rail (replaces the old add-dropdown).** A right-side drawer in
      `PedalboardView`: category icon tabs from `DspModuleCategory` (only
      families present in the catalogue), a search box (pure `GearRailFilter`)
      and a grid of `GearThumbnail` cards built by `GearRailViewModel` (owned
      by `PedalboardViewModel.GearRail`), **sorted highest-quality-first by the
      pure `GearCuration.RankOf` seam** (ties broken A–Z). Card click = `AddGear` (chain end,
      the existing AddNode path); a pointer drag past 6 px lifts a translucent
      ghost and drops onto the rack as `AddGearAt` (AddNode + MoveNode). The
      preview is the SAME LIVE REFLOW the reorder drag uses — while the ghost is
      over the rack the BOARD OPENS A REAL GAP for the incoming module
      (`SplitBoardPanel.BeginGhostDrag`, gap sized to the chassis it will get);
      moving back off the rack closes it. Keyboard: tabs are a ListBox, cards
      focusable Buttons.
    - **Browse by model (ABI v24, gear rail).** A card whose module has a
      labelled model param (amp `voicing`, cab `model` — `GearCardViewModel.Models`
      built from `ModelParamOf`) shows a **"Browse models…" flyout** of named
      models; picking one adds the module PRE-SET to that model
      (`GearModelOption.AddCommand` → `PedalboardViewModel.AddGearWithModel(type,
      paramId, index)` = AddNode + set the param; e.g. "Plexi 100" → amp
      voicing=10). Each option carries its OWN command (no ElementName/ancestor
      binding across the flyout popup). Rail **search matches model label text**
      (`GearRailFilter.MatchesModelLabel` — "Plexi" surfaces the Amp card). Tested
      in `GearRailViewModelTests` + `GearRailTests`.
    - **Pickers on the pedal (ABI v24) — and the rule EVERY skin must obey.** A
      param with `ValueLabels` (`DspParamInfo.ValueLabels`, loaded once with the
      catalogue) renders as a SELECTOR, not a knob: `PedalNodeViewModel` splits
      `Params` into `KnobParams` (continuous) + `ModelSelectors`/`AmpModelSelector`
      (`PedalModelSelectorViewModel`). Labels are no longer only "models" — since
      0.19 EVERY discrete param carries them (tremolo `shape`, wah `mode`, swell
      Auto-Swell/Volume, whammy `formant`, freeze `capture`, merger Average/Sum).
      **`ModelSelectors` is the ONLY surface a labelled param appears on** —
      `KnobParams` AND `WahKnobParams` both filter labelled params OUT — so **every
      skin's face in `PedalboardView.axaml` MUST render `{Binding ModelSelectors}`.**
      The treadle face did not, which drew the swell pedal's mode switch NOWHERE
      (it existed in the VM and on no surface, so it could not be changed at all);
      the amp/graphic-EQ/junction faces had the same hole. `PedalboardSkinReachabilityTests`
      now parses the real axaml + the real VMs and fails if any face drops the list
      (the junction's "⋯" flyout gates on `HasParamControls`, knobs OR selectors —
      gating on knobs alone hid the merger's picker).
      The amp face's model name is a **prominent clickable plate** opening a `Popup`
      of all 24 voicings **grouped** (American/British/Hi-Gain/Boutique/Bass/DI) by
      the pure `DspModelGrouping` index table; cab/od/dist get a **compact `ComboBox`
      selector**. The face **badge shows the current model NAME from the ABI labels**
      (`BadgeText` = `_badgeParam.CurrentLabel`, replacing the old hardcoded 0–6 map;
      hidden on the amp — the plate shows it). `PedalParamViewModel.SelectedIndex`
      (value = min+index) is the two-way seam. Pure tests:
      `DspModelGroupingTests`, `PedalModelSelectorViewModelTests`; VM-integration
      in `PedalboardViewModelTests` (`Amp_model_plate_*`, `Cab_badge_*`).
    - **"Browse models" is a DECLARED param, never "the first labelled one."**
      `GearCardViewModel.ModelParamOf` resolves `DspModuleVisuals.ModelParamId`
      (amp → `voicing`; cab/overdrive/distortion → `model`; everything else → none).
      It used to take the module's first labelled param, which is why the native
      catalogue could not honestly label a footswitch: the rail would have offered
      to add a freeze pedal PRE-STOMPED (`capture = 1`, which `dcdsp_freeze.h`
      forbids in a preset) or a chorus pre-set to an LFO shape. Decoupling the two
      is what made labelling every enum safe — don't re-infer it.
    - **Ordinal counts snap (ABI v27).** `DspParamInfo.Step` (> 0) marks a param the
      native core lrounds into an int COUNT — chorus `voices`, phaser `stages`
      (**step 2**: the core forces the allpass count even), bitcrusher
      `bits`/`downsample`, slapback/glitch `repeats`, filterseq `steps`, splitter
      `enabledBranches`. `RotaryKnob.Step` snaps every gesture (drag, wheel = one
      step per notch, typed entry, reset) through the pure `KnobMath.Snap`, and
      `PedalParamViewModel` snaps the value it caches and pushes, so the face can
      never read "6.5 stages" while the DSP runs 6. Continuous params keep Step 0.
    - **Hz knobs get a LOG taper (0.9 gear wave).** `KnobMath` was purely linear,
      which put 1 kHz at **4.9 %** of a 20–20,000 Hz dial — the whole guitar range
      inside the first twentieth of the sweep. `KnobMath.TaperFor(paramId, min,
      max, step)` is the pure decision and it reads the CATALOGUE, never a
      per-module table: a param whose id ends in `Hz`/`kHz`, **or** whose range
      spans ≥ `KnobMath.LogTaperDecades` (2) decades, gets `KnobTaper.Logarithmic`.
      Two exclusions and both matter — an ORDINAL param (`Step` > 0) stays linear
      because bunching a COUNT makes its low steps unreachable, and a range a log
      taper is undefined on stays linear via `KnobMath.IsLoggable` (min > 0), which
      is what keeps bipolar `freqshift.shiftHz` and "0 = off" `midside.monoBelowHz`
      on the dial they had. **Only the KNOB POSITION changes**: `Value`,
      `DisplayText`, the typed entry, the step grid and what reaches the DSP are
      all plain Hz either way. `PedalParamViewModel.Taper` decides it once in the
      ctor; `FireKnob.Taper` (default `Linear`, so every unbound knob in the app is
      bit-identical to what shipped) is bound from it in all 12 `FireKnob`
      DataTemplates under `Views/PlayMode/RigEditors/`. The drag is applied in
      TRAVEL space, so a pixel of pointer movement is still a pixel of dial
      movement. `RigParamCapabilitiesTests` pins 1 kHz near mid-travel, the
      round-trip, and BIT-IDENTITY of every linear param against the 3-arg
      overloads.
    - **Per-node LATENCY, and the sidechain key — the shared rack's UNIT panel.**
      Both are properties of the NODE, not of a face, so they live in
      `RigModuleEditorView.axaml`'s shared side rack (`x:Name="RigUnitPanel"`,
      visible on `RigDockUnit.HasUnitPanel`) rather than in nine family views.
      Rows: `RigUnitTransport`, `RigUnitMomentary`, `RigUnitSidechain`,
      `RigUnitLatency`, `RigUnitFileError` — each hides itself, and the panel
      hides when they all do, so ~95 of the 100 modules pay one collapsed Border.
        - `PedalNodeViewModel.LatencySamples` / `HasLatency` / `LatencyText`
          ("Latency: 2048 smp / 46.4 ms") over
          `IDspChainService.GetNodeLatencySamples` → `JuceDspNode.LatencySamples`.
          **`null` and `0` are different answers and must stay that way** — 0 is
          "measured, adds nothing" (also what a BYPASSED node reports), null is
          "nobody asked". The row hides at both. Refreshed with NO timer:
          `RefreshPolledState()` runs at card construction, on every board `Sync`
          (`MirrorFrom`), in the `RigDockUnit` ctor (opening the editor is a poll
          beat — the dock is rebuilt per selection) and on any of this node's
          params moving, which is the whammy `lowLatency` case.
        - Sidechain is ABI v34. `DspModuleType.SupportsSidechain` comes off
          `JuceDspModuleInfo.SupportsSidechain` in `JuceDspChainService.LoadCatalog`
          and **the picker is offered ONLY where it is true** — the engine REJECTS
          a key on any other type, so a picker elsewhere is a control whose every
          setting is refused. The enum is the WRAPPER's `JuceDspSidechainSource`
          (None / SeatInput / SongMix) all the way through `IDspChainService`,
          `DspNode.Sidechain` and `RigSidechainOption`: its numbers are ABI and are
          persisted, so a managed re-typing is a drift the engine reports as a
          rejected key. Labels ARE localized (`Rig_Sidechain_*`) — unlike an ABI
          value label, "Song mix" is a sentence for a player. **Presets carry
          it** (2026-09-12): `DcRigModule.Sidechain` → `UserToneModuleDto`
          (as the raw ABI number) → `DcRigApplier.PushSidechain`, with the same
          push-when-set / diff-on-reuse shape the branch tag uses. See
          [presets-and-nam-captures.md](presets-and-nam-captures.md).
    - **TRANSPORT and MOMENTARY: the two named params a picker cannot drive.**
      `RigParamRoles.RoleOf(module, param)` is the ONE table, keyed by (module,
      param) and holding exactly two entries — `looper.state` and `looper.undo`.
      **It is a stopgap and says so**: the honest fix is a
      `dca_dsp_module_param_flags` capability bit beside the v34 type flags, and
      until that exists the information is not on the ABI at all. A transport is
      one button per ABI label over a POLLED value (the engine auto-closes the take
      at `maxLoopS`, so the last WRITE is not the state); a momentary is
      write-only and shows no state, because the engine performs the action and
      answers its resting value — a two-way mirror flashes "Undo", snaps back, and
      can re-fire. Neither stays in `ModelSelectors`: that is the only surface a
      picker appears on, so leaving them there draws both renderings.
      `PedalboardSkinReachabilityTests.DockParamSurfaces` now names both new lists,
      and `AmpFxRigEditorTests`' dock allow-list carries them.
    - **A REFUSED file is now VISIBLE.** `SetNodeString` returning false means the
      engine KEPT the previous IR/capture, so `PedalStringParamViewModel.Commit`
      used to do nothing at all — indistinguishable from "it loaded and sounds the
      same". It now sets `LoadError` (`Rig_FileRejected`, cleared by the next
      accepted push) and the line is rendered twice: beside the picker
      (`RigDockFileError` in the pedal editor's capture column) and in the unit
      panel. **This was never convreverb-specific** — `cab.irPath`,
      `cab.irPathB` and `acousticsim.irPath` had the same silence;
      `convreverb.irFile` only made it reachable from a fifth module.
