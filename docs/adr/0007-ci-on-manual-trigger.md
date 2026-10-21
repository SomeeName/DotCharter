# 0007. CI runs unattended only on the public repo

**Status:** Accepted (revised when the repos went public: the private dev repo
stays manual-only, the public repo runs on push and pull request)

## Context

The full suite is heavy: a Windows build, about 13,000 default-gate cases, a
separate headless-UI run, and optionally the native suite. On a private repo
hosted Windows minutes bill at twice the rate, and a single pull-request run
cost far more than the free allowance covers. Public repos get Actions minutes
free. Development happens in a private dev repo; the public repo is fed by
snapshots of it, and both carry the same `build.yml`.

## Decision

- `build.yml` triggers on `workflow_dispatch` (the "Run workflow" button, or
  `gh workflow run build.yml --ref <branch>`), push to `main` and
  `pull_request`.
- Every job carries
  `if: github.event_name == 'workflow_dispatch' || !github.event.repository.private`
  (or ANDs it onto its own condition). On the public repo a push or a pull
  request runs everything; on the private dev repo it creates a run whose jobs
  are all skipped, which bills nothing.
- Merges in the dev repo are decided on the local gate and the local headless-UI
  run. A pull request records their numbers in its description.
- The submodule token (`SUBMODULES_TOKEN`) is optional: with it the private dev
  JuceSharp is fetched; without it (the public repo, a fork's pull request) the
  public JuceSharp is cloned anonymously. It is passed to the one `git submodule
  update` that needs it (`git -c`), never written to a git config, so no later
  step can read it; the native-runtime fetch gets it only on the private repo.
- The headless-UI suite stays a separate job from the default gate, because
  Avalonia's headless dispatcher is process-global.
- A test fails any workflow that has a trigger other than `workflow_dispatch`
  and a job without that gate, so this cannot drift.

## Consequences

- Contributors run the gate themselves; `tools/agent/Invoke-Gate.ps1` wraps the
  build and the test runs.
- In the dev repo a missing CI result says nothing about a branch. Start the
  workflow when you want a hosted run.
- If the minutes budget changes, this record is the one to revisit.

## Links

`.github/workflows/build.yml`, `CiWorkflowLintTests`,
[CONTRIBUTING.md](../../CONTRIBUTING.md).
