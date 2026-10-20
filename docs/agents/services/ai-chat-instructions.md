# AI chat instructions: FHP / fingering proposals (`DotCharter.Services/Ai/Chat/`, #560)

Part of the AI chart chat epic (#558). A chat message with an instruction ("use 1·3
for two-fret octaves in the verse", "keep the hand in 5th position in the solo") runs
the AI chart pass ([ai-chart-pass.md](ai-chart-pass.md)) on a scope the chat chose,
with the instruction and the recent conversation in the prompt, and HOLDS the result
for the #506 review card. **It never writes the chart.**

## The API the pane calls

`IAiChatChartActions` (`AiChatChartActions.cs`), implemented by
`AiChatInstructionRunner(AiChartPassRun, ILocalizationService)`:

- `RunInstructionAsync(AiChatInstructionRequest, ct)` — the request is the
  instruction, an `AiChatScope`, the `AiChatConversation` so far (or null), the
  model the pane picked (or null for Settings ▸ AI's) and the `AiChartJob`
  (`FhpAndFingering` when the record is built without one, `FhpOnly`, `FingeringOnly`, `SectionsAndPhrases` —
  § Sections and phrases; `Explain` — § Ask / explain; `Tones` — § Tones; `ChordNames` throws).
  Call on the UI thread. A failed run does not throw: the reply says why.
- The reply (`AiChatInstructionReply`): the `AiChartRunStatus`, the assistant's
  `Text`, the held `AiChartReview` (null when nothing is proposed) and the tokens
  as `AiChatUsage`. `ToTurn(at)` makes the assistant `AiChatTurn`, linked to its
  card by `AiChartReview.Id` (`AiChatTurn.ReviewId`).
- `IsRunning` — one chart pass at a time, shared with the menu verbs
  (`AiChartRunStatus.Busy` otherwise).

**Registered beside `AiChartPassRun` in `App.AiServices.cs`** and driven by the chat
pane's Send (`Presentation/Screens/Editor/AiChatViewModel`, through the `AiChatServices`
bundle): the pane builds the `AiChatScope` from its scope picker — `Selection` from
`AiChartPassRun.RangeOf` of the editor's selection, `SectionAt` the playhead
(`TimelineMath.PositionFromMs`) on the active level, or `WholeArrangement` — on the
arrangement picked, refuses a scope that covers nothing BEFORE any run (the scope line
says why), starts the run on the UI thread, stores the user turn while it works and
the reply's `ToTurn` after, and shows the tokens. Stop cancels the run.
`Presentation/Editor/AiChatViewModelTests` pins it over a fake `IAiChatChartActions`.

The pane's words follow the Task picker (#640): `Presentation/Screens/Editor/AiChatJobText` is
the ONE table of the five tasks — label, empty-pane line (`AiChat_Empty_<Task>`: what the
task does and what to say) and the box's hint (`AiChat_Hint_<Task>`: an example message).
A reply that came back with no answer (`Failed` or `Busy`) offers **Retry** on the LAST
turn only: the same `AiChatInstructionRequest` again, in that reply's place, one run at a
time; it applies nothing (a proposal still goes to the review card). A message sent after
it, another chat or another chart retires it. Pinned by `AiChatViewModelTests`
(`EachTask_HasItsOwnEmptyLine_AndHint`, `Retry_SendsTheSameRequestAgain_…`) and the pane's
`Perf/AiChatPaneConstructionUiPerfSmokeTests`.

## Scope: `AiChatScope`

- `WholeArrangement(arrangement?)`, `Selection(range, arrangement?)` (null when
  nothing is selected — never silently the whole arrangement; the range is
  `AiChartPassRun.RangeOf` of the editor's selection), `SectionAt(arrangement,
  level, position)` (the phrase marker at or before the playhead up to the last
  sound before the next one — the same `PhraseMarkers.All` the chunker cuts by).
- `Arrangement` null = the active one. Another arrangement of the open project runs
  on its hardest level (what `ProjectState` makes active on switching to it); one not
  in the project is refused (`NoProject`). Marks are drawn only on the review's level.
- The scope becomes the pass's inclusive `From`/`To`: only those sounds are chunked
  and offered; the rest is at most context.

## The prompt

- `AiChartPassRequest.Instruction` (`AiChartInstruction`: text + conversation)
  reaches every chunk's prompt as ONE section after the listing
  (`AiChartPrompt.InstructionSection`, headed `InstructionHeader`). The system prompt
  and the schema stay the job's, so a provider still caches them, and the retry
  (`AiChartPrompt.Retry`) carries the section because it extends the first prompt.
- **No instruction ⇒ byte-identical prompts** to the menu verbs (`Build(...)` with
  null appends nothing).
- Bounded by `AiChatExcerpt`: the instruction collapsed to one line and capped at
  2,000 chars; the last 8 turns before it, one line each (`charter:` /
  `assistant:`), each capped at 600 chars, the excerpt at 3,000 (newest kept). A last
  user turn equal to the instruction is not quoted twice. Collapsing whitespace means
  a turn cannot fake the prompt's own `---` section lines.

## Rules (the pass's, unchanged)

- **Only anchors and chord fingering can change** — the job's reply schema, reader
  and validator decide, not the prompt. An instruction to move, add or delete a note,
  add a technique, change a fret or move a chord in time yields a reply the pass
  refuses (`malformed`, `frets-changed`, `timing-changed`, `chord-count` /
  `unknown-sound`), retried once with the reason, then dropped; nothing is held.
- Same rule gate, one retry, snapshot and stale-chart checks as the menu verbs: it
  is `AiChartPassRun.RunInstructionAsync`, sharing `RunCoreAsync` with `RunAsync`.
  The held review replaces one still waiting, and opens the card through
  `AiChartPassRun.ReviewChanged`.

## The reply text

Held: a header with the section count, one line per held section — its phrase names
(or "Untitled section"), its start as m:ss, and what it changes in the review card's
OWN words and order (`AiChartReviewViewModel.RowDetail`: `AiChartReviewChunk.HandPositionsChanged`
and `Changes.ChordsRefingered` through the `AiChartReview_*` plural strings, a zero left
out) — the sections the checks refused (`AiChartReview_Failed*`), and where to accept
them (`AiChat_ReviewHint`). New plural pairs go into `UI/PluralStringTests`' table. Anything else: the same line
the run put on the status rail (`AiChartRunOutcome.Message`).

Tests: `DotCharter.Tests/Ai/Chat/AiChatInstructionRunnerTests.cs` (fake model, no
network).


## Sections and phrases (#561)

The pane's **Task** picker (`IAiChatScreen.Jobs` / `SelectedJob`) chooses the job:
"Fingering (hand positions stay)" (`AiChartJob.FingeringOnly`, the measured default on
guitar and bass, #602), "Hand positions & fingering" (`AiChartJob.FhpAndFingering`, the
opt-in), "Sections & phrases" (`AiChartJob.SectionsAndPhrases`), "Ask / explain" or "Tones". A
fingering pick is remembered per arrangement type and the line under the pickers
(`IAiChatScreen.JobNote`) prints the measured reason; switching arrangement re-applies the
default only while a fingering task is picked (`AiChatTaskDefaults`,
`AiChatViewModel.Task.cs`; the table is in [ai-chart-pass.md](ai-chart-pass.md)). The model
picker carries the reasoning level beside it (`IAiChatScreen.EffortText`, #603,
[ai.md](ai.md) § Reasoning effort). **Explicit on purpose, never guessed from
the message**: "verse" and "section" turn up in fingering instructions too, and the chat
is written in three languages. `AiChatViewModel.Job()` puts it on the request;
`Presentation/Editor/AiChatViewModelTests.Send_RunsTheTaskThePickerNames_…` pins it.

`AiChartPassRun.RunInstructionAsync` hands that job to `RunStructureAsync` instead of the
chunked pass (`DotCharter.Services/Ai/Chart/AiStructure*.cs`):

- **One request over the scope** (`AiStructurePass`) on a snapshot of the whole PROJECT:
  a song's form is only visible whole. The prompt (`AiStructurePrompt`) lists every bar
  (start, beats, note/chord counts, fretted span, a REPEAT LETTER shared by bars whose
  sounds are identical, and the beats an anchor starts on as `A@beat`), every section
  `sN` and phrase `pN` marker with its bar and beat, the scope, and the instruction
  section (`AiChartPrompt.InstructionSection`, shared). Its system prompt carries the
  `RsSectionCatalog.Names` vocabulary. **The FHP / fingering prompts are untouched** —
  pinned byte for byte by hash (`AiStructureTests.The_fhp_and_fingering_prompts_are_unchanged`).
- **A closed set of operations**: `add`, `rename`, `move`, `remove` on a `section` or a
  `phrase` (`AiStructureOp`, `AiStructureKind`). Positions are bar (+ 1-based beat) on
  `AiStructureGrid` — bars from `BeatMarker.IsMeasure` — so nothing can land between beats,
  and a section's beat must be 1 (the bar line).
- **Read strictly, refused whole** (`AiStructureReply.TryRead`): an undeclared field or
  key (`notes`, `fret`, `anchors`), an unknown op or kind (`not-structure`), a section name
  outside `RsSectionCatalog` (`section-name`; stored lower case), a phrase name
  `RsPhraseName.IsLegal` refuses (`phrase-name`), no such bar/beat or a section off the bar
  line (`off-grid`), outside the scope (`out-of-scope`), an unlisted id (`unknown-marker`),
  a phrase only the imported list has (`imported-marker` — `PhraseIterations` are listed as
  `[fixed]`), two changes on one marker or a marker onto one of its kind (`conflict`).
  One retry with the reason (`AiStructurePrompt.Retry`), then dropped.
- **The export's rules: no new chart-quality-report finding** (`AiStructureGate`). Each
  change runs on a scratch copy of the project through the very action the write uses, and
  `ChartQualityReporter.Build` (every arrangement rule + the export gate, "Ready to ship?")
  is compared before/after by (code, arrangement, level, ms); a change that adds one is
  refused (`rules`) and the others are held. `ApplyReview` re-judges the ACCEPTED subset
  (`AiStructureGate.Introduced`): one that would add a finding writes nothing and LEAVES THE
  REVIEW OPEN with the codes on the rail (`AiStructure_RulesRefused`).
- **A phrase brings the hand position it needs (#606).** A phrase that plays notes must own an
  FHP anchor from its start to its first note on every level (E06). Before judging an added or
  moved phrase, `AiStructureGate.TryAccept` puts it on the scratch alone and asks
  `Editing/FretAnchors/PhraseStartAnchors.Needed` (the one E06 reading the auto-fix shares) per
  level: the anchor in force RESTATED at the phrase start (same fret and width, floor
  `capo + 1` — the hand does not move), or, with none in force, the FHP generator's anchor at
  the phrase's first sound; never an invented fret, never one where an anchor already covers
  it. The change is accepted WITH those anchors (`AiStructureChange.Anchors`, by level index,
  fret, width — the model cannot name one; the reader still refuses `anchors`) and the report
  judges both together, so a phrase that still adds a finding is refused (a start inside a hand
  shape: the anchor is I21 — the reason says "even with the hand position it needs at fret N").
  `AiStructureApplier` writes them as one `AddAnchorsAction` per level inside the SAME
  `CompositeAction`; an anchor whose spot the charter filled meanwhile is left out, and no
  existing anchor is moved or removed. The row reads "New · + hand position at fret 5"
  (`AiStructureText.AnchorNote`, `AiStructure_AnchorAdded` / `AiStructure_AnchorsAdded`, also
  in the chat reply), and `StructureMarks().Anchors` reach the timeline and song map through
  `ChordUseHighlight.ForEvents(…, proposedAnchors)` as the FHP review's proposed anchors.
  Measured by fake-model replay (a phrase on every 4th bar line with no phrase yet, through
  `AiStructureGate` exactly as the pass runs it; a `LocalOnly_` probe whose source is in the
  #606 pull request): 8 corpus charts, 295 proposals, refused 218 → 67; the E06 refusals (173) all go,
  what is left is D03 (empty phrase), I21 and I10 (a phrase change under a link-next note).
- **Held, never written by the run**: `AiChartReview.HoldStructure` lists one
  `AiStructureReviewItem` per change in `Items` (`Chunks` stays empty); decisions, Accept all
  and counts cover both lists. `StructureMarks()` is `AiStructureApplier.Preview` of every
  change not rejected; `AiChartReviewViewModel` publishes it with
  `ChordUseHighlight.ForEvents` — the #485 / #506 highlight path — so the 2D ribbon draws the
  replaced markers faded (`RenderEventPoint.SectionIsEditTarget` / `PhraseIsEditTarget`) and
  the proposed ones in the EditTarget colour (`FrameSnapshot.ProposedEventPoints`,
  `EventLayer`), and the song map ticks them from the top edge (`SongMapMarks.AppendEvents`).
  Row text is `AiStructureText`, shared by the card and the chat reply.
- **The write** (`AiStructureApplier.Build`) is ONE `CompositeAction` of
  `Mutate/Remove/AddEventPointsAction` on `Arrangement.EventPoints`, plus the
  `AddAnchorsAction`s a phrase's anchors need (#606, above) — nothing else is reachable. Clears run before sets; an event point keeps its `Events` and its other field
  and is removed only when left empty. A rename/move/remove guards on the live marker's
  position AND name: one the charter edited meanwhile is stale and left out of marks and
  write alike. Undo restores the markers exactly (`AiStructureTests.Accepting_writes_one_undo_node_…`).

Tests: `DotCharter.Tests/Ai/Chart/AiStructureTests.cs`, `Ai/Chart/AiStructurePhraseAnchorTests.cs`
(#606), `Ai/Chat/AiChatStructureReplyTests.cs`, `UI/ViewModels/Shell/AiChartReviewStructureTests.cs`,
`UI/ViewModels/Shell/AiChartReviewPhraseAnchorTests.cs`, `Rendering/StructureMarkRenderDataTests.cs`
(fake model, no network).


## Ask / explain (#563)

The **Task** picker's fourth entry, "Ask / explain" (`AiChartJob.Explain`, `AiChat_Job_Explain`):
"why 1·3 here?", "what's wrong with bar 34?" get a TEXT answer. **Nothing is proposed, held or
written**, and it is still explicit (picked, never guessed from the words).

- `AiChartPassRun.RunInstructionAsync` hands it to `RunExplainAsync`: ONE request
  (`AiExplainPass`) on a snapshot of the whole project, under the same busy guard, provider
  resolution, rail pill and cancel as the other jobs. **It does not clear `Review`**: the charter
  may be asking about the card that is waiting.
- The prompt (`AiExplainPrompt.Build`): the scope as ONE chunk of the pass's own listing
  (`AiChartPrompt.Listing` with `Explain`: anchors, notes, chords with frets, fingers and name, a
  few ctx sounds either side, no `[fix]` marks; capped at `MaxSounds` and saying so), the
  arrangement's sections and phrases (`AiStructureContext` markers, by bar and beat), the bars the
  listing spans with their start times, the **chart quality report's findings in the scope**
  (`ChartQualityReporter.Build` on the snapshot → `AiExplainPrompt.Findings`: this arrangement, this
  level or none, timed inside the scope or untimed; code, severity, m:ss, bar and the check's own
  message via `QualityFindingText`; at most `MaxFindings`), then the question and the conversation
  excerpt (`AiChatExcerpt`, as for instructions). Its own system prompt and a one-field schema
  (`{"answer": string}`), so a provider caches them. **The FHP / fingering prompts are unchanged**:
  the `Explain` branches in `AiChartPrompt.Sound` / `AiChartJobs.ListsChords` touch no other job's
  bytes (`AiStructureTests.The_fhp_and_fingering_prompts_are_unchanged`).
- The reply (`AiExplainPrompt.TryReadAnswer`): the `answer` string; ANY other field (anchors,
  chords, changes) is ignored, never read — this path has no reader, validator or applier — and
  logged as ignored. A reply that is not JSON is taken as prose. No answer ⇒ `Failed` with
  `AiChat_ExplainNoAnswer`. An answer ⇒ `AiChartRunStatus.Answered`, the text as the outcome's
  `Message`, which the runner returns as the reply (`Review` null, so `ToTurn` has no `ReviewId`);
  the turn and its tokens are stored like any other. The rail's main line is left alone.

Tests: `DotCharter.Tests/Ai/Chat/AiChatExplainTests.cs` (fake model and fake rules: the answer,
no card, the chart deep-compared unchanged, the scope's findings in the prompt and out-of-scope
ones not, a reply with proposals answered with its text alone, a waiting card kept),
`Presentation/Editor/AiChatViewModelTests.Send_RunsTheTaskThePickerNames_…`, and
`Perf/AiChatPaneConstructionUiPerfSmokeTests.TheTaskPicker_OffersAskExplain_…` (the real pane view
model in en-US, de-DE and ja-JP at 340 px, the dropdown opened).


## Findings: Fix with AI, Explain with AI, what a pass left (#803, #808)

`AiChatFindings` (`Services/Ai/Chat/`) is the one table and the one request builder:

- **Which job fixes a finding** (`JobFor`): the hand-position ones (L02 L03 L04 L05 E06 I20
  I21 I42 I43 I47) → FHP only, the finger ones (L06 L07 L08 I44 I45) → fingering only, E04 and
  I46 → both; a mix → both. Those are exactly the rule gate's `Guarded` set
  (`AiChartRuleGateCoverageTests.Every_finding_fix_with_ai_offers_…`). The phrase findings
  (D03 I10 I25 I26, `StructureTypes`, #808) → sections & phrases; a phrase finding mixed with a
  hand-position or finger one has no single job (`JobFor(issues)` is null, and the Issues tab's
  "Fix all N in this phrase" leaves the other kind out). Anything else — a note or link-next
  finding — has no job and the Issues tab says so. `Fixes(job, type)` says whether a RUN of a job
  can remove a finding: its own, the joint job for any hand-position or finger one, either half for
  E04 / I46.
- **`FixRequest`**: the job, the scope (every finding's phrase joined, `Scope`; E06 sits ON its
  marker), the findings with code, bar, time and message, and each code's prompt rule line as
  the instruction, and `Targets` — the gate rejects a chunk that leaves one (`finding-left`).
  Held for the review card like any run; Accept is one undo step.
- **Phrase findings (#808)** take the sections & phrases job on the WHOLE arrangement (a form is
  only visible whole, and an END after the last note lies outside any phrase). An untimed finding
  (I26) is a target too (`AiChartFindingTarget.TimeMs` is null; the chunked gate never matches one).
  `AiStructurePass` judges each change alone as before, then the WHOLE accepted set:
  `AiStructureGate.TargetsLeft` — a target is left when the quality report still has its code in
  the arrangement at its time (±1 ms, any level; untimed matches untimed). A proposal that leaves
  one is refused whole (`finding-left`, its findings named), asked ONCE more, and dropped whole
  (`AllRejected`, `AiStructure_Refused`) when the retry leaves one too. The charter's Accept of a
  SUBSET is not re-judged against the targets — only against new findings, as before.
- **A typed FHP / fingering message (#808)** with no targets of its own takes the scope's findings
  as targets: `AiChatInstructionRunner` asks `AiChartPassRun.ScopeFindings` (the job's rule gate on
  the level the run reads, in the scope) and keeps the timed ones the job `Fixes`; the instruction
  gets them after the charter's words (`TargetBlock`, English like the prompt, at most
  `MaxTypedTargetLines`, with each code's rule line), and the reply says how many were added
  (`AiChat_TypedTargets_One` / `_Many`). A chunk that leaves one is refused and retried like Fix
  with AI. Sections & phrases, ask / explain and tones messages take none.
  `AiChatInstructionRunner.FindingsAsTargets` (internal, default true) exists only for the tests
  of other contracts whose fixture has findings.
- **`ExplainRequest`**: the Ask / explain job (#563) on the finding's phrase, the finding and its
  rule text as the question. It writes nothing.
- **Leftovers.** `AiChatInstructionRunner` appends the gate's findings still in the pass's range
  (`AiChartPassResult.Leftovers`) to the reply. With a review held they are what accepting all of
  it would leave (`AiChat_Leftovers_HeldHint`); with nothing held the reply carries `FollowUp`,
  the fix request for them. After an Accept, `AiChartPassRun.ReviewApplied` →
  `IAiChatChartActions.Applied` (`AiChatApplied`: the review id, the words, the follow-up); the
  pane adds it as the assistant's turn when one of ITS turns holds that review id. **A menu
  verb's Accept (#808)** — a review the runner did not hold itself (it keeps the ids it held) — is
  raised only when findings are left, with `FromMenu` and `AiChat_MenuPassApplied` before the
  list; the pane adds it to the open conversation (or starts one) with the same "Ask the AI to fix
  these" offer. It does not open the pane: the rail still reports the write.
- **The pane** (`AiChatViewModel.Task.cs`): it is the `IAiChatFindingsDoor` the Issues tab calls
  (`AskAsync`: the request's instruction becomes the charter's turn; `ShowRequested` makes the
  shell open the pane), and a reply with a follow-up offers "Ask the AI to fix these"
  (`IAiChatTurn.CanFix` / `FixCommand`, `AiChatFixThese` in the pane). The door is registered
  lazily in `EditorModule` (`Func<IAiChatFindingsDoor?>`), so building the inspector does not
  build the chat.

Tests: `Ai/Chat/AiChatFindingsTests.cs`, `Ai/Chat/AiChatFindingTargetsTests.cs` (#808: the
phrase fix and its whole-set check, typed targets, the menu Accept), `Ai/Chart/AiChartAnchorRuleGateTests.cs`,
`Presentation/Editor/AiChatViewModelTests` (`TheFindingsDoor_…`, `AReplyWithFindingsLeft_…`,
`AnAcceptOfThisChatsReview_…`, `AMenuPassesAccept_…`), `UI/ViewModels/Editor/IssueAiViewModelTests`,
`Perf/IssueAiRowUiPerfSmokeTests`, `Perf/AiChatPaneConstructionUiPerfSmokeTests` (the
`FindingsLeft` variant).


## Tones (#614)

The **Task** picker's fifth entry, "Tones" (`AiChartJob.Tones`, `AiChat_Job_Tones`): a Rocksmith tone
for each tone definition the scope's placed tone changes name, from the song's audio windows and the
chat's words ("the chorus tone is a high-gain Mesa, darker"). Still explicit, never guessed.

- **Scope read as tones** (`AiChatScope.Tones`, carried as `AiChatScope.ToneChanges` →
  `AiChartInstructedRun.ToneChanges`): Selection = the selected tone changes, This section = the change
  in force at the playhead, Whole arrangement = every placed change. The scope line names the tones
  (`AiChat_Summary_Tones`) or why there are none; a scope covering none is refused before any run. The
  playhead case rewrites the line only when the change in force changes (a loop, no allocation).
- **The run** is the W6 generator ([ai-tone-generate.md](ai-tone-generate.md) § The AI chat's Tones
  task): cached stems only, the instruction + conversation excerpt as the prompt's own section, the
  closed-catalogue gate, one proposal per tone definition. The FHP / fingering, sections and explain
  prompts are untouched (their hash and explain tests).
- **Held for the review card** (`AiChartReview.Tones`); the reply (`AiChatInstructionRunner.ToneSummary`)
  lists each tone with its chain (`AiToneText.Chain`), the refusals with their reasons, and
  `AiChat_TonesReviewHint`. **Apply writes the accepted `ToneDefinition` bodies as one undo step,
  nothing else** (`AiToneApplier`); Audition plays a tone's window with the proposed body on the rig.

Tests: `Ai/Chat/AiChatTonesTests.cs`, `UI/ViewModels/Shell/AiChartReviewTonesTests.cs`,
`Presentation/Editor/AiChatViewModelTests.Send_OnTheTonesTask_…`, and
`Perf/AiChatPaneConstructionUiPerfSmokeTests.TheTaskPicker_OffersTheTask_…` (Ask / explain and Tones,
en-US, de-DE, ja-JP at 340 px).
