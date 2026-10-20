# Tone Designer (UI deep reference)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before touching
the Tone Designer dialog, its history actions, or its preview path.**

**Two different surfaces share the word "designer", and this file is the DIALOG
one.** The *Tone Designer dialog* below authors the project's Rocksmith
`Tone2014` definitions (gear slots, knob metadata, arrangement tone slots) and
opens from Tools → Tone Designer. The rig's *"Design a tone"* panel is an AI
prompt over the in-house DSP chain and is documented in
[pedalboard/ai-tone-design.md](pedalboard/ai-tone-design.md) — since W4
(2026-09-16) it is the left column of the Amp & FX rig's **Tones mode**, not a
drawer. They share no view, no view model and no service; the only thing they
have in common is that both end up applying a tone through `DcRigApplier`, and
[pedalboard/editor-host.md](pedalboard/editor-host.md) is where the rule about
the THREE owners of that chain lives.

## Tone Designer (in-editor Tone2014 authoring, 0.16 2.6)

- `IToneDesignerScreen` on the dialog host (since 2026-09-22): Presentation's
  `Screens/Dialogs/ToneDesignerViewModel` (+ `.Import.cs`), one instance per open,
  with its editor `ToneEditorViewModel`, `ToneGearSlot`, the rows in
  `ToneDesignerItems.cs` and the preview in `TonePreviewSession`; the view is still
  `Views/Dialogs/ToneDesignerDialogView` (Tools → Tone Designer, palette
  `tools.toneDesigner`, and the export dialog's button, all through `IDialogs`; the
  host draws it above the legacy export dialog). Done answers true and Esc false,
  and BOTH commit what is buffered. Authors the project's
  full Rocksmith tones (`ChartProject.ToneDefinitions`) DLC-Builder-style:
  tone list CRUD, 14 gear slot pickers (amp/cab/pre 1-4/loop 1-4/rack 1-4)
  sliced from `RsGearCatalog.Shared` via the pure `RsToneAuthoring` helpers,
  knob sliders from the catalogue's `RsGearKnob` metadata (enum knobs =
  ComboBox), volume + up to 3 descriptors.
- **The BODY is the owner's mockup (`Editor Dialogs.dc.html`, `tone()`), rebuilt
  in 0.51** — a 1160 px card over a 560 px body of THREE columns: 196 px of
  project tones (+ Add / Duplicate / Delete under them), the flexible editor
  (key + name, volume, descriptors, then the 14 gear slots), and a 360 px
  scrolling column for the ARRANGEMENT TONE SLOTS and the IMPORTED TONE
  DEFINITIONS. Those last two used to be collapsed `Expander`s under the editor,
  i.e. the two things the dialog exists to produce were the two things it hid;
  do not put them back behind a disclosure.
  - **The 560 px is a WISH, not a size (#210).** The body is
    `ToneDesignerBody`, a `Controls/YieldingHeightDecorator` with
    `PreferredHeight="560"`: it asks for 560 whatever the content (the card keeps
    its height as tones come and go) and takes what the clamped card leaves it.
    A plain `Height="560"` here, as the last child of the card's `DockPanel`, was
    arranged 560 tall in the ~430 px a 1280×720 window leaves and painted over
    the subtitle and Done. At 1920×1080 the body is 560 either way (captures
    byte-identical). Pinned by `Perf/DialogWindowFitUiPerfSmokeTests`, which also
    holds every other hosted dialog to the same head / body / footer relation at
    1280×720.
  - **The gear PICKER is per row now, revealed by
    `$parent[ListBoxItem].IsSelected`, not one shared ComboBox re-pointed at the
    selected slot.** That shared box is what made an `ItemsSource` swap write
    null through `SelectedGearSlot.SelectedOption` (owner-reported crash
    2026-07-19); the view model's null guard stays as the belt to this brace.
    If that ancestor binding ever fails to resolve, every row stays collapsed
    and the dialog silently becomes a read-only list of gear — a working layout
    with the feature gone, which is why
    `Perf/DialogConstructionUiPerfSmokeTests
    .ToneDesigner_LaysOutThreeColumns_MarksItsRows_AndOpensThePickedGearSlot`
    asserts exactly one picker is visible and that it follows the selection.
  - **The gear picker is searchable (#6):** the selected row's picker is a
    `DotCharter.UI/Controls/DeferredChoiceBox.cs`, a `DeferredFieldBase` (the resting
    field shared with `DeferredAutoCompleteBox`) over an `AutoCompleteBox` built on first
    focus, so an open still builds none. Typing filters by a case-insensitive substring of
    the display name OR the gear key (`ISearchableChoice.SearchKey`, the catalogue key of a
    `ToneGearOption`; a separate interface because `ContractBindingLintTests` wants every `IToneChoice` member bound by XAML); a click shows the whole family; Up/Down move, Enter commits (with
    nothing highlighted, the first match); Escape and leaving the field keep the old
    choice. The box selects an item as soon as the text equals its name, so a selection is
    NOT a commit: the commit is the list closing on a pick while the caret is in the field
    (`OnEditorDropDownClosed`). Knob enum pickers stay `ComboBox`es. Pinned by
    `Perf/ToneDesignerGearSearchUiPerfSmokeTests`; the "exactly one gear picker is visible"
    case above looks for the `DeferredChoiceBox`.
  - It speaks the shared dialog grammar (`Views/Dialogs/DialogStyles.axaml`) and
    added three marks to it that the vocabulary lacked: `ListBox.DlgList` (a
    list whose rows are PICKED — container chrome neutralized so the row's own
    `Border.DlgListRow` carries hover/focus/selected), `TextBlock.DlgRowMeta`
    (+`.Dim`, the mono fact at the end of a row — what is loaded in this slot,
    `(empty)` quieter) and `Border.DlgTag`. `Button.DlgLink` also learned
    `ToggleButton` for the footer's two-state "Preview" — an Avalonia type
    selector matches its type EXACTLY, so `Button.DlgLink` never reached it.
- **An open costs its ROWS (#141, #173).** The view model is new on every open,
  so every list row is rebuilt every time: the arrangement slot cards, the gear
  rows and the selected slot's knobs. Two things keep that inside the budget:
  - `ArrangementSlotRows` virtualizes, so only the cards the column shows are
    built (`Perf/SurfaceShowRewarmUiPerfSmokeTests.TheToneDesigner_BuildsOnlyTheSlotCardsItsColumnShows`).
  - **Each slot is a `DotCharter.UI/Controls/DeferredAutoCompleteBox.cs`, not an
    `AutoCompleteBox` (#173).** At rest it is a bordered `TextBlock` drawn from the
    Fluent TextBox resource keys (pixel-identical in a capture); the first focus,
    by Tab or click, builds the real box in place, moves the focus into its
    `TextBox` and takes itself out of the tab order. `Text` relays both ways,
    `ItemsSource` is handed over, `AutomationProperties.Name` is copied, and at
    rest its own peer is a named Edit with a value pattern. An `AutoCompleteBox`
    is a templated `TextBox` plus a popup, ~10 ms each in Debug, five per card:
    most of what an open cost. Pinned by
    `Perf/ToneDesignerSlotEditorUiPerfSmokeTests` (an open, first or second,
    builds no `AutoCompleteBox`; a focused slot is the real box, keeps its name,
    height and tab order, edits the row, offers completion and commits on Done).
    `UI/AccessibleNameTests` lists it among the row input controls, so it must
    carry `AutomationProperties.Name` like any input.
  - Measured headless, Debug, steady state, on a busy box (medians of 15 opens,
    two runs): 2 arrangements / 5 tones 133-285 ms before, 96-162 ms after;
    6 arrangements / 27 tones 143-197 ms before, 93-103 ms after. A slot's first
    focus costs ~7-10 ms (30-50 ms the first time in the process). What is left
    is the layout of the new rows; the view model and `ShowAsync` together are
    ~4 ms. The release build's `Surface 'Dialog IToneDesignerScreen' shown in N
    ms` log line is the number to judge.
  - Two levers measured and NOT taken: building each gear row's picker only when
    the row opens (a `ContentControl` whose template is never applied while
    hidden) saved two `ComboBox`es and nothing measurable, because the gear list
    already virtualizes; and keeping the view model or its rows across opens
    fights the hosted dialog's one-instance-per-open contract
    ([dialogs/hosted-dialogs.md](dialogs/hosted-dialogs.md)). Recycling does not
    save the rows either: Avalonia 12 clears a recycled container's content
    (dropping its template child) and takes it out of the tree.
- **Volume matches DLC Builder:** a `NumericUpDown` (`VolumeInput`, 0.1..36, F1) and a `Slider` (`VolumeSlider`)
  between a quiet and a loud speaker icon, both over `Editor.VolumeAttenuation` — the POSITIVE number
  (12.0 for -12 dB). The stored value stays `Editor.VolumeDb` (negative dB, the Rocksmith body); the
  attenuation is the same buffered edit seen from the other sign, so it flushes as one history action.
- **History granularity rule:** gear/knob/volume/descriptor edits BUFFER in the
  editor and flush as ONE `UpsertToneDefinitionAction` (body re-serialized by
  `RsToneJsonWriter`) on tone switch / dialog close — never per-knob history
  actions. Add/duplicate/delete/"Add to project" commit immediately. Slot
  assignment (per-arrangement Base/A–D rows, a `DeferredAutoCompleteBox` over the
  authored keys) flushes on close as `AssignArrangementTonesAction` — sets
  the slots plus the DERIVED `ToneNames` (distinct non-empty A–D) and
  `StartingTone` (follows base), and deliberately touches nothing when the
  row wasn't edited (imported ToneNames orderings survive). All actions in
  `DotCharter.Services/Editing/Tones/ToneDefinitionActions.cs`; undo works and the dirty
  tracker picks the edits up like any other chart action.
- **A KEY edit is a rename, and Delete is a delete, of the tone EVERYWHERE (#181).**
  `CommitPendingEdit` hands a changed key to `ToneKeyEdits.PlanRename` with the
  buffered body as the replacement, so the definition, every arrangement's slots,
  starting tone and declared names, and every tone change move as ONE undo step;
  `DeleteTone` uses `ToneKeyEdits.BuildDelete`. Both then `RetargetSlotRows` the
  open slot cards to the new key (or ""), or `CommitSlotAssignments` would read
  the stale row text as an edit and write the old key back on close. Rules and
  the collision policy: [../services/editing-invariants/undo.md](../services/editing-invariants/undo.md).
  `ToneDesignerViewModelTests.A_key_edit_renames_the_tone_everywhere_as_one_undo_step`.
- **A slot naming a key the project has no tone for is MARKED in place (#195).**
  `ToneDesignerArrangementItem.IsTone{Base,A,B,C,D}Undefined` read the designer's
  live `ProjectTones` (key match, trim- and case-insensitive, like the Song tab's
  mark) and follow the slot text as it is typed; `OnTonesChanged` re-asks every
  row after an add, a key edit or a delete. The view puts the dialog grammar's
  notice strip (`Border.DlgStrip.Notice`, the twin of the Inspector's
  `FieldIssue.Warn`) under the field, with the Song tab's words
  (`Inspector_Tones_NotDefined` / `…Tip`). Pinned by
  `ToneDesignerViewModelTests.A_slot_naming_an_undefined_key_is_marked_and_the_mark_follows_the_tones`
  and `Perf/DialogConstructionUiPerfSmokeTests.ToneDesigner_MarksTheSlotThatNamesAnUndefinedKey_AndOnlyThatOne`.
- **Preview contract (mirror of `RsTonePlaybackCoordinator`):** the Preview
  toggle converts the edited tone via `RsToneConverter.ConvertFilled` and
  applies it with the DI `DcRigApplier` off the UI thread (latest-wins version
  counter); the user's chain is snapshotted ONCE per preview session and
  restored verbatim on preview-off AND on dialog close — never leave the
  preview rig behind; the host disposing the dialog restores it too.
  **Live edits reach the chart's playback (#139):** every buffered change is handed
  to `IChartToneLiveEdit` (the shell's `ToneRigViewModel`, coalesced at Background
  priority) under the tone's COMMITTED key, and `ChartTonePreviewController`
  re-prepares the project's tones with that body laid over the committed one, so
  the tone at the playhead — Volume included — is heard as it is edited. Not while
  this dialog's own Preview holds the rig (two writers, one rig); closing ends it.
  `ToneDesignerViewModelTests.AnEdit_IsHeardLive_UnderTheCommittedKey_UntilTheDesignerCloses`.
  **The target is switchable (tonelab):** "Preview in: DotCharter rig / Rocksmith"
  (`TonePreviewTargets`, the view model's preview argument, routes to the chosen one);
  both targets implement `ITonePreviewTarget` and keep this contract, and Rocksmith is disabled with its reason until a tonelab session is
  ready — [../services/tonelab-bridge.md](../services/tonelab-bridge.md).
  `NullDspChainService` ⇒ `TonePreviewSession.IsAvailable` false, the toggle is
  greyed with its reason. VM logic is headless-testable (`ToneDesignerViewModelTests`
  over `FakeDspChainService`; `TonePreviewSession.OffThreadRunner` is the inline-run
  test seam).
- The imported-tones list (psarc import) is a session-only passthrough for the
  export, kept in `DialogMemory.ImportedTones` for the project it was imported for
  (held weakly) and read back by the export dialog through
  `DialogMemory.ImportedToneBodiesFor(project)` — the authored set overlays it at
  build time. The per-arrangement slot rows need no passthrough any more: they are
  committed to the arrangements when the designer closes, and the export reads the
  arrangements. "Add to project" copies a body verbatim into the project.
- "Import tone file…" (`ImportToneFileCommand`, public build, NOT gated) reads a DLC Builder
  tone file straight into the project: `RsToneFileReader` (Tone) tells the format from the content —
  a tone JSON body, `Tone2014` XML (either DataContract namespace), or a `.rs2dlc` project's `Tones`
  array — and `ToneFileImport.ReadAsync` (Services) does the file I/O. New keys land as ONE undo step;
  a key the project owns is skipped and named in `ImportStatus`; a bad file is a status line + log.

## Creating a tone with AI

**Create with AI…** (`CreateWithAiCommand`, the designer's `.Import.cs` half, chip `CreateWithAiChip`)
opens `IToneCreatorScreen` — `Screens/Dialogs/ToneCreatorViewModel` (+ `.Accept.cs`), view
`Views/Dialogs/ToneCreatorDialogView` — as its OWN hosted dialog over the designer, so the designer's
open cost does not grow (nothing of the creator is built until the chip is pressed). The designer reaches
`IDialogs` and its estimate run through one part, `ToneDesignerTools`.

- **Before it opens** the designer commits the buffered tone edit and the slot rows, turns its Preview
  off and ends the live edit; **after an Accept** it rebuilds its tone list and slot rows from the project
  and selects the key the creator answers. A stale slot row would otherwise write its old text back on close.
- **Gear scope:** "Rocksmith gear only" is the one live scope here. "DotCharter rig" is shown greyed with
  its reason printed (`Dialog_ToneCreator_ScopeRigReason`): the creator proposes Rocksmith gear
  only; the closest-tone rig → Rocksmith conversion is the separate "From tone rig…" chip (§ From the tone rig below). The rig's own AI
  designer ([pedalboard/ai-tone-design.md](pedalboard/ai-tone-design.md)) is where rig tones are made.
- **The model side** is `Services/Ai/Generate/ToneCreator` (`IToneCreator`) — the W6 lane's own
  `IRsToneMapper` (Rocksmith catalogue prompt, `RsToneReplyReader`, reject never repair) plus the
  instrument check the catalogue gate does not make (`ToneCreator.InstrumentFault`: a guitar tone naming a
  `Bass_` amp, cabinet or pedal, or a bass tone naming a guitar amp or cabinet). Registered in
  `DialogsModule` as `ToneCreator.CacheOnly`: the stems are read from the cache and never separated.
  [../services/ai-tone-generate.md](../services/ai-tone-generate.md) § The Tone designer's AI creator.
- **Evidence** is optional and off by default: the cached stem over the span at the playhead
  (`ToneCreatorEvidence.AtPlayhead` — the placed change's planned window that holds the playhead, else the
  next 10 s); a cache miss is "from your words only", said on the proposal.
- **The call never runs on the UI thread** (`Task.Run`) and Stop cancels it; a refusal names the gear in
  the status line and leaves no proposal.
- **The proposal** shows every slot (`AiToneText.Gear`: display name + every knob), the volume, the model's
  rationale and where the evidence came from. **Audition** puts it on the DESIGNER's preview target (the
  rig or Rocksmith through tonelab — `ToneCreatorRequest.Preview`) and is stopped on Accept, Cancel and
  dispose. **Refine** sends the current proposal as JSON (`RsToneMapRequest.PreviousProposal`, its own
  prompt section) with the description and the change.
- **Check with audio** (`CheckCommand`, chip `CheckButton`) runs the audio check over the span at the playhead
  ([../services/ai-tone-generate.md](../services/ai-tone-generate.md) § The audio check): greyed with its reason
  (`CheckReason`) without song audio and a beat map; it stops Audition first; its best becomes the proposal, and
  `CheckPanel` prints who judged it (`Dialog_ToneCreator_JudgedByGame` / `…JudgedByClone` with the reason) and
  one line per round. Pinned by `Presentation/Dialogs/ToneCreatorCheckViewModelTests`.
- **Accept** is `Services/Editing/Tones/ToneCreatorAccept.Build` → ONE `CompositeAction`
  (`History_AiToneCreate`): replace the edited tone's body (key and name stay, so the placed changes that
  name it follow), or a new definition with a unique key declared in the first free slot of Base, A–D
  (`ToneCreatorAccept.FreeSlot`; all five taken ⇒ Accept is greyed with the reason), plus optionally a tone
  change at the playhead (declared in a free slot when the arrangement does not declare the key yet).
  **Cancel writes nothing.**
- Pinned by `Presentation/Dialogs/ToneCreatorViewModelTests` (fake model, real creator and gate),
  `Ai/Generate/ToneCreatorTests`, `ToneDesignerViewModelTests.Create_with_AI_opens_the_creator_over_the_selected_tone_and_lists_what_it_accepted`
  and the headless `Perf/DialogConstructionUiPerfSmokeTests.ToneCreator_LaysOutTheDescription_TheGreyedRigScope_TheAudioCheck_AndTheProposalRows`.

## From the tone rig (the inverse of `RsToneConverter`)

**"From tone rig…"** (`ImportFromToneRigCommand`, beside Import tone file) builds the closest
catalogue-valid Rocksmith tone for the rig being played and opens a review over the dialog
before anything is written.
The flow is its own collaborator, `Screens/Dialogs/ToneDesignerRigImport` (the designer holds it
lazily and hands it a `ToneDesignerRigImportHost`), not more partial of the view model. The
review is modal, so the fake's `WithRigImportOpen()` is deliberately NOT a `Variants` entry (it
covers the footer every variant is graded on); `RigToneImportReviewUiPerfSmokeTests` lays it out.

- **No second mapping.** `DotCharter.Tone/Audio/Dsp/RsToneConversion/RigToRsToneConverter`
  answers each rig module with the catalogue gear whose FORWARD conversion
  (`RsToneConverter.ConvertGear`) lands on the same module and model (`RigToRsGearIndex`, built
  per catalogue + overrides + game tables), then searches that gear's knobs inside their
  catalogue ranges until the forward conversion reproduces the module's params
  (`RigToRsKnobSearch`: per-knob grid, a stepped ternary or golden-section refinement, snapped to
  the knob's step, never clamped after the fact). Ties: exact model, longest run (cab + mic
  layer), a pedal over a rack before the amp, closest params, guitar gear over its `Bass_` twin.
- **Every loss is named** (`RigToneLossKind`, worded by `RigToneImportReviewViewModel.LossKey`):
  a NAM capture (a catalogue amp stands in at default knobs), an external IR, bypassed or
  unreached modules, split routing, a second amp/cabinet, a rack moved behind the cabinet, a
  nearest model, knobs below 95 % match. A rail over four slots REFUSES the tone (Accept greyed,
  `RigImport_Refused`) until the user unticks gear. Tested by `RigToRsToneConverterTests`
  (round trip for every ✅ gear in rs-tone-coverage.md, comparing the forward conversion: many
  cabinets and some pedals are indistinguishable from each other on the rig and come back as
  a twin).
- **The rig is the player's own** (`TonePreviewSession.CurrentRig`: the restore point while
  Preview holds the chain). The conversion runs off the UI thread
  (`ToneDesignerViewModel.RigImportRunner`).
- **Accept is ONE undo step** through `Services/Editing/Tones/RigToneImport`: a new tone
  (`ToneRig`, made unique) declared in Base when it is empty, else the first free A–D slot; or
  the edited tone's body replaced in place (key, name, slots untouched). The catalogue gate
  (`RsToneCatalogValidation.Require`) runs first and refuses, never repairs.
- The review is its own view, `Views/Dialogs/RigToneImportReviewPanel.axaml`, hosted in a
  `ContentControl` over the card (a closed review is no control). Not a fake variant: it covers
  the footer every variant is graded on; `Perf/RigToneImportReviewUiPerfSmokeTests` lays it out.
- **Not done:** a closeness measured by RENDERING both chains (`MatchScale`) — the score shown is
  the param match through the forward converter; the VST chain is not read (it is not part of
  the DSP chain the snapshot sees); and the rig's own "Send to Tone Designer" entry point.

## Estimating tones from the song's audio

**Estimate tones from audio…** — `GenerateTonesFromAudioCommand` (the dialog's
`.Import.cs` half), which hands the active arrangement to
`DotCharter.Services/Editing/Tones/ToneGenerationRun.cs` — the third button beside
Add / Import. It measures on this computer with no AI and never overwrites a tone.

**It is not Tools ▸ Tone & sound ▸ "Generate tones with AI…"** (`Services/Ai/Generate/AiToneGenerationRun`,
[../services/ai-tone-generate.md](../services/ai-tone-generate.md)), which asks the AI
provider and REPLACES the arrangement's slots. Both stay (#216); each tooltip names the
other by its exact label, linted by `UI/ToneGenerationReachabilityTests`.

**It never decodes on the UI thread (#215).** The run is three phases:
`ToneGenerationRun.Capture` reads the project (UI thread, no file I/O), `Analyse`
decodes and analyses inside the command's `await Task.Run(...)`, and `Apply` writes
the undoable edits back on the UI thread, skipping a name or key the project gained
meanwhile. `ToneGenerationService` decodes each audio file ONCE per run (it caches
per path); it used to decode the whole song again for every placed tone change,
inline, which froze the window. The command is an `AsyncRelayCommand`, so the button
is disabled while it runs, and `ImportStatus` says `Dialog_Tones_GenerateRunning`.
Pinned by `UI/ToneGenerationReachabilityTests` (the async shape) and
`Services/Editing/Tones/ToneGenerationRunTests` (one decode for N changes).

**The owner's rule, and it is load-bearing: the tone PLACEMENT is not generated.**
The charter drops the tone changes; the command reads them and builds a tone from
the audio each one governs — the span between that change and the next. It adds
no change, moves none, removes none, and `UI/ToneGenerationReachabilityTests`
lints that (`ToneChanges.Add`/`Remove`/`Clear` and `.Position =` may not appear
in the run).

The chain, each part pure and tested without a file:

| Piece | Answers | Where |
|---|---|---|
| `ToneChangeSpans` | which audio | `DotCharter.Core/Utilities/` |
| `ToneAudioSource` | whose audio | `Services/Audio/Dsp/Tones/` |
| `ToneAudioAnalyzer` → `ToneAudioProfile` | what it measures | `DotCharter.Analysis/Tone/` |
| `ToneEvidence` | whether that is worth using | `Services/Audio/Dsp/Tones/` |
| `ToneFromAudioBuilder` | what gear it implies | `Services/Audio/Dsp/Tones/` |
| `ToneGenerationService` | orders them; the ONLY part that opens a file | `Services/Audio/Dsp/Tones/` |

Things that will bite:

- **The source choice matters more than the gear mapping.** A cymbal is
  broadband noise, so analysing a full mix reads as distortion and returns a
  high-gain amp for a clean song. `ToneAudioSource` takes the `guitar` stem, else
  `other` (where a 4-source separation folds guitar), else the mix — and FLAGS
  the mix case. Drums/bass/vocals are never evidence, and the command filters the
  dictionary through `ToneAudioSource.IsGuitarish` before handing it over.
- **`ToneAudioProfile.IsSilent` does not cover loud noise.** White noise measures
  `Drive` **0.990** — the strongest possible "build a high-gain amp" signal from
  a span with no guitar. `ToneEvidence` is the guard, and it needs BOTH high
  flatness and low harmonicity because either alone has a real guitar case. The
  measured gap is wide: clipped guitar 0.027 flatness against noise 0.846.
- **`Drive` is only comparable between spans of real PLAYING.** The crest term
  maps 18 dB → 0 and 6 dB → 1, and a continuous full-level sine has a 3.01 dB
  crest *by definition*, so a steady synthetic tone scores ~0.50. It is not a
  bug and it is not fixable from the crest side; do not "correct" it against a
  synthetic fixture.
- **`Drive` conflates compression with distortion.** A clean part through a heavy
  studio compressor squashes the crest exactly as an overdriven amp does. Expect
  compressed clean funk to land in edge-of-breakup — which is the band whose amp
  is easiest to correct by hand, and that is why the widest ambiguity was put
  there.
- **The archetype thresholds (0.22 / 0.42 / 0.68 / 0.82) are ARGUED, not
  calibrated.** Nothing was fitted against labelled audio. The monotonicity test
  does not care where the boundaries sit, only that the gain windows stay
  ordered, so moving them when a real corpus exists is cheap.
- **Existing tones are never overwritten**, and each generated tone is declared
  in a free A–D slot as it is created — a tone the arrangement does not declare
  is the D01 fault the export check reports.
- **Nothing verifies how the tone SOUNDS.** Every test asserts structure: right
  gear, right ranges, right direction, right JSON round-trip. There is no test
  that the generated tone rendered through the engine resembles the span it came
  from. Present it as a starting point; it cannot recover the rig from a record.
