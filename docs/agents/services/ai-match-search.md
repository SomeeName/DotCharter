# Match a tone from audio — the SEARCH lane

`DotCharter.Services/Ai/Match/Search/**` — the `IToneMatcher` implementation:
**retrieval over a pre-rendered library, then gradient-free knob refinement over
the real renderer with a content-invariant loss.** No training, no labels. It is
ST-ITO ([arXiv 2410.21233](https://arxiv.org/abs/2410.21233)) transplanted onto
our chain, with Okita 2026's "predict the types, then search the parameters"
([arXiv 2604.22276](https://arxiv.org/abs/2604.22276)) as the optional estimator
arm.

The contract this lane codes against is
`DotCharter.Services/Ai/Match/MatchContracts.cs` (`IRigRenderer`,
`IToneFeatureExtractor`, `IToneSimilarity`, `IRigSampler`, `IRigEstimator`,
`IToneMatcher`) — five lanes share it and it is **additive only**. This lane
implements `IToneMatcher` and NOTHING else; every other interface is someone
else's and is faked in tests.

## Files

| File | Purpose |
|---|---|
| `KnobVector.cs` | `KnobCoordinate` + `KnobLayout`: the bijection between a fixed-chain `DcRigPreset` and a point in `[0,1]^n`. |
| `KnobParamKinds.cs` | Which params are searchable and which coordinates are log-scaled. Mirrors the UI's knob taper. |
| `RigLibrary.cs` | Queries the retrieval library (K rigs rendered over one DI medley, embedded) and reads / writes its **append-only v3 file**; a library may be PARTIAL (`Count` < `TargetCount`). The cache key includes the DI's fingerprint. |
| `RigLibraryBuilder.cs` | Grows one library FILE towards K a row at a time, resumable at the last complete row; row `i` is always the `i`-th draw from `Random(Seed)`. What the app's cache and the SHIPPED library are both built with. |
| `RigLibraryProgress.cs` | The status lines ("Building tone library: 12/96 · ~3 min left") and the ETA estimator. |
| `ISynchronousRigRenderer.cs` | A render on the CALLER's thread — `RigRenderer` implements it — so the background grower's BelowNormal priority reaches the DSP. |
| `RigPlausibility.cs` | How far a rig is from the distribution `RigSampler` draws from, in `[0,1]`. Derived from the sampler's own tables; folded into the ranking. |
| `RigLibraryProvider.cs` | `IRigLibraryProvider` — hands the matcher a library on FIRST USE, never in a constructor: the SHIPPED one if installed, else the local cache grown small-first (96 rows) and then on a BelowNormal thread; `PauseGrowth()` holds that thread for a match. `FixedRigLibraryProvider` for tests. |
| `CmaEs.cs` | A dependency-free CMA-ES over `[0,1]^n` with bound repair, a budget in EVALUATIONS, deterministic for a seed. |
| `MultiScaleSpectralDistance.cs` | The small raw-audio term beside the feature distance. Time-AVERAGED, not frame-wise. |
| `ToneMatcher.cs` | The pipeline: prior → retrieval → refinement → estimator, a render budget, the self-distance floor. |
| `RigCatalogValidation.cs` | The CATALOG GATE: only modules `DcRigTargetCatalog` declares, only knob values inside its ranges. Rejects and NAMES; never clamps, never silently drops. |
| `../Similarity/MatchTargetAudio.cs` | Cuts the silent head and tail off the TARGET before anything measures it, and says how much it cut. |
| `MatchResultStore.cs` | Writes a finished match's FEATURE VECTORS and per-block breakdown to `%LOCALAPPDATA%\DotCharter\ai-match\results\`, bounded to 40 files. |
| `../Similarity/MatchScale.cs` | **The SCORE**: the two anchors, the formula, the five UNRATED states, the per-block breakdown. |

## The algorithm

0. **Target preparation (2026-09-16).** The silent head and tail of
   `request.Target` are cut off (`MatchTargetAudio.Trim`) and the amount is put in
   the notes. **A window of SONG is not a window of PLAYING, and every stage below
   this assumed it was.** The panel picks a span and hands the isolated stem for it
   straight here; on the owner's own project the separated guitar for that span is
   digital silence for its first ten seconds and −70 dBFS for five more — half the
   clip (measured 2026-09-16 on `stems/guitar.wav`, 5 s blocks: −180, −93.5, −17.3,
   −16.0, −70.7, −30.2 dBFS). Only the extractor's SPECTRAL blocks gate silent
   frames; its dynamics and modulation blocks average the whole buffer, and the
   floor below slices the raw head — which embedded as the ALL-ZERO vector, so the
   floor was the distance between nothing and something and every candidate read
   100 %. Trimming is reported, never silent. An all-silent target is not refused
   HERE (a short span with no floor is a legal answer, `ToneMatcherTests` pins it);
   the panel refuses it, with `MatchTargetAudio.IsMeasurable`, so the two cannot
   disagree about what "has playing in it" means.
1. **Features + floor.** The target is embedded once. The **self-distance floor**
   is the distance between the target's first and last 5 s window, measured with
   the SAME metric — the number a perfect match would score. A UI that reports a
   distance without it reports a number with no scale.
   **A span that cannot hold two DISJOINT windows has NO floor and the matcher
   returns `double.NaN`** (2026-09-12). It returned `0.0`, which is a legal floor
   value meaning "perfectly self-similar" — the wrong answer for a right-looking
   reason — and `ToneDesignViewModel.MatchPercent` reached its fallback for a NaN
   only by accident (`floor > 0` is false for NaN); the check is explicit now.
   **The floor is weak even when it exists**: on a 12 s span with a 5 s window the
   two windows cover 10 of the 12 seconds, and on eight of twelve rows of the
   first end-to-end eval the reported floor sat ABOVE every candidate's distance.
2. **Prior.** If the caller passed one (the text model's rig, the user's own
   board), it is candidate zero, origin `prior:<source>` — and it does two more
   things: it RE-RANKS retrieval (step 3) and it seeds CMA-ES with a wider sigma
   (step 6). **Where a prior comes from, and all three mechanisms in detail, are
   in [ai-match-prior.md](ai-match-prior.md); this file is the search side of
   them.** All three are inert when `ToneMatchRequest.Prior` is null, and
   `PriorSteeringTests.NoPrior_ShortlistIsThePlainTopK` pins that.
3. **Retrieval.** Top-k of the library by feature distance, origin `retrieval`.
   A full scan; see "no ANN" below. **With a prior**, a pool of
   `TopK × PriorRetrievalPoolFactor` rows is pulled and re-ranked by
   `distance × (1 − PriorFamilyWeight × jaccard(families))`; a row that reaches
   the shortlist only that way is marked `retrieval:prior-seeded`. The shortlist
   length and the render budget do not move — this changes WHICH rows are
   rendered, never how many. The origin vocabulary is `ToneMatchOrigins`.
4. **Estimator.** If an `IRigEstimator` is registered and `IsAvailable`, one more
   candidate, origin `estimator`. A model that throws is a NOTE, never a lost
   match.
5. **Score.** Every candidate is rendered over the caller's reference DI once and
   scored: `IToneSimilarity.Distance(features(render), targetFeatures)`, plus
   `SpectralWeight` × `MultiScaleSpectralDistance` on the raw audio **only when
   `ToneMatchRequest.TargetIsRender` is true** — see "the raw-audio term" below.
   That is the MEASUREMENT, and it is what `ToneMatchCandidate.Distance` carries.
   **Every candidate is also SCORED here** (W5b) —
   `ToneMatchCandidate.Score`, a percentage against two measured anchors plus the
   per-block breakdown. See "The match PERCENTAGE" below; the 0 % anchor costs one
   render, taken before the seeds.
   **What the list is ORDERED by is `RankingDistance`**:
   `Distance × (1 + PlausibilityWeight × RigPlausibility.Penalty(rig, stemHint))`.
   See "the plausibility prior" below.
6. **Refine.** The best `RefineCandidates` get CMA-ES over their `KnobLayout`,
   sharing what is left of the render budget equally. The PRIOR's own candidate
   starts from the prior's knob vector at `PriorInitialSigma` (0.40) rather than
   `InitialSigma` (0.25): a retrieval hit was measured against this target and
   sits near a local optimum, a guess from a model that never heard the recording
   does not. A refinement that does not
   BEAT its start point is discarded and noted — that is how "a prior that is
   already right is not made worse" is enforced, structurally rather than by
   hoping.
7. **Return.** Candidates sorted by `RankingDistance`, capped at
   `request.Candidates`, with the floor, the elapsed time and the notes — which
   include the effective PLAN (`plan: 4 seeds, 2 refinement(s) [21D λ8×2gen], cap
   48 renders / 150 s`) and any plausibility mark-downs.
   **Two consumers re-sorted this list by `Distance` and would have undone the
   prior silently** (`ToneDesignViewModel.PublishCandidates`,
   `ToneSectionRowViewModel.PublishCandidates`); both order by `RankingDistance`
   now. A new consumer must too.

Cancellation is checked before every render and inside the optimiser's
evaluation loop.

## The match PERCENTAGE — what it measures, and against what

`DotCharter.Services/Ai/Match/Similarity/MatchScale.cs`. Every candidate carries a
`MatchScore`; `ToneDesignViewModel` and `ToneSectionRowViewModel` only DISPLAY it.

```
percent = 100 * clamp(1 - distance / referenceDistance, 0, 1)
```

**What goes into `distance`.** `ToneSimilarity` over `tonefx-v2`: a weighted,
block-normalised L2 over the 357-element feature vector. Ten blocks —
`bandMean` 0.5, `bandStd` 0.05, `mfccMean` 0.5, `mfccVar` 0.1, `shape` 0.5,
`shapeVar` 0.05, `dynamics` 1.2, `distortion` 0.6, `modulation` 0.6, `level` 0.15
(`ToneFeatureLayout.Blocks`, the ONE place the layout and the weights live). Each
block contributes `weight x mean(squared difference over its elements)` and the
total is the square root of the sum; dividing by the block LENGTH is what stops
the 144-element band block outvoting the 4-element distortion block by arithmetic
rather than by importance. Both sides come from the same extractor and
`Distance` refuses two vectors that do not.

**The two anchors, both MEASURED on this run, on this target.**

| End | What it is | Why |
|---|---|---|
| **0 %** | `MatchScale.ReferenceRig()` — an information-free `overdrive -> amp -> cab` at catalogue defaults — rendered over the caller's own DI and scored against the same target. One render, taken FIRST, out of the same budget. | "No better than plugging into an amp." An EMPTY rig (the dry DI) would be a worse anchor: a dry electric is so far from any produced record that almost anything beats it, and every score would be high for the same reason the old formula made every score 100. |
| **100 %** | `distance == 0`. | In this metric that IS a perfect match: the candidate's feature vector is the target's. Nothing between the ends is invented. |

**What it replaced, and why the old one could not be rescued.** It was
`100 x floor / max(distance, floor)` in `ToneDesignViewModel.MatchPercent`, where
`floor` was the target's SELF-DISTANCE (first 5 s against last 5 s). That
expression saturates the moment a candidate's distance falls below the floor, and
on real material it routinely does. Measured 2026-09-16 on the owner's Polkadot
Stingray track, 90-120 s of the separated guitar (the span the tone-change-free
fallback picks from a playhead inside the guitar part):

| Candidate | Distance | OLD % | NEW % |
|---|---:|---:|---:|
| `retrieval+cmaes` `noisereducer->twelvestring->ds1->amp->cab->chorus` | 0.3793 | **100** | **46** |
| `retrieval` (same chain, unrefined) | 0.4222 | **100** | **39** |
| `retrieval` `noisereducer->ds1->amp->cab->eq->bbddelay` | 0.4729 | 93 | **32** |
| `retrieval` `amp->cab->tzflanger` | 0.4850 | 90 | **30** |
| `retrieval` `overdrive->amp->cab->eq->ensemble->platereverb` | 0.4891 | 90 | **30** |

self-distance 0.4389, reference distance 0.6966.

**The self-distance is not even ORDERED correctly.** On a target that IS a render
of a known rig through this pipeline: self **0.2934** against a reference distance
of **0.1309** — the information-free rig is more than twice as close to the target
as the target's own two windows are to each other, because those windows differ in
CONTENT (the DI medley is four different performances) and content is what the
metric is built not to hear. A scale with that as its good end runs backwards. It
is REPORTED instead, as `MatchScore.SelfDistancePercent` (37 % on the Polkadot
row) plus `WithinTargetNoise` — the caveat that a candidate is as close as this
target's own variation and the metric cannot separate it further.

**Calibration, both ends, one measurement** (`MatchScoreCalibrationTests`, against
the committed real feature vectors): a known rig against its own render **100 %**
(distance 0.0000); a deliberately wrong rig, `acpre` alone, **0 %** (distance
0.6446, worse than the reference's 0.1309); the reference rig against itself
**exactly 0 %** by construction.

**No headline number without the breakdown.** `MatchScore.Blocks` carries every
block's weighted contribution, its SHARE of the total, and the SAME block's
contribution for the reference rig — so 46 % reads as "dynamics is 41 % of what is
left, and it is still worse than the generic rig's 0.3755".
`BlocksWorseThanReference()` is the honest "what did the search not get" list.

**UNRATED is a third state, not a zero** (`MatchUnrated`): `no-reference` (the
budget was gone before the anchor render), `reference-too-close` (the reference rig
is already within `MinAnchorSpread` = 0.02 of the target, so the scale is a point),
`target-too-short` (under `MatchTargetAudio.MinContent`), `block-gated` (a weighted
block of the TARGET is all zeros — the shape the extractor answers for silence) and
`no-distance`. `ToneMatchCandidate.Score == null` is a FOURTH thing: the producer
did not score at all.

**Reproducible by construction.** A score is a pure function of three vectors, so
it cannot depend on the render budget, the wall clock, how many candidates were
tried or the CMA-ES seed. The anchor render is taken BEFORE the seeds for exactly
that reason: a tighter budget changes WHICH candidates exist, never what a given
one scores. Pinned by `MatchScoreCalibrationTests
.TheScoreDoesNotDependOnHowManyCandidatesTheRunAfforded`.

### Where the vectors go

`MatchResultStore` writes one JSON per match to
`%LOCALAPPDATA%\DotCharter\ai-match\results\match-<yyyyMMdd-HHmmssfff>-<n>.json`:
the extractor id, both anchors, the notes, the TARGET vector, the REFERENCE
vector, and per candidate its origin, distances, chain, percentage (or unrated
reason), per-block breakdown and its own vector. **Bounded at 40 files**, oldest
first — an unbounded diagnostic folder is a disk leak with a good excuse. A store
that cannot write adds a note and the match is unaffected; the path comes back on
`ToneMatchResult.ArtifactPath`.

That format IS the calibration fixture format: the two files under
`DotCharter.Tests/Ai/Search/Fixtures/` are real artefacts captured from the real
pipeline, so the default gate re-runs the scorer against real vectors with no
audio and no engine.

## The plausibility prior

**The first end-to-end measurement (2026-09-12) had the search BEATING the
authored Rocksmith rig on 12 of 12 charted rows with `octaver→klon→tapesat` — no
amp, no cab — as top-1 for a Clean tone, a Chorus tone and an Octave_Distortion
tone alike.** The search was doing its job. `tonefx-v2`'s measured content
invariance is only 1.56×, so a chain that happens to land the stem's long-term
spectrum can out-score the chain that is physically the right answer, and a
ranking loss that knows nothing about what a rig IS cannot prefer the second.

`RigPlausibility.Penalty(rig, stemHint)` returns `[0,1]` — 0 for a board a player
would have built. **Every rule reads one of `RigSampler`'s own structures rather
than restating it**: `TrueAmps`, `PreampTypeAmps` (its `DiTypeAmps`),
`FamilyOfTypeId`, `SamplableTypeIds`, `BassModuleTypeIds`, `LengthPriorSupport`,
`MaxSampledDrives`, `CabDrawer` — five of them exposed read-only for this. A
second hand-typed list would rot the day an archetype changed, and the rot would
be silent: the penalty would go on producing plausible numbers.

| Fault | Weight | Why the sampler argues against it |
|---|---:|---|
| `no-amp-no-cab` | 0.85 | every archetype but `direct` draws an amp with p ≥ 0.62, and even `direct` reaches for a cab sim |
| `no-amp` (cab present) | 0.35 | a real shape — the sampler draws it at `PCab × 0.45` when the amp slot came up empty |
| `no-cab` (speaker amp) | 0.30 | `PCab` ≥ 0.76 in every archetype |
| `guitar-amp-on-bass` | 0.40 | the `bass` archetype's amp pool is `bassamp`/`poweramp` and nothing else |
| `no-bass-gear` | 0.25 | a bass part with no bass-voiced module anywhere |
| `unsamplable` | 0.50 | `Excluded` / series-excluded / outside the vocabulary — reachable only through a prior or an estimator |
| `two-amps` | 0.50 | the sampler's "never two amps" rule |
| `mic-without-cab` | 0.25 | the sampler deletes `micpos` whenever the trim drops the cab |
| `cab-before-amp` | 0.30 | the one adjacent swap `IsAmpCabSeam` forbids |
| `duplicate-family` | 0.12 each, capped 0.25 | deliberately SMALL — real authored RS rigs carry a `graphiceq` AND an `eq` |
| `over-length` | 0.12 per module past 8 | `LengthWeights` has no mass past 8 |

**`PlausibilityWeight` = 0.6 is calibrated, not round.** The gamed chain scored
0.4909 against the authored rig's 0.6953 — a 1.42× gap — so a 0.85 penalty at
0.6 is a 1.51× multiplier: enough to flip exactly that ranking, and no more. A
rig that really is 2× closer still wins.

**It is a PRIOR, not a filter**, and it never touches the MEASUREMENT. Nothing is
removed from the shortlist; `Distance` stays the measured audio distance, which
is what shares a scale with `TargetSelfDistanceFloor` and what every UI
percentage and eval table rests on. CMA-ES minimises the RAW distance: within
one refinement the structure — and therefore the penalty — is fixed, so a
constant scale cannot move the optimum and "a refinement that did not beat its
start point is discarded" keeps comparing two numbers in one space. **The
optimiser cannot remove an amp; retrieval, the prior's own rig and the
estimator's guess all go through one `Candidate` factory and are all scored
through it.**

`UsePlausibility: false` reproduces the pre-2026-09-12 ranking exactly, which is
what an A/B is run with.

**Measured over the shipping `mixed` profile, 1,000 draws:** 540 score zero; the
other 460 are 280 `no-amp-no-cab`, 102 `no-cab`, 75 `no-amp`, 12
`duplicate-family`, and **ZERO structural faults**. Per archetype: crunch 84 %,
highgain 88 %, clean 79 %, bass 71 %, ambient 60 %, **direct 14 %** clean. That
is the prior tracking the archetype rather than rejecting the library — `direct`
IS the amp-less archetype and is what the search kept winning with. Re-measure
(`RigPlausibilityTests`) rather than carrying these forward.

**A `null` stem hint is treated as `guitar`**: the panel's match does not set one
and its target is always a played guitar part, so defaulting to "no instrument
expectation" would switch the feature's main path off. `piano` and `other` are
scored on structure alone.

## The catalog gate

Rocksmith gear is a CLOSED CATALOG and so is this app's DSP module set. Every seed
is checked against `DcRigTargetCatalog` **before a render is spent on it**, and a
rig that fails is **rejected and named** in the notes
(`rejected prior:ai:claude: module 2 is 'klon-centaur-gold', which is not in the
DSP module catalogue`) — never clamped into range, never dropped without a word.
Four fault shapes: an unknown module type, a param the module does not have, a
non-finite value (a NaN compares false against BOTH bounds, so it needs its own
check) and a value outside the declared `[Min, Max]`.

**Retrieval cannot trip it, and that is the point.** Every library row is a
`RigSampler` draw built out of the catalogue's own specs — measured 2026-09-16 over
the shipped 4,000-row library: **0 unknown module types, 0 unknown params, 0
out-of-range values**, and 572 sampled rows applied to a real device-less engine
with **0 failed ops and 0 short chains**. What CAN fail is what did not come from
the sampler: the caller's **prior** (a language model's guess, a user's own board,
a tone file from another install) and the **estimator**'s decoded chain, and
neither was checked anywhere before it was rendered, scored and offered as a tone.

`MatchAsync` also asserts the POST-CONDITION over what it returns and THROWS on a
fault there — by that point a bad rig is our bug (a decode that escaped a range, a
new arm that skipped the gate), not a caller's. `ToneSetMatcher` inherits the gate
because it delegates to the real matcher; `ToneSetMatcherCatalogGateTests` pins
that it is still the real one, because running the set matcher over a fake matcher
would make the whole claim vacuous.

## The raw-audio term, and when it is valid

`SpectralWeight × MultiScaleSpectralDistance(wet, target)` is added **only when
`ToneMatchRequest.TargetIsRender` is true** (init-only, default FALSE — the stem
case). The loss is sample-aligned and its own test,
`ToneSimilarityTests.MultiScaleSpectral_SeparatesRigsOnOneTakeAndIsMeaninglessAcrossTakes`,
demonstrates the failure directly: across takes it rates the same rig on another
riff as WORSE than a different rig on the same riff. Against a separated stem of
a record nobody played into this app it is noise with a weight on it, and **it
was on for every row of the first end-to-end eval.**

The FLOOR is gated on the same flag. It is the scale a candidate's distance is
read against, so the two must carry the term on exactly the same runs.

True only where the caller rendered the target itself: `RealRendererMatchEvalTests`,
`ToneMatcherTests`' whole fixture. The panel, `ToneSetMatcher` and the corpus eval
are all the stem case and leave it false.

## The knob vector

One coordinate per **continuous** param of every module in the chain, in chain
order then catalogue param order, from `DcRigTargetCatalog`'s ranges. A param the
preset omits reads as the catalogue DEFAULT, so encoding a sparse preset and
decoding it back renders the same audio.

**Not in the vector, by design:**

- **Structure** (which modules, in what order) — retrieval's job. CMA-ES over a
  fixed chain is exactly ST-ITO's split.
- **Discrete params** — NAMED (`overdrive.model`) and ORDINAL
  (`phaser.stages`, a count the core `lround`s). A param the core rounds makes
  the loss a STAIRCASE: zero gradient inside a step, a full-scale discontinuity
  at every edge, which is the shape that makes a covariance estimate diverge.
  The library carries one entry per discrete combination that was SAMPLED, so
  the choice is still searched — just not by the optimiser.
- **Strings** (a `namamp` capture, a `cab` IR), branch tags, sidechain keys,
  bypass flags — identity, not knobs. All carried through `Decode` from the
  template untouched, which is what makes the round trip lossless.

**Discreteness is a curated id list** (`KnobParamKinds.DiscreteIds`) plus an
integrality rule (min, max and default all integral, range non-degenerate) —
that is what separates `curve` 0..3 (a named curve) from `curve` 0..1 default
0.5 (a morph) with no per-module table. It has to be curated because
`DcRigTargetCatalog` is a RANGE-ONLY mirror: the native `valueLabels` / `step`
metadata (`DspParamInfo.IsStepped`) exists only on a machine with the DLL, and
this type must work offline in a test with no engine.
`KnobVectorTests.EveryCuratedDiscreteId_IsARealCatalogueParam` fails if an id
rots out of the catalogue, and `KnobParamKinds.IsDiscreteOverride` lets a caller
holding live metadata replace the judgement wholesale.

**Frequencies are log-scaled**, mirroring `DotCharter.UI/Controls/Dsp/KnobMath
.TaperFor`: an id ending in `Hz`/`KHz`, or any range spanning ≥ 2 decades. It is
a MIRROR and not a call because Services may never reference UI (the layering
test enforces that); `KnobVectorTests.TaperRule_MatchesTheUiKnobTaper` pins the
two together. This is not cosmetic: on a linear coordinate, `svf.cutoffHz`
20–20,000 puts every guitar fundamental in the bottom 10 % of the axis, so a
CMA-ES step small enough to tune the presence region is smaller than the
resolution of the whole usable band.

## The library

K rigs from `IRigSampler` (default **4,000**, seed **20260912**), each rendered
once over a fixed **20 s DI medley the caller supplies**, embedded with ONE
`IToneFeatureExtractor`, kept as a plain array.

- **Build cost is reported in RENDERS** (`RigLibrary.RenderCount`), not seconds:
  a render's cost depends on the rig, so a second count means different things on
  two machines. K = 4,000 at 20 s ≈ 22 h of audio — a one-off of a few tens of
  minutes, paid ONCE per machine.
- **Cache:** `%LOCALAPPDATA%\DotCharter\ai-match\library-<extractorId>-<K>-<seed>-<di>.bin`,
  written atomically (temp + move). The key is in the FILE NAME *and* re-checked
  in the header, because features from two different extractors are two unrelated
  spaces and comparing across them produces plausible numbers and wrong rigs.
  A missing / short / garbage / wrong-key file is a cache MISS, never a throw;
  a library that cannot be written is still a usable library.
- **`<di>` is `RigLibrary.Fingerprint(medley)`** — SHA-256 over the sample rate
  and the raw sample bytes, first 8 hex digits — and it joined the key on
  2026-09-12 (header version 2, so every older file on disk is a miss). **A
  library IS its DI**: every row is "this medley through that rig", so one built
  over another dry take describes a different signal and every distance is
  between two unrelated things, with entirely plausible numbers. Over the SAMPLES
  and not a path or a length, because two takes can be the same duration at the
  same rate and the generated medley has no path at all. `LoadOrBuildAsync`
  derives it from the medley it is already given, so no caller has to remember.
- **No ANN index, deliberately.** At K = 4,000 and a few hundred floats a full
  scan is ~1 M multiply-adds — under a millisecond, invisible next to one render.
  An ANN library would add a dependency, a build step and an approximation to the
  one stage that is currently exact. Revisit at K ≥ 10⁵.

### The library GROWS, and the first match never waits for it

**"A one-off of a few tens of minutes" above was wrong by an order of
magnitude**: 4,000 renders at the measured 2.55 s (Release; 1.7–1.9 s on an
idle box at BelowNormal) is **~2.8 hours**, and until v0.55.0 every one of them
ran in front of the FIRST match with "Building tone library: 1/4000 rigs
rendered" as the only feedback. The owner saw that line and gave up. Three
things replace it, and each has its own guard:

- **The file is append-only (format v3).** Header = magic, version, extractor
  id, K, seed, DI fingerprint; then rows, each `[rig json][dim][floats][ROW!]`,
  and NO row count in the header. `TryLoad` takes every complete row and DROPS
  a torn last one (a crash mid-write), answering the byte offset of the last
  good row so an appender truncates to it. **A v2 file for the same key is
  IMPORTED once, not missed** (`RigLibraryBuilder.TryImportLegacyAsync`, called
  by the provider when the v3 file has no rows): the directory is scanned for
  `library-<extractor>-*-<seed>-<fp>.bin` — any K, so the owner's eval-era
  `-96-20260912-5e4b70d7.bin` qualifies — and the largest v2 file whose header
  matches is adopted if every stored rig equals its re-draw from
  `Random(Seed)` (structure, free) AND the first / middle / last rows
  re-render to their stored features through the live renderer (audio, three
  renders: same DI is the fingerprint's claim, same engine and extractor is
  this one's). Refusals are logged with the reason; un-fingerprinted v1 files
  never match the pattern. A
  library therefore has `Count` (rows here) and `TargetCount` (K), and
  `IsComplete` when they meet. `RigLibraryBuilder` appends one row per render
  with a flush-to-disk, and on open RESUMES: it re-draws the stored rows from
  `Random(Seed)` to advance the generator and checks the LAST re-drawn rig
  against the stored one — a file that does not re-draw (a changed sampler or
  catalogue) is started over, never appended to. **Row `i` is the `i`-th draw
  whoever renders it**, so a resumed file, a one-shot build and the shipped
  library are the same rows in the same order
  (`RigLibraryGrowthTests.ResumingContinuesAtTheLastCompleteRow_AndIsTheSameLibraryAsOneShot`
  compares them rig by rig).
- **`RigLibraryProvider` is small-first, then background.** With no library
  for the DI it grows the cache file to `RigLibraryOptions.FirstMatchCount`
  (**96**, ~4 min) in front of the caller — cancellable between renders, rows
  kept — runs the match on that, and then grows the same file towards K on its
  own `Thread` at `ThreadPriority.BelowNormal`, rendering THERE through
  `ISynchronousRigRenderer` (a pool thread cannot be lowered from outside). A
  later match snapshots whatever the grower has reached; an app restart resumes
  at the last row. The provider is `IDisposable` (never `IAsyncDisposable` — the
  container-dispose trap) and stops the thread. **`PauseGrowth()` is taken by
  `ToneMatcher.MatchAsync` for the whole search**: there is one device-less
  engine and its renders serialise, so a library row in flight would queue in
  front of every one of the match's 48 renders.
- **The SHIPPED library wins whenever it is installed.** The bundled DI is
  generated, the sampler is seeded and the extractor is deterministic, so the
  4,000-row library for that DI is the same file on every machine. It is
  rendered once with `tools/rig-dataset build-library` (same builder, same
  rows), published as `rig-library-v1.zip` beside the estimator model
  (`RigEstimatorModelCatalog.Library`, installed by the same downloader into
  `ai-match\rig-library\`), and the provider re-reads the installed path on
  EVERY match, so a download from Settings ▸ AI mid-session replaces the partial
  cache on the next match and stops the grower. A shipped file for another DI,
  or an incomplete one, is ignored by the header check.

**Status lines carry progress AND an estimate** (`RigLibraryProgress`):
"Building tone library: 12/96 · ~3 min left" (an average over the renders so
far — never an EMA, a reverb tank costs more than a gain stage and a short
memory swings the ETA by minutes), no clause before the first render or on the
last row, then "Tone library: 96/4000 rigs (growing in the background)" once so
a weak early result is not read as the feature's ceiling.
`RigLibraryProgress.TryParseBuilding` is the formatter's INVERSE: the panel's
progress bar reads its fraction back out of the line, because the matcher's
`IProgress<string>` is the additive-only contract and a second channel for one
number was not worth a contract change. `RigLibraryProgressTests` pins the two
as inverses.

**In the app the library's medley is ALWAYS `ReferenceDi.Medley()`**
(`App.MatchServices.cs`); a player's own recorded DI is what the candidates are
SCORED over (`ToneMatchRequest.ReferenceDi`), not what the library is rendered
over. The provider takes any medley and keys the file on its fingerprint, so
that could change without touching it.

## CMA-ES

Standard Hansen formulation (weighted recombination, rank-one + rank-μ
covariance, cumulative step-size adaptation) over a Jacobi eigensolver, ~330
lines, no dependency.

- **Bounds by REPAIR**: the sampled point is clamped into the box before
  evaluation AND the clamped point enters the recombination. Evaluating clamped
  while updating unclamped lets the mean walk arbitrarily far outside the box
  while every evaluation sees the same face of it.
- **The budget is EVALUATIONS** and a generation is never left half-evaluated:
  the loop stops at the generation boundary that would exceed the budget, so the
  covariance is never updated from a partial population.
- A **NaN** objective (a render that came out silent) sorts LAST rather than
  becoming the best.
- **Deterministic for a seed**, including the whole matcher above it.

Measured 2026-09-12 (Debug, single core, LOW priority, in this worktree):

| Benchmark | Reaches 1e-3 | Best at budget |
|---|---|---|
| Sphere, 6-D, start 0.8 | **387 evaluations** | 1.07e-20 at 2,000 |
| Rosenbrock, 6-D, start 0.4 | **2,196 evaluations** | 1.46e-28 at 8,000 |
| Sphere, 4-D, `TargetValue` 1e-2 | — | stops at 232 evaluations |

Both benchmarks are mapped onto `[-5,5]^n` by the test, so their optima sit at
0.5 and 0.6 — interior, so a converged run is SEARCHING and not being clamped
onto a face.

## Budget knobs (`ToneMatcherOptions`)

| Knob | Default | Note |
|---|---|---|
| `RetrievalTopK` | 4 | library hits considered |
| `RefineCandidates` | **2** | how many get CMA-ES; at this budget a third halves the generations the first two get, on a rig retrieval already ranked last |
| `MaxRenders` | **48** | the hard cap. `MatchBudget.TryTake` is the ONE place a render is authorised, so an overrun is impossible by construction |
| — | — | **One of those 48 is the SCALE's 0 % anchor** (W5b, 2026-09-16), taken before the seeds. It stays INSIDE the budget deliberately: `TryTake` remains the one place a render is authorised, so the cap keeps meaning every render. `ToneMatcherTests.TheRenderCapIsExact_NotApproximate` reads a cap of 3 as anchor + 2 candidates, and `ToneMatcherBudgetTests`' tight-budget plan moved λ6 → λ5 for the same reason. |
| `Budget` | null -> `ToneMatcher.DefaultWallBudget` = **150 s** | wall clock; whichever bound is hit first ends the search. `request.Budget` overrides |
| `PopulationSize` | **8** | CMA-ES lambda; null = the standard `4 + floor(3*ln n)` |
| `SpectralWeight` | 0.15 | the raw-audio term's weight -- and only applied when `TargetIsRender` |
| `UsePlausibility` / `PlausibilityWeight` | true / 0.6 | see "the plausibility prior" |
| `Seed` | 20260912 | the whole match is deterministic for it |
| `InitialSigma` | 0.25 | smaller than CMA-ES's own default because retrieval already placed the start point |
| `SelfDistanceWindow` | 5 s | the window length for the floor; a span under 2x it has NO floor (NaN) |

An exhausted budget is an ANSWER ("nothing was tried"), not an error: zero
renders, zero candidates, no throw.

### The budget is HONEST now, and that took four changes

It was `MaxRenders` 400 against a 60 s wall. **One evaluation — a render of the
20 s medley plus one `ToneFeatureExtractor` pass — costs a measured 2.55 s in
Release on the owner's box** (6.51 s in Debug; 0.44 s of the Release figure is
the extractor). The wall therefore expired after ~23 renders: the cap was
unreachable, and a reader of `ToneMatcherOptions` believed the search was 17x
deeper than it was.

1. **48 renders = ~122 s of evaluation against a 150 s wall** (~25 % headroom for
   a contended box). The wall default moved out of a constructor literal into
   `ToneMatcher.DefaultWallBudget` so it cannot drift away from the cap's
   justification again.
2. **`PopulationSize` reaches `CmaEsOptions`.** A retrieved rig has ~15-25
   continuous coordinates, so the standard formula gives lambda 12-13 and a
   22-evaluation share buys ONE generation — a random sample around the start
   point, not an adaptation. 8 buys two.
3. **A tight budget SHRINKS lambda instead of skipping.** The old gate was a flat
   `share < 8`, so under pressure the refinement stage silently did nothing and
   the result said nothing about it — which is what forced the first end-to-end
   eval to hand-tune `RetrievalTopK` 3 / `RefineCandidates` 1. Lambda is clamped
   into `[4, share]`; only a share below CMA-ES's own minimum of 4 skips.
4. **The effective plan is in the notes**, so a run that refined nothing says so.

**Re-measure with `RenderCostEvalTests`** (gated on `DOTCHARTER_AUDIO_EVAL=1`,
run in RELEASE — a Debug number is a JIT measurement and must never set a
budget). Its structural half,
`ToneMatcherBudgetTests.TheRenderCapAndTheWallBudget_AgreeAtTheMeasuredCost`,
runs in the default gate and fails if anyone raises the cap without moving the
wall.

## How the estimator plugs in

Register an `IRigEstimator` (the model lane's ONNX inference) and pass it to the
`ToneMatcher` constructor. When `IsAvailable` it contributes ONE more candidate,
origin `estimator`, scored and refined like any other. That is Okita 2026's
predict-then-search, and it is additive: with no estimator the matcher behaves
exactly as it does today. A model that throws adds a note and loses nothing.

## Measured behaviour on the fake (`DotCharter.Tests/Ai/Search`)

The fake renderer is a cheap parametric chain (gain → soft clip with a tone
control → low-pass → feedback delay → three-band EQ) driven by the rig's own knob
values; the fake extractor is 24 LOG-spaced bands of the time-averaged spectrum,
RMS, crest, and 32 envelope-autocorrelation lags. Hidden-rig recovery over an
`eq` chain, 1,000 renders:

- distance **6.7e-2** against a self-distance floor of **6.8e-1** — the match
  reaches the floor with an order of magnitude to spare;
- knob errors: `lowFreq` 0.039, `lowGainDb` 0.043, `midFreq` 0.001,
  `midGainDb` 0.067, `highFreq` 0.056, `highGainDb` 0.043 — mean **0.041** of
  full travel, against Comunità 2021's < 0.05 MAE yardstick for a *trained*
  model with labels.

**Four coordinates are deliberately not asserted, and each is a real property
rather than a concession** — every one of them cost a failed run to find, and
they are the lane's main findings for the eval lane:

1. **Output levels (`overdrive.level`, `eq.outputTrimDb`) are unidentifiable BY
   CONSTRUCTION.** The metric is loudness-invariant on purpose (spectra are
   normalised to unit mean; a rig is loudness-normalised before anyone hears it —
   `DcRigLoudnessNormalizer`). A search that "recovered" a level knob would be
   fitting the one thing the ear is not being asked about. **The matcher's answer
   for level is the loudness normaliser, not the optimiser.**
2. **Bandwidth (`eq.midQ`) needs finer bands than 1/3 octave.** A dip of a given
   depth at Q 1 and at Q 4.7 differ by less than one band edge once folded;
   measured, every other EQ knob landed within 0.07 and `midQ` at 0.48. That is a
   requirement ON THE EXTRACTOR, not a better search.
3. **A long-term average spectrum cannot see a delay's feedback.** Repeats of a
   decaying echo have the same spectrum as the dry signal. Without an
   envelope/modulation branch the search recovered every knob except
   `delay.feedback` while reporting a distance far below the floor — a confident
   match to the wrong tone. The fake extractor grew a 32-lag envelope
   autocorrelation for exactly this, and **the real extractor needs one too**
   ([arXiv 2506.22628](https://arxiv.org/html/2506.22628v1): envelope-DTW,
   ρ = 0.86 with human agreement).
4. **A linear band fold is useless.** At 16 kHz with 24 linear bands each band is
   333 Hz wide, so a whole low shelf lands in band 0 — the search drove
   `lowGainDb` to its MINIMUM while the distance fell. Log bands (mel, 1/3-octave)
   are not a nicety.

Getting to a well-posed test chain took four attempts, and the three rejected
ones are worth knowing before anyone reads a recovery number as a grade:
`gain`→`overdrive` (a pre-gain into a tanh IS the drive knob), `overdrive` at 24×
pre-gain (the top half of the knob is saturated), `overdrive`→`delay` at high
feedback (a short feedback delay is a comb and its loss surface in `timeMs` is
RUGGED — the one thing CMA-ES cannot climb), and `overdrive`→`eq` (the drive's
tone control is a low-pass and the high shelf undoes it). **None was a bug in the
matcher.** The product bar is the AUDIO distance against the floor; per-knob
recovery is a diagnostic, and on a real chain with genuine degeneracies it will
be looser.

## Tests

| Test | What breaks it |
|---|---|
| `KnobVectorTests` (9) | round trip, clamping, discrete params excluded and preserved, identity carried through, log taper == the UI's, curated discrete ids still real, unknown module types |
| `CmaEsTests` (7) | the two benchmarks, determinism per seed, the evaluation cap, early stop on target, cancellation, NaN handling |
| `RigLibraryTests` (8) | a library rig's render retrieves that rig FIRST, build cost in renders, cache round trip, a wrong-key/garbage cache refused, a cross-extractor query rejected, the cache path, the DI fingerprint |
| `RigLibraryGrowthTests` (18) | the v2 import: adopted before anything is rendered (three verification renders, on disk afterwards, the v2 file left alone), refused for another DI in the header, never opened without a fingerprint, refused on structure without a render, refused on the verification renders, the largest file wins and is capped at K. Plus: | the v3 file: a partial loads as partial and a torn last row is dropped; resume continues at the last complete row and IS the one-shot library; a file that does not re-draw from the seed is started over; another key is a fresh file; cancel keeps the rows. The provider: the first match renders the small library ONLY (6 of 40, with ETA lines); a restart finds the rows; growth runs at BelowNormal on the caller-thread path and later matches see it; growth PARKS under a hold; an installed library for the key is used with zero renders, one for another DI is ignored, one installed mid-session is picked up; cancelling the first match keeps its rows. The matcher holds growth for the whole search. |
| `RigLibraryProgressTests` (5 + 9 cases) | the build line's shape, no clause before the first render or on the last row, the duration wording, the other two lines, the estimator's average over an injected clock |
| `ToneMatcherTests` (12) | hidden-rig recovery (audio bar + mean/max knob bars + "refinement beat retrieval"), the floor, an exact prior not degraded, the render cap exact, an expired wall budget, progress strings, cancellation, the three estimator arms, sorting and capping |
| `RigPlausibilityTests` (9) | the authored RS rigs are NOT marked down, the gamed chain loses the ranking it won, one case per fault code, no sampled rig ever trips a STRUCTURAL fault, and the amp/cab penalty tracks the archetype |
| `ToneMatcherPlausibilityTests` (4) | at the same measured distance the amp rig wins; the factor is `Distance × (1 + w × penalty)`; the note names the fault; switched off nothing is scaled |
| `ToneMatcherTargetKindTests` (3) | the raw-audio term is absent for a stem target and present for a render, floor included; the default is the stem case |
| `ToneMatcherBudgetTests` (5) | **with the DEFAULT options refinement RUNS**; the plan note; a tight budget shrinks λ instead of skipping; a budget under λ=4 says so; the cap and the wall agree at 2.55 s/evaluation |
| `MatchScaleTests` (14) | the formula's fixed points (identical = 100, the reference rig = 0, worse than it clamps to 0, halfway = 50), all five unrated states, the self-distance caveat, the breakdown summing to 1 and agreeing with `ToneSimilarity.Explain`, and the reference rig's chain |
| `MatchScoreCalibrationTests` (11) | the two committed real-vector fixtures: every Polkadot candidate under 50 %, the OLD formula reporting 100 for two of the same vectors, the breakdown naming `dynamics`, the known rig at 100 %, the wrong rig at 0 %, the reference at exactly 0 %, the self-distance inversion, and reproducibility |
| `MatchResultStoreTests` (7) | a run leaves its vectors behind and they round-trip; a stored result can be RE-SCORED from its vectors alone; an unrated candidate persists its reason and not a zero; the folder is bounded; a store that cannot write says why and does not throw; another version is refused |
| `RigCatalogValidationTests` (6) | an unknown module, an out-of-range knob, a NaN knob, a param the module has not got — each rejected with a fault naming it; 500 sampler draws all pass; an empty rig is legal and a null is not |
| `ToneMatcherCatalogGateTests` (4) | a prior and an estimator guess outside the catalogue are rejected BY NAME and the search still answers; every returned candidate passes the gate |
| `ToneSetMatcherCatalogGateTests` (1) | the same through the set matcher, over the REAL `ToneMatcher` |
| `MatchTargetTruncationTests` (6) | a silent lead-in is cut and reported; the untrimmed head embeds as the all-zero vector and the trimmed one does not; an all-silent window has no content; **a clip that is all playing is handed back untouched**; a trailing tail is cut; a clip under `MinContent` is not measurable |
| `RealRendererMatchEvalTests` | the same recovery through the REAL renderer — **gated on `DOTCHARTER_AUDIO_EVAL=1`**, `TargetIsRender: true` because it renders its own target |
| `RenderCostEvalTests` | **gated, run in RELEASE** — re-measures render + extract per evaluation, the number every budget default rests on |

All of them were proved non-vacuous by breaking the thing on purpose: dropping
the log taper fails `TaperRule_MatchesTheUiKnobTaper`, removing
`RigLibrary.Query`'s sort fails `ARenderOfALibraryRig_RetrievesThatRigFirst`,
freezing CMA-ES's step-size adaptation fails `Sphere6D_ReachesTheOptimum`, and
disabling `MatchBudget.TryTake` fails both budget tests.

## Evaluating the ESTIMATOR by sound, not by labels (`eval-render`)

The trained estimator is graded by the trainer on labels — family F1, exact
chain match, knob MAE — and the owner's goal is close SOUND, not exact gear.
`tools/rig-dataset`'s `eval-render` subcommand closes that gap: for every row of
a trainer's `predictions-test.jsonl` it renders the PREDICTED rig over the row's
dry DI span through the real offline renderer and measures the distance THIS
lane's metric reports, against the true clip. `tools/rig-model/render_eval.py`
runs it over several run directories and prints one table. The full contract —
flags, the `--target clip|render` split, the measured validation numbers — is in
[docs/agents/testing/rig-dataset.md](../testing/rig-dataset.md).

**It reuses three things from here and that is the point.** The renderer
(`RigRenderer` over a `NullDeviceRigHost`), the metric (`ToneSimilarity` over
`tonefx-v2`, level-matched on both sides), and the SELF-FLOOR — the target
against a disjoint window of itself, the same construction
`ToneMatcher.SelfDistance` uses, NaN and never 0 when the span is too short.
**The floor normalisation is what makes rows comparable at all**: a raw distance
depends on the riff, the DI and the separation bleed, so `distance / selfFloor`
is the only column that can be averaged across a test split. A fixed
`overdrive → amp → cab` at catalogue defaults — `AudioEvalTests`' `floor-generic`
— rides along as the per-row control, so "the model contributed something" is an
answer and not an impression. `--refine` (opt-in, slow) then runs `ToneMatcher`
itself from the prediction as its PRIOR, which is the product path end to end.

**It cannot exceed this lane's ceiling and it does not pretend to.** It
optimises nothing, but it MEASURES with the metric the search optimises, so the
1.56× content invariance below is its resolution too: two models whose medians
differ by a few percent are the same model as far as that table can say. Its
self-floor is also WEAKER than the matcher's, because a dataset clip is 5 s and
two disjoint 5 s windows do not fit — it uses 2 s windows, which is the marginal
case `SelfDistance` warns about, so the floor there is a scale and not a target.

## The metric is the ceiling, and two more routes round it are now dead ends

The search optimises `ToneSimilarity` directly and `rs-truth` does not, so at a
measured **1.56×** content invariance (`tonefx-v2`, re-measured 2026-09-12 —
rig 0.3983, riff 0.2549 on the standard fuzz/clean stimulus) "the matcher beat
the professional's rig" is partly evidence that the metric is gameable. The
plausibility prior above is a PATCH on that, not a fix. `docs/agents/testing/
audio-eval.md` holds the six findings that got the metric from 1.04× to 1.56×;
these two are the search lane's own attempts to go further, both **measured
WORSE and reverted** (2026-09-12):

- **A per-band MEDIAN across frames of the frame's level-normalised shape,
  instead of the power mean: 1.437×.** The reasoning was that a median asks
  "what does this band usually sit at" and a partial lighting one band in 15 % of
  frames should not move it. It fails for the reason `audio-eval.md`'s finding 4
  already records: a median over per-frame LOG bands keeps every note's harmonic
  comb, and averaging POWER and taking the log ONCE is what fills the combs in.
  The same change scaled dB/20 instead of dB/40 measured 1.17×.
- **Anchoring the distortion block's bands to the frame's own PEAK fundamental
  (rather than the centroid the guide already records as worse): 1.526×.** The
  block's riff cost rose from 0.01214 to 0.01697 while its rig cost rose from
  0.00652 to 0.01042 — the internal ratio improved (0.54 → 0.61) and the absolute
  cost grew more, which is the same shape the centroid attempt had. **Two
  different f0 estimators now say the same thing: a window that moves with the
  riff varies more than a fixed one.** Do not try a third without a new argument.

**A finding worth a third attempt, from the block breakdown of that measurement:**
`mfccMean` — which `ToneFeatureLayout` calls "the most content-invariant thing in
the vector" — contributes **0.01662** to the riff distance against **0.00359** to
the rig distance, a ratio of 0.22 and the single largest share of the riff cost
(26 % of riff²). `bandStd` (0.18), `mfccVar` (0.21) and `modulation` (0.003, and
12 % of the riff cost) are the same shape. Only `dynamics` (18.8) and `level`
carry the rig. The claim in the layout doc is not what this stimulus measures,
and that is where the next attempt should start.

## What this lane still needs

**Three items that stood here until 2026-09-12 are DONE — do not re-open them.**
`RealRendererMatchEvalTests.TryResolve` constructs the real `RigRenderer` over a
`NullDeviceRigHost`, the real `ToneFeatureExtractor` and the real `RigSampler`
over `ReferenceDi.Medley()`; the extractor carries log-spaced bands (48 over
60 Hz–10 kHz, finer than 1/3-octave), a 12-value envelope/modulation branch and a
separate coarse cepstral bank; and the DI registration lives in
`DotCharter.UI/App.MatchServices.cs`, which exists. This lane still registers
nothing itself.

- **The metric.** See the section above: the plausibility prior buys headroom
  against a 1.56× metric, it does not raise the metric. That is the ceiling on
  everything here, and the block breakdown says `mfccMean` is where to look.
- **K.** The shipping library is 4,000 and every end-to-end number so far is from
  **96** — 2.4 % of it — with retrieval doing almost all the work (five of twelve
  rows were `retrieval` with no refinement and still won). At 2.55 s a render,
  K = 4,000 is ~2 h 50 m once per machine. It is the cheapest unexplored axis.
- **`RealRendererMatchEvalTests` hard-codes `Count: 4000`**, which is that same
  ~2 h 50 m before the test does anything, so in practice it is run against an
  already-cached library. Give it the same `DOTCHARTER_MATCH_EVAL_K` override the
  eval lane has before relying on it in CI.

## Keeping this current

Re-measure the numbers rather than carrying them forward — the CMA-ES benchmark
figures move with the strategy constants and the recovery figures move with the
fake extractor. If the search gains a stage, add a row to the file table above
and a row to the test table.
