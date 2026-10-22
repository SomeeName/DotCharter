# Tool selection, model routing and workflows (Claude Code only)

Repo knowledge lives in the `AGENTS.md` tree (every `CLAUDE.md` is a one-line
import of its sibling `AGENTS.md`; keep it that way — other agents read the same
files). This folder, `.claude/rules/`, holds only Claude-Code-specific rules;
the path-scoped ones load when you touch matching files.

## Navigation: semantic first, text second

- **C#** — use the `LSP` tool (csharp-lsp plugin → `csharp-ls`, Roslyn) for
  definitions, references, implementations, call hierarchy, hover types and
  workspace symbols. Grep is for strings, XAML, resx keys, comments and
  file names — not the primary way to answer "who calls X" in C#. The Rider
  MCP server (user scope) is an equivalent when Rider is running; do not
  query both for the same question. After a large cross-file change, ask LSP
  for diagnostics on the touched files before running the gate.
- **C/C++** — use the `LSP` tool (clangd-lsp plugin → clangd) for
  definitions, references, types, callers/callees and diagnostics. The
  compilation database is `artifacts/clangd/juce-native`, produced by
  `tools/agent/New-CompileCommands.ps1` (see the root `.clangd`). If clangd
  reports missing headers or a file has no compile command, re-run that
  script instead of guessing flags.
- **Avalonia** — the `avalonia-docs` MCP server (`.mcp.json`) for any
  Avalonia API, XAML, styling, binding or control-template question. Prefer
  it over Context7 and over memory; Avalonia 12 changed a lot.
- **Other external libraries** (Silk.NET, JUCE, CommunityToolkit.Mvvm,
  ONNX Runtime, xUnit/MTP, Rubber Band, …) — Context7 before relying on
  memory for an API shape.
- The big-file table in `AGENTS.md` still applies: never Read a resx, an IR
  table or a large `.axaml` whole.

## Subagents (owner rule, 2026-10-21)

**Subagents implement; the main session orchestrates.** Every issue goes to
an agent in its own worktree (the `dotcharter-worktree` skill) that writes the
code and tests and opens the PR. The main session files issues, writes short
agent prompts, checks the evidence, runs the merge train and merges. It writes
product code itself only when the owner asks for that in so many words — not
for "small" fixes, not while waiting for an agent slot (queue the issue
instead). State the model in every Agent call.

## Usage budget (owner rule, 2026-10-15)

- **One fresh session per issue or per train** (`/clear`). Never run an
  all-day session; memory and the issue carry the context.
- **Sonnet is the default implementer, Opus only for genuinely hard work**
  (novel algorithms, parity ports, audio/ABI/Vulkan), Haiku for mechanical
  edits. Up to THREE implementer agents at once (owner, 2026-10-21): agents and merge trains share CPUs 8-15, so more than three makes timing-sensitive tests flaky and measurements noisy. Short prompts.
- **No heartbeat crons or wakeups** unless the owner is away AND something is
  actually running.
- **Never resume a large finished agent.** Start a fresh one, or hand the
  small rest to a new one.
- **Agents run targeted tests while iterating;** the full gate and headless
  run once, on the merge train.

## Methodology

- **Substantial work** (new feature, multi-file change, anything touching
  the audio callback, the C ABI or Vulkan sync): Superpowers — brainstorm or
  plan first, TDD where a test can express the behaviour, and
  `superpowers:systematic-debugging` (root cause before patch) for any
  non-trivial bug. The repo's own "every change ships with its own
  evidence" rule in `AGENTS.md` is the bar for done.
- **Trivial edits** (typo, one-liner, doc tweak, config value): just do them.
  No brainstorming, no plan documents.
- UI work: apply `frontend-design` principles when a screen's look is being
  designed or reshaped; the UI rules in `DotCharter.UI/AGENTS.md` win.

## Manual only — never start these on your own

`/code-review`, `/ralph-loop` (never start a Ralph loop unprompted, and never
create `.claude/ralph-loop.local.md`), `/feature-dev` and its agents
(`code-explorer`, `code-architect`, `code-reviewer` — an alternative to
Superpowers, never stacked on it), `skill-creator`, CLAUDE.md/AGENTS.md
audits (`claude-md-management`), and broad `/simplify` runs.

## Simplification limits

`/simplify` (and any "clean this up" pass) may simplify ordinary
application-layer code. It must **not** touch these without an explicit
request naming them: realtime audio and anything reachable from the audio
callback, DSP cores, lock-free structures, `unsafe` C#, P/Invoke /
`LibraryImport` declarations and the C ABI (`dotcharter_audio.h`,
`dotcharter_audio.cpp`, `AbiArgs.h`, `JuceSharp/src/JuceSharp/Interop/`),
generated code (IR tables, `.spv`, source-generated bindings), Vulkan
synchronization/lifetime code, and SIMD/perf-critical loops. Verbose code
there is usually verbose on purpose.
