# Editor keyboard input (UI router) — the editor tools are transient; what a pointer drag owes the chart; where the detail lives

## The editor tools are TRANSIENT — one set per ToolCoordinator

The four `Input/Tools` tools carry mutable per-gesture state and were
singletons, so every coordinator (main timeline + each stacked row) traded
drag/marquee state through the same instances.
`TimelineInteractionFactory.CreateManager` resolves a FRESH
`IEnumerable<IEditorTool>` per manager (an injected enumerable would be
materialized once at factory construction and shared again), and
`TimelineInteractionManager.Dispose` disposes disposable tools —
`HighwayInteractionTool` subscribes to `HistoryChanged` in its ctor, so the
disposal is load-bearing, not hygiene. `UI/Input/EditorToolLifetimeTests`
pins the registration lifetime, the per-call resolution, the manager's
disposal and the actual unsubscription (counting fake).

## What a POINTER DRAG owes the chart

Four rules, all found broken on 2026-09-05 and each pinned in the default gate.
They apply to every drag on the timeline, not just the one you are editing.

- **A click is not a drag, and the three EVENT-ribbon gestures did not know
  it.** `EventPointDragGesture`, `LyricDragGesture` and `BeatDragGesture` each
  set `HasDragged = true` on the FIRST pointer move of the press, and
  `EventInteractionTool.OnPointerReleased` commits whenever that flag is set —
  so one pixel of tremor while CLICKING committed a real undo node. None of the
  three edits is small: a marker is re-snapped (`DragState.GetDelta` snaps the
  REFERENCE position, so an off-grid marker — i.e. every RS-XML/psarc import —
  jumps to the nearest grid line), a lyric is snapped to the grid or a waveform
  onset, a beat is retimed with its segment's BPM recalculated. They all gate on
  `Input/Tools/EventInteractionGestures/GestureDragThreshold` now, which LATCHES
  and whose distance is `MarqueeHitTester.DragThresholdPx` — one number decides
  click-vs-drag everywhere. **The highway escapes this by accident, not design:**
  its snap goes through `SoundMagnetSnap`, which pulls back onto the very sound
  under the pointer. Nothing magnets a marker, a lyric or a beat.
  (`GestureDragThresholdTests`.)
- **Clamp the DELTA, never the item.** A drag moves its payload rigidly, so when
  the earliest member would land before zero the delta shortens for everyone.
  `HighwayInteractionTool.RefreshDragPreviews` and `EventPointDragGesture` wrote
  `Max(Zero, original + delta)` per item, which piled everything that went
  negative onto beat 0 and left the rest where the drag put them — the
  selection's spacing, the one thing a move preserves, destroyed in one undo
  node, because the PREVIEW is what the release commits. All four drag paths go
  through the pure `Input/Tools/DragBounds` (`Earliest` + `ClampDeltaToStart`),
  the highway/FHP twin of `LyricDragBounds`. It also retires two private
  `new FractionalPosition(999999)` sentinels tested with `BeatId != 999999`,
  which a payload really at beat 999,999 switched off. (`DragBoundsTests`.)
- **A drag ends where the pointer was RELEASED.** `ToolCoordinator` drops every
  move inside 16 ms and `OnPointerReleased` commits the previews the last
  surviving move built, so a flick whose final frame crossed a grid line dropped
  the selection on the previous one. `HandShapeInteractionTool` recomputed at
  release; `HighwayInteractionTool` and `FretAnchorInteractionTool` did not, and
  both do now (the FHP tool's preview build is ONE `RefreshDragPreviews` the move
  and the release share). Same fault the minimap drag had.
  (`DragEndsAtTheReleaseTests`.)
- **Switching arrangement or level DROPS the content selection.** Every
  `TrackEditorContext` shares the app-wide `ISelectionService`, and only a
  project swap, a snapshot restore and a vocal-phrase replacement ever cleared
  it — so a selection made on the Lead survived into the Rhythm, and it is not
  inert: `InspectorViewModel.ApplyNoteMutation` built a real `MutateNotesAction`
  over it, Delete pushed a node whose UNDO inserted the foreign sounds into the
  level now open, and `ChartRenderer` read them into the frame snapshot.
  `TrackContextRegistry.DropSelectionOrphanedByTheTrackSwitch` compares
  INSTANCES, never the notification — `ProjectState.NotifyActiveArrangementChanged`
  re-raises after a rename without changing anything, and a rename must not cost
  the charter their selection. `InspectorIssuesViewModel.JumpToIssue` is safe because it
  switches the level FIRST and selects afterwards; keep that order.
  (`UI/TrackSwitchSelectionTests`.)

## Where the detail lives

| Read this when… | File |
|---|---|
| you need to know WHAT a keyboard verb acts on — the target ladder, its precedence and its refusals | [editor-input/target-ladder.md](../editor-input/target-ladder.md) |
| you are touching the editor's loop region or the selection audition (it reuses Live Play's plumbing rather than copying it) | [editor-input/loop-region-and-audition.md](../editor-input/loop-region-and-audition.md) |
| you are touching the keyboard PEN — its state table, undo granularity, why bare digits toggle strings, its Esc branch and its snapping | [editor-input/keyboard-pen.md](../editor-input/keyboard-pen.md) |
| you are changing what a verb does to a CHORD, or touching the chord-template panel's selection requirement and shape narrowing | [editor-input/chord-verbs.md](../editor-input/chord-verbs.md) |
| you are adding a keybind or changing a default — the no-collision rule, the required Keybinds settings row, `Ctrl+Alt`/AltGr, the `NormalizeKeybinds` migration, and user-created binds via `RegisterDynamic` | [editor-input/keybind-defaults.md](../editor-input/keybind-defaults.md) |
| you are touching the nudge keys, or ANY repeating/dragging verb's undo granularity — the merge verbs, and where a gesture deterministically ENDS | [editor-input/nudge-keys.md](../editor-input/nudge-keys.md) |
| you are wondering how a chord actually REACHES `ChartKeybindHandler` — the `MainWindow` tunnel, the Esc priority chain, and why a rebindable `ClearSelection` never runs | [editor-input/keybind-routing.md](../editor-input/keybind-routing.md) |
