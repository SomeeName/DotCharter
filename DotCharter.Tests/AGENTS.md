# DotCharter.Tests Agent Guide

xUnit v3 suite on Microsoft.Testing.Platform, covering every other project
(`Core`, `Analysis`, `Services`, `Presentation`, `Presentation.Fakes`, `UI`, the
gallery and two tools). `Avalonia.Headless` + `Avalonia.Skia` for view-model and
headless-UI tests. The default gate is ~19,500 cases (19,489 on 2026-10-11) and
the headless-UI suite ~990 (2026-10-04); **re-count, never carry a number
forward** — both move weekly. `tools/agent/Invoke-Gate.ps1` prints the live
count on every run.

This file holds the conventions every test must follow. Harness detail lives in
[docs/agents/testing/](../docs/agents/testing/) — read the routed file before you
touch that harness, and update it in the same change. How each rule was learned
is in `git log -p DotCharter.Tests/AGENTS.md`.

**Comparing totals:** a worktree count and a main-checkout count differ for two
reasons, neither a regression. `DotCharter.Tests/LocalOnly_*.cs` is git-ignored,
so those probes (some with no env gate and no `Assert`, by design) run only in the
owner's checkout; and `TestData/` is only found by a worktree INSIDE the repo.
Do not report the difference as a finding.

**Local corpus:** the owner's corpus lives in sibling `DotCharter-local/TestData`,
exposed through the ignored root `TestData/` junction. Never remove it during
worktree/output cleanup; an external worktree needs its own link.

**No machine paths, no real names (public repo).** `Infrastructure/PersonalDataLintTests`
fails the gate when a TRACKED text file (`git ls-files`, so git-ignored `LocalOnly_*`
probes are exempt) holds a real name, a personal e-mail or an owner-machine path; its
patterns live in that file and, mirrored, in the public export's leak check
(`tools/release/PublicSnapshot.psm1`), which `Infrastructure/PublicSnapshotExportTests`
keeps equal. Sample data uses neutral names; synthetic paths use
generic examples (`E:\Rocksmith\dlc`). A test that needs the owner's files finds them
through `Infrastructure/LocalFixtures` and SKIPS cleanly when they are absent:

| Variable / source | What it points at | Used by |
|---|---|---|
| `DOTCHARTER_RS7_DIR` | the folder of RS7 (7-string) reference packages + note sheets | the plugin's RS7 read-back tests (the package reader is an optional plugin, #889) |
| `DOTCHARTER_RS2014_DIR` | a Rocksmith 2014 install root; unset = the install the plugin's locator detects | the plugin's owner-only package tests, `TestRsGameTables` (the game tables, #755) |
| `DOTCHARTER_RS_GEAR_DIRS` | folders (`;`-separated) of gear manifests and soundbanks already extracted from the game's gear archive, flat; beats the install for `TestRsGameTables` (a partial extract compares only the groups it holds) | `LocalRsGameTablesTest`, every test that calls `TestRsGameTables.UseReal()` / `TryReal()` |
| `DOTCHARTER_RS_GAME_TABLES_DUMP` | a folder OUTSIDE the repo that `LocalRsGameTablesTest` writes each group's canonical text to, to diff a hash mismatch | `LocalRsGameTablesTest` |
| `DOTCHARTER_LYRIC_MODEL_DIRS` | lyric model folders (`;`-separated, each with `model.onnx` + `vocab.json`), e.g. a re-exported, stripped archive pair (#222; the published v1 archives fail the scan on purpose, they are stripped in memory instead). The graph scan fails a `Reshape allowzero`; the DirectML smoke encodes 24 s on DirectML and on the CPU and needs ≥95 % argmax agreement (skips without a DirectML adapter) | `Services/Audio/Lyrics/LyricModelGraphTests`, `LyricModelDirectMlSmokeTests` |
| `DOTCHARTER_RS_KEYS` | a file holding the owner's Rocksmith keys — an `appconfig.json` (its `RocksmithKeys`) or an old `rocksmith-keys.txt` — when they are not in the user config folder's `appconfig.json` (#756, #774; format in `docs/agents/services/parsing-psarc.md`). Read only. Without it the process runs on SYNTHETIC keys and every test that opens a stock package calls `TestRocksmithKeys.RequireRealKeys()` and skips | the plugin's read-back tests, `LocalFixtures.RocksmithInstall` |
| `DOTCHARTER_SPLIT_BASELINE` (`record`/`compare`) and its siblings | the package-reader split baseline (#899): it reads the owner's library through the package reader, so it lives with the plugin, with its runner script (#889). Hashes and counts only, written OUTSIDE the repo; every random or clock-drawn export value is pinned (`RocksmithBuildIdentity`), so two runs are byte-identical. The writer's intended changes it projects out are listed in `docs/agents/services/export-companions.md` (#907) | the plugin's tests |
| `DOTCHARTER_LYRIC_CORPUS`, `DOTCHARTER_LYRIC_SOURCES` (+ `DOTCHARTER_LYRIC_MODELS`, `DOTCHARTER_LYRIC_STEM_MODELS`, `DOTCHARTER_LYRIC_PROBE_OUT`) | the lyric probe corpus (#237), all OUTSIDE the repo: the built corpus (`corpus.json` + raw 16 kHz audio per song), the `*.lyric-truth.json` sources it is built from (the romaji charts from `TestData/EOF`; the stock English ones from the add-on's lyric-truth exporter (its `Local/` probes)), the lyric model root, the stem model folder and the report folder. Recipe in `docs/agents/services/lyric-autosync.md` § Probes | `Services/Audio/Lyrics/Probes/LyricCorpusBuildProbeTests`, `LyricCorpusProbeTests` |
| `DOTCHARTER_BENCH_TUNING_SHIFT` | a file path the live pitch-shift CPU table is written to (#627); unset = the test skips. Native engine on the headless Null device, no sound card; local measurement, asserts no timing. Run with `Invoke-Gate.ps1 -FilterClass "*TuningShiftCpuBenchTests"` (~5 min) | `Audio/TuningShiftCpuBenchTests` |
| `TestData/` (the junction above) | the corpus | `GpRealArtifactImportTests`, `GpLegacyImportTests`, `PlayheadContinuityProbeTests` |
| `Documents\Ample Sound\Tabs\Guitar` | the Ample Sound sample GP5 | `GpLegacyImportTests` |

Never put a default machine path in code — set the variable in your own environment.

**Every tracked binary or data fixture has a row in `DotCharter.Tests/Fixtures/PROVENANCE.md`** (path, origin, licence; a trailing `/` covers a folder). `Infrastructure/FixtureProvenanceLintTests` fails the gate on a fixture without a row and on a row whose file is gone. Never commit a recording or a note-for-note transcription of a real song: fixtures are self-made or vendored under a licence that allows it.

**No package reader in the public tree (#892).** `Infrastructure/PublicTreeHasNoPackageReaderTests` scans every tracked text file, comments included, for reader and game-package names and fails a tracked `.psarc`/`.sng`/`.bnk`/`.wem`; reword to "the package reader" rather than add to its `Allowed` list, whose entries must still exist and still hit.

**No credential shapes either.** A test that needs a token-shaped value takes it
from `Services/Diagnostics/FakeSecrets.cs` (split at the prefix in the source,
FAKE in the body); `Services/Diagnostics/FakeSecretFixtureTests` fails the gate on
a contiguous `ghp_…` / `github_pat_…` / `AKIA…` / `xoxb-…` / `sk-…` / JWT / PEM
private key in any tracked file, or a tracked `.pfx/.p12/.pem/.key/.snk/.jks`.
`gitleaks dir . --config .gitleaks.toml` runs clean over the tree; its allow-list
holds domain data only (gear keys), never a fixture.

## Layout

Mirrors the projects under test: `Core/`, `Analysis/`, `Audio/`, `Localization/`,
`Services/` (`Audio/`, `Parsing/`, `Editing/`, `Export/`, `Gameplay/`, `Import/`,
`Online/`, `Recovery/`, `State/`, `Update/`, …), `UI/`, `Infrastructure/`, plus:

- `Presentation/` — source and reflection rules over the two Presentation
  projects (`PresentationSourceRulesTests`, `PresentationTypeRulesTests`,
  `FakeVariantsTests`), each with a probe fact on a known-bad input from
  `Presentation/Fixtures/`; the screen-catalog lints (`ScreenCatalogTests`,
  `ContractBindingLintTests`, `ContractConformanceTests`) and the pure
  `NavigatorTests` / `DialogStackTests`.
- `UI/Goldens/` — the golden screenshots' pure half (`ScreenGoldenStoreTests`,
  default gate). `Goldens/ui/` holds the APPROVED PNGs + sidecars; only the owner
  adds to it, with `tools/agent/Update-Goldens.ps1 -Accept`.
- `Perf/` — mostly `*UiPerfSmokeTests`, excluded from the gate and run as their
  own process (below). **The folder is not the exclusion**: six classes in it do
  NOT match the filter and so DO run in the gate — `RefretPerfGateTests`,
  `StartupPerfGateTests`, `RenderPerfBaselineTests`, `DisplayRefreshProbeTests`,
  `EditorLatencyProfileTests` and `PlayModeEntryProfileTests` (the last two skip
  unless `DOTCHARTER_PERF_GATE=1`). A wall-clock assertion in one of those flakes
  the gate. `UiSweepUiPerfSmokeTests` + `UiSweep.cs` is the headless UI sweep over
  EVERY view — a new view is swept the day it compiles.
- `Rendering/Headless/` — GPU golden-frame tests of the offscreen Vulkan 3D
  highway and 2D timeline. Skip unless `DOTCHARTER_GPU_TESTS=1` (GPU) or `=sw`
  (Mesa lavapipe on the CPU, fetched by `tools/ci/Get-SoftwareVulkan.ps1`);
  `[Collection("HeadlessGpu")]`; `DOTCHARTER_GPU_DUMP=1` writes PNGs to
  `TestResults/headless/`.

| Doing this | Read this |
|---|---|
| adding or editing a VIEW, or a sweep finding you did not expect | [../docs/agents/testing/ui-sweep.md](../docs/agents/testing/ui-sweep.md) |
| touching anything under `Perf/`, or a headless failure | [../docs/agents/testing/perf-harnesses.md](../docs/agents/testing/perf-harnesses.md) |
| a golden-frame / GPU test | [../docs/agents/testing-headless-rendering.md](../docs/agents/testing-headless-rendering.md) |
| the usage guide's screenshots (`docs/guide/`) | [../docs/guide/README.md](../docs/guide/README.md): one manifest per page names each callout's control; `Perf/Guide/GuideCaptureUiPerfSmokeTests` regenerates the PNGs and lists (opt-in, `DOTCHARTER_GUIDE_CAPTURE=1`), `Guide/GuideManifestLintTests` (default gate) fails on a manifest name no `.axaml` declares |
| anything under `Ai/Dataset/` | [../docs/agents/testing/rig-dataset.md](../docs/agents/testing/rig-dataset.md) — its render tests SKIP without the JUCE `Null` device on purpose, and building this project fails MSB3021 while a corpus render holds `tools/rig-dataset`'s binaries |
| `Audio/Dsp/RsToneConversion/Coverage/` or `Fidelity/` (the Rocksmith gear audit and tone calibration harness) | [../docs/agents/services/rs-tone-coverage.md](../docs/agents/services/rs-tone-coverage.md) is GENERATED by `RsGearCoverageAuditTests` (`DOTCHARTER_REGEN_RS_COVERAGE=1` rewrites it; `DOTCHARTER_RS_LIBRARY_SCAN=1` rescans the library, read-only). `RsToneFidelityHarnessTests` renders through the real DLL; `DOTCHARTER_RS_FIDELITY_OUT=<dir outside the repo>` (+ `DOTCHARTER_RS_FIDELITY_LIBRARY=1`) writes the eval report and WAVs; the library eval renders every tone twice, on the estimate and LEVELLED through the product's meter (`Fixture.LevelAsync`), ~35 min |
| the auto-chart corpus score (`Analysis/AutoChartCorpusProbeTests`, `AutoChartScorer`) | [../docs/agents/testing/autochart-eval.md](../docs/agents/testing/autochart-eval.md) — env-gated, CPU-heavy, writes its report OUTSIDE the repo |
| anything else | the conventions below |

## Running

```powershell
# the gate — 4 concurrent shards (-Shards 1 = one process), then the 20 slowest classes
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate
# the RELEASE LANE — the slowest classes (tools/ci/release-lane.json), which -Gate leaves out;
# the train, the release preflight and CI always run it (-Gate -ReleaseLane = both)
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -ReleaseLane
# the headless-UI suite — its OWN processes (4 shards), after any view/XAML change
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless      # -Shards N changes the split
# both, at the same time when -MinFreeGB (12) is free (-Sequential: one after the other)
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate -Headless
# prove the gate shards + the release lane list exactly the pre-lane gate's cases (runs no test)
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -SelfTest -NoBuild
# the golden screenshots — a separate process (Skia drawing, en-US); the wrapper sets both
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Goldens
# what the wrappers run underneath, once per shard: the gate's shards add
# --filter-class lists (or, for the remainder, --filter-not-class lists); the
# headless shards one exact --filter-class per class in place of the wildcard
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-not-class "*UiPerfSmokeTests"
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-class "*UiPerfSmokeTests" --filter-not-class "DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests"
```

Measured 2026-10-04 on the owner's 16-thread box: the gate ~115–125 s of test
time (16,151 cases, ~1.1 GB); the headless suite **157–171 s over 4 shards** (992
cases, 1.7–2.3 GB a shard, 6.8 GB together) where one process took 582 s.

- **The gate is sharded by `tools/ci/GateShards.psm1`** (`Invoke-Gate.ps1 -Gate`,
  `Merge-Train.ps1`, CI's gate job with 2). Shards 1..N-1 each run a list of
  whole classes dealt heaviest-first by `tools/ci/gate-shard-weights.json`
  (classes of 1 s or more; `otherSeconds` sums the rest); shard N, the
  REMAINDER, runs `--filter-not-class "*UiPerfSmokeTests"` plus one
  `--filter-not-class` per listed class. So the shards are the gate exactly,
  whatever was added or renamed since the weights were measured (new classes
  run in the remainder; refresh with `-Gate -Shards 1 -UpdateShardWeights`).
  The listed names are capped at 20,000 characters: the remainder's command
  line must stay under Windows' 32,767. Every shard must write a TRX; the TRX
  files merge into `gate.trx`. Pinned by `Infrastructure/GateShardsModuleTests`
  (an exact-cover replay of xUnit's filter semantics) and, against the real
  runner, `Invoke-Gate.ps1 -SelfTest`. **Classes now run in different
  PROCESSES at once**: a test that writes a fixed path outside its own temp
  folder can collide with one in another shard (or in a headless shard, under
  `-Gate -Headless`).
- **The release lane is the gate's second half** (`tools/ci/release-lane.json`,
  read by `ReleaseLane.cs`, `GateShards.psm1` and `Invoke-Gate.ps1`): the heaviest
  corpus / integration / determinism classes, each with a `why` and its measured
  `seconds` (they balance the lane's shards; refresh with `-ReleaseLane
  -UpdateShardWeights`). `-Gate` leaves them out, `-ReleaseLane` runs exactly them,
  `Merge-Train.ps1` / `New-Release.ps1` / CI always run them. Add a class by its
  `FullName` (a nested class joins with `+`) with its reason, in the same change
  that makes it slow; **never to hide a flaky or slow UNIT test**. A renamed or
  deleted entry fails `Infrastructure/ReleaseLaneLintTests` (and a lane run names a
  listed class that reported nothing). That lint is what keeps a class from
  vanishing: it finds every test class by reflection and replays the real shard
  plans of every lane (default gate, release lane, headless, goldens) at 1, 2 and 4
  shards, demanding exactly one. A class under the lane also needs YOU to run it
  when you change its subsystem: `Invoke-Gate.ps1 -FilterClass '*<Class>'`.
- **A plain `dotnet test` (no filter) runs the default gate, not everything.**
  `TestEntryPoint.cs` replaces xUnit's generated `Main`
  (`XunitAutoGeneratedEntryPoint=false`) and adds
  `--filter-not-class "*UiPerfSmokeTests"` plus one `--filter-not-class` per
  release-lane class only when `dotnet test`
  (`--server dotnettestcli`) passes no `--filter*` / `--treenode-filter` option.
  An explicit filter, an IDE (`--server` JSON-RPC) or the console runner is
  untouched (`Infrastructure/DefaultRunFilterTests`). Every script passes a
  filter, so none of them changed. The human guide is [README.md](README.md).
- **Lint failure messages go through `Infrastructure/LintReport.Of(rule,
  offenders, fix, guide)`**, one offender per `path:line: what`. Every
  `*LintTests` class has a class-level `<summary>` of 8+ words
  (`Infrastructure/LintClassSummaryLintTests`).
- **MTP filters:** `--filter-class`, `--filter-not-class`, `--filter-method`,
  `--filter-namespace`; `*` only at either end; repeat a flag to OR, different
  flags AND. TRX: `--report-xunit-trx --report-xunit-trx-filename x.trx
  --results-directory d`. The root `global.json` opts `dotnet test` into MTP.
- **The goldens are excluded from the headless run by class name** — they need
  `DOTCHARTER_UI_SWEEP_DUMP=1` from session start, and fail "no golden" until the
  owner accepts baselines.
- **The filter is a PROCESS split, not a hang.** `Avalonia.Headless` owns a
  process-global dispatcher; in one testhost with the gate, almost every headless
  case fails (`The calling thread cannot access this object` / `You've caused
  dispatcher loop` out of `EnsureIsolatedApplication`) and takes unrelated gate
  tests with it. Never "fix" it by serializing the gate.
- **The gate's tail is single-threaded on purpose.** Members of every
  `DisableParallelization` collection (`CwdSerialized`, `AlphaTab`,
  `LocalizationSingleton`, `SerialDiskIo`, `HeadlessGpu`, `GameplayToneHud`,
  `FrameSnapshotPerfGate`, `FingeringOverlayStatic`, `HeadlessUI`) run one lane,
  AFTER every parallel collection has finished. **`NativeEngine` is NOT one of
  them:** a collection is already one lane, which is all its process-wide
  null-device env vars need, so it runs serialized WITHIN and in parallel with
  everything else. Its waits on native work are therefore 60 s deadlock
  detectors, never budgets. Use `DisableParallelization` only for state that
  tests OUTSIDE the collection also touch. **A collection holds the classes that
  touch its state and no others:** a class that builds its own temp folder and passes
  explicit paths does not belong in `CwdSerialized`; one that builds a tone coordinator
  without the HUD adapter does not belong in `GameplayToneHud`; one that only sets
  `CultureInfo.CurrentCulture` for its own flow (restored in a `finally`) does not
  belong in `LocalizationSingleton`. Those three collections have a lint
  (`Infrastructure/CwdCollectionLintTests`, `GameplayToneHudCollectionLintTests`,
  `CultureCollectionLintTests`) that fails a class which touches the state from outside its collection. The
  collections that guard shared native or process-global state (`HeadlessUI`,
  `HeadlessGpu`, `NativeEngine`, `AlphaTab`) keep every member, and `SerialDiskIo`,
  `FrameSnapshotPerfGate` and `FingeringOverlayStatic` keep theirs for real disk,
  timing and static-flag reasons.
- **Where the gate's wall clock goes (profiled 2026-10-04 from the TRX start/end
  times):** a CPU-saturated parallel phase, then a serialized tail of ~6 s. The
  tail is `NativeEngine` (`ToneLowEndResponseTests` is 40 s of genuine native
  DSP), and starting that lane inside the parallel phase, not after it, is what
  holds the gate to 115–123 s. **A class is ONE serial chain**: a class's cases run back to back, so
  a long chain inside ONE class is the shape to look for — not a slow-looking
  case. The scenarios that take minutes (the two full `ToneMatcherTests` matches,
  the `PublicSnapshotExportTests` and `PublicSyncGuardTests` pwsh runs) are nested
  classes, each its own collection, so they run side by side; each lists itself in
  `tools/ci/release-lane.json`. A fact that only needs a result the search does not
  depend on (the self-distance floor, the candidate cap) runs a small budget, not a
  full match.
- **A lint that parses every product file pays for the parse once per FACT unless
  it is shared.** `StaticLocalizationRatchetTests`, `MethodComplexityBudgetTests`
  hold one `Lazy` scan for all their facts, and `CommentHygieneTests`,
  `LintClassSummaryLintTests` and the localization scan read each file's text first
  and parse only a file whose text can hold what they count (a count of zero needs
  no syntax tree). Do the same in a new lint over the product tree.
- **A case that looks slow in a TRX is usually STARVED, not slow — measure it
  alone before optimising it.** Measured: `LiveDetectionAccuracyTests`' first
  case is 4.7 s alone and 45–53 s in the gate, `SyntheticPerformanceTests
  .Diagnostic_per_string_hit_rate` 4.9 s alone and 55–62 s. Splitting those two
  classes gained nothing (the phase is CPU-bound), so they were left alone.

### Headless-UI suite rules

- **Every `*UiPerfSmokeTests` class carries `[Collection("HeadlessUI")]`**, and
  every class that boots the headless session (`HeadlessSessionFixture.Get()`,
  `HeadlessUnitTestSession`, `HeadlessAppHelper`) is either in that collection or
  renamed to `*UiPerfSmokeTests` — `Infrastructure/HeadlessCollectionLintTests`
  (comment-stripped source for the use, reflection for the attribute, because a
  partial carries it on one part). Never put a default-gate class in `HeadlessUI`:
  `HeadlessSessionFixture.ShutdownAsync` resets `WritablePaths` overrides that two
  `CwdSerialized` classes own.
- **Dispatch through `HeadlessSessionFixture.Dispatch(…)`, never
  `HeadlessSessionFixture.Get().Dispatch(…)`.** It drains the app's Background
  prewarm chains inside the test's time, before Avalonia's per-test teardown
  (which RUNS the queue and throws after 5 s). Linted by
  `Infrastructure/HeadlessDrainLintTests`.
- **A dispatch owns its app: nothing it boots outlives it.** `Dispatch` releases
  the app afterwards: it closes open windows, disposes the container, undoes the
  app's process-wide hooks and stops running `DispatcherTimer`s. So a window or
  view a class caches is gone at the next dispatch; rebuild it on demand, as
  `EscapeKeyRoutingUiPerfSmokeTests.EnsureMainWindow` does. A process-wide hook
  added in `App` needs its `_processWideUnhooks` entry (`UI/IsolatedAppTeardownTests`).
  Before that, each dispatch leaked a whole app and the suite hung at 1,000+
  threads and 24 GB. **A scoped view model subscribed to a process-wide
  singleton is the same leak one level in**: `MainWindow.ReleaseForIsolatedTeardown`
  disposes the shell's `ScreenHost`s (and so their DI scopes), or every
  `MainWindow` dispatch pinned ~250 MB through
  `LocalizationService.Instance.PropertyChanged`
  (`Perf/IsolatedAppReleaseUiPerfSmokeTests`). **A class may cache a window in
  a static only until it closes** (clear the field in `Closed`):
  `Perf/HeadlessStaticLeakGuard` fails the dispatch that leaves a `Visual` or
  an `Application` in a static of this assembly — one dead window's Grids pin
  the NEXT app too, through Avalonia's thread-shared `TempDefinitions` (#250).
  **So is async work that resumes on the POOL and then dispatches**: a pool
  thread that reads `Dispatcher.UIThread` after the release can land between
  the next app's reset and its claim and BECOME its UI thread. A view model with
  such work stops dispatching in its `Dispose` (the container disposes it), as
  `StemMixerViewModel` does (#263; `UI/ViewModels/Editor/StemMixerReleaseTests`,
  `Perf/StemMixerReleaseUiPerfSmokeTests`).
- **The SETUP-side race is fixed (#192) by `Perf/HeadlessUiThreadClaim`**: a
  case used to die with `The calling thread cannot access this object` from
  `ServerCompositor..ctor` / `EnsureIsolatedApplication` when another thread (a
  pool continuation an earlier app started) read the dispatcher between
  Avalonia's reset and platform init and so BECAME the UI thread. The claim makes
  the session thread the UI thread first thing, under Avalonia's own lock, and
  logs every reclaim to `headless-uithread-claims.log` next to the test DLL
  (`Perf/HeadlessUiThreadClaimUiPerfSmokeTests` stages the theft). If that
  stack returns, check the log and the bindings
  (`Infrastructure/HeadlessDrainLintTests`); never "fix" it by widening a
  timeout. A case that fails after an earlier case died there (classes that
  cache one `MainWindow`, like `EscapeKeyRoutingUiPerfSmokeTests`) is collateral
  — read the whole run.
- **A message naming an orphaned `MediaContext` is not about the case it fails
  in:** an earlier case touched a live visual outside a dispatch. Fix that case.
- **The suite is SHARDED across concurrent processes** by
  `tools/ci/HeadlessShards.psm1`, shared by `Invoke-Gate.ps1 -Headless` (4),
  CI's `headless-ui` job (2: the private repo's runner has 2 vCPUs) and the
  release preflight (`-HeadlessShards`, default 4). Discovery (`--list-tests`
  with the suite's filter) names the classes; whole classes are dealt
  heaviest-first to the lightest shard by `tools/ci/headless-shard-weights.json`
  (measured seconds per class, under 4-shard contention); a class that file does
  not know is weighed by its case count, so a new class is assigned the day it
  compiles. Each shard names its classes with exact `--filter-class`es and the
  shards' TRX files are merged into one. **Each class still runs in ONE process
  with its cases in order**, so none of the session rules below change. Re-weigh
  after big moves with `Invoke-Gate.ps1 -Headless -UpdateShardWeights` (a stale
  file costs balance, never coverage). Pinned by
  `Infrastructure/HeadlessShardPlanTests`.
- **A run reporting fewer cases than discovery did has NOT RUN the rest** — they
  are not counted as failures. `Invoke-Gate.ps1 -Headless` names every
  discovered class that reported nothing and fails; `Invoke-HeadlessUiGate`
  (release) and `tools/ci/Invoke-HeadlessUiCi.ps1` (CI's `headless-ui` job)
  compare discovered against reported classes, re-run the missing ones in a
  fresh process, and block on a class that still reports nothing. A shard that
  crashes writes no TRX, so ALL its classes surface this way. Settle an A/B
  only with an isolated re-run of the NAMED case.
- **One list of harness-fault needles**, `$script:HeadlessHarnessFaults` in
  `tools/ci/HeadlessShards.psm1` (`Test-HeadlessHarnessFault`): CI, the release
  preflight and `Invoke-Gate.ps1 -Headless` read it, and
  `Infrastructure/CiWorkflowLintTests` fails a script that spells a needle
  itself. `Invoke-Gate.ps1 -Headless` re-runs classes whose EVERY failure
  carries a needle, alone, up to `-RaceReruns` (2) times, and prints it as a
  re-run (`headless race re-run: PASSED|RED`); a failure without a needle or an
  absent class is never re-run away (`Get-HeadlessRaceRerunDecision`, pinned by
  `Infrastructure/HeadlessRaceRerunDecisionTests`). `Merge-Train.ps1` goes
  through it. `-RaceReruns 0` shows the raw first run.
- **The release blocks on this suite** (`tools/release/New-Release.ps1`
  preflight; `-SkipHeadlessUi` warns). It also re-runs every class that
  compares a width or a height (`$HeadlessLayoutFilter`) in `de-DE` and `ja-JP`
  (`$HeadlessSweepCultures`), because German and Japanese text is where layouts
  clip and overflow. A class that starts asserting widths or heights must join
  that filter, or sit in `NotCultureLayout` with the reason no language can
  change the size it asserts — `Infrastructure/HeadlessLayoutFilterLintTests`
  fails the gate otherwise. It reads comment- and literal-stripped `Assert.*(…)`
  calls and ignores a size checked only to be non-zero (`> 0`, `> 1`,
  `{ Width: > 1 }`), which is the construction smoke every view has. The other
  way round, a filter entry that compares no size fails it too, unless it
  asserts through an offender list (`LayoutThroughOffenderLists`: `UiSweep`).
- **The suite never reads the developer's config.** `HeadlessSessionFixture.Get()`
  claims a throwaway root with `WritablePaths.TryClaimRootsForTesting` (`Try…`,
  because the gate's `CwdSerialized` classes own the setter; pinned by
  `WritablePathsTests`) and seeds `LanguageCulture`, so the suite never follows
  the Windows language. `DOTCHARTER_HEADLESS_CULTURE=de-DE|ja-JP` picks one
  (`HeadlessSessionFixture.SweptCulture`).
- **Never hard-code a count over derived markup or an English string in an
  assertion.** Derive it, or name the markup file in a comment so the next reader
  re-counts. Both shapes have rotted red here.
- **Modal scrim vs screen ground:** a MODAL scrim is `FireScrimFill` (~85 %,
  translucent); `FireOverlayFill` is the opaque gradient a SCREEN stands on.
  Asserted once, in `UI/FireTokenValueTests`; `Perf/` tests only assert that their
  overlay dims through the shared class.
- **Before blaming your change, reproduce on a clean tree and check free RAM** —
  a testhost killed by memory pressure reports `Fehler: 0` / `Bestanden!` with a
  total that moves. Run this suite yourself after a view change; the release is
  only the backstop.

## Conventions

- **Every change brings its own test.** New behaviour ⇒ a default-gate test; a
  new or edited VIEW ⇒ a construction + attach + layout smoke test (root
  `AGENTS.md` lists the XAML faults that build clean and fail at runtime).
- **Prove a new guard is not vacuous.** Break the thing it guards, watch it fail,
  restore. Tests here have passed for weeks against fixtures that already
  violated the rule.
- **A view that failed to style still ARRANGES — it just has no size.** Assert
  measured bounds, not existence.
- **A default-gate test may not build an `ItemsControl`'s Items.**
  `ItemCollection.Add` reaches `Dispatcher.VerifyAccess()`: it passes alone and
  throws in the full gate. Split it the way `UI/MenuFlyoutPrewarmerTests`
  (pure, gate) and `Perf/MenuFlyoutPrewarmUiPerfSmokeTests` (real trees, inside
  `HeadlessSessionFixture.Dispatch`) are split.
- **A guard over a hand-maintained PARALLEL LIST** (a `CopyFrom`, a comparer, a
  reader/writer pair) **is REFLECTION, never a lint that names fields.** Reuse
  `Infrastructure/DomainFieldProbe` (`Populated`, `Alternate`) and
  `Infrastructure/DeepCompare`, as `DomainCloneCoverageTests`,
  `ChartActionFieldCoverageTests` and `RscpFixtureCoverageTests` do. The value
  generator THROWS on a type it cannot fill, and every opt-out is a
  `FieldExclusion` with a REASON whose property is asserted to still exist.
- **A zero-allocation claim goes through `Infrastructure/AllocationProbe`**
  (per-thread counter, cheapest of several batches), and a RATIO claim through
  `AllocationProbe.MeasureCheapest` — never a single-window
  `Assert.Equal(0, …)` and never `GC.GetTotalAllocatedBytes`. Linted by
  `Infrastructure/AllocationAssertionLintTests`; the exemption is
  `*UiPerfSmokeTests`, not the `Perf/` folder. **Never widen a budget to a
  non-zero number to make a reading go away.**
- **Ambient state is restored on the FAILURE path** (`try/finally`).
  `LocalizationService.SetConfiguredCulture` writes the PROCESS-WIDE default
  culture, so every process-wide culture write — direct, or through product code such as
  `OnboardingWizardRig`, `GeneralSettingsViewModel`, `BackupMigrateViewModel` or
  `ToolTipHoverPolicy.Apply` over the real `LocalizationService.Instance` — belongs in
  `[Collection("LocalizationSingleton")]`. `CultureInfo.CurrentCulture =` and
  `CurrentUICulture =` are scoped to the calling flow and need no collection; a write
  through a fake or recording localization lands on that instance, which
  `CultureCollectionLintTests` lists with a witness.
  `Infrastructure/CultureCollectionLintTests` matches a list of such
  constructions: **add to it whenever a product type that writes the culture
  becomes reachable from a test.**
- **A class that takes an `ILocalizationService` gets `Infrastructure/FakeLocalization`,
  never `LocalizationService.Instance`.** `FakeLocalization.English()` answers with
  the English resx and formats arguments invariantly; `KeysOnly()` answers with the
  key (`Key(arg, …)` when formatted) so a test can pin WHICH key without its wording.
  Neither touches the process culture, so neither needs `LocalizationSingleton`;
  the singleton stays only where a test drives the culture or the UI reads it.
- **`PedalboardViewModel` and `PlayModeViewModel` are built through
  `Infrastructure/TestRigBuilders`** (`.Pedalboard(…)`, `.PlayMode(…)`). Their
  services are REQUIRED (#661); the builder fills every one a test does not name
  with an in-memory or inert fake (`Infrastructure/InertServices.cs`), so a
  fixture names only what it asserts on.
- **Remove every process-global class handler a test installs.**
  `ToolTipHoverPolicy.Apply()` hooks `ToolTip.TipProperty.Changed` for the whole
  process; call `ToolTipHoverPolicy.ResetForTesting()` in `Dispose`. The tell of a
  leaked handler: a whole class dies at once with `The calling thread cannot
  access this object` and passes in isolation.
- **A view model's bound collections are read through the dispatcher that owns
  them.** An INLINE fake dispatcher cannot pin thread affinity (it runs the
  delegate on whichever thread dispatched it, often the pool) — use a real
  single-threaded pump, as `UI/ViewModels/Editor/StemMixerTracksThreadAffinityTests`
  does. To read while a worker may be applying, use
  `Infrastructure/GatedInlineDispatcher` (one type — no private copies) or
  `OnboardingWizardRig.InlineDispatcher.Read<T>`; anything that CAN wait settles
  first (`rig.SettleAudioProbe()`).
- **Settle on the thing you are about to read, never on a count.** The stem mixer
  adds stem strips and only THEN inserts the master strip, so `Tracks.Count > 1`
  is true one line early (pinned by
  `UI/ViewModels/Editor/StemMixerRebuildSettleTests`); settling early can return
  a wrong ANSWER, not just throw.
- **A real native engine, or a write to `DCA_ENABLE_NULL_DEVICE` /
  `DCA_NULL_DEVICE_MANUAL`, belongs in `[Collection("NativeEngine")]`** — the
  engine reads those process env vars at construction. Enforced by
  `Infrastructure/NativeEngineCollectionLintTests`; `LocalOnly_*` files are
  excluded from it and need the attribute by hand. A NEW test that wants the null
  device sets `JuceEngineConfig.EnableNullDevice` (per engine, ABI v41) rather
  than the env var; PRODUCT code may not name the env vars at all
  (`Infrastructure/ProductNullDeviceEnvLintTests`, #85).
- **alphaTab is process-global: every call goes through
  `Services/Infrastructure/AlphaTabRuntime.RunExclusive(`** (`Score.ResetIds()`
  rewinds a static counter written into the `.gp`). Region-aware lint
  `GpExporterExclusivityLintTests` plus behavioural `GpExporterConcurrencyTests`.
- **A stress test gives every thread a fixed iteration count**, never
  `while (!stop)` — waiting threads queue allocations and kill the testhost.
- **Pure over timed:** pure state machines with injected clocks (unix-ms passed
  in) over sleeps — see the speed-ladder, latency-calibration and restart-policy
  tests.
- **A wait CAP is a deadlock detector (60 s), never a budget.** Where a timing
  claim is unavoidable, bound it by something the same run MEASURED. A fixed
  sleep followed by one read is a coin flip wearing an assertion; replace it with
  a yielding poll whose expiry is ASSERTED (with a message saying the box was
  starved), never discarded. **Never `SpinWait` for another thread's work** — a
  hot spin starves the pool thread it waits on; `await`.
- **A timeout that expires SILENTLY makes the test vacuous, not red** — a
  rendezvous half, a `SpinOnce` budget or a fixed sleep before an early `return`
  reports PASSED. Assert that each wait was satisfied before asserting on what
  followed it.
- **Never sleep before a "has not happened yet" assertion** — it only opens a
  window for the legitimate path to happen. Check immediately, and make any
  product timeout the test depends on long (a delay it awaits out, not a race).
- **A test that drives a REAL child process hands in its own deadlock-detector
  timeout** (minutes), never inherits the product's, and asserts no wall clock.
- **`AtomicFile` concurrency:** a torn read in `AtomicFileTests` is a real
  regression; an `UnauthorizedAccessException` from `ReplaceWithRetry` means
  something opened the file WITHOUT share-delete (our readers use
  `AtomicFile.ReadAllText`; the replace goes through
  `Infrastructure/Storage/WindowsPosixRename`).
- Codecs and parsers get dedicated `*Tests` with realistic + malformed inputs
  (e.g. `LrcLyricsParserTests`).
- Some `JuceSharp` / stem-separation / child-process tests are
  environment-flaky — compare against a clean tree before blaming your change.
- `InternalsVisibleTo` lets tests reach `internal` seams; use them instead of
  loosening production visibility.

## The commit gate

Every code change is verified with `dotnet build` **and** the filtered
`dotnet test` above before it is committed.

## Keeping this current

When you change a routed harness, update its `docs/agents/testing/*.md` file in
the same change. Conventions that apply to EVERY test belong here; harness detail
and incident history do not.
