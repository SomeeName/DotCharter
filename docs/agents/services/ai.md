# AI tone design (`DotCharter.Tone/Ai/`, namespace `DotCharter.Tone.Ai`)

The designer moved with the rig into `DotCharter.Tone` (2026-09-26) so the
standalone RigLab can ship it; the namespace did not change. What needs the psarc
library (the plugin's library tone anchors) or ONNX (`Ai/Match/`) stayed in
`DotCharter.Services`.

"Describe a tone in words, get a rig." The model never touches audio — a tone here
is pure data, so this is a **text-to-JSON problem with a validator the app already
owns**. The model proposes; `DcRigTargetCatalog` and `DcRigPresetPreparation`
dispose.

Three halves, built in parallel against one contract:

| Half | Owns |
|---|---|
| **the contract** — `Ai/ToneDesignContracts.cs` | every interface and record below. Landed FIRST so the three could be built at once. Add to it; never rename or remove. |
| **the core** — everything else under `Ai/` | the secret store, the two providers, the prompt, the validator, the service |
| **the UI** — `DotCharter.UI/ViewModels/**`, `Views/**` | the "Design a tone" panel in the tone drawer and the Settings → AI tab |

## The pieces

| File | What it is |
|---|---|
| `ToneDesignContracts.cs` | the contract: `AiProviderSettings`, `ToneDesignRequest`/`Result`, `ToneDraft`, `IToneDesignModel`, `IToneDesignModelFactory`, `IAiSecretStore`, `IToneDesignService`, `ToneDesignFault`/`Exception` |
| `AiSecretStore.cs` | `DpapiAiSecretStore` (+ `InMemoryAiSecretStore` for tests) |
| `AiModelPricing.cs` | the list-price table behind the panel's `~$0.04`; an unknown id is "no price", never a guess |
| `ToneAnchor.cs` | grounding stage 2's records + `IToneAnchorLibrary` |
| `ToneAnchorRetriever.cs` | all the ranking: which song, which tone, what an absent song falls back to |
| the plugin's library tone anchors | the real library, over `ILibraryService` + the package tone importer (`IPackageToneImporter`) |
| `AiGearNotes.cs` | the 98-row gear glossary and `UnitOf`, plus `NamAmpId` / `NamAmpNoteWithoutCaptures` — the one row whose prose depends on the player's machine |
| `NamCaptureCatalog.cs` | the CLOSED list of neural captures a `namamp` may pick from: `NamCaptureOption`, the deterministic ordering, the rendered block and the fingerprint that keys the cached prompt |
| `RsConverterIdiom.cs` | the house idiom — which module stands for which real pedal, **derived by running the converter**, not typed |
| `ToneDraftJson.cs` | the JSON Schema, the serializer options, `ExtractJsonObject` |
| `ToneDesignPromptBuilder.cs` | rules + catalogue + few-shot, and the conversation |
| `ToneDraftValidator.cs` | reply → a `DspTonePreset` this build can apply, plus warnings |
| `ClaudeToneModel.cs` | the Claude provider (`Anthropic` 12.55.0) |
| `ClaudeCliToneModel.cs` | the Claude Code (local CLI) provider - the installed `claude` binary, headless, no API key |
| `ClaudeCliProcess.cs` | `IClaudeCliLocator` / `IClaudeCliRunner` and their real implementations: where the binary is, and how a child of it is run |
| `ClaudeCliSystemPrompt.cs` | `IClaudeCliCapabilityProbe` (does this binary take `--system-prompt-file`?) and the hash-named system-prompt FILE that makes the CLI path cacheable |
| `CodexCliToneModel.cs` | the Codex (local CLI) provider — the installed `codex` binary, `codex exec`, no API key; and `CodexCliLocator` |
| `NamCaptureSource.cs` | `INamCaptureSource` + `Tone3000CaptureSource`: answers a design's `captureSearch` by searching TONE3000, picking one capture and downloading it into the library |
| `OpenAiCompatibleToneModel.cs` | LM Studio / Ollama / any chat-completions server, and `AiProviderPresets` |
| `ToneDesignModelFactory.cs` | settings + secrets → a provider, or a specific `NotConfigured` |
| `ToneDesignService.cs` | the one entry point the UI calls |

Registered in `DotCharter.UI/App.AiServices.cs` (three singletons).
Tests: `DotCharter.Tests/Services/Ai/` — W8/W9 added
`ToneDesignCaptureTests` (23 cases: the capture catalogue, the prompt block, the
validator, the preparation seam and one end-to-end over a fake model) and
`ClaudeCliSystemPromptTests` (17: the probe, the hash-named file and both
layouts). Before them the row read **206 cases across 11 classes,
re-measured 2026-09-12 by the provider-compat wave** (the row read 118; the wave
added `AiProviderCompatTests`), plus the CLI provider's `ClaudeCliToneModelTests` /
`ClaudeCliRunnerTests` and the env-gated `ClaudeCliSmokeTests`, which merged the same
evening and are NOT in that 206. Re-measure by running the `--filter-class` names
rather than carrying this forward.

## The secret store

`ai-secrets.bin` under `WritablePaths.ConfigRoot`: a `{ kind: secret }` map,
serialized, `ProtectedData.Protect`ed (CurrentUser + `"DotCharter.Ai.Secrets.v1"`
entropy), written through `AtomicFile`. The same shape as
`DpapiTone3000TokenStore`, for the same reasons.

- **The key is never in `AppConfig`.** `AppConfig.Ai` carries kind, model id and
  base URL only.
- **The file is on `ConfigBackupRules`' credential list** (and, as defence in
  depth, `PayloadReceiptFilter`'s), so it cannot leave this machine in a backup or
  arrive in an update payload. `AiSecretStoreTests` asserts both filters.
- **A blob this build cannot decrypt reads as "no key" and is LOGGED, never
  thrown.** Wrong user, wrong machine, truncated, tampered — the caller is a
  settings page, and every one of them means the same thing to it.
- **Non-Windows persists nothing** (`IsPersistent` is false) rather than writing a
  plaintext key. The Settings page says so, and since 0.55.1 it asks the STORE:
  `IsPersistent` is on `IAiSecretStore` and is an instance member, because the
  page named `DpapiAiSecretStore` statically and so answered for the real store
  even with a fake injected.
- The log line names the exception TYPE and never its message: a serialization
  failure can quote the value it choked on.

## The prompt

Built once per process into `ToneDesignPromptBuilder.SystemPrompt` and ordered
**most-stable-first**, because that ordering is the only thing that makes it a
cacheable prefix:

1. **Rules** — series chain, 1–12 modules, chain order (**pedals before the amp,
   racks after the cab**), a cab after EVERY amp including a bass head,
   `bassamp` for bass, `acousticsim` in front of a real amp for an acoustic,
   prefer the specific module over the generic one, values in range, JSON only.
2. **The house idiom** — § below. Generated.
3. **Catalogue** — one line per module (`id — Display Name — ten-word note`) and
   its params as `id (min..max, def D unit)`, grouped by category.
4. **The capture list** — § below. **Present only when the player HAS neural
   captures**, and positioned here on purpose: everything above it is
   build-constant and identical on every machine, so a shared prefix survives.
5. **Four few-shot examples** — factory presets rendered as `ToneDraft` JSON:
   `clean-glass`, `crunch-plexi`, `metal-5150-rhythm`, `gear-bass-di` (bassod → bassamp — the legacy `bass-pick-rock` was built on the guitar `amp`, which contradicted rule 6). All four
   are algorithmic (no `namamp` capture path, which a draft cannot carry) and the
   bass one is what stops a model reaching for `amp` on a bass request.

The conversation is separate, in `ToneDesignPrompt.Messages`. A fresh request is
one user turn; a follow-up replays the history and then quotes the CURRENT tone as
ToneDraft JSON with "return the FULL chain".

**Measured 2026-09-12, after the house idiom landed: 35,374 characters, ~8,844
estimated tokens** (four characters to a token; no live `count_tokens` call —
that needs a key). It was 29,930 / ~7,483 before, so the idiom section costs
about 1,360 tokens. The budget is 12,000 and
`ToneDesignPromptTests.TheSystemPrompt_FitsTheBudget` is the guard.
**Re-measure by tripping it rather than carrying this number forward** —
`RsConverterIdiom` is generated, so it grows on its own when the converter or the
gear catalogue does. There is ~3k of headroom, which is why the examples are
still four STATIC ones in the cached prefix rather than retrieved per request:
retrieval would buy family coverage the factory library does not have anyway
(there is no preset using `boost`, `swell`, `octaver`, `graphiceq`,
`acousticsim`, `bbddelay`, `fuzz`, `whammy`, `univibe` or `rotary`) and it would
cost the cacheable prefix, which is the whole reason the system prompt is
assembled apart from the conversation.
**Both waves above measured on their own branches, so the two numbers do not add up in one tree: the strict-subset schema (+854 tokens) and the idiom section (+1,360) landed the same evening, so the merged prompt is roughly 9,700 estimated tokens against the 12,000 budget. Trip `TheSystemPrompt_FitsTheBudget` to get the real figure.**


**Cost per request on `claude-opus-5`** at that size: roughly 8.3k input + ~300
output tokens on a first request, and with the cache marker on the system block,
~8k of that reads from cache at a tenth of the price on every request after it
inside the TTL — **one hour**, see the Claude section. A follow-up adds the
quoted current tone (~450 tokens) to the message side only — the prefix does not
move.

### The capture list: the model may choose a NAM capture, by name

`namamp` used to be off the table — the gear note said "needs a captured model
file, so avoid designing with it" and rule 12 forbade it, because a `ToneDraft`
could not carry a capture path. It can now carry a capture **ID**, and the ids
are a CLOSED list the prompt prints: `NamCaptureCatalog`, built from the local
`INamCaptureLibrary` (which already holds both halves — the 63 bundled `.nam`
files under `DotCharter.UI/NamCaptures/` are installed into that same directory
on first run by `BundledNamCaptureInstaller`, and TONE3000 downloads land beside
them).

- **The id is the capture's FILE NAME without its extension, lowercased.** Not a
  path, and not the title: the model never names a file, it names a row of a list
  it was shown. `ToneDraftValidator` resolves the row to the capture's file NAME
  and `DcRigPresetPreparation.Prepare` turns that into the absolute path the
  native module requires — the same two steps a factory capture preset goes
  through, rather than a second path-resolution rule.
- **Order is bundled-first, then the player's own, each block by id.** It sits in
  the cacheable prefix, so a dictionary order would miss the provider's cache on
  every process restart — the same reason `RsConverterIdiom` pins its ordering.
  `NamCaptureCatalog.Fingerprint` (FNV-1a over the ordered ids) is what keys the
  cached system prompt, so a library that gained a capture gets a NEW prefix and
  an unchanged one keeps hitting the old.
- **A player with NO captures gets no block and the old rule back.** Telling a
  model about a list and then showing it none is how a design comes back naming a
  capture nobody has. That is why rule 12 and the `namamp` gear note are both
  functions of the catalogue being non-empty
  (`ToneDesignPromptBuilder.RulesFor`, `AiGearNotes.NamAmpNoteWithoutCaptures`).
- **The validator REJECTS rather than repairs.** An unknown capture id and a
  `namamp` with NO capture are each a dropped module with a warning naming the id
  or the module, and the two messages are DIFFERENT on purpose — an empty capture
  falls through the lookup to null either way, so one shared message would let the
  empty-capture guard be deleted with nothing going red.
- **Only neural formats are listed.** An `ir` entry in the same library is a
  cabinet impulse, not something `namamp` can load.
- **A fifth few-shot appears with the block** — `nam-metal-5150-rhythm`, a real
  capture-backed factory preset, so the model has seen the shape. When the
  player's library lacks that preset's own capture the example falls back to the
  FIRST listed one: an example naming an id the list beside it does not carry
  teaches that inventing one is fine.
- **The author and the size tier are deliberately NOT printed.** Neither
  describes a sound, and with them the prompt measured **~11,750 estimated tokens
  against a 12,000 budget on the bundled 63 ALONE** — no room for the player's own
  downloads. Without them it is **~11,309** (against ~9,794 with no captures at
  all), at roughly 10 estimated tokens a capture line.
  `ToneDesignCaptureTests.TheCapturePrompt_FitsTheSameBudgetAsTheRest` pins both
  the total and the per-capture RATE, because the total alone does not tell a
  reader how many downloads still fit. **Re-measure by tripping it.**

### Capture search: a design may fetch a capture from TONE3000

The closed list is only what the player already has. While TONE3000 is signed
in, a reply may instead name a SEARCH — `ToneDraft.CaptureSearch`, one field on
the draft (a chain has one amp) — and the APP runs it. The model never downloads
anything: a CLI provider runs in a read-only sandbox and an API provider has no
tools, so every provider gets the feature the same way.

- **Offered only while a search can run.** `ToneDesignService` reads
  `INamCaptureSource.IsAvailable` once per design and passes it to
  `ToneDesignPromptBuilder.Build(…, captureSearch)`. With it on, rule 12 is one of
  two more variants (`RulesFor(hasCaptures, captureSearch)`) that teach the field,
  `namamp` keeps its real gear note even with an empty library, and the flag is in
  the system-prompt cache key (`"s:"`), so the two prompts never share a prefix.
- **The search → pick → download is `Tone3000CaptureSource`**: `GearType=Amp`
  (so rule 4's cab still follows it), the query trimmed to one line of at most 80
  characters, then of the first **5** NAM results in the server's best-match order
  the most DOWNLOADED, ties to the server's order (`PickTone`, pure, pinned). The
  model file is `Tone3000ModelPicker.Pick` — the SAME rule as the capture
  browser's Download button, moved out of `NamCaptureBrowserViewModel` for it.
  A tone already in the library is reused, not downloaded again. A 401 invalidates
  the session exactly as the browser does.
- **It runs BEFORE validation**: the first `namamp` whose `capture` is not on the
  list gets the downloaded capture's id, the library is re-read so the validator
  sees an ordinary listed capture, and the rewritten JSON (capture set,
  `captureSearch` cleared) is the result's `RawJson`, so a refinement turn shows the
  model the id it will find on the list next time. A listed capture wins: no
  search runs, nothing is downloaded.
- **Every failure is a warning, never a lost design** — signed out, no NAM
  result, a failed download: the warning names the search, the validator then
  drops the `namamp` with its own warning, and the rest of the chain comes back.
- `ToneDesignResult.DownloadedCaptures` lists what a design fetched, for the panel
  to credit (licence + attribution are in each manifest, as for any download).
- The prompt with the search on and the whole bundled set is pinned under the
  12,000 budget by `ToneDesignCaptureSearchTests.TheSearchPrompt_FitsTheBudget_WithTheWholeBundledSet`.
  `ToneDesignCaptureSearchTests` covers the prompt gating, the pick, the source
  (signed out, download, reuse, expired session) and the service end to end.
- **"Online" means TONE3000 and nothing else.** The app has an authenticated
  client, a size cap and a licence trail for it; fetching `.nam` files from an
  arbitrary URL a model names would have none of the three.

### The house idiom is DERIVED by running the converter

`RsConverterIdiom` is the section that fixed the first measured score. The
problem it solves is not a format problem — the baseline had **zero** validator
warnings — it is a **vocabulary** one. The scorer's truth, and the chain the app
really plays when a player loads a Rocksmith song, is `RsToneConverter`'s
output, and that converter has a narrow idiom: `optocomp` rather than
`compressor`, `graphiceq` rather than `eq`, `springreverb` / `platereverb` /
`hallreverb` and **never** a plain `reverb`, `bbddelay` rather than `delay`.
Nothing told the model any of it, and the module component sat at 0.26.

**Every row is measured, not written.** `Derive()` hands the real
`RsToneConverter` a synthetic one-pedal tone built from a real `RsGearCatalog`
entry and reads back which module came out. That is the only construction under
which the prompt and the converter cannot drift — there is one mapping, not two
— and it picks up the converter's substring routing and `rs_gear_overrides.json`
alike, which a table keyed on the overrides file alone would miss (only 49 of the
613 catalogue entries are in it). The 450 cabinets are the one collapsed case;
everything else is derived.

**The `[slot]` tag is load-bearing twice.** It is read off the gear KEY and TYPE
(`Bass_` / `DI_` prefixes, `Pedals` vs `Racks`) and it carries both halves of
what the model was getting wrong:

- **identity** — `Compression` is a stompbox and converts to `optocomp`;
  `Studio Compressor` is a rack unit and converts to `compressor`. With no tag
  the model answered the generic one on nearly half the corpus.
- **order** — `RsTone.InSignalOrder` wires pre-pedals → amp → cab → racks, so a
  rack EQ, a rack reverb and a rack compressor land AFTER the cabinet while a
  stompbox lands before the amp. Rule 3 states it and the tags carry it per
  module.

**The second half of the block is the family summary, and its value is what it
LEAVES OUT.** `reverb: hallreverb, platereverb, springreverb` is the sentence
"there is no plain `reverb` in one of these rigs", and it took plain `reverb`
from seven train rows to zero where the general "prefer the specific module"
rule alone had not. The bass line is a catalogue fact of the same kind:
`MB Comp` is the only compressor Rocksmith ships for bass, which is why a
converted bass rig's dynamics module is `multiband` and never `optocomp`.

**Two things were deliberately NOT written into this block, and both were
measured.** Printing how many real units land on each module, as a tie-break
("more hall reverbs exist than plates"), was tried and made the score WORSE —
mean 76.1 → 75.7, order 0.87 → 0.81 — so it is not here; see the round log in
[../testing/ai-eval.md](../testing/ai-eval.md). And a rule of the shape "prefer
`graphiceq`" was never written at all: the only evidence for it is 35 songs by
two artists, Rocksmith's own three default rigs use the PARAMETRIC rack, and a
prompt tuned to a corpus is a prompt that is true of nothing else.

**Do not hand-edit the block, and do not add a second copy of this mapping.**
`DotCharter.Tests/Ai/ToneIdiomPromptTests.cs` re-derives every printed example
through the real converter and fails if one disagrees, checks that every idiom
module is one the catalogue section also lists (or the model is told to use a
module it cannot spell the params of), and pins the ordering — the block is part
of a cached prompt prefix, so dictionary order would miss the provider's cache on
every process restart.

### Ranges come from the catalogue, never from prose

`DcRigTargetCatalog` — the managed mirror the validator clamps against — is what
generates the range text. **Do not hand-write a range into a gear note.** A prompt
and a validator that disagree produce a tone "corrected" on every request for no
reason the user can see; `ToneDesignPromptTests` pins one of them
(`gainDb (-24..24, def 0 dB)`) by value.

### Why `AiGearNotes` is a hand-written table

`IDspChainService`'s live catalogue has display names and native categories, but it
needs an OPEN ENGINE — and the prompt is built in Settings, in the eval harness and
in every unit test, none of which has one. So the PROSE is a table here and the
NUMBERS come from the catalogue. `ToneDesignPromptTests
.GearNotes_CoverEveryCatalogueModule_AndNameNoneTheCatalogueLacks` fails if the
two drift in either direction: a missing row is a module the model can never
reach, and a stale row is one it will confidently hallucinate.

Units are **derived** from the engine's own param-id suffix convention
(`*Db`, `*Hz`, `*KHz`, `*Ms`, trailing `*S`, `*Pct`, `*Cents`, `*Semis`, `*Cm`,
`*Deg`) rather than tabulated: a hand-written unit column over ~700 params is a
second source of truth that would rot the first time the catalogue grew.

`splitter` and `merger` are in `AiGearNotes.SeriesExcluded` — v1 designs series
chains only, and a `ToneDraft` cannot express a branch tag, so a chain naming one
is a rig the engine would wire differently from what the model described.

`rsamp` (#88, Rocksmith's own amp chains) is in `AiGearNotes.NotDesignable`: its
ten knobs mean a different control on each of 63 amps, so it has no gear note, the
validator drops it, and `RsConverterIdiom` names each RS amp by the designable
archetype for its slot (`amp`, `bassamp`, `tubepre`). The eval judges a converted
truth's amp the same way (`ToneDesignScorer.RsAmpStandIn`), so a design is never
scored against a module it cannot write.

## Grounding stage 2 — the library anchor

Stage 1 is the catalogue in the system prompt: what the modules ARE. Stage 2 is
what a professional did with them for **this exact song** — the Rocksmith tone
that shipped with it, through DotCharter's own `RsToneConverter` so it is
expressed in the same module ids the model has to answer in.

```
ToneDesignService  ->  ToneAnchorRetriever.RetrieveAsync(IToneAnchorLibrary, request)
                                |
   the plugin's anchors    ->  ILibraryService.ScanAsync (SQLite-backed)
                           ->  the package tone importer             (per archive)
                           ->  RsToneJsonParser.TryParse
                           ->  RsToneConverter.ConvertFilled
```

- **Anchor or analogies, never both.** The song IS in the library → one anchor,
  labelled "this is how Rocksmith rigged it, adapt it". It is NOT → up to two
  nearby songs, labelled as analogies and explicitly *not* the requested song's
  tone. A model told "this is the song's tone" about a different song repeats
  that back in its rationale.
- **Title AND artist must both be present and both match.** A title alone
  matches a dozen covers, and an anchor from the wrong recording is worse than
  none — the model defends it. Matching folds case and punctuation
  (`AC/DC` = `AC DC`, `T.N.T.` = `tnt`) and nothing fuzzier.
- **Nearest = same ARTIST first, then same ERA.** A Rocksmith package carries
  **no genre at all** (`IChartGenreIndex` is for the user's own charts and says
  so), so year is the only era signal there is; `SameEraYears` is 8. A song that
  is neither is dropped rather than ranked last — a random song labelled
  "nearby" teaches the model something false.
- **Which tone: the requested ROLE, else the richest.** `clean` / `lead` (or
  `solo`) / `rhythm` / `bass`, matched whole-word against the tone's own name.
  **Bass wins outright** — "a clean bass" is a bass request, and answering it
  with the guitar's clean tone anchors the chain on the wrong amp. With no role
  named, the LONGEST chain wins: it carries the most of what made the song sound
  like itself.
- **The anchor REPLACES one generic few-shot** (`crunch-plexi`), so the budget
  does not grow. That means two cached system prefixes,
  `SystemPrompt` and `AnchoredSystemPrompt` — both build-constant and both
  cacheable, at the cost of a cache miss when a player alternates. The
  alternative costs the anchor's ~400 tokens of UNCACHED input on every anchored
  request, which is worse. Analogies do NOT buy a few-shot: they are a weaker
  signal than a generic example is a teacher of the format.
- **The anchor is in the same TURN as the request**, so a FOLLOW-UP keeps it:
  "less gain" stays measured against the song's own rig rather than against
  whatever the last answer drifted to.
- **It can never fail a design.** No library, no path, an unreadable psarc, a
  throwing scan — every one is `ToneAnchorSet.Empty` and an ungrounded request.
- **`AiProviderSettings.UseLibraryTones`** (default true) switches it off, and
  off means the library is never ASKED, not that its output is hidden.
- **The eval must run with it OFF for the scored song** — the scorer grades
  against the very tone the anchor would hand over. See
  [docs/agents/testing/ai-eval.md](../testing/ai-eval.md) § anchor leakage.

The library scans ONCE per process (the walk has no business on the critical
path of a request a user is watching) but re-reads the configured root every
time, so repointing the library needs no restart. Tone BODIES are never cached
by the psarc index — only slot names — so only the shortlisted archives
(`MaxArchivesPerLookup` = 6) are opened.

`ToneEvalCorpus.HarvestPsarc` is the test-side copy of this same walk, and it
stays separate on purpose: it reads a corpus directory rather than the configured
library, and it must keep working when this stage is switched off.

## The wire schema, and the subset both decoders actually accept

`ToneDraftJson.Schema` is hand-written and goes to BOTH providers verbatim
(Claude's `OutputConfig.Format`, an OpenAI-compatible server's
`response_format.json_schema`). **It must stay inside the INTERSECTION of the two
documented strict subsets, because a keyword outside it is a 400 before a token
is generated** — not a looser constraint.

| | Anthropic ([docs](https://platform.claude.com/docs/en/build-with-claude/structured-outputs)) | OpenAI ([docs](https://developers.openai.com/api/docs/guides/structured-outputs)) |
|---|---|---|
| `additionalProperties` with a SCHEMA value (an open map) | unsupported — "set to anything other than `false`" is listed as unsupported | unsupported — every object must set `additionalProperties: false` |
| `minimum` / `maximum` / `multipleOf` | unsupported ("Numerical constraints") | unsupported (Number row of the unsupported-keyword table) |
| `minItems` | 0 and 1 ONLY | unsupported |
| `maxItems`, `uniqueItems`, `pattern`, `minLength`/`maxLength`, `format` | unsupported | unsupported |
| every declared property in `required` | supported; the SDKs do it | REQUIRED |

(OpenAI's own page links the list rather than printing it; the same table is
printed verbatim on
[Microsoft Learn](https://learn.microsoft.com/en-us/azure/ai-foundry/openai/how-to/structured-outputs),
which is the Azure mirror of the same feature.)

What that means for this schema, all of it shipped 2026-09-12:

- **`params` is a LIST of `{ id, value }` objects, not an open map.** A param id
  → number map is the obvious encoding and is the one thing neither decoder
  takes. `ToneParamsConverter` in `ToneDraftJson.Options` converts at the JSON
  boundary, so **`ToneDraft` and `ToneDraftModule.Params` are UNCHANGED** — the
  few-shots, the validator and the eval all still see a dictionary. The
  converter still READS the old open-map form, because a local model that
  ignores the schema keeps emitting it and a usable tone must not be lost to its
  encoding. A malformed entry becomes `NaN`, which is the validator's "was not a
  number; the default N was used" warning rather than a silent default.
- **A module carries `capture` (W8), and it is REQUIRED like every other
  property.** Both strict subsets demand that every declared property be in
  `required`, so a non-`namamp` module emits `"capture": ""` — the schema's
  `description` says so and the few-shots show it. `ToneDraftModule.Capture` is
  additive with a parameter default, so every call site written before it
  compiles unchanged, and `ToneDraftJson`'s reader takes a reply without the
  field at all (an older local model) as the empty string, which is simply a
  chain with no capture in it.
- **The draft carries `captureSearch` (2026-09-26), REQUIRED for the same reason**,
  and empty unless the rules allow it. `ToneDraft.CaptureSearch` has a parameter
  default, so a reply without it reads as no search. See the capture search §.
- **No `minimum`/`maximum` on `confidence` and no `minItems`/`maxItems` on
  `modules`.** Nothing is lost: `ToneDraftValidator` clamps every range and
  truncates at `MaxModules` app-side anyway, and the bounds are stated in the
  `description` strings, which the model reads.
- `AiProviderCompatTests` walks the whole document and fails on any keyword
  outside the allow-list (which names the doc URLs), and separately asserts that
  every object is closed and requires every property it declares. **`minItems` is
  deliberately NOT in the allow-list** even though Anthropic takes 0 and 1: the
  intersection is what ships.

## The validator

`ToneDraftValidator.Validate(json)` → a `DspTonePreset` (or null) plus warnings.

- unknown module → **dropped**, warning `"…" is not a module DotCharter has`
- `splitter`/`merger` → dropped, with the parallel-split reason
- unknown param → ignored, warning naming the module and the knob
- out of range → **clamped, and the warning names old → new**. A silent clamp is
  how a tone comes out wrong with nothing on screen saying so.
- non-finite → the catalogue default
- a `namamp` whose `capture` is not in the catalogue, or is EMPTY → **dropped**,
  with a warning naming the id / the module (the capture list § above)
- a `capture` on any other module → ignored, with a warning
- more than 12 modules → truncated (the head of a chain is what defines the sound)
- zero valid modules → null preset → `ToneDesignFault.InvalidOutput`

**A tone FILE is rejected whole for an unknown module (`TonePresetJsonFile.Parse`)
and a model's reply is not.** That is deliberate and is the one place the two
diverge: the user asked for that exact file, but only for a SOUND from the model,
and eleven good modules plus a note beats nothing.

The preset is minted as `ai-<guid>` / `DspToneOrigin.User`; the category is picked
from the draft's words, with `bassamp`/`acpre` in the CHAIN overriding the prose
(structure beats keywords).

## The preparation seam

`ToneDesignService` runs the result through **`DcRigPresetPreparation.Prepare`** —
the same one preset apply uses — so an AI tone and a saved tone
are prepared identically, every omitted param pinned to its catalogue default.

**It is also what resolves a chosen CAPTURE.** The validator writes the
capture's bare FILE NAME into the `namamp` module's `modelPath` string param and
`Prepare` rewrites it to an absolute path under the NAM capture directory —
`ToneDesignService` has been handing that directory in since 0.55, so nothing
about the resolution rule is new; only the thing that can produce a `modelPath`
is.

**It is NOT loudness-normalized, and that is correct.**
`DcRigLoudnessNormalizer` runs on `BuiltIn` presets only; an AI tone is a USER
preset, and a player's trailing gain is a deliberate choice. Applying the
normalizer here as well would be the double-apply the pipeline's contract exists
to prevent. `ToneDesignServiceTests.AUserToneIsNotLoudnessNormalized_…` pins it.

## The providers

Both are thin: marshal the prompt, call, return JSON. No catalogue knowledge, no
validation, no UI. Both map every failure onto `ToneDesignException` with a
`ToneDesignFault`; **no SDK or HTTP type ever reaches the UI**, and
`ToneDesignService` carries a net under a provider that forgets.

### Claude (`Anthropic` 12.55.0)

- **Structured output**: `OutputConfig.Format = new JsonOutputFormat { Schema }`,
  so the reply is schema-valid JSON by construction.
- **Streaming** (`Messages.CreateStreaming`) so a long generation cannot sit on one
  open response until the client timeout. **There is no `GetFinalMessage` helper in
  the C# SDK** — this accumulates the text deltas and reads the stop reason and
  output usage off `message_delta`.
- **Adaptive thinking by OMISSION**: on Opus 5 and 5.5 thinking is on by default and
  `budget_tokens` is removed (400). `Temperature` likewise. Both absences are
  asserted on the WIRE.
- `MaxTokens` 4096 — a tone is small.
- **The system block carries `CacheControlEphemeral { Ttl = Ttl.Ttl1h }` — the
  ONE-HOUR tier, not the 5-minute default.** A tone session is a player
  listening, playing, then asking for "less gain": minutes between turns, which
  is a 5m MISS, and a miss costs a 1.25× cache WRITE that nothing ever reads
  against — **~16 % MORE than not caching at all**. The 1h write is 2× and holds
  for the session, so the SECOND request inside the hour already pays for it
  (2× + 0.1× against 2× uncached). Multipliers:
  [prompt caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching).
  The SDK exposes it as a plain property (`Anthropic` 12.55.0,
  `CacheControlEphemeral.Ttl`) and no beta header is needed. Asserted on
  `BuildParams` AND as `cache_control.ttl == "1h"` on the wire.
- **`stop_reason == "refusal"` → `ModelRefused`.** Read it with
  `StopReason.Raw()`, not `ToString()` — `ApiEnum.ToString()` renders the JSON
  form and comes back QUOTED.
- Faults, most-specific-first: `AnthropicUnauthorizedException`/`Forbidden` →
  Authentication, `AnthropicRateLimitException` → RateLimited,
  `AnthropicIOException` → Network, `AnthropicApiException` → Unknown.
- **Server-side refusal fallbacks are NOT enabled.** The C# README's § Refusal
  Fallbacks is written for `claude-fable-5-1` and needs the BETA messages path;
  this provider is on the non-beta path with an Opus 5.5 default, so the array form
  does not apply. Revisit if the default model changes.
- The test seam is `AnthropicClient.HttpClient`, and it is built through
  `OnlineHttp.CreateClient` — `OnlineHttpPolicyTests` fails a bare
  `new HttpClient(` anywhere under `DotCharter.Services`.

### OpenAI-compatible

POST chat completions under the configured base URL, `stream: false`,
`temperature: 0.3`, 120 s. Presets: LM Studio `http://localhost:1234`, Ollama
`http://localhost:11434`.

- **The endpoint is DERIVED from the base URL; a blind `/v1` append was wrong for
  half the servers this provider claims to support.** `ChatCompletionsUrl` /
  `ModelsUrl` implement one rule, and `AiProviderPresets.BaseUrlExamples` is the
  single list the Settings hint, this doc and the tests all quote:

  | Base URL shape | Result | Example |
  |---|---|---|
  | no version segment in the PATH | `+ /v1/chat/completions` | `http://localhost:1234`, `http://localhost:11434`, `https://api.deepseek.com` |
  | a path that already carries one (`v1`, `v1beta`, `v2`, `v1alpha2`) | `+ /chat/completions` | `https://openrouter.ai/api/v1`, `https://generativelanguage.googleapis.com/v1beta/openai` |
  | the full `…/chat/completions` URL | used as given (and `ModelsUrl` takes its sibling) | `https://api.openai.com/v1/chat/completions` |

  Gemini's OpenAI-compatibility layer is the case that forced this: Google
  documents the base URL as `https://generativelanguage.googleapis.com/v1beta/openai/`
  ([docs](https://ai.google.dev/gemini-api/docs/openai)), and `/v1` appended to
  that addresses `…/v1beta/openai/v1/chat/completions`, which does not exist.
  **Only the PATH is inspected** — a server at `v1.example.com` still gets `/v1`.
- **The schema is sent TWICE, on purpose**: in
  `response_format.json_schema` with `strict: true` (LM Studio honours it and
  constrains generation), AND appended to the system prompt as text, because
  Ollama's `/v1` shim ACCEPTS the parameter and may ignore it. The reply is then
  parsed defensively either way — `ToneDraftJson.ExtractJsonObject` strips fences
  and finds the BALANCED object (brace-counting, string-aware; first-`{`-to-last-`}`
  loses to a sentence containing a brace after the object).
- **…and a server that will not take it at all gets ONE retry without it.**
  DeepSeek supports `response_format: {"type": "json_object"}` and no
  `json_schema` ([docs](https://api-docs.deepseek.com/guides/json_mode)) and
  answers 400. A 400 whose body names `response_format` or `json_schema` is
  retried once in `json_object` mode — the schema is already in the system
  prompt (which also satisfies DeepSeek's "include the word json" requirement)
  and the design service validates the reply either way, so the only thing lost
  is the constraint. **Matched on the PARAMETER NAMES, not on a message**: every
  gateway words it differently. A 400 about anything else is NOT retried —
  retrying every 400 doubles the cost of a bad model id and hides the reason.
  Logged at Information ONCE per process, never with the body (an error payload
  can echo the request back).
- `Authorization: Bearer` only when a key exists — LM Studio and Ollama want none
  and some gateways 401 on an empty bearer.
- `ProbeAsync` is a GET of the models endpoint by the same URL rule: a model that
  is not listed is a WARNING with the available ids, not a failure (Ollama lists
  pulled models, LM Studio loaded ones — the user may be about to load it).

#### OpenRouter

Three extras, keyed off the base URL's HOST alone (`IsOpenRouter`; the host, not
a substring, so a proxy that merely mentions it is not OpenRouter). **None of
them reaches another server** — `provider` is a field LM Studio has never heard
of, and a gateway that validates its body would 400 on it.

- **`"provider": { "require_parameters": true }`** — "Only use providers that
  support all parameters in your request"
  ([provider routing](https://openrouter.ai/docs/features/provider-routing)).
  Without it OpenRouter may route a `json_schema` request to a backend that
  silently IGNORES `response_format`, which is the worst outcome: not an error,
  just a worse tone, scored on a model-comparison eval as the MODEL's fault.
  Omitted from the `json_object` fallback body, which no longer needs the
  capability.
- **`usage.cost` is READ, and nothing is sent to ask for it.**
  `usage: { include: true }` is documented as DEPRECATED and having no effect —
  "Full usage details are now always included automatically in every response"
  ([usage accounting](https://openrouter.ai/docs/use-cases/usage-accounting)) —
  so sending it would be a no-op parameter for a later reader to re-research.
  The value lands in `AiUsage.CostUsd` (additive, default null). It is in
  CREDITS, which OpenRouter sells at one US dollar each. **`null` is "the server
  priced nothing", not zero**: `AiModelPricing`'s estimate stays the answer
  there, and a local server costs nothing at all. The field is read for EVERY
  server — it is absent everywhere else, which is exactly what null means.
- **`HTTP-Referer` + `X-Title`**, the documented optional attribution headers
  ([API overview](https://openrouter.ai/docs/api-reference/overview); `X-Title`
  is the accepted alias of `X-OpenRouter-Title`).

### Claude Code (local CLI)

`AiProviderKind.ClaudeCli = 4`. Drives the `claude` binary the user already has
installed, headless, as a child process. **No API key**: it answers under that
user's own Claude Code login. **Its default model is Claude Haiku 5.5**
(`ClaudeCliToneModel.DefaultModelId`, owner's call 2026-10-07); Opus 5.5 and
Sonnet 5.5 are fill-in presets. The default model of every kind is ONE table,
`DotCharter.Tone/Ai/AiDefaultModels` (`AiDefaultModelsTests`).

**The terms, stated once and repeated in the UI.** This is personal use of the
user's OWN Claude Code installation under their OWN login, so **every design
counts against that Pro/Max subscription's usage**, exactly as if they had typed
the request into Claude Code themselves. It is therefore **OPT-IN, never a
default**, and the Settings row carries a one-line note (`Ai_ClaudeCli_Note`)
saying what it runs, whose login it uses and whose usage it spends. The app never
ships a login, never reads a token, and never passes an API key to the child. Do
not make this the default provider, and do not remove that note.

**`Supports(ClaudeCli)` is the one machine-dependent answer in the factory**: it
is "a binary was found", cached for the process in `ClaudeCliLocator`. The
Settings picker greys the row out where there is none. One consequence worth
knowing: the row is disabled before the user can reach the path box, so an
explicit path only helps a machine where the row was already enabled - an install
that is on PATH. Fixing that means making `Supports` consult the persisted path
too, which is a contract change, not an implementation one.

**`AiProviderSettings.BaseUrl` is REUSED as an optional path to the binary** for
this kind, and the record's doc comment says so. It is not a URL there and
nothing may parse it as one.

The command line, measured against `claude -p --help` on **2.1.269** and
re-checked on **2.1.273** (what is installed on the owner's machine, 2026-09-16):

```
--print --output-format json --model <ModelId> --json-schema <schema>
--system-prompt-file <path>  |  --system-prompt <short framing>
--tools "" --strict-mcp-config
--disable-slash-commands --no-session-persistence --permission-prompts none
```

**With online research on (`AiProviderSettings.WebResearch`, default true since
2026-09-27) `--tools ""` becomes `--tools "WebSearch,WebFetch" --allowedTools
"WebSearch,WebFetch"`**: the two read-only web tools and nothing else,
pre-approved so no permission prompt can wait on a user who cannot see it. The
single-turn guarantee below then no longer holds (a search is a turn); the
schema still binds the answer, and the deadline is `ResearchTimeout` (300 s).
Codex gets `-c web_search="live"` (and `"disabled"` when off — its default is a
cached index, which would search anyway), deadline 360 s. The instruction
(`ToneDesignResearch.Instruction`: look up the recording's amp, cab, mic,
guitar, pedals and tuning; a request naming no recording needs none) rides the
CONVERSATION on stdin, never the system prompt, so the cached grounding is
unchanged by the setting. The API providers have no web tools and ignore it; the
Settings row (`Ai_WebResearch*`) shows for the two CLIs only. Every request also
carries the rig tuner's tuning when one is picked (`ToneDesignRequest.Tuning`,
"The player's guitar is tuned to: Drop D (D A D G B E)"). Pinned by
`ToneDesignResearchTests` and the two providers' exact-argument tests.

Four decisions in that line are load-bearing, and each is pinned by a test:

- **`--bare` is NOT passed, and passing it would break the feature outright.**
  Its own help says *"Anthropic auth is strictly ANTHROPIC_API_KEY or apiKeyHelper
  via --settings (OAuth and keychain are never read)"* - and the subscription
  login IS the OAuth path. `--bare` would turn the no-key provider into a
  key-only one. It is the obvious "minimal mode" tidy-up, which is exactly why
  `ClaudeCliToneModelTests.TheArgumentList_NeverPassesBare_...` exists.
- **`--max-turns` does not exist** - it is in no `--help` output. **The CLI's
  option parser is NOT strict: an unknown flag is accepted silently**, so "the
  command did not error" is no evidence a flag exists, and a forgotten flag reads
  like a working limit. Verify against `--help`, never against exit status. A
  single turn is guaranteed instead by `--tools ""` - with no tools there is
  nothing to take a second turn with.
- **The ~35,000-character system prompt never goes on the command LINE.** A
  Windows command line is capped at **32,767 characters for the whole line**, and
  prompt + schema + flags measured **~32,730** on 2026-09-12 - some 37 characters
  of headroom. The grounding goes either in a FILE (below) or at the head of the
  piped stdin; the command line carries a path or a short framing, never the
  catalogue.
- **W9 (2026-09-16): `--system-prompt-file` EXISTS on 2.1.273, and the two
  claims this section used to make about it were both wrong.**
  - It has **no option entry of its own** in `claude --help`. It appears only
    inside `--bare`'s description, as
    `"Explicitly provide context via: --system-prompt[-file],
    --append-system-prompt[-file], --add-dir"` - and it WORKS. Verified by
    behaviour, not by exit status: a file saying "answer BANANA" produced
    `"result":"BANANA"` with `input_tokens` 808, i.e. the coding-agent prompt was
    replaced. `ClaudeCliCapabilityProbe` therefore accepts EITHER spelling out of
    `--help`, once per process; the literal one alone would find nothing and leave
    the feature permanently off on the machine it was written for.
  - **"This provider gets no prompt-cache discount" was false, and the truth is
    narrower and worse.** The CLI does set a cache breakpoint on the piped user
    turn, so two IDENTICAL requests hit it. But the request text differs on every
    real second design, and that invalidates the whole turn: measured 2026-09-16
    on 2.1.273 with a synthetic 25k-token grounding, the stdin layout with a
    CHANGED request read **0** cached tokens and wrote **25,114**, while the
    `--system-prompt-file` layout read **24,334** every run and wrote only the
    ~765-token tail. Against the REAL grounding, `ClaudeCliSmokeTests
    .TwoDesignsInARow_ReadTheGroundingFromTheProvidersCache` measured
    `cache_read_input_tokens` **39,221** and **39,225** on two back-to-back
    designs.
  - **The file is written once per CONTENT** (`ClaudeCliSystemPrompt`, name =
    prefix + an FNV-1a hash of the bytes) under
    `%TEMP%/DotCharter/ai-cli/prompts/`, so a changed catalogue or capture library
    writes a NEW file and the stale siblings are swept on the first write of a
    process. The framing lives in the file's HEAD, because
    `--system-prompt-file` replaces the CLI's own prompt exactly as
    `--system-prompt` did and the two flags are never passed together.
  - **`--resume` / `--continue` are NOT used and the decision is deliberate.** A
    resumed session replays every prior turn into every later request, which for
    short independent tone requests costs more than the cache saves. The
    invocation stays a one-shot `--print`.
  - **A CLI that does not advertise the flag, or a temp folder that cannot be
    written, falls back to the stdin layout** and the chosen path is LOGGED. Both
    layouts, the probe's decision and the argument-length cap are pinned by
    `ClaudeCliSystemPromptTests`.
- **The child runs in an empty scratch folder under TEMP.** Run it in the app's
  cwd and the CLI auto-discovers whatever `CLAUDE.md`, settings and git state
  live there and prepends them to a request about guitar tones.

The envelope `--output-format json` prints (captured from a real run):
`structured_output` is the parsed object and is PREFERRED, `result` is the same
thing as a string and is the fallback, `is_error` / `subtype` report a failure,
and `usage` splits input tokens three ways. **`AiUsage.InputTokens` is
`input_tokens + cache_read_input_tokens + cache_creation_input_tokens`** - the
first alone reported a ~7,500-token request as 2, which makes the panel's cost
estimate meaningless. `total_cost_usd` is LIST price and is logged, not shown: a
subscription user is not billed it. Since W9 most of that input is a cache READ,
which is what makes the number look large and the bill not.

Faults: no binary -> `NotConfigured` naming the install step; a signed-out CLI ->
`Authentication`, matched on PHRASES over stdout+stderr because no exit code
distinguishes it; our own 120 s deadline -> `Network`; anything else non-zero or
unparseable -> `InvalidOutput`. `ProbeAsync` runs `claude --version` and reports
it - **free, so pressing Test never spends a request**.

`IClaudeCliRunner` is the test seam and `ClaudeCliRunner` is the only place a
process is started: stdin is written and CLOSED (an open stdin is a child that
never answers), cancellation kills the **process tree**, and a timeout comes back
as an OUTCOME while only the caller's own token throws. Containment needs nothing
extra - `ChildProcessJob` already puts the app in a kill-on-close job and a
Windows child inherits it.

**One real end-to-end run exists and is env-gated OUT of the gate**:
`ClaudeCliSmokeTests`, behind `DOTCHARTER_AI_CLI_SMOKE=1`. It spends a real
request against whoever is logged in, so it must stay opt-in - the same rule
`GND_IDMT_ROOT` exists for. Measured 2026-09-12 on 2.1.269 / `claude-opus-5`,
"Master of Puppets clean tone": **16,296 in / 736 out tokens**, and a chain of
`compressor -> amp -> cab -> eq -> chorus -> delay -> reverb` that the validator
accepted with **zero warnings**.

### Codex (local CLI)

`AiProviderKind.CodexCli = 5` (appended; the ordinal is persisted and pinned in
`ClaudeCliToneModelTests.TheProviderOrdinals_ArePinned`). Drives the `codex`
binary the user already has, under THEIR OWN Codex / ChatGPT login — the same
terms as the Claude Code provider above: opt-in, never a default, no key read or
passed, and each design spends that user's own usage.

- **The argument list is the contract** (`codex exec`, verified on 0.156.1):
  `exec --skip-git-repo-check --ephemeral --ignore-user-config --ignore-rules
  --sandbox read-only --color never --json --output-schema <file> -o <file>
  [-m <model>] -`. The prompt goes on STDIN (`-`), never the command line.
  `--ignore-user-config` / `--ignore-rules` keep the user's own `config.toml`
  and AGENTS rules out of a tone request; `--sandbox read-only` means the agent
  can change nothing on the machine.
- **The reply is read from the `-o` FILE**, validated against the schema file
  the CLI enforces, not scraped from stdout. Usage comes from the JSONL events
  (`turn.completed.usage`; output counts `reasoning_output_tokens` too).
  `turn.failed` / `error` events are the fault text. Both scratch files live in
  `%TEMP%\DotCharter\ai-codex` and are deleted afterwards.
- An **empty model id means GPT-6 Luna** (`CodexCliToneModel.DefaultModelId`,
  owner's call 2026-10-07), so `-m` is always passed — `IsEnabled` treats Codex
  as configured without one, unlike every other provider. The Settings tab
  (both hosts) seeds Luna when the kind is picked and offers two fill-in buttons
  for this kind only, `CodexCliToneModel.SolModelId` (`gpt-6-sol`) and
  `LunaModelId` (`gpt-6-luna`).
- Faults: a signed-out CLI ("codex login", "not logged in", 401) is
  `Authentication`; no reply file is `InvalidOutput`; the probe runs `--version`
  and spends nothing. `CodexCliLocator` checks PATH, then
  `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`; `AiProviderSettings.BaseUrl`
  doubles as an explicit binary path, as for Claude Code.
- `CodexCliToneModelTests` pins all of it over a scripted runner that writes the
  `-o` file the way the real CLI does.

### Reasoning effort (#603)

Settings ▸ AI ▸ **Reasoning** (Default / Low / Medium / High) is
`AiProviderSettings.Effort` (`AiReasoningEffort`, additive: an older config reads as
Default, persisted by NAME). **`AiReasoning` (`Ai/AiReasoning.cs`) is the ONE rule** —
`Support(kind, modelId)` and `WireValue(effort, kind, modelId)` — that every provider
applies to the model it is about to call, and that the Settings row
(`Presentation/Screens/Settings/AiReasoningPart`) and the chat's label
(`AiChatTaskDefaults.EffortLabel`) read, so a level the UI shows as live is exactly the
level the request carries. **Default sends nothing** — the provider's own default, what
every build before it did.

| provider | on the wire | source |
|---|---|---|
| Claude API | `output_config.effort` beside the format (two initializers, never a null `Effort`) | GA in the Messages API; SDK `OutputConfig.Effort` |
| Claude Code CLI | `--effort <level>` right after `--model` | `claude --help`, 2.1.296: "Effort level for the current session (low, medium, high, xhigh, max)". The CLI accepts an unknown flag silently, so an older binary ignores it |
| Codex CLI | `-c model_reasoning_effort="<level>"` | the `config.toml` key; `codex exec --help` 0.162.1 documents `-c key=value`. The run uses `--ignore-user-config`, so the user's own value never reaches it and Default is Codex's built-in |
| OpenAI-compatible | `reasoning_effort`; OpenRouter `reasoning: {effort}` | a 400 NAMING `reasoning` is retried ONCE without it (logged once per process), like the `response_format` fallback |

**Unsupported → the row is disabled with the reason printed**: a Claude model older
than the parameter (`claude-3*`, `claude-haiku-4*`, `claude-sonnet-4-0/4-2*/4-5`,
`claude-opus-4-0/4-1/4-2*`; effort arrived with Opus 4.5 and Sonnet 4.6), on the API and
on the CLI alike — those models are sent nothing whatever the setting says. The chat
pane prints the level beside the model (`AiChat_Effort`), Default where the model cannot
take one. `tools/chart-eval/AiChartEval.cs --effort default|low|medium|high` passes it the
same way. RigLab's own AI tab does not offer it (its `riglab.json` saves Default).
Tests: `Services/Ai/AiReasoningEffortTests` (the rule, the CLI argument lists, the
OpenAI body and retry), `ClaudeToneModelTests.TheEffort_IsOnTheWire_…`,
`Presentation/Settings/AiSettingsReasoningTests`,
`Services/Configuration/AppConfigAiRoundTripTests`, and the headless
`SettingsConstructionUiPerfSmokeTests.AiTab_TheReasoningRow_…` /
`AiChatPaneConstructionUiPerfSmokeTests.TheEffortLabel_…` in three cultures.

### Adding a third provider

`AiProviderKind.BuiltIn` is the reserved slot (a bundled downloadable model) and
today answers `NotConfigured` with "not available yet" rather than throwing
something the UI renders as a bug. To fill it:

1. implement `IToneDesignModel` (Kind, `CompleteAsync`, `ProbeAsync`), translating
   every failure into a `ToneDesignFault`;
2. add the arm to `ToneDesignModelFactory.Create`, with its own specific
   NotConfigured message;
3. if it owns an `HttpClient`, go through `OnlineHttp.CreateClient` and implement
   `IDisposable` — the service disposes the provider it was handed;
4. test it with a scripted `HttpMessageHandler`, the shape both existing providers
   use.

**Do not add a provider-shaped enum value without an arm in the factory**: the
`None`/`BuiltIn` arms exist so an un-implemented kind is a MESSAGE, not a throw.
`ClaudeCli = 4` was APPENDED for the same reason the others are pinned - the
ordinal is persisted as an INT in `AppConfig.Ai`, and renumbering would silently
repoint every saved configuration at a different provider
(`ClaudeCliToneModelTests.TheProviderOrdinals_ArePinned`). `NormalizeAppConfig`
needed no change: it coerces an explicit `"Ai": null` and says nothing about the
kind.

## The 0.55.1 contract additions

All four are ADDITIVE — the contract is added to, never renamed:

| Member | Why it exists |
|---|---|
| `IAiSecretStore.IsPersistent` | so the Settings tab stops naming `DpapiAiSecretStore`. It was a STATIC on the concrete store, which a test double could not answer for |
| `IToneDesignModelFactory.Supports(kind)` | the ONE list of kinds this build can mint. The Settings picker's enabled rows come from it instead of a hand-kept list that can disagree with `Create`. `AiContractAdditionsTests.Supports_AgreesWithCreate` drives every kind through BOTH and fails if they drift |
| `IToneDesignService.ConfiguredChanged` + `NotifyConfigurationChanged()` | the panel subscribes instead of re-reading `IsConfigured` on every expand — that read is a whole `LoadAppConfig`, on the UI thread, on a property a binding touches. The Settings tab's Save pokes the service, INSIDE its change test. The comparison is on the whole settings RECORD, not on `IsConfigured`: Claude → a local server never moves the boolean, and a panel that watched only the boolean would keep describing the model that used to answer |
| `ToneDesignResult.Elapsed` | wall time for the whole call, measured by the SERVICE. Optional positional parameter with a `default`, so every pre-0.55.1 call site compiles. `TimeSpan.Zero` means "not measured" and the UI prints nothing rather than `0.0 s` |

### The later contract addition

`AiUsage.CostUsd` (`decimal?`, default null) — what the SERVER charged, for a
provider that says. ADDITIVE with a parameter default, so every pre-existing
`new AiUsage(model, in, out)` compiles unchanged. Only the OpenAI-compatible
provider fills it in today, from OpenRouter's `usage.cost`; see that section for
why nothing is sent to ask for it. **`null` is "nobody priced this", never
zero** — the price table below stays the answer there.

### The copy & paste route

Two members on `IToneDesignService`, for a player with a chat subscription and
no API key — neither calls `IToneDesignModelFactory.Create`, so both work while
`IsConfigured` is false:

| Member | What it does |
|---|---|
| `BuildManualPromptAsync(request, ct)` → `ToneDesignManualPrompt(Text, Prompt)` | `PrepareAsync` — the provider path's own first half (anchors, the live capture list, the capture-search offer, `ToneDesignPromptBuilder.Build`) — then `ToneDesignManualPrompts.Flatten`: a framing line, the system prompt, `ToneDesignResearch.Instruction` while `WebResearch` is on (a chat can search), the labelled turns, the schema, and a LAST line telling the chat to answer with the JSON object only. The text stays English whatever the UI language |
| `ImportReplyAsync(request, reply, status, ct)` | `ToneDraftJson.ExtractJsonObject` (prose and a ```json fence are fine; none is `InvalidOutput`), the capture list re-read the same live way, then `ProcessReplyAsync` — the provider path's own second half (capture sourcing, validation, preparation). Usage is `AiUsage.Manual` (`"ChatGPT (manual)"`, zero tokens): the pricing table finds no row, and the panel prints where the answer came from instead of a cost |

`DesignWithAsync` is `PrepareAsync` → `CompleteAsync` → `ProcessReplyAsync`, so
the two routes cannot drift: `ToneDesignServiceTests.TheCopiedPrompt_IsExactlyWhatAProviderIsSent_*`
compares the structured prompt with what a fake provider received, and
`AChatAnswer_ImportsToTheSamePresetTheProviderPathMakesFromTheSameJson` compares
the presets.

### The price table

`AiModelPricing` — `opus-5-5` $4/$20, `sonnet-5-5` $2/$10, `opus-5` $5/$25,
`sonnet-5` $2/$10, `haiku-5-5` $0.10/$0.50, `haiku-4-5` $1/$5, `gpt-6-astra`
$10/$50, `gpt-6-sol` $2/$10, `gpt-6-luna` $0.10/$0.50 per MTok (list prices
checked 2026-09-28; Haiku 5.5 2026-10-07; pinned by value in
`AiModelPricingTests`). **Haiku 5.5 is priced by prompt length**: the row is the
price up to 100,000 prompt tokens; above that it is 5× ($0.50/$2.50). A tone
prompt is ~10k tokens, so only a research turn that pulls in many pages can
cross it, and the estimate then reads low. **Order matters**: the scan takes the FIRST row whose
token the id contains, and `claude-opus-5-5` contains `opus-5`, so a longer token
must precede every shorter token inside it —
`AiModelPricingTests.NoRow_IsShadowedByAnEarlierShorterRow` fails otherwise.
Matching is on the
model FAMILY inside the id, not equality: the same model ships as
`claude-opus-5`, `-latest` and a dated `-20260401`, and a table keyed by the exact
string would fall through to "unpriced" for two of the three.

**An unknown id has NO price, and that is an answer.** The other half of this
feature is a local OpenAI-compatible server where a request costs nothing, so the
UI prints `local` rather than a guessed figure — and never `$0.00`, which reads
as a measured zero. The estimate over-states rather than surprises: it ignores
prompt caching, which only ever makes the real bill lower.

## Configuration and DI

- `AppConfig.Ai` (`AiProviderSettings`, default `Disabled`). Read LIVE through a
  `Func<AppConfig>`, so changing provider in Settings applies to the next request
  rather than the next launch.
- **`"Ai": null` in the file used to survive the load as a NULL in a
  non-nullable property.** `System.Text.Json` assigns an explicit null straight
  over a property initializer, so `AppConfig.Ai` — declared non-nullable, and
  therefore read without a null check by everything entitled to — came out of the
  cache null and was handed to the whole app. `NormalizeAppConfig` coerces it
  (`config.Ai ??= AiProviderSettings.Disabled`) next to the present-mode
  migration, and the next save rewrites the section as a real object rather than
  leaving the file broken for anything else that reads it. A file with NO `"Ai"`
  property (every config written before 0.55) keeps its initializer and was
  always fine — that is the half that made this easy to miss.
  `Services/Configuration/AppConfigAiRoundTripTests` covers populated, absent and
  explicitly-null, and the flag default.
- **`AiProviderSettings.UseLibraryTones`** (default true) is additive with a
  parameter default, so an older config takes the feature ON rather than silently
  losing it.
- Three singletons in `App.AiServices.cs`. None implements `IAsyncDisposable`
  (the shape that makes `ServiceProvider.Dispose()` throw), and the one
  `IDisposable` in the feature — `OpenAiCompatibleToneModel` — is minted by the
  FACTORY per request and disposed by `ToneDesignService`, never resolved as a
  transient from the root.
- **`ServiceRegistrationReachabilityTests` is what proves the feature is not dark**:
  it fails while `IToneDesignService` is registered and named by no product code
  outside `App*.cs`. It goes green when the UI's view model resolves it. Do not
  add an exclusion to quieten it.

## Status, faults and logging

`IProgress<string>` reports "Reading the gear catalogue…", "Asking Claude…",
"Downloading a capture from TONE3000: …" (capture search only),
"Checking N modules…", "Done.". `OperationCanceledException` passes through
untouched — a user Cancel is not a failure. Usage (in/out tokens) and the prompt
size are logged at Information/Debug. **The API key is never logged and never
reaches `ToneDesignException.Detail`.**

## Match from audio (`Ai/Match/`)

Text-to-rig asks the model to imagine a tone. **Match-from-audio measures one.**
It is a second feature under the same folder, built by five parallel lanes
against one contract — `Ai/Match/MatchContracts.cs`, which is interfaces and
records only and takes **additive changes exclusively**: every lane codes against
it and fakes the rest until the merge.

| Lane | Owns |
|---|---|
| **eval** | `Ai/Match/Similarity/**`, `RigRenderer.cs`, `StemIsolator.cs` — the feature vector, the distance, the offline renderer, the stem isolator, and the audio-level eval |
| **data** | `IRigSampler` + the dataset render tool |
| **search** | `IToneMatcher` — retrieval over a pre-rendered library, then CMA-ES refinement — [ai-match-search.md](ai-match-search.md) |
| **model** | `IRigEstimator` — ONNX inference over a trained estimator — plus, since 2026-09-13, `IRigEstimatorModelDownloadService`, which is HOW a real user gets that model (before it, `IsAvailable` was permanently false off the trainer's own machine) — [ai-match-model.md](ai-match-model.md) |
| **ui** | the "Match from audio" action in the tone-design panel |

Since 2026-09-12 the same contract folder carries a SECOND feature, **match by
SECTION** (`Ai/Match/SectionContracts.cs`), again in three lanes: `sections`
(`IToneSectionProposer`, `IToneSetMatcher`), **`prior`
(`IToneMatchPriorSource`, `IToneNarrator` — the language model guesses the rig,
the matcher refines it, the model explains the result: see
[ai-match-prior.md](ai-match-prior.md))**, and `ui`.

**Match BY SECTION** is a second wave over the same folder, against a second
contract (`DotCharter.Services/Ai/Match/SectionContracts.cs`, additive over
`MatchContracts.cs`):
pick a stem and a span per part of a record — "0:10–0:30 clean, 0:30–0:50
distortion" — and get one rig per section, which is what makes tone-from-audio
usable on a song that is not in the library. Three more lanes:

| Lane | Owns |
|---|---|
| **sections** | `IToneSectionProposer` + `IToneSetMatcher` + the stem-kind isolation (`DotCharter.Services/Ai/Match/Sections/`, `StemIsolator.IsolateAsync`) — [ai-match-sections.md](ai-match-sections.md) |
| **prior** | `IToneMatchPriorSource` + `IToneNarrator` — the language model's guess as the matcher's starting point, and its words for what the matcher found |
| **ui** | the section list in the tone-design panel (span + stem pickers, proposals, "apply as tone set") |

### What the eval lane put in the box

| File | What it is |
|---|---|
| `Similarity/ToneFeatureExtractor.cs` | `tonefx-v2`: 381 content-invariant features over a mono clip |
| `Similarity/ToneFeatureLayout.cs` | **the one place** the vector's layout AND the distance's weights live |
| `Similarity/ToneSimilarity.cs` | the ranking metric: weighted, block-normalised L2. 0 = identical |
| `Similarity/MultiScaleSpectralDistance.cs` | the search lane's refinement loss — **same dry take only** |
| `Similarity/LogFilterBank.cs` | triangular LOG-spaced bank + DCT (JuceSharp.Dsp has neither) |
| `Similarity/MonoResampler.cs` | analysis-path rate conversion. **Never for playback** — `LoadResampler.h` owns that |
| `Similarity/LevelMatch.cs` | −26 dBFS RMS with a −0.5 dBFS peak guard |
| `Similarity/ReferenceDi.cs` | the generated dry DI, and `Medley()` — the fixed 20 s take the retrieval library is rendered over |
| `Similarity/RigCeilingProbe.cs` | isolate → render every candidate → rank. The measurement, and what the UI will reuse |
| `RigRenderer.cs` | `IOfflineRigHost` + `NullDeviceRigHost` + `RigRenderer` |
| `StemIsolator.cs` | the guitar out of a record, cache-first |
| `AudioClipLoader.cs` | `IAudioClipLoader`: a file on disk → `MonoAudio`. The one decoder, because the contract carries none and two lanes had started writing their own |

Registered in `DotCharter.UI/App.MatchServices.cs` (re-count with `grep -c AddSingleton` rather than trusting a number here — this slice holds the
search lane's `IRigLibraryProvider`/`IToneMatcher`, the data lane's `IRigSampler`,
the model lane's `IRigEstimator` **and its `IRigEstimatorModelDownloadService`**,
and the prior and sections lanes). **The estimator is constructed with the
INSTALLER's directory, not with `modelDirectory: null`** — that is what stops the
folder it is written to and the folder it is read from becoming two paths.
The host that hands the panel a
song is `PlayModeToneRigViewModel.cs`'s `BuildRigScreen`.
Tests: `DotCharter.Tests/Ai/AudioEval/`.

### Rules that bite from anywhere

- **Log-spaced bands, finer than 1/3-octave, and a modulation block — all three were
  measured, not assumed.** The bank was an HTK mel scale until the search lane found
  that mel is quasi-linear below 1 kHz, so a 60 Hz–10 kHz bank put a whole low shelf
  in ONE band and an optimiser drove a low-gain knob to its minimum while the distance
  fell. 48 log bands make a shelf visible and a filter's Q identifiable. And the
  modulation block exists because a long-term average spectrum cannot see a delay at
  all: a signal plus a delayed copy has the same average spectrum, so the search
  reported a confident match to a rig with the wrong delay entirely.
- **Output-level knobs are unidentifiable here BY CONSTRUCTION — do not "fix" that.**
  Everything is level-normalised, so a rig at `level` 0.3 and one at 0.7 are the same
  tone to this metric, which is correct. Level is settled by `DcRigLoudnessNormalizer`
  after a rig is chosen. A level term in the extractor would make every candidate
  partly graded on gain, which is exactly what `LevelMatch`'s RMS matching prevents.
- **The same extractor on both sides of every comparison.** `ToneSimilarity`
  REFUSES vectors whose `ExtractorId` differs rather than producing a plausible
  number nobody can interpret. Change the layout, the scaling, the frame gate or
  the resampler kernel ⇒ **bump `ToneFeatureExtractor.ExtractorId`**, because a
  stored retrieval library is only comparable within one id.
- **`ToneSimilarity` is for ranking, `MultiScaleSpectralDistance` is not.** The
  second compares spectrogram frames at the same time index, so it hears a
  different riff as a colossal difference. Use it only between two renders of one
  dry take. **`ToneMatcher` added it at weight 0.15 on EVERY run until
  2026-09-12**, stem targets included, which is every production caller — it is
  now gated on `ToneMatchRequest.TargetIsRender` (init-only, default FALSE = the
  stem case), and so is the self-distance floor, because the floor is the scale
  the distance is read against.
- **A rig the sampler would never draw is marked down before it is ranked.**
  `RigPlausibility` scores a chain against `RigSampler`'s own archetype priors
  (missing amp/cab on a played part, a guitar amp on a bass part, two amps, a mic
  without a cab, duplicated families, a chain past the length prior's support) and
  `ToneMatcher` ranks by `Distance × (1 + 0.6 × penalty)`. It exists because the
  first end-to-end eval had the search beating the AUTHORED Rocksmith rig on 12 of
  12 rows with `octaver→klon→tapesat` — no amp, no cab — for Clean, Chorus and
  Octave_Distortion tones alike. **`ToneMatchCandidate.Distance` is still the
  measured audio distance and the prior never touches it**; the ranking key is
  `RankingDistance`, and a consumer that re-sorts by `Distance` undoes the prior
  silently (two did). Details and the fault weights:
  [ai-match-search.md](ai-match-search.md).
- **A span under 2× the self-distance window has NO floor, and that is
  `double.NaN`, not 0.** 0 is a legal floor meaning "perfectly self-similar", so
  reporting it for "could not measure" made every consumer divide by the wrong
  answer. Anything that divides by `TargetSelfDistanceFloor` must check
  `double.IsNaN` as well as `<= 0`.
- **The render budget is measured, not guessed.** One evaluation (render of the
  20 s medley + one extractor pass) is **2.55 s in Release** on the owner's box,
  so `ToneMatcherOptions` defaults are `MaxRenders` 48 against a 150 s wall —
  they were 400 against 60 s, which made the cap unreachable and the record
  misleading. Re-measure with `RenderCostEvalTests` (gated, RELEASE) before
  quoting it, and never set a budget from a Debug number.
- **A render is a pure function of (DI, rig), and it costs a full chain rebuild
  to stay that way.** DSP modules carry state; a reverb tail leaking from the
  previous candidate into this one's first second is a scoring error that looks
  exactly like a better match.
- **Mono in, LEFT plane out.** `ProcessOffline`'s `channels: 1` folds the output
  as `0.5·(L+R)` and cancels anything the rig put out of phase — a `widener`, a
  ping-pong `delay`, a `rotary` read as rigs that did nothing.
- **Offline means offline.** `NullDeviceRigHost` asks for the Null device through
  its engine CONFIG (`JuceEngineConfig.EnableNullDevice`, ABI v41) and calls
  `Stop()` immediately after construction; the export returns `DCA_ERR_DEVICE`
  while any device is open, null included.
- **Never the env var in product code.** `DCA_ENABLE_NULL_DEVICE` is process-wide:
  the host is built during ordinary play (the loudness meter, #67), and a live
  rebuild on another thread while it was set listed the Null type to the player
  (#85). `Infrastructure/ProductNullDeviceEnvLintTests` fails if product code names
  either null-device variable; `NullDeviceRigHostIsolationTests` races a live
  engine against a held-open host construction.
- **Stems are never regenerated.** `StemIsolator` goes through
  `StemCacheCoordinator.TryLoadCachedAsync` — the only front door — and saves a
  separation back *before* the audio is used.
- **RMS-matched, not peak-matched.** A distorted render's crest is 10–15 dB lower
  than a clean one's; peak matching would hand every dirty candidate 10 dB in
  every band and the metric would grade partly on the squash it is meant to
  measure.

### What the ceiling says

The rig ceiling is the number the whole feature is conditional on, and it is
measured by `DotCharter.Tests/Ai/AudioEval/AudioEvalTests` over the 35-song local
corpus. **Read [docs/agents/testing/audio-eval.md](../testing/audio-eval.md)
before designing anything on top of this metric** — in particular the measured
content invariance of ~1.5–1.8×, which bounds how finely any lane may claim to
rank two candidates apart.

**And read it before quoting "the matcher beat the professional's rig".** It did,
on 12 of 12 charted rows of the first end-to-end run — with rigs that had no amp
and no cab. The search optimises this metric directly and `rs-truth` does not, so
at 1.56× invariance beating the ceiling is partly evidence the metric is
gameable. The plausibility prior above buys headroom against that; it does not
raise the metric. Two further routes to raising it (a per-band median across
frames, pitch-anchored distortion bands) were tried on 2026-09-12 and both
measured WORSE — see [ai-match-search.md](ai-match-search.md) for the numbers, so
nobody spends a day re-deriving them.
