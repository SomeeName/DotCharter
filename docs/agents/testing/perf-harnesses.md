# The `Perf/` harnesses — quiesce, the session race, the startup profiler

Deep reference for `DotCharter.Tests/AGENTS.md`. `Perf/*UiPerfSmokeTests` is
excluded from the default gate/CI and **runs as its own `dotnet test`
invocation**. What each harness measures, and the traps that made them lie. The
biggest resident of this folder — the headless UI sweep — has its own file,
[ui-sweep.md](ui-sweep.md). Also in this folder:
[ai-eval.md](ai-eval.md) — the AI tone-design eval (`DotCharter.Tests/Ai/Eval/`),
its scoring weights, its Rocksmith ground truth and the `DOTCHARTER_AI_EVAL`
opt-in gate. Nothing to do with `Perf/`; it is the other env-gated research
harness in this project.

**Since 2026-08-28 the suite blocks a release**: `tools/release/New-Release.ps1`
runs it in preflight and will not build assets while it is red. See
`DotCharter.Tests/AGENTS.md` § "It DOES have a home now" for what that looks
like and how the session race below is told apart from a real failure.

## Why the split — it is a PROCESS boundary, not a hang

This page used to say "booting the real app session leaves the testhost hanging
at teardown". **That was true and is not any more**: `303b2682` (2026-08-01)
made `HeadlessSessionLifecycle` an `IAsyncDisposable`, and the suite now runs to
completion and the process EXITS — 330 cases in ~7–8 min with ≤7 s between the
last result and process exit. Believing the old sentence is exactly how a stale
safety claim works: the reason was fixed and the exclusion outlived it.

What actually blocks a shared testhost is `Avalonia.Headless`'s **process-global
dispatcher**. xunit runs the other test collections in parallel with the
serialized `HeadlessUI` one; one of them claims the dispatcher first, and from
then on every `HeadlessSessionFixture.Get().Dispatch(…)` throws
`InvalidOperationException: The calling thread cannot access this object` or
`InvalidProgramException: You've caused dispatcher loop` out of
`Dispatcher.ResetForUnitTests()` ← `HeadlessUnitTestSession.EnsureIsolatedApplication`.
An unfiltered run measures **314 of the 330 failing**, plus one unrelated gate
test (`AudioDisposalLifetimeTests.ARecorderStoppedAfterDisposeDoesNotThrow`,
green in the filtered gate) taken down with them — reproduced four times
(309/310/310/315, once also crashing the testhost). That is deterministic, so it is not the memory-pressure kill (which
reports `Fehler: 0` with a total that moves between runs).

Two consequences:

- **Every `*UiPerfSmokeTests` class needs `[Collection("HeadlessUI")]`** (once
  per partial type), and `Infrastructure/HeadlessCollectionLintTests` fails the
  DEFAULT gate if one does not. Without it the class is its own collection and
  runs in parallel with the serialized one — poisoning the session for the whole
  run. Six were outside it until 2026-08-23; two of those
  (`DialogConstruction…`, `UiSweep…`) said in a comment that it was ON PURPOSE
  because joining "hangs a filtered run", which was the synchronous-`Dispose()`
  self-join and has been dead since `303b2682`. Re-checked: a filtered run of
  `DialogConstructionUiPerfSmokeTests` with the attribute on is 29/29 in 21 s.
- **The "residual race" was never a race, and it is FIXED (2026-08-29).**
  For a fortnight this page said 3–6 cases per standalone run die on
  `InvalidProgramException: You've caused dispatcher loop` at
  `Dispatcher.ResetForUnitTests()`, that the membership moves completely between
  runs, and that the suspect was "the booted app's own timers still posting when
  `EnsureIsolatedApplication` disposes". The mechanism is simpler and entirely
  ours:

  1. `HeadlessSessionFixture` starts the session with
     `HeadlessUnitTestSession.StartNew(type)`, whose isolation level is
     `PerTest`. So `EnsureIsolatedApplication` builds a whole `App` **per
     dispatch** and disposes it again when that dispatch returns.
  2. That disposal calls `Dispatcher.ResetForUnitTests()`. Read it (Avalonia
     12.1.1, the `Dispatcher` queue partial under `src/Avalonia.Base/Threading/`): it does not
     discard the queue, it **runs** it — `while (true) { … ExecuteJob(peek) }`
     until nothing is left above `DispatcherPriority.Inactive` — and it
     `throw new InvalidProgramException("You've caused dispatcher loop")` the
     moment its own `Stopwatch` passes **5 seconds**.
  3. The app answers first paint by posting a self-refilling chain of
     `DispatcherPriority.Background` jobs — `HiddenSubtreePrewarmer
     .ScheduleDrain` realizes ONE surface per dispatcher turn and re-posts
     itself, and both `MenuFlyoutPrewarmer` chains do the same. Its own doc
     comment budgets "250-500 ms"; a Debug headless build laying out
     `PlayModeView`'s sixteen screen roots is far more.

  So every case that showed a window was racing a five-second budget nobody had
  declared. That explains all of it: why the victims were the window-building
  cases (`Scenario_0*`, `ShellConstruction…`, `StartupProfile…`,
  `SettingsConstruction…`), why the membership moved (whichever chain was
  slowest that run), why CPU contention made it worse, and why a single
  `--filter`ed scenario hit it every time.

  **The fix is `HeadlessSessionFixture.Dispatch(…)`** — a wrapper that runs the
  body and then drains the same queue itself, inside the test's own (unbounded)
  time, so Avalonia's teardown finds it quiet. Nothing is skipped and no
  assertion moves: the drain runs AFTER the body. Two guards keep it from
  coming undone, both in the default gate
  (`Infrastructure/HeadlessDrainLintTests`): no test may reach the raw session
  with `HeadlessSessionFixture.Get()`, and the reflection binding to Avalonia's
  cancellable `Dispatcher.RunJobs(DispatcherPriority?, CancellationToken)` must
  still resolve — the public overload cannot be bounded, and a hang would be a
  worse failure than the flake it replaces.

  **How big is that queue really?** The drain appends every new worst case to
  `headless-drain.log` beside the test binaries (and to stdout, which xunit
  swallows for a passing case — hence the file). Measured 2026-08-29:

  | Filter | worst drain |
  |---|---|
  | `~ShellConstructionUiPerfSmokeTests` (19 cases) | **0.143 s** |
  | `~Perf.UiPerfSmokeTests` (the six `Scenario_0*`) | **19.96 s, then 39.38 s, then 44.13 s** |

  So the shell classes were never near the cliff and the scenario class was
  eight times past it. That is the whole race in two rows.

  **And it is the bill, not just the diagnosis.** Those seconds are real work
  the suite now RUNS instead of having it aborted at 5 s, so the scenario class
  costs ~7 min where the (more heavily loaded) baseline run spent ~2 m 20 s
  across the same six cases. Part of that is a harness fault this made visible:
  `UiPerfSmokeTests.EnsureMainWindow` has no cache and the headless session has
  no `IClassicDesktopStyleApplicationLifetime`, so its reuse branch never
  matches and **every call builds a whole new `MainWindow`** — three of them in
  `Scenario_01` alone, each scheduling its own prewarm chain, which is also why
  that scenario's `EnsureMainWindow (warm)` line reports a COLD construction.
  `UI/EscapeKeyRoutingUiPerfSmokeTests` already fixed exactly this for itself
  (`_sharedWindow` + `AssertKeyboardWillReach`). Doing the same here changes
  what a reported number means, so it wants its own measured pass rather than a
  drive-by.

  **A SECOND, DIFFERENT session race — the SETUP race — is FIXED too (#192,
  2026-10-05).** After the drain, what remained was one to three cases a run
  failing with `InvalidOperationException: The calling thread cannot access
  this object` from `ServerCompositor..ctor` ← `DefaultRenderLoop.Add` ←
  `Dispatcher.VerifyAccess` ← `AvaloniaHeadlessPlatform.Initialize` ←
  `EnsureIsolatedApplication`, a different case each time (2026-10-05 it was
  mostly `GalleryPagesUiPerfSmokeTests`: 277 cases, one app each).

  The mechanism (Avalonia 12.1.3 source): `Dispatcher.UIThread` is the static
  `s_uiThread`, and the dispatcher constructor does `s_uiThread ??= this` on
  WHATEVER thread it runs. `EnsureIsolatedApplication` calls
  `Dispatcher.ResetBeforeUnitTests()` (null `s_uiThread`, forget every thread's
  dispatcher) and only then `SetupUnsafe()`, whose first dispatcher access on
  the session thread is the compositor's render loop, after the runtime,
  text-shaping and rendering subsystems have initialised. Any other thread that
  reads `Dispatcher.UIThread` or `Dispatcher.CurrentDispatcher` in that gap (a
  `Post` from a pool continuation an earlier app started; an `AvaloniaObject`
  built off-thread, whose constructor reads `CurrentDispatcher`) creates a
  dispatcher bound to ITSELF, and it becomes the UI thread. A live run caught
  one: a pool thread finishing an earlier app's
  `StemMixerViewModel.RebuildOnceAsync` and posting its continuation. The
  August theory ("the next app waits for the previous render loop") was wrong,
  which is why a standalone repro without such a thread never failed.

  The fix is `Perf/HeadlessUiThreadClaim`: the session's app builder runs it as
  the FIRST platform initializer. Under Avalonia's own `s_globalLock` (every
  dispatcher constructor and every slow `UIThread` read takes it) it makes the
  session thread's dispatcher the UI one; if a thread got in first, it repeats
  Avalonia's reset and claims — nothing has been built on the stolen dispatcher
  yet. Each reclaim is logged with the thief's thread and queue head to
  `bin/.../headless-uithread-claims.log` (per process; shards share the file,
  so read it as a lower bound). `Perf/HeadlessUiThreadClaimUiPerfSmokeTests`
  steals the UI thread on purpose before setup; without the claim it fails with
  the exact race stack, without the INSTALL it fails because the staging never
  runs. `Infrastructure/HeadlessDrainLintTests` asserts the three reflection
  bindings. Measured 2026-10-05, same box, `GalleryPagesUiPerfSmokeTests` alone:
  before, 4 race failures in 10 × 277 cases; after, 0 in 20 × 277. Full suite,
  2 shards, `-RaceReruns 0`: 1 in 2 × 1,049 before, 0 in 2 × 1,050 after.

  **If the stack comes back,** check the claims log and the bindings first.
  Do not widen a cap, add a retry inside a test, or weaken an assertion on
  account of it: `Invoke-Gate.ps1 -Headless` already re-runs a class whose
  every failure carries a harness-fault needle, and prints that it did.

  **A race failure does not stop at the case it kills.** Measured 2026-09-04,
  20 runs of `EscapeKeyRoutingUiPerfSmokeTests` alone under ten CPU burners:
  one run lost `Escape_OnSongSelect_GoesHome` to `EnsureIsolatedApplication`
  and then lost `Escape_AfterFocusedElementRemoved_StillNavigates` too, with
  `focused=<none> focusInsideWindowUnderTest=False` after a full 60 s of
  polling for a 250 ms timer — i.e. not late, homeless. That class caches ONE
  `MainWindow` across its cases (deliberately: building a second one is what
  broke it in 0.44), so tearing the application down mid-run orphans the window
  every later case shares. **So a class that reuses ANY per-run fixture can
  report a second, plausible-looking failure downstream of a harness fault.**
  Read the earlier cases in the run before believing the later one.

  **Do not "fix" a recurrence by widening a timeout**, and do not shorten the
  drain cap to "save time" — a drain that gives up early hands the remainder
  straight back to Avalonia's five seconds. If the numbers climb, something new
  is posting background work; the log says how close it is.

**Every dispatch RELEASES the app it booted (2026-10-04) — the suite used to
hang on that.** Avalonia's per-test teardown abandons the `App`; it never
disposes the container. Each abandoned app kept its `AppLogger` (two Serilog
async pump THREADS and a flush timer), its `AppDomain`/`TaskScheduler` handlers,
a `ConsoleLogRouter` wrapping the previous app's, its never-closed windows,
their subscriptions to the STATIC `UiScaleController` / `ChartTheme` events, and
its running `DispatcherTimer`s. Together they kept the whole graph reachable and
still working. Measured on the same box: unfixed, the testhost reached **1,267
threads and 12 GB in 621 s**, with a worst drain of 27.6 s at drain #599. It
was killed there, and left alone it runs on to 24–26 GB and never finishes.
Fixed, all 991 cases ran in 584 s with ~30 threads and a 8.75 GB peak, and the
worst drain at #599 was 6.0 s. `HeadlessSessionFixture.Dispatch` now ends with
`DrainAppBackgroundWork(); ReleaseIsolatedApp();`. That closes every tracked
open window (a `MainWindow` through `MainWindow.ReleaseForIsolatedTeardown`,
because its `Close()` reaches `App.TerminateNow`), calls
`App.ReleaseForIsolatedTeardown` (it undoes `_processWideUnhooks` newest first,
disposes the container and drops the static UI events' subscribers), and stops
every timer left in `Dispatcher._timers`. Pinned in the DEFAULT gate by
`UI/IsolatedAppTeardownTests`: five boot+release lifetimes must free their
logger and restore `Console.Out`, App*.cs must record an unhook for every
process-wide hook, and the fixture must release after every `Dispatch` overload.
**A new process-wide hook in `App.OnFrameworkInitializationCompleted`
(a static event, an `AppDomain` event, a console writer) needs its
`_processWideUnhooks.Add(…)`, or the leak comes back one app per dispatch.**
The thread count is the cheap tell: watch it, not the drain log.

**…and a released app can still be pinned from OUTSIDE it (found 2026-10-04).**
With the release above in place the suite still peaked at 8 GB in one process,
climbing with threads flat. A temporary probe in `ReleaseIsolatedApp` (live heap
after `GC.GetTotalMemory(true)` every dispatch) showed no per-dispatch drift on
most classes — 60–80 MB over hundreds of dispatches — but **+250 MB per dispatch
that builds a `MainWindow`** (`EscapeKeyRoutingUiPerfSmokeTests`: 303 → 555 → 804
→ 1,072 → 1,338 MB; `UiPerfSmokeTests` the same). A heap dump (`dotnet-dump
collect --type Heap`, then `gcroot` on each `DotCharter.UI.App`) named the root:
the PROCESS-WIDE `LocalizationService.Instance.PropertyChanged`, holding
`SettingsShellViewModel.OnLanguageChanged` and
`KeybindsSettingsViewModel.OnLanguageChanged` of every released app. Both are
SCOPED view models of the shell's Settings `ScreenHost`, which unsubscribe only
in `Dispose`; the host's contract is "dispose the host with its owner", and the
window never did — disposing the ROOT container does not dispose child scopes.
`MainWindow.ReleaseForIsolatedTeardown` now disposes every `ScreenHost` under
the window. Afterwards the same five dispatches stay at 303–320 MB (the newest
app, held by Avalonia's last-app statics until the next dispatch replaces it),
`LocalizationService` keeps exactly one subscriber (Avalonia's own weak-event
manager — never drop that one), and the whole suite peaks at 1.7–2.3 GB a shard.
Pinned by `Perf/IsolatedAppReleaseUiPerfSmokeTests` (weak references to the
app, the window, the settings host and its shell view model must all be dead in
the next dispatch). **`gcdump` was no help here**: it caps at 10 M objects and
both snapshots hit the cap; the probe plus one full heap dump was the method.

**…and a STATIC in a test class pins it too — plus the NEXT app (#250).**
`IsolatedAppReleaseUiPerfSmokeTests` failed (its `SettingsShellViewModel` still
reachable) only when `UiPerfSmokeTests` had run earlier in the same process.
Its `EnsureMainWindow` cached the window in `static _window` / `_windowApp` and
never let go, so the last released `MainWindow` and its App outlived their
dispatch. That alone pins one app; what made it pin a LATER one is Avalonia's
`Grid` (ported from WPF): it shares ONE `TempDefinitions` array per thread
(`s_tempDefinitionsDataSlot`), every Grid that lays out keeps a reference to it
in its `ExtendedData`, and the slots keep the column/row definitions of
whichever Grid used them last — whose inheritance parent is that Grid, whose
app is the case that ran last. Found with a full `dotnet-dump` taken inside the
failing assertion and `gcroot` on each `SettingsShellViewModel`: static
`UiPerfSmokeTests._window` → its ScrollBar's `Grid` → `ExtendedData.TempDefinitions`
→ a `ColumnDefinition` of the newer app's Grid → that app. Class order in a
process follows the test assembly's PATH (each worktree gets its own order),
which is why it reproduced in some worktrees and never in others.
`UiPerfSmokeTests` now forgets its window on `Closed`, and
`Perf/HeadlessStaticLeakGuard` runs after every dispatch's release: a static
field of the test assembly still holding a `Visual` or an `Application` fails
THAT dispatch by name, and is cleared so no later class inherits the failure
(pinned in the gate by `UI/HeadlessStaticLeakGuardTests`).

## Sharding — four processes, one dispatcher each

One testhost uses one core: every case runs on the ONE process-global
dispatcher, serially (991 cases, 578 s of summed case time == the wall span).
A second dispatcher needs a second process, so `tools/ci/HeadlessShards.psm1`
splits the suite and every runner goes through it:

| Runner | Shards | Why |
|---|---|---|
| `tools/agent/Invoke-Gate.ps1 -Headless` | 4 (`-Shards N`) | the owner's 16-thread box; balanced shards finish within ~10 s of each other |
| CI `headless-ui` (`Invoke-HeadlessUiCi.ps1 -Shards 2`) | 2 | the private repo's `windows-latest` runner has 2 vCPUs; raise it only against the step summary's peak RSS |
| release preflight (`New-Release.ps1 -HeadlessShards`) | 4 | the full en-US pass and each localized layout pass |

- **Partition:** `--list-tests` with the suite's filter → class → case count;
  each class weighs its seconds in `tools/ci/headless-shard-weights.json`, or
  (a class the file does not know) its case count × the suite's mean seconds
  per case; longest-processing-time first, ties by ordinal name. Whole classes
  only, so a class's cases still share one process, in order.
- **Merge:** `Merge-HeadlessTrx` folds the shards' TRX files into one
  (results, definitions, entries; counters summed). A shard with no TRX
  contributes nothing — its classes are then ABSENT, which every runner's
  discovered-vs-reported check names (the gate wrapper fails on it; CI and the
  release re-run them in a fresh process and block if they still report
  nothing). Proved 2026-10-04 by killing one shard's testhost mid-run.
- **Retries stay single-process**: a harness-race class, an absent class and an
  order-dependent candidate are each re-run in one fresh process, exactly as
  before. `Invoke-Gate.ps1 -Headless` (and so `Merge-Train.ps1`) re-runs only
  harness-fault classes, only when no other failure and no absent class exists
  (`Get-HeadlessRaceRerunDecision`), up to `-RaceReruns` (2) times, and prints
  `headless race re-run: PASSED|RED` under the first run's counts.
- **Cost model:** each shard pays ~15–20 s of start-up, and under 4-way
  contention a class runs 1.2–1.3× its single-process time — the weights file is
  measured under that contention. The floor is the biggest class
  (`UiPerfSmokeTests`, ~60–80 s) plus start-up, so past ~6–8 shards only memory
  grows. Measured 2026-10-04: one process 582 s / 8.0 GB peak with the leak
  above, 461 s / 3.0 GB without it; 4 shards 157 s / 6.8 GB together.
- **Shared files are safe:** every artefact a class writes is its own
  (`perf-reports/`, `TestResults/<class>/`) or per-process (the isolated config
  root carries the PID), and `headless-drain.log` appends are best-effort.

**Never touch a live visual OUTSIDE a dispatch — not even through a static
(found 2026-09-19).** After the network/practice cut the full run failed a
different set of pointer, wheel, focus, resize and popup cases each time, all
green alone. The cause was two harmless-looking things in sequence:
`EscapeKeyRoutingUiPerfSmokeTests` (like `TitleBarDrag…`, `UiPerfSmokeTests`
and every other class that builds a `MainWindow`) left its window shown for
the rest of the process — a test cannot `Close()` one, that reaches
`App.TerminateNow` (since 2026-10-04 the fixture closes it at the end of the
dispatch, see above) — and the window stayed subscribed to the STATIC
`UiScaleController.ScaleChanged`. `InterfaceScaleUiPerfSmokeTests.Dispose` then
reset the scale from a bare `Dispose`, i.e. with no application alive. The
leaked window re-laid-out, which created Avalonia's internal `MediaContext` in
the process-wide ROOT `AvaloniaLocator`, bound to a dispatcher that was already
gone and holding a render operation that could never run. Every later
application resolves that one, so nothing in the process laid out, rendered or
committed a frame again: hit tests found `nothing`, a `Width` change never
reached `ClientSize`, popups opened off their anchor. The cut only changed the
class ORDER (xunit v3 shuffles classes, and the set changed): before it,
`InterfaceScale…` happened to run before any class that leaks a `MainWindow`.
Two fixes, both in the harness: the reset runs inside
`HeadlessSessionFixture.Dispatch` (`DisposeAsync`), and every dispatch now
checks the `MediaContext` belongs to its own dispatcher and, if not, fails with
the case it was first seen after instead of a plausible assertion
(`AssertMediaContextBelongsToThisDispatcher`; its reflection binding is linted
by `Infrastructure/HeadlessDrainLintTests`). Minimal reproducer:
`-FilterClass "*EscapeKeyRoutingUiPerfSmokeTests,*InterfaceScaleUiPerfSmokeTests,*UiBreakpointUiPerfSmokeTests"`.
**So: a static that a view subscribes to is mutated only inside a dispatch**,
and a `Dispose`/constructor that needs to reset one makes its own.

**The suite used to boot the app against the DEVELOPER'S REAL CONFIG — fixed
2026-08-28.** `WritablePaths.ResolveConfigFile` finds
`%APPDATA%\DotCharter\appconfig.json`, so any test asserting a "nothing set up
yet" state failed on a machine that HAD set it up (and the session could write
back to that file). `HeadlessSessionFixture.Get()` now calls
`WritablePaths.TryClaimRootsForTesting` with an empty per-process temp root
before it starts the session, and `ShutdownAsync` releases and deletes it.

Two cases used to be red on the owner's box, and **they had different
causes** — one asserted an empty `AppConfig.OnlineApiUrl` while the owner's
config named a real server, the other asserted an undismissed room-board notice
while the owner had dismissed it. Both cases went with the online backend in
v0.60, but the SHAPE is the thing to remember: a headless case that reads the
real config asserts the OWNER's machine, not the product.

It is `TryClaim…`, not the unconditional `OverrideRootsForTesting`, because the
DEFAULT gate reaches this fixture too — seven `DotCharter.Tests/UI/*` classes
that are NOT named `*UiPerfSmokeTests` boot the session — in parallel with the
`CwdSerialized` classes that own the unconditional setter. Claiming only when no
override is in force is atomic under the type's own lock and can never repoint
the roots out from under one of them mid-test.

**And the root is seeded with a culture — `en-US` unless
`DOTCHARTER_HEADLESS_CULTURE` says otherwise.** `AppConfig.LanguageCulture`
defaults to `"system"`: an empty config hands the suite the developer's Windows
language, and on this German box that alone produced eight failures (two
overflowing settings rows, six `clipped-text` sweep errors) which were real
German-layout bugs but nothing to do with the change under test. Pin the
culture; do not let the machine pick it.

**A `clipped-text` error on a HIDDEN surface needs one sanity check before you
believe it: does it reproduce with that surface VISIBLE?** The sweep realizes
gated sub-surfaces through the product's own `HiddenSubtreePrewarmer.Realize`,
which is deliberately ONE `Measure` + one `Arrange` because that is the cost
model it exists to state — no settling pass, the thing a real layout gives you
for free. Several views declare no styles of their own because their HOST
carries them (`SoundInspectorView` and its six siblings rely on `InspectorView`
including `InspectorStyles.axaml`), so the harness adds them at WINDOW scope in
`ApplyInheritedStyles`. Those attach during the FIRST measure of a subtree that
has never been measured, so the pass sizes a control from the pre-style font and
the style's font then widens the text inside a slot already arranged. Measured
2026-08-29: **15 phantom `clipped-text` errors in `SoundChordTechniqueCard`,
every one exactly 3 px**, none of them reproducible with the same card visible
under the same styles, and none of them real — in the product the styles are on
`InspectorView` from construction, long before the Background-priority prewarm
runs. `RealizeHiddenSurfaces` now runs a second `Realize` over what it realized,
which settles it. **The uniform delta is the tell**: a font that changed after
the measure clips every string by the same few px, where a genuine overflow
scales with the string.

**The pin is a KNOB, not a constant** (`HeadlessSessionFixture.CultureEnvVar` /
`.SweptCulture`, added 2026-08-28). `$env:DOTCHARTER_HEADLESS_CULTURE = "de-DE"`
and the same suite grades German layout; `ja-JP` grades Japanese. Both layout
classes name the culture in their failure message, because "needs 120.7 px in a
92 px slot" is unactionable without the language that produced the string. The
release script sweeps every class that compares a width or a height
(`$HeadlessLayoutFilter`, kept complete by `Infrastructure/HeadlessLayoutFilterLintTests`)
under de-DE and ja-JP after the full en-US run — see `Invoke-HeadlessUiGate` in
`tools/release/New-Release.ps1`. Measured 2026-08-28 on a contended box with the
first two classes: the full en-US pass 7 m 46 s (330 cases), the de-DE layout
pass 1 m 12 s and the ja-JP one 1 m 56 s; the 49 classes the lint added on
2026-10-11 took 70 s (de-DE) and 87 s (ja-JP) of test time in one process
(496 cases), before sharding. **The eight German faults are FIXED as of 2026-08-28** — the list, and
what each fix was, is in [ui-sweep.md](ui-sweep.md).

## The golden screenshots — a FOURTH invocation

`Perf/ScreenGoldenUiPerfSmokeTests` renders every `ScreenCatalog` entry × fake
variant with Skia at 1600×900 and compares it per pixel with
`DotCharter.Tests/Goldens/ui/<area>/<screen>--<variant>.png` (> 8/255 on a
channel counts; > 0.05 % of pixels fails). It needs Skia drawing, which is fixed
when the session starts, so it is its own run:
`tools/agent/Invoke-Gate.ps1 -Goldens` sets `DOTCHARTER_UI_SWEEP_DUMP=1` and the
en-US culture for that one process. The `-Headless` run, CI's headless job and
the release preflight exclude it by its EXACT full name
(`UI/Goldens/ScreenGoldenStoreTests` pins all three), so a missing baseline
never turns the headless suite red. A case with no baseline FAILS ("no golden —
run Update-Goldens"); a mismatch writes `.actual.png`, `.diff.png` and
`index.html` under `TestResults/ui-goldens/` beside the test binaries.
`tools/agent/Update-Goldens.ps1 -Stage` writes a contact sheet to
`artifacts/ui-goldens/staged/index.html`; `-Accept` is the OWNER's and refuses
a non-interactive console. First measured 2026-09-19: 34 cases, 13 s of test
time. Detail: [../ui/target-architecture.md](../ui/target-architecture.md) § 5.1.

The bullets below sit under `Perf/` in the source guide; the nesting is kept
as-is so the text is byte-identical to what it replaced.

## The Library shell capture (#306)

`Perf/LibraryShellCaptureUiPerfSmokeTests` shows the real `MainWindow`, opens the
Library through `OpenSongBrowserCommand` and captures the `LibraryView` the shell
built in `SongBrowserHost`: the app's own library (first run: the suite's config has
no folder), then the same view handed a `LibraryScreen` over an owner-sized and a
long-text song browser, at 1920x1080 and 1280x800. Capture-gated like the class
below (`DOTCHARTER_UI_SWEEP_DUMP=1`, PNGs to `DOTCHARTER_RIG_CAPTURE_DIR`). A capture
run of either class ends with `dotnet test` reporting a RunCommand exception after
the module passed; the PNGs and the pass are real.

## A view captured in its own Window: ask for Dark (#688)

The app is `RequestedThemeVariant="Default"` and `MainWindow` asks for `Dark`. A
headless platform resolves Default to **Light**, so a bare `new Window` hosting a
shell view gives Fluent's black foreground to any text that has no colour of its
own, and the capture shows near-black titles the product never draws.
`PlayModeScreensUiPerfSmokeTests.WithView` sets `RequestedThemeVariant = Dark`
for that reason; do the same in any harness that hosts a view `MainWindow` hosts.
Text should still name its own Fire token: `ResultsScreen_titlesAndProfileNameUseTheFireTitleToken`
fails on Results text whose colour comes from the window.

## The narrow-width shell capture (#130, #134)

`Perf/NarrowWidthShellCaptureUiPerfSmokeTests` shows the real `MainWindow` over
the UI sweep's seeded song and captures it at 1920x1080 and 1280x800, each with
a long warning on the status rail and then with a running task too. It does
nothing unless `DOTCHARTER_UI_SWEEP_DUMP=1`; PNGs go to
`DOTCHARTER_RIG_CAPTURE_DIR` (else `TestResults/narrow-width/`), suffixed with
`DOTCHARTER_HEADLESS_CULTURE`. **The two sizes share ONE window, resized between
shots**: a second `MainWindow` in the same process never started (the first
one's teardown ends the run, and that run writes no TRX). The geometry is
asserted by `StatusRailNarrowWidthUiPerfSmokeTests` and
`InspectorNarrowWidthUiPerfSmokeTests` on the views alone, which share
`Perf/NarrowWidthLayout.cs` and write their own PNGs the same way.

`Perf/EditorShortWindowUiPerfSmokeTests` (#211) is the same one-window pattern
WITH assertions, in every headless run: 1920x1080, 1280x800, 1280x720 and
2560x1440, empty selection and (on the two short sizes) a two-note selection.
It asserts the Sound tab's section viewport (100 px; the #211 footer fold went
with the footer in #601, every verb is in its section), that the tab ends above
the status rail, the timeline's `EditorRowFloors.TimelineCompactPx`, and that on the
two TALL sizes nothing binds. With `DOTCHARTER_UI_SWEEP_DUMP=1` it writes
`editor-<w>x<h>[-selected]-<culture>.png`.

## `Quiesce()` and the tab-switch scenario

  - **`UiPerfSmokeTests.Quiesce()` is called ONCE per run of samples, never per
    sample — do not move it back inside `Measure`.** It is
    `GC.Collect()` + `WaitForPendingFinalizers()` + `GC.Collect()` over the whole
    booted-app heap, ~610 ms a go. Per sample it made `Scenario_06` take ~110 s
    (2 m 11 s for the class) while the work it actually STOPWATCHES summed to
    **176.7 ms across 180 samples** — the quiesce sits outside the stopwatch, so
    the entire wall time was harness, and the ~3 s "per `UpdateLayout()`" it
    looked like was never layout at all. Hoisted: class 2 m 11 s → **21 s**,
    Scenario_06 avg 0.98 → 0.84 ms and WORST 23.3 → 5.7 ms (the per-sample
    collect was itself the noisiest thing in the loop). A loop allocates ~1.5 MB
    total, which does not fill a gen-0 budget, so a sample absorbs at most a
    sub-ms gen-0 — a cost the real UI thread pays too. Keep
    `GC.GetTotalAllocatedBytes(precise: true)`: its EE suspension is tens of
    microseconds and these samples allocate single-digit KB, which an imprecise
    read cannot resolve.
  - **The `Scenario_0*` cases used to fail intermittently with
    `InvalidProgramException: You've caused dispatcher loop`** out of
    `Dispatcher.ResetForUnitTests` → `HeadlessUnitTestSession
    .EnsureIsolatedApplication`. That is the five-second teardown-drain overrun
    above, **fixed 2026-08-29**; the note that it "reproduces identically on an
    unmodified tree" was true and was the tell that it was harness, not product.
    Running a single scenario with a `--filter` hit it EVERY time — the session
    is built per run, so the prewarm chain had nowhere else to be paid — which
    is why the old advice was to run these as a whole class. That reproducer is
    the cheapest A/B if the drain is ever removed.

## The cold-start profiler

  - `StartupProfileUiPerfSmokeTests` is the cold-start profiler: it rebuilds the
    real DI container with every descriptor stopwatch-wrapped, replays
    `App.OnFrameworkInitializationCompleted`'s resolution order, and dumps
    per-service EXCLUSIVE construction cost to `perf-reports/00-startup-profile.txt`.
    It answers "which service is slow", which the app's own `StartupPhases:` log
    line cannot. **Read round 1 only** — every round builds another full
    container, so from round 2 on anything holding a process-wide resource reads
    too slow; rounds 2+ exist only to separate JIT
    (huge in round 1, gone after) from real work (flat across rounds). It asserts
    nothing on purpose — the startup guards are structural, in
    `UI/StartupWorkDeferralTests`.
