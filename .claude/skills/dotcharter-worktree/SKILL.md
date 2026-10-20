---
name: dotcharter-worktree
description: Create, hand off, merge back and clean up an isolated DotCharter git worktree for a subagent or a parallel task, including the JuceSharp submodule copy that makes it buildable and the CRLF check after merging. Use this whenever you are about to dispatch an agent that will edit files in DotCharter, start work that should not touch the main checkout, or merge/remove an agent branch or worktree — even if the user only says "run this in the background", "do these in parallel" or "spin up an agent for it".
---

# DotCharter agent worktrees

A plain `git worktree add` (or the Agent tool's `isolation: "worktree"`)
gives a tree that **does not build**: the `JuceSharp` submodule is empty,
and the agent sandbox refuses the `-c protocol.file.allow` that
`git submodule update` would need. The repo's answer is to copy the main
checkout's `JuceSharp/` as plain sources. The copy brings the prebuilt
`juce_sharp.dll` along, so `DotCharter.sln` builds without any submodule step.
The script below does that and checks the result.

## Create

Run from the DotCharter root. If a `cd` earlier in the session left the shell
inside `JuceSharp/`, `cd` back first: a worktree cut from there is a worktree
of the submodule, with no solution and no tests.

```powershell
powershell -NoProfile -File .claude/skills/dotcharter-worktree/scripts/New-AgentWorktree.ps1 -Name <short-name>
# optional: -Branch feat/<x>   -Base <commit>   (default base is main, not the session HEAD)
```

It prints the worktree path, the branch and whether `juce_sharp.dll` is
present, and fails loudly if the tree would not build. **The copy is checked
against the base commit's JuceSharp gitlink:** when the main checkout's
submodule sits at another commit (it lags after every gitlink bump the owner
has not pulled), the script fetches the gitlink's commit into the main
checkout's submodule repo (objects only, the owner's files are untouched) and
puts exactly that tree in place, keeping the prebuilt DLL. It then reads the
DLL's own `dca_abi_version()` and **warns when it does not match the headers'
`DCA_ABI_VERSION`**: rebuild native in the worktree, or copy a DLL built at that
JuceSharp commit, before running native tests or the app. The default base is
`main` on purpose: a worktree cut from a stale session HEAD looks clean but is
several merges behind, and merging it later can silently undo newer work.

## Hand off to the agent

Put these in the agent's prompt, because it has none of this session's
context:

- the **absolute** worktree path, and "work only inside it, never in the
  main checkout"
- **never `git stash`**: the stash is shared by every worktree, and two
  agents have already swapped each other's half-finished work through it
- the gate is `tools/agent/Invoke-Gate.ps1`, run from the worktree (raw
  `dotnet build/test` is blocked by a hook)
- write files with **LF** line endings (see below)
- the submodule copy is plain sources: JuceSharp commits cannot be made from
  this worktree. Native changes that must land in the submodule go back to
  the main checkout
- the model: state it in the Agent call (sonnet or haiku for read-only
  agents, sonnet or opus for agents that write files), with at most two
  agents running at once

A worktree's test count differs from the main checkout's (`LocalOnly_*`
probes, `TestData/` resolution). Compare counts only within the same tree.

## Merge back

1. Check the branch contains current main before merging:
   `git merge-base --is-ancestor main <branch>`. If it does not, send the
   branch back to be rebased. The agent that wrote it knows which side of each
   hunk matters; hand-resolving conflicts has already demoted a lint once.
2. Merge, then check the touched text files for CRLF. The working copy is
   CRLF on disk and the index stores LF, so a tool that preserves CRLF turns
   into whole-file diffs:
   ```bash
   git diff --name-only HEAD~1 | while read f; do [ -f "$f" ] && c=$(git show HEAD:"$f" | grep -c $'\r'); [ "${c:-0}" -gt 0 ] && echo "CRLF in index: $f"; done
   ```
   If a merge conflicts on every line, normalise base, ours and theirs to LF
   before `git merge-file`.

## Clean up

`git worktree remove` refuses a worktree that contains a submodule folder,
and deep paths can exceed git's delete limit. So:

1. Confirm the branch is merged: `git branch --merged main | grep <branch>`.
2. `Remove-Item -Recurse -Force .claude/worktrees/<name>`. If that fails on
   long paths, run `cmd /c rd /s /q` on the `\\?\`-prefixed absolute path.
3. `git worktree prune`, then `git branch -D <branch>`.

Stage explicit paths when committing; never `git add -A`. `.claude/worktrees/`
is git-ignored, but embedded repos are exactly what `-A` sweeps in.
