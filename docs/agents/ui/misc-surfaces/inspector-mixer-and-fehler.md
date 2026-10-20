# The inspector MIXER and FEHLER tabs

Part of the inspector / misc-surfaces reference. Router: [../misc-surfaces.md](../misc-surfaces.md).

**"FEHLER tab" is the CODEBASE nickname, not the caption.** The header reads
**Issues** since 2026-09-04 (`Inspector_Tab_Issues`, was `Inspector_Tab_Errors`
= "ERRORS"): its badge is `InspectorIssuesViewModel.IssueCount` (`Inspector.Issues.IssueCount`), i.e. every validation
result at every severity, and the pane's own footer already counts them as
"{0} issues found" while the status rail splits them into an ⛔ and a ⚠ chip. A
header saying ERRORS over a number that includes warnings is the one claim a
charter acts on and it was wrong. The nickname is left alone deliberately — it
is what eight files and this document's own name call it.

- **The MIXER and FEHLER tabs were rebuilt from `Chart Editor.dc.html` (0.51)**,
  the only two the owner asked for out of that file. Everything below is real
  state that was already there — the tabs were regrouped and restyled, not
  invented.
  - **ONE ROW SHAPE for every channel in the Mixer**: `[M][S] · label · fader ·
    mono value`, with the label rail (78) and the value rail (54) carried as
    WIDTHS on `TextBlock.MixerLabel`/`MixerValue` rather than on each
    `ColumnDefinition` — a `GridLength` is not a double, so a shared number has
    to live on something that takes one. LEVELS, STEMS, PLAYBACK FX, the EQ
    filters and the MIDI synth all wear them, which is the only thing giving the
    tab one left edge and one right edge.
  - **The fader is `Slider.MixerSlider`, NOT the shared `Slider.FireSlider`.**
    That class hard-wires the accent gradient into its decrease-track template,
    and this tab's whole point is that the FILL carries the channel's identity
    (violet bus / instrument colour / amber effect) while the track stays
    neutral. `Foreground` inherits into the templated RepeatButton, so one
    property per row paints it. Its `Height` is **24** and that must stay a
    `Height`: Fluent's slider measures 44 px inside a three-row Grid and a
    `MinHeight` silently leaves it there, which is nearly a mixer row and a half
    per row.
  - **A stem's colour is `Converters/StemIdentity.TokenFor(name, isMasterMix)` →
    a `FireStem*` token → `StemIdentityBrushConverter`.** The pair is the key,
    never the name alone: the master strip's display name is the literal
    "Original". An unknown stem (anything a user dropped into the project's own
    `stems/` folder) takes the neutral slate rather than another instrument's
    colour. Guarded by `UI/StemIdentityTests`, which also asserts every token it
    can return is DECLARED in `FireTokens.axaml` — a missing key paints nothing.
  - **"Original" and the stems are ONE mutually exclusive pair, so both halves
    are saved.** Un-muting a channel silences the other side (`OnTrackMuteChanged`)
    — that is the doubling guard, and it is fine. What was not fine: only the
    stem half persisted (`Stem.Muted`), because the "Original" strip has no
    backing `Stem`. Its mute was re-derived on load as "muted, because stems
    exist" and its channel strip was hard-coded `_isMuted = true`, so saving "I
    want the original, not the stems" reopened as EVERY channel muted — a project
    that plays silence over a healthy engine, with nothing in the log and nothing
    in the UI to explain it (owner report, 2026-08-27). The strip now takes its
    mute from `ChartProject.OriginalMixMuted` and writes back through
    `StemMixerViewModel.PersistOriginalMixMuted`, run-scoped mutes excepted like
    `Stem.Muted`'s. **A project may not OPEN silent**: `Rebuild` un-mutes the
    original when nothing else would be audible. LOAD time only — muting
    everything while you work is deliberate and must be left alone. Pinned by
    `UI/ViewModels/Editor/StemMixerSilentProjectTests`.
  - **A stem row only exists if the stem does** — `StemMixerViewModel` only adds
    a track whose file was found on disk, so the `ItemsControl` IS the list of
    what really separated.
  - **A row draws only the verbs it HAS.** The master bus has no solo and a
    playback effect has neither; those squares are `Border.MixerVerbSlot`, which
    holds the column open without offering a dead control.
  - **The M/S chrome is stated on the `/template/ ContentPresenter` in every
    state.** Fluent's ToggleButton template-binds the presenter's fill at rest
    (so a control-level setter looked like it worked) and writes it DIRECTLY on
    `:checked`/`:pointerover` — so a muted channel lit up in Fluent's accent
    violet, the layer's selection colour, while the danger tint the grammar
    declared never appeared.
  - **The FEHLER row is `code · message · location`**, three columns wide plus a
    3 px spine cell. The location is `Core/Utilities/ChartPositionLabel` — the
    status bar's own `M{bar}.{beat}` joined to `mm:ss.ff` — reached from markup
    through a `MultiBinding` over the issue's `TimeMs` and
    `ProjectState.Beats`, because an `ArrangementIssue` is a Core record that
    carries a time and nothing else. **No measure markers ⇒ the time alone; no
    time ⇒ an empty cell.** The severity GROUPS survive (Important above
    Warning) but render no header — the row's code colour says which is which.
  - **"The selected row" is `:focus-within`**, because the list has no selection
    model; the row is a Button, clicking one leaves it focused, and the jump
    selects the offending element.
  - **The export verdict is three gated TextBlocks over two real flags**
    (`ExportReadiness.HasExportCheckRun`, `ExportReadiness.HasExportFindings`,
    on `Inspector/ExportReadinessViewModel`) and never says "no blockers"
    before the validator has run — pinned as markup by `UI/IssuesTabVerdictTests`.
  - `Converters/SeverityToBrushConverter` now resolves the **Fire** tokens
    (`FireDanger`/`FireWarning`) through `TryFindResource`, not the app's
    `DangerColor`/`WarningColor` through `TryGetResource`. Its one consumer is
    this tab, which is on the layer; and `FireTokens.axaml` is a `<Styles>` file,
    so `Application.Resources` alone does not reach its keys.
  - Runtime guards: `Perf/InspectorConstructionUiPerfSmokeTests
    .{MixerInspectorView_buildsAndAttaches_andItsRailsLineUp,
    IssuesInspectorView_buildsAndAttaches_andTheFocusedRowLights}`. The Mixer
    case **opens Levels, Playback FX and MIDI synth through the tab's rail in
    turn (#601) and leaves the EQ section closed**: that section's curve is a
    `Polyline` with `Stretch="Fill"`, and a filled shape with no points has empty
    geometry bounds, so the fill scale divides by zero under the harness (no
    transport data), not the view. The rails must line up ACROSS sections, which
    is the same left and right edge the one-scroll tab had.
  - **One section at a time since #601.** The five groups are the sections of the
    tab's icon-only rail (`InspectorSectionRail`, the Sound tab's look); Stems'
    entry exists only while stems do. `Perf/MixerScrollHostUiPerfSmokeTests`
    therefore opens the EQ (or the MIDI synth, for the dropdown case) at the
    ~160 px a 1280x720 window leaves the section body, where it scrolls.
- **FEHLER = severity-grouped triage surface** (0.16 rework). Its view model is
  `Inspector/InspectorIssuesViewModel` (`InspectorViewModel.Issues`, #659; the
  markup binds `Issues.*`). `RecomputeIssues`
  rebuilds `IssueGroups` (`ObservableCollection<InspectorIssueGroup>`) via the
  pure `IssueGrouping.Build` (Important group first, then Warning; empty
  severities omitted; `IssueGroupingTests`). The view renders a group header
  (severity dot + `SeverityToLabelConverter` label + count badge, both coloured
  by `SeverityToBrushConverter`) over single-click rows — each row is a
  `Button.IssueRow` bound to `Issues.JumpToIssueCommand` (routes through
  `ChartEditorViewModel.ExecuteSeek`, then selects the element), NOT the old
  double-tap `ListBox`. `IssueSummary` is the localized headline
  (`Inspector_Issues_Count`, replacing the hard-coded English "{0} issue(s)").
  The Export-readiness block is pinned below and its findings are now
  single-click `ExportReadiness.JumpToExportFindingCommand` buttons too — the
  block is `Inspector/ExportReadinessViewModel` (`InspectorViewModel.ExportReadiness`;
  the Issues tab calls its `RefreshIfChecked` from `RecomputeIssues` and the
  inspector its `Reset` on a project switch; behaviour in `ExportReadinessViewModelTests`).
  A fixable finding carries a **Fix** link BESIDE its row (never inside the row's
  button); it goes through `Presentation/Shared/RocksmithFixes` and re-runs the
  check (#184, [dlc-builder-fixes.md](../../services/editing-invariants/dlc-builder-fixes.md)).
  **A rule finding's fix menu is the `IssueFixRow` in the header (#618)**: jumping to
  an E04 or I46 row offers its fixes there (`Inspector/IssueFixViewModel`,
  `InspectorIssuesViewModel.IssueFix`, bound as `Issues.IssueFix.*`) — the DEFAULT first, the other under it, each ONE
  undo step through `Editing/Anchors/IndexAnchorRepair`. `RecomputeIssues` calls
  `IssueFix.Refresh`, which withdraws the offer once that finding (same rule, same
  element) is gone. Behaviour: `IssueFixViewModelTests`; layout:
  `Perf/IssueFixRowUiPerfSmokeTests`. **Audio lint (v0.29)**
  sits between the issue list and Export-readiness: an EXPLICIT-run block over
  the standalone `AudioLintViewModel` (`InspectorViewModel.AudioLint`, seams
  injected, ceiling-conform) → `IAudioChartLintService` (original-MIX decode
  only — never Opus stems) → jump rows "charted C5 at 1:23.4 — audio says B4".
  Findings live on the sub-VM and persist until the next run — deliberately
  OUTSIDE `RecomputeIssues`, which rebuilds constantly and would wipe an
  expensive analysis.
