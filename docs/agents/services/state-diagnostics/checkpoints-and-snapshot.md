# `State/Checkpoints/` + `Diagnostics/` — snapshots and the diagnostics bundle (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before touching checkpoints, the recovery/snapshot path or the diagnostics bundle.** Checkpoint snapshots are the SAME `ChartProjectCloner` graph as save/recovery (the single snapshot source of truth), and the diagnostics bundle refuses credential FILES via `ConfigBackupRules.IsCredential` — the same "credentials never travel" sense as the backup rules.

---
  - `State/Checkpoints/` — named, restart-surviving whole-project snapshots.
    `CheckpointService` (`ICheckpointService`) persists one RSCP snapshot +
    `manifest.json` per checkpoint under `<ConfigRoot>/checkpoints/<key>/`,
    keyed by project identity (`CheckpointService.ComputeKey` hashes the raw
    identity — path OR `psarc:` id — so it never mangles a non-path id).
    The identity rule is `CheckpointIdentity.For` (saved path, else library
    song, else empty = the shared "untitled" bucket), used by the dialog and by
    Save As: `ProjectSaver` carries the old identity's checkpoints to the new
    path after a successful write (`CopyTo`, #287; the source keeps its own,
    the untitled bucket is never claimed). Pinned by
    `ProjectSessionSaveOutcomeTests.SaveAs_CarriesTheProjectsCheckpointsToTheNewPath`.
    Writes are temp-file-then-atomic-rename like the recovery store; snapshots
    are the SAME `ChartProjectCloner` graph as save/recovery (caller clones
    before `Create`). All manifest interpretation lives in the pure,
    schema-tolerant `CheckpointManifestOps` (drops invalid/duplicate entries,
    never throws) — unit-tested in `CheckpointManifestOpsTests` +
    `CheckpointServiceTests`. UI: `CheckpointsViewModel` (Edit →
    Checkpoints…); restore swaps the project graph and clears undo history.
- `Diagnostics/` — `DiagnosticsBundleExporter` (`IDiagnosticsBundleExporter`):
  assembles the redacted "Export diagnostics bundle" zip (system-info +
  appconfig.json + recent log tails + manifest). `WriteBundle(Stream, request)`
  is the pure, unit-tested seam; `Export(path, systemInfo)` gathers real inputs
  (configRoot/logRoot/userName overridable for tests). **Every part of the
  bundle — system-info, `appconfig.json`, each log tail — goes through the ONE
  redactor, `Diagnostics/SensitiveTextRedactor.Redact(text, userName)`, the same
  pass the crash bundle uses**, and credential FILES are refused via
  `ConfigBackupRules.IsCredential` — same "credentials never travel" sense as the
  backup rules. Until 2026-09-04 the bundle had its own weaker redactor
  (`DiagnosticsRedactor`) with NO user-name or home-path pass and an ANCHORED
  key/value regex, so the artifact the app invites the user to attach to a bug
  report shipped `C:\Users\<name>` in three places and missed every secret that
  sat inside a Serilog-timestamped line. `SharedRedactorLintTests` fails the gate
  if a second redactor reappears, and pins that both bundle assemblers call this
  one; `DiagnosticsBundleExporterTests.TheBundleNeverCarriesTheOsUserNameOrAHomePath`
  drives a synthetic bundle carrying all four leak shapes.
- **`Diagnostics/LogTailReader` is the ONE way to read the end of a log, and
  reading a log any other way is a bug.** The rolling sink is bounded by
  `AppConfig.LogFileMaxBytes` (10 MB), so `ReadToEnd()` + `Split('\n')` to keep
  200 lines is a ~20 MB UTF-16 copy plus ~20 MB of fragments, all LOH, all
  discarded — which is what the Session Health panel did on EVERY refresh. The
  reader seeks to `length - maxBytes` first (`FileShare.ReadWrite`, so the live
  file the sink holds can still be read) and `ReadTailLines` drops the first
  line, which the seek may have cut in half. Consumers: the exporter's
  `ReadTextTail` (which only adds the "truncated to last N KB" note) and
  `DiagnosticsViewModel.ReadLogTail`. That view model also resolves the ACTIVE
  log file once per `Refresh` and passes it down — resolving it per section
  re-enumerates the log directory and re-stats every rolled sibling for the same
  answer. Regression: `DotCharter.Tests/Services/Diagnostics/LogTailReaderTests.cs`
  (asserts ALLOCATED BYTES per thread + line content, never wall-clock).

---

## Checkpoint durability — the staged write is only half a pattern without the flush

- **A snapshot staged through a serializer's OWN stream must be
  `AtomicFile.FlushFile`d before `ReplaceWithRetry`.** `File.Move` is atomic, so a
  reader sees the whole old file or the whole new one — but it is atomic about
  the NAME, not about the bytes: when `IChartSerializer.Save` returns, the `.rscp`
  is still in the OS page cache. A power cut can retire the rename while the
  contents are in flight, and the manifest then lists a checkpoint whose snapshot
  is empty. It survives the restart, it shows in the Checkpoints dialog, and
  `Restore` throws on it. The 2026-07-16 audit fixed exactly this for
  `manifest.json` (that is what the `WriteManifest` comment is about) and left the
  snapshot — the half holding the user's work — writing without the flush until
  v0.44. Order is `Save` → `FlushFile` → `ReplaceWithRetry`, pinned by
  `CheckpointServiceTests.TheStagedSnapshot_IsFlushedBeforeTheAtomicSwap`
  (a source lint, because nothing in a test can pull the plug — comments are
  stripped first).
- **A refused `Create` leaves the folder exactly as it found it.** A `.tmp` from a
  serializer that died part-way is in no manifest, so `Delete` can never reach it
  and it accumulates one file per failed attempt, inside the folder holding the
  user's unpublished charting work. `Create` now deletes the staging file and
  rethrows — the same rule as the unreadable-manifest refusal one step above it.
- `Restore` is a pure read: two calls hand out two INDEPENDENT graphs, and one
  torn snapshot never blocks listing, restoring or deleting the others.

## The snapshot source of truth (moved here from `DotCharter.Services/AGENTS.md`)

- **`ChartProjectCloner` is the single snapshot source of truth.** Save, crash
  recovery and checkpoints all clone the SAME project graph through it. Never add
  a parallel snapshot path. Its one sanctioned sibling is
  `ChartProjectRestorer` (restore a snapshot INTO the live graph, for undo), and
  the cloner DELEGATES to it rather than duplicating the field knowledge — see
  `DotCharter.Core/AGENTS.md`. The quality report's recycled copy
  (`Services/Editing/Quality/QualityReportCopies`, #711) is that same restore
  into a spare copy instead of an empty one — not a third path.
- **Coverage is DERIVED, not listed** —
  `DotCharter.Tests/Services/State/SnapshotFieldCoverageTests`. `ModelGraphWalker`
  fills every writable property and every collection on the domain graph by
  reflection (deterministic FNV over the property path — never
  `string.GetHashCode`, which is randomized per process) and compares the snapshot
  back property by property, so a field added tomorrow joins the rule the day it
  compiles. Three claims: the undo snapshot carries every field, it shares no
  instance with the live graph, and **the CHECKPOINT FILE — a different
  serializer, `.rscp`, so a field can be in one and missing from the other —
  carries every collection of domain objects.** The file path is scoped to
  MEMBERSHIP because the `.rscp` writer legitimately normalizes scalars
  (re-stamps the format version, re-indexes difficulties, pads a tuning to the
  string count); a whole collection going missing is never normalization. Two
  documented non-membership exclusions and no others: the legacy flat
  `ChartData.SingleNotes`/`Chords` store, and `Arrangement.TuningPitches` (a
  per-string vector whose length is derived on read).

