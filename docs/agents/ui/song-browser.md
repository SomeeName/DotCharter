# Song browser + Play Mode Song Library (UI deep reference)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before touching
the Library (`Views/Library/LibraryView`) or Play Mode's Song Library** — they are
ONE screen in two places and the shared half is a real type, not a convention.

**The Library is on the target architecture since 2026-09-23 (wave 5).** The shell's
`SongBrowserHost` holds `Views/Library/LibraryView`, bound to
`Presentation/Contracts/Library/ILibraryScreen` through `ViewModels/Panels/LibraryScreen`,
an adapter over `SongBrowserViewModel`: the scan, the coalesced filter pass, the cover
cache, the load and its guard stay in the view model and are tested there, and the adapter
only changes how they are presented. Its rows (`LibraryRow`, `LibraryScreenParts.cs`) are
one wrapper per song, and the row list mirrors `Songs` edit for edit — a scan appends one
batch at a time, and a re-bound list per add would reset the ListBox thousands of times.
The view talks back through `ILibraryViewHooks` (covers for the rows on screen, filter
counts when a funnel opens). The DETAIL rail's cover is the adapter's own job: the scan
warms only the first 20 covers and the row hook only fires for rows on screen, so
`LibraryScreen` asks for the selected song's cover on every selection change AND in its
constructor, for a selection restored before the screen was built (issue #36,
`LibraryScreenTests.ASongSelectedBeforeTheScreenIsBuilt_…`). `Views/Panels/SongBrowserView` is deleted; the rules below
that name it describe the same behaviour on `LibraryView` (`LibraryScreenTests`,
`Perf/LibraryConstructionUiPerfSmokeTests`). Play Mode's Song Select is the play host
still to move onto the same contract. What the deleted view offered and where each item
went is the #306 audit ("The header reaches every sort again"); the tuning cell's tooltip names the strings' notes (`ILibraryRow.TuningTip`).

**One filter, facet and sort engine serves both lists (#651):**
`DotCharter.Presentation/Shared/Library/` — pure, no Avalonia.

| Type | Holds |
|---|---|
| `ILibrarySong` / `IPlayedLibrarySong` | the row facts the engine reads; `PsarcSongListItem` implements the first, `PlaySongItem` the second (the record: best, grade, last run, genre) |
| `LibraryFacets` | the facet keys, each facet's title key, and the bucket key a row answers (`KeyOf`, `ValuesOf` — PATHS is the one multi-valued facet; a pathless row answers `""`) |
| `LibraryFilter` | one snapshot of every control (`FromFacets` over the view model's `BrowserFacet`s); `IsNarrowing`, `Without(facet)` for counts |
| `LibraryQuery` | `Fold` / `Tokenize` / `TokenizeFolded`, `Matches` (search, facets, lessons, record) and `CountFacet` (a popover's per-option counts) |
| `LibraryFacetOptions` | each facet's options from the rows, unknown bucket last only where present |
| `LibrarySort` | `Library` and `SongSelect` mode/caption tables over one `Order`, `Click` (header click → next state) and `Glyph` |
| `PlayRecordFilter` | the record ladder |

The view models keep their bound collections, commands, facets and chips:
`SongBrowserViewModel.CurrentFilter` / `ApplyFilter` / `RefreshFacetCounts` and
Song Select's `BuildCriteria` / `ApplyFilter` / `RefreshSongFacetCounts` are each a
few lines over the engine. **A filter, bucket, option or order rule goes in the
engine, never in a view model** — `SongBrowserFilterPassTests
.BothSongLists_SearchThroughTheOneEngineLeg_RatherThanTheirOwnSplit` /
`.BothSongLists_OrderThroughTheOneEngineSort` and `SongListFilterCostTests` read
the engine and both view models through `Infrastructure/SongQuerySources`; the
pure tests are `Presentation/Library/LibraryQueryTests`, `LibrarySortTests`,
`LibraryFacetOptionsTests`. The rules the engine settled for both screens:

- **Unknown LAST in both directions** for year, length, rating, tuning cell,
  last run and best score; never mirrored by the ▲/▼ flip.
- **Ties fall through to artist then title** (title then artist for the title
  column), in the direction the user picked. A highest-first mode (Song Select's
  recent, best and difficulty) flips only the VALUE leg.
- **Every distinct raw tuning is its own option**, labelled with its name AND its
  letters (`TuningNameResolver.Describe`), so two spellings of one tuning read
  apart; a blank or whitespace tuning is the unknown bucket.
- **So a large library's Tuning popover is ~40 rows** (every tuning, the unknown
  bucket, then the capo options it shares the popover with). The Library's
  funnel template (`LibraryColumnFunnel` in `LibraryView.axaml`) scrolls its
  option list past 360 px, the same height as Song Select's `FacetOptionsTemplate`,
  so the popover and its Clear stay inside the window. Each label sits on one line
  (`NoWrap`: the CheckBox presenter would wrap it), trims at the popover's max
  width and carries its full text as a tooltip.
  `Perf/LibraryTuningPopoverUiPerfSmokeTests` opens it over
  `FakeLibraryScreen.ManyTunings` (in the layout filter's de-DE / ja-JP pass).

- **The Library (`Views/Library/LibraryView`, formerly `SongBrowserView`) and Play Mode's
  Song Library are ONE screen in two places, and the shared half is
  `ViewModels/Panels/SongRowFacts`.** Tuning + capo fold into ONE fixed-width
  cell (`TuningCell`), amber when either means work on the guitar
  (`NeedsAttention`), and the PATHS column is one initial per arrangement
  (`Chips`). Artist grouping goes through the SAME `SongArtistGrouping.Stamp`
  (generic since 0.49) — the list stays FLAT and each run's header is stamped on
  its first row, so there is still one ListBox, one selection model and one
  virtualized panel over thousands of PSARCs. **A second hand-written copy of
  any of those is the drift the 0.49 redesign was commissioned to remove**;
  `UI/SongBrowserRowFactsTests` asserts the browser row prints what the library
  row prints. Two more rules the browser carries:
  - **The caption row and the row template lay out through the SAME
    `Controls/SongTableLayout` with the same inset** (one `SongTablePanel` each, #212;
    § Columns at narrow widths below). The scrollbar contract that keeps them aligned, precisely
    (this bullet used to say "must stay AUTO-HIDE", which read as contradicting
    the code): the list ships `VerticalScrollBarVisibility="Visible"` — the
    owner asked for a standing indication of scroll position — while
    `AllowAutoHide` stays at its default TRUE, so the bar OVERLAYS the viewport
    and reserves no layout width. It is `AllowAutoHide="False"` that reserves
    width inside the scroll viewport and not in the caption row above it, which
    put the columns 6 px out; Play Mode's live list dropped it for the same
    reason when it moved onto `SongTablePanel` (#231). Measured on both grids by
    `Perf/ShellConstructionUiPerfSmokeTests.SongBrowser_songRows_lineUpWithTheirCaptions_andStayVirtualized`.
  - **The detail pane's path pick is REAL** — it is the arrangement
    `LoadSongItem` opens the editor on (`ProjectState.ActiveArrangement`, via
    the pure `SongBrowserViewModel.ResolvePreferredArrangement`). Note counts
    come from `LibraryArrangementEntry.NoteCount` and are printed ONLY when
    non-zero; 0 means the manifest carried none, not an arrangement with no
    notes.

## DotCharter song packages (#708)

- The list holds `.dcsong` packages beside the `.psarc` archives (the scan, index and load
  are [../services/dcsong-format.md](../services/dcsong-format.md) § Library and Play Mode).
  A package row says so: `ILibraryRow.SourceTag` ("DotCharter song", `Library_Row_SourceDcSong`)
  ahead of the subtitle, in the subtitle's font and size so the row keeps its height
  (`Perf/LibraryConstructionUiPerfSmokeTests.APackageRow_CarriesItsTag_AtTheHeightOfEveryOtherRow`).
- **A package this computer cannot play is disabled with its reason**: an Opus package
  without ffmpeg. `PsarcSongListItem` takes the `LibrarySongBlock` the app's
  `ILibrarySongPlayability` (`LibraryService`, which asks the package's source —
  `DcSongLibrarySource`) answers from its last off-thread probe,
  so `CanLoad` is false, the Status cell says "Needs ffmpeg" and `LoadChartTip` /
  `DetailProblem` say where to put ffmpeg. Play Mode lists only playable rows, so it is
  absent there. `LibraryRescan.Merge` compares the block too: a Refresh after installing
  ffmpeg replaces the row.
- Fakes: `FakeLibraryScreen.Packages` and `.PackageNeedsFfmpeg` (packages beside their
  archives, a long title, the disabled one).

## The library seam (#886)

The browser and Play Mode's Song Select reach the library ONLY through the neutral
`Services/Library/` interfaces — `ILibraryService` (scan, load, cover),
`ILibrarySongPlayability`, `ILibraryAudioPrefetcher` and, for Library Doctor,
`ILibraryPackageProbe` — all answered by ONE `LibraryService` over the registered
`ILibraryPackageSource`s (`.psarc` then `.dcsong`). No view model names a package
source (`Infrastructure/LibrarySeamLintTests`), so a build without the PSARC source
lists, filters and plays `.dcsong` packages unchanged
(`DcSongPlayModeLoadTests.The_library_and_play_mode_work_with_only_the_dcsong_source_registered`).
Rows dedupe through `Library/LibraryDedupe` in both lists.

## Empty state + LOAD CHART gating

- **ONE folder-pick affordance at a time, in BOTH hosts.** On FIRST RUN (no
  library folder picked) the empty-state card carries THE Browse action:
  `SongBrowserView` hides its whole header folder bar
  (`BrowserFolderBar`, `IsVisible` on `PsarcLocation`), and Play Mode's
  browse-bar "Library Folder" button hides while `ShowSongSelectFirstRun` —
  the duplicate affordances were the audit's "three ways to pick a folder".
- **LOAD CHART is DISABLED without a loadable selection and says why**:
  `IsEnabled="{Binding IsSelectionLoadable}"`, tooltip from
  `SongBrowserViewModel.LoadChartTip` (action when live, reason when off) —
  hung on a WRAPPER Border, because Avalonia never shows a tooltip on a
  disabled control. Pinned by `SongBrowserEmptyStateTests`.
- **A FIRST scan (or a new folder) leaves NO resting status line.** `ResultCountText`
  ("N / M") is the fact; "Scan complete — N songs" beside it was a
  self-referential duplicate crammed against the header frame. `StatusText`
  is reserved for abnormal scan ends and load errors now. A REFRESH of the same
  folder answers on its own line, `RefreshResultText` (below), never in `StatusText`.

## Refresh is a merge, not a rebuild (#186)

`SongBrowserViewModel.Scan` tells the two apart by folder: `_listedFolder` is where
`_allSongs` came from, and a scan of that same folder with songs listed is a
REFRESH. Until #186 every scan cleared the list, so Refresh emptied it, selected
the first song, dropped the chosen path, scrolled to the top, released every
decoded cover and said nothing (the owner's log: 23 scans of one folder in 5 s).

- **A Refresh keeps the list up while it scans and streams nothing into it**; the
  result lands once, through `ViewModels/Panels/LibraryRescan.Merge`, keyed by
  `PsarcPath|SongKey` (a multi-song pack shares a path). An unchanged entry keeps
  its `PsarcSongListItem` — and so its row, its selection, its chosen path and its
  decoded cover. A changed entry gets a new item (row values are computed once, in
  the constructor); a selected song that changed is followed to its new item on
  the same path (`FollowReplacedSelection`).
- **`LibraryRescan.SameEntry` compares VALUES**: the library index serves an
  unchanged archive from its cache as NEW objects. It is a hand-written comparer
  over every field of `LibrarySongEntry` and `LibraryArrangementEntry`, so it is
  guarded by reflection (`LibraryRescanTests.EveryFieldOfASong_IsCompared`,
  `…EveryFieldOfAnArrangement_IsCompared`); a new field fails the gate until it is
  compared or excluded with a reason. `CoverImagePath` is excluded: it is where a
  decoded cover was saved, not what the archive holds.
- **The rows that went leave the list first, one Remove each
  (`LibraryRescanResult.RemoveLeavingFrom`), so the pass after them only
  inserts.** `BulkObservableCollection.SyncWith` edits a PURE removal or a PURE
  insertion in place and answers a mix with one Reset (deliberately —
  `SyncWith_MixedDiff_FallsBackToOneReset`), which re-realizes every visible row
  and loses the scroll position. Past `MaxIncrementalEdits` the Reset is the
  cheaper answer and is taken.
- **Covers:** only removed and replaced items release theirs, after the pass.
- **It always answers.** `RefreshResultText` ("Refreshed · 12 new, 1 removed",
  "Refreshed · no changes"; `Library_Refresh_*` in all three resx) shows beside the
  folder verbs (`LibraryView`'s `LibraryRefreshResult`), empty while scanning, after a
  first scan, and from the next search keystroke on.
- **Refresh does not stack.** A scan of a folder that is already being read is
  ignored (`_scanningFolder`): the `[RelayCommand]`'s own no-concurrency guard does
  not cover `Browse`/`Activate`, which call `Scan()` directly, and
  `AsyncRelayCommand.ExecuteAsync` does not check `CanExecute`. A DIFFERENT folder
  still supersedes the scan in flight (`SongBrowserScanRaceTests`). The Refresh
  button is off while scanning; the progress bar, the scan line and Cancel beside it
  say it is working.
- Guards: `UI/ViewModels/Panels/LibraryRefreshTests` (selection and path kept, a
  changed song followed, one Add + one Remove and no Reset, unchanged covers never
  released, the answer, one scan for two Refreshes) and `LibraryRescanTests`.

## Columns at narrow widths (#212)

At 1280 px the Library's Song column was 45 px ("Stran…") while 728 px of fixed
columns stayed mostly blank, and Song Select's was ~165 px. Both tables (the Library's
`LibraryView` + `LibraryRowView`, Song Select's `Views/PlayMode/Redesign/SongSelectScreenView`)
now lay out through `Controls/SongTablePanel` over a shared `Controls/SongTableLayout`
(`Library`, `SongSelect`). So does the LIVE Play Mode list (`SongListBox` under
`SongSelectListHeader` in `Screens/SongSelectView.axaml`, #231), over `PlayModeSongList`: its
Song column was 186 px at 1280 and its title, in a horizontal `StackPanel`, never
trimmed and ran over the path tags. It keeps its own cells (a 52 px cover, length over
difficulty in one cell, the record over a mastery meter) and Song Select's order:
length/difficulty, record, tuning, paths. At 1280 the length cell goes (song ~300 px);
at 1600 and 1920 nothing does. Header and rows share `FirePadListRow` (the header
used `FirePadRow` and sat 10 px left of the rows).

- **The song column gets the space first.** Below `SongTableLayout.SongFloor` (260 px)
  the fixed columns give way one at a time in their declared `GivesWay` order, never
  more than needed and never back at a narrower width. Library: Year, Status, Time,
  Diff, Tuning, Paths. Song Select: Time, Difficulty, your record, Tuning, Paths. At
  1280 the Library drops Year + Status (charting and full-bleed), Song Select drops
  Time + Difficulty; at 1600 and 1920 nothing goes.
- **The panel owns its children's `IsVisible`**: a cell that needs its own visibility
  binding (`RowStatus`, the record) is wrapped one level in. A child with
  `SongTablePanel.StandInFor` shows only while THAT column is hidden: with Status gone,
  a problem is an amber dot on the cover (`RowStatusMark`, tooltip = the status) and
  the Status funnel (status, origin, lessons) sits over the cover column in the caption
  row (`LibraryStatusFunnel`, the `LibraryColumnFunnel` template). Year / Time / Diff
  have no stand-in: a filter set on one stays visible as a chip.
- **Every caption is left-aligned and so are its values** (numbers included): a
  caption starts where its column's values start. Right-aligned numbers under a
  "label + funnel" caption started ~30 px right of it. The record cell reads accuracy,
  grade, then the FC tag, so the grades line up.
- Guards: `UI/Controls/SongTableLayoutTests` (pure: which columns go at the hosts' row
  widths, the floor, the order, no extra column dropped, the places tile the width) and
  `Perf/SongTableColumnsUiPerfSmokeTests` (both hosts at 1280 / 1600 / 1920, charting and
  full-bleed: the title cell's arranged width ≥ the floor on every row, each caption's
  first glyph within 2 px of every value under it, hidden captions hide their values,
  no caption runs into the next, the stand-ins; captions relabelled with the shipped
  strings, in the release's de-DE / ja-JP re-run). The caption/row agreement over real
  rows is `ShellConstructionUiPerfSmokeTests.Library_songRows_lineUpWithTheirCaptions_andStayVirtualized`.
  The live Play Mode list: `Perf/PlayModeSongListColumnsUiPerfSmokeTests` (1280×720 / 800,
  1600, 1920 over 40 long-titled PSARC rows: the song cell ≥ the floor, every title's
  text ends inside its cell and left of the path tags, at least one title trimmed, the
  caption row and the rows on one span and one column set, each caption's first glyph
  within 2 px of every value; also in the release's de-DE / ja-JP re-run).

## The detail rail: the decision first (#187)

At 1280×720 the rail spent 278 px on a cover box (a placeholder glyph for most
CDLC) and showed one of four paths. Now:

- **Order: title, artist, album, then the PATHS, then the facts and the archive.**
  Each path is one line (`Grid "Auto,*"`: the name, and what differs right of it,
  wrapping under itself only when long). Title and artist cap at two lines, the
  album at one.
- **The cover only takes the height the details leave**:
  `Controls/YieldingHeaderPanel` measures the body (the rail's `ScrollViewer`)
  first and gives the cover what is left, up to its `MaxHeight` (278), dropping it
  below `MinShownHeight` (96). At 1920×1080 the cover is whole; at 1280×720 it
  goes before a path does.
- **No art, no box**: `ILibraryScreen.DetailHasCover` (the song HAS a cover source,
  decoded or still streaming) gates the cover, so a song with art does not jump
  when its bitmap lands and one without art reserves nothing.
- Guards: `Perf/LibraryDetailRailUiPerfSmokeTests` (every path of a four-path song
  realized and inside the rail's viewport at the charting and full-bleed sizes of
  1280×720 and 1920×1080, with and without a cover; the cover whole at 1080; in the
  release's de-DE / ja-JP re-run) and, for Play Mode's Song Library,
  `PlayModeScreensUiPerfSmokeTests.SongSelect_detailRail_showsEveryPathOfAFourPathSong`
  (its paths are one row of tiles under a 200 px cover, and they fit at both sizes).

## Filtering: TYPING coalesces, everything else applies at once

**A filter pass is expensive and the row template is why.** Measured Release,
headless, the real `SongBrowserView` attached, over the owner's scale (2,160
songs — `Perf/SongLibrarySearchProfileUiPerfSmokeTests`; the 2026-08-15 column
is after the `FireChipStrip` row and the rest of that day's program):

| | 0.44.1 ms | 2026-08-15 ms |
|---|---|---|
| one Reset pass whose result set CHANGES (real template) | **126–170** | **65–82** |
| the same `ResetWith` with a TRIVIAL row template | 5.1–5.5 | — |
| a republish to an IDENTICAL sequence | 0.2 | 0.0–0.1 |
| `SyncWith` removal diff, collection level | — | ~0.2 |
| tokenized predicate over every row | — | ~3.2 |
| one sort over every row (CurrentCulture) | — | ~3.3 |

**Re-measured 2026-08-29, and the four remaining suspects are all NOISE — do
not spend a change on them.** Play Mode's `ApplyFilter` over the same 2,160
rows (`Perf/SongLibrarySearchProfileUiPerfSmokeTests
.Profile_PlayModeSongLibrary_stageBreakdown`, Release, 10 reps):

| stage | ms |
|---|---|
| the predicate (`LibraryQuery.Matches` since #651) over every row | 12.75 |
| one sort (CurrentCulture) | 4.07 |
| `SongArtistGrouping.Stamp` | 3.75 |
| …`Stamp` **plus** the second `Build` that publishes `SongGroups` | 3.69 |
| `SongGroups` Clear + N Add (2,268 groups) | 0.42 |
| `SongGroups` through `SyncWith` instead | 0.09 |
| `SyncWith` removal diff / identical republish | 0.44 / 0.11 |

- **`PlayModeViewModel.FrontDoor.cs`'s `SongGroups.Clear()` + N `Add()` is the
  obvious clear-then-loop and it costs 0.42 ms.** `SongGroups` is also bound to
  nothing — it is the grouping AS DATA, and `Stamp` is what puts the headers on
  the rows — so the collection has no subscribers to notify. Converting it to
  `SyncWith` would save 0.33 ms of a pass whose budget is 65–82.
- **`Stamp` calling `Build` internally and `RefreshSongListChrome` calling
  `Build` again is a genuine double walk, and it is FREE** (3.75 vs 3.69 — the
  second walk is inside the noise, because the runs are already in cache).
- **The sort is `CurrentCulture` on purpose** and the 4.07 ms is the price of a
  German/Japanese library sorting correctly. `Ordinal` is faster and wrong.
- **Cover art is off the UI thread, all of it (since #180).**
  `SongBrowserViewModel`'s loader is dwell-gated (a fling queues nothing),
  globally throttled through `SongCoverCache.DecodeThrottle`, generation-checked
  on the way back, size-capped, and released on `ContainerClearing`. Until #180
  the two `IArtworkSource.Exists` checks ran on the UI thread before the decode's
  `Task.Run` (a disk stat per cover on a library drive that may be asleep); the
  lookup, the archive extraction and the decode are now ONE `Task.Run`
  (`LibraryFilterPassChurnTests.ACoverLoad_ChecksForTheFileOffTheCallersThread`).

So the pass is LAYOUT-bound — nearly all of it is the ~15 realized rows
re-styling and re-measuring on a Reset — and the cost is DIFFUSE: a ladder of
hand-built templates (bare TextBlock → bound text → trimming → tooltips → the
chips `ItemsControl` → class bindings → a `DynamicResource` brush) has no
single dominating rung. **Do not go looking for the one slow binding; it is
not there** — the levers that worked were fewer passes (coalescing), fewer
Resets (`SyncWith`), and fewer elements per row (the drawn `FireChipStrip`,
which is where the 126–170 → 65–82 came from). Numbers are re-measured, never
carried forward; profile in RELEASE on an idle box — Debug numbers run 3–10×
worse and a concurrent build/test skews single keystrokes by 40×.

Two levers apply — FEWER passes, and CHEAPER republishes for the passes that
remain:

- **`ViewModels/Shell/CoalescedFilterPass` is the one door onto `ApplyFilter`,
  in BOTH surfaces.** The search box calls `Request()` — at most one pass in
  flight, posted at `DispatcherPriority.Background`, i.e. BELOW input, so a burst
  of keystrokes drains first and the pass runs once when typing pauses. Every
  other trigger (a chip, the sort, a finished scan) is
  one discrete gesture and calls `RunImmediately()`, which also consumes the
  typed pass queued behind it. Measured on the same probe, A/B in one run:
  typing "metallica" went from **334–569 ms of UI-thread work spread across the
  burst, with single keystrokes at 132–294 ms**, to **1.6–2.3 ms across all nine**
  and one pass at idle. No timer and no guessed delay.
- **`SongBrowserViewModel.CollapsedSongs` memoises `LibraryDedupe.CollapseByIdentity`**
  (0.8–1.0 ms per pass over 2,268 rows, for a list that is a pure function of
  `_allSongs`). Every mutation of `_allSongs` must call
  `InvalidateCollapsedSongs()`.
- **Play Mode's off-thread `_allSongsSnapshot`** went with LAN multiplayer on
  2026-09-19 (the LAN transfer-source resolver was its only off-thread reader).
- **`PsarcSongListItem` computes its row projections ONCE, in the constructor.**
  `LibrarySongEntry` is init-only so they cannot change, and `ArrangementChips`
  in particular must stay ONE instance — the chips `ItemsControl` compares
  `ItemsSource` by reference.
- **Both lists republish through `BulkObservableCollection.SyncWith` — the
  DIFFERENTIAL republish (2026-08-14).** A pass that trims or restores a few rows (a
  refined query, a chip) is a pure removal/insertion diff of the previous
  ordering, and `SyncWith` applies exactly those single-item edits (≤ 128,
  `MaxIncrementalEdits`) so the realized containers that stay are never
  re-realized; a reorder, a re-keyed rebuild, from/to empty or a bigger delta
  still lands as the ONE Reset. (Play Mode used to `Clear()` + N `Add()` —
  2,160 notifications, 117–129 ms even when the pass changed nothing; then one
  `ResetWith`, which still re-realized every visible row on every changing
  pass.)
- **`LibraryQuery.TokenizeFolded` is hoisted out of the predicate** — the query
  is split and folded once per pass, not once per song, and each row's
  `SearchHaystack` ("artist title album charter", folded through
  `LibraryQuery.Fold`) is built once per row. Every token must hit the haystack,
  ordinally — "metallica one" and "Motorhead" → "Motörhead" match on both
  surfaces. Folding BOTH sides is what reaches an accent. **There is ONE search
  leg, `LibraryQuery.Matches`**, which `CountFacet` calls too, so a popover's
  counts cannot disagree with the list they count.
- **`PlaySongItem` computes its row projections ONCE, like `PsarcSongListItem`
  always did (2026-08-14).** `ArrangementChips` in particular is one instance for the
  row's lifetime — the chips `ItemsControl` compares `ItemsSource` by
  reference — and `CanPlay` answers `File.Exists` once at row build time, not
  on every binding read.
- **Cover art is decode-capped and released symmetrically (2026-08-14).**
  `SongCoverImage` caps retained covers at 640 px (never upscaling — a bare
  `DecodeToWidth` would have tripled a normal 256² cover); BOTH lists release a
  row's cover on `ContainerClearing` (selected song kept for the detail rail)
  and re-ensure realized rows' covers on the effective-visibility flip, because
  the two lists share `PsarcSongListItem` instances and either side can release
  a cover the other's hidden realized rows still bind. Policy + wiring pinned
  by `UI/SongCoverImageTests`.

Guards, all in the default gate and all structural (never a wall clock):
`UI/ViewModels/Shell/CoalescedFilterPassTests`,
`UI/ViewModels/Panels/SongBrowserFilterPassTests` (including
`TheCoalescedResult_IsIdenticalToFilteringOnEveryKeystroke`, which compares the
coalesced view model row for row against one that filters on every keystroke
across every query / chip / sort combination, and
`ANarrowingPass_PublishesOnlyTheRowsThatLeft`),
`UI/ViewModels/PlayMode/SongLibraryRepublishTests`,
`UI/ViewModels/PlayMode/PlaySongItemProjectionTests`,
`Core/Utilities/BulkObservableCollectionTests` (the SyncWith edit-script
contract) and `UI/SongCoverImageTests`. Both lists carry a virtualization
guard pair now: the browser's
`SongBrowserFilterPassTests.TheSongList_DeclaresNoItemsPanelOverride`, Play
Mode's `SongSelectLayoutStructureTests.TheSongList_DeclaresNoItemsPanelOverride`
(behavioural halves in `Perf/`).

## Where a keystroke's time went, and the guards (#180)

Measured by `Perf/LibraryResponsivenessUiPerfSmokeTests.Profile_FirstShow_ReShow_AndTyping`:
the real `LibraryView` over `SyntheticSongLibrary` (1,800 songs + 5 % duplicates,
long and Unicode names), 24 keystrokes typed one at a time, each followed by its
idle frame, Debug headless, medians of three runs interleaved with the A/B leg.
**The filter is not the cost: the browser alone is 0.3–0.4 ms a keystroke.**
Everything else is the list.

| | before | after |
|---|---|---|
| per keystroke (mean of 24) | 239 ms | 42 ms |
| worst keystroke | 4,523 ms | 177 ms |
| row containers prepared (24 keys) | 599 | 136 |
| row trees BUILT (24 keys) | ~200 | ~15 |
| selection changes per Reset pass | 2 | 0 |
| first show (warm, in-process) | 222 ms | 146 ms |

- **Avalonia's `AutoScrollToSelectedItem` follows the selected INDEX.** Relaxing
  a query inserts rows above the selection one `Insert` at a time (`SyncWith`),
  each shifts the index, and each shift scrolled into view through a synchronous
  layout: 59 inserts were 56 layouts and 456 containers (4.5 s). Both song lists
  turn it off with `Controls/SelectionScroll.Coalesced`, which scrolls ONCE per
  selection CHANGE at `Background`, after laying out a pending pass first.
  Guards: `LibraryFilterPassChurnTests.BothSongLists_ScrollTheirSelectionOncePerChange`
  (lint), `LibraryResponsivenessUiPerfSmokeTests.RelaxingAQuery_PreparesOnlyTheRowsItBringsOnScreen`.
- **A recycled row was BUILT again.** Avalonia 12's virtualizing panel detaches
  a recycled container and re-attaches it, and a `ContentPresenter` drops its
  recycled child on attach — so an `ItemTemplate` row was rebuilt for every
  container a pass brought on screen. The row is the container's TEMPLATE now
  (`LibraryView`'s `LibraryRowContainer` ControlTheme over `Views/Library/LibraryRowView`),
  which a container keeps across recycling; only its DataContext changes. It is a
  view of its own because a value set inside a `ControlTemplate` has template
  priority and a style outranks it (`Border.FireListRow`'s padding beat the row's
  `18,10` and moved the columns 4 px). The stock `ListBox.FireList > ListBoxItem:selected
  Border.FireListRow` selectors still reach it. Guards:
  `…AFilterPass_RebindsItsRows_AndBuildsNone`, `…TheSelectedRow_WearsTheSelectedLook`;
  the gallery captures of the Library and Song Select were pixel-identical before/after.
- **The selection survives a pass that keeps its song.** A Reset cleared the
  ListBox's selection, its two-way binding wrote null, the browser rebuilt the
  detail rail for null and then for the first row. `LibraryScreen` ignores the
  list's writes while it mirrors an edit, and `ApplyFilter` replaces the
  selection only when the pass dropped its song (first row, one change).
  Guards: `LibraryFilterPassChurnTests.APassThatKeepsTheSelectedSong_…`,
  `…APassThatDropsTheSelectedSong_…`.
- **One collection event per republish:** the chips (`ActiveFilterChips.SyncWith`,
  none when equal) and the rail's paths (`ArrangementChoices.ResetWith`) were a
  Clear plus an Add per item, and `LibraryScreen` rebuilt its list per event.
- **No disk on the selection path:** the archive size (`FileInfo`) is read in a
  `Task.Run` and dropped if the selection moved on (`…TheArchiveSize_IsReadOffTheSelectionsThread`).
- **Logged on the user's box** through `Views/Shell/SurfaceShowTiming`:
  `Surface 'Library (first show)'` / `'Library'` when the shell shows it
  (`MainWindowPrewarm`), and `Surface 'Library search (3 keys, 71 rows)'` /
  `'Song Library search (…)'` from `Views/Shell/SearchSettleTiming` — first
  keystroke of a burst to the idle tick after its pass. Linted by
  `SurfaceShowTimingTests`.
- **What is left is a Reset's ~10–20 containers re-prepared** (re-attach,
  restyle, rebind, measure: ~8 ms each in Debug). A pass with more than
  `BulkObservableCollection.MaxIncrementalEdits` changes (clearing a search, the
  first letter) is one Reset; the per-event cost of an incremental edit is
  ~0.3 ms Debug. Play Mode's Song Select still renders its rows through an
  `ItemTemplate` (rebuilt on recycle); it got only the scroll fix.

## Datagrid headers + column facets (second pass)

- **The browser's caption row IS the filter/sort UI now.** Clicking a caption
  sorts by that column, second click flips direction, the active caption carries
  its arrow (`SortByColumnCommand`, glyph props). **Since the Library redesign the
  header is the ONLY sort control** (no dropdown), so every mode
  `LibrarySort.Library` knows needs a caption: the song column's header cell
  (`LibrarySongSorts`) captions three — Artist, Title, Pack / file
  (`ILibraryScreen.SongColumn` / `TitleSort` / `ArchiveSort`) — and
  `LibrarySort.Library.ModeOf` maps captions to modes (#306;
  `LibraryScreenTests.EveryOrderTheBrowserSortsBy_HasAHeaderCaption`, the fit at every
  width in `SongTableColumnsUiPerfSmokeTests`).
  Two new columns — YEAR and DIFF (the cached 1–10 index rating) — grew the
  caption/row grids TOGETHER (since #212 both read `SongTableLayout.Library`).
  Sort modes 5 (difficulty) and 6 (tuning cell) follow the unknown-LAST rule in
  both directions.
- **Eight multi-select FACETS replace the strip's dropdowns**
  (`ViewModels/Panels/BrowserFacets.cs`): tuning+capo (one popover), paths,
  year decades, difficulty bands, length buckets, status, and Stock/CDLC
  provenance (`LibrarySongEntry.Origin` over the plugin's `ILibraryOriginClassifier`; Unknown without one, and a facet with no value in the library is not offered). Options are
  DERIVED from the scan; a selection survives a rescan only while its value
  exists; UNKNOWN buckets are explicit options (deliberately stronger than the
  silent unknown-passes rule, which predates having anywhere to show them).
  **Two of the eight did not have one until 2026-09-04** — TUNING and PATHS
  filtered the option list to non-empty values and never appended the bucket, so
  ticking any tuning silently dropped every own chart and metadata-less package
  with no checkbox that could bring it back and no chip naming the exclusion.
  Play Mode's twins had always appended it. The rule is **a facet whose predicate
  can REJECT an unknown value must OFFER that value**, and it is asserted per
  FACET now — `SongBrowserFilterChipsTests.EveryFacetThatCanRejectAnUnknown_OffersIt`
  ticks every option of each facet in turn and requires an all-unknown row to
  survive — because the same omission in a ninth facet would otherwise be a ninth
  bug. The PATHS half needed the predicate too: a row with no chips matched the
  empty sequence under `Any(Selected.Contains)`, which is false whatever is
  ticked, so `LibraryFacets.ValuesOf` answers `[""]` for it.
- **`BrowserFacet.ResetOptions` drops a vanished selection and raises `IsActive`
  ONLY — never `SelectionChanged`.** The editor browser binds `facet.IsActive`
  per funnel and is fine. Play Mode's caption funnels are computed properties
  over SEVERAL facets (`HasTuningFacetFilters` = tuning ∨ capo, and three more),
  notified from `RefreshFunnelStates`, which until 2026-09-04 only the
  `SelectionChanged` handler called — so a rescan that removed the last selected
  tuning correctly dropped the selection and the chip, and left the funnel LIT
  over an empty popover. `BuildSongFacetOptions` calls `RefreshFunnelStates` at
  its end now. **Any new funnel that folds more than one facet must be refreshed
  from BOTH doors.** Pinned by `SongLibraryRepublishTests
  .ARescanThatDropsTheLastSelectedValue_PutsTheFunnelOut`, asserted on the
  NOTIFICATION — the property is computed, so reading it always gave the truth,
  which is exactly why nothing caught it.
  Each caption funnel opens a `FirePopover` of checkboxes with per-option match
  COUNTS, computed on popover OPEN with every other filter applied (never per
  keystroke: `RefreshFacetCounts`). The chips row prints one removable chip per
  selected option; the strip is slim (search + sort). Guards:
  `SongBrowserFilterChipsTests` (facet model), the identity guard sweeps every
  facet + one multi-select shape each.
- **Play Mode's Song Select carries the SAME caption-row model since the
  unified browser (2026-08-15, third pass):** nine facets (played, ownership,
  origin, genre, paths, tuning+capo, length+difficulty) in
  `SongSelectViewModel.cs`, header sorting via
  `SortSongsByColumnCommand` over the existing `SortMode` (modes 5 tuning /
  6 length added, unknown-last), and the same explicit-unknown policy — the
  old unknown-passes rule is GONE there too. **The unknown-last half of that
  sentence was FALSE until 2026-09-04**, and is the shape a claim in a guide
  rots into: Play Mode's descending direction was `ordered.Reverse()` on the
  materialized list — not a comparer — so it inverted every unknown leg the
  sort had just applied (a second click on TIME opened on the songs with NO
  length) and reversed equal-key ties besides. Both lists order through the one
  `LibrarySort` now (#651). Pinned by `SongLibraryRepublishTests
  .SortingByLength_KeepsUnknownLengthsLast_InBothDirections` and its difficulty
  twin. **The browser's own mode 6 (tuning) had no unknown leg in EITHER
  direction** — an empty tuning cell sorts first ascending — fixed with it,
  pinned by `SongBrowserFilterPassTests.TheTuningSort_KeepsUnknownTuningsLast_InBothDirections`
  (the existing `TheTuningSort_GroupsEqualTuningCells` could not see it: on a
  fixture where every song has a tuning, ordered IS ordered). The popover templates are ONE
  shared `ResourceDictionary` (`Views/Panels/SongFacetTemplates.axaml`,
  `ResourceInclude`d by both views) and the `ColumnSort`/`FacetFunnel` button
  looks are ONE shared style file (`Styles/SongListHeaderStyles.axaml`,
  `StyleInclude`d by both) — a second hand-written copy of either is the drift
  the unification exists to remove. Play Mode's RECORD keeps its ordinal
  ladder + `UpdatedOnly` toggle inside the RECORD caption's popover (the
  achievement exception: an unplayed song FAILS it). The search haystack on
  BOTH surfaces reads "artist title album charter" — **true of Play Mode's since
  the day it was written and of the BROWSER's only since 2026-09-04**: the
  charter was added to `PlaySongItem` alone, and because both predicates require
  every token to hit the haystack, typing a charter's name found their whole
  catalogue in one surface and nothing at all in the other, over one library.
  Same date, same class: Play Mode's difficulty key read `< 7` where the
  browser's read `<= 7` (both are `LibraryFacets.DifficultyKey` now), so a song rated
  exactly 7.0 was MEDIUM in one and HARD in the other — and Play Mode disagreed
  with the option LABEL both surfaces print ("Medium (4-7)" / "Hard (above 7)"),
  which is what settles a boundary. **When the two predicates share a bucket
  key, cross-assert them rather than testing each alone** —
  `PlaySongFilterTests.TheDifficultyBands_AgreeWithTheEditorBrowsers` is the
  shape; each surface's own boundary theory had been green throughout. Detail:
  [play-mode/song-select.md](play-mode/song-select.md); guards:
  `PlaySongFilterTests`, `SongLibraryRepublishTests`,
  `SongSelectLayoutStructureTests`, `SongSearchKeyboardTests`.
- **Naming trap paid for here:** a new command must not share a bare NAME with
  a `KnownUnreachable`-listed command on any other view model — the
  reachability lints match names across files, comments included
  (`BrowserFacet.ClearFacetCommand` is named around
  `PedalboardViewModel`'s listed dead `Clear` wrapper).

## Filter UX: chips, the two clear affordances, keyboard flow

- **Both surfaces show ACTIVE filters as removable chips + a clear-all**, and
  since the unified browser both speak `BrowserFilterChip`
  (`ViewModels/Panels/BrowserFilterChips.cs`; Play Mode's `SongFilterChips`
  enum model is DELETED). Chips are rebuilt inside `ApplyFilter` (the one
  chokepoint), never in a setter; chips exist only for NON-DEFAULT state
  (the browser's `HideLessons` defaults TRUE, so it is the FALSE state that
  earns a "lessons shown" chip). Removing a chip resets exactly its one VALUE —
  a facet chip deselects one option, the rest of that facet's selection stays —
  and runs the pass immediately; clear-all resets everything in ONE pass (a
  `_suspendFilter` guard on both view models). Pinned by
  `SongBrowserFilterChipsTests` + `SongLibraryRepublishTests`.
- **The search field's ✕ clears the TEXT ONLY** — it used to run
  `ClearSongFiltersCommand` (all nine filters) while its label said "clear
  search". "Clear filters" remains its own button. Pinned by
  `SongSearchKeyboardTests`.
- **Keyboard flow, identical on both lists** (`Input/SearchBoxKeys`, shared
  rules): Ctrl+F focuses the search box (claimed per-surface — the chart editor
  keeps its own Ctrl+F), Down hands focus to the list (selecting the first row
  when none is), and Esc in a NON-EMPTY search box clears the text and stays.
  The shell's one-Escape-chain rule gained that single deliberate stand-down:
  `GlobalChordGate.Context.SearchBoxClaimsEscape`, false for an empty box, so
  "Esc, Esc" still leaves the screen. Pinned by `GlobalChordGateTests` +
  `SongSearchKeyboardTests`.

**The two surfaces share the row FACTS, the ENGINE and the header chrome.**
Shared: `SongRowFacts`, `SongArtistGrouping`, `LibraryDedupe`, the
`Presentation/Shared/Library/` engine (filter, facet options, counts, sort — see
the table at the top), `BulkObservableCollection.SyncWith`,
`BrowserFacet`/`BrowserFacetOption`, `BrowserFilterChip`,
`SongFacetTemplates.axaml`, `SongListHeaderStyles.axaml`, `CoalescedFilterPass`,
`SearchBoxKeys`, and the cover pipeline. What differs is which facets and sort
modes each screen OFFERS (`LibrarySort.Library` / `.SongSelect`), never what
one of them means.
