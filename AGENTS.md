# DotCharter Agent Guide

Avalonia 12 desktop charting app (Rocksmith-style) with a native JUCE audio engine.
Current branch: `main`. .NET 10, Windows-first. Historical `master` used OwnAudio (gone).

This file holds **current facts and standing rules only**. How a number or a rule
got here is in `git log -p AGENTS.md`, not in the text. Keep it that way: a guide
nobody can read in one sitting stops being followed.

## Start here

- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — the human overview: the
  layers, the two main flows (play a song, save a chart) and why each boundary
  exists. Read it once.
- **[docs/agents/MAP.md](docs/agents/MAP.md)** — symbol→file index: the save/export
  pipeline, the DI table, the five files a new menu item touches, and how to
  navigate the big files. Read it instead of grepping blind.
- **[docs/agents/workflow.md](docs/agents/workflow.md)** — how work flows: issue
  with a screenshot → branch → a test proven to fail → gates → PR with before/after
  images → merge (several at once: `tools/agent/Merge-Train.ps1`), and how to edit
  files without losing backslashes or line endings (`tools/agent/Replace-InFile.py`).

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
small, not the CLASS** — the class totals are in [MAP.md](docs/agents/MAP.md), and
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

## Solution layout (DotCharter.sln)

| Project | Purpose |
|---|---|
| `DotCharter.Core` | Domain model (arrangements, notes, chords, beats) + timeline/fretboard math. Bottom of the graph — no deps |
| `DotCharter.Analysis` | Audio → chart auto-analysis: onset/pitch → draft notes + beat-map detection |
| `DotCharter.Rendering` | GPU chart rendering (Vulkan only): 3D highway + composed-Vulkan 2D timeline |
| `DotCharter.Localization` | `.resx` (EN + de-DE + ja-JP) + `{localization:Loc}` markup |
| `DotCharter.Tone` | The tone rig and the audio host (namespaces `DotCharter.Tone.*`): `JuceAudioPlaybackService`, `Audio/Dsp`, VST, MIDI, TONE3000/NAM, `RigConfig`. Core + Localization + JuceSharp only. See `DotCharter.Tone/AGENTS.md` |
| `DotCharter.Services` | Service layer: Audio/ (playback coordination, stems), Editing/, Session/, Parsing/, State/, Gameplay/, Online/, Import/, Export/, Infrastructure/, Update/ |
| `DotCharter.Presentation` | Screen contracts (`Contracts/<Area>/`), screen view models (`Screens/<Area>/`), area state (`Shared/`). Core + Localization + Services + Tone + CommunityToolkit.Mvvm only — no Avalonia, Dock, SkiaSharp or JuceSharp. See [docs/agents/ui/target-architecture.md](docs/agents/ui/target-architecture.md) |
| `DotCharter.Presentation.Fakes` | Services-free fakes of those contracts, each with a static `Variants` list, for the gallery and tests. **`DotCharter.UI` never references it** |
| `DotCharter.Tone.UI` | The rig's UI (`AmpFxRigView` and its view models) and the design system (Fire + tokens, fonts, rig art); namespaces `DotCharter.Tone.UI.*`. XAML elsewhere reaches it with `using:` (or an `assembly=`-qualified `clr-namespace:`). See `DotCharter.Tone.UI/AGENTS.md` |
| `DotCharter.UI` | Avalonia app + composition root: Views/, ViewModels/, Controls/ (custom-drawn), Styles/, Platform/ (OS adapters behind Services interfaces), Play Mode |
| `DotCharter.RigLab` | The rig as a standalone app. References Core/Localization/Tone/Tone.UI and NEVER UI or Services (`RigLabDependencyTests`); published by `tools/release/Publish-RigLab.ps1` |
| `DotCharter.Launcher` | The release's top-level `DotCharter.exe` (#413): a NativeAOT stub that starts `app\DotCharter.exe` with the same command line and exit code. References nothing first-party. The app resolves install-scoped paths (portable config, `tools/`, `voices/`, `models/`) through `InstallLocation.InstallRoot`, never `AppContext.BaseDirectory` — see [split-install.md](docs/agents/services/infrastructure-update/split-install.md) |
| `DotCharter.UI.Gallery` | Standalone host (assembly `DotCharter.Gallery`) for screens on FAKE view models — no audio, no JUCE, no DI. `DotCharter.Gallery.exe --capture <dir>` writes a PNG per page and variant. See `DotCharter.UI.Gallery/AGENTS.md` |
| `DotCharter.Tests` | xUnit on Microsoft.Testing.Platform. The default gate is ~19,500 cases (19,489 on 2026-10-11; the gate wrapper prints the live count); `UiPerfSmokeTests` run as a SEPARATE invocation (see Validation) |
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

## Validation

```powershell
dotnet build DotCharter.sln --nologo -v:m
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-not-class "*UiPerfSmokeTests"
# …and, after ANY view/XAML change, the headless-UI suite as its OWN run:
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-class "*UiPerfSmokeTests"
```

**Two invocations, and that is the whole reason for the filter — it is NOT a
hang.** `Avalonia.Headless` has a process-global dispatcher: run in the same
testhost as the gate, almost every headless case fails and takes an unrelated
gate test with it. A separate process has its own dispatcher. **Do not "solve" it
by serializing the gate.** A `dotnet test` with NO filter adds the gate's
exclusion itself (`DotCharter.Tests/TestEntryPoint.cs`); any explicit filter
turns that off. For the same reason the headless suite is SPLIT across
concurrent processes, whole classes per shard: `tools/agent/Invoke-Gate.ps1
-Headless` runs 4 shards (`-Shards N`), CI runs 2, all through
`tools/ci/HeadlessShards.psm1`. Budget ~3 minutes for it on an idle box (157 s
measured 2026-10-04; one process was ~10 min) and ~3–4 minutes for the gate.
Exception signatures and the known-bad list are in
[docs/agents/testing/perf-harnesses.md](docs/agents/testing/perf-harnesses.md)
and `DotCharter.Tests/AGENTS.md`.

**The headless-UI half BLOCKS a release and runs in CI as its own job** —
`tools/release/New-Release.ps1` runs it in preflight, and
`.github/workflows/build.yml`'s `headless-ui` job runs
`tools/ci/Invoke-HeadlessUiCi.ps1`. `Infrastructure/CiWorkflowLintTests` keeps the
two `dotnet test`s in different jobs. It is not in the commit gate; run it
yourself after a view change.

**CI runs only when started by hand** — the "Run workflow" button, or
`gh workflow run build.yml --ref <branch>` (add `-f native-tests=true` for the
native suite + RT gate). A push, a pull request or a merge starts nothing: the
private repo gets 2,000 free minutes a month, Windows minutes bill at 2x, and
merges are decided on the local gate + headless run above. `CiWorkflowLintTests`
fails any workflow with another trigger. The native runtime is built once per
run and cached on the JuceSharp gitlink, so a run that does not bump the
submodule compiles no C++.

**The public variant** (#887): the git-ignored `plugins/<name>/` folders may hold
optional plugins (each its own repository) that `DotCharter.UI` references and
declares when present. `tools/agent/Invoke-Gate.ps1 -Public` builds with
`-p:DotCharterPlugins=false` (ignores `plugins/` even when something is there) and
runs the gate; a release publishes that way unless `New-Release.ps1 -WithPlugins`
asks for the plugin build ([tools/release/README.md](tools/release/README.md) § Two
modes). The product never names a plugin (`Infrastructure/PluginNeutralityLintTests`):
a plugin's name, tooling and decisions live in its own repository. Reader entry points
ask `IPackageFeatures` — [contested-features.md](docs/agents/services/contested-features.md).


JuceSharp tests: `dotnet test --project JuceSharp\tests\JuceSharp.Tests\JuceSharp.Tests.csproj`
(some StemSeparator/ChildProcess tests are environment-flaky — compare against a
clean tree before blaming your change).

**Kill stray test hosts before rebuilding.** A test that boots the real app
session or a JUCE engine can hang the testhost at teardown; the corpse holds the
output DLLs (MSB3027 on the next build) and, if it opened an audio engine, holds
the ASIO device EXCLUSIVELY. Kill `testhost.exe`, `DotCharter.Tests.exe` and
`JuceSharp.Tests.exe`.

## Every change ships with its own evidence

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

## Code style

The build enforces C# style: every rule in [.editorconfig](.editorconfig) is
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

## UI responsiveness budget

**Switching a view, tab, screen or dialog must complete in ≤150 ms, and should
be under 20 ms.** The cost is almost never construction — it is the FIRST layout
of a hidden subtree (`IsVisible=false` skips measure, so styles, templates and
text layout all land on the first switch). Three mechanisms keep it inside the
budget and **all of them must stay**: `Views/Shell/MainWindowPrewarm`
(`PrewarmTopLevelViews`), `Views/Shell/HiddenSubtreePrewarmer` +
`IPrewarmableSurfaces`, and `Views/Shell/MenuFlyoutPrewarmer` + `PrewarmableFlyout`.

- **A new hidden surface must be exposed through `IPrewarmableSurfaces`.**
  Realize it by measuring its VISIBLE children — never by writing `IsVisible`.
  Prewarm work is always `DispatcherPriority.Background` and AFTER first paint.
- **Never realize a popover while it is DETACHED.** A detached subtree has no
  style scope, so every `DynamicResource` caches its FALLBACK and the popover
  comes back unstyled. A close takes the popup out of the logical tree;
  `PrewarmableFlyout.OnClosed` puts it back into its target's live scope FIRST
  and only then re-realizes it (#176: repeat opens 25-90 ms → ~5 ms).
  `Perf/PopoverRestyleUiPerfSmokeTests` compares brushes across a close;
  `Perf/PopoverReopenUiPerfSmokeTests` pins the re-parent.
- **Never read a cold-start number without checking `r2r=`.** `PublishReadyToRun`
  only applies to `dotnet publish -r win-x64`, so the dev build JIT-compiles its
  whole startup path (over half its cold start). The `StartupPhases:` log line
  ends with `r2r=yes|no|?` (`Diagnostics/ReadyToRunProbe`); a number from an
  `r2r=no` run is a JIT measurement. `Window.Show()` IS the initial layout pass.

Full detail: [docs/agents/ui/rendering-perf-detail.md](docs/agents/ui/rendering-perf-detail.md).

## Audio architecture (JUCE)

Three layers, bottom up:

1. `JuceSharp/src/JuceSharp.Native` — C++ JUCE shared lib exporting a versioned
   C ABI (`include/dotcharter_audio.h`, `DCA_ABI_VERSION`). Owns
   `AudioDeviceManager`, mixer, transports, VST3 chain.
2. `JuceSharp/src/JuceSharp` — thin P/Invoke wrapper. The DLL is deployed to
   `runtimes/win-x64/native/juce_sharp.dll` by CMake POST_BUILD.
3. `DotCharter.Tone/Audio/JuceAudioPlaybackService.cs` (namespace
   `DotCharter.Tone.Audio`) implements `IAudioPlaybackService` on the wrapper.

The toolchain that actually exists on the owner's machine (VS 2026 Build Tools,
CMake bundled with CLion, neither on PATH) is in `JuceSharp/AGENTS.md` — the
generic `cmake -G "Visual Studio 17 2022"` line does not work there.

- All real-time audio lives in the native engine. **Never add managed audio
  pumps on the hot path.** The wrapper stays marshalling + lifetime only.
- **The DLL is a build artifact, not tracked.** An ABI bump means rebuilding it
  before any managed code runs, or engine open fails with a version mismatch.
- **Contributors get it prebuilt (#832).** When `juce_sharp.dll` is MISSING, the
  build (`tools/setup/NativeRuntime.targets`, hooked by `Directory.Solution.targets`
  and `Directory.Build.targets`) runs `tools/setup/Get-NativeRuntime.ps1`: it
  downloads the pair for the gitlink from JuceSharp's `native-prebuilt` release
  and installs it only if its SHA-256s match `tools/setup/native-runtime.json` and
  `dca_abi_version()` matches the header. A present DLL is never touched; the
  build in CI never fetches unless `DotCharterFetchNative=true` (CI's `native` job
  calls the script as a step instead, with `SUBMODULES_TOKEN` as `GH_TOKEN` while
  the release is private, and compiles C++ only if that fails or the native suite
  is asked for); `DotCharterSkipNativeFetch=true`
  turns it off. **A gitlink bump publishes its pair**
  (`tools/release/Publish-NativeRuntime.ps1 -Upload`) and commits the pin with
  it — `Infrastructure/NativeRuntimeFetchTests` fails while the gitlink is unpinned.
- JuceSharp changes: commit in the submodule first, then bump the pointer here.
  **Push the submodule before the parent** or the gitlink dangles.
- `PositionChanged` fires at up to 250 Hz on a timer thread. UI-visible state
  goes through a coalescing `Dispatcher.UIThread.Post` (see `AudioPlaybackCoordinator`).
- **Any master-bus or per-seat state must be replayed on engine REBUILD** (device
  change, NAM 48 kHz request, watchdog recovery), or it is dropped silently.
- **The live seat count follows the input configuration, and exactly one place
  writes it**: `LiveInputSeatLayout.FromInputConfig` (recorded from
  `ApplyAudioConfigurationCore`). A temporary override (the latency wizard's
  channel probe) layers on top and hands it back. "Two guitars" is an explicit
  setting, never inferred from a second open channel (that is a stereo pickup).
- **A capability the app cannot honour must never look live.** The UI ships it
  visibly disabled with its reason printed.

Full detail: [docs/agents/services/audio-engine.md](docs/agents/services/audio-engine.md).

## Stem separation (htdemucs)

- **Stems are NEVER regenerated.** They are cached as one compressed `.dcstems`
  archive per song, keyed by song identity folded with an audio content
  signature. Check the cache before separating; save after.
- **Opus stems are playback-only** — never feed them to live note detection.

Full detail: [docs/agents/services/stem-separation.md](docs/agents/services/stem-separation.md).

## Sub-guides

Every project has its own `AGENTS.md` (and a `CLAUDE.md` that imports it). Read
the one for the area you are touching:

- `DotCharter.UI/AGENTS.md` — UI router: hard rules + the routing table into [docs/agents/ui/](docs/agents/ui/)
- `DotCharter.Services/AGENTS.md` — service-layer router: folder map, threading contracts, an index of hard rules; the rules live in [docs/agents/services/](docs/agents/services/)
- `DotCharter.Tone/AGENTS.md`, `DotCharter.Tone.UI/AGENTS.md`, `DotCharter.RigLab/AGENTS.md` — the rig's assemblies and the standalone rig
- `DotCharter.Core/AGENTS.md` — domain model + timeline invariants, cloner rule
- `DotCharter.Analysis/AGENTS.md` — audio→chart pipeline, determinism contract
- `DotCharter.Rendering/AGENTS.md` — Vulkan 3D + composed-Vulkan 2D, swapchain lifetime rules
- `DotCharter.Localization/AGENTS.md` — resx conventions
- `DotCharter.Presentation/AGENTS.md`, `DotCharter.Presentation.Fakes/AGENTS.md` — screen contracts and their fakes
- `DotCharter.UI.Gallery/AGENTS.md` — the screen gallery: add-a-page recipe, capture commands
- `DotCharter.Tests/AGENTS.md` — test layout, the headless filter, timing conventions
- `JuceSharp/src/JuceSharp/README.md` — the native audio submodule
- [docs/agents/claude-code.md](docs/agents/claude-code.md) — Claude Code: `.claude/rules/`, LSP, the Avalonia MCP, manual-only workflows

## GitHub workflow: issues, branches, pull requests

The owner manages this project from GitHub's web UI, so every piece of work must
show up there properly. Use `gh`. The repo is `SomeeName/DotCharter`, the
private dev repo (the `DotCharter` name is kept for the public project); every
assignee is `SomeeName`.

1. **An issue first** for every bug, feature or plan. Small chores and doc
   fixes may go straight to a PR. Each issue gets:
   - one type label: `bug`, `enhancement`, `refactor`, `perf`, `chore`,
     `documentation` or `epic`;
   - one or more `area: *` labels: `audio`, `native`, `midi-preview`,
     `library`, `editor`, `play-mode`, `rig`, `ui`, `tests`, `ci`;
   - a milestone: the next release (the lowest open `v0.x.0` milestone —
     `gh api repos/SomeeName/DotCharter/milestones`) or a feature milestone.

   A plan with several parts is an `epic` with **sub-issues**
   (`gh api -X POST repos/SomeeName/DotCharter/issues/<parent>/sub_issues -F sub_issue_id=<child's numeric id>`).
   The body says what happens, the cause with file paths, and the fix and test.
2. **One branch per piece of work, cut from `origin/main`** (fetch first; a
   local `main` can lag). Link it to its issue before pushing, so it shows in
   the issue's Development box:
   `gh issue develop <n> --name <branch> --base main`. Agent work goes in a
   worktree (`dotcharter-worktree` skill).
3. **One pull request per branch.** Create it with
   `gh pr create --base main --assignee SomeeName --label "<type>,area: <x>" --milestone "<m>"`.
   - The body: what, how, evidence (gate and headless numbers, non-vacuity
     proofs), known limits. It is the report, never a dated file in the repo.
   - Put `Fixes #n` / `Closes #n` on their own lines (`Refs #n` for partial
     work), so merging closes the issue.
   - **Push once, when the work is verified.** No push starts CI (see
     Validation); the PR body carries the local gate and headless numbers.
4. **Before merging,** check the branch contains current `main`
   (`git merge-base --is-ancestor origin/main <branch>`); a stale branch goes
   back to be rebased, OR goes in a merge train (`tools/agent/Merge-Train.ps1`:
   the PRs merged onto main in order and gated ONCE, then merged in that order).
   Merge in dependency order (a JuceSharp PR before the DotCharter PR whose
   submodule pointer needs it). Then delete the worktree and branch (see the
   gotchas below).
5. **JuceSharp:** app-driven native work keeps its ONE issue here; the JuceSharp
   PR body says `Fixes SomeeName/DotCharter#n` (or `Refs`) so the issue's timeline
   shows it. Native-only work (RT gate, a flaky native test, ABI or toolchain
   cleanup, a submodule doc) gets its issue in `SomeeName/JuceSharp`.

## Bounded agent work

At most **two subagents**, only for independent tasks with explicit file and
outcome ownership. Read MAP.md and the relevant subtree guide once; return paths,
symbols, evidence and next action instead of raw logs. The orchestrator owns the
integration and release gates. The loop is [docs/agents/workflow.md](docs/agents/workflow.md).

## Agent workflow gotchas

- **Every automated or measurement launch of the app sets `DOTCHARTER_DATA_ROOT`
  to a fresh temp folder, and nothing ever deletes in the user's real
  `%APPDATA%\DotCharter` or `%LOCALAPPDATA%\DotCharter`** (#439: a launch against
  them re-keyed the owner's stem archives and its cleanup deleted them). With the
  override, `Infrastructure/Storage/UserDataFolders` puts every per-user path —
  config, logs, the PSARC/stem/waveform/album-art caches, models, the Vulkan
  pipeline cache — under `<root>\Roaming` and `<root>\Local`, and a portable
  config beside the install is ignored: seed `<root>\Roaming\DotCharter\appconfig.json`
  when the launch needs settings. The log says `Data root override: …` when it
  is active. `Infrastructure/UserDataFolderLintTests` fails a direct
  `GetFolderPath` of either folder anywhere else.
- **A scripted launch of the app goes on a hidden desktop** —
  `Start-HiddenDesktopProcess` from `tools/release/HiddenDesktop.psm1`, never
  `Start-Process`: a minimised launch still takes the foreground about one time
  in five. `Infrastructure/ScriptedAppLaunchLintTests` lints `tools/`.
- **Network listeners must use ports 47810–47829** — the owner's firewall allows
  exactly that inbound range and silently blocks the rest. The app's ONE inbound
  listener is 47821, the TONE3000 OAuth loopback callback
  (`Tone3000OAuth.CallbackPort`). A new listener takes a fixed in-range port,
  never port 0; `Services/Online/InboundListenerPortTests` lints the service layer
  for it.
- **Launch agents with the working directory at the DotCharter root** — a
  worktree is cut from whichever repo the orchestrator's shell is in, and a stray
  `cd JuceSharp` gives every agent a worktree of the submodule. `ls DotCharter.sln`
  first.
- **Agent worktrees do not check out the `JuceSharp` submodule.** Run
  `git -c protocol.file.allow=always submodule update --init JuceSharp` in the
  worktree. Under the Agent tool's `isolation: "worktree"` that is refused; copy
  the main checkout's `JuceSharp/` instead (excluding `.git`) and delete the
  copied `JuceSharp/.git` pointer file. Such a worktree cannot commit to the
  submodule. The `dotcharter-worktree` skill has the full recipe.
- **NEVER `git stash` while other agents are running** — the stash is one stack
  shared by every worktree. A/B against a clean tree with `git show HEAD:<path>`.
- **A file split breaks every lint that locates source BY PATH — silently.**
  Moving code to a new file makes such a lint scan a file that no longer contains
  its subject and pass vacuously, and an exclusion can invert. **After any split,
  grep the test project for the old filename and repoint each hit at the whole
  `<Original>*.cs` set.**
- **A worktree's gate count differs from the main checkout's**, and neither is a
  regression: `TestData/` is git-ignored (a worktree under `.claude/worktrees/`
  still finds the main checkout's by walking parents; one outside the repo does
  not), and `DotCharter.Tests/LocalOnly_*.cs` probes exist only in the owner's
  checkout. Compare counts only within the same tree.
- **Check the merge-base before merging an agent branch**
  (`git merge-base --is-ancestor main <branch>`). A stale branch can silently
  revert a later decision in a hunk that merges clean. Prefer sending it back to
  rebase over hand-resolving.
- **Mind memory when agents build/test concurrently.** A testhost killed under
  memory pressure looks like a flaky test with a moving crash point. If `main`
  crashes identically, check free RAM before blaming a branch.
- **Keep the file-search surface small.** Worktrees and local reference checkouts
  are git-ignored, and `.claude/settings.json` sets `CLAUDE_CODE_GLOB_NO_IGNORE=false`
  — Grep honours `.gitignore` but Glob does not without it, and returns stale
  worktree copies ahead of the real file. Keep both halves.
- Stage explicit paths rather than `git add -A`, and delete stray empty files or
  directories left by shell-quoting accidents.
- `git worktree remove` refuses worktrees with an initialized submodule; verify
  the branch is merged, delete the directory, `git worktree prune`, `git branch -D`.

## Keeping these docs current

Project context lives in the codebase, not in an agent's context window. **When
you change a subsystem in a way that would make its guide misleading — a moved
entry point, a changed invariant, a new gotcha — update that folder's `AGENTS.md`
in the same change.** Terse bullets with real file/type names; current facts, not
history. The `CLAUDE.md` files are one-line `@AGENTS.md` imports — never put
content in them.

**A SAFETY claim in a guide is the dangerous kind of rot, because a reader stops
looking.** Any sentence shaped *"X is bounded / validated / enforced by test T"*,
*"X is the ONLY place that does Y"* or *"X is missing"* must be re-verified
against code whenever you touch that area. **A "this is missing" note is the
worst of them** — it makes the next agent delete working code.

Mechanical backstops:

- `Infrastructure/DocReferenceLintTests` fails the gate on a dead file path, a
  dead `*Tests` name or a dead relative link in any `AGENTS.md` or
  `docs/agents/**`. Its allow-list exists for guides that name deleted things on
  purpose — **adding a name there to turn the build green, without checking the
  doc's claim, defeats the test.**
- `Infrastructure/SourceFileSizeBudgetTests` keeps files from silently growing
  back into god files.
- `Infrastructure/MethodComplexityBudgetTests` does the same for methods.
- Nothing mechanical covers the numbers or the prose. Re-measure; don't carry.
