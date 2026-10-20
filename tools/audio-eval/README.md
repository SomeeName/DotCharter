# tools/audio-eval

The audio-level tone eval — **the rig ceiling**. Unlike `tools/ai-eval`, which
compares module lists, this one renders audio and measures how close a rig gets
to a real record.

There is no separate harness here: the eval IS the test, because it needs the
app's engine, its stem cache and its converter, and a second copy of that wiring
is a second thing to keep in sync. `Run-AudioEval.ps1` is a LAUNCHER, not a second
harness — it sets the environment, detaches, and logs.

```powershell
pwsh tools/audio-eval/Run-AudioEval.ps1 -Match -K 512 -Build
Get-Content artifacts/audio-eval/<stamp>/run.log -Tail 30 -Wait
```

**Run it through that script and not by hand.** It starts the eval hidden at LOW
priority, launches the test EXECUTABLE directly so the exit code it records is the
eval's own, samples commit headroom and RSS every 30 s into
`artifacts/audio-eval/<stamp>/run.log`, keeps stderr beside it, and relaunches on
a non-zero exit. Three runs on 2026-09-12 died
silently at 10–16 minutes and were written up as an undiagnosed kill. The cause is
another agent on the box running `Stop-Process -Name DotCharter.Tests` before its
own rebuild — exit −1, empty stderr, 37 GB of commit free at the moment of death —
so pass `-Retries` and never kill test hosts by image name. Evidence in
[docs/agents/testing/audio-eval.md](../../docs/agents/testing/audio-eval.md).

The raw invocation still works, and is what the script runs:

```powershell
$env:DOTCHARTER_AUDIO_EVAL = "1"
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-class "*AudioEvalTests*"
```

| Variable | Effect |
|---|---|
| `DOTCHARTER_AUDIO_EVAL` | must be exactly `1`; anything else skips the run |
| `DOTCHARTER_AUDIO_EVAL_MAX` | cap the number of songs (try `2` first) |
| `DOTCHARTER_AUDIO_EVAL_DI` | a WAV to use as the reference DI instead of the generated one |
| `DOTCHARTER_AI_EVAL_CORPUS` | corpus root, if not the `TestData` junction |

Output: `artifacts/audio-eval/<stamp>/report.md`.
Excerpt cache: `artifacts/audio-eval/excerpts/` (safe to delete; regenerated).
Stem cache: `%LocalAppData%\DotCharter\StemCache` — **do not delete it**, that is
the expensive half.

## First run needs a stem model

The eval separates the guitar out of each song. It uses the app's own backends in
the app's own order, and on a development machine the first one is the one that
works:

| Backend | Needs | Note |
|---|---|---|
| `OnnxStemSeparationService` | `htdemucs_6s.onnx` in `%AppData%\DotCharter\models` | 109 MB, CUDA/DirectML/CPU |
| `JuceStemSeparationService` | a `ggml-model-htdemucs-*-f16.bin` **and** a `juce_sharp.dll` built with `DOTCHARTER_STEMS_DEMUCS_CPP=ON` | the prebuilt DLL in an agent worktree is NOT — check with `strings juce_sharp.dll \| grep demucs` before assuming a missing model is the problem |

Both models are pinned by hash in the catalogues
(`OnnxModelCatalog`, `DemucsGgmlModelCatalog`) — download through the app's
Settings, or fetch the catalogue URL and verify the SHA-256 yourself.

The eval's `StemModelManager` is given an **in-memory** configuration naming
`htdemucs-6s-onnx`. That is deliberate: `ActiveModelId`'s setter persists through
the real configuration service, and an eval must not rewrite which stem model the
owner's app uses.

## Cost

Measured 2026-09-12 at `/LOW` priority: **17 min 38 s for all 35 songs** on the
first run (separation of a 30-second excerpt each, then four offline rig renders
per song), and **15 min 03 s** on the second, when every stem was a cache hit and
only the renders and the feature extraction ran. The `.dcstems` cache is keyed by
content, so the separation half happens exactly once per excerpt, ever.

Separating 30-second EXCERPTS rather than whole songs is what makes this
affordable: htdemucs processes in segments, so the stem is the same, and 35
excerpts is 17 minutes of audio instead of two and a half hours.

## Reading the report

- **Lower is better.** 0 is identical.
- `rs-truth` is the ceiling. Everything the search, data and model lanes build is
  graded against it.
- `floor-generic` is `overdrive → amp → cab` with default knobs — it knows
  nothing about the song. **A ceiling that does not beat it on a row means that
  row carries no usable tone information**, and the usual cause is visible in the
  span column.
- `floor-dry` is the reference DI with no rig at all.
- `baseline-tonefromaudio` is what the app already does today.
- The span column says where the 30 seconds came from. `head-of-song` rows fell
  back because no `<tones>` marker matched the elected tone; read them
  separately, not as part of the mean.

The metric's measured content invariance is 1.56×, so differences of a few
percent between candidates are inside the noise. **Read the charted-span
aggregate, never the all-rows one** — on the fallback rows the ordering inverts.

**And do not call `rs-truth` a ceiling without checking the number.** Re-measured
2026-09-13 on 23 DISTINCT charted songs, it beats `floor-generic` on 13 of 23 and
`baseline-tonefromaudio` on 10 of 23, losing to both on the mean. The earlier
18-of-21 result was measured through a resolver that scored ~5 recordings 21
times. See
[docs/agents/testing/audio-eval.md](../../docs/agents/testing/audio-eval.md) for
what that means and for the six measurement findings the tests caught.
