# Running the AI tone-design eval

This is the thing that tells you how good "Design a tone" actually is. It asks
the real design service for a tone per song, compares what came back against the
song's **real Rocksmith tone** as DotCharter itself converts it, and writes a
report you can read and diff.

**Latest, 2026-09-12: Claude Opus 5 scores a mean of 76.1 on the 35-song local
corpus** (p50 74.4, p10 67.4; family 0.81 / module 0.51 / knob 0.89 / order
0.87), against the floor of that day (69.8; 67.3 since the 2026-10 conversion
gained the cab mic layer — rerun before comparing) and a reachable 100.0. Measured through the
**transcript provider** (see below), because this box has no API key and no
local server.

**The first baseline the same day was 65.5 — BELOW the floor** — and the whole
10.6-point difference is the SYSTEM PROMPT. It was never a format problem (zero
validator warnings in every run): the model was answering in a different
vocabulary from the one the converter's ground truth is written in, and did not
know that a bass rig here ends in a cab or that an `Acoustic` tone is
`acousticsim` into an amp rather than an acoustic DI. The round-by-round log and
the blind protocol that produced it are in
[`docs/agents/testing/ai-eval.md`](../../docs/agents/testing/ai-eval.md).
**Re-measure before quoting any of it.**

## The one-liner

```powershell
$env:DOTCHARTER_AI_EVAL     = "1"
$env:DOTCHARTER_AI_PROVIDER = "claude"           # or "openai"
$env:DOTCHARTER_AI_MODEL    = "claude-opus-5"    # whatever you want to grade
$env:ANTHROPIC_API_KEY      = "sk-ant-…"         # or DOTCHARTER_AI_KEY
$env:DOTCHARTER_AI_EVAL_MAX = "20"               # songs; default 20

dotnet build DotCharter.sln --nologo -v:m
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build `
  --filter-class "*ToneDesignLiveEvalTests*"
```

It prints the estimated cost **before the first call**, then the aggregate and
the path to the report:

```
[ai-eval] Claude / claude-opus-5: 20 songs, 20 requests, estimated $0.65 …
[ai-eval] mean 74.3 of a reachable 100.0 · median 76.1 · p10 51.0 · 0 failed · 121,004 in / 17,880 out · billed $0.60
[ai-eval] report: …\artifacts\ai-eval\20260912-141233\report.md
```

## Compare many models through OpenRouter

One command, one table. `tools/ai-eval/Compare-Models.ps1` runs the eval above
once per model — sequentially, at LOW process priority — and writes a ranked
`compare.md` + `compare.json`. Nothing to edit between models.

```powershell
# Price the sweep first. No key, no network cost, nothing spent.
pwsh -File tools/ai-eval/Compare-Models.ps1 -DryRun -Max 35

# …then run it.
$env:OPENROUTER_API_KEY = 'sk-or-…'
dotnet build DotCharter.sln --nologo -v:m
pwsh -File tools/ai-eval/Compare-Models.ps1 -Max 35
```

`-DryRun` against the live catalogue on **2026-09-12**, the default 13-model
shortlist, 35 songs each:

```
[compare] verifying 13 model id(s) against https://openrouter.ai/api/v1/models
[compare] catalogue: 445 models
  ok      anthropic/claude-sonnet-5
  …
[compare] 13 model(s) x 35 request(s) each
  anthropic/claude-sonnet-5             $0.73   $2.00/$10.00 per MTok
  anthropic/claude-opus-5               $1.84   $5.00/$25.00 per MTok
  anthropic/claude-haiku-4.5            $0.37   $1.00/$5.00 per MTok
  openai/gpt-5.6-terra                  $0.80   $2.00/$12.00 per MTok
  openai/gpt-5.6-luna                   $0.08   $0.20/$1.20 per MTok
  google/gemini-3.8-flash               $0.28   $0.75/$3.75 per MTok
  x-ai/grok-4.6                         $0.61   $2.00/$6.00 per MTok
  x-ai/grok-4.3                         $0.34   $1.25/$2.50 per MTok
  deepseek/deepseek-v4-flash            $0.02   $0.07/$0.13 per MTok
  moonshotai/kimi-k3                    $0.97   $2.65/$13.28 per MTok
  qwen/qwen3.8-flash                    $0.05   $0.15/$0.47 per MTok
  z-ai/glm-5.3-flash                    $0.05   $0.15/$0.50 per MTok
  mistralai/mistral-large-2512          $0.15   $0.50/$1.50 per MTok
                                        $6.28   TOTAL (at ~6,000 in / 900 out per request)
```

**The whole sweep is ~$6, not ~$1 per model** — the spread is 90x, from $0.02
(DeepSeek V4 Flash) to $1.84 (Opus 5). Re-price it; these are list prices read
off the endpoint on the day.

| Parameter | Default | What it does |
|---|---|---|
| `-Key` | `$env:OPENROUTER_API_KEY` | Scrubbed out of every captured line before anything is written to disk. |
| `-Models` | the 13 above | Comma-separated (`-Models x-ai/grok-4.6,qwen/qwen3.8-flash`) — the script splits, because `pwsh -File` does not. |
| `-Max` | all | Songs per model. |
| `-Short` / `-Anchor` | off | The two eval switches. `-Short` doubles cost; `-Anchor` makes every number an upper bound. |
| `-BaseUrl` | `https://openrouter.ai/api` | Any OpenAI-compatible host — the app appends `/v1/chat/completions`. xAI is `https://api.x.ai`, LM Studio `http://localhost:1234`. |
| `-OutDir` | `artifacts/ai-eval/compare-<timestamp>` | `compare.md`, `compare.json`, one log per model. |
| `-TimeoutMinutes` | 90 | Per model. A run past it is killed and recorded as a failure. |
| `-DryRun` | off | Verify + price + exit. **Needs no key.** |

**Every id is verified against `https://openrouter.ai/api/v1/models` first** (no
key needed). A gone id is DROPPED with the same-vendor ids that do exist printed
beside it; a MOVED id is renamed only from an explicit table in
`CompareModels.psm1`. There is deliberately no fuzzy fallback:
`mistralai/mistral-large-3` stripped to `mistralai/mistral-large` lands on the
2411 model at **$2.00/$6.00** instead of Large 3 at **$0.50/$1.50**, and a
comparison harness that silently prices the wrong model is worse than one that
refuses.

### Reading the table

Rows are ranked by **p10, then mean** — the tail is what a user remembers, and
the whole table sits above the reminder that **67.3 is the floor**, not a pass.
Per row: p10 / mean / p50, the mean ceiling, the four sub-scores
(family / module / knob / order), `fail` (requests that threw), `warn`
(validator warnings across all requests — a model whose chains are constantly
clamped is guessing), billed tokens, `$` and wall time. The `$` is billed tokens
at that model's **real OpenRouter list price**: the script passes it into the run
through `DOTCHARTER_AI_EVAL_PRICE_IN` / `_OUT`, so each model's own `report.md`
prices itself correctly instead of at Opus list.

**A model whose run fails gets a ROW, never a crash of the sweep** — auth, a 400
on the JSON schema, a timeout, all of it — carrying the one line from its log
that says why. The full log is beside `compare.md`.

**The strict-JSON caveat: OpenRouter routes per backend.** `strict` is passed
through, but enforcement belongs to whichever provider serves the request, and
without `provider.require_parameters` a request can land on one that treats the
schema as a hint. So a model can score badly here for its ROUTING rather than
for its tone design — read a row of schema failures as "check the log", not as
"this model cannot design a tone". Direct endpoints (xAI `https://api.x.ai` is
the one that accepts this app's schema unchanged) are the way to confirm.

## Every variable

| Variable | Required | What it does |
|---|---|---|
| `DOTCHARTER_AI_EVAL` | **yes** | Must be exactly `1`. Without it the test skips and nothing is spent. |
| `DOTCHARTER_AI_PROVIDER` | **yes** | `claude` (or `anthropic`) / `openai` (or `openaicompatible`) / `transcript` (see below). |
| `DOTCHARTER_AI_TRANSCRIPT_DIR` | for `transcript` | Directory the prompts are written to and the replies read from. |
| `DOTCHARTER_AI_MODEL` | **yes** | The model id, verbatim. |
| `DOTCHARTER_AI_BASE_URL` | for `openai` | Base URL of the OpenAI-shaped server (LM Studio, Ollama, a gateway). Ignored for `claude`. |
| `DOTCHARTER_AI_KEY` | for `claude`¹ | The API key. Checked first. |
| `ANTHROPIC_API_KEY` | for `claude`¹ | Fallback key — the name the SDK already uses. |
| `DOTCHARTER_AI_EVAL_MAX` | no | Song cap. Default `20`. |
| `DOTCHARTER_AI_EVAL_SHORT` | no | `1` also sends the sparse prompt per song, **doubling calls and cost**. |
| `DOTCHARTER_AI_EVAL_CORPUS` | no | Point the corpus loader at a psarc/rs2dlc tree other than `TestData/` — e.g. your real CDLC folder. |
| `DOTCHARTER_AI_EVAL_ANCHOR` | no | `1` runs the **anchored** upper bound instead of the default generalisation run. See below — the number it produces is **not** a score. |
| `DOTCHARTER_AI_EVAL_PRICE_IN` / `_OUT` | no | This model's list price, USD per **million** tokens. Unset keeps the Opus $5/$25 default, which is wrong by two orders of magnitude for a cheap model. **Both or neither** — half a pair is ignored. `Compare-Models.ps1` fills them from the models endpoint. |

¹ one of the two, and **only for `claude`**. An OpenAI-compatible server usually
wants no key at all (LM Studio, Ollama), so the eval does not demand one — the
same split `ToneDesignModelFactory.Create` makes. That is the **free** way to run
this: point `DOTCHARTER_AI_BASE_URL` at your local server and spend nothing.

**Anything missing is a SKIP, never a failure**, and the skip message names every
variable it wanted. That is deliberate: "I forgot to export the key" is not a
regression, and a research harness is opt-in or it is part of the gate.

## Anchored vs unanchored — read this before quoting a number

The scorer grades the designed chain against the song's own converted Rocksmith
tone. Grounding stage 2 — the feature that hands the model the tone of the song
from your library — would hand it **that same chain**. So:

- **Default (nothing to set): unanchored.** The eval pins `UseLibraryTones =
  false` into the settings the real service reads, so the model never sees the
  ground truth. **This is the number to quote**, and the report header says
  `Mode: unanchored`.
- **`DOTCHARTER_AI_EVAL_ANCHOR=1`: anchored.** The model is given each song's own
  truth as its anchor. The score goes up, and what it measures is how well the
  pipeline can COPY a reference it was given — an **upper bound, not a
  generalisation score**. The report header says exactly that. It is worth
  running occasionally: the gap between the anchored and unanchored means tells
  you how much of the shortfall is the model's and how much is the converter's
  and the scorer's.

Never compare an anchored run against an unanchored one and call the difference
progress.

## Transcript provider — no key, no server, any model

`DOTCHARTER_AI_PROVIDER=transcript` swaps the HTTP call for a pair of files. The
real design service writes each prompt to `<dir>/<n>.prompt.json` and reads the
reply from `<dir>/<n>.reply.json`; **everything else in the path is the shipping
code**, so the score is comparable with a wire run. Use it to grade a model you
can only reach in a chat window, or to re-grade an old transcript after a scorer
change without spending anything.

```powershell
$env:DOTCHARTER_AI_EVAL           = "1"
$env:DOTCHARTER_AI_PROVIDER       = "transcript"
$env:DOTCHARTER_AI_MODEL          = "claude-opus-5"     # whatever you are grading
$env:DOTCHARTER_AI_TRANSCRIPT_DIR = "D:\scratch\ai-eval-transcript"
$env:DOTCHARTER_AI_EVAL_MAX       = "35"

# Pass 1 — the DUMP pass. Every reply is missing, so every prompt is written and
# the test SKIPS with the directory in the message. This is expected.
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build `
  --filter-class "*ToneDesignLiveEvalTests*"

# …now answer every <n>.prompt.json into <n>.reply.json, then:

# Pass 2 — the SCORED pass. Same command; the report lands in artifacts/ai-eval/.
```

- **No API key is required** and none is read.
- A reply may be bare JSON or a fenced ```json block — the same extraction the
  wire providers use runs on it.
- `<n>` is the **corpus position**, so it is stable between runs, between caps and
  between anchored and unanchored. An anchored run needs its OWN directory (the
  anchor changes the prompt) but keeps the same numbering.
- The token and `$` columns are **estimates** — nothing was billed. Read them as
  a size.

## What it costs

At Opus 5 list price (**$5 / MTok in, $25 / MTok out** — the constants live in
`ToneEvalOptions` and the report prints the price it used), one request is
assumed to be ~6,000 in / ~900 out ≈ **$0.05**. So:

- 20 songs (the default) ≈ **$0.65**
- 35 songs (the whole local corpus today) ≈ **$1.15**
- add `DOTCHARTER_AI_EVAL_SHORT=1` and double both

The estimate is printed before the first call; the report prints what was
actually **billed**, from the provider's own usage numbers.

## What it grades against

The ground truth is **not** the Rocksmith tone. It is the `DcRigPreset` that
DotCharter's own `RsToneConverter` builds from that tone — i.e. the chain the app
would actually play if you loaded the song. That is the only fair comparison: the
same 100-module catalogue, the same param ids, the same ranges.

The corpus is one tone per song, taken from your local `TestData/` (or
`DOTCHARTER_AI_EVAL_CORPUS`): `.rs2dlc` toolkit projects first, then `.psarc`
packages. Where a song has several tones the **richest converted chain** wins —
a "Bass" tone is usually amp + cab and grades almost nothing.

## Reading the numbers

The total is 0..100, mixed from four components:

| Component | Weight | What it measures |
|---|---:|---|
| **Family** | 0.40 | F1 over the tonal-family sets: drive / amp / cab / dynamics / eq / filter / modulation / delay / reverb / pitch. Reaching for the wrong *kind* of pedal is the only error that is always audible. |
| **Module** | 0.25 | Jaccard over the exact type ids. Below family on purpose: `rat`, `ds1` and `distortion` are three plausible answers to one request. |
| **Knob** | 0.25 | Mean `1 − |Δ| / range` over every catalogue param of every module both chains carry. Gains and levels included. |
| **Order** | 0.10 | Kendall-tau agreement of family order. Real but small — drive-into-amp matters, delay-before-reverb mostly does not. |

### The ceiling

Every row also carries a **ceiling**: the score a *perfect* model could have
earned. A model can only answer in a `ToneDraft`, and `ToneDraftValidator` then
drops what this build cannot apply — unknown modules, anything past 12 modules,
string params and branch tags. The ground truth is under no such limit.

So read every score **against the ceiling, not against 100**. A 78 against a
reachable 94 is a decent answer with room left; a 78 against a ceiling of 78 means
the model was perfect and the *pipeline* costs the other 22 — a different bug, in
a different file.

**Measured 2026-09-12: the ceiling is 100.0 on all 35 local corpus songs and on
all 5 fixtures** — today's design pipeline can express every truth in the corpus,
so nothing is capping the scores. That is pinned by `ToneEvalCeilingTests`, and if
it ever drops the failure says which song and by how much.

**Three anchors you can trust, all pinned by tests in the default gate:**

| Answer | Score |
|---|---:|
| the truth itself | **100.0** |
| an empty chain | **0.0** |
| a fixed `overdrive → amp → cab` at catalogue defaults, for every song | **67.3** (69.8 before the truth gained the cab mic layer, 2026-10) |

**That third row is the one that matters.** ~70 is what an answer that knows
*nothing about the song* earns, because most guitar tones contain those three
families and a catalogue default sits in the middle of every range. **Treat 70 as
the floor a model has to beat, not as a passing grade.** A model at 72 has told
you almost nothing; the interesting range is 80+, and the **p10** column matters
more than the mean — it is the tail a user remembers.

**What this does NOT measure:** how the tone *sounds*. Nothing is rendered and
nothing is listened to. Two chains can score 60 and be nearly indistinguishable,
or score 90 and not. It is a regression signal and a model-vs-model comparison.

## The artifact

Each run writes `artifacts/ai-eval/<yyyyMMdd-HHmmss>/` (git-ignored):

- `results.json` — every row, machine-readable, round-trips into `ToneEvalRun`.
  It is also what `Compare-Models.ps1` reads, which is why the run prints
  `[ai-eval] artifacts: <dir>` on a line of its own (`ToneEvalConsoleLines` —
  one prefix, named by the producer and the parser alike).
- `report.md` — the summary table (with a `Ceil` column per row), then per song
  the truth chain and the designed chain side by side, the score components, the
  model's confidence, the design service's warnings and the wall time.

Diff two `report.md`s to compare models. The header carries the weights, so a
report written before a weight change is still readable afterwards.

## Adding a song

Drop the CDLC into your `TestData/` tree (or whatever `DOTCHARTER_AI_EVAL_CORPUS`
points at) — a `.rs2dlc` toolkit project or a `.psarc` both work, and the loader
finds it on the next run. Nothing to register. Raise
`DOTCHARTER_AI_EVAL_MAX` if you want more than 20 songs in one run.

To add a song to the **committed** fixture corpus (the five that run offline in
the default gate), see `docs/agents/testing/ai-eval.md`.

## Offline

The same runner, the same report writer and the same scorer run in the default
gate against a deterministic fake — `ToneDesignEvalOfflineTests`,
`ToneDesignScorerTests`, `ToneEvalCorpusTests`. By the time you spend money, the
only untested thing in the path is the model.
