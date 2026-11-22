# The inspector MIXER and FEHLER tabs

Persisted mixer changes are project edits (#553). Gain, mute, solo,
original-mix mute, Guitar roles and stem recovery notify the session's dirty
tracker through `ProjectState.NotifyProjectChanged`. Run-scoped changes and
the solo-scoped original mute do not persist. Muting the last solo restores
the original's baseline just like releasing Solo. Replacing native audio
invalidates the loaded-set cache through `ProjectState.AudioLoaded`, including
when the filename stays the same. `ProjectSessionAttachAudioTests` covers these paths.

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
    mono value`, laid out on the `ColumnFlow*` card grid since #751 P8 (the
    0.51 `TextBlock.MixerLabel`/`MixerValue` rails and `Border.MixerVerbSlot`
    were deleted in P9 once no row wore them). **Since #493 the tab has NO Levels and NO Playback FX
    section**: master / song / input / MIDI level with their mute and solo, track
    pan, speed and pitch are bound ONCE, in the toolbar's Mix and Tempo popovers
    (one home per mix control; the table is
    `UI/MixerLevelsInPopoversTests`, which also pins that the tab binds none of
    them again). The tab keeps the deep controls: stems, EQ, the chart-sound
    instrument and play style, plus a link button to the VST instrument picker,
    which lives in Practice tools only.
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
  - **A saved stem volume of zero is silence, not a missing value (#531).**
    `StemTrackViewModel` defaults only negative values to unity and preserves
    zero when applying gains to the engine (`UI/ViewModels/Editor/StemTrackViewModelTests`).
  - **The EQ analyser clock follows effective visibility (#531).**
    `EqModuleView` creates a stopped timer and uses `EffectiveVisibilityWatcher`
    to start/stop it on ancestor visibility changes as well as attach/detach.
    Closing `MixerEqBody` therefore parks spectrum reads and repaint work
    (`Perf/EditorUiFaultsUiPerfSmokeTests`).
  - **A stem row only exists if the stem does** — `StemMixerViewModel` only adds
    a track whose file was found on disk, so the `ItemsControl` IS the list of
    what really separated.
  - **A row draws only the verbs it HAS**: no dead mute or solo control.
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
    .{MixerInspectorView_buildsAndAttaches_andEverySectionLaysOut,
    IssuesInspectorView_buildsAndAttaches_andEveryCardLaysOut}`, `Perf/MixerTabCardsUiPerfSmokeTests`
    (every section at 480/530 x 370 in en/de/ja, in the release's layout filter) and
    `Perf/IssuesTabCardsUiPerfSmokeTests`.
  - **Inspector design D (#751 P8): a section STRIP over cards.** The Mixer tab is a Stems / EQ / MIDI strip
    (`MixerSectionStrip`, `ColumnFlowNav` toggles, like the Note tab's) over one section's `ColumnFlowPanel`
    at a time; `InspectorViewModel.MixerSections` (`MixerSectionsViewModel`, `MixerSection`) owns the open
    section: Stems is always offered (without stems it says why and offers "Separate stems", the shell's cache-first
    separation); no stems opens EQ, stems that go away keep their section, stems that arrive open Stems only when
    nothing was chosen (`UI/ViewModels/Editor/MixerSectionsViewModelTests`). Stems = Levels (the Playback source
    `StemMixerViewModel.PlaybackSource` — Stems / Original mix / No stems, a VIEW of the strips' mutes so the project
    and an engine rebuild keep it — strips, a per-stem reset `StemTrackViewModel.ResetLevelCommand` and Reset levels
    `StemMixerViewModel.ResetLevelsCommand`, both to unity, mute/solo kept; Reset levels asks first and marks the
    project changed ONCE), Routing (waveform follows the mix, the mini waveform `Controls/ColumnFlowStemWaveform` over
    `ProjectState.WaveformData` with its Mixed / Source caption, redrawn only per coordinator publish; Guitar roles)
    and Monitor (fader-derived bars, no meter tap: no managed audio work). EQ = `EqModuleView`'s Response (plot +
    preset + Bypass = `EqModuleViewModel.IsBypassed`; the analyser polls only while the Response card is on the page,
    `EqModuleView.IsPollingSpectrum`), Band (the chip row — a colour dot that switches its band, a chip that selects
    it — the picker over `Bands` with `PickerLabel`, `SelectedBand`; Solo needs the band on; `EqModuleViewModel.Edits`
    = `EqBandEdits`: typed Frequency / Gain / Q fields committed on Enter or focus loss with the range named inline,
    and Reset gain + Q, which asks and goes back to the loaded preset's band, flat without one) and Routing cards.
    MIDI = Instrument (`MidiInstrumentCardView`, design D's grammar over the transport's
    `ChartSynthInstrumentViewModel`; Play Mode's Amp/FX panel keeps `ChartSynthInstrumentView`; "Not available"
    placeholders), Routing and Play style; without the synth Routing and Play style stay on screen disabled with the
    reason; each card's Reset (instrument, routing, play style) asks on its card first. Routing's two design-D rows are
    `ChartSoundRoutingView` over `Transport.SoundRouting` (`ChartSoundRoutingViewModel`): Sound source (four segments:
    Song tones / My rig / My instrument / Instrument + rig) and Route instrument (Guitar / Bass / Both, the
    engine's seat masks). A choice the setup cannot honour is disabled with its reason under the row: the instrument
    values without a loaded plugin or in the sandbox, Route instrument with one live input, under Song tones (the
    song's tone exists only on the guitar seat's rig) and under My instrument (after the mix, through no rig).
    `UI/ChartSoundRoutingChoicesTests`, `Perf/MixerSoundRoutingUiPerfSmokeTests`. The plot is `Controls/ColumnFlowEqPlot` (was `EqPlotControl`):
    a press near a handle grabs that band, anywhere else moves the SELECTED band (`ColumnFlowEqPlot.Grab`,
    `UI/ColumnFlowEqPlotGrabTests`); a frequency label that would touch its neighbour is left out
    (`PlaceAxisLabels`: "20k" goes in a narrow column) and the handles stay `HandleInset` inside the plot. Each card scrolls by itself, so `Perf/MixerScrollHostUiPerfSmokeTests`
    pages the card that holds its control (`ColumnFlowPanel.ShowPage`) and wheels THAT card's scroller.
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
  an E04, I46 or I30 row offers its fixes there (`Inspector/IssueFixViewModel`,
  `InspectorIssuesViewModel.IssueFix`, bound as `Issues.IssueFix.*`) — the DEFAULT first, the other under it, each ONE
  undo step through `Editing/Anchors/IndexAnchorRepair`. `RecomputeIssues` calls
  `IssueFix.Refresh`, which withdraws the offer once that finding (same rule, same
  element) is gone. An I30 (tone change on a note, #759) offers "a 16th note earlier"
  (`Editing/Tones/ToneChangeNudge`); when a guard blocks it the button is disabled and
  `IssueFixBlockedReason` prints why. Behaviour: `IssueFixViewModelTests`; layout:
  `Perf/IssueFixRowUiPerfSmokeTests`. **Audio lint (v0.29)**
  sits between the issue list and Export-readiness: an EXPLICIT-run block over
  the standalone `AudioLintViewModel` (`InspectorViewModel.AudioLint`, seams
  injected, ceiling-conform) → `IAudioChartLintService` (original-MIX decode
  only — never Opus stems) → jump rows "charted C5 at 1:23.4 — audio says B4".
  Findings live on the sub-VM and persist until the next run — deliberately
  OUTSIDE `RecomputeIssues`, which rebuilds constantly and would wipe an
  expensive analysis.
