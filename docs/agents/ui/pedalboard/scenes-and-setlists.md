# Scenes, setlists and MIDI program change

Part of the Amp & FX rig reference. Router: [../pedalboard.md](../pedalboard.md).
Helix / Quad Cortex / Fractal are the reference for every convention below.

## Scenes (A–H)

- **Data** — `DotCharter.Tone/Audio/Dsp/Scenes/RigScenes.cs`: `RigSceneModule`
  (type, params, bypass), `RigScene` (name + modules; `Modules == null` = never
  used), `RigSceneSet` (eight slots + `Active`) and the pure rules in
  `RigScenes` (`Capture`, `Fold`, `Switch`, `Project`, `StartOn`, `Rename`,
  `Store`, `Clear`, `SameScenes`, `IsTrivial`). A scene matches modules by TYPE
  and OCCURRENCE (the k-th overdrive onto the k-th overdrive) — the rule
  `DcRigApplier` reuses nodes by — so a unit added elsewhere takes nothing.
  **A scene never carries structure**: string params, branch, sidechain and
  controls are the preset's.
- **Where it lives** — `DcRigPreset.Scenes` (init, null for every factory tone and
  every tone saved before scenes) and `IDspChainService.Scenes` (managed state
  beside the chain; the default interface members keep test doubles scene-less;
  `JuceDspChainService` and `FakeDspChainService` hold it and CLEAR it when a new
  engine attaches). **`DcRigApplier.Apply` writes `chain.Scenes = preset.Scenes`
  with every rig** — a rig without scenes clears them, so one tone's scenes are
  never projected onto another's modules. A transform that builds a new rig must
  use `with { Modules = … }` (FillDefaults, the loudness normalizer and
  `DcRigPresetPreparation` do), never `new DcRigPreset(…)`, or the scenes drop.
- **Edits go into the scene that is playing** (Helix "snapshot edits: recall").
  `DcRigSnapshot.FromChain` folds the live values into the active slot, so every
  snapshot — undo, save, the persisted live rig, the tone designer's restore
  (`TonePreviewSession`), the RS in-game-tone run's restore
  (`RsTonePlaybackCoordinator` keeps the scenes beside its node snapshot) — has
  them. `FromNodes` has NO scenes; use `FromChain` for anything that hands the
  player's own board back. A set that holds nothing beyond the live rig (only the
  active slot used, no names) folds to NULL, so clearing every scene is exactly
  a rig without scenes again.
- **A switch never adds or removes a unit.** `PedalboardViewModel.SelectSceneAt`
  builds the LIVE rig with the scene's params and bypass projected over it and
  applies it verbatim (USER origin) — the applier's diff reuses every node and
  writes values only, so delay/reverb tails ring through. An unused scene is
  FORKED from what is playing (the A/B compare's rule): the press always lands and
  nothing audible changes. A switch turns the master bypass off first. One switch
  / rename / store / clear = ONE undo step (`RigHistory.Committed`, never folded
  into an open knob burst); the history compares with `RigEditState.IsSameState`
  (scenes exact, active included). The EDITED badge (`RigEditState.IsEdited`)
  ignores which scene is playing: switching scene is not an edit, a knob moved in
  any scene is.
- **A switch WAITS for a tone that is arriving.** A tone pick applies on a worker
  (`ToneApplyQueue`), and a pedal board sends a program change and the scene CC
  together — a switch built from the half-applied chain would put the half-old
  rig back. While `PresetBrowserViewModel.IsApplying` (`ToneApplyQueue.IsBusy`),
  `SelectSceneAt` only remembers the scene; `OnPresetApplied` switches it on the
  new tone (`ApplyDeferredScene`), and `ResyncAll` drops it.
- **Engine rebuild** — scene values are params, so the chain restore replays the
  sound; `ToneSessionService` persists the scenes with the live rig
  (`UserTonePresetDto.Scenes` → `RigSceneSetDto`, omitted from the JSON when null,
  so an old store reads back unchanged) and its signature includes them (a rename
  alone is written).
- **Persisted/imported** — `RigSceneSetDto.ToSet` normalizes (eight slots,
  active in range, names trimmed/capped at `RigSceneSet.MaxNameLength`);
  `TonePresetJsonFile.Parse` clamps scene values and drops unknown module types.
- **UI** — `Views/PlayMode/RigSceneBar.axaml` over `PedalboardViewModel`
  (`SceneSlots` → `RigSceneSlotViewModel`), hooked into `AmpFxRigView.axaml` with
  ONE element at the top of `RigWorkspace` (row 0, over the stage — `RigDock` moved
  to row 1; `x:Name="RigScenes"`). Its 48 px come out of the STAGE: on top of the
  map it cost a 1280×800 triple split its third lane
  (`Perf/RigAmpliTubeLayoutUiPerfSmokeTests.ASplitRig_FitsTheMap`). Eight fixed-width `FireModeButton SceneSlot` toggles, a
  right-click menu (rename in place / store current sound / clear — never the
  scene playing), the active setlist's ◀ name · n/N · tone ▶, and a fixed-width
  MIDI monitor. Keys: 1–8 / keypad 1–8 = scene A–H, PageUp / PageDown = the live
  setlist, via `RigPerformanceKeys.Map` in `AmpFxRigView.OnPerformanceKey` —
  never while a `TextBox` has the keys, never with a modifier, and the page keys
  stay the scrollers' when no setlist is live.
- **The A/B strip in the header is NOT scenes.** It was labelled "Scene A/B";
  its keys (`Rig_Scene`, `Rig_SceneA/B`, the tips) now read "Compare".

## Setlists

- `RigConfig.RigSetlists` (`RigSetlistDto` / `RigSetlistEntryDto`: preset id +
  optional starting scene) and `RigActiveSetlistId`, per INSTALL like the user
  tones they point at. Entries name presets by ID: an overwritten tone keeps its
  place, a deleted one reads "Missing tone (…)" and is skipped with a status line.
- `ViewModels/PlayMode/RigSetlistsViewModel.cs` (`PedalboardViewModel.Setlists`,
  declared in the root `PedalboardViewModel.cs`): new / rename (in place, on focus
  loss) / delete, add the CURRENT tone (`Browser.ActivePreset`), ▲▼ reorder,
  remove, per-entry starting scene, "Use live". Next / Previous stop at the ends
  (no wrap). Which song is playing is session state (starts at none).
- A song applies through `PresetBrowserViewModel.ApplyPresetOnScene` —
  `RigScenes.StartOn` puts the scene's values into the SAME apply, and the active
  preset stays the preset as saved (so a starting scene is not an edit).
- **UI** — `Views/PlayMode/RigSetlistPanel.axaml` (root over the BROWSER, body over
  `Setlists`), shown in the Tones mode's card column when the rail's "Setlists"
  row (`PresetFilterKind.Setlists`, only when the browser was given setlists) is
  picked; `ShowCards` / `ShowNoCards` yield like they do for the NAM section. The
  hook is ONE element before `RigBrowserCardHost` (`x:Name="RigSetlists"`) — in
  `RigTonesView.axaml` since the Tones mode became its own view (its own name
  scope: find it in the visual tree, not with the rig view's `FindControl`). The
  rail row prints no count (`PresetRailRow.Count` is null for it, like the NAM row).

## MIDI program change and the scene CC

- `RigMidiProgramMessage.TryDecode` (2-byte `0xCn pp`) beside the untouched CC
  decoder; `JuceAudioPlaybackService.RigMidi` raises
  `IRigMidiInputSource.RigMidiProgramReceived` (default no-op members on the
  interface). `RigMidiInputViewModel` queues ONE pending program (a burst is "go
  to the last one"), drains it on the UI post with the CCs, and keeps
  `LastMessage` (the card and the bar's monitor show it).
- `RigMidiRouting` (Tone, pure): `RigMidiChannel` (0 = omni, 1..16),
  `RigMidiProgramChange` (default on), `RigMidiSceneCc` (default 69 — Helix's
  snapshot CC; -1 = off). Edited in `RigMidiControlsView` (the Controllers & MIDI
  expander of the module editor's details, where the input device is chosen).
- `PedalboardViewModel.HandleMidiProgram`: program n = song n of the live setlist
  (0 = the first; past the end is ignored with a status line), else the n-th of
  the player's own tones. `HandleMidiControl`: the scene CC (value 0..7 = A..H,
  anything else ignored — a pedal's 127 never jumps to H) unless a MIDI learn is
  running; every other CC goes to the knob assignments unchanged. RigLab starts
  the MIDI bridge at startup when a device is configured (`RigLabHost.StartCore`).

## Pinned by

`Audio/Dsp/Scenes/RigSceneModelTests` (the pure rules, applier/snapshot/pipeline
carrying scenes, the DTO round trip and old-JSON compatibility, the import
sanitizer, the PC decode and routing), `UI/RigScenesTests` (switch writes values
and never adds/removes a node, edits recalled, one undo step, rename/store/clear,
scenes save with a tone and leave with the next, not an edit, master bypass,
engine rebuild), `Audio/Dsp/ToneSessionScenesTests` (persist + restore on an
engine rebuild, a rename alone is written), `UI/RigSetlistsTests` (CRUD +
restart, stepping and ends, starting scene, missing tone, program change /
own-tone fallback / channel filter / switch, scene CC and learn, a scene asked
for mid-apply landing on the new tone with the worker HELD in the apply, the key
map),
`UI/RigMidiProgramChangeTests` (delivery, burst, stale generation, settings), and
`Perf/RigSceneBarUiPerfSmokeTests` (the bar at 1920 and 1600 — nothing moves or
leaves its line across state changes; the keys; rename in place; the setlist
panel and the MIDI card lay out; captures with `DOTCHARTER_UI_SWEEP_DUMP=1` +
`DOTCHARTER_RIG_CAPTURE_DIR`).
