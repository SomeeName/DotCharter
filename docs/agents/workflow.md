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
   - Upload images through the GitHub contents API to the orphan `issue-assets` branch: `gh api -X PUT repos/SomeeName/DotCharter/contents/<dir>/<name>.png -f message="asset" -f branch=issue-assets -f content="$(base64 -w0 <png>)"`, with `<dir>` = `issues/<n>` for an issue or `prs/<n>` for a PR. Embed the returned `content.download_url` (or `https://github.com/SomeeName/DotCharter/blob/issue-assets/<dir>/<name>.png?raw=true`) as markdown. The old `Desktop/dotcharter-screens/upload-issue-image.sh` kit no longer exists.
   - **Never paste a local path (`C:\Users\…`) into GitHub**: nobody else can open it.
2. **Branch and worktree** from `origin/main` (fetch first).
3. **Test that would fail without the change**, in the default gate where possible.
   - **Prove it**: break the fix, watch the test fail, restore.
   - **The break must COMPILE.** A break that fails the build leaves the tests running the OLD binaries, and "it passed" means nothing. Check that the build line says OK before reading the result.
4. **Gates**: `tools/agent/Invoke-Gate.ps1 -Gate` (4 shards), or `-Gate -Headless` after any view change (both suites at once when RAM allows). `-Gate` leaves out the slow **release lane** (`tools/ci/release-lane.json`): the train, the release preflight and CI run it for you; run a lane class yourself with `-FilterClass` when you change its subsystem. **`-Headless` always runs the WHOLE headless suite, even with `-FilterClass`** (the filter then runs as a second pass); to run one headless class, use `-NoBuild -FilterClass "*<Class>"` WITHOUT `-Headless`. Every pattern needs its leading `*`.
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

It merges the PRs onto `origin/main` in order, builds RELEASE (as CI) and runs
the gate, the release lane and, if any PR touches a view, the headless suite in
ONE `Invoke-Gate.ps1 -Gate -ReleaseLane [-Headless]` call, side by side out of
one testhost budget (#325; the release lane is skipped only for a docs-only
train). With `-Merge`, one plain, non-forced `git push origin <tested-tip>:refs/heads/main`
lands the exact gated commits for every PR together (`Invoke-MergeTrainMerges`
in `tools/agent/MergeTrain.psm1`). A divergent main advance rejects the push,
leaves every PR unmerged, and posts no success comments. A PR head move found
before push also refuses landing. A head move after that check cannot alter
the immutable tip; an unconfirmed PR stops comments and requires inspection.
The train polls GitHub for up to 60 seconds for every PR to report MERGED with a
merge commit before posting any tested comment; expiry leaves the tested tip on
the base and calls for inspection without rerunning the train. Local merge
commits use GitHub's `Merge pull request #n from owner/branch` message plus the
PR title, with repository `user.name` and `user.email`; the email must end in
`@users.noreply.github.com`. Author/committer environment overrides are replaced
with that identity during commit creation and restored afterwards. One exclusive lock handle keeps a second train out of
the worktree for the whole run. A PR that moves the JuceSharp gitlink is refused: merge it on its
own, after its JuceSharp PR. A headless run whose EVERY failure carries a
harness-fault needle (`tools/ci/HeadlessShards.psm1`) is not a stop by itself:
`Invoke-Gate.ps1 -Headless` re-runs those classes alone, up to `-RaceReruns`
(2) times, and the train is green only if they pass; the verdict line and each
merged PR's comment say so. A failure without a needle stops the train. Every shard and retry is also
graded by `Get-HeadlessIncompleteClasses` / `Test-HeadlessExitAccepted`; an
unexplained exit blocks local race recovery even if all classes reported results.

A RED train records the lanes it actually executed and passed against its tree. Re-running the
same train on the same main (same tree), in the same configuration and with
the same release-lane list hash, runs only the lanes without a green record, so the red lane runs again in full and alone (`Get-MergeTrainLanePlan`);
`-ForceGates` runs every lane. Planned lanes revoke their previous evidence
before build/execution, including forced runs; only accepted greens are saved.
Readers and writers use the same configuration and release-lane input hash.
Release and export gates use Release, matching the train. Public/dev tree
aliases are revoked together, so a failed run cannot reuse an older alias.
All record reads and read/modify/write updates use one exclusive cross-process
store lock, with a 10-second contention deadline and release in finally.
A record is the verdict of the last COMPLETED run of that lane for that tree,
configuration and lane inputs. A run killed or crashed before grading has no
verdict: it cannot record green and does not erase another completed verdict.
A completed red run erases the record.
Evidence is checked immediately before each ref update, after all network preflight.
A local file and a remote ref cannot close the remaining gap between that check
and the server accepting the push: a verdict completed in that gap is no different
from one completed just after landing, and does not affect a landing already made.

An attempt writes only the accepted lanes it actually executed; reused lanes
are never re-saved. Reused records are checked again under the store lock on
their original tree and inputs before accepting the result, and before landing
or declaring assets publishable. Lost evidence names its lane and stops the
train, release or export; the next run executes that lane. The train checks both
after its gates, then after the base/head network checks directly before pushing.
The public export completes all push-plan preflight, then checks immediately
before each push (including later repos). New-Release checks after its gates
and after local metadata/asset preparation, before printing the publish command;
it never executes that command or updates a remote ref.

Full lanes are derived once for both execution and revocation. Adding a trailing
`-FilterClass` still runs (and revokes) explicitly requested full lanes before
the filtered pass. Only filtered/affected-only selections are exempt; an affected
fallback runs and revokes the full default gate. `-ParseTrx` runs nothing.
A non-default `-Project` has no configured class weights, so its default gate
uses one unsharded run with its own assembly; foreign class includes and weight
updates are never applied. An unexpected empty run (exit 8) remains red.

A GREEN full-gate train records its executed lanes against the tree id (with `+ headless` when the suite ran and
`+ release lane`) in the release cache (`tools/release/ReleaseCache.psm1`, see
`tools/release/README.md` § Release caches). The public export's `-Gate` and
`New-Release.ps1`'s gates (default gate, headless suite, release lane) then skip
that identical test run on the same tree; `-ForceGate` / `-ForceHeadlessUi` /
`-ForceReleaseLane` run it anyway. **Before a release, run the last train with
`-Headless`** so the release reuses the headless suite too and runs only the
localized layout sweeps (`tools/release/README.md` § Fast release recipe).

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
- **The native pair is checked on every train.** After the PRs are merged
  locally, both files' SHA-256 must match the tested tree's
  `tools/setup/native-runtime.json` entry for its JuceSharp gitlink. A marker,
  matching ABI or cached green lane is insufficient. A mismatch invokes that
  tree's `tools/setup/Get-NativeRuntime.ps1 -Force` once (including support for
  `DOTCHARTER_NATIVE_ASSET_DIR`), then rereads both hashes. A missing pin,
  installation failure or remaining mismatch stops before any gate or landing.
  This applies to fresh trains after gitlink moves and reused worktrees alike.
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
  the choice is the first line of the output and the last (`cpus half|all`).
  **A train when the owner is not gaming:** add `-AllCores`, nothing else (it
  widens even a run started under `/affinity FF00`, and doubles `-Shards`):
  `powershell -NoProfile -File tools/agent/Merge-Train.ps1 -Prs 142,143 -AllCores`. The decision is `DevBuild.psm1`'s `Get-DevCpuPlan`.

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
  same ABI number does not mean the same native code. The worktree script
  takes a complete clean pair built at the right commit from another worktree,
  or installs the pair pinned for the gitlink with its own
  `tools/setup/Get-NativeRuntime.ps1 -Force` and verifies both installed hashes.
  A missing pin or failed install warns with its reason and reports `warn`
  in the final `native-pair: <JSON>` line; native can then be built locally.
  The train requires the pinned hashes and stops if they cannot be installed.

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


## GitHub workflow: issues, branches, pull requests (full text, moved from the root AGENTS.md)

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
   - **Push once, when the work is verified.** On the private dev repo no
     push starts CI (see Validation); the PR body carries the local gate and
     headless numbers.
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
6. **Public → dev sync (#77).** A commit merged on a PUBLIC repo is imported
   into dev before the next release: `tools/release/Import-PublicCommit.ps1
   -Repo DotCharter|JuceSharp -Commit <sha> | -Pr <n>` makes the dev branch and
   PR with a `Public-Commit: <Repo>@<sha>` trailer. `Export-PublicSnapshot.ps1`
   REFUSES while a public commit since the last snapshot has no such trailer and
   prints the import command (a public MERGE commit counts too when it changed
   something by hand, e.g. a conflict resolution: `PublicSync.psm1`
   `Get-PublicMergeOwnBase`; the import applies only that part after the merged
   commits). Each diff retains an unchanged dev-only suffix as context on both sides
   when the public base is an exact complete-line prefix of the staged dev file;
   a public EOF edit then preserves the suffix. Other differences still use normal
   three-way conflict detection. The gitlink is pinned to that diff's own base, and
   `New-Release.ps1`'s preflight runs the same check
   (`Find-UnimportedPublicCommits.ps1 -FailOnMissing`). The scheduled
   `public-sync-watch.yml` does NOT run: Actions is off on the dev repos. To skip one
   on purpose, commit an empty dev commit with the trailer saying why. Never edit
   the guard to get past it.
