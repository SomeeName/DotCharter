# Audio eval — the rig ceiling (`DotCharter.Tests/Ai/AudioEval/`)

The symbolic eval ([ai-eval.md](ai-eval.md)) grades a designed rig against the
converted Rocksmith tone by comparing MODULE LISTS. Nothing is rendered and
nothing is listened to. This one renders, and asks a different question:

> **How close does a KNOWN-CORRECT rig get to the actual record?**

That number is the **rig ceiling**, and every other lane of "match a tone from
audio" is conditional on it. It is the distance achieved by the song's own
Rocksmith tone — chosen by a professional, converted by the app's own
`RsToneConverter`, rendered through the app's own engine. No search, no trained
estimator and no knob refinement can beat the right answer. If the ceiling is
not clearly better than an information-free `overdrive → amp → cab`, then the
remaining distance is separation bleed, the mix, mastering and the reference DI —
and the feature cannot work the way it was specified, whatever is built on top.

## Running it — DETACHED, and that is a hard rule

```powershell
# The only supported way to run it. Returns at once and prints the log path.
pwsh tools/audio-eval/Run-AudioEval.ps1 -Match -K 512 -Build
Get-Content artifacts/audio-eval/<stamp>/run.log -Tail 30 -Wait
```

**Never launch a run of this lane attached to a shell, and always give it
retries.** `tools/audio-eval/Run-AudioEval.ps1` starts it hidden at LOW priority,
launches the MTP test EXECUTABLE directly (so the exit code it records belongs to
the eval and not to MSBuild), samples commit headroom and RSS every 30 s into
`artifacts/audio-eval/<stamp>/run.log`, keeps stderr in its own file, and
relaunches on a non-zero exit.

### What kills a long eval: TWO failure modes, and only one of them is understood

Nine runs across two lanes have now died mid-flight. They are **not one bug**,
and the exit code separates them cleanly. Three hypotheses were tested first and
all three died:

- **NOT the agent harness's tool timeout.** Measured directly: a foreground
  command that outlives its Bash-tool budget is **moved to the background and runs
  to completion** (a 90 s command under a 20 s budget exited 0). The harness does
  not kill it — and the durations (8 m 55 s … 46 m 58 s, no two alike) rule out
  any timeout on their own.
- **NOT a runner timeout.** No `xunit.runner.json`, no `testconfig.json`, no
  `.runsettings` anywhere in the repo; no `--timeout` in any script or workflow;
  no assembly-level or per-`[Fact]` timeout attribute. xunit.v3 on
  Microsoft.Testing.Platform defaults to no session timeout.
- **NOT memory, and this is the hypothesis that looked strongest and was wrong.**
  The box does run near its commit limit (63.9 GB RAM + a 9.4 GB pagefile =
  73.1 GB, pagefile peak at its allocated size), and Windows logged a
  `RADAR_PRE_LEAK_64` resource-exhaustion diagnostic on 2026-09-12 — **naming
  `GuitarPro.exe`, not the eval.** The 2026-09-13 kill settles it: at the instant
  the process died there were **37.4 GB of commit free and 40.4 GB of physical
  free**, the launcher's sampler had logged 24–40 GB of headroom for the preceding
  ten minutes, and peak RSS was 5.7 GB. *Do not re-open this one on the strength
  of a scary-looking pagefile number.*

**Mode A — exit `-1` (0xFFFFFFFF): an external kill, and the exit code names the
caller.** Zero-byte stderr — no `Fatal error.`, no unhandled exception, no WER
record, nothing the CLR writes on its own way out. `-1` is what .NET's
`Process.Kill()` and PowerShell's `Stop-Process -Force` pass to
`TerminateProcess`; `taskkill /F` would leave `1`. The 46 m 58 s death landed
within two minutes of `ui-lane-c` and `ui-lane-d` writing
`obj/Debug/.../Avalonia/references`, i.e. of two other agents starting builds. The
cause is this repo's own instruction: `AGENTS.md` tells every agent to *"Kill
stray test hosts before rebuilding … named `testhost.exe` **and**
`DotCharter.Tests.exe`"*, and by image name that is **everyone's** eval.

> **Kill test hosts by PID or by working directory, never by image name.**
> `Get-CimInstance Win32_Process -Filter "Name='DotCharter.Tests.exe'"` gives you
> `CommandLine` and `ExecutablePath`; kill the ones under *your* worktree. If you
> are about to kill by image name, you are about to end someone else's hour.

**Mode B — exit `1`, and it is NOT diagnosed.** Stdout stops immediately after
xunit's `Starting: DotCharter.Tests`, stderr is empty, no test result is
reported, and `dotnet test` renders it as `Es wurden keine Tests ausgeführt`.
Seen four times on 2026-09-13 in this lane (twice within 30 s of launch) and —
the load-bearing observation — **independently in the match-quality lane, whose
K = 400 library build died twice at 8 m 55 s in a test that touches neither
`AudioEvalCorpus` nor `AudioEvalTests`.** So mode B is not the corpus, not this
test class, and not the report writer. What the two share is a **long native
render loop** through `juce_sharp.dll`. Two candidates remain and the evidence
does not yet separate them:

1. an external `taskkill /F`, which also exits `1`; or
2. the native engine ending the process itself — an `ExitProcess`/`abort` on the
   render path would produce exactly this signature, and unlike a managed fault
   it writes nothing.

**Do not write this up as solved.** The next step is a crash dump: enable WER
`LocalDumps` for `DotCharter.Tests.exe` (HKLM, needs elevation) or attach a
native debugger to a long render loop, and check whether a dump appears at all —
an external kill produces none, a native abort does.

Until then, `-Retries` is the mitigation for both modes: every expensive artifact
is cached (stems by content, excerpts, the retrieval library by its cache key),
the report is rewritten after every row, and `DOTCHARTER_AUDIO_EVAL_ONLY` fills
whatever a partial report is missing. **Treat a partial report as a normal
outcome** — the 2026-09-13 re-measurement was assembled from four runs for
exactly this reason.

The raw invocation below still works; it is here so the variables are documented
in one place, not as a recommendation.

```powershell
$env:DOTCHARTER_AUDIO_EVAL = "1"
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-class "*AudioEvalTests*"
```

| Variable | Effect |
|---|---|
| `DOTCHARTER_AUDIO_EVAL` | must be exactly `1`; anything else **skips** |
| `DOTCHARTER_AUDIO_EVAL_MAX` | cap the number of songs (default: the whole corpus) |
| `DOTCHARTER_AUDIO_EVAL_ONLY` | comma-separated song-id/title substrings; only those rows are scored. For re-running the few songs a previous run lost on |
| `DOTCHARTER_AUDIO_EVAL_DI` | a WAV to use as the reference DI instead of the generated one |
| `DOTCHARTER_AI_EVAL_CORPUS` | the corpus root, if not the `TestData` junction |
| `DOTCHARTER_MATCH_EVAL` | `1` adds the END-TO-END candidates — see below. Off by default: it multiplies the lane's cost by the matcher's render budget |
| `DOTCHARTER_MATCH_EVAL_K` | K for the retrieval library (default 96 here; the shipping default is 4,000) |
| `DOTCHARTER_MATCH_EVAL_RENDERS` | the matcher's per-song render budget (default 16) |
| `DOTCHARTER_MATCH_EVAL_WALL_SECONDS` | the matcher's per-song wall budget (default 60) |
| `DOTCHARTER_MATCH_EVAL_CHARTED_ONLY` | `0` also runs the matcher on `head-of-song` rows (default: it does not) |
| `DOTCHARTER_MATCH_EVAL_NOCHART` | how many songs also get the NO-CHART treatment (naive 30–60 s span). 0 = off |

**It is opt-in because it separates stems**, which is minutes per song the first
time — the repo's standing rule that a research harness is opt-in or it is part
of the gate. The *offline* half of the lane is not opt-in and does run in the
default gate: `ToneFeatureExtractorTests`, `ToneSimilarityTests`,
`RigRendererTests`, `RigRendererNativeTests`, `StemIsolatorTests`.

Output: `artifacts/audio-eval/<stamp>/report.md` — aggregate (mean, median, p10,
p90) per candidate, then one row per song with its span and where the span came
from. Excerpts are cached in `artifacts/audio-eval/excerpts/`.

## The measured ceiling (`tonefx-v2`, 23 DISTINCT charted songs)

> **There is currently no measured rig ceiling, and this section's predecessor
> said the opposite.** Re-measured after the corpus fix, on 23 charted rows over
> 23 distinct (audio, span) pairs:

| candidate | charted mean | median | p10 | head-of-song mean | median |
|---|---:|---:|---:|---:|---:|
| **`rs-truth` (the "ceiling")** | **0.8849** | 0.8377 | 0.5292 | 1.5047 | 1.4799 |
| `floor-generic` | 0.8701 | 0.8716 | 0.5061 | 1.5671 | 1.5332 |
| `floor-dry` | 0.9584 | 0.9533 | 0.6793 | 1.4300 | 1.4200 |
| `baseline-tonefromaudio` | 0.8022 | 0.8146 | 0.5699 | 1.3957 | 1.3449 |

**`rs-truth` beats `floor-generic` on 13 of 23 rows and
`baseline-tonefromaudio` on 10 of 23, and loses to both on the mean.** The
professional's converted rig is a coin flip against an information-free
`overdrive → amp → cab`. By this file's own stated test — *if the ceiling is not
clearly better than an information-free rig, the remaining distance is separation
bleed, the mix, mastering and the reference DI* — **the answer that was "go" is
now "unresolved"**, and the 0.60 margin that produced the "go" was the tone-JSON
resolver scoring ~5 recordings 21 times.

`floor-dry` bounds it further: the bare medley with **no rig at all** is 0.958
against the truth's 0.885, so the entire tonal contribution of a professional's
rig measures **0.073** here — inside the metric's own 1.56× content noise.

**Do not build a claim on a rig ceiling until this is re-established or retired.**
The full write-up, the per-song table, the end-to-end matcher numbers and the
no-chart delta are in the match-e2e-eval-unique-2026-09-13 document in the owner's
out-of-repo DotCharter-local\scratch folder.

## The measured ceiling (`tonefx-v2`, 35 songs, 15 min) — PSEUDO-REPLICATED

> **Every number in this section is dead.** It was measured through the tone-JSON
> folder resolver, so its "n=21" charted rows were roughly five distinct
> recordings scored several times each (see "What a row is"). The SHAPE of the
> finding — the ordering inversion on fallback rows, `floor-generic` being the
> worst candidate on charted spans — survived the re-measurement; the VALUES did
> not. Kept because the shape is still the reason the lane splits its populations,
> and deleting it would invite someone to re-derive it. Quote the 2026-09-13
> re-measurement instead.

**Split by span source, because the two populations do not merely differ — their
ORDERING inverts.**

| candidate | charted spans (n=21) | | | head-of-song (n=14) | |
|---|---:|---:|---:|---:|---:|
| | mean | median | p10 | mean | median |
| **`rs-truth` (the ceiling)** | **0.7505** | **0.6366** | **0.5082** | 0.9917 | 0.8968 |
| `floor-generic` | 1.3519 | 1.3861 | 1.0204 | 0.8680 | 0.7702 |
| `floor-dry` | 0.8067 | 0.8710 | 0.7131 | 1.0867 | 1.1448 |
| `baseline-tonefromaudio` | 1.2401 | 1.2905 | 0.7688 | 0.7456 | 0.6807 |

On the 21 rows where the span is the tone's own charted span, the ceiling beats
the information-free floor on **18/21** and the app's existing audio-to-tone
baseline on **18/21**, by a mean margin of 0.60 and 0.49 respectively. **That is
the result the rest of the feature is conditional on, and it says go.**

On the 14 fallback rows the ordering inverts — the floor "beats" the truth. That
is not noise, it is the wrong-span signature: those rows score the intro's tone
against whichever rig the song's elected tone happens to be. **Never read the
all-rows mean as a ceiling.**

Three things in that table are worth arguing with rather than quoting:

- **`floor-dry` is only 0.06 behind the ceiling.** The bare reference DI, no rig
  at all, scores 0.81 against a mastered record's guitar stem. Some of that is
  real (a DI and a mixed guitar share a fundamental range and a dynamic
  envelope); some of it is the metric being generous to a neutral spectrum. It
  bounds how much of the 0.60 gap is *tone* rather than *presence*.
- **`floor-generic` is the WORST candidate on charted spans**, well behind doing
  nothing. An `overdrive → amp → cab` at catalogue defaults is not a neutral
  guess; it is a specific, loud, mid-heavy guess. A search lane that reports
  "better than the floor" should check which floor.
- **`baseline-tonefromaudio` loses to the dry DI on charted spans** (1.24 vs
  0.81). The shipped archetype path is calibrated on a performance's crest factor
  and, as `DotCharter.Analysis/AGENTS.md` already warns, misreads steady tones —
  a separated stem is exactly that. It is the number to beat and it is beatable.

`tonefx-v1` measured the same corpus at 0.8284 / 1.2072 (ceiling / floor) on the
charted rows with 16/21 and 15/21 win rates, so v2's log bands and modulation
block are worth 0.08 on the ceiling and 0.22 of extra separation.

## End to end: `matcher-top1` and `matcher-bestN`

The ceiling above is what a KNOWN-CORRECT rig scores. `DOTCHARTER_MATCH_EVAL=1`
adds the only number that says whether the feature works: what `ToneMatcher`
(retrieval over the library, then CMA-ES over the real renderer —
[ai-match-search.md](../services/ai-match-search.md)) actually finds when it is
given the isolated stem and **no prior**.

The protocol, and every part of it is load-bearing:

- **One DI for the whole row.** With the matcher on, the reference DI becomes
  `ReferenceDi.Medley()` (20 s, four variants) for EVERY candidate, including
  `rs-truth` — because the retrieval library's features are renders of the
  medley, and retrieving over the medley while scoring over the 12 s phrase would
  measure the DI change as well as the rig. **A medley ceiling is therefore not
  comparable to a phrase ceiling from an earlier run**; the report header says
  which was used.
- **The matcher's own distance is NOT the reported one.** It ranks with
  `ToneSimilarity + 0.15 · MultiScaleSpectralDistance`, and that second term is
  only meaningful between two renders of the same dry take — here the target is a
  separated stem, so it is not. Every candidate it returns is re-rendered and
  re-scored with the plain `IToneSimilarity` that scores `rs-truth`. That costs
  one render per candidate and it is the only way the columns mean the same
  thing.
- **`matcher-bestN` is the best of the returned candidates under THAT re-scoring**,
  which is not necessarily the matcher's own first choice. The gap between
  `matcher-top1` and `matcher-bestN` is the cost of the ranking loss disagreeing
  with the reported one.
- **The tally is per row, not a mean.** "As close as the authored rig, or closer,
  on most songs" is a count; `MatchEvalReport.Tally` counts it, a tie counts as a
  LOSS, and a row where either side did not score is `unscored` rather than
  either. That arithmetic and the table run in the DEFAULT gate
  (`MatchEvalReportTests`) — the eval that feeds them does not.
- **`head-of-song` rows get no matcher run by default.** A matcher number on a
  row whose span is the intro grades the SPAN, not the search.

`DOTCHARTER_MATCH_EVAL_NOCHART=N` then answers the question the corpus cannot:
the same songs with the chart thrown away and a naive fixed **30–60 s** span,
which is what a song nobody has charted gets today.

### The end-to-end number (K = 512, 16 renders/song, 23 DISTINCT songs)

| candidate | rows | mean | median | p10 | p90 |
|---|---:|---:|---:|---:|---:|
| `rs-truth` | 23 | 0.8849 | 0.8377 | 0.5292 | 1.3384 |
| `baseline-tonefromaudio` | 23 | 0.8022 | 0.8146 | 0.5699 | 1.1627 |
| `floor-generic` | 23 | 0.8701 | 0.8716 | 0.5061 | 1.3961 |
| `floor-dry` | 23 | 0.9584 | 0.9533 | 0.6793 | 1.1697 |
| **`matcher-top1`** | 23 | **0.4830** | 0.3994 | 0.2958 | 0.6826 |
| **`matcher-bestN`** | 23 | **0.4685** | 0.3957 | 0.2824 | 0.6294 |

**`matcher-top1` beats `rs-truth` on 23 of 23**, mean margin 0.4019, and beats
`floor-generic`, `floor-dry` and `baseline-tonefromaudio` on 23 of 23 each. The
sweep survived de-replication on a 4–5× larger population. **Three things must
travel with it:**

- **It is beating a ceiling that is itself a coin flip** — see the 2026-09-13
  ceiling section above. A search that optimises `ToneSimilarity` directly beating
  a rig that does not is partly a statement about the metric.
- **Two thirds of it is a library LOOKUP, not a search.** 15 of 23 rows came back
  `retrieval` with no CMA-ES at all, eight of them having spent only 3 renders;
  mean spend was 9.1 of a 16 budget. Refinement is largely unmeasured here.
- **The rigs are closer to the truth's family than at K = 96 but still not it.**
  14 of 23 top-1 rigs contain an amp and 20 contain a cab (at K = 96, an
  amp-less, cab-less `octaver→klon→tapesat` won six rows) — yet
  `limiter→octaver→klon→cab` still wins five rows spanning Clean, Lead, Solo1,
  Acoustic and Chorus tones, against truth rigs that are all `…→amp→cab→eq→…`.

Self-distance floor across those rows: min 0.1480, max 1.7396, mean 0.6231, and
on **4 of 23** it sits above every candidate's distance. Better than 2026-09-12's
eight of twelve, still not a floor.

No-chart (naive 30–60 s span, 5 songs, complete): the matcher beats that span's
own `rs-truth` on **5 of 5**, and the absolute distance moves by −0.04 to +0.21
(mean |delta| 0.082), three of five for the worse. One song retrieves a different
FAMILY on the naive span than on the charted one.

**Measured at `3ce70e550`, before `88510d01b`** changed `RankingDistance`,
`MaxRenders`/budget, `RefineCandidates` and the `RigLibrary` cache key. Re-run
before comparing with anything on current `main`.

### The first measured end-to-end number (K = 96, 16 renders/song) — PSEUDO-REPLICATED

| candidate | rows | mean | median | p10 | p90 |
|---|---:|---:|---:|---:|---:|
| `rs-truth` | 12 | 0.7480 | 0.6953 | 0.6413 | 0.9076 |
| `baseline-tonefromaudio` | 12 | 0.6964 | 0.6713 | 0.6668 | 0.7492 |
| `floor-generic` | 12 | 0.7720 | 0.7886 | 0.6375 | 0.8716 |
| `floor-dry` | 12 | 0.8383 | 0.8246 | 0.7602 | 0.9690 |
| **`matcher-top1`** | 12 | **0.4761** | 0.4909 | 0.4154 | 0.5252 |
| **`matcher-bestN`** | 12 | **0.4593** | 0.4631 | 0.4154 | 0.4909 |

**`matcher-top1` beat `rs-truth` on 12 of 12 charted rows**, mean margin 0.272 —
and **three caveats have to travel with that sentence**:

- **Those 12 rows were ≈ 5 distinct songs, and every number in this section is
  therefore DEAD.** `AudioEvalCorpus.FindSongFolder` matched an entry to a folder
  by the first 200 characters of its tone JSON, so every song that REUSED a tone
  definition resolved to the same folder and scored the same audio; three
  "different" songs produced identical distances to four decimals on all seven
  candidates. Fixed 2026-09-13 (see "What a row is"), and the corpus now resolves
  to **35 rows over 35 distinct audio files, 23 of them charted**. **Do not quote
  this table, the 0.7505 ceiling, or any other aggregate this lane published
  before 2026-09-13.** The re-measurement is the
  match-e2e-eval-unique-2026-09-13 write-up in the owner's out-of-repo
  DotCharter-local\scratch folder.
- **The rigs it finds are not the family the truth is.** `octaver→klon→tapesat`
  — no amp, no cab — was the top hit for six rows spanning a Clean, a Chorus and
  an Octave_Distortion tone. The search optimises the metric directly and
  `rs-truth` does not, so at 1.56× content invariance, beating the ceiling is
  partly evidence that the METRIC is gameable.
- **The self-distance floor is not a floor here.** Measured 0.3153–1.0822, and on
  eight of twelve rows it sits ABOVE every candidate's distance: the target's own
  content variation across the excerpt exceeds the distance to any rendered rig.

On the NO-CHART span the verdict survives — the matcher still beat that span's own
`rs-truth` on both songs measured — but the absolute distance moved by −0.12 and
+0.24, comparable to the whole margin, and one song retrieved a different FAMILY
on the naive span than on the charted one.

**Cost, measured on this box in Release:** one render of the 20 s medley plus
`ToneFeatureExtractor.Extract` is **2.55 s** (0.44 s of it the extractor; 6.51 s /
4.31 s in Debug). So K = 4,000 is ≈ 2 h 50 m of one-off library build, K = 96 is
4 m 05 s, and a 16-render match is 24–58 s. **`ToneMatcher`'s shipping defaults
(`MaxRenders` 400, `Budget` 60 s) disagree by 17×** — the wall budget expires
after ~23 renders, so the render cap is unreachable on real hardware.

**The budget floor is sharp.** `ToneMatcher` skips a refinement whose share of the
remaining budget is < 8, and CMA-ES needs λ = 4 + ⌊3·ln n⌋ ≈ 12–14 for one
generation on a real chain. At `RetrievalTopK` 3 / `RefineCandidates` 1, **16
renders is the minimum at which CMA-ES runs at all**; at 12 the lane silently
measures retrieval only. Five of the twelve rows above were pure `retrieval` even
so — and still won.

### The report is written after EVERY row, and that is not a nicety

The test host was terminated three times mid-run on 2026-09-12 — 15 m 56 s into a
35-row pass, 13 m 13 s into the re-run and 10 m 44 s into the no-chart pass —
reporting `Es wurden keine Tests ausgeführt` and exit **−1**. All three lost every
row they had already scored, because the report was only saved at the end. It is
now rewritten after each row, which is the only reason the 2026-09-12 table exists
at all. **Keep that.**

As of 2026-09-13 there are TWO kill modes and only one is understood — an
external `Stop-Process` (exit −1) and an undiagnosed exit `1` that also hits a
lane which never touches this test class. See "What kills a long eval" above; it
is neither memory nor a timeout. It is also **not a cap of ~20 rows on this
lane**: run through `tools/audio-eval/Run-AudioEval.ps1` with `-Retries`, and use
`DOTCHARTER_AUDIO_EVAL_ONLY` to fill any gap a partial report leaves.

## What a row is

| Piece | Where it comes from |
|---|---|
| truth rig | `ToneEvalCorpus.LoadLocal()` — one elected tone per song, already converted |
| song audio | the largest non-preview `.wav` beside the song's `.rs2dlc`, in the ONE folder whose package carries the entry's title + artist |
| span | the arrangement XML's `<tones>` markers: the first `<tone time name/>` matching the elected tone, or `<tonebase>` if the base tone is the elected one |
| target | that span's **guitar stem**, separated once and cached forever |
| candidates | `rs-truth` (the ceiling), `baseline-tonefromaudio` (what the app already does), `floor-generic`, `floor-dry` |

**A row's audio is resolved by TITLE + ARTIST, and uniqueness is REQUIRED.**
Until 2026-09-13 it was resolved by the first 200 characters of the entry's TONE
JSON, so every song that reuses a tone definition — the normal case for one
author — resolved to whichever folder was enumerated first and scored that one
song's audio N times. The 2026-09-12 end-to-end report's twelve charted rows
were ≈ five distinct recordings; seven candidates agreed to four decimals across
three "different" songs. `AudioEvalCorpus` now folds the identity through
`ToneEvalCorpus.TryReadPackageSongId` / `ToneEvalEntry.MakeSongId` — the same
normalisation the symbolic corpus keys on, shared rather than re-derived — and
**reports** (never throws) when zero or two folders match, or when two different
songs land on one audio file anyway. `AudioEvalCorpusResolutionTests` pins it in
the default gate over a synthetic corpus whose two songs share one tone body.

**The report prints the DISTINCT audio count once per run, and that is the sample
size.** Measured 2026-09-13 on the local corpus: **35 rows over 35 distinct audio
files, of which 23 are charted rows over 23 distinct (audio, span) pairs.**
A row count is not an N, and nothing in the old report said so. **Every aggregate
this lane published before that date — the 0.7505 / 0.5082 ceiling "on n=21"
included — is pseudo-replicated and must not be quoted.**

**The span is not a detail.** Scoring a "Solo" tone against the first thirty
seconds of a song measures the intro's rhythm tone against a lead rig and calls
the difference a ceiling. Rows that fall back to the head of the song say
`head-of-song` in their `SpanSource` column, so the two populations can be
separated instead of averaged.

**Excerpts, not songs.** htdemucs processes in segments, so separating a
30-second clip gives the same stem as separating the 30 seconds inside a
four-minute song — but 35 excerpts is 17 minutes of audio against two and a half
hours. The stem cache keys on file CONTENT, so each excerpt gets its own durable
entry and a second run separates nothing.

## The reference DI, and why it is generated

A match compares a stem off a record against a rig rendered over SOME dry take —
we never have the record's own DI. That take ships with the app, so it has to be
redistributable, and the licensed electric-DI corpora are not:

| Set | Licence | Why not |
|---|---|---|
| IDMT-SMT-Guitar | CC BY-NC-ND | no commercial use, and ND forbids redistributing our renders of it |
| EGDB | unstated | ask the authors first |
| MUSDB18-HQ, MoisesDB | NC | same problem one level up |
| EGFxSet | CC BY 4.0 ✅ | usable — but isolated single notes, no chords and no performance |
| GuitarSet | CC BY 4.0 ✅ | usable — but an ACOUSTIC guitar through a hex pickup |

So `Ai/Match/Similarity/ReferenceDi.cs` **generates** one: Karplus-Strong plucks
with a pick-position comb, a fixed-seed LCG, a 12-second phrase of power chords,
a single-note line and a sustained chord. Zero bytes of audio committed, no
licence question, and bit-identical on every machine — which the corpora would
not be, so two eval runs are comparable.

It is a pluck, not a guitar: no fret noise, no pick scrape, no player. It excites
the right range with a plausible harmonic series and a plausible envelope, which
is what a drive needs to clip and a cab needs to filter. Where the *choice* of DI
matters, point `DOTCHARTER_AUDIO_EVAL_DI` at a real one; the two IDMT clips under
`TestData/pedal-ab/` are fine for that in-house measurement and for nothing that
ships.

`ReferenceDi.Generate(variant)` gives a DIFFERENT performance of the same
instrument. That is what makes content invariance testable — and what a retrieval
library should render over, so the library does not learn the phrase.

## The metric, honestly

`ToneSimilarity` over `tonefx-v1`: a weighted, block-length-normalised L2 across
nine blocks (see `ToneFeatureLayout`, which holds the layout AND the weights in
one place). Lower is better; 0 is identical.

**Measured content invariance is 1.56×** (`tonefx-v2`, 2026-09-12): a rig change costs 1.5–1.8×
what a riff change costs, on the same synthesised DI through a clean amp versus a
fuzz. That is clearly above 1 and clearly not the order of magnitude a trained
timbre embedding would give. **Read every number in a report with that in
mind** — a difference of a few percent between two candidates is inside the
content noise, and the p10/p90 spread matters more than the mean.

Six things were found by the tests and by the search lane, and are worth not
re-introducing:

1. **Spectral flux normalised by the current frame alone is unbounded.** A loud
   frame followed by a quiet one produced a flux of several hundred; two
   encodings of the same audio came out 200× further apart than a clean amp is
   from a fuzz. Normalise by both frames.
2. **A peak-picked harmonic-to-noise ratio is chaotic.** Which bins are local
   maxima flips on inaudible changes. Wide-band integrals (2–6 kHz against
   60–800 Hz, plus that band's flatness) say the same thing and are stable.
3. **Spectral flatness over the full spectrum measures the empty bins above
   16 kHz**, not the tone. Everything is band-limited to 60 Hz–10 kHz and floored
   relative to the frame's own peak.
4. **Averaging per-frame LOG spectra keeps every note's harmonic comb.** Average
   POWER and take the logarithm once: the combs of different notes fill each
   other in and what is left is the envelope. This one change took content
   invariance from 1.04× (i.e. nothing) to 1.5×.
5. **A fine filterbank is the WRONG tool for a cepstrum.** The statistics bank
   and the cepstral bank have opposite jobs — resolve a filter versus smooth a
   spectrum into an envelope — and giving the cepstral one 64 log bands made the
   "envelope" a description of the chord (0.046 between two performances of one
   rig against 0.011 between two rigs). 32 bands and 12 coefficients, coarser
   than the statistics bank, reverses that.
6. **Anchoring the distortion bands to the frame's own fundamental centroid
   measured worse**, on both sides: a window that moves with the riff varies more
   than a fixed one. The dynamics block, not the distortion block, is what
   actually separates clean from fuzz — by nineteen to one.

A width-3 median filter across mel bands was tried for the same purpose and
**removed**: measured, it took out more rig signal than riff signal.

**Resampling is not free.** A 48 → 44.1 → 48 kHz round trip costs roughly 0.6× a
clean-vs-fuzz distance. The production path never resamples — the stem cache
decodes at 48 kHz and `RigRenderer.RenderRate` is 48 kHz — but a future consumer
that compares across rates is comparing rates as well as tones.

`MultiScaleSpectralDistance` is the OTHER metric in the folder and it is **only
valid between two renders of the same dry take**; it is the search lane's
refinement loss, never a ranking one. `ToneSimilarityTests` pins that by showing
it rates the same rig on a different riff as WORSE than a different rig on the
same riff.

## Level

Everything is RMS-normalised to −26 dBFS with a −0.5 dBFS peak guard
(`LevelMatch`). RMS and not peak, because a distorted signal's crest is 10–15 dB
lower than a clean one's: peak-matching would hand every dirty candidate 10 dB in
every band, and the metric would grade partly on the squash it is supposed to be
measuring honestly. −26 and not −20 because the reference DI's crest is 23.9 dB,
and at −20 the peak guard bound and the RMS match silently did not happen.

## `NullDeviceRigHost` users are in the NativeEngine collection

`NativeEngineCollectionLintTests` finds test classes that open a native engine by
grepping their source for `new JuceAudioEngine(`, `new NullDeviceRigHost(` or the
`DCA_*` guard names. `NullDeviceRigHost` is on that list because it builds an
engine inside product code (on the null device, through the per-engine
`EnableNullDevice` flag since #85), so a class that uses it names neither. Two engines open at once on this box is how
the ASIO device gets taken exclusively.

## Follow-ups this eval exists to inform

- **search lane**: the retrieval library should be rendered over a MEDLEY of
  `ReferenceDi.Generate(variant)` takes, and refinement should use
  `MultiScaleSpectralDistance` only between candidates over the same DI.
- **model lane**: a trained embedding is the known fix for the 1.5–1.8× content
  invariance; that is the number to beat, and it is measurable before any
  training starts.
- **data lane**: `RigSamplingProfile` draws should be scored with the same
  `ToneSimilarity` so the library's coverage of the METRIC's space is visible.
