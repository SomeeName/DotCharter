# What the inspector EDITS — sounds, strings, chords, lengths, lyrics

Part of the inspector / misc-surfaces reference. Router: [../misc-surfaces.md](../misc-surfaces.md).

- **The `Sound` tab (0.36, editing-rework stage 5).** Tabs 0 (Note) and 1
  (Chord) are ONE tab, `Views/Editor/Inspector/SoundInspectorView.axaml`. They
  authored the same thing — a sound — through two headers, two footers and
  three fretboards, one of which defaulted to rewriting every chord in the
  level. What is on screen is now a function of the SELECTION, not of which
  header was clicked: `InspectorViewModel.SelectionKind` (`SoundSelectionKind`)
  is derived from `ISelectionService` in the order **hand shape → sound count →
  kind**, and the `Show*Card` booleans off it gate every block. The matrix and
  its rationale are the 2026-07-25 editing-rework audit §3–§4.
  - **Bends render for exactly ONE sound.** States 4–6 get the one-line
    `BendSummary` readout instead — a curve editor over eight different curves
    cannot be told the truth about any of them (rscharter does the same).
  - **The chord box renders for states 1–3 and 6 only.** A multi-selection gets
    `SelectionShapeSummary` (fret range · strings · distinct shapes), never an
    ambiguous diagram.
  - The old **`WarningFaint` "applies to all" strip is cut** — it used the
    warning colour for an entirely normal state. Every card header now carries
    `AppliesToLabel` ("applies to 8 sounds") instead.
  - **The tab is a STRIP of sections over ONE section's card, ONE section at a
    time (#601; since #751 in inspector design D's shell).** Which sections the
    strip offers is a function of the selection type —
    `ViewModels/Editor/Inspector/SoundSectionsViewModel` (`Inspector.Sections`, fed
    `SelectionKind`): nothing selected → Task · Library; a note → Placement ·
    Technique · Bends · Findings; a chord → Shape · Technique · Bends · Findings; a
    hand shape → Shape (+ Span) · Findings; shape + sounds → Shape · Technique ·
    Findings; several sounds → Selection · Technique · Findings. The open section
    is remembered PER TYPE for the session (a chord stays on Technique across a
    detour through a note). The strip (`NoteSectionStrip`) is one row of caption
    toggles in `ColumnFlowNavToggle` (`Styles/ColumnFlowCardTheme.axaml`, app-wide), each
    named and tipped off ONE key, packed at the start of an `OverflowTabStrip`
    with `IconsFallback` and `ShareSlack` off (a caption that does not fit goes
    into the strip's More menu); the selection summary ends the row. The open
    entry is lit (accent text, 2 px underline), Findings carries a count badge (red
    when a finding blocks the export), Bends a dot when the selected sound bends
    (`SelectionHasBends` — the strip never switches sections by itself, so it MARKS
    a bent sound; the #118 auto-opening Bends disclosure and `BendDisclosurePolicy`
    are gone). **Playback is not a section:** the whole-chord flags (Accent,
    Ignore, Pass other, Split notes, No notes) are the "Whole chord" group of a
    chord's Technique section, with the repeat line under them and "Clear
    techniques" in the group's header (it resets the staged draft: `StagedChordTechniqueViewModel.Reset`).
    Each section is a `Controls/ColumnFlowCard` in the `ColumnFlowPanel`
    `NoteCards` (D's column flow: tiers by width in `ColumnFlowTiers`, the paging
    plan in the pure `ColumnFlowPages`, a `CardPager` when cards are paged; the
    card scrolls inside its own slot and shows a "More below" cue; at the Tiny tier the cue rides at the right
    end of the pager row instead (`ColumnFlowCard.CueHosted`), so the card keeps its whole height). Under every
    section, PINNED with no scroller above it: the footer (`NoteFooter`, a
    `Controls/FooterRail`): on the left the filter (`NoteFooterFilter`), ‹ › = the
    queue's own step, the "?" help, the playhead time (`NoteFooterTime`, `m:ss.cc`
    from `Inspector/InspectorPlayheadClock`, refreshed like `PlayheadLabel`: while
    stopped and once when playback stops) and `Queue.PositionLabel`; on the right,
    over a chord's Shape, Technique and Bends only
    (`SoundSectionsViewModel.ShowApplyBar`), ONE "Apply to" ladder
    (`SoundApplyBarView`: the #484 ladder as a picker that always shows
    `Ladder.Effective` — closed, by its short label `ChordApplyRungRow.ShortLabel`
    ("every use"), named "Apply to" — and the SHORT reach `Ladder.EffectiveShortDetail`
    beside it, the whole one in the tooltips; it is a preview host, the job the
    deleted `ChordApplyScopeBar` had). The footer is ONE row: short of room, the rail drops
    the place in the queue, then the time (`FooterRail.DropOrder`), and stacks the
    ladder over the navigation only when it still does not fit whole (the dock's
    320 px floor); it never squeezes the ladder. The #485 preview (its own ‹ ›)
    and the refused reshape hint stand in a strip PINNED over the footer
    (`NoteApplyStrip`), outside the card scroller. In the markup the footer and
    that strip come before the cards, so Tab reaches the ladder before the card's
    controls. Pinned by `Perf/NoteFooterUiPerfSmokeTests`. No section repeats its
    name as a heading. Each section is a view
    of its own —
    `SoundTaskView`, `SoundPlacementView`, `SoundShapeView`, `SoundBulkCardView`,
    `SoundNoteTechniqueView`, `SoundChordTechniqueView`, `SoundFindingsView` —
    hosted in a card named by the tab for the prewarm (`SoundInspectorView.SectionBodyNames`); lints
    that read the tab's markup read the `Sound*View.axaml` set. The technique
    sections are DENSE GROUPED rows ("label | value", worded chips, icon
    segments). Pinned by `SoundSectionsViewModelTests` (default gate) and
    `Perf/NoteSectionStripUiPerfSmokeTests` (the strip, the pinned footer, the
    default size acceptance, the chips, the picker, no clipped text at 320–600 px),
    `UI/ColumnFlowTiersTests` (the tiers and paging plans) and
    `Perf/NoteShellUiPerfSmokeTests` (every section at 530x370, 480x370, 640x450 and
    1100x450 with 6 and 8 strings: one card, the footer pinned and uncut, both
    strips one row); both headless classes are in the release's culture filter.
  - **A chord's "Per string" header IS the string mask (#601).** One chip per
    string the selected chords play (`StringMaskViewModel.Chips`, synced from
    `NotifyMaskScopeChanged`), in the string's colour and named the way the lane
    gutter names it ("A (5)", `GuitarStringLabel`); a chip toggles the SAME mask
    as the chord box's pips and `Ctrl+1..9`, so a per-string technique goes to one
    string without leaving the section. The scope sentence beside it names the
    strings the same way (`Inspector_Scope_SomeStrings`), "Clear mask" shows while
    a mask is set, and ⧉ copies the active string's techniques to all strings.
  - **Nothing selected leads with the NEXT task (#601).** `SoundNextTaskViewModel`
    (`Inspector.NextTask`) orders the queue's own counts — fingerings, then names,
    then export blockers — into one sentence and one verb ("Show first problem" =
    `Queue.TriageFilter`), the other open tasks one line each, then the playhead
    block (with the keyboard readout) and the two verbs the old footer carried
    (auto-fill, the export check). The "needs work" tiles, the pinned footer and
    the short-pane fold (#211) are gone: every verb is in its section.
  - **The auto-switch shrank.** With Note and Chord merged there is nothing to
    auto-switch between, so `OnUserOrProgrammaticSelection(kind)` is just "go to
    Sound unless the tab is pinned". The pin, and the rule that an issue jump
    never switches tabs, both survive.
- **Sound fingering rows share the Chord panel's `DisplayStringInputs`.**
  They follow `ProjectState.InvertStrings` live; Up/Down between fret boxes uses
  `ChordTemplateEditorViewModel.AdjacentStringInput` in that visual order. String and pitch labels use
  `Core/Utilities/TuningDisplayNames`; persisted `Arrangement.Tuning` is not a
  display spelling table. `NewChordNames` gives the inspector, Shapes panel, and
  Edit Shape dialog the same spelling used to name new templates. Suggestion
  lists remove enharmonic duplicates. The selected naming style's resolved key
  takes precedence over the tuning. Authored names stay verbatim.
- **One fretboard: `Controls/ChordBoxControl` (0.36, was `MiniFretboardControl`).**
  `Role` (`MoveNote` / `Shape` / `ReadOnly`) decides what the primary click
  means, so two look-alike instruments can no longer mean different things. It
  owns a dedicated **mask-pip rail column** (x < 14) so "toggle the string mask"
  is never the same click as "play the open string"; the open-string column runs
  to the nut at x = 40. `Mask` is `ProjectState.StringEditMask` — the SAME value
  the lane headers draw. Hover gestures: bare digits set the fret through the
  pure `FretDigitAccumulator` on the SHEET's 800 ms window, `T`/`Shift+1..4` set
  the finger (into the buffered draft, not committed), `Del`/right-click clears
  the string. **One typing run is ONE history node** — a continuation digit
  undoes the run's own node first, anchored to the history node id. The perf
  contract is unchanged: invalidate only on a changed hover cell, and the VMs
  must keep reassigning `Chord`/`Template`/`MutedStrings`/`IssueStrings` as
  FRESH references (`Mask` is a value type for the same reason). The pure
  `ChordBoxGeometry` + `DigitFor`/`FingerFor` are unit-tested in the default
  gate (`ChordBoxGeometryTests`).
  The 800 ms window gates both fret accumulation and `ContinuesRun`: an expired
  run starts a separate undo step (`SoundEntryUiPerfSmokeTests`).
  - **Every fret the panel WRITES goes through
    `ChordFingerDraftEditor.SnapFretToNeck` → `SoundingPitch.SnapToNeck`**
    (legal set `{0} ∪ [capo, 24]`, direction 0 = absolute, so a sub-capo write
    lands ON the capo). The POINTER path cannot produce an illegal fret —
    `ChordBoxGeometry.DisplayFrets` floors its window at `capo + 1` and its open
    rail returns the capo — **and that is exactly why the KEYBOARD paths shipped
    without it**: hover-typing builds a `ChordBoxFretEdit` straight from the
    accumulator (which caps only the TOP, `MaxTypedFret`) and the per-row fret
    box `int.TryParse`s anything, so `3` under a capo 5 authored an `L01` into a
    SHARED template and `99`/`-4` landed verbatim. The open-string finger strip
    reads the SNAPPED fret, or a fixed `L01` becomes an `L06`. The fret box's
    TEXT is deliberately NOT rewritten — it commits per keystroke, so coercing
    mid-run would turn "12" into "52". Pinned by
    `UI/ViewModels/Panels/ChordTemplateCapoFretTests`.
- **There is exactly ONE per-string selector: `ProjectState.StringEditMask`.**
  The inspector's `ChordStringTargets` (defaulting to every string SELECTED,
  against the mask's empty) is DELETED. Masking strings on the lane headers and
  then clicking a technique chip used to apply it to all strings.
  `ApplyChordNoteMutation`, `SetFingerOnSelectedStrings` and the technique
  readout all go through `InspectorViewModel.MaskedChordNotes` (internal, pure,
  unit-tested), the inspector subscribes to `nameof(ProjectState.StringEditMask)`
  so `Ctrl+1..9` updates the panel, and every scope bar quotes the mask in
  words. Guarded by `InspectorStringMaskTests`, including a source lint that
  fails if a second selector reappears.
- **A chord's slide target is ONE number with OFFSET semantics (#530)** — EOF's
  (`rs.c` exports `fret + slideend - lowestfret` per string) and rscharter's
  (`GuitarSoundSelectionEditor.setSlide`): the typed number is the target of the
  lowest-FRETTED masked string, and every other masked fretted string slides by
  the same number of frets; an open string neither anchors nor moves. The pure
  `ViewModels/Editor/Inspector/ChordSlideScope` owns it (`Read`, `Plan`,
  `PlanOffset`). When the masked strings disagree the field is blank and
  `ChordSlideSummary` spells each one out ("A (5): 5→6 · D (4): holds 8"); a
  target that would put any string outside frets 1–24 changes nothing and shows
  `ChordSlideRefusal`. That revert is POSTED: the binding that wrote the refused
  number ignores a value raised during its own write. "Apply techniques to all
  strings" (the staged Technique page's "Copy to N") copies the slide as an interval too. Pinned by
  `ChordSlideScopeTests` (default gate, incl. a lint that the inspector asks the rule),
  `ChordCopyTechniquesTests` and `SoundTabFitUiPerfSmokeTests` (headless, through the real field).
- **One selected chord says whether it ships as a repeat, and why not (#564).**
  `ChordRepeatVerdictLine` in the chord's Technique section, "Whole chord" card (`SoundChordWholeView`), binds
  `InspectorViewModel.ChordRepeatVerdict`, refreshed in `SyncChordProperties`
  (so on every selection change and history step): `ExplainRepeat` asks the
  export's own `RepeatChords.Explain` over the active level, and
  `Inspector/RepeatVerdictText` words it ("Repeat: no — vibrato on string A
  (5)"). Hidden for no chord or several. Pinned by
  `InspectorRepeatVerdictTextTests` (default gate) and
  `SoundTabFitUiPerfSmokeTests.ASelectedChord_SaysWhetherItShipsAsARepeat`.
- **The work queue: `SoundQueueViewModel` (0.36).** EOF's `F` dialog,
  generalised, on the Sound tab's pinned navigation row (#601). A STANDALONE sub-VM with
  injected seams (`InspectorViewModel.Queue`) — the same shape as
  `PhrasesInspectorViewModel`/`AudioLintViewModel`, which is what keeps the VM
  exception small. Seven filters with LIVE counts (any / missing fingering /
  unnamed / untemplated / no FHP coverage / has an issue / blocks export, the
  last two over `InspectorIssueFieldMap`); `‹‹`/`››` are `QueuePrevMatch` /
  `QueueNextMatch` on `Ctrl+PgUp`/`Ctrl+PgDn` (new rows, so no
  `NormalizeKeybinds` migration). It **selects** the sound rather than only
  seeking — that is the difference from the `SelectNextMissingFingering` button
  it subsumes, because the panel's fields have to be live and the per-string
  verbs need a target — and it **wraps with a visible end-of-list state**
  (`Wrapped`) instead of silently doing nothing. `CurrentIndex` is 1-BASED with
  `0` meaning "the selection is not in the queue", so a forward step from 0
  lands on the FIRST match. Counts ride `RecomputeIssues`' existing 150 ms
  debounce; **do not add a second timer.** It works on `ITimedElement`, NOT
  `IGuitarSound` — a `Chord` does not implement the latter (it is the per-STRING
  contract) and casting to it throws. Regression: `SoundQueueViewModelTests`.
  **The counter follows the CURRENT selection (#611):** a click in the chart
  changes the selection without an edit, so the queue listens to
  `ISelectionService.SelectionChanged` and re-locates the selection in its last
  match list (no data read, nothing while it is deferred); a multi-selection,
  nothing, or a sound outside the filter shows "—", and ‹ › step on from the
  clicked sound. **Its row is ONE line since #601:** the filter is a funnel
  button in the row's star column whose label trims (the #134 "ny sound (240)"
  overhang cannot recur — there is no `ComboBox` with a `MinWidth` in it);
  `Perf/InspectorNarrowWidthUiPerfSmokeTests` asserts every part inside the row
  at 400 and 320 px.
  - **It triages the ACTIVE level (0.41), which is a fourth injected seam.** It
    used to take `arr.Levels.FirstOrDefault()` — level 0, the THINNEST
    difficulty. On any multi-level chart (every multi-level package import, and
    everything after Tools → Generate Levels, which deliberately leaves the
    editor on level N-1) the stepping, the filter counts and all three health
    tiles described a level nobody had on screen — and because stepping
    **selects**, the technique verbs then acted on a sound from the wrong level.
    The host passes `() => _projectState.ActiveLevel`, and the
    `ProjectState.ActiveLevel` change already routes into `ScheduleIssueRefresh`
    → `Queue.Refresh()`, so switching level re-triages with no new wiring. The
    fallback to level 0 when the ambient level is null or belongs to ANOTHER
    arrangement is deliberate: the two seams are read independently and can
    disagree for a dispatcher turn during an arrangement switch. Because the seam
    is a delegate, the behavioural tests pass just as well with the host wiring
    `() => null` — `TheInspectorWiresTheQueueToTheAmbientActiveLevel` is a source
    lint over that call site for exactly that reason.
  - **A refresh is DEFERRED while the Sound tab is hidden (0.61), and the
    filter list is updated in place.** `RebuildIssueGroups` (now on
    `InspectorIssuesViewModel`, which hands the queue to the inspector's
    `IInspectorIssuesHost.OnIssuesRecomputed`) called
    `Queue.Refresh()` unconditionally, on every committed edit, while the very
    next lines deferred the FEHLER groups for the same reason: nothing outside
    the Sound tab (`SoundInspectorView.axaml`, `SoundTaskView.axaml`) binds
    anything the queue produces. It takes the
    same flag now — `MarkStale()` off-tab, `EnsureCurrent()` from
    `OnSelectedTabIndexChanged`, `IsStale` for the tests. **The keybinds are why
    this is not just a copy of the groups' rule:** `QueuePrevMatch` /
    `QueueNextMatch` fire from any tab, so `Step` and `TriageFilter` call
    `EnsureCurrent()` themselves — a deferral may not change what a gesture
    does. And `SoundQueueFilterOption` is an `ObservableObject` with settable
    `Count`/`Label` rather than a record: `Filters` was `Clear()` + seven
    `Add`s per refresh (eight `CollectionChanged` into the bound `ComboBox`),
    and because the records were VALUE-equal a changed count also made
    `SelectedFilter` unequal to itself and re-entered `OnSelectedFilterChanged`
    mid-refresh. One stable instance per filter, only `Display` changes.
- **The empty state is designed, not defaulted (0.36).** With the pen on, a
  charter spends whole minutes with nothing selected. State 0 now carries the
  playhead block (`PlayheadLabel` + one click into the queue), three **health
  tiles** that ARE the queue's filters pre-counted (clicking one sets the filter
  and jumps to the first match — the triage loop used to be discoverable only as
  a button inside a collapsed expander inside another tab), and the **Chord
  Library**: the old browse mode, named. In the Library the chord box is
  `ChordBoxRole.ReadOnly` with the reason on it, which kills the
  `ChordTemplates.First()` trap BY CONSTRUCTION — browse mode can no longer
  reshape anything, where before the model refused a browse-mode reshape
  silently. Name, fingering and arpeggio stay editable there, exactly as browse
  mode always permitted.
  - **Each count says what it counts, and the export verdict has ONE home
    (#119).** The blockers tile counts SOUNDS on the active level whose LINT
    findings would stop the export ("sounds with blocking errors"); its tooltip
    (`Queue.BlocksExportTileTip`, `Inspector/ProblemCountText`) adds the
    finding count (`Queue.BlocksExportFindingCount`) and points at the Issues
    tab. The Issues tab badge counts the findings the Issues LIST shows, and says so
    (`IssueBadgeTip`). That list leaves out the findings on easier difficulty levels
    other than the one on screen until "Show easier difficulty levels" is ticked
    (#397: `InspectorIssuesViewModel.OnEasierLevels` over `Editing/Quality/QualityLevelScope`,
    the rule the Chart quality report dialog used; a finding that stops the
    export always shows). Pinned by `InspectorEasierLevelIssuesTests`.
    The dialog is gone (#497): everything it showed lives on this tab. Since inspector design D (#751 P8) the tab
    is four cards on one `ColumnFlowPanel` (`IssuesFindingsCard` = `IssuesListView`, `IssuesFixesCard` =
    `IssuesDetailView`, `IssuesProjectCard` = `IssuesProjectView`, `IssuesReviewCard` = `IssuesReviewView`), as many
    side by side as the pane is wide and the rest behind the card pager, whose captions keep the old rail's badges
    (`IssueRowConverters.PageTitle`: the findings count, a dot; the view follows the pager through
    `ColumnFlowPanel.ShownCardsChanged`, raised only when a card moves on or off stage, never per layout pass). `InspectorViewModel.IssuesSections`
    (`IssuesSectionsViewModel`, `IssuesSection`) owns which is open; `IssuesInspectorView.CardFor` maps it to a card
    and a page click back to it. **Findings** (default): this arrangement's list and the all-clear state; a row
    whose rule the fix menu can repair carries Fix (`InspectorIssuesViewModel.FixIssueCommand`; Fix shows only when
    `IssueFixViewModel.CanOffer` — the same check the fix menu's `Offer` makes — finds a fix: jump, then the default
    fix, one undo step), and a click on a finding brings the **Fixes** card on screen with that finding (jump, the
    "Export blocker / Needs review" status, the offline About flyout — message, code, status, the fix menu's remedy,
    "Ask the assistant" — the fix menu, the AI row, or a disabled "No default fix"), its row marked by the accent
    spine (`IssueListRow.IsOnFixesCard`). The list is ONE flat list of row models (`Issues.Rows` =
    `InspectorIssueRows`, `IssueListRow`: no `$parent` lookups, message / stamp / Fix check computed on first read),
    with a Difficulty filter once the arrangement has levels (arrangement-wide findings always show). Identical
    findings (same code and English sentence) fold into ONE group row with a count (`IssueRowKind.Group`, `×75`)
    that expands to its occurrences, each still a jump. A WARNING row carries Ignore (a group row Ignore all); an
    ignored finding leaves the count and the list until Show ignored lists it with Stop ignoring. The ignores are
    `ChartProject.IgnoredFindings` (`dcIgnoredFindings` in the `.dcproj`, one undo step each through
    `SetIgnoredFindingsAction`); `Services/Editing/Validation/FindingIgnores` matches code + sentence + a hash of the
    subject (the element's fields, a lyric's whole line), so an edit to the subject brings the finding back, and
    never matches an Important finding. Export validation and Fix chart errors do not read the list
    (`FindingIgnoresTests`, `InspectorIssueIgnoreTests`, `Perf/IssuesIgnoreGroupUiPerfSmokeTests`). Auto-fix
    asks first through the card's `InspectorConfirmRow` over an `InspectorPendingAction`
    (`Issues.AutoFixConfirm`, withdrawn by a project / arrangement switch or an undoable edit) — the confirm
    grammar every design-D reset shares. Not virtualized on purpose: the list's ScrollViewer sizes from its content
    inside a card, and a `VirtualizingStackPanel` there realized one row and sized the viewport to it
    (`Perf/IssuesLanguageSwitchUiPerfSmokeTests`). **Whole project**: the verdict, totals, an Arrangement choice over
    the project's findings (`ExportReadiness.ProjectFindings` = `ProjectFindingsList`: Jump makes the arrangement
    active and seeks; Fix only for the active arrangement, with the same `CanOffer` check), one row per arrangement (a row makes that arrangement
    active and opens Findings) and, when there are any, a song-wide row for the findings no arrangement owns (the
    D13 lyric checks, vocal export findings; it opens Findings and keeps the arrangement), so the rows add up to the
    totals (`ProjectFindingCount`, `WholeProjectReportTests`), then Audio check and Export readiness; its dot says a finding sits in some
    arrangement, the project is not ready or an export check found something. **Editor on Fire**: the round trip
    (`ExportReadinessViewModel.ChartReport`), checked while its section is open OR its card is on screen beside
    the others (`IssuesSectionsViewModel.ShowEditorOnFire`, `UI/ViewModels/Editor/IssuesSectionsEditorOnFireTests`;
    `IEofRoundTripSection.IsExpanded` follows, and a project switch that closes it falls back to Findings — or, with the
    card still on screen in a wide pane, computes the round trip again); a whole Review row seeks (`EofPlaceRow`); its dot shows once a computed round trip drops something. **Doors**: the status rail counters, Jump to
    issue and the Sound tab's selection findings open Findings; Tools ▸ Chart quality report (palette entry too),
    the Rocksmith export's "Show in Issues tab" (`ChartIssuesDoor`) and the Sound tab's export verdict
    (`ShowExportReadinessCommand`) open Whole project; the Editor on Fire export's "Show" opens Editor on Fire
    (`MainWindowViewModel.OpenChartIssues(section)`). The check runs `ChartQualityReporter` over a
    `QualityReportSnapshot` OFF the UI thread (the refresh only posts; a stale result is dropped), on Refresh and when
    the tab opens. "Check export" (`ExportReadiness.CheckExportReadinessCommand`) is bound by `IssuesInspectorView`
    ONLY. Pinned by `InspectorProblemCountTests` and, for the layout and the doors,
    `Perf/IssuesTabCardsUiPerfSmokeTests` (in the release's de-DE / ja-JP filter).
- **Field-level validation: `ViewModels/Editor/Inspector/InspectorIssueFieldMap`
  (0.36).** Two pure tables — `Map(type)` → `InspectorField` and
  `BlocksExport(type)` — both **exhaustive over `ArrangementIssueType` BY TEST**,
  not by a compiler-checked switch: a new code must be given an explicit home
  rather than degrading to `Card` (or to "blocks nothing") forever.
  `BlocksExport` is paired with the twelve `ChartExportValidator.Key*` constants
  that error, and `InspectorIssueFieldMapTests` reads those constants by
  reflection so the editing and export validators cannot drift apart silently.
  Findings for the current selection render as `Border.FieldIssue` with
  `Classes.Error`/`Classes.Warn` bound directly (Avalonia class binding — no
  converter, and **no transition**: an error must appear instantly), the
  offending string gets a pip on the chord box rail, and the header carries a
  clickable count badge that reads danger when anything would make the export
  refuse.
  - **One row per PROBLEM, not per note (#117).** The lint emits one finding
    per chord note with a message that omits the string, so
    `SelectionFieldIssues` folds the selection's findings by (type, message)
    through the pure `InspectorFieldIssue.Fold`: one row, a `Count`, and a
    `Detail` naming the strings ("— strings E (6), A (5)"). The header badge
    (`SelectionIssueCount`) counts those rows. The fold is memoized on the
    IDENTITY of the `SelectionIssues()` list, so it shares that memo's
    invalidation. Since #572 the rows sit behind a one-line disclosure (the
  count, the export colour, the first row's code and sentence —
  `SelectionIssueSummary` / `SelectionIssueHeadline`). The FEHLER tab's grouped list is untouched. Pinned by
    `InspectorIssueFoldTests` and `Perf/SoundTabFitUiPerfSmokeTests`.
- **Validation can see the PROJECT, and null means UNKNOWN (0.57).**
  `IArrangementValidationService.Validate` takes an optional `ChartProject?`,
  carried on `ArrangementValidationContext.Project`, for the facts an arrangement
  cannot hold — the authored tone BODIES above all. **A rule reading it must
  no-op on null**, or a caller that omits the project turns a partial context
  into a screenful of false positives. Two callers pass it:
  `InspectorIssuesViewModel.RecomputeIssues` and `BatchChartLintService`; the parameter is
  optional so old callers still compile, which is exactly the shape that let
  `CoopRigApplier` ship unreached — hence a source lint in
  `ToneSilenceAndPhraseRuleTests` that the inspector really passes it.
  The first rule to use it is **D04 `ToneChangeWithoutDefinition`**: a tone change
  naming a tone the arrangement DECLARES but the project owns no body for. It is
  distinct from D01 `ToneChangeWithoutMatchingTone` (a chart contradicting
  itself) because this chart is internally consistent and still plays SILENTLY —
  which is why nothing reported it: the gap check only ever compared tone changes
  against the arrangement's slots, so every psarc-opened chart validated clean
  while not one of its tone changes could be heard. Matching goes through
  `Core/Utilities/ChartToneNames`, which resolves the way the rig does (trimmed,
  case-insensitive, key OR name) — anything stricter calls a tone missing that
  the rig then plays.
- **Strings are named ONE way: the lane gutter's (#118).** Every place the
  Sound tab names a string — the Placement card's String picker, the chord-bend
  string picker, the queue strip's mask readout, a folded issue row — goes
  through the pure `Inspector/GuitarStringLabel`: the tuning name and the
  guitarist's number counted from the HIGH string ("E (6)" for the low E on six
  strings), exactly what `TimelineGutterControl` prints. The String box used to
  show the 0-based model index and the readout a 1-based one from the LOW
  string plus `StringEditMask.ToString()`'s hard-coded "all". The String picker
  is a `ComboBox` whose `SelectedIndex` IS `NoteStringIndex` (a `-1` write
  while its items are replaced is ignored). The status rail's `MASK n` still
  names the `Ctrl+digit` keys. Pinned by `InspectorStringLabelTests` and
  `StringMaskViewModelTests`.
- **One pane height per section of one note (#118, #601).** At the owner's 694 x 534
  pane a plain note's Placement section and its Technique section each fit
  without scrolling: `Perf/SoundTabFitUiPerfSmokeTests` binds the real
  `InspectorViewModel` to the UI sweep's stress chart and asserts each section's
  bottom edge, and with `DOTCHARTER_UI_SWEEP_DUMP=1` writes PNGs to
  `TestResults/inspector-fit/`. What bought the room: Bends is a section of its own
  (the rail marks a bent sound); the pitch rides on the Placement section's first
  line; String/Fret/Length/Slide-to are ONE 2x2 grid with one spinner chrome
  (Length is a fraction `TextBox` inside a `ButtonSpinner.InspectorNumber`,
  `InspectorStyles/PickersAndAppClasses.axaml`); the technique groups are one
  dense row each; `▸ MORE` hides when it has no badge (`HasProvenanceBadges`);
  and "Clear techniques" (was "Reset flags") sits on the technique section's
  first line, so a single sound has no footer. A zero-length
  note shows `0` (`SustainBoxText`), and the bend graph's own button is "Clear
  bend".
- **The length editor (0.36).** `Sustain` was read-only and its tooltip named a
  timeline drag gesture that does not exist. It is now editable and
  GRID-RELATIVE (owner-confirmed, never ms) through the pure
  `Controls/FractionalLengthFormat`: below a beat it spells the note value (a
  beat IS a quarter, so half a beat is `1/8`), from a beat up it speaks in
  beats, triplets get a three-based denominator, and parsing yields an EXACT
  numerator/denominator so no double round trip re-introduces drift. Commits on
  `LostFocus`; the spinner's up/down go through the EXISTING `AdjustSelectedNotesSustain` so
  they, `]`/`[` and wheel rule 1 are one implementation. **It re-reads the model
  after every commit** and says "shortened to the gap before the next sound"
  when `ChartSpacingEnforcer` took length back — the user is never told a value
  stuck when it did not. `▸ MORE` carries the tier-3 read-only provenance badges
  (`VibratoStrength`, `GraceFret`, `NotationDuration`, `IgnoreDerived`): they
  round-trip in the model, nothing in the app authors them, and invisible is
  worse than read-only. **`NotationDuration` is a BADGE by owner decision** —
  notation-only, never gameplay, never exported to RS, and the sheet palette
  already owns editing it.
- **Techniques on the ladder (#581).** Every chord technique and playback flag
  the Sound tab writes goes through `InspectorViewModel.ApplyChordMutation`,
  which asks `ChordTemplateEditor.TechniqueTargets()` →
  `ChordApplyScope.TechniqueTargets(rung)` — the SAME scope the ladder counts
  and the #485 preview marks, never a second resolver. "this chord" is the
  panel's chord and "selected" is the WHOLE selection (any shape), which keeps
  the inspector's own write path (`ChordTechniqueTargets.IsSelection`), exactly
  as before #581; "every use" and "+ the same grip" are `Members(...)` of the
  shape edit's target set, chords only, on every level. The write is
  `DotCharter.Services/Editing/Highway/ChordTechniqueEdit.Build`: one
  `MutateChordsAction` per level against THAT level's list and spacing pass
  (`IHighwayEditingService.GetLevelSpacingUpdater`), ONE history node; templates
  are never touched. The mask picks strings by STRING index on every target
  chord (`MaskedChordNotes`). The ladder counts/previews technique reach while
  a chord's Technique or Bends section is open (`SoundSectionsViewModel.ApplyFamily` →
  `ChordTemplateEditorViewModel.ApplyFamily`), the shape's otherwise.
  `SoundApplyBarView` is a preview host of its own, so the marks show over
  Technique and Bends with the chord panel hidden. With no chord in the selection (a note,
  a multi-selection; `SoundSectionsViewModel.HasChordScope`) nothing pins a
  ladder, so edits stay on the selection. Pinned by `ChordTechniqueApplyToTests`
  (default gate) and `NoteSectionStripUiPerfSmokeTests.OverTheTechniques_*` (headless).
- **Apply step: optional, OFF by default (#805).** Settings ▸ Editor ▸ "Inspector edits wait for Apply"
  (`AppConfig.InspectorApplyStep`, mirrored live into `ProjectState.InspectorApplyStep` at startup and on the
  tab's Save; read as `InspectorStagedEdits.ApplyStep`). OFF: a page still stages, but
  `StagedPageViewModel.Staged` → `InspectorStagedPages.Staged` writes it at once through
  `InspectorStagedEdits.ApplyNow` → the same `Apply` (same plan, targets, skips; ONE undo step per edit, the
  status rail says what was written, or why nothing landed) and drops the draft even when nothing landed — so the
  leave prompt, the close prompt and `NoteDraftsElsewhere` never see one. The footer hides only the Apply button
  (`StagedFooterViewModel.ShowsApplyButton`); the scope button/text stays and counts the sounds an edit reaches.
  Selection ▸ Move and Findings' "Fix all" still ask through the footer. ON: everything below, unchanged.
  Switching it off with a draft waiting runs `ApplyAll` (never dropped). Pinned by `InspectorImmediateApplyTests`
  and `EditorSettingsViewModelTests`; the staged tests set it ON in their setup
  (`InspectorStagedEditsTests.Engine(applyStep: true)`).
- **Staged Apply engine (#751 P3; Placement, Selection and Technique bind it since P4, Bends in P5).** `Inspector/InspectorStagedEdits`
  (`Inspector.StagedEdits`, a plain collaborator, not DI) stages Placement, Selection, Technique and Bends
  edits per source sound and writes them with ONE `Apply` (one history node; chords through
  `ChordTechniqueEdit.BuildPerChord`, a mutation per chord, on every level the rung covers). `Plan(page)`
  → `StagedEditPlan` (writes / skips / "only in part" notes on written targets, each with a
  `StagedSkipReason` / replaced bends; a target that already holds every staged value is not counted,
  by the history node's own `ChartActionStateComparer` test), `Review(page)` → the footer sentence
  (`Inspector_Staged_*`; confirm when >1 target, a skip, a part or a replacement; a sound Apply still
  writes is never listed as skipped), `Discard`, `CanLeave` (the leave guard; the Selection draft is
  dropped on any change of the selected sounds instead), `PlanMove`/`ApplyMove` (Selection ▸ Move). The
  rules are pure:
  `StagedTechnique` (per-string values by string index, silent strings named, a slide copied as an
  INTERVAL and SKIPPED on an off-neck target instead of refusing the edit — decision 2), `StagedBulk`
  (tri-state reads: a chord whose strings disagree reads mixed; "no staged value lands there"; a chord
  slide moves only fretted strings inside the string mask), Reset keeps the slide of a target whose
  staged slide would leave the neck,
  `StagedBends` (only an edited curve writes; a target's differing authored bend is named),
  `StagedPlacement` (a fret move carries the slide end). Selection ▸ Move by frets is
  `IHighwayEditingService.MoveSelectedFretsWhole` (`WholeFretMove`: whole sound or none, open strings
  stay, slide ends move with their strings; the fret KEYS still clamp per string); by strings, one up or
  down only; by sustain, the keyboard's `]`/`[` (`AdjustSelectedNotesSustain` on the timeline's
  `GridDivisor`), planned by `PreviewSelectedNotesSustain` (`SustainStepOutcome`: the same floor,
  next-sound gap, audio-end clamp and linked rule, so plan = write). A history change
  re-reads the baselines (`Rebase`: a staged technique value the sound now holds by a keyboard edit is PARKED,
  so Apply never reverts it, and comes back when an undo returns to the node before that edit); "still in the
  project" reads `ProjectSoundIndex`, rebuilt only when a level gains, loses or swaps a sound. Another level or
  arrangement KEEPS each sound's draft (a sound that left the project is pruned); another project drops every
  draft. A draft counts as unsaved work (`IChartEditorSurface.HasPendingDrafts` →
  `ProjectSessionService.HasUnsavedWork`), so close asks; the prompt's Save (`SaveInitiator.Prompt`) runs
  `ApplyPendingDrafts` → `ApplyAll` first (every draft, one undo step; a draft left on another sound writes that
  sound only). A chord "Apply to" rung or family change re-reads the footer's plan. Pinned by
  `InspectorStagedEditsTests` and `InspectorStagedApplyTests`.
- **The staged Note pages (#751 P4, design D).** `Inspector/InspectorStagedPages` (`Inspector.Staged`)
  holds one view model per page — `StagedPlacementViewModel` (Placement: the board, Position and Import
  cards), `StagedSelectionViewModel` (Selection: Move, Values for Apply, Summary), `StagedNoteTechniqueViewModel`
  and `StagedChordTechniqueViewModel` (Technique: a note's techniques and detail; a chord's string card, one
  string at a time with its key lit and a Mask key; its Chord techniques card (#953, `SoundChordAllStringsView`,
  the partial `StagedChordTechniqueViewModel.AllStrings.cs`): the note's rows — mute, HOPO, harmonic, picking with
  a none option, Accent, Tremolo — staged on EVERY masked string of every chord the ladder reaches in one stage
  (one undo step), a row lit nowhere when those strings differ (`ChordAllStringsTechniqueTests`); and its Whole
  chord card) — and `StagedFooterViewModel`.
  A page control STAGES and writes nothing; the footer's Apply writes at once for one sound with nothing
  passed by and otherwise shows the review sentence with Confirm / Cancel in the footer's place
  (`NoteFooterAsk`); a draft that lands nowhere still offers Apply, so the review can say why. Leaving a page
  with a draft (the section strip, the footer's ‹ ›) asks Stay / Discard / Apply; a selection or history change
  withdraws an open question with its held action (`StagedFooterViewModel.Dismiss`). A timeline click, level or
  arrangement switch away from a draft keeps it: `NoteDraftsElsewhere` over the footer names it and jumps back
  (`JumpToDraft` → `InspectorStagedPages.Show`). A stage re-reads the staging page, the page on screen and the
  footer only; the footer works its plan out once per re-read. "Copy to strings" puts the slide on the movers
  only (`ChordSlideScope.Movers`, never an open string) and carries the vibrato depth. The Placement sustain
  spinner stops at the audio end. Selection ▸ Move writes at once through the same ask. Timeline and keyboard edits stay immediate. A re-read must not stage: the
  pickers' item lists are kept while they hold (a new list resets a ComboBox, whose reset writes its first
  item back). Under Split notes, No notes takes no edit and its STORED flag stays. Pinned by
  `InspectorStagedPagesTests`, `ChordCopyTechniquesTests` (bug c: Copy stages the per-string Ignore) and
  headless `NoteShellUiPerfSmokeTests` (every card of every page at the design's sizes, and "showing a page
  stages nothing"), `NoteSectionStripUiPerfSmokeTests`, `NoteFooterUiPerfSmokeTests`.
- **The Task, Library, Bends and Findings pages (#751 P5, design D).** Each page is several `ColumnFlowCard`s.
  **Task**: Fix the next sound (`SoundNextTaskViewModel`; the three triage rows always shown, disabled at zero),
  Export readiness (`ReadinessTitle` / `ReadinessDetail`, the Issues tab's check, Generate all fingerings) and At
  the playhead. **Library** (nothing selected): `Inspector/ChordLibraryViewModel` (`Inspector.Library`) searches
  the templates by name AND frets (`TemplateSearch`, "x07") through a `CoalescedFilterPass` into a fixed-height
  virtualizing list, each row its frets and name; picking one browses `ChordTemplateEditorViewModel` (a
  non-visual collaborator now: the old `ChordTemplateEditorView` and `ChordApplyScopeBar` are deleted) through
  `Browse` (never re-templating a chord; Edit template opens the Edit shape dialog on the pick), whose fields —
  template picker, New, name and suggestions, Arpeggio, No name, Nop, Pinned, containing shapes, Fingerless,
  finger rows (frets read-only), Save fingering / Save & next / Next missing / Generate all — are the Template
  card (`SoundLibraryTemplateView`); Grip draws it read-only, Uses has Select uses / Next use (a seek only). **Bends**:
  `Inspector/StagedBendsViewModel` (`Staged.Bends`) builds the inspector's note and chord `BendEditorViewModel`s
  so a curve edit or preset STAGES (`InspectorStagedEdits.StageBend`) and the footer's Apply writes it onto the
  rung, remapped per target; the curve is `Controls/ColumnFlowBendPlot` (28 px handles, drag, click to add,
  Tab / arrows / Delete / Insert; a curve that stops before the sustain's end holds flat to the right edge,
  `BendEditorViewModel.HoldFrom`, display only), with presets (`ColumnFlowBendPreview` glyphs) and a Scope card (rung, ‹ ›
  targets, chart target, range, points; the rung is also picked here as "Bend scope"). **Findings**: `Inspector/SelectionFindingsViewModel`
  (`Inspector.Findings`) — the list (pick → Detail), Open Issues, Fix all (n) (`IssueFixViewModel.DefaultFix`,
  asked first in the footer, ONE undo step for all), Detail (Open this issue, Jump to sound, Open grip editor) and Review (Previous / Next sound). The
  Shape page's Undo save / Save / Save and next / Save & next missing sit in the footer (`NoteFooterShapeSave`).
  Pinned by `BendScopeApplyTests`, `TemplateSearchTests` and headless `NoteShellUiPerfSmokeTests.ThePageParts_LayOut`.
- **…and so do the chord bend editor and the technique KEYS (#604).**
  - **Bends.** The chord curve stages on the Bends page and the footer's Apply commits it through the same
    `ApplyChordMutation` (`Inspector/BendCurveRemap.ChordWrite`), so the picker pins over a chord's
    Bends section too and the rung, the preview and the write are the
    technique family's. A bend is per string and the bend picker's string is
    its mask: the curve lands on that STRING (by index) of every target, a
    target without it is left alone; the string-edit mask does not widen it.
    Before #604 the curve already followed the rung while no picker showed
    over Bends — the one edit broader than its bar.
  - **Bend times follow each target (#751).** Bend points are absolute chart
    positions, so every target (chord string or selected note) gets the curve
    through `BendCurveRemap.Remap`: moved onto ITS onset, each point keeping its
    fraction of the source sustain, clamped into the target's sustain
    (`BendCurveRemapTests`, `ChordBendApplyToTests`). The Technique page's "Copy to
    strings" (`StagedChordTechniqueViewModel.CopyToStrings`) stages the per-string
    Ignore flag too, onto the played strings the string-edit mask includes, and the
    slide as an interval (`ChordCopyTechniquesTests`).
  - **Keys.** `IHighwayEditingService.TechniqueReach` is the inspector's
    `Inspector/ChordTechniqueKeyboardReach` (set in the inspector's
    constructor): every technique verb (mute, HOPO, harmonic, picking,
    vibrato, tremolo, link-next, accent, ignore, pass-other, and each group
    variant) asks it, and on a rung past "selected" writes `LadderTargets()`'
    chords through `ChordTechniqueEdit.BuildPerLevel`, in ONE node with the
    selected notes, and says so on the status rail. It follows the rung only
    while a ladder is ON SCREEN (`ChordApplyPreview.IsHostShown`), only for
    the user's own selection (never an implied hand-shape or pen target) and
    only for the selection the ladder was resolved on. Fret and string keys
    never ask: a shape change is the chord panel's own edit.
  - Pinned by `ChordTechniqueKeyboardApplyToTests` and `ChordBendApplyToTests`
    (default gate, the shared `ChordLadderFixture`; "selected" is deep-compared
    against the same key with no ladder wired) and
    `NoteSectionStripUiPerfSmokeTests.OverTheBends_*` /
    `TheMuteKey_FollowsTheLadderOnScreen_AndOnlyTheLadderOnScreen` (headless).
  - Known limit: over a chord's SHAPE section the pinned ladder previews the
    shape's reach, so a technique key there marks the bars too (the chord
    marks are the same set from "every use" up).
- **Reach: the "Apply to" ladder, `ChordTemplateEditorViewModel.ApplyRung`,
  DEFAULT "selected" (#484).** Four rungs — this chord (+ its hand shape) ·
  selected (N) · every use of this shape · + the same grip elsewhere — each with
  a LIVE count off `ChordApplyScope`, the resolver the planner writes through
  ("every use" counts every chord AND hand shape in the ARRANGEMENT bound to the
  template, which is what the in-place edit really rewrites — never say "all
  chords"). It replaced the 0.36 two-state `ScopeIsThisChordOnly` switch and the
  "Finger N alike shapes" button (detail in
  [../editor-input/chord-verbs.md](../editor-input/chord-verbs.md)). **Every LEVEL, since 0.60.1** — a
  `ChordTemplate` is arrangement-scoped, and both the count and the rewrite read
  `ChartEditorViewModel.PlacedChords`, i.e. the ACTIVE level. So the button
  offered "all 3" for an edit that reshaped thirty, and the rewrite mutated the
  shared template in place while re-projecting notes onto ONE level's chords:
  every other level's chords kept their old notes under a template that no
  longer describes them, which is the drift resolve-or-create exists to prevent.
  The ISOLATE branch was widened to every level in a163cfe4 and its comment
  names this exact failure as what happens when an edit "falls through to the
  in-place global rewrite" — the rewrite itself was not widened with it, so
  choosing "all N" DELIBERATELY did what the isolate fix had just stopped
  happening by accident. It bites on every psarc import and everything after
  Tools ▸ Generate Levels. The **anchor** sync stays on the ACTIVE level, because
  `_editor.Anchors` is the active level's list. Pinned by
  `UI/ViewModels/Panels/ChordTemplateScopeAcrossLevelsTests`. The rung is sticky per session, never persisted — a
  working mode like the grid divisor and the mask. The fret `TextBox` now
  commits on `UpdateSourceTrigger=LostFocus`, so typing "12" over "5" is one
  reshape, not a reshape to fret 1 followed by one to 12.
  The default is pinned by `ChordTemplateEditorViewModelTests`.
  **Compact and pinnable since #572**: the ladder is its own view,
  the old `ChordApplyScopeBar` (deleted; the footer's `SoundApplyBarView` picker has the rungs now), its rungs side by side
  with only the LIT rung's count printed under them (`Ladder.EffectiveDetail`)
  and every rung's count in its tooltip (`ChordApplyRungRow.ToolTipText`). The
  chord panel embeds it; the Sound tab turns that copy off (`ShowsApplyScope`)
  and pins one above its segments.
- **Rocksmith's Nop chord flag is a named toggle (`NopToggle`, `IsNop`, #489)**,
  where it used to be a `-nop` typed into the name; a typed suffix still folds
  into it. It is its own edit kind on the ladder (`ChordTemplateEditKind.Nop`,
  status word `ChordTemplate_Applied_Nop`), and the tooltip says what is and is
  not verified about it in game. Field and identity rules:
  [../../services/editing-invariants/chord-templates.md](../../services/editing-invariants/chord-templates.md).
  "Generate fingerings" lives in `ChordTemplateFingeringBackfill` (lifted out
  of the view model at its size budget).
  Every chip/segment glyph is preserved — `NoteTechniqueIconReachabilityTests`
  still gates the `IconTech*` references (it reads `SoundNoteTechniqueView.axaml` since #572).
- **Note-tab technique editing is icon-chip based (0.24).** The seven binary
  techniques (Accent/Vibrato/Slide/LinkNext/Tremolo/PassOther/Ignore) render as
  `TechniqueChip`s grouped articulation / sustain-shape / behaviour; the
  enum pickers (Mute/HOPO/Harmonic/Picking) keep the segmented `Seg` control but
  their segments carry a `PathIcon` instead of a text glyph. Glyphs live in
  `Styles/Icons.axaml` as the `IconTech*` family (24×24 fill, same weight as the
  Material set) — `Seg`'s and `TechniqueChip`'s inner `PathIcon` foreground is
  driven per state (PathIcon does not inherit `Foreground`). Semantics are
  unchanged from the old text chips: `IsThreeState` stays OFF so the VM's
  `bool?` projection (`InspectorViewModel.AllSameNullable`) shows `:indeterminate`
  for a mixed selection and a click always commits true/false to every selected
  note in one `IHistoryService` gesture. The only VM change was making
  `AllSameNullable` `internal` for the tri-state unit tests
  (`NoteTechniqueChipBehaviorTests`, `NoteTechniqueIconReachabilityTests`) — no
  binding/command restructuring, so the ceiling below still holds.
- Empty-state gating: the **Note** tab hides its controls behind an
  `InspectorEmpty` hint when `!HasNoteSelection`; the **Chord** tab is NOT
  gated (its `ChordTemplateEditor` is usable standalone — browse/create/edit
  templates with no chord selected); the **FEHLER** tab shows an `IconCheck`
  `InspectorEmpty` all-clear when `!Issues.IssueCount`. `HasNoteSelection`/
  `HasChordSelection` are computed props on `InspectorViewModel`, re-notified
  from the selection change + collection-change sync paths.
- **`ChordTemplateEditorViewModel` displays the SELECTED CHORD's own notes, not
  blindly its template.** A freshly GP-imported chord sits at `TemplateId -1`
  until the first save (`RscpTemplateManager.RegenerateTemplates` assigns real
  ids on save only), and an edit can leave a chord's notes ahead of its
  template. `UpdateFromSelection` resolves the chord's template only when it
  genuinely matches the chord's frets; otherwise it shows a display template
  built from the ACTUAL notes (reuse an equivalent real template, else a
  synthesised one with suggested fingers+name). The `ChordTemplates.First()`
  fallback is the **no-selection browse default ONLY** — never a substitute for
  an unresolved selection (that bug read a 14/15 chord as "12 12 12 12"). Edits
  are correspondingly template-safe: when the selection isn't cleanly BOUND to
  the loaded template, `ChordTemplateEditPlanner.ApplyEdit` reconciles (`ReconcileSelectionToTemplate`)
  by find-or-create + rebind-selected-only, so reshaping an untemplated chord
  never rewrites the other `-1` chords; and a handshape edit also binds+reshapes
  its enclosed untemplated strums (`ChordApplyScope.EnclosedUnbound`, on the
  in-place rungs) so the 3D
  bar/fingering can't detach from the note heads. Regression:
  `ChordTemplateEditorViewModelTests` (through the panel) and
  `ChordTemplateEditPlannerTests` (the planner on its own). The view model
  keeps the panel state; the planner owns every write path and returns the
  template the panel shows next.
- **The inspector never picks the Shape card's template itself.** Its re-sync
  after a selection or history move (`InspectorViewModel.SyncChordTemplate`)
  is `ChordTemplateEditorViewModel.ReloadForSelectedChord`: a chord naming a
  real template loads it by id, anything else resolves through
  `UpdateFromSelection`. It runs AFTER the panel's own history handler, so it
  has the last word; when it loaded a fretless `ChordTemplate { Name = "Custom" }`
  for a `-1` chord, every technique edit turned the card into "Custom" /
  "selected (0)" (#552). Regression: `ChordTemplateTechniqueEditResyncTests`.
- **The Sound tab names the PITCH, not only the position** (0.46.1, owner:
  "the sound tab in the inspector view should also show the note's actual
  pitch, not [just] the fret"). Single note: the placement card's `Sounds` row
  (`InspectorViewModel.NotePitchName` / `.HasNotePitch`, `x:Name`
  `NotePitchReadout`). Chord: a column in every per-string row
  (`StringFingeringInput.PitchName`). Both go through
  `ViewModels/Editor/SoundingPitchReadout` over `Core/Utilities/SoundingPitch`,
  which is the ONE derivation that folds in the tuning offsets
  (`TuningOffsetResolution`), the capo and the bass octave (`NameFor` also
  moves an A220 reference's octave into the name: a guitar-path 5/6-string bass's
  low B reads B0, #161) — **never
  reimplement the arithmetic, and never duplicate the standard-tuning table.**
  Three rules the readout carries:
  - **Nothing honest to name ⇒ NOTHING printed.** No arrangement, a string the
    arrangement has not got (`SoundingPitch` covers indices 0-5, so a 7th
    string is blank rather than wrong), a fret behind the capo (rule `L01`),
    and — in the chord rows — an unplayed (`""`) or muted (`x`) string.
  - **`StringFingeringInput.PitchName` is COMPUTED from `FretText`**, not
    pushed by the edit paths, so no path can rewrite a fret and leave the
    readout naming the previous one. The editor's only duty is to hand each row
    the `Arrangement`, which `RefreshStringNames` does beside the tuning letter.
  - **The rows keep riding `DisplayStringInputs`** — the flip-aware mirror that
    matches `ChartRenderContext.LaneTopFor`. The pitch column introduces no
    second ordering.
  Pinned by `UI/SoundTabPitchReadoutTests` (pure + markup + resx, default gate)
  and, outside it, by `Perf/InspectorConstructionUiPerfSmokeTests
  .TheSoundTabsPitchRow_LaysOutNonZero` +
  `Perf/SoundLibraryTemplateConstructionUiPerfSmokeTests
  .APerStringRow_ShowsThePitchAndLeavesAMutedStringBlank`.
## Five rules the panel broke on itself

Each was reachable from a shipped gesture and each is pinned in the default
gate; the mechanisms are in the commit bodies.

- **A per-string target must SURVIVE a committed edit.** `Chord.CopyFrom` —
  what `MutateChordsAction` transfers with — replaces every `ChordNote` with a
  clone, so `SyncChordProperties` has to rebuild `ChordBendStrings` on every
  `HistoryChanged`. Rebuilding it with `FirstOrDefault()` snapped the bend /
  technique target back to the LOWEST string after every commit, so a chord
  bend on any other string could not be authored past its first point.
  `InspectorViewModel.PreserveBendTarget` keeps it by STRING INDEX, and "the
  same chord" is REFERENCE identity (`InspectorChordBendTargetTests`).
- **`InspectorViewModel.SetVibrato` is the ONE place this panel writes
  vibrato**, and `InspectorVibratoDepthTests` lints that nothing in
  `InspectorViewModel*.cs` writes `.Vibrato` outside it. `SngFromDomainMapper`
  ORs the RS bit from `Vibrato || VibratoStrength > 0` and every import arrives
  at strength 80, so a flag cleared without its depth still exports as vibrato —
  which is what "Reset" and `ApplyNoteValuesToAll` did.
- **The chord box's ✕ reads the SOUND, not `PreviewChord`.** The preview is
  synthesised from `Template.Frets` and a `ChordTemplate` carries no mute, so
  `MutedStrings` was structurally always empty and the marker never rendered
  for any chord — while property, doc comment and binding all read as shipped.
- **Neither shape gesture may leave a finger on an open string** (rule `L06`,
  which the panel's own Issues tab reports). `ToggleNote` stamped finger 1
  unconditionally and `SetHoveredFret` left a stale one behind; "open" is
  `fret <= capo`, the reading `TemplateAndOverlapRule` and
  `ChordTemplateFingerSetter` both use.
- **…and neither may the FINGER gestures, which is the half that was still
  open until 0.50.1.** The three SHAPE gestures were fixed; `SetFingerForStrings`
  → `SetFingerDraftForStrings` — the funnel behind the global `SetFinger1..4`
  keybinds (Inspector ▸ `SetFingerOnSelectedStrings`, where **no mask means
  "every string of the chord"**) and behind the chord box's hover-typing — wrote
  the finger to every `StringInputs` ROW. `StringInputs` is one row per LANE, so
  one keystroke stamped a finger on the grip's open strings AND on the strings it
  does not play at all, minting the finding the panel's own Issues tab then
  reported. It skips a string the loaded template does not fret above the capo
  now (`IsFretted`); the line is drawn at `finger > 0`, exactly where
  `TemplateAndOverlapRule` draws it, so the THUMB is still allowed on an open
  string. **CLEARING (`SetFingerNone`, `fingerId < 0`) still reaches every
  string** — that is how an existing bad finger is repaired.
  `UI/ViewModels/Panels/ChordTemplateFingerOpenStringTests`.

- **"Indeterminate = leave alone" has to hold for the ENUM pickers too** (0.61).
  `ApplyNoteValuesToAll` skipped every mixed `bool?` chip and then wrote Mute /
  HOPO / Harmonic / Picking UNCONDITIONALLY. `Seg` has no indeterminate state,
  so those four SHOW the first selected sound's value — a display compromise,
  not a claim about the rest — and one click stamped it over the whole
  selection. `InspectorViewModel.NoteEnumAgreement` is the enum twin of
  `NoteFlagAgreement`; the verb writes a picker only where the selection agreed.
  Nothing is lost by skipping a mixed one: the per-field handlers already write
  through `ApplyNoteMutation` to the WHOLE selection, so a value the user chose
  has reached every sound before this verb is reachable — which is why each
  handler records its picker as agreed on the way past.
  `InspectorEnumAgreementTests`.

Outside this file but the same wave: the old bend graph opened a
`BendEditorViewModel` transaction BEFORE it knew whether the press had grabbed
anything, and a press in the canvas padding captured no pointer — so the release
never arrived, `_transactionDepth` stuck above zero and every later bend edit was
swallowed for the life of the control. That graph (`BendEditorControl` +
`BendEditorGeometry`) was deleted in #751 P9; its successor, the Bends page's
`Controls/ColumnFlowBendPlot`, opens a transaction only on a left press that always
hands back a point to drag, so its release closes it
(`UI/BendEditorTransactionTests.ALeftPress_AlwaysGrabsThePointItsTransactionIsClosedBy`).

The bend plot's keyboard is `Views/Panels/BendEditorKeys` (pure over the view
model, one undo entry per key): Enter / Insert adds a point after the selected
one, Home / End and Page Up / Page Down pick points, the arrows pick a point
when none is selected and nudge it when one is, Delete removes it, Esc clears
the selection. The plot is a Tab stop. The point readout under it comes from
`BendEditorViewModel` (`Inspector_Bends_PointReadout`), so the view model takes
an `ILocalizationService` (`UI/Views/Panels/BendEditorKeysTests`,
`UI/Controls/ColumnFlowBendPlotInputTests`; the old graph's hover callout went
with it in P9).

## Vocals / the Lyrics inspector tab

- Auto-sync review findings stay on each syllable (`AlignmentConfidence` /
  `ReviewReasons`). The lyrics lane marks doubtful pills; the inspector's
  previous/next review commands select and seek to them, wrapping within the
  active vocal track. The selected syllable shows its score and localized
  reasons. Navigation and automatic repair never clear findings; an EDIT does:
  `MoveLyricAction` and `ResizeLyricAction` clear the syllable's reasons in
  their own undo node (undo restores them), and the inspector's "Mark reviewed"
  button (`MarkLyricReviewedAction`) accepts a syllable that was right all
  along. `LyricReviewCount` shows "n to review"; with nothing flagged it says
  so and the three review buttons are disabled (`LyricReviewUiPerfSmokeTests`,
  `LyricReviewClearingTests`). A text edit (`RenameLyricAction`) keeps them.

- The lyric row places on right-click in empty space and renames on double-click
  or F2 / Enter on a selected syllable. `LyricInlineEditorViewModel` uses the
  existing `InlineEditOverlayView`: trailing `-` marks a word part; Enter commits,
  Esc cancels, Tab commits and edits the next syllable (Shift+Tab the previous).
  Closing by Enter, the end of Tab traversal or Esc returns focus to the timeline;
  clicking away cancels the draft and leaves focus on the clicked control.
  A new draft writes nothing until non-empty text is committed, including its
  first vocal track if needed. Placement joins the line spanning the snapped
  position or adds a sorted line; the line end and flags share the same undo step.
  These actions retain linked measures and leave the inspector's tap queue alone.
  Existing right-click deletion remains. Coverage: `LyricInlineEditorViewModelTests`
  and `LyricInlineEditingUiPerfSmokeTests`.

- Auto-sync captures its destination before any model download or inference.
  Completion requires the same project, active vocal track and live track
  membership; a switch or removal discards the result. An initially missing
  track is still added with its phrases as one undo node.
  `LyricAutoSyncProjectIdentityTests` covers switching and removal.
- Tap-sync queue removal belongs to `LyricTapAction.Execute` for both the
  initial tap and redo. `LyricTapSession.TapNext` must not pop it beforehand:
  repeated words would be consumed twice and undo could not restore the queue
  (`LyricTapSessionTests`).
- Phrase-end edits (checkbox and keybind) use `SetLyricPhraseEndAction`: setting
  a mid-line syllable splits its container after that syllable; clearing a line's
  last syllable joins the next container. The last line refuses the clear and
  explains why in the status bar. A line cannot end mid-word: setting the end on a
  word part clears its word join, and turning a line's last syllable into a word
  part (the inspector's Word part toggle, keybind, a trailing `-` COMMITTED in the inspector text box
  (Enter, lost focus, leaving the Lyrics tab, or `IChartEditorSurface.CommitPendingEdits`,
  which a USER-started save (`SaveInitiator.User`, or `Prompt` from the close/replace prompt), export, the export check and
  close/replace call first; an unattended save (`SaveInitiator.Unattended`, after stem
  separation) leaves the draft pending; the box is a draft while typing, so "x-ray" stays
  text, and a typed draft survives a history change that left its syllable's stored text
  alone; a commit asked for while the history applies an undo/redo step
  (`IHistoryService.IsApplyingStep`, set before the focus request, so the app's own focus
  handler clearing the selection first is covered) is deferred to the step's end
  (`IHistoryService.StepEnded`, raised on every exit): a step that applied commits it, a failed
  one (failed apply, failed link rebuild, a throwing focus listener) hands it back to the box,
  `LyricsInspectorDraftTests`; the history refuses any `Do` during a step, `HistoryServiceTests`)
  or the inline editor, or placing `hel-` after a line's last syllable) joins the
  next line, each in the same undo step (`Services/State/LyricWordPartEdit`,
  `JoinNextLyricLineAction`); on the track's last line the end stays and the word
  part is dropped. The rule lives once in `Core/Domain/Vocals/LyricLineRule`; the tap
  queue joins a line ending in `-` to the next one and sets a tapped syllable's word
  part only when the next syllable of its line lands.
  One history node retains the same lyric objects,
  phrase collections and original containers through undo/redo. `LyricPhrases`
  aliases the active track's collection, so the next timeline snapshot sees the
  changed grouping; `HistoryChanged` refreshes the inspector and the main window's
  renderer, and the edit also requests a redraw. Covered by
  `UI/ViewModels/Editor/LyricsInspectorPhraseEndTests` and
  `Services/State/SetLyricPhraseEndActionTests`, `LyricInlineEditorViewModelTests`
  and `Services/Parsing/Roundtrip/RsXmlVocalsLineRoundTripTests`.
- Tapping, placeholder insertion and deletion also move the end flag to the
  remaining last syllable inside their existing undo step. Only the last
  syllable stays flagged (`LyricTapSessionTests`, `ChartChordEditingRegressionTests`,
  `RemoveLyricPhraseEndTests`).
- `LyricTapTarget` resolves tap appends from the current chart: the newest surviving
  tap in the queued line owns the next tap, even after deletion or split/join.
  Only the nearest chronological predecessor can have its tail shortened; a later
  or equal-start syllable retains its end. Undo/redo restores the anchor history,
  end flags, spans and trimmed tail within the tap's history node.
- Split spans use every retained syllable's minimum start and maximum end, keeping
  the original leading padding on the prefix and trailing padding on the suffix.
  A join contains both original spans and every syllable. Placeholder insertion
  first resolves an exact syllable start, so overlapping split spans cannot steal
  its owner; equal starts in multiple lines choose the first collection owner.
  It normalizes end flags from the resulting collection and restores old flags on
  undo. Covered by `LyricTapBoundaryOwnershipTests`, `VocalInsertionBoundaryTests`,
  `LyricEditSequenceTests`, the pure state table in `LyricTapTargetTests`, and
  the 300 fixed seeds in `LyricEditInvariantTests` (spans, duration, ownership,
  future-tail preservation, queue state and undo/redo identity).
- Vocals editing lives in the Lyrics inspector (`ViewModels/Editor/LyricsInspectorViewModel` + `Views/Editor/Inspector/LyricsInspectorView`): syllable tapping, LRC/UltraStar import+export (codecs in `Services/Parsing/{LRC,UltraStar}`; an UltraStar DUET's first voice replaces the active track and every further voice is added as its own track through `Services/State/AddVocalTrackAction`, all in ONE `CompositeAction` so one undo takes the import back — `UI/ViewModels/Editor/LyricsInspectorUltraStarImportTests`), per-syllable text/word-part/phrase-end/pitch edits (all through `IHistoryService`). Lyric drags snap to waveform onsets via `ChartEditorViewModel.SnapLyricStartMs` — **onset snapping is lyric-only**; note-highway placement still uses plain `SnapToGrid`.
- The selected lyric's fields refresh on `HistoryChanged` as well as selection
  changes, under the same sync guard (#531). Undo/redo of text, word/phrase flags
  and pitch must update the inspector without writing another history node
  (`UI/ViewModels/Editor/LyricsInspectorStaleSelectionTests`).
- **A BULK phrase replacement must drop the lyric selection it orphaned** (fixed 2026-09-04, `UI/ViewModels/Editor/LyricsInspectorStaleSelectionTests`). `SetVocalPhrasesAction` clears the track's phrase collection and refills it with new objects, so a `Selection.SelectedLyric` from before an LRC import, an UltraStar import or an auto-sync points at a `LyricEvent` in no phrase — and it is not inert: the inspector still shows it, and the text box / word-part / phrase-end / pitch editors each push a REAL undo node against the detached object (an edit that reports success, burns an undo slot and changes nothing), `DeleteSelectedLyric` finds no owning phrase and no-ops, `ChartRenderer` reads it into the frame snapshot and `TimelineWheelAdjuster` RESIZES it. All three call sites go through one `ReplacePhrases` seam. The test is REACHABILITY across `ChartEditorViewModel.VocalTracks`, not track identity — a pick in a SECOND vocal track survived the replacement and must not be thrown away. (`SelectionService.ClearSelections` covers the project-swap and snapshot-restore paths for the same reason; a phrase replacement goes through neither.)
- Drag and wheel use the configured note minimum distance at the earlier syllable's start. A move resizes the previous syllable inside the phrase and fills the moved syllable to the same end limit the wheel uses. An intentional wheel shortening carries through a move, and the previous syllable retains a minimum visible length. Drag preview, cancel and undo restore both ends together.
- **Close gaps in phrases** repairs the active track in one undo step. It trims overlaps and fills gaps inside phrases with the configured note distance. Starts, phrase-final ends and deliberate wheel shortenings keep their timing. The word-span setting controls whether word endings are extended. Review marks clear only on syllables whose timing changes.

## Held grip editing

Select a hand shape to edit the frets and fingers of its grip. Save fingering
preserves its frets and ghost strings, including a per-bar mask set by
**Grips from held notes**. Editing a grip shared by multiple bars rebinds the
selected bar to an equal template or a new copy.

**The Shape page** (`SoundShapeView` over `GripInspectorViewModel`, inspector
design D): one 28 px row per string (label, Note fret, Grip fret, ghost box,
finger cells, mask dot) beside `Controls/ColumnFlowFretboard`, whose lanes are
the rows' 28 px (pinned by `Perf/SoundShapeViewUiPerfSmokeTests`). Pages Board,
Name, Scope, Flags and Double-stop sit beside the rows; **Full board** keeps
the board and moves the page below (one tree, placed by `PageRow`/`PageColumn`).
The header (fret pager or title, page toggles, Full board) is a `Controls/SplitRowPanel`: one 18 px row
while both halves fit (German at 530 px included), else the page strip moves under the pager and wraps; no
toggle is ever hidden or painted under another (`TheHeader_KeepsThePagerAndEveryToggle_VisibleApartAndClickable`).
The footer's Save and next / Save & next missing print where they go under their captions and drop with them.
Every edit is a DRAFT until Save / Save and next / Save & next missing; Undo
save takes back exactly that save. **A grip edit writes hand shapes and
templates only and never adds a note**, even with a chord of the grip selected
(`GripInspectorViewModelTests`). The page's scope IS the footer's Apply-to rung
(`ChordTemplateEditor.ApplyRung`, two-way). A ghosted grip edited from the
chord-template editor rebinds every target of the rung through
`GripInspectorTargets.Resolve`, each bar keeping its own ghost mask; on Every
use and Same grip the grip's chords follow, and on Same grip a fingering also
reaches the grip's other templates (same frets or moved) in place, one undo
step (`HandShapeGripGrid`, `GhostedGripApplyToTests`, `GripInspectorRungTests`).

The page holds ONE grid, ONE board, ONE template list, ONE Pin and ONE Save set;
the old `ChordTemplateEditorView` is not on it (`ThePageHoldsOneOfEach_AndTheOldEditorsTools`).
Its tools live on the pages: the template list (virtualized, with the shapes
that contain this grip as its "Part of shape" group), New template and the
suggested names ("Use …", one click) on Name; Next missing, Auto-fill (on the
Apply-to rung only) and Generate all fingerings on Scope; Arpeggio, No name,
Fingerless, Pin and the Nop flag (Double-stop) on Flags; the issue pips on the
board; Up / Down walk a fret-box column. The Name page's lists are built only
while it shows and only when the frets, the struck notes or the search change
(a search burst is one `CoalescedFilterPass`). An inferred ghost (held, not
struck at the opening) is never stored: it is read off the opening's notes each
time, so a later note edit cannot leave a stale mask for the exporters
(`GripInspectorPageTests`). At 530 x 370 all six rows show unscrolled in en,
de and ja (an 18 px header, 2 px gaps).
