# `State/Checkpoints/` + `Diagnostics/` — snapshots and the diagnostics bundle (deep reference) — `State/ProjectDirtyTracker`; `State/HistoryService`

## `State/ProjectDirtyTracker`

  `State/ProjectDirtyTracker` derives the unsaved-changes flag from the undo
  history (`IHistoryService.CurrentNodeId` vs. a save/load baseline — undo back
  to the saved node reads clean; a pruned baseline stays dirty until the next
  save). Consumed by the UI's `ProjectSessionService` for the title-bar `*` and
  the window-close prompt; don't add parallel mutation tracking.

- **`MarkClean()` ENDS THE OPEN GESTURE, and that is load-bearing, not tidiness.**
  Deriving dirty from `CurrentNodeId` has one blind spot: a merged gesture tick
  rewrites the head action IN PLACE and does not move the current node. So a save
  landing mid-gesture (autosave, or Ctrl+S inside the 900 ms merge window) left
  every following tick invisible — the chart moved, the title-bar `*` stayed off,
  the close prompt never fired. The second half is worse: the node that stood for
  the on-disk state also ended up carrying the edits made after the save, so
  Ctrl+Z stepped straight PAST what was on disk. Ending the gesture costs at most
  one undo node. Pinned by
  `ProjectDirtyTrackerTests.ASaveMidGesture_DoesNotSwallowTheTicksThatFollowIt`.

- **An apply that THREW moves no node, so it read CLEAN over a damaged chart
  (0.61).** `HistoryService.Do` pushes the undo node only after `Execute`
  returns, so a `CompositeAction` child that threw halfway left the chart with
  the first N edits applied, the rest not, and no node describing any of it —
  and because dirty is derived from the node id, the title bar showed no `*` and
  closing the app never prompted. Two halves, each needed on its own:
  `CompositeAction.Execute` is **all-or-nothing** (it remembers how far it got
  and undoes exactly those children in reverse before rethrowing; a rollback that
  throws in turn rides out as an `AggregateException` beside the original,
  because at that point the chart state is genuinely unknown), and
  `IHistoryService.ActionFailed` fires from `Do`/`Undo`/`Redo` — the exception is
  still rethrown, this is a notification — with `ProjectDirtyTracker` subscribed
  and marking dirty. A spurious prompt costs a click; a missing one costs the
  work. `ProjectDirtyTrackerTests
  .AComposite_ThatThrowsMidApply_RevertsWhatItDid_LeavesTheHistoryAlone_AndReadsDIRTY`
  and `.AnUndo_ThatThrowsMidRevert_LeavesTheProjectDIRTY`.
- **`DirtyMarked` fires on EVERY `MarkDirty`, unlike the edge-triggered
  `IsDirtyChanged` — and crash recovery is why (0.61).**
  `CrashRecoveryService` armed its snapshot off `HistoryChanged` alone, so
  project-level state the history cannot represent (attaching or replacing the
  song audio, a checkpoint restore, a recovery restore, the new-project wizard —
  every one of them a `MarkDirty` caller) was never snapshotted at all: the app
  knew the project was dirty and still had nothing to recover. Subscribing to
  the EDGE would only half-fix it, because a second `MarkDirty` on an
  already-dirty project raises nothing — which is exactly the "edit, autosave,
  then attach audio" order. `ProjectSessionService` wires `DirtyMarked` to
  `ICrashRecoveryService.NotifyProjectChanged`; pinned by
  `ProjectDirtyTrackerTests.DirtyMarked_FiresOnEveryMarkDirty_EvenWhenAlreadyDirty`
  and `ProjectSessionSaveOutcomeTests.MarkProjectDirty_ArmsACrashRecoverySnapshot_EveryTime`.
- **…and `CleanMarked` is its mirror, for the same reason and against the
  opposite failure (0.62).** The owner's report was "sometimes it still asks to
  restore the project from loaded PSARCs even though there were no changes."
  A change SIGNAL is not a change: **every** replace path runs
  `ChartEditorViewModel.ApplyProject(resetHistory: true)`, which raises
  `HistoryChanged`, so merely OPENING a song armed the snapshot scheduler and one
  debounce (10 s) later the app wrote a full crash snapshot for a chart nobody
  had edited. Only `ProjectSessionService.LoadFile` ever disarmed it
  (`NotifyProjectSwitched` in its finally); `SongBrowserViewModel.LoadSongItem` —
  the path **Play Mode's song select** drives — called `MarkProjectClean()` and
  nothing else, so a library song left the scheduler armed AND the outgoing
  project's snapshot on disk. A PSARC session has `CurrentProjectPath == ""`, so
  that snapshot lands under `RecoverySnapshotStore.UntitledKey`, and
  `IsRecoverable` has no original file to date it against: **untitled is
  recoverable unconditionally.** Two halves, and the fix is at the DECLARATION so
  a new replace path gets it for free:
  - `ProjectDirtyTracker.CleanMarked` fires on every `MarkClean` (the edge is
    silent when the session was already clean — browsing one library song after
    another is exactly that case), and `ProjectSessionService` wires it to
    `ICrashRecoveryService.NotifyProjectSwitched`.
  - `ICrashRecoveryService.HasUnsavedChanges` is the **did-anything-change gate**
    the snapshotter never had: `ProjectSessionService` hands it
    `ProjectDirtyTracker.IsDirty` (the tracker is created there, so it cannot be
    injected), and `CrashRecoveryService.RunTickAsync` RESETS the scheduler rather
    than writing when it reads false. Left null the gate is open — old behaviour.
  Pinned by `ProjectDirtyTrackerTests.CleanMarked_FiresOnEveryMarkClean_EvenWhenAlreadyClean`,
  `ProjectSessionRecoveryWiringTests` (all three wirings) and
  `CrashRecoverySnapshotGateTests` (unedited PSARC ⇒ no snapshot; EDITED PSARC ⇒
  snapshot, still recoverable after a crash; clean shutdown ⇒ empty store).
- **A snapshot write that THREW re-arms the scheduler (0.62).**
  `CrashRecoveryService.WriteSnapshotAsync` calls
  `RecoverySnapshotScheduler.NotifySnapshotCaptured` BEFORE the write is
  attempted — deliberately, so edits arriving during the async write re-dirty
  the state. A write that threw therefore left `_dirty` false with nothing on
  disk, and every later tick short-circuited on `if (!_scheduler.IsDirty)
  return`: one transient failure (an AV or backup tool holding the recovery
  folder, a full disk, a roaming profile that blinked) silently switched crash
  recovery off for the rest of the editing burst, behind a single log line. The
  catch now calls `NotifyChanged`, which restarts the debounce — a bounded
  ~10 s retry while the work stays unsaved, not a spin.
  `CrashRecoverySnapshotGateTests.ASnapshotWriteThatThrew_IsRetriedOnTheNextDebounce`
  + `.ASnapshotThatSucceeded_DoesNotWriteAgainWithoutAnEdit`.
- **`RecoverySnapshotStore.WriteSnapshot` writes the SIDECAR before it renames
  the snapshot into place, and that order is load-bearing (0.62).** The file name
  is a one-way hash of the project path (`ComputeKey`), so `<key>.rscp.json` is
  the ONLY record of which project a `<key>.rscp` belongs to — and of the
  original, usually RELATIVE, audio reference, since the snapshot's own
  `AudioFileName` is rewritten absolute so it stays playable from the recovery
  folder. The sidecar used to be written second, so a crash between the two
  renames — the exact moment a crash snapshot exists for — left a visible
  snapshot with no metadata. `ListSessions` then surfaces it as UNTITLED
  (correctly: it has no path to show), the recovery dialog restores a titled
  project with Save pointing at nothing and the audio baked absolute, and
  `IsRecoverable` can never gate an untitled session, so it re-offers itself on
  every launch until it is discarded by hand. A failed sidecar sweeps the staged
  `.tmp` and rethrows, leaving the PREVIOUS (still described) snapshot standing.
  `RecoverySnapshotStoreTests.WriteSnapshot_WhoseSidecarFails_LeavesNoUnnamedSnapshot`
  + `.WriteSnapshot_ProducesASidecarThatNamesTheProject`.

## `State/HistoryService` — the undo TREE and the merge window

- The tree itself holds up under the node budget: `EnforceBudget` eats the
  coldest leaf OFF the current path first and only collapses the root when the
  tree is a pure chain, so `_current` is never pruned, `CurrentNodeId` never
  dangles, and `GetTree()` keeps exactly one root with no orphan parents.
  `NavigateTo` walks to the common ancestor and back down either branch.
  Verified, unchanged.
- **There is a BYTE budget beside the node cap (#328),** `DefaultMaxBytes` =
  256 MiB, fed by `IChartAction.EstimatedBytes` (0 for an ordinary edit; a
  whole-project `ProjectSnapshotAction` reports its two clones through
  `Core/Utilities/ChartProjectFootprint`, a per-element estimate measured
  against real clones; a `CompositeAction` sums its children). A power-tool step
  on a base-game song imported with every level holds 20-67 MiB, so the node cap
  alone let a session pin gigabytes. Over budget, byte-holding cold leaves go
  first, then the oldest steps collapse; the CURRENT step is never collapsed for
  bytes, and the loop stops once every byte left is its own (collapsing
  weightless edits would free nothing). `HistoryByteBudgetTests`; the estimate is
  held to a measured clone (±25 %) there. A new action that keeps a large graph
  alive must report it.
- **A tool that changes ONE arrangement snapshots ONE arrangement (#370):**
  `ChartMaintenanceService.ArrangementSnapshotAction` (Refret, GenerateLevels,
  SquashLevels) clones the arrangement twice and reconciles through
  `ChartProjectRestorer.RestoreArrangement`, cutting the step by the arrangement
  count. A tool that also moves the beat grid or adds/removes arrangements keeps
  the whole-project `ProjectSnapshotAction`. Moving a tool to the scoped action
  means adding it to `ArrangementSnapshotUndoTests`, which deep-compares the WHOLE
  project across do/undo/redo beside an arrangement the tool must not touch.
- **Every committed edit bumps the revision (`Arrangement.MarkEdited`, read by
  `GetRevision()`) of each arrangement it can
  have changed (#720)** — do, a merged gesture tick (it moves no node), a direct
  `PushAction`, undo, redo, and an apply that threw. `State/ChartEditRevisions`
  decides which, erring WIDE: a collection action (`ICollectionChartAction`: the
  add/remove/mutate actions) marks the arrangement owning its collection, plus the
  scope's arrangement when it has a post-execute pass; a `CompositeAction` marks
  what its children mark; anything else, or a collection no arrangement owns,
  marks them all. `ChartProjectRestorer.RestoreArrangement` bumps too. The quality
  report's spare copy skips an arrangement whose revision has not moved
  (`QualityReportCopies`), so a missed bump is a STALE report.
  `ChartEditRevisionCoverageTests` walks every collection of an arrangement by
  reflection (edit, undo, redo must bump that arrangement and no other) and fails
  a collection-action subclass that adds its own `Execute`/`Undo`; a new
  `IChartAction` needs nothing (it is marked wide).
- **The merge window is a wall-clock SUBTRACTION, so a NEGATIVE gap closes it too.**
  `DateTime.UtcNow` is not monotonic: an NTP correction, a VM resume or a user
  fixing the system clock moves it backwards mid-session, and a bare
  `elapsed > window` reads that as "no time has passed" — the window stops
  closing and two edits made an hour apart fold into ONE undo node, silently, in
  the direction that loses a step. Refusing to merge is always safe (one extra
  node, never correctness). `HistoryGestureMergeTests
  .AClockThatSteppedBackwardsDoesNotReopenAClosedGesture`.
- **Node timestamps come from the SERVICE's clock, not ambient `DateTime.UtcNow`.**
  The clock is injectable so the type is deterministic under test; a node reading
  the ambient time made the one field the history UI displays
  (`HistoryNodeSnapshot.TimestampUtc`, `HistoryStepInfo.TimestampUtc`) the one
  field no test could assert.

