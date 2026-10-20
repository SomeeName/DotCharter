# The AI tone-design eval harness

Deep reference for `DotCharter.Tests/AGENTS.md`. Everything under
`DotCharter.Tests/Ai/Eval/`: how "AI tone design" is graded, what the weights are
and why, where the ground truth comes from, and the env gate that keeps the live
run off the default gate. The owner-facing "how do I run it" page is
[`tools/ai-eval/README.md`](../../../tools/ai-eval/README.md) — this file is the
one to read before you CHANGE anything here.

Neighbours in this folder: [perf-harnesses.md](perf-harnesses.md),
[ui-sweep.md](ui-sweep.md).

## The files

| File | Purpose |
|---|---|
| `ToneFamily.cs` | `ToneFamily` + `ToneFamilies.Of(typeId)` — the tonal taxonomy the family score works over. |
| `ToneEvalEntry.cs` | One ground-truth row: song, tone, the RS JSON, the converted truth, and the two request shapes. |
| `ToneEvalCorpus.cs` | The five committed fixtures and the local `.rs2dlc` / `.psarc` loader. |
| `ToneScore.cs` | `ToneScore` + `ToneScoreWeights` — the four components and the ONE place the weights live. |
| `ToneDesignScorer.cs` | The scorer. Pure, deterministic, no I/O, no engine. |
| `ToneEvalOptions.cs` | Run options, list prices, the pre-flight cost estimate. |
| `ToneEvalRun.cs` | `ToneEvalRow` / `ToneEvalRun` — the `results.json` shape. |
| `ToneDesignEval.cs` | The runner + the Markdown report writer. |
| `FakeToneDesignService.cs` | The offline `IToneDesignService`: Perfect / Empty / Mediocre / Failing. |
| `ToneEvalEnvironment.cs` | The `DOTCHARTER_AI_EVAL` gate. |
| `ToneEvalCeiling.cs` | The best score a PERFECT model could earn on a row, after the draft schema and the validator. |
| `ToneDesignServiceLocator.cs` | Builds the real `ToneDesignService` from the env vars. |
| `ToneEvalAnchorLibrary.cs` | The corpus as an `IToneAnchorLibrary`, for ANCHORED mode only. |
| `TranscriptToneDesignModel.cs` | The TRANSCRIPT provider, `ToneTranscriptIndex` and its factory — § Transcript provider below. |
| `ToneEvalConsoleLines.cs` | The console lines a MACHINE reads back — today just `[ai-eval] artifacts: <dir>`. § Comparing many models. |
| `*Tests.cs` | 72 cases plus the 5 in `TranscriptToneDesignModelTests`: 32 scorer, 17 corpus, 13 runner, 4 ceiling, 6 live-gate (1 of which skips) — **plus 12 measured 2026-09-12** for the comparison sweep (11 `ToneEvalCompareScriptTests`, which shell out to `pwsh`, and 1 `ToneEvalPriceEnvironmentTests`). **Re-count rather than trusting this.** |

## Ground truth: the converter, not the tone

**The truth is `RsToneConverter`'s output, not the Rocksmith tone.** A designed
tone is a `DcRigPreset` over DotCharter's own 100-module catalogue; an RS tone is
Ubisoft gear with Ubisoft knob units. Comparing them directly would need a second
mapping — and that mapping already exists and is the app's, so the eval uses it.
The consequence worth knowing: **a converter bug moves every score**, and the
corpus loader drops any tone the converter maps to nothing rather than reporting
the converter's gap as the model's.

### Canonicalization — three steps, all load-bearing

`ToneDesignScorer.Canonicalize` runs on BOTH sides before anything is compared:

1. **Drop a trailing, non-bypassed `gain`.** `DcRigLoudnessNormalizer.Normalize`
   appends one to *every* converted tone, so leaving it in hands every truth a
   free `Dynamics` family and marks down any model that did not invent a master
   volume. The predicate here is the normalizer's own (last module, type `gain`,
   not bypassed) so the two cannot drift apart; a MID-chain `gain` is a clean
   boost and survives, which `AMidChainBoostGain_Survives` pins.
2. **Drop bypassed modules.** Ten pedals with nine off is a one-pedal tone.
3. **`DcRigTargetCatalog.FillDefaults`.** An omitted param *is* its catalogue
   default once applied, so filling both sides compares what the engine would
   really run — and closes the hole where emitting one knob per module wins the
   knob score by having nothing else to be wrong about.

## The weights, and why

In `ToneScoreWeights`, one place, summing to 1 (asserted).

| Component | Weight | Definition |
|---|---:|---|
| Family | **0.40** | F1 over the two family SETS. The wrong kind of pedal is the only error that is always audible. |
| Module | **0.25** | Jaccard over the distinct type ids. Below family because `rat` / `ds1` / `distortion` are three plausible answers to one request; demanding the converter's pick would punish a good tone. |
| Knob | **0.25** | Mean `1 − |Δ|/(max−min)` over every catalogue param of every type present in both, ranges from `DcRigTargetCatalog`. Equal to module on purpose: a JCM800 at 10 % gain is not a rhythm tone. |
| Order | **0.10** | Kendall-tau concordance over the families both carry, by first occurrence. Real but small, and RS tones are series-only anyway. |

Defaulting rules, each chosen so the empty chain cannot collect a free component:

- Family/module of two empty sets → 1.0; of one empty set → 0.0.
- Knob with no comparable param → 1.0 if a module type is shared (a cab has no
  knobs — nothing to be wrong about), else **0.0**.
- Order with one common family → 1.0; with none → **0.0**.

### The taxonomy

`ToneFamilies.Of` **derives** from `DspModuleVisuals.Categorize` (DotCharter.UI)
rather than re-typing a table: that map already exists, and
`PedalboardVisualsTests` fails the gate if a catalogue id is missing from it. A
second hand-maintained list here would be a parallel list that rots the day a
module is added — and `EveryCatalogueModuleId_HasAFamily` fails if one ever falls
through to `Utility`.

**One refinement**: `DspModuleCategory.Time` is split into `Delay` and `Reverb`
(`id.Contains("reverb") || id == "shimmer"` → Reverb). The board paints both
teal; an eval must tell a slapback from a hall. Note `reversedelay` deliberately
does not contain `reverb`, and there is an `InlineData` row pinning that.

`Routing` (splitter/merger) and `Utility` are **excluded from the family score** —
structure carries no tonal identity, and scoring an unmapped id would make the
number move when a module is added to the engine rather than when the model got
better. They still count for module and order.

## The corpus

**Fixtures** (`ToneEvalCorpus.Fixtures`, always present, default gate): the three
committed `default_*.json` RS golden vectors this repo already ships, plus two
hand-written tones — a modulated clean (chorus + delay, no drive) and a high-gain
stack (gate + drive, no time effects). Five rows, ≥ 6 distinct scored families
between them, because a fixture corpus that only ever contains amp + cab + drive
cannot catch a scorer bug in the modulation or time arms.
`BuildFixtures` **throws** if any of the five fails to convert: a fixture the
converter cannot map is a broken fixture, not a skip.

**Local** (`ToneEvalCorpus.LoadLocal`, skipped when absent): walks the corpus root
for `*.rs2dlc` then `*.psarc`. `.rs2dlc` first because it is plain JSON carrying
both the metadata and the tone bodies, where a psarc costs a full decode; the EOF
corpus ships one per song, so the psarc leg rarely fires there. **One tone per
song**, elected as the richest converted chain with an ordinal tie-break on tone
name, and never replaced once elected. Rows come back sorted by `SongId` — a run
whose row order moves cannot be diffed.

Root resolution: `DOTCHARTER_AI_EVAL_CORPUS` if set, else the nearest ancestor
`TestData` directory walking up from `AppContext.BaseDirectory` — the same walk
every other corpus test does, and the reason a worktree under
`.claude/worktrees/` still finds the main checkout's junction.

**Measured 2026-09-12** on the owner's box: **35 songs** from
the repo's `TestData/` junction (35 `.rs2dlc` under
`TestData/EOF/`, 125 tones before de-duplication; the 80 psarcs there are `_p`/`_m`
pairs of the same songs and are already covered). **Re-measure; never carry this
number forward.**

### Adding a song to the fixture corpus

Add a row to `BuildFixtures` in `ToneEvalCorpus.cs`, either a new
`default_*.json`-style file (add it to the `<None … CopyToOutputDirectory>` group
in `DotCharter.Tests.csproj`) or an inline const in the exact shape
`RsToneJsonParser` eats. **Gear keys and pot names must be real
`tone_gear_data.json` entries** — an invented key converts to nothing and
`BuildFixtures` throws. Then re-run `ToneDesignEvalOfflineTests`: the pinned
`MediocreMean` will move, and that is the point.

## The ceiling — read every score against it

`ToneEvalCeiling.Of(truth)` renders the truth through
`ToneDesignPromptBuilder.ToDraft` → `ToneDraftJson.Serialize` →
`ToneDraftValidator.Validate` and scores what survives against the truth itself.
That is the **best a perfect model could do**, because a model can only answer in
a `ToneDraft` and the validator drops what this build cannot apply: unknown
modules, anything past `ToneDraftValidator.MaxModules` (12), and — by `ToDraft`'s
own documented rule — string params and branch tags, which a v1 draft cannot
express. The truth is under no such limit.

Without it a score is unreadable. 78 against a reachable 94 is a decent answer;
78 against a ceiling of 78 is a perfect model and a lossy PIPELINE, which is a
different bug in a different file. The runner computes it per row (it is pure and
costs nothing) and the report prints it in the header and in a `Ceil` column.

**Measured 2026-09-12: 100.0 on all 5 fixtures and on all 35 local corpus rows.**
Nothing is capped today. `ToneEvalCeilingTests` pins the fixture number, floors
the local one at 95 and prints the worst row either way;
`TheCeilingIsNotVacuous_AChainPastTheValidatorsCapIsClipped` proves the
measurement really goes through the validator, by handing it thirteen distinct
modules and watching the Pitch family fall off the end.

### A limitation worth knowing

**The module component is a Jaccard over DISTINCT type ids**, so two delays and
one delay are the same to it — thirteen copies of one overdrive truncate to
twelve and still score 100. The knob component pairs instances in order of
occurrence and so sees multiplicity for the shared prefix only. This is a
deliberate trade (a model that adds a second near-identical pedal should not be
punished twice) and it is written down here rather than hidden behind a test that
happens not to hit it. If real tones start turning on duplicate modules, the fix
is a multiset Jaccard — and every pinned number moves with it.

## The three anchors, and the floor

Pinned in `ToneDesignEvalOfflineTests` over the fixture corpus:

| Fake behaviour | Mean |
|---|---:|
| `Perfect` (returns the truth) | **100.0** |
| `Empty` (returns no modules) | **0.0** |
| `Mediocre` (fixed `overdrive → amp → cab` at defaults, for every song) | **67.28** (69.82 until the 2026-10 cab mic layer joined the truth) |

**`MediocreMean` is a FLOOR, not a mediocre score.** ~70 is what an answer
carrying no information about the song earns, because most guitar tones contain
those three families and a catalogue default sits mid-range on every knob. Say so
wherever the number is quoted; a reader who takes 70 for "passably good" will
conclude a useless model is working. When the pin fails, decide WHICH number is
right before editing it.

## Anchor leakage — the two modes

**Grounding stage 2 hands the model the song's own converted Rocksmith tone, and
that is exactly the chain the scorer grades against.** Leaving it on for the
scored song would make the eval measure how well the model COPIES the answer it
was given. The number rises and says nothing about the case the feature exists
for — a song the player's library does not have.

So there are two modes, and the report header names which one ran:

| Mode | How | What it measures |
|---|---|---|
| **unanchored** (default) | nothing to set | Generalisation. `ToneEvalEnvironment.Settings` pins `UseLibraryTones = false` into the settings the REAL service reads, and the live test hands it NO anchor library. **This is the number to quote.** |
| **anchored** | `DOTCHARTER_AI_EVAL_ANCHOR=1` | An UPPER BOUND. The model gets each song's own truth as its anchor, so the score bounds what the pipeline can reach with perfect retrieval — which is how much of the remaining gap is the model's rather than the converter's and the scorer's. **Never a generalisation score**, and the report says so in those words. |

Both halves are required for an anchored run: the env var sets
`UseLibraryTones` AND the live test constructs the library. One alone leaves the
run unanchored rather than half-anchored, which is a number nothing could
interpret — `ToneDesignServiceLocator.Create` enforces it by ignoring an anchor
library it is handed for an unanchored environment.

**The anchor comes from the CORPUS, not from the plugin's library tone anchors.** Going
through the real library would make the anchored number depend on whether the
psarc walk happened to find each scored song, so a retrieval MISS would read as
a model failure. An upper bound has to assume perfect retrieval or it is not a
bound. `ToneEvalAnchorLibrary` still returns the whole corpus and lets the
product's own `ToneAnchorRetriever` rank it, so the anchored run exercises the
real selection rather than a shortcut that always finds the perfect tone.

`ToneEvalAnchorModeTests` pins all of it, including the report header's wording.

## Transcript provider — grading a model with no key and no server

`DOTCHARTER_AI_PROVIDER=transcript` plus `DOTCHARTER_AI_TRANSCRIPT_DIR=<dir>`
replaces the HTTP call with a pair of files: the real `ToneDesignService` writes
each prompt to `<dir>/<n>.prompt.json` and reads its reply from
`<dir>/<n>.reply.json`. **It bypasses the TRANSPORT and nothing else** — the
prompt builder, grounding, the validator, `DcRigPresetPreparation`, the runner
and the scorer are the shipping code, which is what makes a number produced this
way comparable with one produced over the wire. It is how the first live
baseline was measured (2026-09-12, Claude Opus 5, by hand).

- **No key is demanded** and `ToneEvalEnvironment.Provider` still resolves to
  `Claude` — the model is Claude, only the transport moved. `ToOptions().Provider`
  is `"transcript"`, so the report header can never be mistaken for a wire run.
- **Two passes.** The first over an empty directory fails every row for a missing
  reply and therefore writes EVERY prompt (`ToneDesignEval.RunAsync` records a
  per-row failure and carries on) — `ToneDesignLiveEvalTests` turns that into an
  `Assert.Skip` naming the directory rather than a red test. Answer the prompts,
  run again, and the same run is scored.
- **File names come from the CORPUS position, never the call order**
  (`ToneTranscriptIndex`), matched back from the trailing
  `Request:`/`Song:`/`Artist:` lines the prompt builder appends last. Those lines
  are identical in both modes, so an anchored transcript keeps the same numbering
  in its own directory. A prompt that matches nothing gets a content hash, not an
  ordinal — a fallback that counts calls is the failure the class exists to
  avoid. `TranscriptToneDesignModelTests` asks the corpus BACKWARDS and asserts
  the numbering does not move.
- **Usage is ESTIMATED** (four characters to a token over what was sent and
  received) because nothing was billed. Read the `$` column as a size.
- The anchored run needs its OWN directory: the anchor changes the prompt.

First measured baseline, 35 local songs: **unanchored mean 65.5** (p50 66.6,
p10 57.2; family 0.73 / module 0.26 / knob 0.88 / order 0.78) against the 69.8
floor, and **anchored 99.1** — zero validator warnings across all 70 requests.
**That baseline is superseded**: the prompt hill-climb below took the same model,
the same corpus and the same protocol to **76.1 / p10 67.4 / module 0.51** by
changing the SYSTEM PROMPT only. Read the round log, not this paragraph, for the
current number — and re-measure rather than quoting either.
The write-up is OUTSIDE this repo, in the owner's local scratch directory
(DotCharter-local/scratch, file ai-eval-opus5-transcript-2026-09-12.md).
**Below the floor**: the module component collapses because the converter's
idiom (`optocomp`/`multiband`, `graphiceq`, `springreverb`, `bbddelay`) is not
the idiom a model reaches for, and because every RS bass tone converts WITH a
cab and every `Acoustic` tone converts to `acousticsim` into an amp rather than
to the acoustic DI preamp. Re-measure; do not carry that number forward.

## Comparing many models — the OpenRouter sweep

`tools/ai-eval/Compare-Models.ps1` runs the live eval once per model id, low
priority and sequential, and writes a ranked compare.md / compare.json. The
owner-facing page is [`tools/ai-eval/README.md`](../../../tools/ai-eval/README.md);
what matters when you CHANGE it:

- **The pure half is `tools/ai-eval/CompareModels.psm1`** — catalogue parsing,
  id resolution, the estimate, the failure-reason digger, ranking, the Markdown
  — and `ToneEvalCompareScriptTests` executes it **through `pwsh` in the default
  gate**, over canned inputs and over a REAL `results.json` written by
  `FakeToneDesignService`. Re-implementing that logic in C# to test it would
  only test the copy. `pwsh` absent is an `Assert.Skip`.
- **Two lines make the sweep possible and both are pinned.**
  `ToneEvalConsoleLines.ArtifactPrefix` (`[ai-eval] artifacts: `) is how the
  script finds the run it just paid for — the artifact directory carries a
  timestamp only the run knows — and `DOTCHARTER_AI_EVAL_PRICE_IN`/`_OUT` carry
  the model's real list price INTO the run so its own report is priced at that
  model rather than at Opus. Both or neither for the price pair; half a pair is
  ignored, because a report mixing two models' prices is worse than the honest
  default.
- **An id is never renamed by guessing.** `mistralai/mistral-large-3` reduced to
  `mistralai/mistral-large` resolves to the 2411 model at 4x the price of the
  Large 3 it meant. A rename lives in the module's alias table; anything else is
  DROPPED with its near misses printed.
- **A failed model is a ROW.** Auth, a schema 400, a timeout — the sweep carries
  on and the row says why. Anything else loses twelve paid runs to the
  thirteenth.
- The key is scrubbed out of every captured log line before it is written.

## The blind protocol — how a prompt change is measured honestly

A transcript run is only worth something if the answers were written by
something that could not see the score. The 2026-09-12 prompt hill-climb ran
this way and any future one must:

1. **Change the prompt, then DUMP.** A fresh directory per round
   (`ai-eval-transcript-r<N>`), unanchored only — the anchored run is an upper
   bound on COPYING and does not move with a prompt change worth measuring.
   **Copy** an existing baseline transcript rather than re-running in place; a
   dump pass overwrites `<n>.prompt.json` and a mutated baseline cannot be
   re-scored later.
2. **One fresh subagent answers, and its whole brief is the prompts.** Read
   `<i>.prompt.json`, write a schema-valid `<i>.reply.json` from its own
   knowledge of the song; read nothing else on disk, and look at no other
   round's directory. It never sees a score, a truth chain, a report or a
   previous round's replies. **The person running the eval does not write a
   reply**, in any round, for any reason.
3. **Split the corpus.** Rows are sorted by `SongId`; the leading rows are the
   TRAIN split (the only ones whose truth chains may be read per song when
   deciding what to change) and the trailing ones are HELD OUT, looked at only
   as an aggregate per round. **This is a guard against per-song tuning and
   nothing more** — the local corpus is 35 songs of which 22 are two artists, so
   both halves share a genre and a converter idiom. Do not call the held-out
   number generalisation.
4. **Never edit a reply after seeing a score.** A round that needs a better
   answer needs a better PROMPT.

## The round log — prompt hill-climb

Claude Opus 5, transcript provider, unanchored, 35 local songs. Train = rows
1–24, held out = rows 25–35. **Re-measure; do not carry these forward.**

| Round | What changed in the prompt | Mean | p10 | Family | Module | Order | Held-out mean / p10 | Sys tok |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| 0 | (the v0.55 prompt) | 65.5 | 57.2 | 0.73 | 0.26 | 0.78 | 63.5 / 50.1 | 7,474 |
| 1 | `RsConverterIdiom` + the bass-cab and acoustic rules | 73.6 | 68.5 | 0.83 | 0.40 | 0.83 | 72.6 / 68.1 | 8,264 |
| 2 | the `[slot]` tag (pedal vs rack) + rule 3 rewritten around it | 74.1 | 67.5 | 0.81 | 0.42 | 0.86 | 73.7 / 68.8 | 8,623 |
| **3** | **the by-family summary of the whole reachable vocabulary + the bass-gear line** | **76.1** | **67.4** | **0.81** | **0.51** | **0.87** | **75.1 / 69.3** | **8,844** |
| 4 | …plus a per-module catalogue COUNT as a tie-break — **REVERTED** | 75.7 | 66.5 | 0.82 | 0.51 | 0.81 | 74.6 / 67.5 | 8,954 |

**Round 3 is what shipped.** +10.6 mean and +11.6 on the held-out split against
the baseline, 65.5 → 76.1, from under the 69.8 floor to comfortably over it, and
the module component doubled. Zero validator warnings throughout, in every round
— the baseline's answers were never malformed, they were written in a different
vocabulary from the converter's. See
[`ai.md` § the house idiom](../services/ai.md).

**Round 4 is here because it FAILED and that is worth as much as the rest.**
Printing how many real Rocksmith units land on each module, and telling the
model the bigger number is the likelier rig when the request decides nothing,
is a perfectly reasonable idea and it moved the score DOWN on the mean, on p10
and on both held-out columns, with `order` falling 0.87 → 0.81 for no reason the
change predicts. It did not shift the miss it was aimed at either: `graphiceq`
was missed on 12 train rows before it and 13 after. It was reverted rather than
kept for the sake of a fifth round. **A round that does not clear the previous
round is a round you undo**, and a prompt change with no mechanism behind it is
a coin flip whichever way the number lands.

**A finding left deliberately unfixed.** What remains of the module gap is
mostly SAME-FAMILY substitution — over the 24 train rows, **28** such pairs in
round 1 (`compressor` for `optocomp` 6, `compressor` for `multiband` 4,
`platereverb` for `springreverb` 4), **21** in round 2 and **13** in round 3, by
then almost entirely a spring or a hall answered as a plate. Every one is a
pedal that would sound like the truth's pedal. They cost the module component (a
Jaccard over exact ids) and cost the family component nothing, which is exactly
the trade § the weights describes. **It was not loosened, and must not be**: the
module weight sitting below the family weight is already how that judgement is
expressed, and widening it to a within-family credit would make every future
score incomparable with every past one.

**Two more findings from the same wave, neither a scorer change.** The owner's
gear list names a **tuner** and a **buffer** and the catalogue has no module for
either, so no prompt and no tone can ever contain one. And the factory preset
library cannot supply a few-shot per family — nothing in it uses `boost`,
`swell`, `octaver`, `graphiceq`, `acousticsim`, `bbddelay`, `fuzz`, `whammy`,
`univibe` or `rotary`.

## The gate

`ToneEvalEnvironment.TryResolve` reads `DOTCHARTER_AI_EVAL` (must be `1`),
`DOTCHARTER_AI_PROVIDER`, `DOTCHARTER_AI_MODEL`, `DOTCHARTER_AI_BASE_URL`
(required for `openai` only) and a key from `DOTCHARTER_AI_KEY` or
`ANTHROPIC_API_KEY`; `DOTCHARTER_AI_EVAL_MAX` caps songs at 20 by default and
`DOTCHARTER_AI_EVAL_SHORT=1` adds the sparse prompt. **A key is required for
`claude` only** — an OpenAI-compatible server usually wants none, which is
`ToneDesignModelFactory.Create`'s own rule mirrored rather than re-decided, and it
is what makes a free local run possible. **Any gap is an
`Assert.Skip` naming every variable, never a failure.** The full table is in the
tools README.

This is the `GND_IDMT_ROOT` discipline and it exists for the same reason: a
hard-coded fallback once made a ten-minute research harness run on every
`dotnet test` on exactly one machine. Here it would also spend money.

The gate itself is tested **in the default gate**
(`TheGate_IsClosedInAnUnconfiguredEnvironment`,
`TheGate_RefusesEachHalfConfiguration`), so the skip is a mechanism rather than a
comment. Both restore every variable in a `finally` — they are process-wide.

The pre-flight **cost estimate is printed before the first call**
(`ToneEvalOptions.EstimateUsd`, ~6,000 in / ~900 out per call at $5/$25 per
MTok), so a mis-set `MAX` is caught by a human reading the console rather than by
an invoice. The report prints what was actually billed.

## `ToneDesignServiceLocator` — constructing the real service

```csharp
new ToneDesignService(
    () => new AppConfig { Ai = environment.Settings },
    new ToneDesignModelFactory(new EnvironmentSecretStore(environment.ApiKey)));
```

Three things that are deliberate:

- **It does not build the app's DI graph.** `App.ConfigureServices` would
  construct the audio engine, the psarc index and everything else a tone eval has
  no business starting. The service needs exactly a settings source, a model
  factory and a key.
- **The settings come from the ENVIRONMENT, through a throwaway `AppConfig`.** An
  eval run can never read or write the owner's configured provider, and the key
  never reaches `AiSecretStore`'s DPAPI store — the eval's own
  `IAiSecretStore.Write` is a no-op so it cannot clobber a stored key.
  `TheLocator_BuildsTheRealServiceFromTheEnvironment` asserts both the concrete
  type and that `Settings` really is the env's.
- **`TryCreate` checks `IsConfigured` before spending anything.** False is a SKIP
  with the reason, not a failure: settings the shipping service will not act on
  mean the eval has nothing to measure.

**This was reflection until 2026-09-12.** The harness was built in parallel with
the service, against nothing but `ToneDesignContracts.cs`, so it hunted for an
implementation by type scan and skipped when it found none. That is gone, and it
would not have worked anyway: `ToneDesignService` takes a `Func<AppConfig>`, which
no type-directed bag can supply. Naming the type is a compile-time check where the
scan was a runtime guess.

## Rules for changing this

- **Never widen a weight or an anchor to make a number look better.** The pins
  exist so a scoring change is visible; editing the pin instead of the reasoning
  turns the eval into a rubber stamp.
- **A new component gets a non-vacuity test that breaks on purpose.** Every
  existing one does: the truth scores 100, the empty chain 0, a reversed chain
  loses exactly the order weight (90.0), a single module with every knob at the
  far end of its range scores exactly 75.0.
- **Nothing here may touch the network in the default gate.** The offline half
  runs on `FakeToneDesignService`; the live half is behind the env gate.
- **The eval is a regression signal, not a verdict on sound.** Nothing is
  rendered and nothing is listened to. Do not let a doc or a commit message
  start implying otherwise.
