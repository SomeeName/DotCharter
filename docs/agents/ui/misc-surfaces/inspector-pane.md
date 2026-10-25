# The inspector PANE — dock context, the tab strip and its shared chrome

Part of the inspector / misc-surfaces reference. Router: [../misc-surfaces.md](../misc-surfaces.md).

## Inspector pane (`Views/Editor/Inspector/*`)

- The Inspector is a Dock.Avalonia dockable whose `Context` stays a **view
  model** (`InspectorViewModel`) — the tab views are re-materialized on
  float/redock, so never put a control in Context. `InspectorView.axaml` is a
  custom tab host (a `Controls/OverflowTabStrip` of tab headers + a `Grid` of
  **eight** always-alive `UserControl`s gated by `IsVisible`, NOT a
  `TabControl` — the default rebuilds the previous tab's tree on every switch;
  O(1) matters with ~76 KB of tab XAML). **The Grid is `x:Name`d
  `InspectorTabPanels` and that name is load-bearing**: `InspectorView`
  implements `IPrewarmableSurfaces` and sweeps that Grid's hidden children, so a
  rename is a SILENT no-op that takes every tab back to cold — see the prewarm
  bullet below. **Since 0.36 the order is Sound(0) ·
  Mixer(1) · Song(2) · Lyrics(3) · FEHLER(4) · Notes(5) · Phrases(6)** — the
  Note and Chord tabs merged (see the Sound-tab bullet below), so every
  `Is*TabSelected`, every `Tag` and `InspectorViewModel.PhrasesTabIndex` shifted
  down by one — and **since 0.42.4 the Riff Library is tab 7**, not a dock pane.
  **Tab 8 is Chord shapes (#486)**, the first inspector tab on the target
  architecture: `InspectorViewModel.ChordShapes` is the `IShapePanelScreen`
  contract (`Presentation/Screens/Editor/ShapesPanelViewModel`, a DI singleton from
  `EditorModule`), hosted as `Views/Editor/ShapesPanelView` with its DataContext
  bound to it — no `ViewLocator`, no adapter VM. It keeps itself current while
  hidden (one coalesced `IUiDispatcher.Post` per history step / arrangement / level
  change, rows reused by template), so the tab's generic `HiddenSurfaceRewarmer`
  lays its rows out before the click. Opened from the tab strip and the palette's
  `view.showChordShapes`; it has no View-menu row (the bar's one file was at its
  size budget when this shipped; since #653 the View menu is its own
  `Views/Shell/Menus/ViewMenu.axaml`, so a row is now a choice, not a cost). Verbs and their invariants: `Services/Editing/ChordShapes/`
  ([chord-templates.md](../../services/editing-invariants/chord-templates.md)).
  Pinned by `Perf/ShapesPanelConstructionUiPerfSmokeTests` (the real VM through
  the app's own `InspectorViewModel`, 60 seeded templates, every card measured).
  **Its structure: a PINNED header over ONE scroll region, and a decluttered card.**
  The header (`ShapesHeader`) is the summary, Clean up (today it removes the shapes
  nothing uses; the dialog replaces it) and the search box. Below it the card list
  (`ShapesScroll`, `AllowAutoHide="False"` so the bar says the list goes on) is the
  only thing that scrolls; `Controls/AdaptiveColumnsPanel` lays the cards in
  columns once two fit at 340 px. **A card shows the diagram, the name (a wrapping
  `TextBox`, applied on Enter or when it loses focus) and the use count, plus a dot
  when the shape needs attention.** The problem with its one fix, the actions (Edit,
  Select uses, Merge…, Delete) live in two RESERVED strips
  (`Border MinHeight`, holding a `.ShapeReveal` panel) that show on the hovered,
  focused or current card only (the No name switch sits beside the use count in a fixed 16 px slot, because German labels fill the strip's single row), so a card never changes height under the pointer;
  `ShapesPanelView.axaml.cs` keeps the last pressed or focused card current
  (the `current` class). Nothing is truncated: names, issues and counts wrap. A merge (Merge…
  then Merge here, or the Merge fix) and a delete of a used shape wait in ONE
  confirmation bar (`ShapesPendingBar`) that says what moves where. Edit and the bulk
  bar are wired (#266, #270), and so is drag-to-merge (#271): `Controls/ShapeCardDrag` (four attached commands on the card;
  a press on the blank card or its diagram arms it, 6 px of travel begins it and takes the pointer, the card under the
  pointer is hit-tested, release drops, Esc or a release over nothing ends it, the list scrolls within 28 px of its edge)
  drives `Presentation/Screens/Editor/ShapesDragVerbs`, which picks the source exactly as Merge… does and ends in
  the same bar through `ChordShapeEdits.PlanMerge`; a drop on other frets, an arpeggio mismatch or an unused source
  says that plan's refusal on the status rail and changes nothing. A card's tick
  (`ShapeTick`, `CanTick`) sits in its own fixed column beside the name, so revealing it never changes the
  card's height, and a ticked card keeps its strips open (`Classes.Record`). The bulk bar
  (`ShapesSelectionBar`: Edit together, Merge into one, Delete, Clear) is `Presentation/Screens/Editor/ShapesBulkVerbs`
  over `Services/Editing/ChordShapes/ShapeBulkEdits`: Merge keeps the most-used ticked shape (all must share frets
  and arpeggio flavour), Delete moves the uses of a used shape onto a same-frets shape OUTSIDE the ticks (refused,
  changing nothing, when one has none), Edit together opens the shape editor with `ShapeEditRequest.Together`.
  Merge and a delete of used shapes wait in the one bar; each is ONE undo node.
  What a verb did goes to the STATUS RAIL (`ProjectState.SetMessage`, refusals as
  `Warning`), never a line in the pane. The smoke test asserts, at the default
  inspector size (600 x 470 for the tab) and at 320 / 420 / 600 px, with every
  card's strips forced open: the header has no `ScrollViewer` ancestor, whole cards
  in view, no trimmed or overflowing text, and every diagram's dots, fret label
  and o / x row apart.
  **Shapes are drawn by `Controls/ChordDiagramControl`, a chord CHART** (strings
  down, lowest left, o / x above the nut, the first fret named "8fr", one dot per
  fretted string with its finger number inside), its arithmetic in the pure
  `ChordDiagramLayout` (`UI/Controls/ChordDiagramLayoutTests`). It is NOT
  `ChordBoxControl`, the inspector's editing fretboard: that one's fixed rail and
  per-fret numbers ran together at a thumbnail's size.
  **The tab is also the chord-shape PALETTE (#477)** — there is no second list.
  A card's diagram is a `Button` (`ShapePlace`, `FirePresetCard`): a click
  places that whole chord at the playhead, or reshapes the selected sounds onto it (one undo step); the
  action strip's `Place` does the same. The strip also holds `Next use` (steps to the next chord or
  hand shape of the shape after the playhead, wrapping) and `Split off selection` (only on the card of
  a shape the selection holds a use of: `CanSplitSelection`). A "Needs attention (N)" chip beside the
  summary (`ShowAttentionOnly`) lists only the cards with an issue, together with the search.
  "Repeat previous chord" (`Ctrl+Shift+C`, the palette's `tools.repeatPreviousChord`)
  is a command on the same screen; there is no recent strip. All of it drives `Services/Editing/ChordShapes/IChordShapePlacementService`.
  **A pen placement leaves the selection alone on purpose**: the selection wins over
  the pen, so selecting the new chord would turn the next click into a reshape of
  it, and a fresh chord selection pulls `InspectorViewModel.AutoSelectTab` off this
  tab onto Sound — the smoke test clicks a real chord box and asserts the tab is
  still the visible one. No digit-row slot keys: every digit row is taken
  (`Ctrl+Shift+5`/`8` included) and `Ctrl+Alt` is AltGr.
  Tab 6 is the ONE **Phrases** panel (the Song tab has no phrase section): a
  STANDALONE `PhrasesInspectorViewModel` (injected seams, headless-tested) behind
  the catalog contract `IPhrasesScreen` (gallery fake `FakePhrasesScreen`), composed
  as `InspectorViewModel.Phrases`. Rows come from `PhraseMarkers.Iterations` (both
  phrase stores, what the exporters read), rebuilt on tab activation and on
  `HistoryChanged` when the fingerprint moved. A phrase line renames in place
  (Apply/Enter only once the text differs, Esc cancels) and deletes, both through
  `Services/Editing/Phrases/PhraseEdits` — one undo node over both stores and the
  name-keyed `PhraseInfos`; jump rows route `ChartEditorViewModel.ExecuteSeek`, and
  propagate is a per-row two-step confirm over the shipped
  `IPhraseIterationPropagationService` (the Tools-menu power tool keeps its
  playhead-only flow).
- **The tab strip is the SHARED Fire segmented control (0.50).** Each header is
  a `ToggleButton.FireSegmentItem` — the one `FireSegmentItemToggle`
  `ControlTheme` in `Styles/Fire.axaml`, the same component the editor's nav
  switcher wears — so the active tab is
  the layer's violet `.28` fill with white text, and the local underline +
  `AccentSubtle` pill template it carried since 0.16 is gone. What is still
  local, in `InspectorStyles.axaml`: `Border.TabStrip` (the full-width rail the
  segments sit on — deliberately NOT `Border.FireSegment`, whose rounded pill
  frame would read as a floating island inside a pane header),
  `StackPanel.TabLabel` (the icon + caption lockup; the `PathIcon`'s dim→accent
  tracking comes from Fire.axaml's own `:is(ToggleButton).FireSegmentItem
  PathIcon` rules) and `Border.TabCountBadge` (the FEHLER counter — `FireBadge`
  is accent-filled and has no danger spelling).
  - **A `ToggleButton`, never a `RadioButton`**: `IsChecked` is a OneWay binding
    onto `Is*TabSelected`, so a RadioButton's own group bookkeeping would fight
    it. `TabHeader_Click` handles "clicked the already-active tab".
  - **The strip is ALL labelled or ALL icons, and overflows into "More ▾" (#573).**
    One rule, `Controls/TabStripLayout.Decide` (pure, pinned by
    `UI/InspectorTabStripLayoutTests`): when every caption fits, every tab is
    labelled; otherwise every tab is an icon and only the ACTIVE tab keeps its
    caption; when even the icons do not fit, the "More ▾" button
    (`InspectorTabMore`) takes the end of the strip and the tabs that do not fit
    go into its menu — the active tab never does, the others fill the slots in
    tab order, the rest overflow in tab order. The active tab's slot is sized for
    the WIDEST caption, so picking a tab never changes the mode or how many tabs
    fit (the owner's complaint was a strip that reshuffles). `Controls/OverflowTabStrip`
    feeds it MEASURED widths, so German switches earlier on its own, raises
    `:icons` (the rule that hides unchecked captions is in
    `InspectorStyles/TabStripAndMixerRow.axaml`) and SHARES the width left over
    between the shown tabs, so the strip never ends in a gap. Every tab keeps
    one inset (`FirePadSegmentItemDense`) in both modes: the panel derives a
    tab's icon width by subtracting its caption from the measured tab, which
    only holds while the padding does not change with the mode. A hidden
    caption's width is the one last MEASURED while it showed (keyed by its text;
    a fresh text is measured on a probe `TextBlock`), so the decision cannot
    flip at the threshold. An overflowed tab stays in the tree (it must still
    be measured) but is arranged outside the clipped panel and loses
    `IsHitTestVisible` and `IsTabStop`. **The More menu's rows are built ONCE**
    (`Views/Editor/InspectorTabMoreMenu`, one bound row per tab) and opening it
    only toggles their visibility: rows added in `Opening` never reached the
    presenter and the menu opened EMPTY. The button is lit while its menu is
    open; its caption is not a `StackPanel.TabLabel`, so `:icons` leaves it.
    Measured 2026-10-09 (headless, Lyrics selected): labelled from 720 px in
    en-US, de-DE and ja-JP, icons at 560 and 420, five tabs + More at the dock's
    320 px floor. Only the caption hides: glyph, tooltip,
    `AutomationProperties.Name` and the Issues `TabCountBadge` stay.
    `Perf/InspectorTabStripUiPerfSmokeTests` lays the real view out at 320 /
    420 / 560 / 720 / 1100 px with every tab selected in turn and asserts no
    mixed state, no tab or caption clipped, no gap wider than a tab, overflowed
    tabs out of input, and the More menu listing and selecting them;
    `Perf/EditorShortWindowUiPerfSmokeTests` keeps the real shell's strip one
    row at 1280x720 and 1920x1080 with the selected tab labelled.
  - **The captions go through `{localization:Loc Key, Upper=True}`.** The mono
    face is uppercase-only, the resx values are mixed case, and the SAME keys
    are the Chakra header of the tab each one opens — so the transform is at the
    presentation layer, not in the resx. `LocExtension.Upper` routes through
    `Converters/TextConverters.ToUpper`.
- **ONE visual language across the tabs (#601, "Rail (dense)").** A tab with several sections —
  Sound, Mixer (Levels · Stems · Playback FX · EQ & filters · MIDI synth), Song (Key signature ·
  Tones · Sections · Phrases) and Lyrics (Raw lyrics · Selected syllable · Import / export) —
  shows them behind the same 38 px icon-only rail, ONE section at a time; a list tab (Issues,
  Annotations, Phrases, Riff library, Chord shapes) has none. No tab repeats its own name as a
  header (the tab strip says it): what stays at the top is ONE slim pinned row
  (`Border.InspectorHeader`, 28 px floor, `FirePadTag`, the summary in `TextBlock.HeaderTitle` at
  the UI face FireSizeXs bold) carrying the tab's summary and verbs, or the composer / status /
  navigation row a tab pins. The Sound tab's rail is a view model (`SoundSectionsViewModel`,
  [inspector-editing.md](inspector-editing.md)); the other three are pure layout,
  `Views/Editor/Inspector/InspectorSectionRail`: each entry is a `ToggleButton` whose `Tag` is the
  `x:Name` of its section body, the body and the accent bar bind `IsVisible` to the toggle's
  `IsChecked`, and the class lights exactly one toggle (a hidden entry — Stems without stems, the
  selected syllable without a selection — is never the open one). A section that does not fit
  scrolls inside the tab's one `ScrollViewer`. The Sound tab's #211 short-pane footer fold is
  gone with its footer: every verb is in its section, under ONE 28 px row, so
  `Perf/EditorShortWindowUiPerfSmokeTests` asserts the section body keeps 100 px at 1280x720.
  Pinned by `Perf/InspectorSectionRailUiPerfSmokeTests` (every entry named twice off one key,
  every section opens alone and measures, every body is in `InspectorView.NestedPaneNames`).
- **The Annotations tab (`NotesInspectorView`) spends its height on the LIST (#233).** The tab is
  ~220 px tall at 1280x720 (~490 at 1920x1080 on the 2:3 default split), so: the composer is the tab's ONE pinned row (#601, no header above it) at every width (note
  box `*`, kind picker, an icon-only `IconPlus` add button named and tooltipped off
  `Notes_AddAtPlayhead`; Enter in the box adds too), the scope sentence shows only while the list is
  empty (it is also the header's and the tab's tooltip), and a row is one ellipsised line (~28 px:
  the check box lays out at its 20 px glyph through `Margin="0,-6"`) whose full text is its tooltip
  and wraps when the row is selected. `Perf/EditorShortWindowUiPerfSmokeTests` seeds six
  annotations and asserts at least 3 whole rows at 1280x720, all six at 1920x1080, and a note box of at
  least 160 px (measured ~220 at 1280 px in all three cultures).
- **Shared inspector chrome lives in `Views/Editor/InspectorStyles.axaml`**
  (included once at the `InspectorView` root, so the class styles cascade to
  every child tab regardless of its own DataContext; `MixerInspectorView`,
  `PhraseCatalogView` and `StemMixerView` include it too). Reuse these instead of
  re-inlining chrome: `Border.InspectorHeader` (+ `TextBlock.HeaderTitle`) for
  the slim pinned row (#601: a summary and verbs, never the tab's name),
  `Border.InspectorFooter` for the pinned action bar,
  `Border.InspectorCard` for a titled grouping surface, `Grid.FieldRow` for an
  aligned label/control row (84px label rail + pointer-over tint — Grid has no
  CornerRadius, so don't add one), `StackPanel.InspectorEmpty` for the
  friendly "nothing selected" state (icon + hint), `TextBlock.TechniqueGroupLabel`
  for a group's eyebrow (the 0.24 icon+label `ToggleButton.TechniqueChip` went with
  the #601 dense technique rows, which wear worded `Chip Mini` chips), and (0.33)
  `Border.FretboardWell` (framed surface hosting an interactive
  `ChordBoxControl`) + `TextBlock.InspectorCardTitle` (card caption).
  Since #601 no tab carries a header that repeats its name: Issues and Phrases keep the slim
  pinned row for their summary and verbs, the other tabs pin their own row; Mixer/Notes/Issues
  lead with their primary action instead.
- **…and since 0.50 that file is the FOURTH converted grammar, on the Fire
  layer** — after `Views/Dialogs/DialogStyles.axaml`,
  `Views/Settings/SettingsStyles.axaml` and `Views/Shell/ShellChromeStyles.axaml`,
  and converted the same way: re-point the shared file, do not edit eight views.
  Every class NAME survived; every shape moved.
  - **Fire ladders ONLY** (`FireRadius*`/`FireSize*`/`FirePad*`/`FireGap*`/
    `FireSpace*`), pinned by
    `UI/FireStyleClassTests.EachConvertedGrammar_ReachesOnlyForFireLadderTokens`.
    Two app keys are exempt on purpose and are not ladder steps:
    `RowHeightField` (32) and `FieldLabelRail` (84) are this pane's own
    geometry — the Fire ladder's nearest row height is `FireFieldHeight` = 36,
    which is a full-width settings row.
  - **The file also RESTATES the app classes this pane leans on** —
    `Button.Subtle`/`Primary`/`Secondary`/`Danger`/`Pill`(+`.Compact`),
    `ToggleButton.Seg`, `ToggleButton.Chip`(+`.Mini`), `TextBlock.Label`/
    `Caption`/`HelpText`, `PathIcon.SectionIcon`, `Border.PaneHeader` +
    `TextBlock.PaneTitle` — because between them they carry ~150 controls in
    these eight tabs and converting without them leaves the surface half on each
    grammar. A `UserControl.Styles` include is applied AFTER the application's
    styles, so this file wins for its four includers and every other consumer of
    those classes keeps its old look. Same call `SettingsStyles` made for
    `Border.SettingsGroup`.
  - **Button chrome goes on `/template/ ContentPresenter`, never on the
    Button** — Fluent's Button template ROOT is the presenter, so a fill set on
    the control is painted over and the button renders as bare text.
  - **It MERGES `Styles/FireMenuChrome.axaml`**, which is the only way a
    dropdown's chrome can be reached (the ComboBox template sets its popup
    Border's fill from a `{DynamicResource}` INSIDE the template — a local value
    that beats any style setter). Without it the pane's ~26 pickers keep opening
    in Fluent's `#1c1d28` at radius 5. Merging it into the grammar SCOPES it: a
    resource lookup from inside a popup walks up to the control that opened it.
  - **No `Transitions` setter, ever** (all eight tabs are `IsVisible`-gated and a
    hidden control's style clocks keep ticking) — but a **`Cursor` setter IS
    allowed here**, unlike in the other three grammars: that ban exists because
    `SettingsSearchTests` builds settings tabs with no Avalonia platform, and
    nothing constructs an inspector view that way. Both halves are asserted by
    `FireStyleClassTests.TheInspectorGrammar_CarriesNoTransitionsSetter_ButMayCarryACursor`,
    and `NoInspectorSurface_AnimatesItsOwnChrome` extends the Transitions ban to
    the three views with style blocks of their own.
  - **Accent is information**: `InspectorCardTitle` is plain display text and the
    ONE accent mark in a section lockup is `PathIcon.SectionIcon`. A checked
    control (technique chip, `Seg`, M/S) wears a TINT plus an outline, never a
    solid fill.
  - Runtime guards: `Perf/InspectorConstructionUiPerfSmokeTests
    .{InspectorTabStrip_isTheSharedFireSegment_andEveryHeaderLightsWhenActive,
    TheInspectorChrome_ReallyWearsTheFireTypeLadder}` — the second one uses
    LITERAL type expectations (Chakra Petch 15, IBM Plex Mono 9.5 + 2.3
    tracking, radius 12), because deriving them from the same token the style
    reads would move both sides together.

- **The pane is prewarmed, and it took a walk of `BottomDock` to get there
  (0.61).** `IsVisible=false` skips `MeasureCore`, so until this landed every tab
  the user had not clicked THIS SESSION paid its whole first layout on the click,
  and no mechanism reached the pane at all: it is materialized by the app
  `ViewLocator` inside the dock, so it is named in no markup `MainWindow` can
  `FindControl` and `HiddenShellSurfaces()` could never list it. Now
  `MainWindowPrewarm.PrewarmableSurfaceOwners()` walks `BottomDock` for an
  `InspectorView` and yields **two** owners, parent before child:
  `InspectorView.HiddenSurfaces()` (the inactive tab panels, then the hidden rail sections of
  Mixer, Song and Lyrics, `InspectorView.NestedPaneNames`) and then
  `SoundInspectorView.HiddenSurfaces()` (its section bodies, `SoundInspectorView.SectionBodyNames`
  since #601 — every section of the rail, with Bends listed as its two hosts `NoteBendsBody` /
  `ChordBendsBody`; the prewarmer stops at the first hidden root on purpose, so the tab being
  realized buys the sections nothing). The walk
  finds nothing while the pane is CLOSED, which is correct — the view does not
  exist until the click that opens it builds it.
  - Measured by `Perf/InspectorPrewarmUiPerfSmokeTests` (headless, Debug,
    `r2r=no`, so directional): the seven tabs' first shows **544 ms → 2.4 ms**
    (Mixer alone was 287 ms, Lyrics 155), the five Sound cards **731 ms →
    11 ms** (Placement 376, Chord technique 195). Timings are LOGGED; what is
    asserted is that the sweep still finds the panels and that realizing one
    really measured its children.
  - **The card `x:Name`s are looked up by string.** A rename in
    `SoundInspectorView.axaml` silently drops that card from the prewarm; the
    perf class asserts each one still resolves, with that reason in the message.
  - Adding a tab means the panel count in
    `InspectorPrewarmUiPerfSmokeTests.TheSweep_FindsEveryInactiveTabPanel`
    changes — re-count the markup rather than guessing.
  - **The Mixer tab is realized a SECTION per prewarm turn** (#253): its one
    ~250 ms realize turn (Release R2R) became five. Since #601 the sections are
    the rail's bodies, so this is `InspectorView.NestedPaneNames` (every Mixer,
    Song and Lyrics section body, each its own turn after its tab) rather than a
    chunked `MasonryPanel`; `UI/HiddenSubtreePrewarmerTests` checks each name is
    declared once on a gated element.
  - **The startup prewarm runs ONCE, usually before a project is open (#141).**
    A tab whose rows come from the project is realized EMPTY, and the project
    then fills its lists behind `IsVisible=false`; those rows were laid out by
    the click. The Song tab therefore owns a `Views/Shell/HiddenSurfaceRewarmer`
    (`SongInspectorView.Rewarmer`): any of its `ItemsControl`s changing while the
    tab is hidden schedules one `HiddenSubtreePrewarmer.RealizeStale` through
    `PrewarmPacer`. **Its two `ListBox`es stack while short and virtualize only
    past `Converters/ItemsPanelConverters.StackedRowLimit`** — a
    `VirtualizingStackPanel` behind a hidden tab has no effective viewport, so it
    measures to zero rows and no prewarm can ever build them. Measured over a real
    chart (4 tones, 9 sections): the first Song click **125-330 ms → ~1 ms**
    (headless, Debug). Pinned by
    `Perf/SurfaceShowRewarmUiPerfSmokeTests.TheSongTab_LaysOutRowsThatArriveWhileItIsHidden_BeforeTheClick`.
    **Every tab panel has one since #175** (`InspectorView.TabRewarmers`; the Song
    tab's own is reused), because the owner's log still measured Mixer 214 / 158 ms,
    Song 202 ms and Phrases 322 ms on the first visit: a project or arrangement
    switch also flips `IsVisible`, text and enabled state that no list announces.
    So `InspectorView` watches `ProjectState` (`ActiveProject` / `ActiveArrangement`,
    subscribed only while attached) and calls `HiddenSurfaceRewarmer.Request()` on
    every hidden tab. Pinned by `Perf/InspectorTabRewarmUiPerfSmokeTests`
    and `.ALongSongTabList_StillVirtualizes`. Another tab with the same shape
    takes the same two lines; nothing applies it automatically.
- **The Song tab's Sections and Tones are the Phrases panel's shape (#246)**: one
  `Views/Editor/Inspector/MarkerGroupsView` each, over `MarkerGroupsViewModel`
  (`SongOverviewViewModel.SectionsPanel` / `.TonesPanel`, contract
  `IMarkerGroupsScreen`). A row per name folds open (`MarkerFold`, shared with Phrases
  in `InspectorStyles/ScopeAndIssueChrome.axaml`) to its PLACES, each a jump through
  `IChartEditorSurface.ExecuteSeek`. Both are project-wide, so a place is a moment of
  the song, listed once with the arrangements it is in when there are several. Every
  place label — Phrases' iterations too — is `MarkerOccurrenceLabel`: "#2 · bar 17 ·
  0:42.0", the bar by `LiveBarNumbers.BarAt`. Rename is in place (Apply/Enter only
  once the text changed, Esc puts it back); rename and delete are one undo node each.
  `UI/MarkerGroupsViewModelTests`, `UI/MarkerOccurrenceLabelTests`,
  `Perf/SongInspectorMarkerGroupsUiPerfSmokeTests`.
- **The Song tab's Tones list is the project's TONES, not its tone changes (#181)**:
  every key the project defines or references (`ToneKeyEdits.Keys`), so a designed
  tone nobody placed is listed with no places, and a placed key with no definition
  wears the `MarkerWarning` ("plays the game's default tone"). Rename and Delete go
  through `ToneKeyEdits` — every store, one undo step — and a refused rename (a
  collision, an invalid key) prints its reason in `MarkerRenameRefusal` under the row;
  the next keystroke clears it. `UI/SongOverviewToneRenameTests`,
  `Perf/SongInspectorToneMarksUiPerfSmokeTests`.
