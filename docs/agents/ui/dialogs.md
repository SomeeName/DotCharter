# Dialogs (`Views/Dialogs/`) — UI deep reference

One file per subject under `dialogs/`. Read the one you need:

- [grammar.md](dialogs/grammar.md) — the dialog grammar
- [onboarding-and-shortcuts.md](dialogs/onboarding-and-shortcuts.md) — the first-run onboarding wizard; Help > Keyboard Shortcuts
- [name-picker-and-arrangement-dialog.md](dialogs/name-picker-and-arrangement-dialog.md) — a name field is a picker; the arrangement dialog
- [long-running-and-import-dialogs.md](dialogs/long-running-and-import-dialogs.md) — what a long-running dialog owes the user; the four import dialects
- [save-through-undo.md](dialogs/save-through-undo.md) — save goes through undo
- [hosted-dialogs.md](dialogs/hosted-dialogs.md) — the dialogs on `IDialogs` + `DialogHost` (UI rewrite wave 2):
  which moved, the per-dialog recipe, what the host does for them, and why the message dialog has not moved

**One rule that is not in any of them, because it bites card-shaped surfaces
outside `Views/Dialogs/` too:** a row of buttons inside a `MaxWidth`-capped card
must WRAP or SHARE width — never a horizontal `StackPanel`, which arranges from
x=0, clips nothing and therefore DRAWS its last button past the card's border
while reporting a perfectly correct rectangle. The editor's launch card shipped
that and overflowed by 36 px in de-DE and 45 px in ja-JP while measuring clean in
English. Use a `WrapPanel` (`ItemSpacing`/`LineSpacing` — it has no `Spacing`)
and size the cap from the LONGEST shipped language. The containment test is
`Perf/EditorLaunchCardContainmentUiPerfSmokeTests` (three screen sizes × en-US /
de-DE / ja-JP, every button's arranged bounds asserted inside the card); full
account in [layout-traps.md](layout-traps.md) § Columns.
