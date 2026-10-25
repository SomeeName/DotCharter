# Root guide detail: big files, layout, validation, evidence

Moved verbatim from the root `AGENTS.md`, which keeps condensed forms. Section order follows the old guide.

## Never `Read` these whole (full table and the file-size guards)

**Never `Read` these whole** — one Read costs more context than most tasks need.
Sizes measured 2026-10-11 and rounded on purpose; re-measure, never carry a
number forward.

| File | Size | Instead |
|---|---|---|
| `DotCharter.Localization/Resources{,.de-DE,.ja-JP}.resx` | ~605 / 635 / 695 KB, ~5,000 keys each (5,274) | **Grep the key.** All THREE must stay in sync — grep all three, edit all three. ja-JP is biggest on disk because each kana/kanji is three UTF-8 bytes. |
| `JuceSharp/.../dcdsp_cab_irs.h`, `dcdsp_acoustic_irs.h` | ~846 / 98 KB | Generated IR float tables, zero logic. |
| `AmpFxRigView.axaml` | ~133 KiB (135,847 bytes / 1,622 lines, the split lane in `RigBoardLaneTemplate.axaml`, its own style rules in `AmpFxRigStyles.axaml`) | Grep the `x:Name`, read ±150 lines. |
| Play Mode's screens, `Views/PlayMode/Screens/*.axaml` (lifted out of `PlayModeView.axaml` in #294) | 2–56 KB each, Song Select the biggest; `PlayModeView.axaml` itself ~14 KB | Grep the `x:Name` across `Views/PlayMode/`. **Each screen's names live in ITS view's namescope**, so `FindControl` on `PlayModeView` reaches none of them — search the visual tree. Five screens (settings modal, Get Ready, Pause, Song Select, Results) are `ContentTemplate` bodies under a `ContentControl` host (`PlayModeView.DeferredScreenHostNames`), not constructed before the first frame — the code-behind reaches Song Select and Results through on-demand lookups, never a field. Source lints read the set through `DotCharter.Tests/UI/PlayModeMarkup` (the screens inlined where they are declared). |
| Any other `.axaml` view or style, and the tone-rig HTML prototype outside the repo | 5–160 KB each | Grep the `x:Name`, style class or CSS selector, then Read with offset/limit. |
| `Audio/Dsp/Tones/tone_gear_data.json` | ~147 KB | Gear catalogue on ONE line — query it in code. |

**Split types.** The most-opened types are split across partial files
(`PlayModeViewModel`, `JuceAudioPlaybackService`, `LivePlayViewModel`,
`MainWindow`, `MainWindowViewModel`, `App`, `InspectorViewModel`,
`PedalboardViewModel`). Each sibling opens with a one-line `//` header saying
what it holds: `ls` the set and open the ONE you need. **A partial makes the FILE
small, not the CLASS** — the class totals are in [MAP.md](../../docs/agents/MAP.md), and
that total is what you are adding to. `Infrastructure/SplitTypePartialRatchetTests`
lists every type split across three or more partials with its exact file set and a
class-total ceiling: the set may only shrink, the ceiling may only go down, each
listed file must open with its header, and a NEW three-partial type fails until it
is listed. Prefer extracting a real collaborator over
adding a partial: `MainWindow`'s prewarm, shell layout, capture and diagnostics
partials became classes under `Views/Shell/` because each shared zero or one
field with its siblings. Lints that read the shell source therefore glob
`Views/**/MainWindow*.cs` RECURSIVELY; a non-recursive glob passes VACUOUSLY.

**Source files have a size budget.** `Infrastructure/SourceFileSizeBudgetTests`
fails the default gate when a new `.cs` / `.axaml` file passes the limit, or when
a file on its exceptions list grows. The list may only shrink: when you split a
listed file, lower or delete its entry in the same change. Every entry carries
the reason it is KEPT rather than split (Vulkan code, export-boundary
algorithms, preset data, the rig view's namescope).

**Methods have a complexity budget too.** `Infrastructure/MethodComplexityBudgetTests`
measures every method, accessor and operator (syntax-only Roslyn,
`Infrastructure/MethodComplexityMeter`): McCabe cyclomatic complexity 25 and 150
lines. A member over either fails unless it is on the hotspot list
(`MethodComplexityBudgetTests.Hotspots.cs`), which may only shrink: lower or
delete a row when its member shrinks, never add one. Code under the
simplification limits (Vulkan, per-frame rendering, DSP, the engine's managed
edge, `unsafe`) is excluded, each prefix with its reason.

The plan lives in GitHub issues and milestones, not in a file. Do not write
dated audits, reviews, plans or handovers into the repo: put the durable part in
a guide and let the report be the PR description (or the issue). Deleted reports
are in git history.

**If you split or grow one of the files above, update this table and MAP.md in
the same change.**

**Never put a big screen inline in another screen.** Markup nested inside a view
is not a type a second host can name; `PlayModeView` reached 780 KB that way.
Lift it into its own view (as `Views/PlayMode/AmpFxRigView.axaml` was).

`README.md` is written for users — this file, ARCHITECTURE.md and MAP.md are the
source of truth for structure. `CONTRIBUTING.md` is the short human version of
Validation, evidence and code style below; when you change one of those rules,
change it there too. `SECURITY.md`, `CODE_OF_CONDUCT.md` and the `.github/`
issue and PR templates are the rest of the community files.

## Solution layout (DotCharter.sln), full table and dependency rules

| Project | Purpose |
|---|---|
| `DotCharter.Core` | Domain model (arrangements, notes, chords, beats) + timeline/fretboard math. Bottom of the graph — no deps |
| `DotCharter.Analysis` | Audio → chart auto-analysis: onset/pitch → draft notes + beat-map detection |
| `DotCharter.Rendering` | GPU chart rendering (Vulkan only): 3D highway + composed-Vulkan 2D timeline |
| `DotCharter.Localization` | `.resx` (EN + de-DE + ja-JP) + `{localization:Loc}` markup |
| `DotCharter.Tone` | The tone rig and the audio host (namespaces `DotCharter.Tone.*`): `JuceAudioPlaybackService`, `Audio/Dsp`, VST, MIDI, TONE3000/NAM, `RigConfig`. Core + Localization + JuceSharp only. See `DotCharter.Tone/AGENTS.md` |
| `DotCharter.Services` | Service layer: Audio/ (playback coordination, stems), Editing/, Session/, Parsing/, State/, Gameplay/, Online/, Import/, Export/, Infrastructure/, Update/ |
| `DotCharter.Presentation` | Screen contracts (`Contracts/<Area>/`), screen view models (`Screens/<Area>/`), area state (`Shared/`). Core + Localization + Services + Tone + CommunityToolkit.Mvvm only — no Avalonia, Dock, SkiaSharp or JuceSharp. See [docs/agents/ui/target-architecture.md](../../docs/agents/ui/target-architecture.md) |
| `DotCharter.Presentation.Fakes` | Services-free fakes of those contracts, each with a static `Variants` list, for the gallery and tests. **`DotCharter.UI` never references it** |
| `DotCharter.Tone.UI` | The rig's UI (`AmpFxRigView` and its view models) and the design system (Fire + tokens, fonts, rig art); namespaces `DotCharter.Tone.UI.*`. XAML elsewhere reaches it with `using:` (or an `assembly=`-qualified `clr-namespace:`). See `DotCharter.Tone.UI/AGENTS.md` |
| `DotCharter.UI` | Avalonia app + composition root: Views/, ViewModels/, Controls/ (custom-drawn), Styles/, Platform/ (OS adapters behind Services interfaces), Play Mode |
| `DotCharter.RigLab` | The rig as a standalone app. References Core/Localization/Tone/Tone.UI and NEVER UI or Services (`RigLabDependencyTests`); published by `tools/release/Publish-RigLab.ps1` |
| `DotCharter.Launcher` | The release's top-level `DotCharter.exe` (#413): a NativeAOT stub that starts `app\DotCharter.exe` with the same command line and exit code. References nothing first-party. The app resolves install-scoped paths (portable config, `tools/`, `voices/`, `models/`) through `InstallLocation.InstallRoot`, never `AppContext.BaseDirectory` — see [split-install.md](../../docs/agents/services/infrastructure-update/split-install.md) |
| `DotCharter.UI.Gallery` | Standalone host (assembly `DotCharter.Gallery`) for screens on FAKE view models — no audio, no JUCE, no DI. `DotCharter.Gallery.exe --capture <dir>` writes a PNG per page and variant. See `DotCharter.UI.Gallery/AGENTS.md` |
| `DotCharter.Tests` | xUnit on Microsoft.Testing.Platform. The default gate is ~21,900 cases (21,927 on 2026-10-21; the gate wrapper prints the live count); the release lane (`tools/ci/release-lane.json`) adds ~120; `UiPerfSmokeTests` run as a SEPARATE invocation (see Validation) |
| `JuceSharp/` | **Git submodule** ([SomeeName/JuceSharp](https://github.com/SomeeName/JuceSharp), private). C# wrapper + native C++ engine. See `JuceSharp/src/JuceSharp/README.md` |

Dependency direction (bottom → top): `Core` → { `Analysis`, `Rendering`,
`Tone` → `Services` } → `Presentation` → `UI`; `Presentation` also references
`Tone` directly, and `Tone.UI` (on `Tone`) sits under both `UI` and `RigLab`. `Services` also references `Analysis` (the audio-lint
wrapper). `Rendering` is consumed by `UI` alone. `Analysis` stays Core +
JuceSharp.Dsp and `Rendering` stays Core — neither may ever reference `Services`.
`Localization` is a leaf that `Tone`, `Services` and `UI` all depend on.
`Tests` references everything.

**The direction is a TEST, not a convention** —
`DotCharter.Tests/Infrastructure/AssemblyLayeringTests.cs` carries each
assembly's exact allowed set of `DotCharter.*` references, asserts the
Presentation assemblies reference nothing named `Avalonia*` / `Dock*` /
`SkiaSharp*` / `JuceSharp*`, and that UI never references `Presentation.Fakes`.
**Declare every dependency you use** in the csproj; a reference that only arrives
transitively breaks the day the middle project changes.

Local reference checkouts, scratch projects and the corpus live outside the repo
in the sibling `DotCharter-local/` directory on the owner's machine; an ignored
`TestData/` junction keeps test discovery working. Never delete the junction's
target. CI can omit the optional corpus. Agent worktrees go under
`.claude/worktrees/` and must never be staged.

## Validation, full detail

**Two invocations, and that is the whole reason for the filter — it is NOT a
hang.** `Avalonia.Headless` has a process-global dispatcher: run in the same
testhost as the gate, almost every headless case fails and takes an unrelated
gate test with it. A separate process has its own dispatcher. **Do not "solve" it
by serializing the gate.** A `dotnet test` with NO filter adds the gate's
exclusion itself (`DotCharter.Tests/TestEntryPoint.cs`); any explicit filter
turns that off. For the same reason the headless suite is SPLIT across
concurrent processes, whole classes per shard: `tools/agent/Invoke-Gate.ps1
-Headless` runs 4 shards (`-Shards N`), CI runs 2, all through
`tools/ci/HeadlessShards.psm1`. **The gate is sharded too** (`-Gate`, 4 shards,
CI 2, `tools/ci/GateShards.psm1`): shards 1..N-1 run explicit class lists from
`tools/ci/gate-shard-weights.json`, the last runs the gate's own filter minus
every listed class, so the shards are the gate's case set exactly — a new class
runs in that remainder. `Invoke-Gate.ps1 -SelfTest` proves it against the
runner's listing; `-Shards 1` is the one-process run above. `-Gate -Headless`
runs both suites AT ONCE when `-MinFreeGB` (12) is free, else one after the
other. After the gate the 20 slowest classes are printed and every class's
duration lands in `gate-durations.json` beside `gate.trx`.
**CPUs (#171).** `Invoke-Gate.ps1` and `Merge-Train.ps1` pin themselves at
below-normal priority on CPUs 8-15 (the owner games on 0-7); there is no game
detection. `-AllCores`, passed by hand, uses every core. The first output line
prints the mask and the reason ("default" / "-AllCores"); shards default to 4 on
half, 8 on all. The decision is `tools/agent/DevBuild.psm1`'s `Get-DevCpuPlan`.
Measured 2026-10-21 on CPUs 8-15 with other gates running: `-Build -Gate
-Headless` 404–572 s concurrent against 915 s one after the other; the gate
alone 363 s in 4 shards against 465 s in one process. Eight cores are the ceiling, not the sharding: under load the
summed case time triples, and one long serial chain bounds any split: a
class's cases run one after another. That is why the slow end-to-end classes sit
in the release lane and why its heaviest scenarios are nested classes (each its
own collection, so they run side by side): measured the same day, the `-Gate`
wall time was 291 s with the ToneMatcherTests full matches in it (22,038 cases)
and 122 s without (21,927 cases).
Exception signatures and the known-bad list are in
[docs/agents/testing/perf-harnesses.md](../../docs/agents/testing/perf-harnesses.md)
and `DotCharter.Tests/AGENTS.md`.

**The default gate is NOT every non-headless class: the RELEASE LANE holds the
slowest.** `tools/ci/release-lane.json` is the ONE declared list of heavy
corpus / integration / determinism classes (the snapshot-export and sync-guard
tooling tests, the analysis determinism check, the full tone-match and
live-detection harnesses, the native-engine tone fidelity checks), each with the
reason it is not a per-change check. `Invoke-Gate.ps1 -Gate` and a plain
`dotnet test` leave them out; `Invoke-Gate.ps1 -ReleaseLane` runs exactly them,
sharded the same way (`-Gate -ReleaseLane` = both, the old full set). **It is
never dropped:** `Merge-Train.ps1` runs it after a green gate on every train that
is not docs-only (and records it), `New-Release.ps1` runs it in preflight with no
skip switch (it reuses only a green record of the same tree, `-ForceReleaseLane`
re-runs), and CI's test job passes `-ReleaseLane`.
`Infrastructure/ReleaseLaneLintTests` replays every lane's real shard plans over
every test class and fails one that runs in no lane or in two. Move a class
there by what it tests, not to hide a slow unit test; touching a lane class's
subsystem means running it yourself with `-FilterClass`.

**The headless-UI half BLOCKS a release and runs in CI as its own job** —
`tools/release/New-Release.ps1` runs it in preflight, and
`.github/workflows/build.yml`'s `headless-ui` job runs
`tools/ci/Invoke-HeadlessUiCi.ps1`. `Infrastructure/CiWorkflowLintTests` keeps the
gate (`Invoke-Gate.ps1 -Gate`) and the headless suite in different jobs. It is not in the commit gate; run it
yourself after a view change.

**While iterating, `Invoke-Gate.ps1 -Affected`** (`-Base <ref>`, default
`origin/main`; `-SelectOnly` prints and stops) builds and runs only the test
classes that cover the diff, each printed with its reason: the changed test
files' classes, classes that name a changed file or its type, the test folder
mirroring the changed folder, ALWAYS the structural lints, and the headless
classes naming a changed view. A change to `tools/`, a project, props, targets or
global config runs the full gate instead and says so. **It is a loop aid, never
the gate:** it records no green tree and the merge train still runs the full
gate once. The rules are the header of `tools/agent/AffectedTests.psm1`, pinned
by `Infrastructure/AffectedTestsModuleTests`.

**CI runs on its own only on the PUBLIC repos; the private dev repos run it by
hand.** `build.yml` triggers on `workflow_dispatch`, push to `main` and
`pull_request`, and every job carries
`if: github.event_name == 'workflow_dispatch' || !github.event.repository.private`:
on the public `SomeeName/DotCharter` (free minutes) a push or PR runs it all; on
the private dev repo it produces a run whose jobs are all SKIPPED (0 minutes —
2,000 free a month there, Windows billing at 2x). Start a dev-repo run with the
"Run workflow" button or `gh workflow run build.yml --ref <branch>` (add
`-f native-tests=true` for the native suite + RT gate); merges there are decided
on the local gate + headless run above. `CiWorkflowLintTests` fails a workflow
with a non-manual trigger and a job without that gate (the one exception is
`public-sync-watch.yml`: schedule or dispatch, and only in the `*-Dev` repos;
JuceSharp's own `CiWorkflowLintTests` holds its workflow to the same rule).
`SUBMODULES_TOKEN`
is optional: with it the submodule comes from the private dev JuceSharp, without
it (public repo, fork PRs) anonymously from the public one. It lives for ONE git
command (`git -c credential.helper= -c url…insteadOf=… submodule update`, never
a global git config), and only the submodule checkout and, on the private repo
alone, the native fetch may read a secret. Every CI restore is `--locked-mode`
against the committed `packages.lock.json` files (`Directory.Build.props`; a
package change commits its lock-file update). Every action is
pinned by full commit SHA, every checkout sets `persist-credentials: false`, the
concurrency group includes the event (a skipped push run never cancels a manual
one) and the artifact-pruning job never runs on a pull request — all linted by
`CiWorkflowLintTests`. The native runtime
is built once per run and cached on the JuceSharp gitlink, so a run that does
not bump the submodule compiles no C++.

**The public variant** (#887): the git-ignored `plugins/<name>/` folders may hold
optional plugins (each its own repository) that `DotCharter.UI` references and
declares when present. `tools/agent/Invoke-Gate.ps1 -Public` builds with
`-p:DotCharterPlugins=false` (ignores `plugins/` even when something is there) and
runs the gate; a release publishes that way unless `New-Release.ps1 -WithPlugins`
asks for the plugin build ([tools/release/README.md](../../tools/release/README.md) § Two
modes). The product never names a plugin (`Infrastructure/PluginNeutralityLintTests`):
a plugin's name, tooling and decisions live in its own repository. Reader entry points
ask `IPackageFeatures` — [contested-features.md](../../docs/agents/services/contested-features.md).

JuceSharp tests: `dotnet test --project JuceSharp\tests\JuceSharp.Tests\JuceSharp.Tests.csproj`
(some StemSeparator/ChildProcess tests are environment-flaky — compare against a
clean tree before blaming your change).

**Kill stray test hosts before rebuilding.** A test that boots the real app
session or a JUCE engine can hang the testhost at teardown; the corpse holds the
output DLLs (MSB3027 on the next build) and, if it opened an audio engine, holds
the ASIO device EXCLUSIVELY. Kill `testhost.exe`, `DotCharter.Tests.exe` and
`JuceSharp.Tests.exe`.

## Every change ships with its own evidence, full detail

**A change is not done until something would fail if it regressed.** Not "I ran
the app", not "the build is clean" — a test, or a stated reason why one is
impossible.

- **New behaviour ⇒ a test in the DEFAULT gate.** Prefer a pure type with an
  injected clock over anything timed (see `DotCharter.Tests/AGENTS.md`).
- **New/edited VIEW ⇒ a construction + attach + layout smoke test.** These XAML
  faults build clean and fail only at runtime: an unresolvable `StaticResource`;
  a `StringFormat` given a `Binding`; a `RelayCommand<int>` fed a literal
  `CommandParameter`; a view that fails to construct and renders as "Not Found: …";
  `Padding="{DynamicResource SpaceXs}"` (`Space*` is an `x:Double`, `Padding` a
  `Thickness`). Model the test on `DotCharter.Tests/Perf/*ConstructionUiPerfSmokeTests.cs`:
  attach to a real `Window`, `Show()`, lay out, and assert the subtree **measured
  non-zero** — a view whose styles failed still arranges, it just has no size.
- **Prove the test is not vacuous.** Break the thing on purpose, watch it fail,
  restore. A guard nobody has seen fail is a guard nobody should trust.
- **Never a wall-clock assertion in the default gate.** Assert STRUCTURE ("this
  service is not constructed eagerly", "prewarm lays out") and LOG the timing.
- **A DI registration nobody resolves is a DEAD FEATURE.** An `AddSingleton`
  factory never runs until someone asks for the type, so a registered-but-unused
  service compiles, passes its own unit tests and reads as shipped.
  `UI/ServiceRegistrationReachabilityTests` fails if a registered `DotCharter.*`
  service type is named by no product code or XAML outside `App*.cs` (it strips
  comments and blanks type declarations, so `class Foo : IFoo` does not count as
  a use). `UI/PowerToolReachabilityTests` is the per-feature example that also
  lints the VERBS — **a service the app resolves but never DRIVES is the same
  fault one level in.**
- **A new third-party package, asset folder, bundled tool or downloadable model
  needs its entry in `THIRD-PARTY-NOTICES.md`** (licence, source, what ships).
  `Infrastructure/ThirdPartyNoticeCoverageTests` reads each inventory from where
  it is declared and fails the gate on a gap. `LICENSE` stays the unmodified GPL
  text, so GitHub detects it.
- **Two DI shapes are bugs on their own** (both linted by `UI/AppServiceGraphTests`):
  a singleton that implements `IAsyncDisposable` but not `IDisposable` makes
  `ServiceProvider.Dispose()` throw and skip every earlier singleton's disposal;
  and a TRANSIENT `IDisposable` resolved from the ROOT provider is tracked for the
  life of the process even after you dispose it — resolve those from a per-owner
  `IServiceScope`.

## Sections the root guide condenses (original wording, kept so nothing is lost)

### Opening and "Start here" (full wording)


This file holds **current facts and standing rules only**. How a number or a rule
got here is in `git log -p AGENTS.md`, not in the text. Keep it that way: a guide
nobody can read in one sitting stops being followed.

## Start here

- **[docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md)** — the human overview: the
  layers, the two main flows (play a song, save a chart) and why each boundary
  exists. Read it once.
- **[docs/agents/MAP.md](../../docs/agents/MAP.md)** — symbol→file index: the save/export
  pipeline, the DI table, the five files a new menu item touches, and how to
  navigate the big files. Read it instead of grepping blind.
- **[docs/agents/workflow.md](../../docs/agents/workflow.md)** — how work flows: issue
  with a screenshot → branch → a test proven to fail → gates → PR with before/after
  images → merge (several at once: `tools/agent/Merge-Train.ps1`), and how to edit
  files without losing backslashes or line endings (`tools/agent/Replace-InFile.py`).

### Code style (full wording)

The build enforces C# style: every rule in [.editorconfig](../../.editorconfig) is
`warning`, which `EnforceCodeStyleInBuild` + `TreatWarningsAsErrors` make a
build ERROR in every `DotCharter.*` project. Each rule's block in that file
gives the counts behind it. Fix a violation; never suppress it (no `#pragma`,
no `[SuppressMessage]`, no per-folder severity). The non-obvious conventions:

- `var` everywhere; file-scoped namespaces; explicit `private`.
- Braces only when the BODY spans lines (stacked `foreach` headers included);
  `if (x) return;` stays bare.
- `=>` for any method, constructor, property or accessor whose body fits on
  one line; operators keep block bodies.
- Private fields `_camelCase`, **private static readonly fields and every
  `const` PascalCase**, local constants `camelCase`.
- A `{ }` block under a `case` label sits at the label's indent.
- `dotnet format whitespace|style DotCharter.sln --exclude JuceSharp/` applies
  the code fixes. It also strips UTF-8 BOMs and rewrites CRLF files to LF,
  which is churn: put back what it changed that the build does not ask for.
- `this.`, `Int32` and throw expressions are listed but IDE-only: the
  command-line build does not report them.
- Files committed with CRLF keep CRLF: the `end_of_line = crlf` section of
  `.editorconfig` declares the ones where IDE0055 would otherwise flag a
  line break next to a comment.
- `.axaml` has no build-enforced formatter; XAML style is not checked.

### Stem separation (full wording)

- **Stems are NEVER regenerated.** They are cached as one compressed `.dcstems`
  archive per song, keyed by song identity folded with an audio content
  signature. Check the cache before separating; save after.
- **Opus stems are playback-only** — never feed them to live note detection.

Full detail: [docs/agents/services/stem-separation.md](../../docs/agents/services/stem-separation.md).

### Sub-guides (full list)

Every project has its own `AGENTS.md` (and a `CLAUDE.md` that imports it). Read
the one for the area you are touching:

- `DotCharter.UI/AGENTS.md` — UI router: hard rules + the routing table into [docs/agents/ui/](../../docs/agents/ui/)
- `DotCharter.Services/AGENTS.md` — service-layer router: folder map, threading contracts, an index of hard rules; the rules live in [docs/agents/services/](../../docs/agents/services/)
- `DotCharter.Tone/AGENTS.md`, `DotCharter.Tone.UI/AGENTS.md`, `DotCharter.RigLab/AGENTS.md` — the rig's assemblies and the standalone rig
- `DotCharter.Core/AGENTS.md` — domain model + timeline invariants, cloner rule
- `DotCharter.Analysis/AGENTS.md` — audio→chart pipeline, determinism contract
- `DotCharter.Rendering/AGENTS.md` — Vulkan 3D + composed-Vulkan 2D, swapchain lifetime rules
- `DotCharter.Localization/AGENTS.md` — resx conventions
- `DotCharter.Presentation/AGENTS.md`, `DotCharter.Presentation.Fakes/AGENTS.md` — screen contracts and their fakes
- `DotCharter.UI.Gallery/AGENTS.md` — the screen gallery: add-a-page recipe, capture commands
- `DotCharter.Tests/AGENTS.md` — test layout, the headless filter, timing conventions
- `JuceSharp/src/JuceSharp/README.md` — the native audio submodule
- [docs/agents/claude-code.md](../../docs/agents/claude-code.md) — Claude Code: `.claude/rules/`, LSP, the Avalonia MCP, manual-only workflows

### Bounded agent work (full wording)

At most **three subagents**, only for independent tasks with explicit file and
outcome ownership. Read MAP.md and the relevant subtree guide once; return paths,
symbols, evidence and next action instead of raw logs. The orchestrator owns the
integration and release gates. The loop is [docs/agents/workflow.md](../../docs/agents/workflow.md).
