# The inspector PANE — dock context, the tab strip and its shared chrome

Part of the inspector / misc-surfaces reference. Router: [../misc-surfaces.md](../misc-surfaces.md).

## The D inspector, as built (#751)

The inspector is design D in production (the gallery's `Pages/Inspector` D page stays as the
design reference). Detail per tab follows below and in [inspector-editing.md](inspector-editing.md)
and [inspector-mixer-and-fehler.md](inspector-mixer-and-fehler.md).

- **Tabs.** Eight, always alive and `IsVisible`-gated in `InspectorTabPanels`: Note (Tag 0) and
  Chord shapes (7) for the selection, a divider, then Song (2) · Lyrics (3) · Issues (4) ·
  Annotations (5) · Riff library (6) · Mixer (1). Note, Issues and Mixer carry a section strip
  under the tab strip (`SoundSectionsViewModel`: Task · Library with nothing selected, else
  Placement / Shape / Selection · Technique · Bends · Findings by what is selected;
  `IssuesSectionsViewModel`; `MixerSectionsViewModel`: Stems · EQ · MIDI).
- **Card pages.** Every tab body is a `Controls/ColumnFlowPanel` of `ColumnFlowCard`s laid out by
  the pure `ColumnFlowTiers` (pane width: Tiny < 480 px one card at a time on the pager;
  Small 480–619, the primary 530x370, the first card plus a `CardPager` column; Usual 620–899
  the rest stacked beside it, paged while the body is short; Wide >= 900 a column per card).
  A tier change re-lays the SAME children: nothing is rebuilt per resize, per section or per
  tab switch, and every card page is named so `InspectorView.PrewarmableSurfaces` warms it.
- **Footer.** Each tab pins one footer row (never inside a `ScrollViewer`). On the Note tab it is
  `StagedFooterViewModel`: queue filter and previous / next match, the scope (or the "Apply to"
  ladder, `SoundApplyBarView`, over a chord) with the count Apply writes, Apply, and the ask bar
  (`NoteFooterAsk`; other tabs' destructive verbs use the inline `InspectorConfirmRow`): Confirm / Cancel when Apply reaches more than one sound, skips one,
  writes one in part or replaces a bend; Stay / Discard / Apply when a page is left with a draft.
- **Staged edits.** Placement, Selection, Technique (note and chord) and Bends stage into
  `InspectorStagedEdits` and are written by Apply as ONE history node (pure rules:
  `StagedPlacement`, `StagedBulk`, `StagedTechnique`, `StagedBends`; plan: `StagedEditPlan`).
  Off-neck targets are skipped and named. Timeline and keyboard edits stay immediate; a history
  change under a draft re-reads its baselines. Task, Library, Shape (its own Save), Findings and
  the song-side tabs write at once, except the Song tab's details (their own Apply).
- **Pending-draft rule.** A draft is kept per sound; a section switch or queue step that would
  leave it asks Stay / Discard / Apply; a timeline click leaves it on its sound and the footer
  names it ("1 draft on another sound", one click back); a selection change drops only the
  Selection page's draft; another project drops all. Drafts are unsaved work:
  `IChartEditorSurface.HasPendingDrafts` makes the close / replace prompt ask, and saving from
  that prompt applies them (`ApplyPendingDrafts`); a plain Ctrl+S leaves them staged.

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
  bullet below. **Since #493 the STRIP order is grouped: Note (Tag 0, the old
  "Sound" tab, code names unchanged) and Chord shapes (7) are about the
  SELECTION, then a divider, then the SONG tabs Song (2) · Lyrics (3) · Issues (4) ·
  Annotations (5) · Riff library (6) · Mixer (1)** — only the markup order moved,
  every `Tag` and `Is*TabSelected` is as below. The divider is the
  `OverflowTabStrip`'s own hairline (`GroupGap`, `DividerBrush`, the `GroupStart`
  class on the Song header: the gap is part of the width the strip decides
  against, so it cannot overhang), pinned by `UI/InspectorTabGroupingTests` and
  `Perf/InspectorGroupedTabsUiPerfSmokeTests`. **Since 0.36 the tag order is Sound(0) ·
  Mixer(1) · Song(2) · Lyrics(3) · FEHLER(4) · Notes(5)** — the
  Note and Chord tabs merged (see the Sound-tab bullet below), so every
  `Is*TabSelected` and every `Tag` shifted
  down by one — and **since 0.42.4 the Riff Library is a tab**, not a dock pane
  (**tab 6 since #291**, when Phrases left the strip for the Song tab).
  **Tab 7 is Chord shapes (#486)**, the first inspector tab on the target
  architecture: `InspectorViewModel.ChordShapes` is the `IShapePanelScreen`
  contract (`Presentation/Screens/Editor/ShapesPanelViewModel`, a DI singleton from
  `EditorModule`), hosted as `Views/Editor/ShapesPanelView` with its DataContext
  bound to it — no `ViewLocator`, no adapter VM. `ShapesPanelView` reports effective
  visibility through `IScreenLifecycle` and `EffectiveVisibilityWatcher`, including
  hidden ancestors and detach. `Shared/ScreenRefreshScheduler` coalesces visible
  history / arrangement / level changes; hidden changes wait for ONE refresh on
  show, so generic tab prewarm cannot rebuild an unseen card catalogue. A queued
  pass also parks if the view hides before it runs. Project replacement releases
  the old rows and pending verbs immediately; show reads the live graph, including
  undo/redo and edits in an inactive arrangement. Rows are reused by template.
  `Presentation/Editor/ShapesPanelVisibilityTests` and
  `Perf/ShapesPanelVisibilityUiPerfSmokeTests` pin this boundary without timing assertions.
  The card list is `Controls/ShapeRowsList`: a plain `ItemsControl` (in the pane's own
  `ScrollViewer`) over STRIPS (one per row of the adaptive columns) in a
  `VirtualizingStackPanel`, so only the strips in view exist. It is NOT a `ListBox`: a strip
  has no selection, item chrome or automation peer, and the list takes no key (its
  `OnKeyDown` skips the base, which would eat the editor's bare Home / End / arrow
  shortcuts). Tab is the one key it helps: from a strip's outermost tab stop it realizes the
  next strip first, so Tab and Shift+Tab walk every card
  (`Perf/ShapesPanelKeyboardUiPerfSmokeTests`). The view reports shown / hidden through
  `Views/Shell/ScreenViewPresence` (shown while ANY view of the screen shows it: float and
  redock build the new view before the old one leaves). The
  strips are updated IN PLACE (a new catalogue hands each `ShapeCardStrip` its new rows;
  replacing `ItemsSource` made the panel recycle and re-realize every container in view),
  and `ShapeStripCards` keeps a strip's card visuals and swaps their data context, so an
  arrangement switch or a scroll step builds no card. Whatever must follow the ROW
  (the current card, a name being edited, a drag's source) is therefore held by row in
  `ShapesPanelView` / `ShapeCardDrag`, never on a card visual.
  `Perf/ShapesPanelVirtualizationUiPerfSmokeTests`.
  Opened from the tab strip and the palette's
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
  `TextBox`, applied on Enter or when it loses focus; a nameless shape also gets the chord namer's name as a one-click
  `ShapeUseSuggestion` button, never over a deliberate No name), a wrapping row of the use count, the labelled bulk-edit
  tick and No name, a dot when the shape needs attention, the problem with its one fix, and ALL its actions (Place, Edit,
  Select uses, Next use, Merge…, Delete) (#290: nothing is hidden until hover, there is no `.ShapeReveal` and no reserved
  strip; an action row wraps to a second line when a card needs it, so nothing is clipped at the pane's minimum width).**
  `ShapesPanelView.axaml.cs` keeps the last pressed or focused card current (the `current` class, a fill only). Nothing is truncated: names, issues and counts wrap. A merge (Merge…
  then Merge here, or the Merge fix) and a delete of a used shape wait in ONE
  confirmation bar (`ShapesPendingBar`) that says what moves where. Edit and the bulk
  bar are wired (#266, #270), and so is drag-to-merge (#271): `Controls/ShapeCardDrag` (four attached commands on the card;
  a press on the blank card or its diagram arms it, 6 px of travel begins it and takes the pointer, the card under the
  pointer is hit-tested, release drops, Esc or a release over nothing ends it, the list scrolls within 28 px of its edge)
  drives `Presentation/Screens/Editor/ShapesDragVerbs`, which picks the source exactly as Merge… does and ends in
  the same bar through `ChordShapeEdits.PlanMerge`; a drop on other frets, an arpeggio mismatch or an unused source
  says that plan's refusal on the status rail and changes nothing. A card's tick
  (`ShapeTick`, `CanTick`) is labelled "Select" and wraps in the use-count row. The bulk bar
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
  The ONE **Phrases** panel is a card of the Song tab (#291), beside Sections and Tones
  (`SongPhrasesCard`, wearing the same row grammar as `MarkerGroupsView`: a `ListBox`, "places" wording,
  the Song tab's single Refresh in the information card re-reads it too via
  `SongOverviewViewModel.PhrasesPanel`; `Perf/SongPhrasesSectionUiPerfSmokeTests`): a
  STANDALONE `PhrasesInspectorViewModel` (injected seams, headless-tested) behind
  the catalog contract `IPhrasesScreen` (gallery fake `FakePhrasesScreen`), composed
  as `InspectorViewModel.Phrases`. Rows come from `PhraseMarkers.Iterations` (both
  phrase stores, what the exporters read), rebuilt when the Song tab is activated and on
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
    `:icons` (the rules that hide unchecked captions and show their glyphs are in
    `InspectorStyles/TabStripAndMixerRow.axaml`). Since inspector design D (#751)
    the labelled strip is captions only, packed at the start (`ShareSlack` off);
    each tab's glyph (`PathIcon.TabGlyph`, `OverflowTabStrip.IsIconsOnlyGlyph`,
    fixed 14 px) shows only on the unchecked tabs of the icons fallback, so every
    tab stays ONE click away where the captions do not fit (the review of P1
    caught a captions-only strip that put tabs into More at 480 px and in
    German at 530). A strip with tabs in More shares its slack, so it never ends
    in a gap beside the More button. The panel derives a tab's two widths from
    the measured tab: minus the caption and the icons-only glyph while shown,
    plus the caption for labelled, plus the glyph (from its fixed width while
    hidden) for icons; every tab keeps one inset in both modes. A hidden
    caption's width is the one last MEASURED while it showed (keyed by its text;
    a fresh text is measured on a probe `TextBlock`), so the decision cannot
    flip at the threshold. An overflowed tab stays in the tree (it must still
    be measured) but is arranged outside the clipped panel and loses
    `IsHitTestVisible` and `IsTabStop`. **The More menu's rows are built ONCE**
    (`Views/Editor/InspectorTabMoreMenu`, one bound row per tab: the glyph, the
    name and a live copy of the tab's badges, `TabBadgeMirror` — the Issues
    count, and on the Note tab's section strip the Bends dot and the Findings
    count) and opening it
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
    tabs out of input, and the More menu listing and selecting them; at 530 x 370
    and 480 x 370 in en-US, de-DE and ja-JP it asserts every tab in the strip,
    and that an overflowed Issues row carries the count;
    `Perf/EditorShortWindowUiPerfSmokeTests` keeps the real shell's strip one
    row at 1280x720 and 1920x1080 with the selected tab labelled.
  - **The captions go through `{localization:Loc Key, Upper=True}`.** The mono
    face is uppercase-only, the resx values are mixed case, and the SAME keys
    are the Chakra header of the tab each one opens — so the transform is at the
    presentation layer, not in the resx. `LocExtension.Upper` routes through
    `Converters/TextConverters.ToUpper`.
- **ONE visual language across the tabs (#601, "Rail (dense)").** The Mixer (Stems · EQ · MIDI)
  shows its sections behind design D's section strip (#751 P8). The Note tab, Chord shapes, Song,
  Lyrics, Issues, Annotations and the Riff library have moved to inspector design D (#751): a `Controls/ColumnFlowPanel` of cards (Tiny < 480 px pages every
  card; Small 480–619 keeps the first card in the left column and pages the rest) over a pinned
  footer — see "Design D project tabs" below and [inspector-editing.md](inspector-editing.md). No tab repeats its own name as a
  header (the tab strip says it): what stays at the top is the section strip or the
  composer / status row a tab pins; its verbs sit in its cards and its footer. The Note tab's sections are a view model (`SoundSectionsViewModel`,
  [inspector-editing.md](inspector-editing.md)). The old pure-layout section rail
  (with its stacking rule and its section-header toggle theme) was deleted in #751 P8 once no tab hosted it (Song and Lyrics are cards, the Mixer has its section strip).
  The Sound tab's #211 short-pane footer fold is gone with its footer: every verb is in its section,
  under ONE 28 px row, so `Perf/EditorShortWindowUiPerfSmokeTests` asserts the section body keeps
  100 px at 1280x720.
- **Design D project tabs (#751).** Each is a `ColumnFlowPanel` of cards over a pinned footer
  (`FireSurfaceFooter`, 28 px floor, never inside a `ScrollViewer`); a card body scrolls with a
  "More below" cue, a list card (`ColumnFlowFrame`) pins its own head and foot. Cards stay
  `IsVisible=true` and paged-away ones are measured off-stage, so they need no prewarm entry.
  - **Song** (`SongCards`: information · sections · tones · phrases; footer `SongFooter`): Title,
    Artist, Album and Year are a DRAFT (`SongOverviewViewModel.Details`, `SongDetailsDraft`):
    Apply writes all four plus Key and Minor (staged too) as ONE `SetSongMetadataAction`, Revert /
    Esc drops the edits, a re-read keeps edited fields and lets untouched ones follow the chart;
    ANOTHER project drops the draft (`ReadNewProject`, after the prompt). It is a pending draft
    like the inspector's staged pages (`IChartEditorSurface.AddPendingDraft`): Ctrl+S leaves it
    staged, the one unsaved-changes prompt before a close or a replace counts it, and that prompt's
    Save applies it (`SaveInitiator.Prompt`); Discard drops it, Cancel keeps it. Refresh tidies every marker list (time
    order, exact duplicates gone) as ONE undo node (`SongMarkerTidy`) and re-reads. Sections and
    Tones each carry an "All groups" filter (`MarkerGroupsViewModel.GroupFilterIndex`); Tones picks
    the active arrangement's starting tone (`StartingToneChoice`, one `SetStartingToneAction`).
    "More details…" opens File ▸ Song metadata. `UI/SongDetailsDraftTests`,
    `UI/SongOverviewKeySignatureTests`, `UI/ViewModels/Shell/ProjectMenuViewModelTests`.
  - **Lyrics** (`LyricsCards`: review list · syllable · raw text · files; footer `LyricsFooter` =
    the tap status + Apply): the list is `LyricsInspectorViewModel.Syllables`
    (`LyricSyllableList`: every syllable or only the flagged ones; a picked row is the editor's
    selected syllable and seeks there, clicking it again seeks again, Previous / Next step the rows
    shown). A history change rebuilds the rows only when the vocal track changed; while the tab is
    hidden the list only goes stale and catches up once when shown (`IsShown`, set by
    `InspectorTabActivation`). The status line re-words on a language switch.
    `UI/ViewModels/Editor/LyricSyllableListTests`.
  - **Annotations** (`NotesCards`: list · details; footer "Add at playhead"): a filter (all /
    open / resolved), a details card that rewords (Enter / lost focus) and retypes the selected
    annotation as one undo node each (`SetAnnotationTextAction`, `SetAnnotationKindAction`), a
    Resolved card (`ResolvedAnnotations`, every resolved one whatever the filter, with Reopen and
    Jump), and a DRAFT for a new one, placed at the time its heading showed when it opened.
    Another project drops the draft and its text. `UI/ViewModels/Editor/AnnotationsInspectorCardsTests`.
  - **Riff library**: see [../phrase-catalog.md](../phrase-catalog.md).
  - All four at 530x370 and 480x370 in en / de / ja: `Perf/InspectorProjectTabsUiPerfSmokeTests`
    (in the release layout filter); its riff case seeds long en / de / ja riff names, a picked riff
    and a selection and fails any ellipsized line or text cut at a card's bottom, also in the
    1280x720 editor window.
  - **Note tab, #751 P5 pages** (cards in `SoundInspectorView`, one view per card under `Views/Editor/Inspector/Sound*View`):
    - **Library** (nothing selected; `ChordLibraryViewModel`): the searchable template list (a shared size group keeps the
      frets column aligned), the picked template's READ-ONLY grip (`ColumnFlowFretboard.LanePitch` = 18 px a string, so
      an 8-string grip grows instead of squeezing), Uses and the Template card. An arrangement or level switch rebuilds
      the list while the page is shown. "Next use" only SEEKS (selecting would leave the page, which exists only with
      nothing selected); "Select uses" selects the active level's uses, and the count names that set ("3 uses · 4 in
      all" when other levels use it too). `UI/ViewModels/Editor/ChordLibraryUsesTests`, `TemplateSearchTests`.
    - **Bends** (`StagedBendsViewModel`): the curve plot, presets (wrapping captions, no tooltip: the caption is whole)
      and a scope card whose "Bend scope" picker is the SAME ladder as the footer's "Apply to" (both kept on
      `Ladder.Effective` by `Views/Editor/Inspector/ApplyRungPickerSync`, which forwards only a user's pick). Delete
      point is enabled only with a selected point; a loaded curve that peaks above the range raises the range to the
      smallest that holds it. `UI/ViewModels/Editor/StagedBendsPageTests`.
    - **Findings** (`SelectionFindingsViewModel`): "Fix all" asks first in the footer's ask bar ("Fix N findings
      across M sounds?", `StagedFooterViewModel.AskToConfirm`) and writes every default repair as ONE undo step, each
      planned on the live chart as it runs; a chord's fingering finding offers "Open grip editor" (the Shape page). The
      repair count is worked out once per re-read and only while the page is shown (entering it re-reads).
      `UI/ViewModels/Editor/SelectionFindingsFixAllTests`.
    - **Task**: the triage, export-readiness and playhead cards; the pen / strings rows keep the keyboard readout's
      tooltip (`Inspector_Queue_KeyboardTip`).
    - Laid out at 530x370 (6 and 8 strings) by `Perf/NoteShellUiPerfSmokeTests.ThePageParts_LayOut`, which also
      asserts the whole Shape Save set visible in the footer, untrimmed preset captions, the library grip's pitch and
      the two pickers on one rung.
  - The Annotations list keeps its rows in a short window: `Perf/EditorShortWindowUiPerfSmokeTests` seeds six
  annotations and asserts at least 3 whole rows at 1280x720, all six at 1920x1080, and a note box of at
  least 160 px (measured ~220 at 1280 px in all three cultures).
- **Shared inspector chrome lives in `Views/Editor/InspectorStyles.axaml`**
  (included once at the `InspectorView` root, so the class styles cascade to
  every child tab regardless of its own DataContext; `MixerInspectorView`
  and `PhraseCatalogView` include it too). Since #751 the card pages, cards, pagers,
  section strips and footers are `ColumnFlow*` themes and controls
  (`Styles/ColumnFlowCardTheme.axaml`, `Controls/ColumnFlowPanel` / `ColumnFlowCard` /
  `CardPager`); what the grammar still owns is the pickers and restated app classes,
  the tab strip and mixer fader rows, `StackPanel.InspectorEmpty` (the friendly
  "nothing selected" state), `Border.FretboardWell` (the frame around an interactive
  `ChordBoxControl`), `TextBlock.InspectorCardTitle`, the `Border.FieldIssue` marks and
  the provenance badge. #751 P9 deleted the header / footer / card / field-row /
  disclosure / scope-bar / pending-bar classes and the whole EQ-module part of the grammar
  once no view wore them (the EQ module is `ColumnFlow*` cards over `ColumnFlowEqPlot`).
- **…and since 0.50 that file is the FOURTH converted grammar, on the Fire
  layer** — after `Views/Dialogs/DialogStyles.axaml`,
  `Views/Settings/SettingsStyles.axaml` and `Views/Shell/ShellChromeStyles.axaml`,
  and converted the same way: re-point the shared file, do not edit eight views.
  Every class NAME survived that conversion; every shape moved.
  - **Fire ladders ONLY** (`FireRadius*`/`FireSize*`/`FirePad*`/`FireGap*`/
    `FireSpace*`), pinned by
    `UI/FireStyleClassTests.EachConvertedGrammar_ReachesOnlyForFireLadderTokens`.
    One app key is exempt on purpose and is not a ladder step:
    `RowHeightField` (32) is this pane's own geometry (`FieldLabelRail` went
    with #751) — the Fire ladder's nearest row height is `FireFieldHeight` = 36,
    which is a full-width settings row.
  - **The file also RESTATES the app classes this pane leans on** —
    `Button.Subtle`/`Primary`/`Secondary`/`Danger`/`Pill`(+`.Compact`),
    `ToggleButton.Seg`, `ToggleButton.Chip`(+`.Mini`), `TextBlock.Label`/
    `Caption`/`HelpText`, `Border.PaneHeader` +
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
  - **Accent is information**: `InspectorCardTitle` is plain display text. A checked
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
  the Mixer, the Issues bodies and the Lyrics syllable body, `InspectorView.NestedPaneNames`) and then
  `SoundInspectorView.HiddenSurfaces()` (its section bodies, `SoundInspectorView.SectionBodyNames`
  since #601 — every card of every section of the rail, the #751 P5 Task / Library / Bends / Findings cards
  included; the prewarmer stops at the first hidden root on purpose, so the tab being
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
    the rail's bodies, so this is `InspectorView.NestedPaneNames` (every Mixer
    section body, each its own turn after its tab) rather than a
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
  `SongOverviewViewModel` refreshes this project-wide metadata on project replacement
  and history edits, not on active arrangement changes. `UI/SongOverviewRefreshCostTests`
  pins the subscription and still checks that authored marker edits reach the lists.
- **The Song tab's Tones list is the project's TONES, not its tone changes (#181)**:
  every key the project defines or references (`ToneKeyEdits.Keys`), so a designed
  tone nobody placed is listed with no places, and a placed key with no definition
  wears the `MarkerWarning` ("plays the game's default tone"). Rename and Delete go
  through `ToneKeyEdits` — every store, one undo step — and a refused rename (a
  collision, an invalid key) prints its reason in `MarkerRenameRefusal` under the row;
  the next keystroke clears it. `UI/SongOverviewToneRenameTests`,
  `Perf/SongInspectorToneMarksUiPerfSmokeTests`.
