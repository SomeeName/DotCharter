# The preset browser, NAM captures and A/B compare

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).

- `Controls/Dsp/` — continued from [widgets-and-faces.md](widgets-and-faces.md):
    - > **This panel was MISSING between v0.43 and 0.57, and the story is worth
      > keeping.** It lived in `PedalboardView.axaml` and went with that 302 KB
      > file when v0.43 lifted the rig into `AmpFxRigView`. The view model, its
      > services, its auth and its unit tests all survived, so nothing went red —
      > but the rail row kept being built and kept opening an EMPTY card grid
      > (`PresetBrowserFilter` yields `[]` for that kind BY DESIGN, because a
      > panel was meant to take the column). Nothing distinguished "the feature
      > is gone" from "you have no captures". The cref-dangling sweep that caught
      > fifteen orphaned controls at the time did not catch a whole panel, and no
      > smoke test selected that rail row.
      > **Restored in 0.57**, ported to the Fire grammar, with ten localization
      > keys that an unused-key sweep had removed along with the markup. The
      > guard that was missing now exists:
      > `PlayModeScreensUiPerfSmokeTests.RigToneBrowser_swapsTheCardGridForTheTone3000CapturePanel`
      > picks the rail row and asserts the panel MEASURED, drives both tabs, and
      > checks the grid comes back. **`ShowCards`/`ShowNoCards` on
      > `PresetBrowserViewModel` are load-bearing**: a binding cannot AND two
      > properties, and without the second one the preset empty state draws on
      > top of the capture panel — which is what the missing panel looked like.
    - **NAM Captures section (0.16, in the preset browser drawer).** A rail
      entry after "My Tones" (`PresetFilterKind.NamCaptures` — the pure filter
      yields NO cards for it; the view was to swap the search+card grid for the
      capture panel via `PresetBrowserViewModel.IsNamCapturesSelected`).
      `NamCaptureBrowserViewModel` drives it: TONE3000 online search (free-text
      + gear-type filter, rows carry gear/format+size/**license** chips — the
      license chip is attribution policy, keep it) with per-row download
      progress, and a "Library" view over `INamCaptureLibrary` (delete,
      attribution link, filtered live through the pure `NamCaptureIndex`).
      **Search requires a sign-in** (the live API rejects anonymous requests):
      the signed-out Online view shows a friendly sign-in CTA (`ShowSignInCta`,
      reuses `SignInCommand`) instead of an error row, `SearchAsync`
      short-circuits when signed out (never fires a doomed request) and the
      pending query re-runs automatically after a successful sign-in. Flow
      order is distinct: NOT configured ⇒ the `IsNotConfigured` configure hint
      **plus an inline client-id TextBox + Save** (`ClientIdEntry` /
      `SaveClientIdCommand`, writes through the optional `IConfigurationService`
      ctor arg) so setup happens right at the wall; configured but signed out ⇒
      the CTA. Signed-in behaviour is unchanged. The full setup also lives in
      **Settings → TONE3000**, a screen of its own:
      `Presentation/Screens/Settings/Tone3000SettingsViewModel.cs` behind
      `ITone3000SettingsScreen`, rendered by
      `Views/Settings/Tone3000SettingsView.axaml` — client-id + Save, a
      sign-in/out row with `SignedInText` via `ITone3000AuthService`, and the
      tone3000.com/settings + redirect-URI help line; driven by the settings
      shell's Save/Load flow through `ISettingsTab`. Both write the same
      `AppConfig.Tone3000ClientId` (TONE3000 services are DI singletons, so
      configuring in one place lights up the other).
      Model tier preference on download: lite → feather → standard → nano
      (`PickModel`, per PLAN-0.16 4.4). **"Load into rig"** is enabled only
      when `IDspChainService.Catalog` exposes a `namamp` module type (else
      disabled with the "v22 engine update" tooltip, wrapped in a Border so the
      tooltip shows while disabled). When live it REUSES an existing namamp node
      if the chain has one (swaps the capture) else adds one, pushes the capture
      file as the `modelPath` string param (WITHOUT this the amp is modelless =
      silent), and auto-appends a **Parametric EQ directly after the amp,
      BYPASSED** at catalogue defaults (`EnsureBypassedEqAfterNamamp` — a
      ready-to-grab tone stack; skipped when an `eq` is already immediately
      after, so a reload never dupes it). Adding the node fires
      `IDspChainService.NodesChanged` → the audio service's 48 kHz policy.
      The namamp card is the **neural amp HEAD skin** (`PedalSkin.NeuralAmp`, see
      the face bullet above — it stopped being a stompbox in 0.36) and carries the
      **capture-status pill** along the foot of its chassis
      (`Border.NamStatus`; `PedalNodeViewModel.IsNeuralAmp` +
      `NamStatusText`/`NamHasWarning`, read from the poll-only `loaded`/
      `rateMismatch`/`resampling` params via `GetParam`, refreshed in
      `PedalboardViewModel.RefreshNeuralAmpStatus()` on the natural `Sync` rebuild;
      `IAudioPlaybackService.ActiveSampleRate` supplies the engine rate in the text);
      the panel itself carries a static `Nam_RateHint`.
      **The native capture load is ASYNCHRONOUS** — `SetNodeString(modelPath)`
      returns true immediately and `loaded` only flips a few hundred ms later, so a
      status read taken at push time ALWAYS sees "not loaded" and the badge used to
      keep its warning for the rest of the session. Every path that binds a capture
      (the NAM browser's Load-into-rig, the head's faceplate capture PLATE →
      `PedalNodeViewModel.StringCommitted`) therefore calls
      `PedalboardViewModel.ScheduleNeuralAmpStatusRefresh()`: a bounded,
      SELF-TERMINATING burst (~250 ms × ~3 s, single-flight, stops as soon as every
      neural amp reports loaded) marshalled through the optional `IUiDispatcher` ctor
      arg — still no standing timer on a hideable surface, and inert in unit tests.
      Pinned by `PedalboardAuditRegressionTests.Namamp_status_*`.
      **A cab with an empty `irPath` is NOT unconfigured — its plate must never
      say "No capture loaded".** Every factory preset picks one of the twelve
      built-in IRs via the cab's float `model` param; the string `irPath` is
      only for a USER `.wav`, no preset sets it, and no `.wav` ships. The owner
      read the old fallback as "library presets forgot the cabinet". The file
      strip now prints `Rig_BuiltInIr` ("Built-in · 4x12 Greenback") off the
      node's badge param (`PedalStringParamViewModel`'s optional `builtinLabel`
      closure, wired only for `irPath`; the node's badge-changed handler calls
      `RefreshPlate()` so switching the model re-titles it live). A namamp's
      empty plate stays "No capture loaded" — there the empty state is real
      (the signal passes dry). Pinned by `RigLibraryChoiceTests`
      (`ACabsEmptyIrPath_NamesTheBuiltInIr_NotNoCapture` and siblings).
      The VM re-reads auth/library state after awaited calls and
      on `Activate()` instead of subscribing to service events (they fire on
      worker threads). `PedalboardViewModel` only builds the section when the
      TONE3000 services are DI-wired — preset-only tests pass null and the
      rail entry is absent (`PresetBrowserViewModel.NamBrowser == null`).
      Tested in `NamCaptureBrowserViewModelTests` (fake services, no Avalonia).
    - **Tone-preset browser + save profiles — the rig's TONES VIEW (`Views/PlayMode/RigTonesView.axaml`, its own file since 2026-09-27; the Tones MODE since W4, a drawer before that).** `PedalboardViewModel` takes `IDspTonePresetService` and owns a `PresetBrowserViewModel` (`Browser`); the mode is `PedalboardViewModel.IsBrowserOpen` (`AmpFxRigViewModel.IsTonesMode`, the header's preset pill and `ToggleBrowserCommand`). The view: a HEAD (title + count, the search, the selected USER tone's `···` menu — Overwrite / Delete —, Save as new…, the AI designer toggle, ✕), a fixed SIDEBAR with the category rail (All → the eight `DspToneCategory` → Legacy → My Tones → NAM Captures; each row carries its COUNT under the current search, `PresetRailRow.Count`, and an empty row is dimmed, never hidden — the sidebar is also where a setlist section goes), and the `PresetCardViewModel` grid (each card is THREE things since the owner found it "way too much text" (2026-09-27): the name (two lines at most), one quiet line of category + `Mine` / `Edited` tags, and the chain as a strip of `PresetModuleGlyph` bars in the unit family's Fire colour; the description and `ChainSummary` in the catalogue's words, from the `moduleDisplayName` seam, are the card's TOOLTIP; `FireRigToneCardMinHeight` 88 / 96 wide). `IsActive` marks the card of the tone that is PLAYING (the last apply, including the designer's Hear it), `IsSelected` the one Overwrite / Delete act on. The empty grid says why (`EmptyText`) and offers `ClearSearchCommand`. **The searchable `AutoCompleteBox` dropdown (`PickerRows` / `SelectedPickerRow` / `PresetPickerRow`, `PresetBrowserFilter.Matches`) was the retired rack's and is DELETED (2026-09-27)** — no view had bound it since v0.43. Applying a card goes through `ApplyPreset` (tracks the active preset for `ActivePresetName` and the playing card); **Save/Overwrite re-point `_activePreset` at the SAVED preset** — the tone you just saved IS the live rig, and leaving the active tone on the previously-applied preset made the header's name lie about what the rig is playing. The card grid is ordered by the pure `PresetCuration.OrderFeaturedFirst` seam (marquee artist/genre tones lead; everything else keeps catalogue order). Clicking a card = `IDspTonePresetService.Apply` onto the live chain through the apply queue, then `PedalboardViewModel.Sync()` reconciles the board (`onApplied` — incremental since 0.59); Save/Overwrite snapshot the live rig via `BuildCurrentRig()` → `SaveUser(...,overwriteId)` (overwrite refused on built-ins by the service). All VM logic is unit-tested against a `FakeDspTonePresetService` (`PresetBrowserViewModelTests`, `PresetBrowserLibraryTests`). Rig-accent brushes (`AmpOrange`/`AmpOrangeBright`/`AmpTeal`/`RigPanel`/`RigStroke`/`RigBackdrop`) are fixed BIAS-FX-style brand colours — like the Medal* podium brushes they are deliberately NOT in `ThemeTokens`.
      **Drawer geometry + search coalescing (2026-08).** The drawer is 480 px
    - **A tone card's right-click menu (2026-09-28)** — right-click, the menu key or Shift+F10 on a card (Avalonia raises all three as `ContextRequested`; `RigTonesView.OnCardContextRequested`) opens `Views/PlayMode/RigToneCardContextMenu.Build(browser, card)`, built per open like `RigUnitContextMenu`, at the pointer or, from the keyboard, under the card (`RigTonesView.CardMenu`, one at a time). A right-click does NOT select the card. Items, each a `PresetBrowserViewModel` command with the CARD as parameter: Load (`ApplyCardCommand`), Rename / edit details… (`BeginEditCardCommand` — the ONE save dialog in edit mode: `EditTarget` / `IsEditingDetails`, `SaveDialogTitle` / `SaveDialogConfirmText`; confirming calls `SaveUser(..., card.Id)` with the card's OWN rig, never the live one; any close leaves edit mode, `OnIsSaveDialogOpenChanged`), Duplicate (`DuplicateCardCommand` — any card, built-in included, into My Tones as `PresetBrowser_CopyName`, selected, NOT applied), Overwrite with current rig (`OverwriteCardCommand`, the one implementation the head's `Overwrite` also runs), Export to file… (`ExportCardCommand` — the card's preset through the same picker / writer / `ImportStatus` path as `ExportActiveCommand`, disabled without pickers) and Delete… (`RequestDeleteCardCommand`). **Delete ASKS FIRST**, from the card menu and the head's `···` alike: `PendingDelete` opens `RigBrowserDeleteDialog`, a screen modal beside `RigBrowserSaveDialog` in `AmpFxRigView.axaml` (keys on Cancel); only `ConfirmDeleteCommand` calls `DeleteUser`, and both Esc walk-ups close it first (`RigEscapeParityTests`). On a built-in, Rename / Overwrite / Delete are DISABLED with `PresetBrowser_BuiltInLocked` as their tooltip (`CanChangeCard`, `LockedReason`). Guards: `PresetBrowserViewModelTests`, `PresetToneFileTests`, `Perf/RigToneCardMenuUiPerfSmokeTests`.
      with its interior padded ONCE (it shipped 352 px with `FirePadPanel`
      doubled, leaving a ~160 px body — three of twelve rail categories
      visible), the rail is `FireList DenseRows` (`FirePadListRowDense`) so the
      whole taxonomy fits on screen, and `SearchText` routes through
      `CoalescedFilterPass` (post = the optional `IUiDispatcher`; rail clicks
      `RunImmediately`). Pinned by `AmpFxRigScreenTests.TheDrawer_PadsOnce_*`
      and `PresetBrowserViewModelTests.Typing_a_search_burst_*`.
      **The save-as-new dialog is a SCREEN modal (2026-08)**: `RigBrowserSaveDialog`
      scrims `RigScreenRoot`, not the drawer. It opens from TWO places — the
      header's Save and the drawer's Save-as-new — and snapshots the LIVE rig,
      and its first home inside the drawer's subtree meant the header path with
      the drawer CLOSED opened an invisible dialog (the flag flipped, Esc ate a
      keypress, nothing appeared). Pinned by
      `AmpFxRigScreenTests.TheSaveDialog_IsAScreenModal_VisibleWithTheDrawerClosed`;
      the Esc unstack order (save modal → drawer → board) is unchanged and pinned
      by `PlayOverlayFactsTests.EscapeUnstacksTheBoardsLayersInOpeningOrder`.
      **The legacy filter + the NEURAL badge (0.37).** The catalogue now carries
      both halves of 70 capture/algorithmic pairs, so an unfiltered grid shows
      every tone TWICE under near-identical names. `PresetBrowserFilter.Filter`
      therefore takes `includeSuperseded` (default **false**, bound to the
      browser's "Show legacy" checkbox) and drops any preset for which a
      `nam-`-prefixed twin exists. **Hiding is keyed on the twin ACTUALLY
      EXISTING, never on the name ending in "(Legacy)" and never on being a
      factory preset** — that is what guarantees a rig with no capture version
      (the acoustic sims, the vocal-synth rigs, every user tone) can never be
      filtered out of the player's own browser. The card's NEURAL badge comes
      from `PresetBrowserFilter.IsCaptureBacked`, which reads the RIG (a `namamp`
      in the chain), not the id, so a user tone built on a capture badges exactly
      like a factory one. Search composes with the filter rather than bypassing
      it. Pinned by `PresetBrowserFilterTests`.
      **A/B compare (0.37, rebuilt 0.61).** `PedalboardViewModel` owns a pure
      `ToneCompareSlots` (two `DcRigPreset` snapshots + which is live) behind
      **four** controls in the rig header's compare strip (`RigSceneStrip`; it was
      labelled "Scene" until real scenes A–H arrived on 2026-09-27 and reads
      "Compare" now — scenes are [scenes-and-setlists.md](scenes-and-setlists.md)): the A and B lamps
      (`SelectCompareSlotCommand`, `CommandParameter` `"A"` / `"B"`), SWITCH
      (`ToggleCompareCommand`) and "Copy to both" (`SeedCompareCommand`).
      **Switching STORES the live rig into the slot being left**; without that,
      a knob moved on A is discarded the moment you flip to B and back, which is
      the classic A/B bug and exactly when a player will not notice the loss.
      Three rules that are easy to get wrong:

      - a slot applies as a **USER-origin** preset (the factory path
        loudness-normalizes, and a comparison whose two sides are re-levelled is
        not a comparison);
      - **an EMPTY target slot is FORKED from the live rig, not refused** — this
        reversed in 0.61 and the old rule ("toggling into an empty slot changes
        nothing at all") is the bug the owner reported: on a rig nobody had
        seeded, A, B and SWITCH were all inert, and SWITCH was additionally
        `IsEnabled`-gated on `CanCompare`. Forking stays non-destructive (the
        slot being left is still stored on the way out), and **nothing in the
        strip may carry an `IsEnabled` again** —
        `AmpFxRigNoDeadButtonsTests.NothingInTheSceneStrip_IsUnpressable`
        fails if one does;
      - applying a preset RE-SEEDS, so "compare" always means "against the tone
        I just picked".

      **Level-matched switching (#299), on by default** (`RigCompareLevelViewModel`,
      `PedalboardViewModel.CompareLevel`; the header's `RigCompareLevelView`). Slot A is the reference; B is trimmed
      onto A's level on the rig's TRAILING `gain` module (appended at 0 dB when a
      slot has none) with one `SetParam` after the apply. Each slot's level is
      learnt from `OutputMeters`' RMS on the rig's meter clock
      (`CompareLevel.Sample`, gated and smoothed in `ToneCompareLevelMatch`); until
      BOTH slots have been heard, the static walker
      (`CompareLevelTrim.EstimateDb`) stands in for both. **A slot is stored
      UNTRIMMED** (`CompareLevel.Snapshot` strips the landed trim, `CompareLevelTrim.Strip`)
      or the trim would compound on every switch. A save while B plays keeps the
      trim: it is what was heard. Pinned by `ToneLabViewModelTests` and
      `ToneLabMathTests`.

      **The push goes to `ApplyToPlayer(SelectedPlayer, …)`, never `Apply`.** It
      was `Apply` — seat 0 for ever — while the seat picker repoints `_dsp`, so
      on player 2's board SWITCH compared player 1's tone
      (`RigCompareSceneTests.TheComparePush_LandsOnTheSelectedSeat_NotSeatZero`).
      **`ApplyToPlayer` still has no other UI caller: the preset BROWSER applies
      to seat 0 too, and that is the same bug one surface over, unfixed.**

      The buttons live in the preset toolbar, whose DataContext is the BROWSER
      view model, and reach the pedalboard view model through
      `$parent[UserControl]` — a wrong ancestor there compiles clean and resolves
      to null (blank, disabled button), so
      `InspectorConstructionUiPerfSmokeTests.AmpFxRigHeader_BindsTheCompareCommandsThroughTheChainViewModel`
      realizes the header and asserts a button really holds the command.

## Tone FILES: export the live rig, import somebody else's

`PresetBrowserViewModel.ExportActiveCommand` / `ImportCommand`, over the pure
`DotCharter.Services/Audio/Dsp/Presets/TonePresetJsonFile` (`Serialize` /
`Parse`). The rig screen surfaces the same two commands on its header
(`AmpFxRigViewModel.ExportToneCommand` / `ImportToneCommand` /
`ToneFileStatus`), because the tone pill they sit beside is in the header's own
scope, not the browser drawer's.

- **The document is `UserTonePresetDto`, the shape `AppConfig.UserTonePresets`
  already stores** — NOT a second format. That is what carries the ABI-v22
  string params (a `namamp` capture path, a cab IR), the ABI-v23 branch tag and
  the ABI-v34 SIDECHAIN key, which is exactly what two of the four snapshot loops
  used to drop.
- **Three NODE-STATE fields travel, and they are the ones no param loop
  notices.** `StringParams`, `Branch` and `Sidechain` are not params: a native
  string/tag/key does NOT survive a remove/re-add, and `DcRigApplier` re-pushes
  only what the preset actually CARRIES. Dropping one therefore restores a
  MODELLESS namamp, a FLATTENED split region, or a ducker that no longer ducks —
  and in every case every op in the apply reports success, so nothing says so.
  `Sidechain` was the third to fall down that hole (added 2026-09-12). The ONE
  capture site is `DcRigSnapshot`; the persisted field is a RAW ABI NUMBER
  (`UserToneModuleDto.Sidechain`, `int`, 0/1/2) and NOT the enum, because a
  `JsonStringEnumConverter` added anywhere upstream would silently rewrite every
  saved tone's key as a name. An unknown number folds to `None`
  (`UserToneModuleDto.SidechainOf`) rather than being cast blindly — an undefined
  value reaches `dca_engine_dsp_set_node_sidechain`, is rejected, and a rejected
  configuration leaves the module BYPASSED. Pinned by
  `Audio/Dsp/Presets/TonePresetSidechainTests` (capture → JSON → import → apply,
  the missing-field fixture, the reuse/NoOp halves) and by
  `FactoryPresetsAgainstLiveCatalogueTests`, which now fails a shipped preset that
  keys a module the LIVE catalogue says takes no key.
- **Export writes the LIVE chain** (`captureRig`, the same snapshot "Save as
  new" takes) with the ACTIVE preset's identity — what a player wants to send is
  what they are hearing, tweaks included.
- **Import is defensive by contract and never throws.** Every module id is
  checked against `DcRigTargetCatalog.ModuleTypeIds`, every param is clamped
  through `Clamp` (an unknown param id passes through — the native module
  refuses what it does not know), the branch tag is clamped 0..3, the sidechain
  key is folded to a source this build knows, unspecified
  params are filled by `FillDefaults` so an imported tone cannot inherit the
  previous one's values, and an invalid control map is DROPPED rather than
  costing the whole tone. Faults travel as `TonePresetImportFault` and the WORDS
  are chosen in the view model (`ImportStatus`).
- **It lands through the `ConfirmSave` path, which mints a FRESH id**: an
  imported file may never overwrite a preset the player made, whatever id it
  carries. It is then applied, because an import you cannot hear is a file
  operation.
- `UI/ViewModels/PlayMode/PresetToneFileTests` (fake picker + real temp files)
  and `Audio/Dsp/Presets/TonePresetJsonFileTests` (the hostile documents).

## Spillover switch (ABI v37)

`RigSpilloverToggle` in the Tones head (`RigTonesView`'s `RigBrowserHead`, its
own `Auto` column right after the search and left of the spacer, so the selected
tone's `···` menu appearing never moves it and it is never squeezed at 1280 px —
`RigTonesRedesignUiPerfSmokeTests` watches it in both head cases) is `PedalboardViewModel.Spillover`: a delay/reverb the
next tone drops keeps ringing. Rig-wide and persisted (`RigConfig.RigSpillover`,
default on), written to EVERY seat's chain; disabled with its reason in the
tooltip (`SpilloverTip`, `ToolTip.ShowOnDisabled`) while no engine can honour it
(`CanToggleSpillover` = the chain's `SupportsSpillover`). A module the preset
diff KEEPS (`DcRigApplier` reuses same-type nodes) was never removed and rings
through without it. `SpilloverSettingTests`, `RigReampUiPerfSmokeTests`.
