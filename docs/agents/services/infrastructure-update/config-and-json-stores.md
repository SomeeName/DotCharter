# `Infrastructure/` + `Update/` — config, backup, crash reports, library doctor, auto-update (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before touching config/backup, the crash reporter, the library doctor, or anything in `Update/`.** The `InstallLayout` / `--self-contained` rule and the portable-install `PayloadReceiptFilter.IsLocalState` footgun (a miss lets an update payload overwrite the user's own data and credentials) live here.

---

## Config saves are COALESCED, and both exit paths flush

`IConfigurationService.SaveAppConfig` / `Update` publish the new `AppConfig` to
the in-process cache SYNCHRONOUSLY — several callers `LoadAppConfig()` straight
after a save and must see what they just wrote — but only **arm** the file
write. The write itself (serialize + `AtomicFile.WriteAllText`) runs on a
background thread through
`Infrastructure/Configuration/CoalescedFileWriter.cs`: one pending payload per
target path, last write wins, 250 ms after the last change
(`CoalescedFileWriter<T>.DefaultDebounce`).

Why: measured on the owner's real 16,878-byte `appconfig.json` on the real
config drive, the atomic write + fsync + rename costs **4.40 ms median /
9.68 ms p90 / 13.48 ms max** (1.15 ms without the fsync — the fsync is ~75% of
it); serializing is 0.28 ms. Twelve `partial void On<X>Changed` setters paid
that inline on the UI thread, and `SheetMusicPaneViewModel.OnSheetZoomChanged`
fires once per zoom step: 47–94 ms of UI thread PER SECOND at 10–20 steps/s,
against the 150 ms view-switch budget.

The rules that come with it:

- **Both exit paths must flush**, or everything changed inside the debounce
  window dies silently. `App.TerminateNow` calls `IConfigurationService.Flush()`
  (that path `TerminateProcess`es, so no `Dispose` ever runs), and the clean
  path passes `config?.Flush()` into `App.RunCleanShutdown` as its FIRST step —
  the order is pinned by `CleanShutdownSequenceTests`.
- **Anything that reads `appconfig.json` FROM DISK must `Flush()` first.** Two
  do: `DiagnosticsBundleExporter.Export` (the bundle carries the file verbatim)
  and `ConfigBackupService.Export` (it copies the config-root files). Both are
  flushed at their view-model call sites. `Program.cs`'s two pre-Avalonia probes
  run before anything can have written, and `PayloadReceiptFilter` /
  `ConfigBackupRules` both refuse to let an update payload carry it.
- **`InvalidateCache()` DISCARDS the pending write.** A backup import replaces
  the file behind the service, so a payload armed before the import predates it
  — landing it later would write the pre-import settings straight back over the
  imported file, the exact failure the invalidation exists to prevent.
- The serialize stays inside `_configSync` (it just happens on the writer
  thread now): a concurrent `Update` mutating a `List`/`Dictionary` would throw
  "Collection was modified" and silently drop the save. `Flush()` therefore must
  NOT be called while holding that lock — the write takes it on a DIFFERENT
  thread, and `lock` is reentrant per thread, not per call stack.
- **A crash inside the window costs at most that window's worth of settings.**
  That is the deliberate trade; do not widen the debounce without revisiting it.

Pinned by `CoalescedFileWriterTests` (caller thread ≠ writer thread, a 20-change
burst is ONE write carrying the LAST value, `Flush`/`Dispose` drain, `Discard`
abandons), `JsonConfigurationServiceTests` (cache synchronous / file deferred,
one save TRACE per burst, invalidation abandons) and the source lints in
`ConfigWriteDeferralTests` (no `On*Changed` handler writes a file synchronously;
only `JsonConfigurationService` resolves the app-config path; `SaveLocked` only
arms; both exit paths flush).

---

## A degraded READ is never authoritative — the rule for every JSON store

**Three outcomes, and telling them apart is the whole job.** ABSENT is a new
install (start fresh, write). **CORRUPT** (`JsonException`, or a document that
parses as literal `null`) is readable bytes that are not a store: move the file
ASIDE as `<name>.corrupt-<yyyyMMdd-HHmmssfff>.json` and start fresh — and if the
move FAILS, write nothing. **UNREADABLE** (anything else: `IOException`,
`UnauthorizedAccessException`, an antivirus scan, a cloud sync, a backup tool) is
a file presumed INTACT — so the empty stand-in is served but **never cached**
(the next call retries and a transient lock heals itself mid-session) and **every
save is a no-op** until a read succeeds.

A bare `catch (Exception)` that caches an empty store collapses all three into
the worst one, and every one of these callers is read-modify-write, so the next
save writes the emptiness back. Six shipped that way:

| Store | What an empty stand-in cost |
|---|---|
| `Configuration/JsonConfigurationService` | `LoadAppConfig` cached `new AppConfig()`; the next `Update()` — one happens on nearly every open and save — atomically wrote factory DEFAULTS over every setting the user had. `_degraded`/`_degradedLogged`, `TryQuarantineCorruptConfig`, `SaveLocked` refuses while degraded |
| `Configuration/JsonConfigurationService.LoadKeybinds` | the SAME file's second store, missed by the appconfig fix: a bare `catch (Exception)` returned defaults and `SaveKeybinds` wrote whatever it was handed — the settings page reads the binds back and saves, so one AV/cloud-sync hold wrote every factory chord over the user's. Fixed 2026-08-29: `_keybindsDegraded`, `TryQuarantineCorruptConfig(…, "Keybinds")`, `SaveKeybinds` refuses while degraded (`JsonConfigurationServiceTests.LoadKeybinds_Unreadable_…`) |
| `Gameplay/Profiles/PlayerProfileService` | fixed in 0.58 — see `gameplay.md` |
| `Audio/LivePlay/PracticeProfileService` | the store itself was already correct, but it only SAID so internally — see the take-sweep note in `gameplay.md` |
| `Audio/StemSeparation/CustomStemModelStore` | every registered custom model dropped by adding one |

`Audio/Dsp/Tones/SongToneEditStore` (the player's song-tone edits, one file per
song, 2026-10) was written to this rule from the start and caches nothing, so it
needs no flag: `Read` serves empty for all three, `Save`/`Remove` quarantine a
CORRUPT file and refuse an UNREADABLE one (`SongToneEditStoreTests`). The same
file carries the songs' chart-synth instrument overrides (`"instruments"`, #66),
under the same rule. `Audio/ChartSynth/ProfileChartSynthInstrumentStore` (a
player's chart-synth instrument, `profiles/<id>/chart-synth-instrument.json`) is
the same shape: it caches nothing and reports `Absent` / `Loaded` / `Corrupt` /
`Unreadable`; its one reader, `ChartSynthInstrumentCoordinator`, serves the
defaults for an unreadable file without caching them, and `Save` refuses an
unreadable file and the registry's degraded stand-in profile
(`ProfileChartSynthInstrumentStoreTests`).

Each of the stores above carries a `_storeUnreadable`/`_degraded` flag set only on the non-JSON
branch, cleared on a good read (and by `InvalidateCache`). Pinned by
`Services/Infrastructure/DegradedStoreReadTests` (four stores, one shape) and
`Services/Configuration/JsonConfigurationServiceTests`.

**The test holds the file `FileAccess.Read` shared `Write | Delete`, and the
`Delete` share is what makes it mean anything**: a reader is denied while a
RENAME over the file is still permitted, so the writer really could have
clobbered it. Sharing nothing would make the assertion pass on Windows' sharing
rules rather than on the fix.

**And anything that DELETES on the strength of what a store does not mention must
ask first.** `IPracticeProfileService.IsStoreAuthoritative` exists for exactly
that — see the take-sweep bullet in `gameplay.md`.


## Unknown settings survive a save (#806)

- `AppConfig.UnknownSettings` and `RocksmithKeysConfig.UnknownSettings` are
  `[JsonExtensionData]`: a member this build does not declare is kept and written
  back, so a newer build's setting survives an older build's save. Only those two
  objects keep unknowns; a new nested settings class that must survive needs the
  same property. Builds before v0.70 still drop them.
- The wipe of the Rocksmith keys by such an old build is detected, not prevented:
  `RocksmithKeysFingerprint` keeps `rocksmith-keys.fingerprint` (a SHA-256 under a
  domain prefix, never a key) beside `appconfig.json`. Keys set: the record is
  refreshed on load. Keys empty and a record present: `JsonConfigurationService`
  logs a WARNING (status bar) every launch. No record (first run): silent. The file
  is on `PayloadReceiptFilter`'s exclusion list and in no backup.
