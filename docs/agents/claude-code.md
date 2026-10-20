# Claude Code setup on this repo

Claude Code reads the same `AGENTS.md` tree as every other agent: every `CLAUDE.md` is a
one-line `@AGENTS.md` import, and it stays that way. What is Claude-specific
lives in four committed places, and this page says what each one does.

| Where | What |
|---|---|
| `.claude/settings.json` | Enables the `csharp-lsp` and `clangd-lsp` plugins for this project. Registers the `tools/agent/Guard-DotnetCommand.ps1` hook, which refuses a raw `dotnet build`/`dotnet test`; use `tools/agent/Invoke-Gate.ps1` instead. Sets `CLAUDE_CODE_GLOB_NO_IGNORE=false` |
| `.claude/rules/` | Short rule files. `.claude/rules/tool-selection.md` always loads. The others load only when a matching file is touched: `.claude/rules/realtime-audio.md` (native engine, `JuceSharp.Dsp`, `DotCharter.Services/Audio`), `.claude/rules/native-interop.md` (C ABI, `JuceSharp` wrapper, OS P/Invokes), `.claude/rules/vulkan.md` (`DotCharter.Rendering`, shader compiler), `.claude/rules/avalonia-ui.md` (UI, gallery, localization) and `.claude/rules/tests.md`. Each one points to the deep guide rather than repeating it |
| `.claude/skills/` | Project skills that load when their task comes up: `dotcharter-abi-bump` (add or change a C ABI export), `dotcharter-rt-gate` (build and run `juce_sharp_rtcheck`, through `.claude/skills/dotcharter-rt-gate/scripts/Invoke-RtGate.ps1`), `dotcharter-dsp-module` (add a tone-rig module end to end), `dotcharter-shaders` (recompile only the changed `.spv`, validate and render-test it) and `dotcharter-worktree` (a buildable agent worktree through `.claude/skills/dotcharter-worktree/scripts/New-AgentWorktree.ps1`, plus merge-back and cleanup) |
| `.mcp.json` | The official Avalonia docs MCP server (`avalonia-docs`, `https://docs-mcp.avaloniaui.net/mcp`, no auth needed). Claude Code asks you to approve it the first time |
| `.clangd` + `tools/agent/New-CompileCommands.ps1` | The C/C++ compilation database for clangd (below) |

`.claude/settings.local.json` is personal and git-ignored, and so is Ralph
Loop's state file `.claude/*.local.md`.

## Machine prerequisites (not in the repo)

- **User-scope plugins**: `superpowers`, `context7`, `claude-md-management`,
  `code-review`, `claude-code-setup`, `frontend-design`, `model-router` (from
  the `nobodyohm-web/claude-code-model-router` marketplace), plus
  `ralph-loop` and `feature-dev`, which are only ever run by hand.
  Skill creation uses the built-in `anthropic-skills:skill-creator`, and
  cleanup uses the built-in `/simplify` rather than the `code-simplifier`
  plugin (installing both would duplicate the same capability).
- **`csharp-ls`** (the Roslyn language server behind `csharp-lsp`):
  `dotnet tool install -g csharp-ls`. It loads `DotCharter.sln`; the first
  query waits about 30 s while the solution loads.
- **clangd**: LLVM via `scoop install llvm`. clangd finds the VS 2026 Build
  Tools headers and the Windows SDK on its own.
- **Optional, for Vulkan work**: the Vulkan SDK (`scoop install vulkan`, then
  its `install-vk-layers.ps1`). It provides `VK_LAYER_KHRONOS_validation`,
  which `DOTCHARTER_VK_VALIDATION=1` needs, and `spirv-val`. Without it that
  variable silently does nothing, apart from one debug-log line.
- A new tool on PATH is only seen by a Claude Code session started after it
  was installed.

## clangd and the compilation database

The real native build uses the `"Visual Studio 18 2026"` generator, and CMake
writes no `compile_commands.json` for Visual Studio generators. Run:

```powershell
powershell -NoProfile -File tools/agent/New-CompileCommands.ps1   # -Clean to start over
```

It configures a separate Ninja + MSVC tree into `artifacts/clangd/juce-native`
(git-ignored and outside the submodule, so the submodule stays clean). The
tree is configured only and never built. It reuses the `FETCHCONTENT_SOURCE_DIR_*` paths and
`DOTCHARTER_*`/`JUCESHARP_*` options from the real build's
`JuceSharp/src/JuceSharp.Native/build/CMakeCache.txt`. The root `.clangd`
points clangd at that directory and mutes diagnostics in the generated IR
tables and in third-party code. Re-run the script after adding a native
source file or changing a CMake option. Measured 2026-09-19: 152 entries,
about 35 s to configure. `clangd --check=JuceSharp/src/JuceSharp.Native/src/dotcharter_audio.cpp`
reports no compile diagnostics; its ten `tweak: … FAIL` lines are clangd
testing its own refactorings, not errors.

**Never build that tree.** It would run the POST_BUILD step and overwrite
the deployed `juce_sharp.dll`.

## The working loop

Issues, branches, agents, gates, PRs, merge trains and safe file edits:
[workflow.md](workflow.md). Read it before running agents or merging.

Launching the app to measure or verify something: set `DOTCHARTER_DATA_ROOT` to a
fresh temp folder, every time, and never delete anything in the user's real
`%APPDATA%\DotCharter` or `%LOCALAPPDATA%\DotCharter` (root guide, workflow
gotchas; [services/infrastructure-update/storage.md](services/infrastructure-update/storage.md)).

## What happens automatically, and what does not

Automatic: model routing (the router's per-prompt hint plus the table in
`.claude/rules/tool-selection.md`), LSP navigation for C# and C/C++, the
Avalonia MCP for Avalonia questions, Context7 for other library APIs, the
scoped rules, Superpowers for substantial work, and `frontend-design` when a
screen is being designed.

Manual only: `/code-review`, `/ralph-loop`, `/feature-dev`, skill creation,
CLAUDE.md/AGENTS.md audits, and broad `/simplify` runs. The simplification
limits in `.claude/rules/tool-selection.md` protect realtime, DSP, interop,
ABI, generated, Vulkan-sync and SIMD code.
