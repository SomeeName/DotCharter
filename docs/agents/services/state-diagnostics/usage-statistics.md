# `State/Checkpoints/` + `Diagnostics/` — snapshots and the diagnostics bundle (deep reference) — `Diagnostics/UsageStatistics`

## `Diagnostics/UsageStatistics`

- **`Diagnostics/UsageStatistics` counts what gets USED while charting (0.41) —
  aggregates, never an event log.** Counts plus first/last use answer every
  question asked of it ("often", "barely", "not since March"), stay a few KB
  forever and cannot reconstruct a session timeline. **The valuable half is the
  denominator**: `BuildReport` takes what EXISTS (`IAppDialogService
  .KnownDialogIds`, `ChartKeybindHandler.KnownKeybindLabels`, the command
  catalogue) so it can name what was NEVER used — which is the half that says
  what to move into a submenu, and the half a bare counter cannot give. Counted
  ids outside the catalogue are reported rather than dropped, so a RENAME does
  not read as "nobody used it" — and that section is also where every button
  the catalogue does not list lands (Play Mode's screens, a panel's own
  controls), because the click hook counts ALL of them. Four hooks, each the
  single choke point for its kind: `AppDialogService.OnDialogPropertyChanged`
  (the open EDGE only — `IsOpen` fires on close too),
  `ChartKeybindHandler.HandleKeyPress`, `AppCommand`'s wrapped execute, and
  `UI/Diagnostics/UsageInstrumentation`'s class handler over every `MenuItem`
  and `Button` click. Recording happens on the UI thread on the click, so it
  only bumps an int; the write is debounced and the app's terminate path
  flushes it (`App.TerminateNow` kills the process, so `Dispose` never runs).
  Nothing is uploaded.
- **The ids must be STABLE and READABLE, and two of the three kinds were
  neither until 2026-09-03.** Keybind labels were `action.Method.Name`, which
  for a lambda is `<RegisterAll>b__53` — the owner's real report read "110
  available, 3 used" over a list of those, and the number renumbers on every
  edit of the registrar. `IKeybindRegistry.Register` now takes an
  `Expression<Func<KeyChord>>` and labels the bind after the `KeybindConfig`
  property it reads (`PlayPause`); a fixed key MUST pass its verb label
  (`KeybindUsageLabelTests` lints the registrar), and a compiler-generated id
  is refused on record and dropped on load. Commands had no denominator at
  all — the report listed every clicked command as "no longer offered" and
  none as never used — and the palette counted a `[RelayCommand]` under its
  catalogue id (`file.saveProject`) while the click hook counted the same
  command under its method name (`SaveProject`). `AppCommand.UsageId` is now
  the method name for a `FromCommand` entry, resolved through the same
  `CommandIdResolver` the hook uses, and the report's command denominator is
  the catalogue's `UsageId`s (`CommandUsageBucketTests`). Stored counts from
  before the fix are unreadable for keybinds and split for commands; a cut
  decision needs data recorded AFTER it.
