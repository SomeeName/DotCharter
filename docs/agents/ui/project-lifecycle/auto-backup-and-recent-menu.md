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
— configured retention now prunes only this project's recognized backup names
after a successful write, never counting an incomplete stage. `Services/Editing/AutoBackupGate.Decide` is the pure
verdict now: in-flight → skip; disabled OR CLEAN → reset the schedule (so the
interval restarts at the first edit, not at launch); dirty and due → run. It
is the gate `CrashRecoveryService.RunTickAsync` has had since 0.49, one timer
over. `Directory.CreateDirectory` and the unique-name `File.Exists` loop moved
inside the `Task.Run` beside the serialize + zip — the project folder is
routinely a network share. `Services/Editing/AutoBackupGateTests` pins the
verdicts and lints the wiring in `ProjectAutoBackup.cs` (the tick reads `_dirtyTracker.IsDirty` through
the gate; no filesystem call before the worker starts), and
`Services/Session/ProjectAutoBackupTests` drives the writer and the real tick:
a cloned snapshot zipped into `backups/`, the temp file removed, and unchanged
dirty content retried on the next due tick after a failed write. The snapshot's
change stamp is captured before writing but committed to the gate only after
the archive is promoted, and only if the same project is still active. Edits
made during writing remain owed a backup; a project switch forgets the old
stamp even when the old write finishes later.

Backup and recovery clone whatever derived volume fields are currently live;
neither starts nor awaits decoding. Completed attach/export/companion measurements
may have adopted automatic values without changing the dirty/history stamp. The
next actual write persists them; pending work leaves the captured old fields alone.
Manual values stay untouched and no decoder runs inside the recovery store lock.

The test tick dispatcher pumps until the backup actually completes, with a real
deadlock deadline; it never assumes a fixed number of asynchronous stages.
Volume fields are captured at the document writer's entry, so a late mutation
of the recorded snapshot cannot make the persistence assertion pass.

## Recent-project menu refresh

`ProjectMenuViewModel.RefreshRecentProjects` (`MainWindowViewModel.Project`) publishes cached rows immediately and
checks file existence on a worker. Each refresh advances a generation; its posted
result applies only while that generation is current. This prevents an older disk
probe from replacing newer rows. `UiThreadIoDeferralTests` pins the worker, UI
publication, and generation check; row equality includes the display name.
