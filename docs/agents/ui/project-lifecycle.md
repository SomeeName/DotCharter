# Project lifecycle — open / replace / dirty / audio / shutdown (UI deep reference)

One file per subject under `project-lifecycle/`. Read the one you need:

**The code these describe left the UI in wave 0 (2026-09-19):** `ProjectSessionService`
is `DotCharter.Services/Session/`, `CrashRecoveryService` is
`DotCharter.Services/Infrastructure/Recovery/`, `ChartMaintenanceService` is
`DotCharter.Services/Editing/Maintenance/`, and `AutoBackupGate`,
`HistoryStepGuard` and the editing coordinators are `DotCharter.Services/Editing/`.
The session reaches the UI thread only through `IUiDispatcher` (a REQUIRED
constructor argument; tests pass `Tests/Infrastructure/TestUiDispatcher`), the
editor and transport through `IChartEditorSurface` / `IEditorTransport`, and its
failed-save and replace-audio prompts through `IMessagePrompt`. Their tests
moved to `DotCharter.Tests/Services/Session/`, `Services/Editing/` and
`Services/Infrastructure/Recovery/`.

- [traversals-and-forgetting.md](project-lifecycle/traversals-and-forgetting.md) — the three exhaustive chart traversals; a project switch must forget the project it closed
- [closing-and-dirty-state.md](project-lifecycle/closing-and-dirty-state.md) — closing, replacing and dirty state
- [auto-backup-and-recent-menu.md](project-lifecycle/auto-backup-and-recent-menu.md) — the auto-backup is dirty-gated; recent-project menu refresh
