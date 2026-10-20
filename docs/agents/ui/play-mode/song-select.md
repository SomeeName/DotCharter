# Play Mode — Song Select, its filters and listen-back

Part of the Play Mode reference. Router: [../play-mode.md](../play-mode.md).

- **Listen-back.** The results screen plays the run just played back. There
  is one master transport, so it is BORROWED and handed back — see
  [docs/agents/services/live-play.md](../../services/live-play.md). The v0.27
  take library (star / rename / delete / re-amp from the History screen) was
  removed with that screen on 2026-09-19; restore from tag `pre-cut-2026-09`.
- **Song Select filtering, facet options, counts and sorting** live in the pure
  engine both song lists share (#651): `LibraryQuery.Matches(item, LibraryFilter)`,
  `LibraryFacetOptions`, `LibraryQuery.CountFacet` and `LibrarySort.SongSelect`, in
  `DotCharter.Presentation/Shared/Library/` (table in
  [../song-browser.md](../song-browser.md); unit-tested in `PlaySongFilterTests` and
  `Presentation/Library/LibraryQueryTests`). `ApplyFilter` snapshots every control
  into one `LibraryFilter` (`BuildCriteria`) and runs the predicate once over the
  backing `_allSongs` (never per-keystroke re-query). **Filtering and sorting read `PlaySongItem` properties only, NEVER
  `PlaySongItem.Inner`** — `Inner` is null for own charts (and was for LAN peer songs until
  2026-09-19), so reaching through it
  silently dropped their artist/album/length out of search/sort/filter. Every
  field Song Select needs (`Title`, `Artist`, `Album`, `TuningSummary`,
  `ArrangementSummary`, `LengthSeconds`, `Capo` (`int?`, null = unknown)) has the
  remote / own-chart fallback baked into `PlaySongItem`.
  **FACET-SHAPED since the unified browser (2026-08-15):** the metadata filters
  are `BrowserFacet` selection SETS of canonical bucket keys (the editor
  browser's model), opened from the caption row's funnels — nine facets
  (played, ownership, origin, genre, paths, tuning, capo, length, difficulty),
  lazily created in `SongSelectViewModel.cs`, options derived from
  the library in `BuildSongFacetOptions` (`LibraryFacetOptions.For`), per-option
  counts refreshed on popover OPEN (`RefreshSongFacetCounts` via
  `OnFacetFlyoutOpening`). The bucket keys come from `LibraryFacets.*KeyOf`
  (length short/mid/long at 180/300 s, difficulty easy&lt;4/medium&le;7/hard,
  the genre key is
  CASEFOLDED so "Metal"/"metal" are one option). **Unknown-data policy,
  REVERSED from the old silent unknown-passes rule:** an unknown value is the
  explicit `""` bucket, offered as its own labelled option — including or
  excluding unknowns is the USER's call. The two non-facet specials keep their
  controls inside the RECORD caption's popover: the RECORD combo
  (`PlayRecordFilter` — any / grade-X-or-better best-first from
  `GradeCalculator`'s real scale / full-combo-only; **the ONE filter an
  unplayed song FAILS** — an achievement filter, not metadata: a never-played
  song's no-grade/no-FC state is KNOWN, and the `!HasBest` guard is
  load-bearing at the F floor). The has-update toggle (`UpdatedOnly`) went with
  the LAN catalogue on 2026-09-19. The
  search haystack reads "artist title album charter", and it is FOLDED:
  `LibraryQuery.Fold` (NFD, drop combining marks, lower-case invariantly) is
  applied to `PlaySongItem.SearchHaystack` once per row and to the query once per
  pass (`LibraryQuery.TokenizeFolded`), so the per-row test is an ORDINAL
  `Contains` and "motorhead" finds the umlauted spelling — on both song lists,
  which fold both sides the same way. Pinned by `PlaySongSearchFoldingTests`. Active filters render as
  `BrowserFilterChip`s (one per selected OPTION plus search/record),
  rebuilt by every pass in `RefreshSongListChrome`; a chip's ✕
  (`RemoveSongFilter`) deselects exactly its one value and runs the pass
  IMMEDIATELY (a chip is a discrete gesture, not typing).
  **Difficulty ratings reach UNPLAYED songs (0.24.x):** the `ChartDifficultyEstimator`
  rating flows to the difficulty chip / sort (`SortMode` 4, hardest first ▲; ties by
  artist then title) / band filter for the
  whole library, not just played songs. A scan can't compute it (a `.hsan` has no
  note data), so the plugin's package source rates the hardest arrangement
  the first time a song is loaded and caches it in the library index (schema v5
  `rating` column); the scan reads it back cheaply. `PlaySongItem.ApplyStats`
  prefers the per-run stats rating (played songs) and falls back to the index
  rating (unplayed). A never-loaded song reads unrated (0) until first opened —
  the honest cost tradeoff (scan-time SNG parse across the whole library is
  prohibitive; see [services/parsing-psarc.md](../../services/parsing-psarc.md)). Filter state
  is intentionally NOT persisted (transient browsing state, like the other Song
  Select filters).
  **`RefreshSongStats` runs a filter pass UNCONDITIONALLY** (after a run records,
  after an abort back to Song Select, on every profile switch). It used to run
  one only for the three stats-driven SORTS; stats also decide MEMBERSHIP -- the
  played facet is `HasBest`, the record ladder is `HasBest`/`Grade`/`IsFullCombo`,
  the difficulty band is the rating `ApplyStats` just re-stamped -- so on the
  default artist sort a profile switch left the outgoing player's played songs
  listed, and counted in the "N of M" readout, with their own `HasBest` already
  false. Pinned by `SongListStatsRefreshTests`.
- **Tuning shows the NAME, filters on the VALUE.** The Song Select tuning chip
  and the pre-play "Erwartete Stimmung" display the resolved common-tuning name
  (`TuningNameResolver` — "Drop C", "DADGAD"), the raw note letters only when
  unknown, with the full letters ALWAYS in the tooltip
  (`PlaySongItem.TuningDisplay` / `ArrangementOption.TuningDisplayLabel` /
  `FlowTuning` + `FlowTuningNotes`). **The tuning facet's option VALUES are the
  raw `PlaySongItem.TuningSummary` strings and its LABELS the resolved name with
  the letters** ("Drop C (C G C F A D)", `LibraryFacetOptions`); never feed a
  resolved name into `LibraryQuery`.
  Tuning names are canonical English, NOT localized.
- **The detail rail's tuning check has THREE states, and green is a CLAIM.**
  `SelectedTuningIsVerified` (the song's tuning matches the last verified one),
  `SelectedTuningNeedsChange` (amber) and `SelectedTuningIsUnverified` (neutral),
  all written together by `SetTuningCheckState` so exactly one pill can show.
  The view keyed the green pill on `!SelectedTuningNeedsChange`, which painted
  the two claim-NOTHING branches (the song carries no tuning; nothing verified
  yet) as IN TUNE beside the Play button.
- **The rail's arrangement tiles are `ToggleButton`s with a ONE-WAY `IsChecked`,
  so `SelectArrangement` republishes the PICKED tile even when nothing changed**
  -- the click has already flipped the visual to unchecked and only a fresh
  notification puts it back. Both pinned by `SongDetailRailTests`.
- **The Song Select surface is one centered `MaxWidth=1360` column with
  `HorizontalAlignment="Stretch"`** (header, browse bar, list, loading indicator,
  both empty states). It MUST be `Stretch`, not `Center`: a `Center` grid sizes to
  its content, so while the library loads (empty list, empty state suppressed) the
  column collapsed and the chrome shifted with a detached scrollbar. Guarded by
  `SongSelectLayoutStructureTests`.
- **Duplicate byte-copies collapse to one row.** A library can hold byte-copies
  of a pack under different file names, so the same song identity
  (`SongKey|Charter|Version`) scans as 2–3 rows. `RebuildFromBrowser` folds the
  FromPsarc rows through `LibraryDedupe.CollapseByIdentity` (Services,
  `Library/`) — smallest ordinal path wins — BEFORE building `_allSongs`, so
  the chooser lists each song once. The editor browser collapses the SAME way in
  `SongBrowserViewModel.ApplyFilter`, so both surfaces agree. LibraryDoctor is the
  diagnosis surface and still reports the duplicates (it reads the browser's
  uncollapsed `AllSongs`). Collapsing also reduces ambiguous shareable/leaderboard
  keys; leaderboard keying is unchanged (same identity = same board).

## Redesign

The screen is being rebuilt beside the shipped one, not in place. Nothing below
is wired into `PlayModeView` yet — the swap is a later step, and waits for the
owner to see the screenshots (the rule for every redesign).

- **A redesigned screen binds to a CONTRACT, never to `PlayModeViewModel`.**
  The contracts live in `DotCharter.Presentation/Contracts/PlayMode/`, the fake
  implementations in `DotCharter.Presentation.Fakes/PlayMode/`. The real view
  model is 542 KB across 27 partials and drags profiles, the practice store and
  the psarc index behind it, so the old screen could only be reviewed by running
  the app against a real library.
- `DotCharter.Presentation/Contracts/PlayMode/ISongSelectScreen.cs` — the
  contract, plus `ISongRow`, `IArrangementTile`, `IFilterChip` and the optional
  `ISongSelectViewHooks` (covers for the rows on screen, filter counts when a
  funnel opens). **It speaks the Library's language** (wave 5, 2026-09-24): the
  columns are the Library's `ILibraryColumn` — the header is the ONE sort
  control and carries the column's funnel, so the 2026-09-17 sort combo and the
  chips-only filter state are gone (chips alone gave no way to ADD a filter) —
  artist headings stamp the first row of a run while the list is in artist
  order, the tuning cell prints only a non-standard tuning or a capo, an
  unknown difficulty is "—", and an unplayed song's record cell is empty. What
  it adds over the Library is the player's: the RECORD column (sorts best
  first, filters played / not played / full combo), the tuning check in the
  rail (`TuningCheckLabel`, `TuningIsVerified`, `TuningNeedsChange`), the best
  run, Back and Play.
- `DotCharter.Presentation.Fakes/PlayMode/FakeSongSelectScreen.cs` (rows,
  columns and item fakes in `FakeSongSelectData.cs`) — `Library()` (41 rows in
  artist order), `Empty()` (first run), `Full()` (played only, best first),
  `Loading()` (a library still being read), `NoMatch()` and `LongText()` (the
  stress case). **`LongText()` is load-bearing** — every layout fault reported
  on this screen is invisible against an empty fixture. The fake's headers sort
  and its funnels filter, so the screen is interactive with no services at all.
  The verbs are plain `RelayCommand` FIELDS, not `[RelayCommand]` methods: a
  generated `…Command` reached only through an interface `x:DataType` reads as
  ambiguous to `UI/RelayCommandReachabilityTests`.
- `Views/PlayMode/Redesign/SongSelectScreenView.axaml` — header
  (`SongSelectRedesignTopBar`: Back, title over the count, search, library
  folder), the chip row (`SongSelectRedesignChips`), the table
  (`SongSelectRedesignColumnHeader` over `SongSelectRedesignList`, the same
  column grid in both) and the rail (`SongSelectRedesignDetailRail`: cover,
  title, the tuning check, best run, path tiles, `SongSelectRedesignPlayButton`),
  plus `SongSelectRedesignNoMatchPanel` and the full-width
  `SongSelectRedesignFirstRunPanel`. The column header template is a copy of the
  Library's (its funnel's `Opening` calls this view's own code-behind). **The
  Play button is always present and `IsEnabled`-gated**, so its place does not
  move between "nothing picked" and "ready"; double-clicking a row plays it.
- Tests: `Perf/SongSelectRedesignUiPerfSmokeTests` (headless — construction,
  attach, layout per variant, row realization, one empty panel at a time, Play
  enablement, the rail's record whole at rest, and
  `EveryRowsText_StaysInsideItsParent_UnderLongText`; writes
  `songselect-redesign-<variant>.png` under `DOTCHARTER_UI_SWEEP_DUMP=1` +
  `DOTCHARTER_RIG_CAPTURE_DIR`) and `UI/FakeSongSelectScreenTests` (default
  gate — the fixture cannot decay back into an empty one, and the headers sort
  and filter the way the Library's do).
- One resource key of its own, in all three resx:
  `PlayMode_SongSelect_FirstRunEyebrow` — every eyebrow on a Fire surface is
  mono CAPS, and the mixed-case key that would have done the job read wrong at
  that size. (`PlayMode_SongSelect_Col_Difficulty` went on 2026-09-24: the
  column captions are the columns' own `Title`s now, as in the Library.)
  **A third (`…_Col_Arrangements`) was written and then deleted**: its German
  is the English word, and `LocalizationSweepTests.UntranslatedValues_DoNotGrow`
  ratchets on exactly that. The arrangements eyebrow reuses
  `SongBrowser_Col_Paths`, which is the shipped vocabulary for the same data
  and is already caps in all three languages. Everything else reuses the
  shipped screen's `PlayMode_SongSelect_*` / `SongBrowser_Col_*` keys.
