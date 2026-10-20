# Project lifecycle — open / replace / dirty / audio / shutdown (UI deep reference)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before adding any
path that opens, replaces, imports or restores a project, before touching
`MainWindow.OnClosing`, and before adding an audio-attach path.** Losing the
user's unsaved work is the failure mode every rule here exists to prevent.

## The three exhaustive chart traversals

- **The coordinators (in Services since wave 0: `Editing/`, `Editing/Maintenance/`, `Session/`) hold three EXHAUSTIVE chart traversals, and all three used
  to be written by hand (0.41).** The tempo-map tools' position walks
  (`ChartMaintenanceService.TempoMap`) now delegate to
  `Core/Utilities/ChartProjectPositions` — the ONE walk, with the "a new
  position-bearing element class MUST be added here" rule and a reflection guard
  behind it (see `DotCharter.Core/AGENTS.md`); both hand-written copies had
  omitted `ChartLevel.FretHandMutes` and `Arrangement.NotationVoices`, so Insert
  Beat / Remove Beat / Add Beats At Start silently desynchronised every fret-hand
  mute and every imported GP voice 2-4. Its sibling `RemoveContentAtBeat` stays
  separate (a remove is not a remap) and carries the same obligation.
  `GlobalEditingCoordinator.DeleteSelectedRelated` is the third: hand-shape
  containment is `HandShape.Covers` (start inclusive, end **exclusive**), never an
  open-coded `<=` — shapes abut, so the closed reading made an emptied shape look
  occupied. **Note the spacing enforcer hides this in a default-config test**: it
  pulls a shape's end back off the next onset in every collection action's
  post-pass, so `DeleteSelectedRelatedTests` turns min-note-distance OFF to reach
  the defect at all.

## A project switch must FORGET the project it closed

Measured 2026-08-28, after the owner reported ~2.2 GB of working set from two sequential
opens. The native audio side is bounded — see
[../services/audio-engine.md](../../services/audio-engine.md) § *Preloaded audio* for the
numbers — but two app-wide SINGLETONS kept the closed project alive, and both had the
same shape: a collection that is only ever added to, on a service that outlives every
project.

- **`Services/Editing/Tracks/TrackContextRegistry` keys a `TrackEditorContext` off the
  `Arrangement` INSTANCE and had no remove.** So every arrangement of every project the
  session ever opened stayed reachable — levels, notes, chords, hand shapes, anchors,
  chord templates, event points — for the life of the process. What hid it is that
  `SyncProjectOnAllContexts` walked the dead contexts on every switch and repointed their
  `Project` at the incoming one: they went on looking live while `_ordered` grew linearly
  with opens. `DropContextsOutsideTheOpenProject` now runs on the `ActiveProject` /
  `ActiveArrangement` notification. **`SyncWithActiveArrangement` has to skip an
  arrangement that is not in the open project** — `ApplyProject` publishes `ActiveProject`
  BEFORE `ActiveArrangement`, so on the first notification the active arrangement is still
  the CLOSED project's, and focusing it would re-create the context the prune just
  dropped. Pinned by `UI/TrackContextRegistryProjectSwitchTests`, whose load-bearing case
  is the WeakReference one: the count assertions are satisfied by a registry that merely
  stops adding, while a stale KEY still pins the graph.
- **`Services/Audio/Coordination/StemWaveformCoordinator` caches a peak pyramid per stem PATH**, and only
  `StemMixerViewModel` resets it — from one of its four clear sites. The no-project and
  no-stems early returns of `RebuildOnceAsync` cleared the engine's stems and left the
  cache, so opening a stemmed project and then an un-stemmed one stranded the pyramids.
  It was a correctness bug too: the coordinator does `_fileWaveform ??= _state.WaveformData`,
  so project B drew project A's waveform. All four sites now go through
  `ClearStemsAndTheirWaveforms`, and `UI/StemWaveformCacheLifetimeTests` fails on a bare
  `_audio.ClearStems()` anywhere else in that type.

Two more of the same shape were found on 2026-08-28 and are fixed; both are pinned by
`UI/ProjectSwitchForgetsTheClosedProjectTests`.

- **`Editing/MultiTrack/GhostArrangementsService` + `StackedArrangementsService` are
  `HashSet<Arrangement>` singletons whose `Clear()` had no caller on any
  project-replace path** — only `Editing/Tracks/TimelineSolo`. `Arrangement` declares no
  `Equals`/`GetHashCode`, so those are reference keys and every arrangement the session
  ever ghosted or stacked stayed pinned with its chart graph. **Ghosts were a visible
  correctness bug too**: `ChartRenderer` walks `VisibleGhosts` directly and reads
  `Levels[0].Notes` off each entry, so the CLOSED project's notes were drawn over the open
  project's timeline. `MultiTrackOverlayViewModel.OnActiveProjectReplaced` now clears both
  on the `ActiveProject` notification — **and drops Solo with them**, because Solo
  remembers what it hid in order to put it back, and across a switch that is the closed
  project's arrangements.
- **`ChartEditorViewModel.ApplyProject` guarded the `ActiveArrangement` assignment with
  `if (project.Arrangements.Any())`, which was a data-corruption bug, not a safety check.**
  `ProjectState.ActiveArrangement`'s setter is what cascades `ActiveLevel`, and
  `ChartDocumentState.PlacedNotes/PlacedChords/HandShapes/Anchors` plus `IEditingScope` all
  resolve off `ActiveLevel` — so a project with no arrangements never entered the setter
  and left BOTH pointing into the project just closed. Every edit after such a switch
  landed in the closed project's level while the new one was on screen. That setter's own
  comment documents this exact failure as fixed one layer down; the guard reintroduced it
  from above. It is now `project.Arrangements.FirstOrDefault()`, unconditionally. A
  zero-arrangement project is reachable: `XmlChartSerializer` handles vocals-only RS XML
  explicitly.

- **Both GLYPH SHEETS were the same shape, and there it is a CORRECTNESS bug before it
  is a memory one** (2026-08-29). `Rendering/Text/ChartFontAtlas` (2D) and
  `Rendering/Vulkan/FontAtlas` (3D) each keep a `HashSet<char>` of everything they have
  been asked to draw, and it was only ever ADDED to — every lyric, section name, phrase
  name and tone name of every song opened in the session, on objects that outlive a
  project (`Fretboard3DEngine` is an app-wide DI singleton). The sheet is a fixed 2048²;
  a glyph that no longer fits is SKIPPED, and the batcher draws an absent `GlyphMap`
  entry as zero-width — so a long session silently starts losing text on the highway.
  The only report of that was a `Debug.WriteLine`, which is compiled OUT of a Release
  build. Both now expose `ResetToSeedCharacters()`, driven from the `ActiveProject`
  notification in `ChartRenderer.OnProjectStateChanged` and
  `Preview3DRenderDriver.OnProjectStateChanged` (the 2D side also clears
  `_lastRegisterSig`, or the new project's text is never re-registered), and the overflow
  warning now reaches `Console.Error` as well. Pinned by
  `Rendering/FontAtlasProjectSwitchTests` — including a source lint that both renderers
  still make the call, because neither control can be driven headlessly.

**Three more of the same shape, and the fix was NOT a null at a lifecycle point** (`8d20b44c`,
2026-08-28). `RocksmithExportDialogViewModel._lastOpenedProject`,
`ToneDesignerDialogViewModel._project` and `RsTonePlaybackCoordinator._preparedArrangement`
(plus `_previewArrangement` one line down) each held a whole project/arrangement for the life
of the process. Every one exists to answer *"is this the same project as last time"*, so
CLEARING it makes the next open look like a switch — the export dialog would throw away the
DLC key and album art the user just set, the tone designer would drop its imported-tone list,
and `EndRun` (reachable from the `UseInGameTones` toggle, which stops and restarts a run with
no second prepare) would make the rest of the song play dry. So each became a
`WeakReference<T>` read back through a small property: **identity without ownership**. What is
handed in is always the ACTIVE project, so the target resolves for exactly as long as it is
the loaded one, and a collected target can never be the one being opened now — "changed" is
the right answer either way.

**The general rule: a singleton that stores anything keyed by a project's OBJECT GRAPH
needs an eviction rule, and the test that proves it is a `WeakReference` over the closed
project — not a count.** `ChartEditorViewModel.ApplyProject` already clears the two that
were found earlier (undo history, `ISelectionService`) and its remarks say why. When the
field is there for IDENTITY rather than for use, the eviction rule is a weak reference, not
a clear — see the three above.

### The measured floor, and the five-cycle table

`UI/ProjectSwitchCycleMemoryTests` is the integration over all of this: it wires the
singleton set the composition root wires (`ProjectState`, `SelectionService`,
`TrackContextRegistry`, `ChartEditorViewModel`, `MultiTrackOverlayViewModel`, the ghost and
stacked services, `StemWaveformCoordinator`) and runs open ▸ realize tracks ▸ ghost/stack ▸
walk the playhead ▸ switch, five times, then asserts every closed project is collectable.
One case is synthetic so it runs everywhere; the other loads the five largest real
`.rscp` files from `TestData/RsCharter` and skips without the corpus. **The bytes are LOGGED,
never asserted** — a heap figure under a parallel gate is not deterministic.

Measured 2026-08-29 (Debug, worktree, the real-corpus case; each row taken with that cycle's
project still open):

| cycle | song | sounds | managed heap | private bytes | gen2 |
|---|---|---|---|---|---|
| 0 | Yorushika – Odorouze | 4,487 | 2.6 MiB | 34.1 MiB | 3 |
| 1 | Yorushika – Fireworks Beneath My Shoes | 3,788 | 2.4 MiB | 35.2 MiB | 6 |
| 2 | Yorushika – Replicant | 3,079 | 2.2 MiB | 35.2 MiB | 9 |
| 3 | Yorushika – Matasaburo | 2,942 | 2.3 MiB | 35.2 MiB | 12 |
| 4 | Yorushika – Yu, Sansan | 3,648 | 2.4 MiB | 35.2 MiB | 15 |
| — | after the closing switch + collect | — | **0.6 MiB** | 33.3 MiB | 18 |

**Flat, and that is the finding.** Three things follow, and they close the "working set
1,347 MB at v0.43.2" question that stood open on the roadmap:

- **A whole real chart graph is ~2 MiB of MANAGED heap**, and it is fully released on the
  next open. Five cycles ended 0.6 MiB above an empty heap.
- **So the managed side CANNOT be a gigabyte.** Even a total leak of every project a long
  session opens is tens of MiB. A working set in the hundreds of MB is the native audio
  floor — ~10.6 MB per decoded stereo minute, so ~106 MB for a 5-minute stem and ~740 MB
  resting for a 6-stem chart plus its song, by design since v0.48 (see
  [../services/audio-engine.md](../../services/audio-engine.md) § *Preloaded audio*) — plus
  Skia/Vulkan and the library's own bounded caches (`SongCoverCache.Shared`, 64 MB LRU).
  **Do not go looking for a managed leak to explain a number of that size.**
- The test is the gate for everything above it: a singleton that starts pinning the closed
  project fails it on the next run, and adding such a singleton to `SwitchRig` is what makes
  it covered.

### Two that survive a switch WITHOUT being in that rig

Both were found by the same sweep and are fixed; neither is reachable from a plain view
model, which is why the cycle test cannot see them.

- **`Controls/Preview3DRenderDriver`'s font-atlas latch held the chart's text collections
  strongly.** It re-registers the atlas only when the lyric/event collections change by
  instance or count — and `ChartEditorViewModel.LyricPhrases` is the open vocal track's own
  `Phrases`, `ProjectState.EventPoints` the active arrangement's own collection. The driver
  lives as long as the 3D canvas, so it kept every lyric word and every section marker of
  every song the session previewed. It is `Controls/ChartTextRegistrationLatch` now — the
  same weak-identity rule, extracted so it can be tested without a canvas
  (`UI/ChartTextRegistrationLatchTests`, five cases: the four behaviours the latch exists
  for plus the collectability probe).
- **`LanScoreboardViewModel`'s unbounded avatar cache** went with the LAN scoreboard on
  2026-09-19; `BoundedLruCache<T>` stays for the next cache of that shape.

### One residual, and it is NOT a memory problem — read this before "fixing" it

`DotCharter.Rendering/Vulkan/FontAtlas`'s `_charsToRender` is only ever ADDED to
(`RegisterCharacters`), and nothing narrows it back to the open chart. **That is not a
leak**: the sheet is a FIXED 2048×2048 RGBA texture regardless of how many characters are in
the set, and `RebuildIfNeeded` disposes the old texture before uploading the new one.

What it can cost instead is CORRECTNESS on a long CJK session. `Fretboard3DEngine
.PreRegisterChartText` registers every lyric syllable and every section/phrase name of each
chart opened, the set accumulates across the session, and `FontAtlas.Rasterize` `break`s out
of its packing loop once a glyph would fall past the sheet's bottom edge — absent glyphs
fall back to `?`. So a session that opens enough Japanese charts could start drawing `?` for
a LATER song's lyrics because an earlier song's kanji are still in the set. **This has NOT
been reproduced** — it is a reading of the code, not a measurement. Before acting on it,
count the real glyphs: the fix (narrow the set on switch, then rebuild) costs a full
rasterize plus a `WaitDeviceIdle` at exactly the moment the switch is trying to stay inside
the 150 ms budget, and would have to account for every other registrant — the per-frame
visible scan, HUD labels, the debug text.

