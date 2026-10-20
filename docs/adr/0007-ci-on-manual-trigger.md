# 0007. CI runs only when started by hand

**Status:** Accepted

## Context

The full suite is heavy: a Windows build, about 13,000 default-gate cases, a
separate headless-UI run, and optionally the native suite. Hosted Windows
minutes bill at twice the rate, and a single pull-request run cost far more
than the free allowance covers.

## Decision

- `build.yml` has one trigger, `workflow_dispatch` (the "Run workflow" button,
  or `gh workflow run build.yml --ref <branch>`). A push, a pull request or a
  merge starts nothing.
- Merges are decided on the local gate and the local headless-UI run. A pull
  request records their numbers in its description.
- The headless-UI suite stays a separate job from the default gate, because
  Avalonia's headless dispatcher is process-global.
- A test fails any workflow with another trigger, so this cannot drift.

## Consequences

- Contributors run the gate themselves; `tools/agent/Invoke-Gate.ps1` wraps the
  build and the test runs.
- A missing CI result says nothing about a branch. Start the workflow when you
  want a hosted run.
- If the minutes budget changes, this record is the one to revisit.

## Links

`.github/workflows/build.yml`, `CiWorkflowLintTests`,
[CONTRIBUTING.md](../../CONTRIBUTING.md).
