# Rendering / perf — what an EDIT costs on a big chart

Part of the rendering/perf reference. Router: [../rendering-perf-detail.md](../rendering-perf-detail.md).

The other files here are about a FRAME. This one is about a GESTURE — a delete, a
Ctrl+Z, a technique toggle, an arrow key — on a 10k–20k-sound chart. Those land
on the UI thread between the keypress and the next frame, so they are held to the
same ≤150 ms budget as a view switch ([responsiveness-budget.md](responsiveness-budget.md)),
and in v0.60 four of them were over it.

**The frame path was already fine and is not the subject here.** `FrameSnapshot`
is viewport-bound, allocation-free warm, and skips its selection re-hash on an
unchanged `SelectionVersion` — pinned by `FrameSnapshotPerfGateTests` and
`FrameSnapshotSelectionVersionTests`. Everything below is the EDIT side.

## How to re-measure

`Perf/EditorLatencyProfileTests` — env-gated, asserts nothing, prints
`[PerfHarness]` lines:

```powershell
$env:DOTCHARTER_PERF_GATE = "1"
dotnet test DotCharter.Tests\DotCharter.Tests.csproj --no-build `
  --filter EditorLatencyProfile --logger "console;verbosity=detailed"
```

It builds a live editing stack (real `ProjectState`, real history, real selection
service, the real spacing enforcer behind the highway service's post-pass) over a
`StressChartFactory` chart, and **flags the downbeats** — a grid with no
`IsMeasure` makes the enforcer's measure lookup O(beats) per sound and the number
stops describing any chart a charter has (it read 328 ms instead of 47 that way).

## Measured, 18,000 notes + 2,000 chords unless stated

Debug build on the owner's box **with other agents building alongside**, which
this box does badly: the same unchanged code path read 47 ms in one run and
140 ms in another. **The wall clocks below are the lowest of several runs and are
directional; the ALLOCATION figures are deterministic and are the ones to
compare.** Re-measure on an idle box before quoting either as a product number.

| Gesture | Before | After |
|---|---|---|
| Delete 5,000 of 18,000 notes + Ctrl+Z (collection work only) | **989 ms** | **21.6 ms** |
| …the same at 1,000 of 4,500 | 44.4 ms | 1.5 ms |
| …including the spacing post-pass | 1,127 ms | 96 ms |
| One technique verb over 5,000 selected notes | 79 ms · **9.16 MB** | 84 ms · **2.93 MB** |
| Spacing post-pass alone (runs on EVERY committed edit) | 47 ms · **80,346 B** | 47 ms · **266 B** |
| Arrow-key next/prev sound | 2.19 ms · 184 B | **0.002 ms · 0 B** |
| Inspector tri-state chip fold, 5,000 selected (×2 per edit) | 0.91 ms · 40,336 B | **0.38 ms · 0 B** |
| Replace a 5,000-note selection | 0.26 ms, **20,004** notifications | 0.09 ms, **1** notification |

## The five shapes, and where each one lives

Every one of these is "whole rebuild where a delta would do" or "O(n) helper
called O(n) times". None of them was a rendering fault.

- **A bulk collection edit was O(k·n).** `RemoveElementsAction` spelled its bulk
  half `foreach (item) Collection.Remove(item)` — an `IndexOf` scan per item —
  and `AddElementsAction` spelled its own `foreach (item) InsertSorted(item)`,
  which walks from index 0 per item. `CollectionActionBase.RemoveEach` resolves
  each item's CURRENT index from a map plus a Fenwick count of what has already
  gone; `InsertEachSorted` checks ONCE that the collection is ordered by position
  (the precondition under which the walk and a binary lower-bound agree by
  definition) and then binary searches. Both refuse, before mutating anything, on
  input the loop would answer differently — an absent item, a repeated one, an
  unordered collection — and fall back to the loop.
  `BulkCollectionEditEquivalenceTests` pins them against verbatim copies of those
  loops: same sequence by reference, same `CollectionChanged` script (action,
  item, index, in order).
- **Every keyboard verb formatted the whole selection into a log line.**
  `IAppLogger.Info` takes an already-interpolated `string`, so
  `HighwayEditingService.RunMutation`'s `string.Join(…SelectedNotes.Select(FormatNote))`
  ran before the logger was asked whether it wanted it — 9.16 MB per keypress at
  5,000 selected. Capped at 20 plus an "…and N more" tail; the drag-commit path,
  which wrote one LINE per sound, is capped the same way. The cap and the
  `Describe` helper are `Services/Editing/EditLogDetail` now, because the same
  fault sat outside the highway service too. **The one-line-per-item spelling is
  the same bug and it is the one that survives a sweep** —
  `AdjustSelectedNotesSustain` and `AdjustSelectedAnchors` each wrote a line per
  selected item, and `]`/`[` is ALSO the timeline mouse wheel
  (`TimelineWheelAdjuster`), so ONE notch over a whole-level selection was one
  interpolated line per sound on the UI thread; `EventEditingService
  .RemoveEventPoints` was a bare `string.Join` over the whole removal. All three
  go through `EditLogDetail` now.
  `LargeSelectionEditCostTests` is a `[Theory]` over the verbs (accent toggle,
  sustain adjust, anchor nudge) and fails if the line grows with the selection or
  if a verb starts writing an unbounded number of lines; the event-point removal
  is pinned by `EventRemovalSelectionTests.RemoveEventPoints_LogLineIsCapped`.
- **The post-drag reselection added one item at a time.** Every `Selected*`
  collection has a handler that re-reads the WHOLE selection, so that is
  quadratic — the shape `SelectionBulkOps`' own summary says "has been
  reintroduced three times"; the drag commit was the fourth and
  `EditTargetService.Promote`'s `if (!Contains(x)) Add(x)` was the fifth. Both go
  through `SelectionBulkOps` now.
- **The spacing enforcer re-derived one constant per sound.**
  `ChartSpacingEnforcer` runs over the whole active level after EVERY committed
  edit, and its sweep called `GetMinDistance` once per sound. In the two FRACTION
  modes that answer depends only on which MEASURE the sound is in, so on a 4/4
  chart it is one value for the level — behind a one-slot cache keyed on the beat
  now (`MinDistanceMemo`; Milliseconds mode keys on the whole position, because it
  genuinely varies inside a beat). `ChartSpacingEnforcerTests
  .EnforceLevel_UsesEachMeasuresOwnMinDistance_WhenTheMeterChanges` is the guard,
  and it changes METER on purpose: a cache that assumed one answer per level is
  invisible on the 4/4 chart every other test in that class uses.
- **Arrow-key seek sorted the whole chart.** The four seek/select verbs each ran
  `PlacedNotes.Where(time > cursor).OrderBy(n => n.Position).FirstOrDefault()`
  over an already-sorted list. `TimelineMath.FirstAfter` / `LastBefore` are the
  shared answer. **`LastBefore` walks back over the equal-time run and that is
  behaviour, not tidiness** — `OrderByDescending` is STABLE, so among sounds
  sharing the greatest passing position it returned the FIRST in list order, the
  low string of a chord rather than the high one. `TimelineScanEquivalenceTests`
  sweeps both against the LINQ shapes at every element's own time.
  **The sheet strip's click-to-select was the FIFTH and it was missed**, because
  it is not spelled as a LINQ sort: `ChartEditorViewModel.SelectNearestToTime`
  ran two hand-written full passes with a `GetPositionMs` per element, over the
  same already-sorted lists. `TimelineMath.NearestTo` is the shared answer, and
  it carries both of the scan's tie-breaks (earlier wins on a tie; first in list
  order within an equal-time run) — dropping the second fails 5 of that class's
  13 cases.
- **The inspector folded the selection eight times per sync, twice per edit.**
  Seven `AllSameNullable` walks over a `SelectedNotes.ToList()` snapshot (five and
  a copy on the chord side) — and the sync runs on BOTH signals, because the
  selection service re-derives its scalar `SelectedNote` from inside its own
  `CollectionChanged` handler. `NoteFlagAgreement.Over` / `ChordFlagAgreement.Over`
  do all of them in one walk with no snapshot; `InspectorFlagAgreementTests`
  compares the fold to `AllSameNullable` itself.
- **The work queue rebuilt its whole work set on a tab nobody was looking at.**
  `SoundQueueViewModel.Refresh` is a `Cast+Concat+OrderBy+ToList` over every
  sound plus seven filter passes, and `RebuildIssueGroups` called it
  unconditionally on the inspector's 150 ms debounce — while the very next lines
  deferred the FEHLER groups on exactly the reasoning that applies here too:
  everything the queue feeds is bound by the Sound tab (`SoundInspectorView.axaml`
  and its `SoundTaskView.axaml`) and nothing else. It takes the same stale flag now (`MarkStale` / `EnsureCurrent` /
  `IsStale`, brought current by `OnSelectedTabIndexChanged`). **The keybinds are
  what makes this more than a copy of the groups' rule**: `QueuePrevMatch` /
  `QueueNextMatch` fire from ANY tab, so `Step` and `TriageFilter` settle a
  stale queue themselves — a deferral may not change what a gesture does.
  Its filter list also went `Clear()` + seven `Add`s per refresh, which is eight
  `CollectionChanged` notifications into a bound `ComboBox` **and** re-entered
  `OnSelectedFilterChanged`, because the options were value-equal RECORDS and a
  changed count made `SelectedFilter` unequal to itself.
  `SoundQueueFilterOption` is an `ObservableObject` with a settable `Count` and
  `Label` now, one stable instance per filter, so a refresh raises nothing on
  the collection and only `Display` changes. `SoundQueueViewModelTests
  .ARefresh_DoesNotRebuildTheFilterList`,
  `.AChangedCount_RaisesDisplayOnTheOption`,
  `.AStaleQueue_DoesNoWorkUntilItIsBroughtCurrent`,
  `.SteppingFromAStaleQueue_SettlesItFirst` and
  `.TheInspectorDefersTheQueueRefreshUntilTheSoundTabIsShowing` are the guards.
- **The sheet strip re-rendered on edits the notation does not show.**
  `SheetMusicView.RebuildAsync` runs on every `HistoryChanged` while the strip is
  open (behind the 250 ms debounce), and a great many committed edits change
  nothing AlphaTex carries — an `Ignore`/`PassOtherNotes` toggle has no glyph at
  all. The EXPORT has to run either way and stays on the UI thread; the AlphaTab
  layout + AlphaSkia raster behind it do not. `Services/Export/Sheet/SheetRenderKey` folds the
  exported string together with **the geometry** — width, height, DPI, zoom,
  page/strip mode, staves mode — and an unchanged key keeps the chunks that are
  already on the canvas (the cursor and the selection highlights are overlays,
  so they still follow). **The geometry is why this is not a one-line string
  compare**: `RebuildAsync` is the single method a resize, a zoom, a DPI change
  and a layout flip all go through, so a key of the AlphaTex alone would stop
  the strip re-rendering when the pane is dragged wider. The key is recorded
  ONLY on the path that put chunks on the canvas, and cleared on every failure,
  so a skip can never stand in for a render that did not happen. The cancel of
  the in-flight render moved BELOW the check for the same reason. Pinned by
  `AlphaTexExportStabilityTests` — the exporter is byte-deterministic, blind to
  the fields it does not emit but not blind to a fret change, and
  `ShouldRerender` is swept over every input one at a time.

## What a SELECTION change and a Play Mode ENTRY cost

The section above is about a keyboard verb. These are the two gestures beside it —
clicking a note, and opening a Play Mode screen. Same budget, same shapes.

Re-measure the Play Mode half with `Perf/PlayModeEntryProfileTests` (env-gated on
`DOTCHARTER_PERF_GATE=1`, asserts nothing, prints `[PerfHarness]`). Measured Debug
on a 200-arrangement × 6-run × 14-section practice store with a 2,160-song
library:

| Called on every… | Cost per call |
|---|---|
| `SessionHistory.BuildGroups` — was the front door's newest-run read (removed 2026-09-19 with the History screen) | 1.222 ms · 1,473,714 B |
| …plus the `SelectMany` + `OrderByDescending` that stood on top of it | **1.281 ms · 1,481,906 B** |

The probe now times `GameplayStatsService.GetMostRecentSession` alone; the
rows above are the historical before-figure.

- **A `Clear()` that removes nothing was a full selection fan-out.**
  `ObservableCollection.ClearItems` raises `Reset` unconditionally, and each of
  `SelectionService`'s six collections raises the coarse `SelectionChanged` from
  its own `CollectionChanged` — so `ClearSelections()` on an empty selection ran
  every subscriber SIX times, and `HighwayInteractionTool`'s plain-click path
  cleared four collections of which hand shapes and anchors are empty on an
  ordinary note click. `BulkObservableCollection.ClearItems` early-outs now, the
  same no-change rule `ResetWith` always had. `BulkObservableCollectionTests` and
  `SelectionServiceTests` pin both directions — an empty clear raises nothing, a
  real one still raises exactly one `Reset` plus `Count` and `Item[]`.
- **Both inspector SUMMARY getters are bound, so each is re-evaluated on every
  `NotifySelectionKindChanged`** — which fires from all three syncs, several
  times per click. `SelectionShapeSummary` + `BendSummary` made FOUR passes over
  the selection per evaluation and allocated a `string.Join` PER SELECTED CHORD
  for the distinct-shape count. `Inspector/SelectionShapeFold.Over` is one pass;
  the shape identity packs into a `ulong` (`fret + 1` in the 7-bit slot of its
  string, so an open string is distinguishable from an absent one and the pack is
  order-independent like the sorted spelling was) and **refuses** what it cannot
  hold exactly — a string past slot 8, a fret past 126, a repeated string that
  would OR into its own slot — falling back to the original spelling.
  `SelectionShapeFoldTests` sweeps it against verbatim copies of all four
  expressions, including 400 random selections. **The repeated-string guard needs
  its own case**: most repeated-string grips survive the OR by luck, and the first
  attempt to prove the guard non-vacuous passed without it.
- **Three bound properties read two O(CHART) hand-shape counts**, so one
  hand-shape selection change ran three whole-level walks. Folded once in
  `HandShapeEditorViewModel.SyncHandShapeProperties`, which is the ONLY raiser of all five — so a value
  cached at the raise is exactly as fresh as one computed at the read.
  `UI/InspectorHandShapeCostTests`.
- **`SongOverviewViewModel.Refresh` runs on every `HistoryChanged`** and
  republished three bound `ObservableCollection`s (`Clear()` + one `Add` per
  distinct name) plus fifteen `Replace` into the key `ComboBox`. A note edit
  changes no tone/section/phrase name and no count, so every one of those
  rebuilds published the identical list — and it threw away a half-typed rename,
  because the row's rename text is two-way bound and the row was replaced.
  Both are no-change-guarded: `MarkerGroupsViewModel.Refresh` compares each row's
  name, warning and place labels, and deliberately NOT `EditName` or `Error`.
  `UI/SongOverviewRefreshCostTests`.
- **The sheet strip's time → measure lookup was a scan from bar 1, in four
  places**, run on every coalesced playhead publish (≤60 Hz) while the strip is
  visible. `SheetMusicView.SpanIndexAt` is a monotonic cursor that clamps and
  walks BOTH directions, so it self-corrects after a seek — the fourth such
  cursor on this clock, after `Audio/Coordination/PlaybackMarkerCursor`,
  `StatusBarViewModel._measureScanIndex` and `RsToneScanner`.
  `UI/SheetCursorSpanScanTests` sweeps it against a verbatim copy of the loop.
- **Play Mode: the front door projected the whole practice history to read ONE
  run.** `IGameplayStatsService.GetMostRecentSession()` walks the store once and
  allocates one record. The whole-history projection it replaced (1.48 MB
  above) was removed with the History screen on 2026-09-19 — restore from tag
  `pre-cut-2026-09`. Entering the Menu is the app's boot surface under
  `AppConfig.StartInPlayMode`, and Esc from Song Select lands there too.
- The practice-plan rebuild cost (7.2 ms / 1.24 MB per generation, pinned by a
  generations-per-gesture test) left with the practice plan on 2026-09-19.
  `UI/ViewModels/PlayMode/FrontDoorLastSessionReadTests` keeps the
  one-run-read guard above.
- **The section/phrase readout walked BACK to the nearest named event on every
  60 Hz publish** (#309) — to the start of the song on a phrases-only chart.
  `DotCharter.Services/Audio/Coordination/PlaybackMarkerCursor` keeps ONE cursor
  (the two always agreed) and latches the two owner indices: walked again only
  when the cursor crosses an event, the list changes identity or count, or
  250 ms have passed. The owner's NAME is read fresh, so an in-place rename shows
  at once; the 250 ms bound covers the case that stopped the 2026-09-04 attempt
  (a section ADDED in place to an event between the owner and the cursor, which
  raises nothing) without a `HistoryChanged` dependency.
  `Services/Audio/Coordination/PlaybackMarkerCursorTests`.
- **The minimap's SELECTION path had no throttle** (#309) while its playhead path
  went through `RepaintThrottle`; a marquee repainted the whole map and walked the
  selection on every pointer move. `Controls/TrailingRepaintGate` gives it the
  same 33 ms interval WITH a trailing edge (one repaint scheduled for the end of
  the interval), so the last change of a burst always draws and a lone change
  still repaints at once. `UI/TrailingRepaintGateTests`.

## Measured and deliberately LEFT

Verified 2026-09-04 and NOT fixed, each with the reason:

- **`HighwayInteractionTool` runs `SelectedChords.Contains` / `SelectedNotes
  .Contains` on every left click** (and again per right click, and per arrow key
  in `ChartEditorViewModel`) — a linear scan of a 20,000-item collection.
  Measured shape only: ~20 µs of reference compares, well inside the budget, and
  the fix is a HashSet mirror in the selection service, i.e. a second source of
  truth for what is selected. Left on purpose.
- **The double-fire is still there.** One selection edit still runs
  `SyncNoteProperties` twice — once off `SelectedNote`'s `PropertyChanged`, once
  off `SelectedNotes.CollectionChanged`. Neither subscription is redundant on its
  own (`SelectedNote` can move without the collection changing, and vice versa),
  and `SelectionVersion` cannot dedupe them because the service bumps it AFTER it
  sets the scalar — so the two calls legitimately see different versions. The
  per-call cost is now small enough that the remaining fix is a correctness-risky
  event-topology change for a ~0.4 ms saving.
- **The spacing post-pass is still O(n log n) over the WHOLE level per edit** —
  an `Array.Sort` of every sound plus a full backward sweep, for an edit that
  touched one note. A windowed pass is the real fix and a large one: the
  enforcer's contract is that the whole level is self-consistent afterwards, and
  `SpacingCollateral` exists because it legitimately edits NEIGHBOURS.
- **A power tool still clones the whole project TWICE.**
  `ChartMaintenanceService.Refret` takes a `before` and an `after`
  `ChartProjectCloner.Clone` and hands both to a `ProjectSnapshotAction`, whose
  Undo/Redo reconciles the entire live graph — synchronously on the UI thread,
  unlike `GenerateLevelsAsync`, which offloads its middle phase. Not measured
  here; `Perf/RefretPerfGateTests` covers the refret ENGINE, not the clone pair
  around it.
- **Sheet music's EXPORT is still a full-level pass on the UI thread before the
  first `await`, and it stays there** (`SheetMusicView.RebuildAsync`) — it reads
  the live chart graph, so an edit landing under a worker would tear it. The
  250 ms debounce and the effective-visibility gate are the only things in front
  of it. What no longer follows it is the expensive half: see the render-key
  bullet above.
