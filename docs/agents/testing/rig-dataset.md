# The rig dataset: sampler, corpus, and what its tests actually pin

The DATA half of "match a tone from audio" (`DotCharter.Services/Ai/Match/`,
contract in `MatchContracts.cs`). Two pieces:

| | |
|---|---|
| `DotCharter.Services/Ai/Match/RigSampler*.cs` | the `IRigSampler` implementation — **product code**, also used by the search lane's retrieval library |
| `tools/rig-dataset/` | the corpus renderer and `fetch-di.py`. Its own [README](../../../tools/rig-dataset/README.md) is the operator's guide; this file is the *why* and the test map |
| `DotCharter.Tests/Ai/Dataset/` | `RigSamplerTests` (24) + `RigDatasetRenderTests` (8) + the v2 trio: `RigBedPlanTests` (6), `RigSeparateStageTests` (8), `RigGpuSelectorTests` (6) |

The corpus itself lives **outside the repo**, at
`DotCharter-local/datasets/rig/` (and `rig-smoke/` for the 200-clip set the
model lane builds its loader against).

---

## The sampler is the label distribution, and that is why the priors are load-bearing

A uniform draw over 94 modules and 578 knobs is **noise**, and a model trained on
noise learns to recognise the artefacts of noise. Every published
blind-parameter-estimation paper that skipped this reports the same failure. So
`RigSampler` draws rigs a guitarist would build:

* **Chain length** mostly 2–5 (79 % of the mass). That is the shape of the
  166-preset factory library — 2–5 is 141 of its 166 rigs — and of the 125
  converted Rocksmith tones.
* **Family order** `dynamics → pitch → drive → amp → cab/mic → eq → filter →
  modulation → delay → reverb → utility`, the canonical pedalboard order the
  tone-design prompt already states, **deviated in ~7 % of draws** by one
  adjacent swap. A corpus whose order is a hard rule teaches the model the RULE
  rather than the sound. The amp→cab→mic seam is never swapped.
  *Two additions the prompt's rule list does not cover*: **pitch** sits before
  drive (every monophonic tracker's own guide says so — they track a clean neck
  pickup far better than a clipped signal), and **mic** is not a family at all
  (the engine files `micpos` under `cab`), so it is ordered by a sub-key.
* **Six archetypes** — clean / crunch / high-gain / bass / ambient / direct —
  each with its own amp pool, drive count, family probabilities and drive
  **window**. This is the single most important conditioning: the same
  `overdrive.drive` value is a wrong label for a clean rig and a wrong label for
  a metal rig, and one distribution for both asks the model to learn a
  contradiction.
* **Knob priors**, four layers most-specific-first: discrete table → archetype
  drive window → ~60 explicit per-(module, param) Beta priors → per-param-id
  heuristics (`mix`, `level`, `tone`, `depth`, `feedback`, symmetric dB) →
  Beta(2,2) over the middle 80 % of the catalogue range. Delay times land on a
  **subdivision of a beat** 75 % of the time; a mix on a time/modulation effect
  is a blend, not 100 %; an unsigned feedback stays clear of self-oscillation; a
  signed feedback picks a SIGN then a magnitude, because a draw centred on the
  middle of `[-0.95, 0.95]` is a draw centred on the module switched off.
* **No knob ever sits exactly on its catalogue default** (±2 % of range),
  discrete knobs excepted. A corpus where the untouched knobs are all at their
  defaults teaches the model the default.
  **The one exception is `RigSampler.HeldAtDefault`**: params that exist only for
  the Rocksmith conversion (`gate.autoFloor`, the `bitcrusher` FutzBox stages,
  #155/#157) stay at their defaults WITHOUT a draw, so adding them moved neither
  the corpus's distribution nor its random stream
  (`TheRocksmithConversionParams_AreHeldAtTheirDefaults`).

Sources of truth, not copies: families come from `AiGearNotes` (the glossary the
AI prompt already ships), ranges from `DcRigTargetCatalog`. **Do not add a third.**

### Two rules that are subtler than they look

* **"Never two amps" is `RigSampler.TrueAmps`, not the `amp` FAMILY.**
  `AiGearNotes` files `tubepre` under **drive** (a preamp pedal in front of an
  amp is an ordinary rig) and `poweramp` under **amp**. A family test let the
  wildcard draw put a poweramp behind a tubepre amp slot and call it one amp —
  caught on the first test run.
* **`namamp` is gated on the CAPTURE LIST, not on `AllowNamCaptures`.** The flag
  says "you may"; an empty library says "there is nothing to point at", and a
  `namamp` whose `modelPath` names nothing is a label the renderer cannot honour.
  It leaked in through the wildcard draw until the gate moved.

### Six modules the sampler never draws

`splitter`/`merger` (parallel topology; v1 is series only), `looper` (43 MiB of
ring buffer per instance, and it processes nothing without a footswitch),
`miku`/`mikustomp` (the sound is a string-valued phrase table, not the float
params the label carries), and **`glitch`** — its `chance` param rolls a native
RNG this side does not seed, so the same rig and the same DI would render two
different clips. Every reason is the same reason: *the label would not describe
the audio.*

---

## The tests, and the line each one prints when it breaks

`DotCharter.Tests/Ai/Dataset/RigSamplerTests.cs` — 24 cases, every one a
property of the DISTRIBUTION over 10,000 seeded draws, not of one rig.

**Four of them have been seen red against a real bug** (marked ✔ — those lines
are quoted verbatim from the run that found it). The rest carry the message they
would print and the one-line change that trips them; **trip them before you trust
them.** Note that the render tool's binaries are LOCKED while a corpus render is
running, so `dotnet build` of the test project fails with MSB3021 until it
finishes — do the break-and-restore when nothing is rendering.

| Test | Fails with | To trip it |
|---|---|---|
| `EveryDraw_IsAModuleAndAKnobTheCatalogueHas_InRange` | `N sampled values are not valid rigs; first 10: <module>.<param> = v outside [min, max]` | widen a prior past its range |
| `NoKnob_SitsExactlyOnItsCatalogueDefault` | `N knobs landed exactly on the catalogue default; first 10: …` | `DefaultAvoidance = 0f` |
| `TenThousandDraws_ReachEveryModule_AndNoneTakesMoreThanAFifthOfTheCorpus` | ✔ `1 of 94 modules never appeared in 10000 draws — the model could never name them: namamp` | drop `WildcardChance` to 0 |
| `ChainsAreMostlyTwoToFiveModulesLong` | `only X% of chains are 2–5 modules long (budget ≥ 70 %); histogram 1:… 2:…` | flatten `LengthWeights` |
| `TheSameSeed_ReproducesBitIdenticalRigs` | an `Assert.Equal` on the raw float bits | read a clock anywhere in `Sample` |
| `ADifferentSeed_ProducesADifferentCorpus` | `N/200 chains are identical across seeds — the seed is not driving the draw` | guards the guard above |
| `TheCanonicalFamilyOrder_HoldsInAtLeastNinetyPercentOfDraws` | `only X% of chains follow dynamics → pitch → …` **and** `X% are canonically ordered — the deviation prior is not firing` | `OrderDeviationChance` to 0.6 trips the first, to 0 the second |
| `NoRigHasTwoAmps_AndASpeakerAmpAlmostAlwaysHasACab` | ✔ `two amps in one chain: overdrive → tubepre → poweramp` | test the `amp` FAMILY instead of `TrueAmps` |
| `AMicIsNeverOnANonExistentCabinet` | an `Assert.Contains` for `cab` | remove the `TrimToTarget` cleanup |
| `NamCaptures_AreOnlyDrawnWhenALibraryIsGiven_AndAlwaysCarryAPath` | ✔ `Assert.DoesNotContain() Failure: Filter matched in collection` | gate `namamp` on the FLAG instead of the capture list |
| `TheArchetypeDrivesTheGain` (3 cases) | `highgain: mean amp gain 0.31 is outside [0.52, 1.00] over N amps` | point every archetype at one drive window |
| `AModulationOrTimeEffectsMixIsRarelyFullyWet` | `X% of time/modulation mixes are above 75 % wet` | drop the `FullWetByNature` split |
| `ACabinetIsEssentiallyFullyWet` | `a cabinet at 0.34 wet is a half-bypassed cabinet` | same |
| `DelayTimesLandOnMusicalSubdivisions` | `only X% of delay times are tempo-plausible` **and** `no delay time is ever off the grid — the 25 % free draw is dead` | set the tempo-grid probability to 0, then to 1 |
| `FeedbackNeverSitsOnTheSelfOscillationCeiling` | `N unsigned feedbacks are above 0.90 — a runaway tail is not a tone: bbddelay=0.94` | raise the prior's ceiling |
| `PhaserStagesStayEven` | `Assert.Equal(0, s % 2)` | drop the step-2 override |

**The 20 % budget is a share of SLOTS, not of draws, and that is the only
reading compatible with the sampler's own rules.** A cab follows an amp, so
`cab` is in ~60 % of RIGS by construction and always will be; what matters for
training is that no single module dominates the module INSTANCES the model sees.
The test says so in its own message.

`RigDatasetRenderTests.cs` — 8 cases; four drive a **real 20-clip render**
through the real engine into a temp directory, with a synthetic 6-file DI pool
built in the test (so it runs in a worktree with no corpus).

| Test | Fails with |
|---|---|
| `ATwentyClipRender_ProducesValidRowsWhoseRigsValidateAndWhoseAudioIsFiniteAndLevelled` | `only N/20 clips survived the reject rules`, or `<module>.<param> is not a knob the catalogue has`, or an RMS outside −18 ±6 dBFS, or `a rendered clip is over full scale` |
| `TheSameSeed_ReproducesTheManifestByteForByte` | a line-by-line `Assert.Equal` |
| `ARenderIsResumable_AndAResumedRunMatchesAnUninterruptedOne` | same, against a run done in two halves |
| `EveryRowRecordsWhatWasDoneToIt` | a missing `norm:` / `tilt:` / `bleed:skipped-no-beds` tag |
| `TheClipSeedDependsOnTheRunSeedTheIndexAndTheAttempt` | `N of 2,000 clip seeds collided` |
| `NormalisationHitsItsTarget_AndTheTiltIsBounded` | ✔ **`Value: "tilt:-1,2dB"`** — how the culture-dependent-tag bug was found |
| `AnIdenticalRenderIsRejected_AndADifferentOneIsNot` | a rig that did nothing passing the reject rule |
| `TheManifestMergeIsSortedByClipIndexWhateverOrderTheShardsFinishedIn` | indices out of order, or leftover shard files |

### These render tests SKIP without the JUCE `Null` device, on purpose

`RenderOptions.RequireNullDevice` switches OFF the tool's own fallback to the
**default** audio device. A test host that dies holding the real sound card holds
it EXCLUSIVELY until someone kills the process — one survived 32 hours that way
(see the root guide). A skip is cheap; that is not. They are in the default gate
and are not in the headless-UI suite.

---

## Four things the first runs taught

1. **`ProcessOffline` is not concurrent, not even across engine SEATS.** Four
   workers on one engine produced **1 clip and 199 `JuceAudioStatus.Device`
   refusals**. `--threads N` spawns N child PROCESSES at Idle priority, each
   rendering `index % N == shard`. This also matches the study's "renders must
   run in a separate process from the app".
2. **Manifest tags were culture-dependent.** `$"{db:F1}"` writes `-1,2` on this
   German box; a manifest whose tags depend on the renderer's locale is one two
   machines cannot compare. Every tag is `string.Create(InvariantCulture, …)`.
3. **`fetch-di.py` extracted zip members by BASENAME**, and Guitar-TECHS names
   every player's DI leg `directinput_allchords.wav` — P2 overwrote P1, and the
   clip glob (`{stem}_*.wav`) then double-counted what was left: **2,473 clips on
   disk against 4,167 reported**. Members carry their zip's name now and the glob
   is exact (`{stem}_[0-9][0-9][0-9][0-9][0-9].wav`).
4. **A cache bounded by ENTRY COUNT over entries that are whole decoded clips is
   not bounded.** `ClipSource` kept 4,096 DI reads, and a 5 s mono `float[]` is
   960 KB — a ~3.9 GB ceiling per child, times `--threads N`. The 100k run
   reached **9.5 and 10.3 GB of private bytes per child after 140 minutes**
   (2026-09-13). It is a BYTE-bounded LRU now
   (`ClipSource.DefaultCacheBudgetBytes`, 192 MB), pinned by
   `ClipSourceCacheTests`. The bisect that found it is worth repeating for any
   future growth here: render the same clip count over a DI pool SMALL enough to
   fit the cache and over the real one — 83–133 MB flat against 114 → 680 MB in
   350 clips separates "the pool cache" from "the engine, the rig or the writer"
   in ten minutes, with no profiler installed.

## Known imprecisions, stated rather than hidden

* **Stereo-only effects partly cancel in the stored mono sum** (`autopan`,
  `midside`, `widener`). Mono is the right storage format — the inference input
  is a mono guitar stem — but for those three the label is more precise than the
  audio. They sit at the bottom of the weight table; do not raise them without
  deciding what the label should say.
* **Stage B (separation) runs on DirectML** since the app's ONNX Runtime became
  the DirectML build (#151): no CUDA/cuDNN download, any DirectX 12 GPU. Measured
  CPU throughput before that, one process, 4 intra-op threads, Debug build:
  **6.7 clips/min** (~9 s per 5 s clip; ONNX `run` 3.0-8.8 s, iSTFT 1.5-2.6 s),
  i.e. ~100 CPU-hours for 40,000 clips. **The stage REFUSES to run rather than
  falling back**, because `OnnxStemSeparator` degrades a GPU request to the CPU
  silently and hands back a working session either way. Pass `--cpu` to mean it.
* **`RigSampler` never draws a `Bypassed` module, a `Branch` tag or a
  `Sidechain` source.** All three are real parts of a `DcRigModule`'s identity
  and all three are out of scope for v1's series-only chains.

---

## v2: beds, mix-and-separate, and the device rule

The v1 corpus (49,908 clips) contains **zero** separated rows — `render.log` says
`0 separated` — while every real target IS one, because the match lane gets its
guitar out of `StemIsolator`. Two things caused that and both are now code:
`beds/` was empty, so there was nothing to mix a clip INTO, and the CUDA EP would
not load, so there was nothing to separate with.

### `RigBedPlanTests` — the bed generator's pure half

`BedPlanner` takes its param ranges as an `IParamRanges` rather than reading a
static catalogue, and that is the whole reason the musical half of the bed
generator is testable at all: `DcRigTargetCatalog` is **`internal` to
`DotCharter.Services`** and unreachable from the tool, and the reachable source of
truth — `JuceDspModuleCatalog` — needs `juce_sharp.dll`. Injecting the ranges
keeps `BedPlanner`, `BedSynth` and `BedSidecar` native-free.

| Test | Fails with | To trip it |
|---|---|---|
| `TheSameSeed_ReproducesTheSameBedPlan_AndADifferentIndexDoesNot` | `only N of 30 bed plans are distinct — the index is not driving the draw` | drop `index` out of the seed |
| `EverySidecar_CarriesTheOursLicence_AndRoundTripsTheWholePlan` | ✔ `Assert.Contains() Failure … Not found: "No third-party audio"` | reword `BedSidecar.OursLicence` |
| `ThirtyBeds_CoverEveryKey_BothScales_AndTheWholeTempoRange` | `the bed pool covers only N of 12 keys` | fix the root to one value |
| `EveryLayer_SynthesisesAudibleAudioAtTheReferenceLevel` | `layer 'bass' came out at −240.0 dBFS, not −18` | return early from a voice |
| `TheSynthesisIsBitIdenticalForTheSamePlan` | an `Assert.Equal` on the samples | use `Random.Shared` for the drum noise |
| `EveryKnobTheBedRigsDeclare_IsAKnobTheEngineCatalogueHas` | ✔ `1 bed-rig knobs are not in the engine catalogue and are therefore set on NOTHING: bassamp.bassKnob` | misspell any id in the planner's chains |

**That last one is the one that matters**, and it is why `DeclaredKnobs` exists as
a public list. `BedPlanner.Module` DROPS a param the catalogue does not have —
correct at runtime, and completely silent: the bed then renders through that
module at its DEFAULTS and the sidecar simply does not mention the knob. The
first draft of the bed rigs guessed six of the ids wrong
(`compressor.threshold` for `thresholdDb`, `bassamp.level` for `master`, …) and
nothing else would have said so. It `Assert.Skip`s without the native DLL.

### `RigSeparateStageTests` — stage B over a fake separator

Eight cases, none of which loads a model: the real one is 110 MB and seconds of
CPU per clip, so a test that used it would be testing htdemucs.

| Test | Fails with |
|---|---|
| `SelectionIsDeterministic_AndRaisingTheFractionOnlyEverAddsRows` | `N rows selected at 20 % are NOT selected at 40 %` |
| `EverySeparatedRow_KeepsItsRig_AndRecordsTheBedTheLevelAndTheModel` | a missing `mix:` / `bed:` / `separated:` tag, a repointed `ClipPath` with no file behind it, or a changed rig |
| `TheGuitarToMixLevelSpansItsWholeWindow_AndTheTagIsCultureInvariant` | ✔ `the guitar-to-mix draw only reached [-12.0, -9.0] dB over 400 rows`; and a comma in any tag — the same culture bug the tilt tag had |
| `AResumedPass_DoesNotReSeparateARowItAlreadyDid` | ✔ `a resumed pass separated 51 rows that were already done — … the clip is overwritten with a stem OF a stem` |
| `RaisingTheFractionSeparatesOnlyTheNewRows` | an `Assert.Equal` on the call count |
| `AFailedSeparation_IsRecordedAndNeverRetriedForEver` | ✔ `a row whose separation FAILED was retried 60 times` |
| `WithNoBeds_ItStillRuns_AndSaysSoOnEveryRow` | a missing `separated:htdemucs_6s:no-bed` |
| `TheStopFile_EndsThePassCleanlyWithTheManifestIntact` | a non-zero call count, or a shrunken manifest |

Two design decisions the tests pin rather than describe:

* **A separated stem goes to `clips-sep/`, never over `clips/`.** In-place
  overwriting makes a crash between the WAV write and the manifest flush leave a
  separated clip whose row says "clean" — and the resume then separates it again,
  producing a stem of a stem.
* **`separated:` is a PREFIX contract**, matched by `Augmentation.IsSeparated`.
  `:failed` and `:unavailable` match it deliberately, or one unseparable clip is
  retried on every resume for ever.

### `RigGpuSelectorTests` — by name, never by index

Six cases. `nvidia-smi` here is PCI ordered (**0 = RTX 3080**, 1 = RTX 5070 Ti),
CUDA defaults to `FASTEST_FIRST`, and torch calls the 5070 Ti `cuda:0` — three
numberings that disagree (DXGI makes four: its adapter 0 is the display card,
the 5070 Ti), over a box whose second card is the owner's **gaming** GPU.
`GpuSelector` resolves a NAME, and `GpuSelector.DirectMlDeviceFor` maps that name
onto exactly one DXGI adapter — the DirectML `deviceId` — or refuses.

`ItRefusesTheGamingGpu_UnlessItIsExplicitlyAllowed` is the load-bearing one; it
was seen red as ✔ `Assert.Throws() Failure: No exception was thrown` with the
guard's condition short-circuited.

`OnnxStemSeparator` takes the `deviceId` (#151), so `OnnxGuitarSeparator` asks
for DirectML on the mapped adapter explicitly. `CUDA_VISIBLE_DEVICES` pins nothing
any more — the runtime has no CUDA provider. `TheChosenCard_MapsToItsDxgiAdapter_ByName`
and `AnAmbiguousOrMissingCard_MapsToNothing` pin the mapping.

### The DI stage, and where GuitarSet went

`RigDatasetRenderTests` now also covers three augmentations applied to the DI
**before** the rig — `di-pitch:` (±2 st, by resampling), `di-level:` (±6 dB after
normalisation) and `pickup-comb:` (half the clips). The rig's label is exactly as
true after them as before, which is what makes them safe; all three are recorded,
because a DI augmentation is invisible in the wet clip.

`fetch-di.py` gained **GuitarSet** (Zenodo 3371780, CC BY 4.0) as an **opt-in**
set: `--only guitarset`, 1,990 clips / 2.76 h from the hex-pickup *debleeded* leg,
written to **`di-optional/guitarset/`** and not to `di/`. That path is not
tidiness — `ClipSource.Scan` enumerates `di/` RECURSIVELY, so 2.76 h of ACOUSTIC
guitar dropped into `di/` would silently be a third of a 5.88 h pool whose entire
job is electric tone.

### The unattended run

`tools/rig-dataset/pipeline-data-v2.ps1` is beds → render → separate → merge into
`DotCharter-local/datasets/rig-v2`, every stage resumable, every child at Idle
priority, with a STOP file. **Stage 0 records `model.onnx`'s mtime under
`%LOCALAPPDATA%/DotCharter/ai-match/rig-estimator/` BEFORE it waits** and then
waits for a NEWER one that settles — an export from the previous run is already
on disk, so "the file exists" is true before the run being waited for has done
anything. Measured 2026-09-13 (Debug build, idle box): beds 13 s each, render
~224 clips/min over four processes (100k ≈ 7.4 h), separate 6.7 clips/min on CPU.

## `eval-render`: does the estimator's guess SOUND close?

Every number the trainer reports is a LABEL metric — family F1, exact chain
match, knob MAE — and none of them is the owner's question. The goal is close
SOUND, not exact gear, and the two come apart in both directions: a chain that
misses one pedal of five can be inaudible, and a chain that is exactly right
with one gain knob 0.2 off can be a different amplifier. `eval-render`
(`tools/rig-dataset/DotCharter.Tools.RigDataset/EvalRenderCommand.cs`) answers
the audible question directly.

```
eval-render --predictions <run>/predictions-test.jsonl --root <dataset root>
            [--limit N] [--seed S] [--threads 2] [--target clip|render]
            [--self-window 2.0] [--top-confident N] [--refine]
```

For every row it renders the PREDICTED rig over the row's dry DI span through
the real offline renderer (`RigRenderer` over a `NullDeviceRigHost` — the same
device-less engine `render` and `knob-sensitivity` use) and measures the
`ToneSimilarity` distance to the target. It writes one JSONL row per prediction
plus a summary JSON; `tools/rig-model/render_eval.py` runs it over several run
directories and prints one table, so a "sound distance" column can sit beside
famF1 and knobMAE. **Give it `--out-dir` when a training job owns those run
directories** — by default it writes the report beside the predictions, and a
directory a trainer is still writing to is not this script's to add files to.
Use ONE `--seed` for every model in a table, or it compares different rows,
which at 1.56x content invariance is most of the signal.

### Three columns, because a raw distance is not readable on its own

A `tonefx-v2` distance has **no absolute scale** — it depends on the riff, the
DI and the separation bleed — so two more numbers ride along with every row:

- **`selfFloor`** — the target measured against a DISJOINT window of ITSELF,
  the same construction `ToneMatcher.SelfDistance` uses. It is what "the same
  tone on this material" costs, and `distanceOverFloor = distance / selfFloor`
  is therefore **the only column that is comparable ACROSS rows**. It is
  **NaN, never 0**, when the target cannot hold two windows: 0 is a legal floor
  value meaning "perfectly self-similar", and a consumer dividing by it gets a
  wrong answer for a right-looking reason.
- **`genericDistance`** — a fixed `overdrive → amp → cab` at catalogue defaults,
  rendered over the SAME DI span. It is byte-for-byte the chain `AudioEvalTests`
  calls `floor-generic`, kept identical on purpose, and it makes "beats generic"
  a per-row answer rather than a global hand-wave. A model that cannot beat an
  information-free chain is not contributing.
- **`plausibility`** — the predicted rig's `RigPlausibility` penalty, so a model
  that wins on distance by emitting boards no player would build says so in the
  same file.

**The self-floor window defaults to 2 s and not the matcher's 5 s, because a
dataset clip is 5 s long.** Two disjoint 5 s windows do not fit, so every row
would report NaN. At 2 s a 5 s clip holds two with a second between them — which
is exactly the marginal case `ToneMatcher.SelfDistance` warns about, so read the
floor as a SCALE and not as a target, and read the raw median beside it.

### `--target clip` is the product question; `--target render` is the harness's own check

- **`clip` (the default)** scores against the wet clip already on disk — the
  audio the model was actually asked about. It carries the corpus augmentations
  (level, tilt, pickup comb, bleed, separation) that no rig can reproduce, so
  **even a perfect prediction does not measure 0 here.** It also carries the
  16-bit storage: writing one render as `Pcm16` and reading it back measured
  **0.1436** against the same render in float, which is under 0.05
  (`EvalRenderSmokeTests`, probed 2026-09-14). That offset is per row and it is
  why the floor and the generic column exist.
- **`render`** re-renders the TRUTH rig as the target instead. A prediction
  identical to the truth then measures **exactly 0**, which is the non-vacuity
  proof of the whole harness — nothing else in it can produce that number by
  accident.

**A predictions file MUST carry `"seconds": [start, length]`.** Without it the
predicted rig is rendered over the START of each DI while the clip came from a
window somewhere inside it, so in `clip` mode every distance carries a CONTENT
mismatch on top of the rig difference and the whole table reads as "the model is
bad". The tool prints a warning when no row carries a span. The dataset manifest
does not record the offset, so a predictions file synthesised from
`manifest.jsonl` can only be scored with `--target render`. **As of 2026-09-14
the trainer emits `"seconds": null`** on every row of `baseline/` and
`round-1/v2-clap-quiet/`, so those files are `--target render` only; a `[start,
length]` there is the one change that unlocks clip mode.

### Measured (200 rows off `DotCharter-local/datasets/rig`)

| predictions | median `d` | mean `d` | median `d`/floor | `<1.5x` | beats generic | median generic |
|---|---|---|---|---|---|---|
| `predicted = truth` | **0.0000** | 0.0063 | **0.0000** | 100 % | **100 %** | 0.6386 |
| `predicted = generic` | 0.6386 | 0.6680 | 0.9563 | 80.5 % | **0 %** | 0.6386 |

Both are `--target render`, 2.63 s/row on a box with two GPU trainings on it
(three renders a row: predicted, generic, and the truth target). The identity
row is the proof the harness measures the RIG: 8 of 10 rows are bit-exact zero
and the mean is 0.0063 because a handful of modules are not bit-deterministic
across engine instances — ~2 % of the self-floor, far below anything the table
resolves. The generic row is the proof the generic COLUMN is the same
measurement: a prediction that IS the control measures the control exactly (the
two medians agree to every printed digit), and beats it on zero rows.

**Read the generic row's `d/floor` of 0.9563 as a warning about the FLOOR, not
as praise for the generic rig.** A 2 s window on a 5 s clip puts the self-floor
at roughly where a wrong rig lands, which is exactly the weakness
`ToneMatcher.SelfDistance` documents for a span under 2x the window. On longer
targets — a real song excerpt, or a corpus rendered at 10 s — raise
`--self-window` and the column separates. Until then, `distanceOverFloor` is
useful for RANKING models against each other and must not be read as an absolute
"within 1.5x of the floor means it sounds right".

### First real models (200 rows, `--target render`, one seed)

```
model            n     med d   med d/floor  <1.5x  <2x    <3x    beat-gen  med gen  s/row
v2-clap-quiet    200   0.4019  0.5684       90.5%  92.5%  97.5%  73.0%     0.6111   3.14
baseline         200   0.5770  0.8728       80.0%  88.5%  98.5%  53.0%     0.6097   2.80
```

`round-1/v2-clap-quiet` against `baseline`, both from the trainer's own
`predictions-test.jsonl`, both scored on the SAME 200 rows (`--seed 20260914`),
zero rows skipped and zero rigs the engine refused. The two `med gen` columns
agree to three digits, which is the cross-check that the two runs really are on
the same rows. **The trained model beats the information-free chain on 73 % of
rows against the baseline's 53 %**, and that gap — not the raw distances — is
the readable result: 0.5684 vs 0.8728 `d/floor` is a 1.54x separation, right at
the metric's own 1.56x resolution, so treat it as "clearly better" and not as a
measured factor.

### `--refine` is the product path and is opt-in because it is slow

With `--refine` the matcher (`ToneMatcher`) runs its CMA-ES refinement from the
predicted rig as the PRIOR for `--refine-renders` renders a row, and the row
reports the distance AFTER refinement. It builds a `--refine-library`-row
retrieval library once first (the shipping library is 4,000 rows and ~2 h 50 m —
this defaults to 64, which is a refinement harness and not the shipping search).
Off by default. Measured 2026-09-14: **20.07 s/row** at 6 renders over an 8-row
library.

**`refinedDistance` is the WINNER's distance, not the minimum over the
candidates** — what the product would hand the user. The matcher ranks by
plausibility-weighted distance, so that number can be FURTHER in raw distance
than the prediction was, and at a small `--refine-renders` / `--refine-library`
the retrieval seeds outrank the prior outright: three rows went 0.4312 -> 0.5829
that way. Give the refinement a real budget before reading the column as a
verdict on refinement; taking the minimum instead would flatter a path nobody
ships.

### What it inherits, and cannot exceed

`eval-render` optimises nothing, but it MEASURES with the metric the search lane
optimises, so it inherits that metric's ceiling exactly: `tonefx-v2` measures
**1.56× content invariance** (`docs/agents/services/ai-match-search.md`). Two
models whose medians differ by a few percent are the same model as far as this
table can tell, and no aggregate here is finer than that factor.

### The tests

`EvalRenderMathTests` is pure and needs no audio: the summary arithmetic (each
statistic folded over the rows that CARRY it, never over the whole set with NaNs
in it), the predictions parsing (a row whose `predicted` key is misspelt is
REFUSED — it would otherwise arrive as null and be scored against an empty rig,
which produces a perfectly plausible distance), the deterministic subsample, the
top-N-confident rule (a file with no confidence column has NO top-N, rather than
the first N rows), the generic baseline being a real catalogue chain with a zero
plausibility penalty, and the self-floor's NaN rule.

`EvalRenderSmokeTests` runs the real engine over three rows and SKIPS without the
JUCE `Null` device, exactly as `RigDatasetRenderTests` does and for the same
reason. It pins the three claims that would be invisible if they broke: an
identical prediction measures exactly 0, a prediction equal to the control
measures the control exactly, and a row the tool cannot score is COUNTED and
NAMED rather than dropped into an aggregate it would have improved.
