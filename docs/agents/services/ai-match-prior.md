# Match by section — the PRIOR lane

`DotCharter.Services/Ai/Match/Prior/**` — **knowledge before measurement.** The
language model guesses the rig for a part of a song, the audio matcher starts
from that guess and refines it against the recording, and the model then explains
the result in words.

Two types, one helper:

| File | What it is |
|---|---|
| `Prior/ToneDesignPriorSource.cs` | `IToneMatchPriorSource` over `IToneDesignService` — the guess, as a `ToneMatchPrior` the matcher takes |
| `Prior/ToneNarrator.cs` | `IToneNarrator` over `IToneDesignModelFactory` — the matched rig, back into a name and a rationale |
| `Prior/DcRigPresetDescriber.cs` | a rig as readable text: the arrow-joined chain, and every knob it sets in the param's own unit |
| `Ai/AiProviderResolution.cs` | the ONE answer to "which provider is in force, and can this build mint it" — shared with `ToneDesignService` |

The contract is `Ai/Match/SectionContracts.cs` (`IToneMatchPriorSource`,
`IToneNarrator`, `ToneSection`, `ToneNarration`), which three lanes share and
which takes **additive changes only**. This lane implements those two interfaces
and nothing else; the section proposer and the set matcher belong to the sections
lane, and the panel to the UI lane.

## The prior source owns NO prompt

`ToneDesignPriorSource` is a thin adapter: it composes a `ToneDesignRequest` and
hands it to `IToneDesignService`, which already carries this build's module
catalogue, the twelve wiring rules and the four few-shots. **A second prompt here
would be a second place those rules can rot** — and the catalogue is generated
from `DcRigTargetCatalog`, so a hand-written one could describe a range the
validator then disagrees with.

What it DOES own is the description, and the whole of it is context the model
cannot get anywhere else:

```
A rig for the chorus part, 1:05–1:32, guitar.
Instrument: guitar.
You cannot hear it; give the rig this part most likely used, as a starting
point an audio matcher will then refine.
```

**The stem is stated twice on purpose.** Rule 6 of the design prompt ("a BASS
request uses `bassamp`, never the guitar amp") and the `gear-bass-di` few-shot
both exist to keep a guitar amp off a bass part — and **both only fire if the
request says it is bass**. `ToneDesignPriorSourceTests.ABassSection_SaysBassInTheRequest`
is the one assertion between a bass part and a guitar rig, and it is on the
REQUEST TEXT rather than on the answer, because the answer is the model's and no
test here can pin it.

**A prior never fails a match.** No provider ⇒ null before a request is built;
any `ToneDesignException` ⇒ a Warning and null, so the pure-audio path runs
exactly as it would have. `OperationCanceledException` is the one thing that
propagates — a user Cancel is not a prior that could not be obtained, it is a
match that is over.

The prior's `Source` is `ai:<kind>` (`ai:claude`, `ai:openaicompatible`) from
`AiProviderResolution.SourceTag`, and it becomes part of the winning candidate's
origin.

## How the prior steers the matcher

`Ai/Match/Search/ToneMatcher.cs`. Three mechanisms, and **all three are inert
when `ToneMatchRequest.Prior` is null** — the pure-audio path is the plain top-k
it always was (`PriorSteeringTests.NoPrior_ShortlistIsThePlainTopK`).

1. **Candidate zero.** The prior's rig is rendered and scored like any other.
   Unchanged since the search lane wrote it.
2. **Retrieval seed** (`ToneMatcher.RetrievalSeeds`). With a prior, a POOL of
   `RetrievalTopK × PriorRetrievalPoolFactor` (4 × 6 = 24) library rows is pulled
   and re-ranked by `distance × (1 − PriorFamilyWeight × overlap)`, where overlap
   is the **Jaccard similarity of module-type sets** with the prior's chain; the
   top k of that is the shortlist. Jaccard rather than "how many of the prior's
   modules are present", because the latter scores a twelve-module kitchen sink
   that happens to contain the prior's four as a PERFECT match. The bias is
   MULTIPLICATIVE because the feature distance has no fixed scale — an additive
   bonus would mean something different for every extractor.
   **The shortlist never grows and the render budget does not move**: this changes
   WHICH rows are rendered, never how many (`SteeringDoesNotGrowTheShortlistOrTheRenderCount`).
3. **CMA-ES seed.** The prior's own candidate is refined from the PRIOR'S KNOB
   VECTOR at `PriorInitialSigma` (0.40) rather than `InitialSigma` (0.25). Wider
   deliberately: a retrieval hit was MEASURED against this target and its knobs
   already sit near a local optimum, while a prior is a guess from a model that
   never heard the recording. Its chain is the valuable part; its knobs deserve
   more exploration. The run says which candidate was seeded and at what sigma,
   in `ToneMatchResult.Notes`.

**A confident, wrong prior costs candidate slots and never a wrong answer** —
every shortlisted row is still rendered and scored on audio.

### Origins

`ToneMatchOrigins` (in `ToneMatcher.cs`) is the vocabulary, so a UI can say where
a winner came from without parsing free text:

| Origin | Meaning |
|---|---|
| `prior:ai:claude` | the model's own rig, unrefined |
| `prior:ai:claude+cmaes` | …refined, and it beat its start point |
| `retrieval` | a library row from the UNBIASED top-k |
| `retrieval:prior-seeded` | a library row the prior pulled into the shortlist — the visible evidence that it steered |
| `estimator` | the trained ONNX estimator's guess |

Read them with `ToneMatchOrigins.IsPrior` / `IsPriorRig` / `WasRefined`, never by
comparing whole strings: the prior's source tag is part of the origin.

## The narrator

`ToneNarrator` does **not** go through `IToneDesignService`, and that is the one
design decision in it. That service's system prompt is the whole module
catalogue plus four few-shots — grounding for EMITTING a chain, and pure cost for
describing one that is already decided, charged per "Explain" the player clicks.
So: own small prompt, own two-field schema (`{name, rationale}`, ≤ 12-word name,
≤ 80-word rationale), the app's language taken from the configured culture, and
the same `AiProviderResolution` the designer uses.

The rig goes into the prompt through `DcRigPresetDescriber`, the same renderer
the prior lane uses everywhere else, so the model is never shown two different
accounts of one board.

**A refusal is not a narration.** Unparseable JSON, a missing or blank `name`, an
array instead of an object — all answer null at Warning. A narration whose text
is an apology is worse than no narration. An over-long name or rationale is cut
at a WORD boundary with an ellipsis rather than trusted.

## The describer, and the UI's third copy

`ToneDesignViewModel.ChainSummary` builds the same arrow-joined chain from the
LIVE ENGINE's display names; `DcRigPresetDescriber.Chain` builds it from
`AiGearNotes`' glossary and needs no open engine. **This lane deliberately did not
touch the UI.** Replacing `ChainSummary` with the describer is a follow-up for
the UI lane, and the only thing to check first is whether the glossary's names
read as well in the panel as the engine's do.

## DI

`DotCharter.UI/App.MatchServices.cs`, at the end under `// prior lane`. Both are
singletons: they hold a func and a factory, own nothing unmanaged, and implement
neither `IDisposable` nor `IAsyncDisposable` — so neither can be the async-only
singleton that makes `ServiceProvider.Dispose()` throw. The models the narrator
mints are per-call and disposed in a `finally`, which is what keeps the
OpenAI-compatible provider's `HttpClient` from being a transient `IDisposable`
rooted for the life of the process (`ToneNarratorTests.TheModelIsDisposedAfterTheCall`).

> **Known red on the prior lane's own branch:**
> `ServiceRegistrationReachabilityTests` fails with `IToneMatchPriorSource` and
> `IToneNarrator` listed — a registration nothing RESOLVES is exactly the
> `CoopRigApplier` shape that lint exists to catch, and it has no "another lane
> owns the consumer" allow-list. The consumers are the sections lane's
> `IToneSetMatcher` (which asks for a prior per section) and the UI lane's panel
> (which asks for a narration on Explain). It goes green when either lands; it
> must NOT be silenced with an `Exclusions` entry.

## Tests

`DotCharter.Tests/Ai/Prior/` — 33 cases, all in the default gate, no engine, no
DLL, no audio device.

| File | Pins |
|---|---|
| `PriorFakes.cs` | the design service, the model and the factory, all CAPTURING what they were asked |
| `ToneDesignPriorSourceTests.cs` | the request text (label, m:ss span, stem), the bass rule, the source tag, every fault ⇒ null, cancel ⇒ throw |
| `ToneNarratorTests.cs` | the prompt names every module and the section, units are real, the language rule, parse, refusals ⇒ null, the model is disposed |
| `PriorSteeringTests.cs` | the three mechanisms above, over the search lane's fakes in `DotCharter.Tests/Ai/Search/SearchFakes.cs` |

The steering tests build a 24-row library of two chain shapes (`eq`, and
`overdrive → delay`) — exactly one default pool — so "was the row in the pool" is
never the reason an assertion passes. The target is a render of an `eq` row, and
the test ASSERTS the precondition that the unbiased top-k is all-`eq` before
asking what the prior changed.
