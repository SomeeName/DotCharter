# DotCharter Agent Guide

Avalonia 12 desktop charting app (Rocksmith-style) with a native JUCE audio engine.
Current branch: `main`. .NET 10, Windows-first. Historical `master` used OwnAudio (gone).

Current facts and standing rules only, loaded every session: keep it small, put detail in `docs/agents/` (history is in `git log -p AGENTS.md`).

## Start here

- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** - layers, main flows, boundaries.
- **[docs/agents/MAP.md](docs/agents/MAP.md)** - symbol→file index (save/export pipeline, DI table). Read it, don't grep blind.
- **[docs/agents/workflow.md](docs/agents/workflow.md)** - issue → branch → a test proven to fail → gates → PR with before/after images → merge (`tools/agent/Merge-Train.ps1`); safe file editing (`tools/agent/Replace-InFile.py`).

**Never `Read` these whole** (measured 2026-10-11; re-measure): grep, then Read with offset/limit.

| File | Instead |
|---|---|
| `DotCharter.Localization/Resources{,.de-DE,.ja-JP}.resx` (~650 KB each) | Grep the key; all THREE stay in sync |
| `JuceSharp/.../dcdsp_*_irs.h` | Generated IR tables, no logic |
| `AmpFxRigView.axaml` (~133 KiB), `Views/PlayMode/Screens/*.axaml`, any other `.axaml` (5–160 KB) | Grep `x:Name` / style class, read ±150 lines (each screen's names live in its own namescope) |
| `Audio/Dsp/Tones/tone_gear_data.json` (~147 KB) | One-line catalogue, query it in code |

Full table and split-type / source-size / method-complexity budgets (ratchets that only shrink):
[guide-detail.md](docs/agents/guide-detail.md). Splitting or growing a listed file -> update that table and
MAP.md in the same change. Plans live in GitHub issues, never dated repo files. `CONTRIBUTING.md` mirrors
Validation, evidence and code style: change both together.

## Solution layout (DotCharter.sln)

| Project | Purpose |
|---|---|
| `DotCharter.Core` | Domain model + timeline/fretboard math; no deps |
| `DotCharter.Analysis` | Audio → chart auto-analysis |
| `DotCharter.Rendering` | Vulkan: 3D highway + 2D timeline |
| `DotCharter.Localization` | `.resx` (en, de-DE, ja-JP) |
| `DotCharter.Tone` | Tone rig + audio host (`JuceAudioPlaybackService`, `Audio/Dsp`, VST, MIDI, NAM) |
| `DotCharter.Services` | Service layer (Audio, Editing, Session, Parsing, State, Gameplay, Online, Import, Export, Update) |
| `DotCharter.Presentation` | Screen contracts + view models; no Avalonia/Dock/SkiaSharp/JuceSharp ([target-architecture.md](docs/agents/ui/target-architecture.md)) |
| `DotCharter.Presentation.Fakes` | Fakes for gallery/tests; **`UI` never references it** |
| `DotCharter.Tone.UI` | Rig UI + Fire design system |
| `DotCharter.UI` | Avalonia app + composition root |
| `DotCharter.RigLab` | Standalone rig app; NEVER references UI or Services |
| `DotCharter.Launcher` | NativeAOT `DotCharter.exe` stub; paths via `InstallLocation.InstallRoot` ([split-install.md](docs/agents/services/infrastructure-update/split-install.md)) |
| `DotCharter.UI.Gallery` | Gallery on FAKE view models |
| `DotCharter.Tests` | xUnit (MTP); ~21,900 gate cases (+ ~120 in the release lane, `tools/ci/release-lane.json`); `UiPerfSmokeTests` run SEPARATELY |
| `JuceSharp/` | **Git submodule** ([SomeeName/JuceSharp](https://github.com/SomeeName/JuceSharp), private): C# wrapper + native C++ engine |

Dependency direction (bottom → top): `Core` → { `Analysis`, `Rendering`, `Tone` → `Services` } → `Presentation` → `UI`;
`Presentation` also references `Tone`; `Tone.UI` sits under `UI` and `RigLab`. `Analysis` stays Core + JuceSharp.Dsp, `Rendering` stays Core;
neither may reference `Services`. `Localization` is a leaf. **The direction is a TEST** (`Infrastructure/AssemblyLayeringTests`):
declare every dependency you use in the csproj ([guide-detail.md](docs/agents/guide-detail.md)).
Reference checkouts/corpus live in sibling `DotCharter-local/` (never delete the `TestData/` junction's target). Worktrees: `.claude/worktrees/`, never staged.

## Validation

```powershell
dotnet build DotCharter.sln --nologo -v:m
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-not-class "*UiPerfSmokeTests"
# …and, after ANY view/XAML change, the headless-UI suite as its OWN run:
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build --filter-class "*UiPerfSmokeTests"
```

- **Two invocations are deliberate, not a hang** (process-global headless dispatcher); do not serialize the gate. `tools/agent/Invoke-Gate.ps1 -Headless` shards it. It blocks a release but is not in the commit gate: run it after a view change.
- **The gate is sharded too** (`Invoke-Gate.ps1 -Gate`; pinned to CPUs 8-15 unless `-AllCores`). **The default gate excludes the RELEASE LANE** (`tools/ci/release-lane.json`): run it with `-ReleaseLane` when you touch a lane class's subsystem; the merge train and `New-Release.ps1` always run it. While iterating use `Invoke-Gate.ps1 -Affected` (a loop aid, never the gate).
- **On the private dev repos CI runs only when started by hand** (`gh workflow run build.yml --ref <branch>`; on the public repos it runs on push/PR); merges are decided on the local gate + headless run. Public variant: `Invoke-Gate.ps1 -Public` (git-ignored `plugins/<name>/` are optional; the product never names a plugin).
- **Kill stray test hosts before rebuilding** (`testhost.exe`, `DotCharter.Tests.exe`, `JuceSharp.Tests.exe`): they hold DLLs and the ASIO device.

Detail: [guide-detail.md](docs/agents/guide-detail.md), [perf-harnesses.md](docs/agents/testing/perf-harnesses.md).

## Every change ships with its own evidence

**Not done until something would fail if it regressed**: a test, or a stated reason why none is possible.

- **New behaviour ⇒ a test in the DEFAULT gate.** Prefer a pure type with an injected clock.
- **New/edited VIEW ⇒ a construction + attach + layout smoke test** asserting the subtree **measured non-zero** (model: `DotCharter.Tests/Perf/*ConstructionUiPerfSmokeTests.cs`).
- **Prove the test is not vacuous:** break the thing, watch it fail, restore.
- **Never a wall-clock assertion in the default gate.** Assert STRUCTURE, LOG timing.
- **A DI registration nobody resolves or drives is a DEAD FEATURE** (`UI/ServiceRegistrationReachabilityTests`).
- **New third-party package/asset/tool/model ⇒ `THIRD-PARTY-NOTICES.md` entry** (`Infrastructure/ThirdPartyNoticeCoverageTests`).
- **DI traps** (`UI/AppServiceGraphTests`): an `IAsyncDisposable`-only singleton; a TRANSIENT `IDisposable` resolved from the ROOT provider.

XAML fault list, DI rationale: [guide-detail.md](docs/agents/guide-detail.md).

## Code style

Every rule in [.editorconfig](.editorconfig) is `warning`, which
`EnforceCodeStyleInBuild` + `TreatWarningsAsErrors` make a build ERROR in every
`DotCharter.*` project. Never suppress (no `#pragma`, `[SuppressMessage]`, per-folder severity). Conventions:

- `var` everywhere; file-scoped namespaces; explicit `private`.
- Braces only when the BODY spans lines (stacked `foreach` headers included);
  `if (x) return;` stays bare.
- `=>` for any method, constructor, property or accessor whose body fits on
  one line; operators keep block bodies.
- Private fields `_camelCase`, **private static readonly fields and every
  `const` PascalCase**, local constants `camelCase`.
- A `{ }` block under a `case` label sits at the label's indent.
- `dotnet format whitespace|style DotCharter.sln --exclude JuceSharp/` also strips BOMs and rewrites CRLF to LF: put that churn back.
- `this.`, `Int32` and throw expressions are IDE-only (the build does not report them).
- Files committed with CRLF keep CRLF (`end_of_line = crlf` section of `.editorconfig`).
- `.axaml` has no build-enforced formatter.

## UI responsiveness budget

View/tab/screen/dialog switches must finish in ≤150 ms (aim <20 ms): new hidden
surfaces go through `IPrewarmableSurfaces`; never realize a detached popover; check
`r2r=` before trusting a cold-start number. Prewarm, popovers, startup: [rendering-perf-detail.md](docs/agents/ui/rendering-perf-detail.md).

## Audio architecture (JUCE)

Native JUCE engine (C ABI `DCA_ABI_VERSION`) -> P/Invoke wrapper -> `DotCharter.Tone/Audio/JuceAudioPlaybackService.cs`.

- Real-time audio lives in the native engine. **Never add managed audio pumps on the hot path.**
- The DLL is an untracked build artifact (ABI bump = rebuild); contributors get it prebuilt (#832). The native pair imports only Windows system DLLs (static MSVC runtime, #176).
- JuceSharp changes: commit the submodule first and **push it before the parent**, then bump the pointer.
- `PositionChanged` fires up to 250 Hz: post UI state through a coalescing `Dispatcher.UIThread.Post`.
- **Any master-bus or per-seat state must be replayed on engine REBUILD**, or it is dropped silently.
- A capability the app cannot honour is shown visibly disabled with its reason.

Engine, seats, ABI, native fetch: [audio-engine.md](docs/agents/services/audio-engine.md) (section "Audio architecture (JUCE): root-guide rules").

## Stem separation (htdemucs)

- **Stems are NEVER regenerated**: one `.dcstems` archive per song; check the cache first, save after.
- **Opus stems are playback-only**: never feed them to live note detection.

Detail: [stem-separation.md](docs/agents/services/stem-separation.md).

## Sub-guides

Each project has its own `AGENTS.md`; read the one for the area you touch: `DotCharter.UI`, `.Services`
(routers into `docs/agents/`), `.Tone`, `.Tone.UI`, `.RigLab`, `.Core`, `.Analysis`, `.Rendering`, `.Localization`,
`.Presentation`, `.Presentation.Fakes`, `.UI.Gallery`, `.Tests`; `JuceSharp/AGENTS.md` and
`JuceSharp/src/JuceSharp/README.md` (native submodule); [docs/agents/claude-code.md](docs/agents/claude-code.md) (Claude Code, LSP, MCP).

## GitHub workflow: issues, branches, pull requests

Use `gh`; repo `SomeeName/DotCharter` (the private dev repo; `DotCharter` is the public name), every assignee `SomeeName`.

1. **Issue first** (chores/doc fixes may skip): one type label, `area: *` labels, the lowest open `v0.x.0` milestone; multi-part plans are an `epic` with sub-issues.
2. **One branch per work item from `origin/main`**: `gh issue develop <n> --name <branch> --base main`. Agent work goes in a worktree (`dotcharter-worktree` skill).
3. **One PR per branch**: `gh pr create --base main --assignee SomeeName --label "<type>,area: <x>" --milestone "<m>"`; body = what, how, evidence, limits; `Fixes #n` / `Refs #n` on own lines. **Push once, when verified** (on the private dev repo a push starts no CI).
4. **Before merging** the branch must contain current `origin/main` (else rebase or merge train); JuceSharp PRs merge first.
5. **JuceSharp:** app-driven native work keeps ONE issue here (`Fixes SomeeName/DotCharter#n` in the JuceSharp PR); native-only work gets its issue in `SomeeName/JuceSharp`.
6. **Public → dev sync (#77):** import every commit merged on a public repo with `tools/release/Import-PublicCommit.ps1` (`Public-Commit:` trailer) before the next release; the snapshot export and `New-Release.ps1` refuse otherwise. Never edit the guard.

Details: [workflow.md](docs/agents/workflow.md) (section "GitHub workflow").

## Bounded agent work

At most **three subagents**, only for independent tasks with explicit file and outcome
ownership. Read MAP.md and the subtree guide once; return paths, symbols, evidence and next
action, not raw logs. The orchestrator owns integration and release gates. Loop: [workflow.md](docs/agents/workflow.md).

## Agent workflow gotchas

Full list: [docs/agents/agent-gotchas.md](docs/agents/agent-gotchas.md). First-bite rules:

- Automated app launches set `DOTCHARTER_DATA_ROOT` to a fresh temp folder; never delete in the real `%APPDATA%\DotCharter` / `%LOCALAPPDATA%\DotCharter` (#439). Scripted launches use `Start-HiddenDesktopProcess`.
- Network listeners use ports 47810–47829 only, never port 0.
- Agents start in the DotCharter root; NEVER `git stash` while agents run; check the merge-base before merging an agent branch.
- A file split silently breaks path-based lints: grep the tests for the old filename and repoint to `<Original>*.cs`.

## Keeping these docs current

Update the folder's `AGENTS.md` in the same change when a guide turns misleading; `CLAUDE.md` files stay one-line `@AGENTS.md` imports. Re-verify any "ONLY place / bounded by test T / is missing" claim you touch. Never allow-list a name in `DocReferenceLintTests` to go green. Detail: [docs/agents/docs-maintenance.md](docs/agents/docs-maintenance.md).

## Routing: touching X -> read Y

UI/XAML -> `DotCharter.UI/AGENTS.md` · services/editing/export -> `DotCharter.Services/AGENTS.md` ·
audio engine/ABI -> [audio-engine.md](docs/agents/services/audio-engine.md) · prewarm/popovers/startup ->
[rendering-perf-detail.md](docs/agents/ui/rendering-perf-detail.md) · Vulkan -> `DotCharter.Rendering/AGENTS.md` ·
tests/headless -> `DotCharter.Tests/AGENTS.md` · budgets, full layout/validation/evidence ->
[guide-detail.md](docs/agents/guide-detail.md) · worktrees/ports/data root -> [agent-gotchas.md](docs/agents/agent-gotchas.md)
