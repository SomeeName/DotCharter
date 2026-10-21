# `Infrastructure/` + `Update/` — config, backup, crash reports, library doctor, auto-update (deep reference) — a restore is atomic per file and per set

## A restore is atomic per FILE and per SET

`ConfigBackupService.PromoteStagedFiles` swapped each staged file in with
`AtomicFile.ReplaceWithRetry`, but a failure on file 3 of 6 left three imported
files beside three of the user's own — a config root that never existed on
either side of the restore — and `Import`'s `finally` then deleted the staging
tree, so the rest of the archive went with it. It now keeps the previous bytes of
every destination it is about to overwrite (in a SIBLING of staging, never
inside it — the caller wipes that) and restores them newest-first on any failure.
A rollback that itself fails rides out as an `AggregateException` beside the
original: a partial rollback must never be reported as a clean failure. Pinned by
`ConfigBackupServiceTests.Import_ThatFailsPartWayThroughThePromote_LeavesTheConfigRootExactlyAsItWas`.

## A backup is a file somebody else may have written

A restore takes DATA, never anything the app would run, load or open. Three guards,
each pinned by `ConfigBackupImportSecurityTests`, `RecoveredProjectPathTests` and
`TransportVstScanGateTests`:

- **`appconfig.json` goes through `AppConfigBackupFilter`**, an allowlist with a
  `BackupSettingKind` per key: `Value` (taken), `DataFolder` (taken only when it is a
  local, rooted, non-UNC, non-device directory that EXISTS here), `ExistingProjectFiles`
  (`RecentProjects`, entries kept only when they are local `.rscp` files that exist) and
  `Never` (VST chain/paths, `FfmpegPath`/`YtDlpPath`, `LastProjectPath`, `LogFilePath`,
  consents, and `Ai.Kind`/`Ai.BaseUrl` — the AI address receives the stored key and
  doubles as the Claude Code path). An unlisted key is `Never`. This machine's value is
  kept for everything not taken, and `ConfigBackupResult.NotRestoredSettings` lists what the
  backup carried and lost; the Files tab prints it. A NEW `AppConfig` property fails
  `EveryAppConfigSetting_HasExactlyOneCategory` until it is classified.
- **`ConfigBackupRules.ShouldTransfer` is the one list of files that travel**: per tree,
  the types the app writes there (`profiles/` json + wav + images, `languages/` json,
  `themes/` json + images, `phrases/` rscp, `song-tones/` json), nothing under a bundle
  directory, and **`recovery/` not at all** (it stays in `UserDataTopDirs`, so an update
  still never overwrites it). Export uses it too.
- **A recovery sidecar's `OriginalProjectPath` is adopted only through
  `RecoveredProjectPath.IsAdoptable`** (plain local `.rscp`, outside the OS and program
  folders); otherwise the session is recovered untitled. The recovery dialog prints the
  adopted path.
- **The startup VST restore loads only chain entries the scan reported**
  (`TransportViewModel.ScanAndRestoreVstSelections`); the rest stay as slots with
  `IsUnavailable` + a printed reason and are filtered out of every engine push.

**`SettingsViewModel.ImportBackupAsync` drops the caches in a `finally`, not the
`try`.** An import that threw part-way still REPLACED files on disk, and that is
exactly when the invalidations were being skipped — leaving every service holding
pre-import state over post-import files, which is when writing the stale instance
back does the most damage.

---

- `Infrastructure/` — `Configuration/` (`AppConfig`, `JsonConfigurationService`),
  `Storage/WritablePaths` (a config at the install's top — `InstallLocation.InstallRoot`, the launcher's folder on a split install, see [split-install.md](split-install.md) — wins over %APPDATA%), `Recovery/`
  (crash snapshots), `CrashReporting/` (opt-in local crash bundles:
  `AppConfig.CrashReportOptIn`, default OFF; on an unhandled exception
  `CrashReportService.TryCapture` writes a redacted `.txt` bundle
  — exception + stack + recent log tail + minimal system info — under
  `<ConfigRoot>/crash-reports/`, never uploaded, pruned to the newest 20.
  Assembly is the PURE, unit-tested `CrashReportBuilder`; redaction is
  `Diagnostics/SensitiveTextRedactor` (strips OS user name incl. inside paths,
  Windows/Unix home segments, JWTs, token prefixes, Bearer + credential-ish
  key=value pairs, long hex runs, and the Rocksmith keys `PsarcToc` / `SngPc` /
  `SngMac` of `appconfig.json` by NAME to the end of the value, spaces included —
  #774, `RocksmithKeyRedactionTests`); the service is the thin impure edge.
  **That redactor is the ONE in the app** — it was `CrashReportRedactor` until
  2026-09-04, when the diagnostics bundle's separate, drifted second copy was
  folded into it (`SharedRedactorLintTests` fails the gate on a third). **`client_id` and `anon_key` are on the key list (0.45)** — they
  are identifiers rather than secrets, which is exactly why both redactors
  missed them, but all three OAuth flows (GitHub device flow, TONE3000)
  log a client id beside the secret and `AppConfig` stored three of them
  (`GitHubOAuthClientId` left it in #49; older config files still carry the key).
  (`anon_key` was a fourth `AppConfig` field until v0.60 removed the online
  backend; both key lists keep the name, because a bundle or a crash report is
  collected from a config file an OLDER build wrote.) They sit on the one
  `SensitiveTextRedactor.SensitiveKeyFragments` list, which matters most for the
  diagnostics bundle: it carries `appconfig.json` VERBATIM, and
  `"clientid"`/`"anonkey"` normalize to no fragment that was on the old list
  (`"apikey"` is not `"anonkey"`; only `"clientsecret"` was there). **The key
  match is by CONTAINS, not by word boundary** — a `\b`-style match cannot see
  the `ClientId` inside `Tone3000ClientId`, because the preceding character is a
  digit. Pinned by
  `CrashReportServiceTests.TryCapture_RealisticBundle_CarriesNoSecretAndNoUserPath`
  and `DiagnosticsBundleExporterTests.Export_RedactsEveryCredentialIshAppConfigField`,
  the latter built from a SERIALIZED real `AppConfig` so a renamed property
  cannot make it vacuous. **A new credential-ish `AppConfig` property needs a
  fragment on that list, or it ships in the clear.**
  The bundled log tail goes through `Diagnostics/LogTailReader` like every other
  log read (0.45) — `CrashReportService.ReadLogTail` was a fourth hand-rolled
  copy of the seek-then-drop-the-partial-line dance and had drifted from the
  other three. Wired from `App.axaml.cs`'s existing AppDomain/TaskScheduler
  handlers AFTER `logger.LogUnhandledException`), `Documents/`,
  `Psarc/` (low-level archive read/write),
  `Backup/` (whole-install backup & migrate: `ConfigBackupService` zips the
  user data under `WritablePaths.ConfigRoot` into one schema-versioned archive
  and restores it; `ConfigBackupRules` is a pure ALLOWLIST — never a denylist,
  because on a portable install ConfigRoot IS the install dir — that includes
  settings/keybinds/profiles/practice/languages/recovery/themes
  and always refuses credential stores (DPAPI token files) and caches, on
  import too (traversal-safe entry sanitization, newer-schema-major archives
  rejected). Buttons live in Settings → General.
  **Import writes files BEHIND the services that cache them, so every cache it
  touches must be invalidated before anything reads again** — `appconfig.json`
  (`IConfigurationService.InvalidateCache`), `player-profiles.json`
  (`IPlayerProfileService.InvalidateCache`) and the practice store
  (`IPracticeProfileService.InvalidateCache`), plus
  `IProjectSessionService.LoadAppConfig()` to re-seat the live `AppConfig`
  instance the session holds. `SettingsViewModel.ImportBackupAsync` does all
  four. Skip any of them and `ProjectSessionService.SaveAppConfig()` — which runs
  on every project open AND save — writes the stale pre-import config straight
  back over the imported file, silently and permanently losing the import).
  `LibraryDoctor/` — batch PSARC library health check over the song-browser
  data source (the already-scanned `LibrarySongEntry` list), READ-ONLY. The rule
  engine `LibraryDoctorAnalyzer` is PURE apart from reading its finding text from
  the string table (`LibraryDoctor_Detail_*`, since 2026-09-24 — they were English
  literals) (`Analyze(LibraryDoctorSnapshot)` →
  `LibraryDoctorReport` grouped by `LibraryIssueKind`: duplicate identity
  (SongKey|Charter|Version across files), missing audio, invalid/unknown tuning
  (known-set generated from `TuningUtilities` named tunings — structurally
  broken = Error, valid-but-unnamed = Info), no loadable arrangement, unreadable
  package). `LibraryDoctorService` is the thin coordinator: probes each distinct
  package once via `ILibraryPackageProbe` — `LibraryService`, which asks the package
  source that owns the file (#886): `DcSongLibrarySource.Probe` verifies a `.dcsong`'s
  checksums, and a plugin's package source probes its own packages (readability and
  audio presence; its guide has the rules)) then calls the
  analyzer. Probe is a seam so the rules unit-test with a fake. UI =
  `Views/Dialogs/LibraryDoctorDialogView`, opened by the Library's "Check library".
  **The doctor is the DIAGNOSIS surface — it keeps REPORTING duplicate identity;
  it never collapses.** The song LISTS collapse those duplicates instead, via the
  pure `Library/LibraryDedupe.CollapseByIdentity` (see
  [parsing-psarc.md](../parsing-psarc.md)) —
  the doctor reads the browser's uncollapsed `AllSongs`, so both stay honest.
- `Update/` — GitHub-based auto-update (device-flow/PAT auth, staged self-apply).
  **The serverless LAN peer-to-peer updater was REMOVED in 0.59** (`LanUpdate*`,
  the UDP 47812 beacon and the TCP 47818 package server, and the Settings ▸
  Updates rows that drove them). Do not restore it: the receive-side guard it
  motivated — `Update/PayloadReceiptFilter` — stays and now covers every apply
  path on its own.
  **The updater script's launch flags are part of its correctness, and getting
  them wrong silently disabled EVERY release's self-update.** `DETACHED_PROCESS`
  gives the child no console, and a console-less `cmd.exe` running a batch file
  **deadlocks on the first pipeline** — 0 % CPU, no error, no exit, forever
  (measured 3/3 on Windows 11 26200 against the exact command line
  `UpdateInstaller.LaunchUpdater` uses; 0/3 with the flag removed). The wait loop's first
  statement was `tasklist … | findstr …`, so every update staged its payload,
  closed the app, and then copied nothing and relaunched nothing. The user's only
  recovery was to copy `update-staging` over the install by hand. Two independent
  fixes, and **both must stay**: `ChildProcessJob.UpdaterCreationFlags` is
  `CREATE_BREAKAWAY_FROM_JOB | CREATE_NO_WINDOW` and never the detach
  (`CREATE_NO_WINDOW` alone already runs a console app window-less), and the
  generated script is **pipeline-free** — `tasklist` writes a probe file,
  `findstr` reads it — because a script that only works under one particular set
  of creation flags is a trap for whoever changes them next. Pinned by
  `UpdaterBreakawayGuardTests.TheUpdaterIsNeverLaunchedWithoutAConsole` (on the
  flag constants, not the source text) and
  `UpdaterScriptBuilderTests.Build_UsesNoPipeline`.
  **The wait is BOUNDED and every phase is LOGGED.** An unbounded spin on a PID
  that may never die is indefensible however the process got there, and a total
  failure that leaves no file behind is undiagnosable — the field report arrived
  with nothing to send. `UpdaterScriptBuilder.DefaultWaitTimeoutSeconds` (300)
  bounds it; the transcript is `<install>\dotcharter-update.log` (truncated per
  attempt, carries robocopy's own summary) and is **the one file to ask a user
  for**. On timeout the script copies NOTHING and deletes nothing — a half-copied
  install is exactly the unbootable host/runtime mix the `InstallLayout` rule
  above exists to prevent — and exits 2 leaving the payload for the recovery path.
  The wait loop is built from `goto`s with **no parenthesised block**: cmd expands
  `%VAR%` when it PARSES a block, so a loop written with parentheses compares the
  counter's entry value forever.
  **A staged payload is never abandoned silently.** `ApplyDownloadedUpdate`
  writes `UpdateApplyState` to `<install>\dotcharter-update-pending.json` BEFORE
  launching, and the script deletes it on its success path only, so all three
  ways an apply can stall (the launch threw, the wait timed out, robocopy failed)
  leave a resumable record. It carries the DELETIONS because
  `PayloadDeletions.TakeFrom` removes `payload-deletions.txt` from the payload —
  a resume with only the staging tree could not reconstruct them and every
  dropped file would survive the no-purge copy forever.
  **The record is DATA, not an authority (#109).** It sits in a user-writable
  folder, and Resume copies from its paths while Discard deletes them. So
  `UpdateInstaller.GetPendingUpdateApply` believes a record only when its
  `StagingRoot` IS `<install>\update-staging`, its `PayloadDir` sits inside it and
  its `ZipPath` (if any) sits in the update download folder; otherwise it reports
  the record unresumable with the reason, and Discard deletes only the canonical
  staging root and never a path the record named. Resume also re-runs
  `PayloadReceiptFilter` on the staged tree. `ReleaseSigningTests` pins all three.
  **The updater script has a RANDOM name in a per-user folder (#109):**
  `UpdaterScriptBuilder.CreateScriptFile` writes `dotcharter-update-<32 hex>.cmd`
  under `%TEMP%\DotCharter-updater` with `FileMode.CreateNew`, never the old fixed
  `<install>\dotcharter-update.cmd` (still deleted when found, still refused
  inside a payload).
  **…and the record is CLEARED when the staging tree it describes is replaced
  (2026-09-04).** `ApplyDownloadedUpdate` deletes and rebuilds
  `<install>\update-staging` before anything else, so a record from an earlier
  attempt stops being true at that line — and every refusal below it (bad
  archive, inflation budget, layout mismatch, `PayloadReceiptFilter` contraband)
  throws with the tree already gone. It used to survive, so one unrelated failed
  download made the app report on every start that an update was staged and that
  "the staged payload is gone", with Discard as the only honest offer.
  `PendingUpdateApplyTests.AnApplyThatIsRefused_LeavesNoStaleRecordFromTheEarlierAttempt`.
  `IAppUpdateService.GetPendingUpdateApply` / `ResumePendingUpdateApply` /
  `DiscardPendingUpdateApply` are the recovery API; the resume **regenerates the
  script against the current PID** (the one on disk waits on a dead id that
  Windows may have reused) and re-runs `AppPayload.ValidateDirectory`, because a
  resume is exactly as irreversible as a first apply. Discard keeps the
  transcript. UI: a notice at the top of Settings → Updates plus a status-bar
  line from the startup check (`PendingUpdateApplyTests`).
  **The device-flow poll rides out a server having a moment (2026-09-04).**
  `GitHubAuthService.PostFormAsync` calls `EnsureSuccessStatusCode`, and
  `WaitForDeviceAuthorizationAsync` used to call it bare — so ONE 502 from
  GitHub's edge ended a sign-in the user was standing in the middle of, with
  "Response status code does not indicate success" and no recovery but starting
  over with a fresh code. A 5xx, a bare connection failure (an
  `HttpRequestException` with no `StatusCode`) and an `HttpClient` timeout say
  nothing about THIS flow; every 4xx and the flow's own `access_denied` /
  `expired_token` do. `IsTransientPollFailure` draws that line, the retry backs
  off exponentially over the poll interval capped at 60 s, and
  `MaxConsecutiveTransientPolls` (5) bounds it — CONSECUTIVE, so a real answer
  resets the streak and one bad poll in a long wait costs nothing. Four cases in
  `GitHubAuthServiceTests` cover the ride-out, the bounded give-up, the streak
  reset and 4xx staying terminal.
  **A failed LAUNCH must never close the app** — that is the reported bug in one
  sentence. `UpdaterLaunchException` is its own type for that reason and
  `Services/Update/UpdateApplyLauncher` is the ONE place that orders "apply,
  then close"; both call sites (the download dialog and
  `UpdateCheckFlow.FinishPendingApply`, behind Settings ▸ Updates) go through
  it and close through `IApplicationCloser`, linted by
  `UI/UpdateApplyLauncherTests.EveryCallSiteThatAppliesAnUpdate_GoesThroughTheLauncher`
  and its `NothingClosesTheAppForAnUpdateDirectly` sibling.
  **Layout rule:** an update is applied by robocopying the payload over the
  install dir with **no purge**, so payload and install must share an
  `InstallLayout` (`AppPayload` classifies both from `DotCharter.runtimeconfig.json`).
  Mixing them strands the old `hostfxr.dll` next to a new runtimeconfig and the
  app dies with "You must install .NET", unfixable by installing the runtime
  (hostfxr then probes the app dir, and .NET 7 removed multi-level lookup).
  Consequences: releases **must** stay `--self-contained` (CI enforces it), and
  `ApplyDownloadedUpdate` validates the staged payload before launching the
  irreversible updater script. A `dotnet build` output has a `DotCharter.exe`
  too, so `File.Exists` is *not* a release check.
  **A package is filtered on RECEIPT, never trusted because whoever built it
  filtered (0.48).** `AppPayload.Validate` is presence-and-layout only (is there
  an exe, a runtimeconfig, a matching host pair) and says nothing about what ELSE
  is in the archive, and `UpdaterScriptBuilder`'s robocopy copies every entry over
  the live install. A zip that did not come out of our release pipeline could
  therefore ship the victim's own binaries plus one `appconfig.json`. **That
  single file is the whole attack:** on a portable install
  `WritablePaths.ResolveConfigRoot` treats an `appconfig.json` next to the exe as
  the DEFINITION of a portable install, so planting it makes the attacker's
  settings authoritative — down to the update channel and every path the app
  reads — and the same copy overwrites profiles, keybinds and the practice
  store, with no prompt.
  `Update/PayloadReceiptFilter` walks the STAGED payload in
  `UpdateInstaller.ApplyDownloadedUpdate` (every apply path funnels through
  it; `GitHubUpdateService` forwards there) and refuses the package naming the offending entry — a refusal nobody can
  act on is a mystery failed update.
  **`PayloadReceiptFilter.IsLocalState` is a data-loss footgun.** On a portable
  install `WritablePaths.ConfigRoot` **is** the install dir, so any user-data or
  credential file it fails to refuse is OVERWRITTEN by the next update. Every new
  file under ConfigRoot must be considered there — and credential stores (e.g.
  the DPAPI `tone3000-token.dat`) must ALSO be on `ConfigBackupRules`' credential
  list.
  **The two lists do not have to be kept in sync by hand (0.45).**
  `IsLocalState` asks `ConfigBackupRules.ShouldInclude` / `.IsCredential` FIRST,
  so anything the backup system classifies as the user's data or as a credential
  is refused by CONSTRUCTION — a name added to the backup allowlist alone can no
  longer slip through. The filter keeps its own literal lists as defence in depth
  and because most of them (caches, `update-staging/`, `models/`, `gpu-runtime/`)
  are deliberately NOT on the backup allowlist. `Services/Update/PayloadReceiptFilterTests`
  pins both directions — dirs, root files and credential stores, plus
  `Filter_ReachesTheBackupRulesRatherThanRestatingThem`, which is the one that
  fails if someone folds the derivation back into literals — and the "honest
  payload still applies" half is the one that matters if this is ever tightened.

