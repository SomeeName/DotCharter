# How work flows here (agents and the owner)

The owner manages DotCharter from GitHub's web UI and tests built packs by
hand. Everything below exists so that both see the same thing: an issue says
what is wrong with a picture of it, a PR shows what changed with proof that it
works, and the owner's own test is the LAST gate, not the first. The GitHub
rules themselves (labels, milestones, `gh issue develop`, `Fixes #n`) are in
the root [`AGENTS.md`](../../AGENTS.md) § GitHub workflow; this is the loop around them.

## Roles

- **One orchestrating session** owns integration: it reviews and merges,
  builds releases, and implements what is on the critical path itself.
- **At most three agents at a time**, each FRESH (never resume a finished
  agent with a huge context), each with ONE explicit scope (an issue or two that
  share files), in its OWN worktree (the `dotcharter-worktree` skill). An agent
  opens a PR and stops; it never merges.
- An agent prompt carries the rules it cannot know: the worktree path, never
  `git stash`, the gate wrapper, LF line endings, no local paths on GitHub,
  before/after images, and "do NOT merge".

## The loop

1. **Issue first**: what happens, the cause with file paths, the fix and the test.
   - A bug or visual issue carries a SCREENSHOT.
   - Upload images with `bash "$USERPROFILE/Desktop/dotcharter-screens/upload-issue-image.sh" <png> issues/<n>` (the owner's screenshot kit, outside the repo); it puts them on the orphan `issue-assets` branch and prints the markdown.
   - **Never paste a local path (`C:\Users\…`) into GitHub**: nobody else can open it.
2. **Branch and worktree** from `origin/main` (fetch first).
3. **Test that would fail without the change**, in the default gate where possible.
   - **Prove it**: break the fix, watch the test fail, restore.
   - **The break must COMPILE.** A break that fails the build leaves the tests running the OLD binaries, and "it passed" means nothing. Check that the build line says OK before reading the result.
4. **Gates**: `tools/agent/Invoke-Gate.ps1 -Gate` (4 shards), or `-Gate -Headless` after any view change (both suites at once when RAM allows). `-Gate` leaves out the slow **release lane** (`tools/ci/release-lane.json`): the train, the release preflight and CI run it for you; run a lane class yourself with `-FilterClass` when you change its subsystem.
   - While iterating, `Invoke-Gate.ps1 -Affected` runs only the classes that cover your diff (printed with the reason for each; `-SelectOnly` just prints). It is a loop aid: it records no green tree, falls back to the full gate on a change to `tools/`, a project or global config, and never replaces the full gate or the train.
   - A red class in a busy run is re-run ALONE before it is called a flake.
   - A test that flakes twice gets its own issue.
5. **PR**: what, how, evidence (gate and headless numbers, the non-vacuity proofs), known limits.
   - A visual change carries BEFORE and AFTER images, so the owner can judge it without launching the app.
6. **Merge** by the bar in the root `AGENTS.md`. Then delete the worktree and branch (verify the tip is in `origin/main` first).

## Merging several PRs: the train

Merged one by one, each merge makes the next branch stale: a rebase and
another ~8-minute gate per PR. **Batch every ready PR into ONE train** instead
of merging them one at a time:

```powershell
powershell -NoProfile -File tools/agent/Merge-Train.ps1 -Prs 142,143,144          # report
powershell -NoProfile -File tools/agent/Merge-Train.ps1 -Prs 142,143,144 -Merge   # and merge
```

It merges the PRs onto `origin/main` in order, runs the gate once (and the
headless suite, 4 shards, if any PR touches a view), then the release lane
(`Invoke-Gate.ps1 -ReleaseLane`, skipped only for a docs-only train), and with `-Merge` merges
them on GitHub in the SAME order, refusing a PR whose head moved since it was
tested. GitHub repeats exactly those merges, so the tested tree is the tree
that lands. A PR that moves the JuceSharp gitlink is refused: merge it on its
own, after its JuceSharp PR. A headless run whose EVERY failure carries a
harness-fault needle (`tools/ci/HeadlessShards.psm1`) is not a stop by itself:
`Invoke-Gate.ps1 -Headless` re-runs those classes alone, up to `-RaceReruns`
(2) times, and the train is green only if they pass; the verdict line and each
merged PR's comment say so. A failure without a needle stops the train.

A GREEN full-gate train records its tree id (with `+ headless` when the suite ran and
`+ release lane`) in the release cache (`tools/release/ReleaseCache.psm1`, see
`tools/release/README.md` § Release caches). The public export's `-Gate` and
`New-Release.ps1`'s headless preflight then skip that identical test run on the
same tree (the release lane's record is reused the same way); `-ForceGate` /
`-ForceHeadlessUi` / `-ForceReleaseLane` run it anyway.

What keeps it fast:

- **One reused worktree, `.claude/worktrees/train`.** Each train resets it to
  `origin/main` and keeps its ignored `bin/obj`, so the build is incremental
  (its builds run with `SuppressImplicitGitSourceLink=true`, as does every
  `Invoke-Gate.ps1` build, both through `tools/agent/DevBuild.psm1`: SourceLink
  stamps HEAD's commit id into every assembly, so each new commit would recompile
  all projects; a one-line change rebuilds in ~15 s instead of ~65 s, #153). It is rebuilt from scratch only when main's JuceSharp gitlink moved
  since it was created (a marker in the worktree's git directory) or with
  `-Fresh`. A lock in the main `.git` stops a second train from resetting it
  under a running one. Never use it for anything else, and never delete it
  while a train runs.
- **A XAML project keeps its reference assembly's timestamp.** Avalonia's XAML
  compile rewrites `obj/refint` and deletes `obj/ref` before the SDK's copy, so
  the copy always ran and a comment-only edit in `DotCharter.UI` recompiled
  `DotCharter.UI.Gallery` and `DotCharter.Tests`. Two targets in
  `Directory.Build.targets` restore `obj/ref`'s timestamp when the bytes are equal;
  `Infrastructure/RefAssemblyStabilityLintTests` pins them. A public-API change
  still rebuilds the dependents.
- **Its `juce_sharp.dll` is kept too.** When a FRESH train's worktree script
  warns that the DLL was not built for the gitlink, copy one that was into the
  train worktree's `JuceSharp/src/JuceSharp/runtimes/win-x64/native/`; it then
  serves every train until the gitlink moves.
- **Docs fast path.** When the train's combined change is only `*.md` and
  `docs/**` (a `.resx` is NOT docs), it runs the build plus the lints that read
  docs (`Get-MergeTrainDocsOnlyClasses` in `tools/agent/MergeTrain.psm1`, led by
  `DocReferenceLintTests`) instead of the full gate. A new test that reads a
  guide's wording joins that list. `-Headless` forces the full path.
- **Its own processes only.** It stops stray test hosts whose path lies inside
  the train worktree, never another agent's, and pins itself at
  below-normal priority on a 16-thread box (everything it starts inherits both):
  CPUs 8-15 by default (no game detection), ALL cores only with `-AllCores`.
  `Invoke-Gate.ps1` decides the same way, so a gate needs no `cmd /c start
  /affinity` wrapper; the train passes `-AllCores` on to its child gates, and
  the choice is the first line of the output. The decision is `DevBuild.psm1`'s `Get-DevCpuPlan`.

The decisions are pinned by `Infrastructure/MergeTrainModuleTests`.

## Editing files

- Use the file tools (Edit/Write). For many edits, or text with backslashes
  (regexes, Windows paths, C# escapes), write a JSON spec with the Write tool
  and run `python tools/agent/Replace-InFile.py <spec.json> [--root <dir>]`
  (`--self-test` proves it). It keeps each file's CRLF/LF and BOM, and writes
  NOTHING unless every edit matches exactly.
- **Never pass multi-line text with backslashes through a shell heredoc**: the
  backslashes were lost several times.
- **Never `sed -i` a resx or another CRLF file.** After any edit, compare
  `git diff --numstat` with `git diff --numstat --ignore-cr-at-eol`: a difference
  is line-ending churn.

## Native (JuceSharp)

- Work driven by an app feature or bug keeps its ONE issue in DotCharter. The
  JuceSharp PR references it with the full form, `Fixes SomeeName/DotCharter#n`
  or `Refs SomeeName/DotCharter#n`, in the BODY, so the issue's timeline shows it
  (a bare "DotCharter #n" in a title links nothing).
- Native-only work (an RT-gate regression, a flaky native test, ABI or toolchain
  cleanup, a wrong submodule doc) gets its issue in `SomeeName/JuceSharp`.
- JuceSharp PRs merge first, as merge commits; push the submodule before the parent.
- **A worktree's `juce_sharp.dll` must be built for ITS JuceSharp commit.** The
  same ABI number does not mean the same native code. The worktree script now
  takes a clean DLL built at the right commit from another worktree, or warns
  loudly when there is none.

## Releases

- `tools/release/New-Release.ps1`, from CLEAN `bin/Release` and `obj/Release`
  (an interrupted run can leave zero-filled binaries that the incremental build
  reuses). Then:
  - scan the staged payload for zero-filled `.dll`/`.exe`;
  - LAUNCH the staged `DotCharter.exe` and check that it stays alive and logs
    `StartupPhases: … r2r=yes`;
  - let the owner test that exact pack;
  - `gh release create`;
  - run the live updater test.
- Release notes are plain language for players and charters; never claim more
  than the code does.
