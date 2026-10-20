# Match a tone from audio — the SECTIONS lane (`DotCharter.Services/Ai/Match/Sections/`)

**Match-from-audio measures one tone. This lane measures a SONG.** The owner's
goal, in their own words: pick a stem and a time span per part of a record —
"0:10–0:30 clean, 0:30–0:50 distortion" — and get one rig per section. That is
what makes tone-from-audio usable on a song that is not in the library, because
a song with no chart has no tone changes to hang a rig on and a whole-song match
averages an intro, a chorus and a solo into one tone that is none of them.

Two halves: **where the parts are** (`IToneSectionProposer`) and **what each part
sounds like** (`IToneSetMatcher`), plus the stem-kind isolation both rest on.

The contract is `DotCharter.Services/Ai/Match/SectionContracts.cs` — interfaces and records only,
**additive changes exclusively**, extending `DotCharter.Services/Ai/Match/MatchContracts.cs` rather
than replacing it. Three lanes code against it: **sections** (this one),
**prior** (`IToneMatchPriorSource`, `IToneNarrator`) and **ui** (the section list
in the tone-design panel). This lane implements `IToneSectionProposer`,
`IToneSetMatcher` and `StemIsolator.IsolateAsync`, and NOTHING else; every other
interface is someone else's and is faked in tests.

## Files

| File | Purpose |
|---|---|
| `DotCharter.Services/Ai/Match/Sections/ToneSectionProposer.cs` | The segmenter: isolate, scan, pick peaks, merge with the chart, build sections. Plus `ToneSectionProposerOptions` |
| `DotCharter.Services/Ai/Match/Sections/SectionNovelty.cs` | The peak picker — pure arithmetic over a `NoveltySample` curve, no audio anywhere in it |
| `DotCharter.Services/Ai/Match/Sections/SectionGainHeuristic.cs` | The one guess: "(clean)" / "(driven)" from crest factor + high-band energy |
| `DotCharter.Services/Ai/Match/Sections/ToneSetMatcher.cs` | The per-section loop: isolate → prior → `IToneMatcher`, sequentially |
| `DotCharter.Services/Ai/Match/StemIsolator.cs` | `IsolateAsync` + `ResolveStemAsync` + `AcceptableSourcesFor` — all four `ToneStems` out of ONE cached separation |
| `DotCharter.UI/App.MatchServices.cs` | Both registrations, at the end under `// sections lane` |

## The algorithm

1. **Isolate the whole stem, cache-first.** One `IStemIsolator.IsolateAsync` call
   for the requested stem over `ToneSectionProposer.WholeSong`. Never a
   separation per window; never a second separation for a second stem of the
   same song.
2. **Slide a window.** Four seconds (`ToneSection.MinDuration`), hop one second,
   each window embedded with `IToneSimilarity.Extractor`.
3. **Novelty.** The curve's sample at time *t* is the distance between the window
   ENDING at *t* and the window STARTING at *t* — `lag = window / hop`, so the
   pair is exactly adjacent. Overlapping windows blur a boundary into a ramp;
   a gap between them measures a change that may have happened inside it.
4. **Peaks.** Local maxima above an adaptive-and-floored threshold, taken
   strongest-first and never closer together than `ToneSection.MinDuration`
   (`SectionNovelty.PickPeaks`).
5. **Merge with the chart.** A chart boundary an audio peak agrees with (±2 s) is
   promoted to `chart+audio` and that peak is CONSUMED. A peak no chart boundary
   claims is kept as `audio`. A chart boundary the audio missed stays `chart`.
6. **Build sections.** Boundaries tile the song. Audio labels are "Part 1",
   "Part 2"… plus the gain heuristic's word; chart labels are the charter's own,
   untouched.

## Using the matcher's own metric is the design, not a convenience

The proposer takes `IToneSimilarity`, not an extractor of its own. A boundary it
finds is therefore by definition a place where the thing about to be matched is
**measurably different to the matcher** — so every section it proposes is one the
matcher can tell apart. A segmenter with a private notion of similarity would
happily split a song where the matcher hears nothing, and the user would get two
rigs that are the same rig.

It also means the thresholds below are in `ToneSimilarity`'s documented units,
and that scale is the only reason they mean anything: two performances through
ONE rig land around 0.1–0.3, a clean amp against a fuzz on the same riff is above
1.0.

## The threshold is adaptive AND floored, and it needs both halves

`threshold = max(MinNovelty, median + PeakFactor × (max − median))`

- **Adaptive alone** finds "the biggest wiggle" in a curve that is pure noise, so
  a song that never changes tone comes back in four parts.
- **A fixed floor alone** cannot cope with the range between a clean→fuzz change
  and a clean→crunch one.

The floor says "this is a real timbre change at all", in the metric's units; the
adaptive part says "and it is one of the big ones in THIS song".
`SectionNoveltyTests` pins each half with a curve the other half would pass.

**The floor has very little headroom, and that is measured, not feared.** With it
removed, the synthetic CONSTANT-tone stem splits into three parts at novelty
**0.319** — the same notes through the same timbre, one second apart. 0.35 is
above that by 10 %, and a real separated stem sits far higher still (see the
gated run below). Anything that moves `DefaultMinNovelty` downwards is moving it
into the noise.

| Knob | Default | Note |
|---|---|---|
| `ToneSectionProposerOptions.Window` | `ToneSection.MinDuration` (4 s) | Shorter and the extractor describes a chord |
| `ToneSectionProposerOptions.Hop` | 1 s | The resolution of every boundary this can report |
| `SectionNovelty.DefaultPeakFactor` | 0.45 | Below half: a song with one enormous change must still find the ordinary ones |
| `SectionNovelty.DefaultMinNovelty` | 0.35 | Above the 0.1–0.3 two-performances band |
| `SectionNovelty.ConfidenceScale` | 1.0 | Confidence = height / this, clamped. A FIXED scale, never the song's own maximum |
| `ToneSectionProposerOptions.ChartAgreement` | ±2 s | Twice the hop |

**Confidence is normalised against a fixed scale on purpose.** Against the song's
own maximum, one violent change would push every other real change towards zero
and the number the UI shows would mean something different in every song.

## The song's first part is not opened by a change

It is opened by the song starting. When nothing sits at 0 an opening boundary is
synthesised and **its evidence — origin and confidence alike — is the change that
ENDS it**: that a part ends is exactly what makes it a part, and whatever found
that change is what found this one. When there is no such change either, the
whole song is one section at confidence 1 — not a claim about a boundary, but the
plain statement "this song has one tone", which is the same answer
match-from-audio gives today.

## Stem-kind isolation

`IStemIsolator.IsolateAsync(path, stem, from, to, status, ct)` is an ADDITIVE
default-implemented member. The **default forwards `guitar` and THROWS for
everything else** rather than quietly handing back a guitar: a bass section
scored against a guitar stem produces a plausible distance and a wrong rig.
`StemIsolator` overrides it for all four `ToneStems`, out of the same cached
separation.

The fallbacks are asymmetric and that is deliberate
(`StemIsolator.AcceptableSourcesFor`):

| Stem | Accepted sources, best first | Why |
|---|---|---|
| `guitar` | `guitar`, `other` | A four-source model emits no `guitar`; this is `ToneAudioSource.GuitarishStems`, the rule the tone pipeline already used |
| `piano` | `piano`, `other` | Same shape — a four-source model puts the piano in `other` |
| `bass` | `bass` | EVERY model emits it, and `other` is defined as everything except it |
| `other` | `other` | It is itself |

An unknown stem name is refused BEFORE the separator is asked: finding out that
"guitars" is not a stem at the end of a twenty-minute separation is the worst
possible moment to find out.

## The set matcher

Sequential, because the renderer is ONE device-less engine with one chain and one
seat and its own lock serialises concurrent renders — parallelism buys nothing
and costs the ability to say which section is running.

- **A set never fails as a whole.** A section that cannot be isolated, cannot be
  matched, or is shorter than `ToneSection.MinDuration` comes back as
  `ToneSectionMatch(section, null, reason)` and the run continues. Four sections
  and one missing bass stem must leave the user with three rigs, not an error
  dialog.
- **Cancellation is the one exception that propagates.** A user's Cancel is not a
  failed section and must not be recorded as one.
- **The prior source is asked only for a section that has none** — a user's or a
  proposer's guess outranks the model's — and only when `IsAvailable`. A prior
  that throws is a NOTE, never a lost section: the matcher can always start from
  retrieval instead.
- **Progress is `"Section i of n: …"`**; notes carry the per-section elapsed
  time, the candidate count, the best distance and the self-distance floor.

## `ToneMatchRequest.StemHint` — the one change to the search lane

An ADDITIVE init-only property, so the positional constructor is unchanged. The
set matcher sets it to the section's stem.

**It adds seeds; it never removes one.** The retrieval library is drawn from the
MIXED archetype, so the nearest rows to a bass stem are regularly dark guitar
amps — near in the embedding and wrong as an answer. `ToneMatcher` therefore adds
the best bass-family rows (`bassamp` / `bassod`, the same two the sampler's
`RigProfileKinds.Bass` archetype draws from) as EXTRA seeds tagged
`retrieval:bass`, walking DOWN that ranking past anything ordinary retrieval
already found. A hint is knowledge about the INSTRUMENT, not a measurement of the
tone, and it must never discard the row that actually measured closest.

## Measured behaviour

Measured 2026-09-12 in the `feat/match-sections` worktree (Debug, LOW priority,
five other agents on the box). **Re-measure rather than carrying these forward.**

- One `tonefx-v2` vector over a four-second window costs roughly **0.9 s** here
  (three STFT scales, the longest 16,384 samples). The scan takes one per second
  of stem, so a 150-second excerpt is ~150 of them — **the scan, not the
  separation, is what a user waits for on a warm cache.**
- `ToneSectionProposerTests` (10 cases, real extractor, 16-second stems):
  **1 m 05 s**. The stems are as short as each case allows for exactly that
  reason.
- The gain heuristic's reference readings, pinned by
  `SectionGainHeuristicTests`: clean plucks **crest 14.602 dB, high-band share
  0.00236**; the same playing hard-clipped **crest 0.528 dB, share 0.00943**. The
  two ratio thresholds (0.003 / 0.006) were set from those and from the shape of
  a cab-filtered guitar spectrum, and they are **the least certain numbers in the
  lane** — re-measure them against real separated stems first.

### The first real run: recall 0/3, and why that is the useful result

`SectionProposalEvalTests`, 2026-09-12, one corpus song ("Natsu ga Kuru Tabi",
`PART REAL_GUITAR_22_RS2.xml`), first 150 s, ONNX `htdemucs-6s`, **6 m 20 s**
(separation + 147 windows), recall **0/3 within ±2 s**:

```
chart times : 72.9, 74.5, 136.8
audio times : 11.0, 85.0, 89.0, 125.0
confidences : 1.00, 1.00, 1.00, 1.00, 0.95
```

**The thresholds do not transfer from the synthetic signals to a real separated
stem, and the confidences say so out loud.** Every proposal came back at ~1.0,
which means the novelty between two adjacent four-second windows of a real
guitar stem is already at or above `ConfidenceScale` — the distance a clean amp
against a fuzz was supposed to score. Separation artefacts, bleed and ordinary
playing variation put the whole curve up there, so `DefaultMinNovelty` (0.35) is
inert on real material and the adaptive half is picking the largest wiggles in a
noisy curve. Two of the picks are 4 s apart, which is the minimum gap: the
classic signature of a picker running on noise.

Two other things the run says, which a recall number alone would hide:

- **At most 2 of the 3 were ever findable.** The chart's changes at 72.9 s and
  74.5 s are 1.6 s apart — under `ToneSection.MinDuration`, so no configuration
  of this design can emit both.
- **The separation, not the scan, is the one-off cost; the scan is what a user
  waits for every time.** ~147 extractions dominated a warm-cache run.

**Nothing was retuned on the back of this.** One song is not a calibration set,
and moving a constant until a single number looks better is how a threshold
becomes unfalsifiable. What it buys is the knowledge of WHICH constants are
wrong and in which direction, and a harness that will say so again over more
songs.

## Tests

| Test | What breaks it |
|---|---|
| `SectionNoveltyTests` (9) | A peak picker that finds boundaries in noise, drops the stronger of two close peaks, normalises confidence against the song's own maximum, or reports a flat top twice |
| `SectionGainHeuristicTests` (6) | A heuristic that reads LEVEL instead of gain, labels silence, or drifts off its reference readings |
| `StemKindIsolationTests` (9) | A stem that costs a second separation, a `bass` request answered with `other`, an unknown stem that separates first and fails second, a default implementation that hands back a guitar |
| `ToneSectionProposerTests` (10) | The splice not found, a constant tone split, a chart boundary overruled or duplicated by the audio, a non-tiling section list, a second isolation, a swallowed cancel |
| `ToneSetMatcherTests` (11) | A failed section failing the set, a prior asked for a section that has one, an unavailable prior asked at all, a cancel recorded as an error, a missing stem hint |
| `ToneMatcherStemHintTests` (3) | A hint that FILTERS instead of adding, or that changes anything at all when it is absent |
| `SectionProposalEvalTests` (1, gated) | Nothing in the gate — it is a MEASUREMENT over a real record and reports recall rather than asserting a threshold |

Every one was proved non-vacuous by breaking the thing on purpose and watching it
fail; the failure lines are in the commit message.

## Running the gated eval

```powershell
$env:DOTCHARTER_AUDIO_EVAL = "1"
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-class "*SectionProposalEvalTests"
```

It picks the first corpus song with two charted tone changes in its first
`DOTCHARTER_AUDIO_EVAL_SECONDS` (default 150) seconds, separates that excerpt
once (the stem cache keys on CONTENT, so a second run separates nothing), scans
it with NO chart input, and reports how many charted changes the audio found
within ±2 s. The report lands in `artifacts/section-eval/<stamp>.txt`.

**Recall is reported, not asserted.** A threshold here would be a number invented
today and defended for ever — see the audio eval's own rule in
[../testing/audio-eval.md](../testing/audio-eval.md). The first run's numbers are
above; a run with a warm cache is the scan alone (~1 s per second of excerpt).

## What this lane still needs

- **Re-calibration against real separated stems — this is the top item, and the
  eval above says why.** `DefaultMinNovelty` and `ConfidenceScale` were both set
  from `ToneSimilarity`'s documented scale for *rendered* audio, and a separated
  stem sits an order of magnitude further up that scale. They need a corpus, not
  another synthetic splice.
- **A recall number over more than one song.** The eval scans one; the honest
  version walks the corpus and separates the two populations (songs whose chart
  has many changes against songs with two) rather than averaging them.
- **A curve the thresholds can live on.** The most likely shape of the fix is to
  score novelty against the stem's OWN adjacent-window baseline (a curve
  normalised by its median, so "how unusual is this join for this song") instead
  of against an absolute distance — but that is a hypothesis, and it needs the
  corpus run before it is a plan.
- **The gain heuristic against real stems.** Its two ratio thresholds come from
  synthetic signals; nothing has yet checked them against a separated guitar.
- **A second stem in one proposal pass.** The proposer answers for ONE stem; a
  song where the bass changes tone and the guitar does not needs two calls today.
- **The UI lane's `ConfigureSections`** — until it resolves these two services,
  `UI/ServiceRegistrationReachabilityTests` reports them as dead registrations.

## Keeping this current

Re-measure every number above rather than carrying it forward; the extraction
cost in particular depends on the box. Add a row to **Files** when a class
appears, and a row to **Tests** when a case does. If a threshold moves, move the
sentence that says why it was set where it was.
