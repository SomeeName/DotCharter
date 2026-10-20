# Project lifecycle — open / replace / dirty / audio / shutdown (UI deep reference) — the auto-backup is dirty-gated; recent-project menu refresh

## The auto-backup is dirty-gated and probes the disk on the worker

The timer, the gate and the zip writer are `Services/Session/ProjectAutoBackup`,
which `ProjectSessionService` builds in its constructor, starts in `Initialize`,
resets on every project switch (`ResetSchedule`) and disposes with the session.
Its tick once asked only "enabled? due? not already running?" — never whether
anything had CHANGED. With the defaults
(on, every five minutes) an untouched project paid a whole-project
`AssembleProject` + `ChartProjectCloner.Clone` on the UI thread every interval
(the one heavy UI-thread step of a save, here unprompted, with no status
pill) and then wrote one more identical zip into `<project>/backups/`, forever
— there is still no retention rule there, deliberately: deleting a user's
backups is the owner's call. `Services/Editing/AutoBackupGate.Decide` is the pure
verdict now: in-flight → skip; disabled OR CLEAN → reset the schedule (so the
interval restarts at the first edit, not at launch); dirty and due → run. It
is the gate `CrashRecoveryService.RunTickAsync` has had since 0.49, one timer
over. `Directory.CreateDirectory` and the unique-name `File.Exists` loop moved
inside the `Task.Run` beside the serialize + zip — the project folder is
routinely a network share. `Services/Editing/AutoBackupGateTests` pins the
verdicts and lints the wiring in `ProjectAutoBackup.cs` (the tick reads `_dirtyTracker.IsDirty` through
the gate; no filesystem call before the worker starts), and
`Services/Session/ProjectAutoBackupTests` drives the writer: a cloned snapshot
zipped into `backups/`, the temp file removed, a failed write that does not
wedge the next one.

## Recent-project menu refresh

`ProjectMenuViewModel.RefreshRecentProjects` (`MainWindowViewModel.Project`) publishes cached rows immediately and
checks file existence on a worker. Each refresh advances a generation; its posted
result applies only while that generation is current. This prevents an older disk
probe from replacing newer rows. `UiThreadIoDeferralTests` pins the worker, UI
publication, and generation check; row equality includes the display name.
