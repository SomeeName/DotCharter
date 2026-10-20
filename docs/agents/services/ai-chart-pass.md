# AI chart pass: FHP and fingering, chord names (`DotCharter.Services/Ai/Chart/`, #497)

The charter selects a range (or nothing, for the whole arrangement) and asks the
AI provider from Settings ▸ AI to fix FHP and/or chord fingering, or **chord names**
in a chosen style. Same providers, factory and settings as AI tone design
([ai.md](ai.md)); own prompt, own schema, own validator per job.

**Four jobs (`AiChartJob`), the charter's choice (owner decision 2026-10-09):**

| job | the model proposes | fixed input | the rest |
|---|---|---|---|
| `FhpAndFingering` | anchors + chord fingers | notes, frets, times | FHP and fingering decide each other (#494: anchor right → fingering right 80%, else 0%) |
| `FhpOnly` | anchors | everything else, fingers included | free fingerings FOLLOW the new anchors through `ChordFingeringFollower`; the model is never asked about, nor shown, a free fingering |
| `FingeringOnly` | chord fingers | anchors | a fingering whose index finger is off the fixed anchor's fret (E04) rejects the chunk |
| `ChordNames` | names (EOF or musical style) | everything else | |

**No arrangement gating:** every job runs on guitar and bass alike; which arrangements
to run it on is the user's call. The measurement in `tools/chart-eval/README.md` is
information, never a restriction.

**The default is what measured best (#602, `AiFingeringDefaults`).** The 2026-10-10
comparison (four held-out arrangements, one run per model at default reasoning):

| | guitar FHP | guitar fingering | bass FHP |
|---|---|---|---|
| algorithm | 95.7 % | 67.1 % | **79.5 %** |
| Sonnet 5.5 | 95.8 % | **96.1 %** | 77.7 % |
| Haiku 5.5 | 92.4 % | 85.8 % | 65.3 % |
| Opus 5.5 | 96.6 % | 59.7 % | 74.3 % |
| GPT 6.1 Sol | 94.0 % | 78.1 % | 76.7 % |

So `FingeringOnly` is where the AI chat's Task picker starts, on guitar AND bass (the
anchors stay the algorithm's), with the reason printed under the pickers
(`AiChat_JobNote_Guitar` / `_Bass`, and `_GuitarFhp` / `_BassFhp` for the opt-in). The
model's FHP is the explicit opt-in ("Hand positions & fingering"). **The charter's pick
always wins and is remembered per arrangement type** (`AppConfig.AiFingeringJobGuitar` /
`AiFingeringJobBass`; a pick the pane makes for them is not stored). In the menu,
*Fingering only* is the submenu's FIRST row and its tooltip carries the measurement; the
two FHP rows' tooltips say the algorithm won on bass and tied on guitar. The numbers live
in the reason strings: re-measure (with #603's reasoning level) before changing a default.
Tests: `Presentation/Editor/AiChatViewModelTests.TheFingeringTask_DefaultsTo…`,
`AnExplicitPick_Wins_…`, `ANonFingeringTask_IsKept_…`.

Verbs: Tools ▸ Fix & clean up ▸ *Fix FHP & fingering with AI* ▸ *Fingering only (FHP
stays)* / *FHP and fingering together* / *FHP only (fingering follows)* (one submenu,
because `MainMenuLocalizationTests` caps the Tools menu at 10 entries), *Fix chord
names with AI (EOF style)* / *(musical)*, *AI settings…*. Palette: `tools.aiFixFhpFingering`,
`tools.aiFixFhpOnly`, `tools.aiFixFingeringOnly`, `tools.aiFixChordNames`,
`tools.aiFixChordNamesMusical`, `tools.aiSettings`. The submenu rows' short headers carry
the whole verb as `AutomationProperties.Name`.

## The pieces

| File | What it is |
|---|---|
| `AiChartContracts.cs` | jobs (+ `AiChartJobs`: what each lets the model change), naming styles, chunk/sound/reply/plan records, `AiChartRejectionCodes` |
| `AiChartChunker.cs` | phrase chunks with context either side; every sound in exactly ONE core |
| `AiChartPrompt.cs` | one system prompt and one reply schema per job, the per-chunk listing |
| `AiChartReplyReader.cs` | strict decode: an undeclared or missing field rejects the chunk |
| `AiChartValidator.cs` | the STRUCTURAL check: reply → `AiChartChunkPlan`, or a rejection |
| `AiChartRuleGate.cs` | the inspector's own rules on a scratch copy, the list pinned per job (`RulesFor`); a chunk may not INTRODUCE a finding, nor leave one it was asked to fix (`Targets`, #803) |
| `AiChartApplier.cs` | plans → ONE `CompositeAction` (`History_AiChartPass`); `Preview` reads the SAME composition's targets (`AiChartApplyTargets`) without building it |
| `AiChartPass.cs` | one pass: chunk, ask (concurrently), validate in order, ONE retry of a rejected chunk; writes nothing |
| `AiChartReview.cs` | the HELD proposals (#506): one `AiChartReviewChunk` per section that changes something, the charter's `AiChartDecision`, `Marks()` |
| `AiChartPassRun.cs` | the verb as the user sees it: snapshot, rail task + cancel, the held `Review`, `ApplyReview` (one undo step) / `DiscardReview`, the rail line; `RunInstructionAsync` is the AI chat's entry (#560, [ai-chat-instructions.md](ai-chat-instructions.md)): the same core on a chosen arrangement and range, with an instruction, returning an `AiChartRunOutcome` |
| `AiChartRunOutcome.cs` | how a run ended (`AiChartRunStatus`), the rail line it showed, the pass and the held review; `AiChartInstructedRun` |
| `AiStructure*.cs` | the AI chat's SECTIONS & PHRASES job (`AiChartJob.SectionsAndPhrases`, #561) — not chunked, its own prompt, schema, reader, gate and applier; held in the same `AiChartReview` as `Items`. With targets (Fix with AI on D03 I10 I25 I26, #808) the WHOLE accepted proposal must remove them (`AiStructureGate.TargetsLeft`), else it is refused whole and retried once. An added or moved phrase brings the FHP anchor it needs (E06) in the same undo step (#606, `AiStructureChange.Anchors`). See [ai-chat-instructions.md](ai-chat-instructions.md) § Sections and phrases |

UI: `DotCharter.UI/ViewModels/Shell/AiChartViewModel.cs` (`ShellDialogsViewModel.AiChart` — the shell is at its dependency budget)
and its `Review`, `AiChartReviewViewModel` (same folder) bound by `Views/Shell/AiChartReviewView`
(`AiChartReviewOverlay` in `MainWindow.axaml`, over the timeline's top-right corner),
registered in `App.AiServices.cs` (the run) and `App.ViewModelServices.cs` (the VMs).
Tests: `DotCharter.Tests/Ai/Chart/` (fake model, no network; the modes in
`AiChartModeTests`, the review and the retry in `AiChartReviewTests`, the structure job in
`AiStructureTests`, I20 / E06 / targets / the prompt's rule list in `AiChartAnchorRuleGateTests`,
the per-job rule pin in `AiChartRuleGateCoverageTests`), the five schemas in `Services/Ai/AiProviderCompatTests`, the card in
`UI/ViewModels/Shell/AiChartReviewViewModelTests`,
`Perf/AiChartReviewConstructionUiPerfSmokeTests` and `Perf/AiChartReviewSizesUiPerfSmokeTests`
(fixture: `Perf/AiChartReviewHarness`).

## Review before apply (#506)

- **The card (#613, the Inspector's "Rail (dense)" language).** A slim header bar
  (title · counts · ⓘ, whose tooltip carries the job, the explanation and the keys;
  the job and arrangement on a second line), ONE 26 px row per section (name ·
  time span · what changes · ✓ / ✕), a slim footer bar (Accept all · Reject all ·
  Discard · Apply accepted (N)). Clicking a row IS Go to. The card is content-sized
  and never taller than the timeline panel it hangs over — it cannot hang lower,
  because the editor's bottom dock (Inspector + 3D preview) is not the card's to
  cover — so it shows ≥ 6 rows at 1280x720 (a 270 px timeline) and ≥ 9 at
  1920x1080 (360 px); the list's scrollbar never auto-hides. Keyboard, while focus
  is on the card (`AiChartReviewView.HandleKey`, a TUNNEL handler so a focused row
  does not take Enter as its own click and the editor's keybinds never see the
  keys): ↑ / ↓ move `AiChartReviewViewModel.CurrentRow` and go to it, A / R decide
  it, Enter on a row applies. Pinned by `Perf/AiChartReviewSizesUiPerfSmokeTests`
  (rows, scrollbar, one-row footer — in the release's de-DE / ja-JP layout filter)
  and `Perf/AiChartReviewConstructionUiPerfSmokeTests` (click, keys).
- **Nothing is written by the run.** `AiChartPassRun.RunAsync` ends by HOLDING the
  accepted plans as an `AiChartReview` on the LIVE arrangement and level (a plan
  that changes nothing there is not listed) and says so on the rail
  (`AiChart_ReviewReady`). A new run replaces a review still waiting.
- **Per section:** Pending (default) / Accepted / Rejected; "Accept all" accepts
  every PENDING one and "Reject all" rejects every PENDING one (#613) — neither bulk
  verb overrides a section decided by hand. A row's ✓ / ✕ are TOGGLES (a lit one
  clicked again puts the section back to Pending); the keys A / R only ever set. `ApplyReview` writes the ACCEPTED plans through
  `AiChartApplier.Build` as ONE undo node; pending and rejected ones are not
  written. Any subset is a valid write: each section's anchors begin at its own
  start and are replaced only inside its own span.
- **The marks are the write's.** `AiChartReview.Marks()` is `AiChartApplier.Preview`
  of every section not rejected: the chords and hand shapes repointed, the live
  anchors removed and the anchors added BY VALUE (one restated identically is
  neither). `AiChartReviewViewModel` publishes them through
  `ChartEditorViewModel.PublishReviewHighlight` — the #485 `ChordUseHighlight`
  path, which the review wins while it is open — and re-reads them after every
  history step. Pinned against the write by
  `AiChartReviewTests.The_marks_are_exactly_what_the_apply_changes`.
- **The chart stays editable while a review is open.** A plan carries the chords
  it changes, its `Guards` and its `SeenAnchors` (the hand positions the model was
  shown in its span); one that no longer matches the live level is stale, dropped
  from the marks and the write alike.
- **One retry, with the reason.** A chunk the reader, the validator or the rule
  gate rejects is asked ONCE more, in chunk order against the same scratch:
  `AiChartPrompt.Retry` = the first prompt + the rejected reply as the model's turn
  + `RetryHeader` with the code and reason. A second rejection is final; a model
  that did not answer at all (`model-failed`) is not retried. `AiChartPassResult.Retried`
  counts them.

## Rules (reject, never repair)

- **A reply may change ONLY what its job names** (table above): anchors (fret,
  width), chord-template fingers, or names. The reader is per job: a field another
  job owns (`chords` in an FHP-only reply, `anchors` in a fingering-only one) is
  `malformed`. Everything else in it is an ECHO the validator compares: chord ids must
  name CORE sounds (a context id is `context-sound`), frets must equal the chord's
  template frets string for string (`frets-changed`), `ms` must match within 1 ms
  (`timing-changed`), every chord the chunk offered must come back exactly once
  (`chord-count`). Any difference rejects the WHOLE chunk.
- **Structural finger/anchor checks** a plan cannot be built past: a finger on an
  open or unused string (`finger-on-open-string`, L06), a fretted string without one
  (`finger-missing`, L07), the first anchor not at `"start"`, an anchor not on a core
  sound or out of order, an anchor under `AnchorFretSuggester.MinFret(capo)`
  (`anchor-below-capo`, L02), off the neck (`anchor-range`).
- **Fingering only: the anchors are input.** A CHANGED fingering whose index finger
  is not on the fret of the anchor in force (`IndexFingerPosition.Disagrees` against
  `ArrangementValidationContext.FindActiveAnchor`, the E04 question) is
  `anchor-contradicted` — even where the chart already had that E04, which the rule
  gate alone would not call "introduced".
- **FHP only: fingerings follow, deterministically, never by the model.** The plan
  carries `FingersFollowAnchors`; `AiChartApplier` runs `ChordFingeringFollower.Plan`
  over the level with the plan's anchors in place and keeps the repoints of chords an
  ADDED anchor governs. A FREE chord (the suggestion, or a variant the follower
  minted) moves to its base grip shifted to the new anchor, resolved through
  `FindIdenticalTemplate` or minted (and marked) in the SAME node; an AUTHORED one is
  never touched and is shown to the model as `fingers [...] fixed`, so a reply that
  contradicts it introduces E04 and the rule gate rejects the chunk. The minted mark
  is a template field (`ChordTemplate.FollowedFingering`, #508), so the run's
  snapshot and the gate's scratch copy carry it like any other and judge free and
  authored exactly as the live write does. The plan's `Guards` (the core's chords) make it stale if
  they changed meanwhile; the gate skips that check (`checkGuards: false`), because
  there an earlier chunk's follower may legitimately have repointed a later chunk's
  chord.
- **`AiChartRuleGate` runs the job's production rules** (`RulesFor`): every chunked
  job runs `CapoAnchorRangeRule`, `ChordRule`, `HandShapeRule`, `IndexFingerAnchorRule`
  and `TemplateAndOverlapRule`; a job that moves anchors (FHP + fingering, FHP only)
  also runs `AnchorRule`, `NoteRule` and `PhraseAnchorRule` (#803 — the model's own
  anchors broke I20 and E06 and nothing caught it). They run before and after executing
  the plan's own action on a one-level scratch clone. A chunk that introduces any
  `Guarded` finding — L02 L03 L04 L05 L06 L07 L08 E04 E06 I20 I21 I42 I43 I44 I45 I46
  I47 — between its start and the first anchor after its end is undone on the scratch
  and rejected (`rules`), the reason quoting the prompt's own rule line. E06 is timed at
  the phrase marker, which lies inside the chunk's reach, so a phrase start INSIDE a
  merged chunk is judged too. Findings the chart already had are not the reply's fault.
  **The list is pinned per job** by `Ai/Chart/AiChartRuleGateCoverageTests`: every rule
  in `ArrangementValidationService.DefaultRules()` is either in the job's gate or listed
  there with why the job's output cannot trip it — a new production rule fails the gate
  until someone decides.
- **The model is told the rules up front** (#803): each FHP / fingering system prompt
  carries the rules its output can trip, code first (`AiChartPrompt.RuleList`,
  `RuleCodes(job)`, `RuleText(code)`), and the listing of a job that moves anchors marks
  each phrase start inside the chunk (`--- phrase "x" starts at … it needs its own
  anchor (E06) ---`) and every sound strictly inside a hand shape (` in-shape`, I20).
  The hash pin `AiStructureTests.The_fhp_and_fingering_prompts_are_unchanged` was
  re-pinned for it.
- **Fix with AI: targets** (#803). `AiChartPassRequest.Targets` (code, time, string) are
  findings the pass must REMOVE: a chunk whose reach holds one that is still there after
  its plan — including a reply that changes nothing — is rejected (`finding-left`) and
  retried with the finding named. `AiChartPassResult.Leftovers` is the gate's findings
  left in the pass's range with every accepted chunk applied; `AiChartPassRun.ReviewApplied`
  reports the ones left on the LIVE level after an Accept (range: `AiChartReview.PassFrom`
  / `PassTo`) — for a menu verb's review too, which the chat reports when findings are left (#808).
  `AiChartFindingTarget.TimeMs` is null for an untimed finding (I26); only the structure job aims at
  one, and the chunked gate never matches it. `AiChartPassRun.ScopeFindings` is the job's gate run on
  the level an instructed run reads, in a scope: what a typed FHP / fingering chat message takes as
  its targets (#808). The chat side is [ai-chat-instructions.md](ai-chat-instructions.md) § Findings.
- **Shared templates are never edited.** A changed fingering or name resolves
  through `IFingeringGenerationService.FindIdenticalTemplate` (on a probe holding the
  arrangement's templates plus those minted so far, so ten chords re-fingered alike
  converge on ONE template) or mints a clone with `NextTemplateId`. A hand shape that
  pointed at the chord's old template follows when every such chord under it went to
  the same new one. Minted templates, repointed chords and hand shapes, removed and
  added anchors: one `CompositeAction`, one Ctrl+Z.
- **Context is never applied.** A chunk's anchors are replaced only inside
  `[Start, End)`: `Start` is the phrase marker (or the first selected sound), `End` is
  the next chunk's start, or for the last chunk the next sound or phrase marker. The
  first anchor of every reply is at `"start"`, so chunks are self-contained and the
  model calls run concurrently (`AiChartPass(concurrency: 3)`).
- **The live chart is snapshotted** (`ChartProjectCloner.CloneArrangement`) on the UI
  thread; the pass runs on the snapshot in `Task.Run`; a plan whose chord no longer
  matches the live chart (index, position, frets, template id) or whose span's
  anchors changed (`SeenAnchors`) is dropped as stale.
- **Web research is forced OFF** for this lane (`settings with { WebResearch = false }`);
  the CLI provider would otherwise prepend the tone designer's research instruction.
- **Provider not configured** → the rail says so and names Settings ▸ AI; nothing is
  minted (`AiChartPassRunTests`).

## Chunking

`AiChartChunkOptions.Default`: phrases merged until ≥ 24 sounds, cut at 64, 6 sounds
of context before and 10 after (the hand position needs look-ahead). Ids are
`s<index>` over the level's time-sorted sounds (notes before chords at one position).

## Measured (held-out, `tools/chart-eval/AiChartEval.cs --job joint|fhp|fingering`)

See `tools/chart-eval/README.md` § AI pass for the per-mode table, the tokens and the
time. The algorithm column there is ChartEval's `auto` mode on the same tree (the walk
with free grips, then the follower). Information, not a gate: re-measure rather than
carrying numbers forward.

## Known limits

- A section the CHARTER rejects is not re-asked: the retry is for the checks'
  rejections, which come with a reason to send.
- Marks are drawn for the review's level; on another level only the card shows.
- The name job's style is a verb per style (EOF / musical); #493's default-style
  decision is the owner's.
