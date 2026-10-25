# Agent workflow gotchas

Moved verbatim from the root `AGENTS.md` (which keeps a one-line pointer). Read before launching the app, starting agents or worktrees, or splitting a file.

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
