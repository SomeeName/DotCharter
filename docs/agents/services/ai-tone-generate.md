# Generate ROCKSMITH tones from placed tone changes + stems

`DotCharter.Services/Ai/Generate/**` — one **in-game Rocksmith tone** per placed
`ToneChange`, built from that change's window of the arrangement's own separated
stem plus what is known about the song. The user-facing name is **Generate tones
with AI…** (Tools ▸ Tone & sound, and the palette's `tools.generateRocksmithTones`);
the command is `ToneRigViewModel.GenerateRocksmithTonesCommand`, exposed as
`MainWindowViewModel.ToneRig`, and it is one line over **`AiToneGenerationRun`**
(the generator arrives there as a `Func<IRsToneGenerator?>` factory).

**Not the Tone designer's "Estimate tones from audio…"** (`Editing/Tones/ToneGenerationRun`,
[../ui/tone-designer.md](../ui/tone-designer.md)). They are different products and both
stay (#216): that one measures the audio on this computer, picks gear by rule, needs no
provider and never overwrites a tone; this one asks the configured AI provider, isolates
the arrangement's own stem, validates against the game's catalogue and REPLACES the
arrangement's tone slots and the placed changes' names. Each surface binds exactly one of
them, and each tooltip names the other by its exact label in all three languages —
`UI/ToneGenerationReachabilityTests` fails a rename of one without the other.

**The menu verb asks for the charter's own words first (#316 part 1).** `ToneRigViewModel.GenerateRocksmithTonesCommand`
opens the hosted dialog `IAiToneGenerateScreen` (`AiToneGenerateViewModel`, `Views/Dialogs/AiToneGenerateDialogView`): a
multi-line box, capped at `AiToneContextText.MaxLength` (500) characters with a counter, remembered per project in
`DialogMemory`. Generate answers the cleaned text (whitespace collapsed, cut at the cap, empty allowed); Cancel answers
`null` and nothing runs. `AiToneGenerationRun.RunAsync(notes)` appends it to `ToneGenerateRequest.Context` as one more part
after the metadata (`..., the charter's notes: <text>`), so it reaches every window's prompt under the existing CONTEXT
header; an empty box leaves the context, and so the prompt, byte-for-byte what the metadata alone wrote. Pinned by
`Ai/Generate/AiToneContextTextTests`, `AiToneGenerationRunTests` and `Presentation/Dialogs/AiToneGenerateViewModelTests`.

**A second door: the AI chat's Tones task (#614)** — § The AI chat's Tones task below. Same
generator, same gate; it writes tone BODIES only and holds them for review first.

## What the user sees: `AiToneGenerationRun`

- **A status-rail task** (`TaskId` `ai-tone-generate`) from the first window to the
  write: determinate, its bar the share of tone changes started (the generator's
  optional `IProgress<double>`), its label the generator's latest localized line, and a
  cancel affordance on the pill.
- **Every outcome is the rail's main line** through `ProjectState.SetMessage` with its
  severity: nothing to generate from (Warning), cancelled (Warning), the project
  switched mid-run (Warning, nothing written), none generated (Error, naming the first
  reason), done (Success). An Info log line alone never reaches the rail; the
  per-tone trail goes to the log as well.
- **Never on the UI thread:** the project is read on the caller's thread, the generator
  runs inside `await Task.Run(...)`, and the write lands back on the caller's context as
  ONE undo step. The command is an async `[RelayCommand]`, so the menu item is disabled
  while a run is in flight.
- Pinned by `Ai/Generate/AiToneGenerationRunTests` (task + progress + rail line, a
  blocking generator does not block the caller, cancel, project switch, none generated,
  nothing to do).

**Not the same lane as `Ai/Match/`.** The matcher produces a `DcRigPreset` — the
app's own DSP rig, which the app can PLAY. This produces an `RsTone` — gear the
GAME has, which a package can ship. They share the stem isolation, the window
trimming and the W5b confidence scale, and nothing else.

## The three seams

```
audio  -> IToneEvidenceSource -> ToneWindowFeatures   (measured, named, testable)
model  -> IRsToneMapper       -> RsTone               (features + context -> GEAR)
both   -> IRsToneGenerator    -> ToneSetGenerationResult
```

**`IRsToneMapper` is the model boundary.** Everything above it is deterministic
and runs in the default gate with no provider, no engine and no separation;
everything below it is one API call. `LanguageModelRsToneMapper` is the shipping
implementation, over **`IToneDesignModel`** — the app's configured tone-design
provider (Claude API / an OpenAI-compatible server / the Claude Code CLI,
whichever Settings ▸ AI names), resolved PER CALL through
`AiProviderResolution.Create` so a provider change takes effect without
rebuilding the container.

It does **not** go through `IToneDesignService`: that service's system prompt is
the app's own DSP module catalogue and four few-shot `DcRigPreset`s, which is
grounding for emitting a DC rig and active misdirection for emitting Rocksmith
gear. Own prompt, own schema, own validator; same provider, transport and key
store.

## Features first, and the model is a MAPPER

`ToneWindowFeatureExtractor` measures eleven **named, unit-carrying** quantities
from the window: RMS/peak dBFS, crest dB, spectral centroid and 85 % rolloff,
**spectral tilt in dB/octave**, flatness, 2–6 kHz-vs-200–800 Hz presence, decay to
−20 dB, and modulation depth + rate over 0.5–12 Hz.

**Why not the `tonefx-v2` vector.** It is the right thing to COMPARE two tones
with and the wrong thing to DESCRIBE one: its 357 elements are normalised into
"1.0 is a large perceptual step" and carry no names, so a prompt built from them
is 357 numbers a model can only pattern-match. Only the LEVEL block is read from
the shared extractor (it is the one block in absolute units); the rest are
measured here because no named form of them exists.

**Why not raw audio to a multimodal model.** Eleven measured numbers can be
asserted in a unit test, printed in a run report, and disagreed with by a human
who says "a tilt of −3 dB/octave is not a closed-back 4×12". A spectrogram a
model looked at can be none of the three. `IToneEvidenceSource` is the seam a
raw-audio arm would arrive behind — a second implementation contributing its own
prompt text, with the mapper unchanged.

## The window: one MEASURE of lead, clamped

`ToneWindowPlanner`, pure over the beat map.

**A window starts one measure BEFORE the change**, because a charter places a
tone change by ear and by ear is late: the moment the amp opens up can precede
the marker by most of a bar, and a window starting AT the marker analyses the
tone that follows the one being described.

**One measure is MUSICAL time**, taken from the `BeatMarker.IsMeasure` spans
around the change — never a fixed millisecond value, and never derived from BPM ×
beats-per-measure (that disagrees with the grid the charter reads wherever a
marker was nudged). Measured on the owner's Sakasma at ~220 BPM: **1,091 ms**.

Two clamps: the previous tone change, and the start of the arrangement
(`ToneWindowClamp` says which bit). **A window ENDS where the NEXT one STARTS**,
not at the next change — that is the whole no-overlap rule, and its consequence
is worth knowing: two changes closer together than a measure leave the EARLIER one
a window that is entirely its own lead, the bar before its marker. On this
planner's own premise that bar is the better half.

**No beat map ⇒ no window, and the caller is told so** rather than handed a
fixed-millisecond guess that would be half a bar in one song and three in another.

## The catalog gate — reject, never repair

`RsToneCatalogValidation`. Rocksmith's gear is a fixed list shipped with the game
(63 amps, 450 cabinets, 83 pedals, 17 racks, each with declared knob ranges), so
a tone naming anything else is not a tone that sounds slightly wrong — it is a
tone the game refuses. A model asked for gear WILL invent a plausible pedal name.

Eleven fault codes (`RsToneFaultCodes`): unknown gear, wrong gear type for the
slot, unknown knob, out-of-range knob, non-finite knob, bad enum index, a fifth
slot on a four-slot rail, a bad slot index, a volume outside the game's range, an
invented descriptor, an unusable tone key.

**`RsToneAuthoring.ClampKnobValue` is deliberately NOT used here**, and
`RsToneWriterLintTests` fails the gate if it appears in this folder. Clamping is
right in the tone EDITOR, where a human dragged a knob past its stop and meant
"as far as it goes". For a generated value it is wrong: a model answering
`Gain 250` on a 0–100 knob misread the unit, and silently writing 100 turns a
detectable fault into a tone nobody can explain.

**Nothing bypasses it.** `RsToneReplyReader` validates before it returns,
`RsToneProjectWriter` validates the whole set again before it writes anything
(all-or-nothing), and `RsToneWriterLintTests` source-lints that every file under
`Ai/Generate/` constructing an `RsTone` also reaches the validator — comments and
string literals masked out, with a behavioural pair so a source lint is not the
only claim.

## The prompt

`RsToneGeneratePrompt`, **generated from `RsGearCatalog`, never hand-written** —
a hand-written list is a second copy of the game's data that rots silently and
teaches the model gear the validator will then reject.

Amps, pedals and racks are listed with their exact keys and every knob's unit and
range. **Cabinets are listed as MODEL stems plus the nine mic/position suffixes**
(~50 + 9 lines instead of 450 keys ≈ 12 KB), and the composed key is validated
afterwards, so an impossible combination is caught rather than trusted. The
guitar block excludes `Bass_*` gear and the bass block offers it; the split is on
amps and cabinets only, because the game offers bass-voiced pedals beside the
rest on a bass board.

The reply is a strict JSON object (`RsToneGeneratePrompt.Schema`); the caller
assigns the tone KEY (uniqueness is a project fact the model cannot know).

**The tone's NAME is a SOFT hint (#614, owner 2026-10-10).** Charters often name tones
"Clean", "Distortion", "Lead", "Crunch", "Acoustic", "Clean_Chorus", "Dist2" — and many do
not. `RsToneNameHint.Of(name, songTitle, arrangementName)` splits the name into words (separators,
camel case, letter/digit) and drops every generic one (tone, base, default, preset, patch, slot,
sound, new, untitled, rig, setting, a number, one character) and every word of the song title or
the arrangement name; a name with a word left is descriptive. Only then does the prompt carry
`RsToneGeneratePrompt.NameHintLine` — "the charter named this tone …; a SOFT hint: the measurements
and the charter's instruction below win where they disagree" — after the measurements and before
the context, so the instruction section FOLLOWS it. A generic name ("Tone B") adds nothing (it is
still the reply's display name). This applies to the menu verb too. Pinned by
`Ai/Generate/RsToneNameHintTests`.

**The AI chat's instruction** (`ToneGenerateRequest.Instruction` → `RsToneMapRequest.Instruction`)
is its own section after the context (`RsToneGeneratePrompt.InstructionSection`, headed
`InstructionHeader`), with the conversation excerpt under the chart pass's own header. No
instruction ⇒ no section: the menu verb's prompt for a generic name is byte-for-byte what it was
(`Ai/Chat/AiChatTonesTests.The_menu_verbs_tone_prompt_for_a_generic_name_is_unchanged`).

## Where a generated set lands

`RsToneProjectWriter`:

- one `ToneDefinition` per tone in `ChartProject.ToneDefinitions`, its body
  serialized by **`RsToneJsonWriter`** — the app's one `RsTone` → manifest-JSON
  writer, which the package export embeds verbatim;
- each placed `ToneChange.ToneName` pointed at its tone (matched by the window's
  INDEX into the time-sorted change list, not by its old name);
- the arrangement's `ToneBase` / `ToneA`–`ToneD` / `ToneNames` filled.

**What it deliberately does NOT touch: the `.rs2dlc`'s `Tones` arrays.**
`Rs2DlcProjectBuilder` hard-codes them empty because DLC Builder's project file is
somebody else's and a save that filled them blanked hand-assigned tones
(`CompanionOwnership`, owner report 2026-08-27). Setting `Arrangement.ToneBase` is
the ONE tone field that builder already forwards, so a generated set reaches the
`.rs2dlc` **without the format or the ownership rule changing** — verified on a
real run: `"BaseTone": "Verse"`, `"Tones": []`.

The whole write is ONE undo node, through
`IChartMaintenanceService.RunAsSingleUndoStep` (which snapshots around it via
`ChartProjectCloner`).

## Provenance

`ToneGenerationProvenance` on every generated tone: the `ToneWindow` (including
`Describe()`, which spells the measure arithmetic out), the stem, which of
`ToneGenerationSources` contributed (`audio` / `context` / both), the model id,
the model's own one-line rationale, the measured features, and a **W5b
confidence** — the tone converted back through `RsToneConverter.ConvertFilled`,
rendered over the reference DI and scored by `MatchScale`, or `unrated` with its
reason. That score inherits the RS→DC conversion's approximation and says so: it
is "how close is the tone we can PLAY", which is what the player hears anyway.

## Measured

Real run on a COPY of the owner's Sakasma project, three placed changes, provider
**ClaudeCli / claude-sonnet-5**, guitar stem, the real null-device renderer:

| Tone | Window | Chain | Confidence | Tilt / crest / decay |
|---|---|---|---:|---|
| Verse | 38,909–73,909 ms | `Pedal_Chorus → Amp_TW22 → Cab_TW112C_57_Cone` | 13 % | −7.9 dB/oct, 16.7 dB, 0.75 s |
| Chorus | 73,909–118,909 ms | `Amp_CS90 → Cab_CS212C_Condenser_Cone → Pedal_VintageChorus` | 24 % | −8.1 dB/oct, 17.7 dB, none |
| Solo | 118,909–183,000 ms | `Pedal_RangeBooster → Amp_TW26 → Cab_TW410C_57_Cone → Rack_StudioVerb → Pedal_AnalogueDelay` | 26 % | −7.4 dB/oct, 18.0 dB, 4.86 s |

3/3 generated, 149.7 s, every tone catalogue-valid, one measure of lead = 1,091 ms
at this tempo. **The confidences are low and that is the honest reading** — W5b's
scale says these are barely better than an information-free amp and cab. Making
the mapping BETTER is the next item, not this one.

## The AI chat's Tones task (#614)

The chat pane's fourth Task, "Tones" (`AiChartJob.Tones`), is this generator driven from the chat:
the chat message is the free-text context #316 asked for. `AiChartPassRun.RunTonesAsync` (under the
chart pass's busy guard, provider resolution, rail pill and cancel) over `Ai/Generate/AiTonePass`:

- **Scope** (`AiChatScope.Tones`): the same three picker entries read as TONES — the timeline's
  selected tone changes (`ISelectionService.SelectedTones`), the change in force at the playhead
  (the last at or before it), or every placed change. One covering none is refused before any run.
- **One proposal per tone DEFINITION, not per placed change** (`AiTonePass.Plan`): changes are
  grouped by `ToneName`; the window is the LONGEST of that tone's in-scope windows (planned over ALL
  the arrangement's changes, so the clamps are real). An unnamed change is refused (`unnamed-tone`).
- **Stems: cache only, never a separation.** The composition root hands the generator
  `StemIsolator.CacheOnly()` (`SeparateOnMiss = false`, same store). A cache miss throws
  `StemsNotCachedException`, which the generator — and only for that type — turns into "no
  measurement": the window is mapped from the context + instruction alone, provenance stem `none`,
  unrated `no-stem`, and the card says "From your words only". The cached stems are the same archive
  the menu verb reads (Opus inside, expanded to WAV — this lane already accepted them; the
  playback-only rule is about LIVE detection).
- **The chat's model**: a `LanguageModelRsToneMapper` over the model the pane picked, metered so the
  tokens reach the chat's cost line.
- **The gate is the generator's**, then once more: each tone is re-keyed to its definition
  (`tone with { Key, Name }`) and validated again (`RsToneCatalogValidation.FirstFault`); a key the
  game cannot load refuses it. Rejected, never repaired.
- **Held** (`AiChartReview.HoldTones` → `Tones`, `AiToneReviewItem`: key, name, window, proposed
  tone + `RsToneJsonWriter` body, the current body and parsed tone, provenance). Nothing is written.
- **Apply** (`AiToneApplier.Build`): the ACCEPTED tones as ONE `CompositeAction` of the Tone designer's
  `UpsertToneDefinitionAction` — the definition object is replaced in place, so undo puts the very
  objects back. A body edited since the hold (or a definition added/removed) is stale and left out.
  **Never** a tone change, a slot, `ToneBase` or a note: `RsToneProjectWriter` is not called.
- **The review card** (`AiChartReviewViewModel`, `AiToneRowViewModel`), in the dense row (#613): per
  tone its name, its song window, the proposed chain in short (`AiToneText.Summary`), Audition, ✓ / ✕,
  the row click = Go to (the placed change); the row's tooltip carries the whole proposed chain beside
  the current one (`AiToneText.Chain`: game display names, a cabinet's mic, every knob) and where the
  evidence came from. **Audition**: `ChartToneAudition` puts the
  proposed body on the rig through the chart tone preview's unsaved-body seam
  (`ChartTonePreviewController.PreviewToneEdit`, the Tone designer's #139 path), switches the preview
  on for it (and off after) and plays the window once (`TransportViewModel.AuditionSpan` →
  `EditorLoopService.BeginAudition`). Heard through what drives the rig: the live guitar and the chart
  synth (#138) — the song audio never goes through it. A tone with no body in the project yet cannot
  be previewed and its Audition is disabled with the reason; closing the review ends the audition.

Tests: `Ai/Chat/AiChatTonesTests` (fake model + stems: instruction in the prompt, scope, one target
per definition, gear and knob refusals, held + deep compare, one undo node, exact undo, stale, no
cached stems), `UI/ViewModels/Shell/AiChartReviewTonesTests`, `UI/ViewModels/Shell/ChartToneAuditionTests`
(the real preview on a fake DSP chain), `Presentation/Editor/AiChatViewModelTests.Send_OnTheTonesTask_…`,
and the headless `Perf/AiChartReviewTonesUiPerfSmokeTests` (en-US, de-DE, ja-JP).

## The Tone designer's AI creator

A third door, ONE tone at a time: `ToneCreator` (`IToneCreator`, this folder) over the same
`IRsToneMapper`, registered by `DialogsModule` as `ToneCreator.CacheOnly` (cached stems only, never a
separation). The UI half is [../ui/tone-designer.md](../ui/tone-designer.md) § Creating a tone with AI.

- The charter's description (and a Refine's change) is the INSTRUCTION section; the song's metadata is the
  context (`AiToneGenerationRun.ContextFor`); a span at the playhead is measured only when asked.
- **Refine** carries the proposal being refined as manifest JSON in `RsToneMapRequest.PreviousProposal`,
  printed by `RsToneGeneratePrompt.PreviousSection` under `PreviousHeader`, BEFORE the instruction. No
  previous proposal ⇒ no section, so the menu verb's and the chat's prompts are unchanged.
- **The instrument check** (`ToneCreator.InstrumentFault`, code `wrong-instrument`) refuses a guitar tone
  with any `Bass_` gear and a bass tone with a guitar amp or cabinet — the catalogue gate validates gear
  EXISTS, not which instrument it belongs to. Rejected by name, never repaired.
- It writes nothing itself; `Editing/Tones/ToneCreatorAccept` plans the ONE undo step the dialog runs.

## The audio check (#12)

**Check with audio** in the Tone designer's AI creator runs `RsToneFeedbackLoop` (`IRsToneFeedbackLoop`, this
folder; registered in `DialogsModule` as `RsToneFeedbackLoop.CacheOnly`): Rocksmith tones judged by how they SOUND.

- **Propose**: the model (`IRsToneMapper`, unchanged) proposes `RsToneLoopOptions.Proposals` (3) tones; each ask
  carries "Proposal i of N: a genuinely different rig" after the charter's words. A refused proposal (catalogue
  fault, gear of the other instrument, `ToneCreator.InstrumentFault`) is a candidate with an `Error`, never repaired.
- **Refine**: per proposal a CMA-ES knob search (`Ai/Match/Search/CmaEs`, `KnobEvaluations` 40 renders, seeded)
  through the RS→DC clone (`IRsToneCloneRenderer` → `RigCloneRsToneRenderer`: `RsToneConverter.ConvertFilled` on the
  match lane's `IRigRenderer`). `RsToneKnobSpace`: the gear and the knob SET stay fixed, enum knobs stay, every
  continuous knob is an axis over the catalogue's own range snapped to its step, so nothing is clamped into range,
  and every decode passes `RsToneCatalogValidation.Require`. The search keeps the proposal when it finds nothing better.
- **Judge**: the clone's best `Finalists` (2) per round go through the game (`IGameToneRenderer`,
  [tonelab-bridge.md](tonelab-bridge.md) § Offline renders), the DI resampled to the game's rate. Every score is
  `MatchScale` over the match lane's metric against the measured stem (`MatchScaleToneLoopScorer`); the 0 % anchor
  is the reference rig through the CLONE for both judges, so the two scores share one scale.
- **Revise**: `RevisionRounds` (2) more rounds, one ask each: the best tone so far as `PreviousProposal` and, after
  the charter's words, `RsToneFeedbackLoop.Feedback` under `FeedbackHeader`: who judged it (the game, or the clone
  and that the game could not), its score, and `RsToneLoopFeedback.Differences` — tilt ("too bright/dark"), crest
  ("too compressed or distorted / not driven enough"), modulation ("missing / too much modulation"), decay
  ("too dry / too wet"), each with both numbers.
- **Pick**: a game score beats any clone score; within a judge the smaller distance wins. `RsToneLoopResult` keeps
  every round (`RsToneLoopRound`: the feedback sent, every candidate with its proposed and refined tone, the clone
  score before and after the search, the game score or `GameRefusal`), the judge and `CloneOnlyReason`.
- **Never looks live.** Without a ready session, with a live preview routing audio, or a game on its own device,
  the round is clone-only and says why; the game is asked per round, so it can join later rounds. The creator
  stops its own Audition before a check. The UI prints "Judged by Rocksmith itself: N %" or "Judged by DotCharter's
  clone only, not by the game (<reason>): N %" and one line per round.
- **Inputs**: the creator's span at the playhead (cached stems only: `StemsNotCachedException` → "separate them
  first"), its measured features, the charter's words and the proposal on screen; the DI is `ReferenceDi.Generate()`.

Not measured yet: the before/after table on the owner's projects (confidence BY THE GAME against the one-shot
generator) needs a real game run. Tests: `Ai/Generate/RsToneFeedbackLoopTests`, `RsToneKnobSpaceTests`,
`RsToneLoopFeedbackTests`, `Presentation/Dialogs/ToneCreatorCheckViewModelTests`, and the headless
`Perf/DialogConstructionUiPerfSmokeTests.ToneCreator_LaysOutTheDescription_TheGreyedRigScope_TheAudioCheck_AndTheProposalRows`.

## What is still wrong

- **The confidence is low on real material** (13–26 %). The mapper is a first
  cut: eleven numbers and a catalogue, with no few-shot examples and no
  measured feedback loop. The name hint and the chat's words (#614) add evidence; they
  were not re-measured on real material, so the table above still stands (#316 open; its free-text box shipped, the mapping is the open half).
- **The stem is chosen from the bass PART decision / name**
  (`ToneRigViewModel.StemFor` over `ArrangementBassTones.IsBassPart`: the Bass
  path, a guitar path at A220, or one whose tones are all bass tones — #161). A
  rhythm-guitar arrangement takes the guitar stem like a lead one, because
  htdemucs has one guitar source.
- **`Rs2DlcProjectDto.Tones` stays empty**, so a charter opening the `.rs2dlc` in
  DLC Builder gets the base-tone NAME but not the tone BODIES; the bodies travel
  in the `.psarc`/manifest path. Filling that array means writing DLC Builder's
  own F# tone DTO shape, which is a format change this item was told not to make.
- **The real tool's import was not verified.** DLC Builder 2014 is installed here
  and was launched on the generated file without crashing, but it is a GUI with
  no CLI and no loadable assemblies (single-file publish), so "it imported it" is
  not something this run can claim.

## Keeping this current

The measured table above is from ONE run of one song with one provider.
Re-measure rather than carrying it forward; a different provider, a different
song or a changed prompt moves every number in it.
