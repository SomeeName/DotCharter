# `Parsing/` — package keys, the library, EOF, tones, lyric codecs (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before touching the package keys, the library, the EOF exporter, tone slots/definitions or the library dedupe.** The `.hsan` tone-backfill note and the EOF byte-for-byte block rules are hard-won.

---

**The package READER is not in this repository (#889).** Reading a
`.psarc` or an SNG is an optional plugin's (`DotCharter.Services.Plugins`): it
registers a library package source, a tone importer (`IPackageToneImporter`) and the
game-install reader. A build without it reads `.dcsong` packages and projects only,
and still WRITES packages (`Export/`, `Infrastructure/Psarc/Write/`). The plugin keeps
its own copy of this guide for the reader's half.

- **Rocksmith keys come from the user, never from the source (#756, #774).**
  DotCharter ships no PSARC TOC key and no SNG key. The user pastes them into the
  `RocksmithKeys` object of `appconfig.json` (`AppConfig.RocksmithKeys`,
  `Infrastructure/Configuration/RocksmithKeysConfig`; the file is
  `WritablePaths.ResolveConfigFile("appconfig.json")`, under `DOTCHARTER_DATA_ROOT`
  `<root>\Roaming\DotCharter\`). The three fields are written EMPTY by default, so
  the file shows where each key goes:

  ```json
  "RocksmithKeys": {
    "PsarcToc": "",
    "SngPc": "",
    "SngMac": ""
  }
  ```

  Each value is a 32-byte key as 64 hex digits (whitespace allowed); any may stay
  empty. **Edit appconfig.json while DotCharter is CLOSED** — the app rewrites the
  whole file on its next save, so an edit made while it runs is lost. **No menu
  item, settings row or UI text names the section** (owner decision on #756/#774);
  it is documented here only. `Infrastructure/Psarc/RocksmithKeys` is the one
  reader: the app wires it to its configuration service at startup
  (`JsonConfigurationService.ForApplication` → `RocksmithKeys.UseConfiguration`), and it re-parses whenever
  the config's values change. A malformed value is IGNORED on its own (the other
  keys still count) and named — never quoted — in an Info log line on load
  (`RocksmithKeysConfig.Problems`; Info, not Warning, so it never reaches the
  status bar) and in `RocksmithKeys.LoadProblem`.
  **The old `rocksmith-keys.txt`** (builds between #756 and #774) is read only by
  the one-time migration in `JsonConfigurationService` (`LegacyRocksmithKeyFile`):
  its values fill EMPTY fields (a key already in appconfig.json wins), the config
  is written synchronously, and the txt is DELETED only after that write
  succeeded. A malformed txt is left in place and logged by line number.
  Without keys a `.dcsong` reads, plays and exports fully (a package reader
  reports an unreadable package with the neutral resx key `Library_PackageUnreadable`,
  which the song browser shows through `Library/ILibraryReadError`);
  `RocksmithPackageBuilder` writes a PLAIN package whatever
  `PlainPackage` says, and the export dialog's Package picker is disabled on Plain (#804; with keys the default is Encrypted for every string count).
  **Whenever the PC package will be plain, by that or by choosing Plain** (`IExportTarget.WritesPlainPackage`:
  `ExportTargetViewModel.WritesPlain` and not Mac-only), the dialog shows an amber box,
  `Dialog_Export_PlainPackageWarning`: plain packages load only with the RS7String 7-string mod, and a
  standard chart goes through DLC Builder. Its button writes that project on request
  (`RocksmithExportDlcBuilder` → `Export/DlcBuilder/DlcBuilderProjectWriter`: the `DTRS XML/` bundle and the
  `.rs2dlc` into a picked folder, replacing the ones there; the save companion shares its file naming and
  path references through `DlcBuilderProjectFile`). The text names no key and describes no decryption.
  A Mac package is never plain, so without the
  TOC and Mac SNG keys there is none (#782, `RocksmithPlatformTargets`). With keys
  everything is as before.
  **The values are secrets.** `Diagnostics/SensitiveTextRedactor` blanks
  `PsarcToc` / `SngPc` / `SngMac` (and the old `psarc-toc` / `sng-pc` / `sng-mac`
  lines) to the closing quote or end of line, so the diagnostics bundle's
  appconfig.json, its log tails and the crash report carry `<redacted>` (an empty
  value stays `""`) — `RocksmithKeyRedactionTests`. The LAN update payload never
  carries appconfig.json (`PayloadReceiptFilter`); the config BACKUP does, keys
  included, because restoring it on a new machine must restore them — it is the
  user's own archive, not a support export.
  Tests: the test assembly's module initializer
  (`DotCharter.Tests/Infrastructure/TestRocksmithKeys.cs`) pins the process to the
  owner's real keys when present (`DOTCHARTER_RS_KEYS` naming an appconfig.json or
  an old key file, else the product's appconfig.json, else an old key file beside
  it — read only), else to SYNTHETIC keys; a test that opens stock packages calls
  `TestRocksmithKeys.RequireRealKeys()` and skips without them
  (`RocksmithKeysTests` covers the no-key paths with synthetic keys,
  `AppConfigRocksmithKeysTests` the config, validation and migration). The real
  keys appear in no tracked file — `RocksmithKeyLeakLintTests` knows them only
  by SHA-256 and fails on raw bytes, hex, `0x..` lists or base64 of any of them.
- **The tone converter's game tables come from the optional plugin that reads the
  game install (#755, #888)**; without it they are empty and the conversion is
  generic. Detail: `dsp-tone-rig.md` § "Rocksmith's game tables".
- `Library/` — the NEUTRAL song library (#886, namespace `DotCharter.Services.Library`):
  `LibraryService` (the walk, the SQLite index `Index/SqliteLibraryIndex` with its
  `(size,mtime)` cache, the parallel parse `LibraryScanParallelParse`, progress and song
  batches) over every registered `ILibraryPackageSource`, plus the types that carry any
  package (#884): `LibrarySongEntry` (`PackagePath`), `LibraryArrangementEntry`,
  `LibraryLoadedSong`, `ILibraryService`, `LibraryScanProgress`, `LibraryDedupe`,
  `LibraryArchiveFiles`, `LibraryCoverCache` (the album-art PNG cache),
  `Cache/LibraryAudioCacheStore`, `Prefetch/ILibraryAudioPrefetcher`. The SQL columns
  (`psarc_path`) and the cache folder (`PsarcAudioCache`) keep their names: they are on
  the user's disk.
- **The library seam (#886).** A package source implements `ILibraryPackageSource`:
  its `FileExtensions`, `BeginScan` (once per scan, off the UI thread), `ReadForIndex`
  (one file's rows; throws for a file it cannot read, which the scan logs as skipped),
  `LoadSongAsync`, `PrepareAudio` (convert-on-select), `LoadCover`, `BlockFor` and
  `Probe` (Library Doctor). `LibraryService` asks the source whose extension the path
  ends in and refuses a file no source reads (`NotSupportedException` on load, `NoAudio`,
  no block, an unreadable probe). The app registers `Package/DcSong/Library/DcSongLibrarySource`
  (`.dcsong`); a plugin registers its own package source AHEAD of it (`App.Plugins.cs`
  runs first), and the scan lists each source's files in registration order, so the
  file order (and with it the tie order of the sorted result) is stable.
  `LibraryService` is registered as `ILibraryService` and forwarded as
  `ILibrarySongPlayability`, `ILibraryAudioPreparer` and Library Doctor's
  `ILibraryPackageProbe`; **nothing outside the Library and `App.*.cs` names a source**
  (`Infrastructure/LibrarySeamLintTests`). `LibraryServiceTests` pins the seam on fake
  sources; `DcSongPlayModeLoadTests` runs the Library and Play Mode with ONLY the
  `.dcsong` source registered.
- **A library song's identity is `lib:<SongKey>`, spelled ONLY by
  `Services/Library/LibrarySongIdentity`** (#884). Builds before it wrote
  `psarc:<SongKey>`; every store keyed on identity reads both and writes `lib:`
  (practice profiles/stats/leaderboards, setlists, `AppConfig.SongTonePreferences`
  and `DropTunerSongs`, song tone edits, checkpoints). An old entry leaves disk only
  in, or after, the write that stores the new one; a `lib:` entry wins over its
  `psarc:` twin. The stem cache hashes `lib:` too since #898 and
  RE-KEYS an archive it finds under the old `psarc:` name (copy, verify, rename,
  then delete; see `stem-separation.md`). `LibrarySongIdentityMigrationTests`.
  **A new field on `LibrarySongEntry`/`LibraryArrangementEntry` needs a
  `SchemaVersion` bump even when it adds no COLUMN** — arrangements are stored
  as `arrangements_json`, so an old row deserializes the new member as its
  default and, because nothing about the archives changes when the app is
  upgraded, a stale `(size,mtime)` hit serves that default forever. v7 is the
  worked example: `LibraryArrangementEntry.NoteCount` (the manifest's
  `NotesHard`, which the `.hsan` header the scan already reads carries — no
  extra archive work) would otherwise have read 0 on every existing library.
  v8 did it again for `LibraryArrangementEntry.Tuning`/`Capo`: **the index
  stores the tuning PER ARRANGEMENT now** (a package source fills it per
  arrangement), while `LibrarySongEntry.TuningSummary` stays the
  one song-level value the FILTER matches on and still prefers a guitar
  arrangement (`PickTuningSource`). Rocksmith writes every tuning into six
  slots regardless of instrument — a six-slot vector is not always a guitar —
  so a Lead in E standard and a Bass in drop D are two tunings in one package
  and only the per-arrangement fields can state both. Surfaced in the
  browser's path list (`PsarcArrangementChoice.TuningLabel`) and the Play Mode
  arrangement tiles (`SongArrangementFact.TuningLabel`). Pinned by
  `ArrangementTuningLabelTests` (the labels) and the plugin's own tests (both
  indexed, song-level prefers the guitar, a v7 database triggers the rebuild).
  The `song_ratings` carry-over table (v6) is what makes such a rebuild free:
  the one figure a scan cannot re-derive survives it.
  `Parsing/{Xml,GuitarPro,Eof,Rscp}/` — the other chart formats.
  **The scan reads `.dcsong` packages (#708)**: `LibraryArchiveFiles.Enumerate` lists
  every source's files (the sources' extensions, in registration order),
  and `LibraryService` hands a package to
  `Package/DcSong/Library/DcSongLibrarySource` to parse, load, prepare and cover — same
  index, rows and load path; `LibrarySongEntry.SourceKind` is the extension (no column, no
  schema bump). Full text: [dcsong-format.md](dcsong-format.md) § Library and Play Mode.
  **The root walk uses `LibraryArchiveFiles.RecursiveScanOptions`, NOT
  `SearchOption.AllDirectories`.** That overload is `EnumerationOptions.Compatible`,
  i.e. `IgnoreInaccessible = false`, so the first directory the user cannot list
  threw `UnauthorizedAccessException` out of the enumeration before a single
  archive was opened -- no partial result, no cache use, both song lists empty
  behind a "scan failed" status. A drive-root library is supported (`PurgeMissing`
  has a case for it) and every NTFS volume root carries `System Volume
  Information`, so the supported configuration was the broken one. The options
  also clear `AttributesToSkip` back to zero, because `EnumerationOptions`'
  default hides hidden/system entries and a library may live in a hidden folder.
  Pinned by `LibraryScanInaccessibleFolderTests` (structural on the options plus
  a real denied directory).

  **A COLD scan parses in parallel, behind ONE index writer (#330).**
  `LibraryService.ScanAsync` runs in two stages: stat + index lookup stay
  sequential on the scan thread (the cache hits stream out first, in 256-song
  batches), then the misses go to `LibraryScanParallelParse.Run`. Its rules, each
  pinned (on the plugin's package source, which has a real parse to run) and each
  proven by breaking it:
  - **Bounded and polite**: `DegreeFor` = half the logical processors, at most
    `MaxDegree` (4), at least 1; the parsers are dedicated BACKGROUND threads at
    `ThreadPriority.Normal`. **The bound is the politeness, not the priority —
    never lower the parsers below the priority the old serial scan ran at (a
    Normal pool thread).** BelowNormal was tried first and measured 8-10x SLOWER
    than the serial scan under load (below).
  - **One writer**: parsers only parse and push outcomes onto a queue; the scan
    thread is the only caller of the `ILibraryIndexScan` session (its
    contract is single-threaded) and of `songFound` / `progress`.
  - **File order, not completion order**: early outcomes wait in a reorder
    buffer, so the rows, their rowids, the `songFound` stream and the result's
    tie order are byte-identical to a serial scan's (the test holds the first
    archive back until every other one has parsed, then diffs both SQLite files).
    (`LibraryDedupe` picks its winner by path, independent of order; the
    stable `Artist, Title` sort keeps the per-file order inside a tie, so a
    completion-order result would reorder ties by thread timing.)
  - **A failure costs one file**: a parse that throws is an outcome carrying
    the exception — logged `PSARC scan skipped`, not indexed, retried next scan.
  - **Cancellation joins every parser before `ScanAsync` ends.** The join is
    load-bearing: a parser left running would `Add` to a disposed queue and the
    unhandled exception kills the PROCESS (seen when the join was removed on
    purpose). Upsert comes before `songFound`, so a cancel from that callback
    still leaves the file whole in the index.
  - The scan logs `Library scan parsed N/M file(s) on D thread(s) in X ms`;
    no test asserts a time. Two internal init-only seams on the service set
    the parser count and wrap each parse on its parser thread.
  - **Measure old and new under the SAME load**, never one against a quiet
    box. Owner's library (1,901 files), cold index, Idle class on CPUs 8-15, with
    a synthetic 8-thread busy loop at Normal thread priority on the same cores:
    serial 4.3–6.4 s; BelowNormal parsers 42–52 s (starved: ~4.5 s of CPU across
    the wall). That is why the parsers run at Normal.

  **A warm scan is 1,825 iterations of the same loop, so per-file costs are
  multiplied by the library size — treat anything inside it as a hot path.**
  Two measured on the owner's 1,825-file / 2,160-song library (~330 ms warm, ALL
  cache hits, not one archive opened):
  - **The two lookup statements are PREPARED ONCE per session** and re-executed
    with a rebound parameter. `CreateCommand()`/`ExecuteReader()`/`Dispose()` per
    file re-ran `sqlite3_prepare_v2` every time — ~3,650 parse+plan cycles per
    scan to read two rows each. The failure mode of the reuse is a parameter
    that does not rebind (it would serve the PREVIOUS file's songs, silently and
    plausibly), which is what
    `SqliteLibraryIndexTests.One_session_serves_many_different_files_without_bleeding_between_them`
    exists for. The session is single-threaded by contract, and the commands are
    disposed BEFORE the connection.
  - **Scan progress is time-COALESCED (~30 Hz), never per file.** `progress` is a
    `Progress<T>` built on the UI thread, so each `Report` is a dispatcher work
    item running a `LocalizationService.Format` + two `PropertyChanged` raises —
    one per file meant 1,825 of them aimed at the UI thread during a scan the
    boot path starts while the window is still settling, and invisible anyway at
    ~5,500 files/second. The LAST file always reports so the bar reaches 100%.
    `songFound` keeps its own (count-based, 256) batching and still fires per
    PARSED file — which is why the cancellation test hangs off that one.
  - **That backlog was measured and mostly TAKEN (2026-08-29). A warm scan is
    not ~330 ms — it was 518–747 ms, and is now 195–356 ms** (Release, idle box,
    the owner's 1,825-file / 2,160-song library; the "~330 ms" this file carried
    was never re-measured after the features that grew the row). Re-measure; do
    not carry these forward either.

    The A/B that decides it runs both read paths **interleaved in ONE process**
    (`LocalOnly_PsarcScanAbProbe`, gated by `DOTCHARTER_NO_PSARC_PREFETCH=1`),
    because absolute numbers on this box move 3× with another agent building —
    an A/B taken minutes apart says nothing. Contended, four rounds each, both
    arms asserting the same 2,160 songs: **744.5 → 392.6 ms min, 1.90×**.

    Per-stage attribution (`LocalOnly_PsarcScanAttributionProbe`, Release, idle,
    3 reps):

    | stage | before | after | taken? |
    |---|---|---|---|
    | 1,825 × two point lookups | 67.6 ms | one bulk read, 13.2 ms | **yes** |
    | 1,522 cover `File.Exists` | 156.6 ms | one dir listing, 37.1 ms | **yes** |
    | 2,160 reflection JSON deserializes | 78.0 ms | source-generated | **yes** |
    | 1,825 redundant `FileInfo` stats | 1.2 ms | — | **no — it is NOISE** |
    | directory walk (`AllDirectories`) | 115–159 ms | 111 ms parallel over 693 dirs | **no** |
    | `PRAGMA quick_check(1)` | 9.4 ms once | — | **no** |

    **Two of the ranked items were wrong and the ranking is why they looked
    right.** The `FileInfo` row was #2 and is 1.2 ms: `Directory.EnumerateFiles`
    plus `new FileInfo` measured 154.6 ms against `DirectoryInfo.EnumerateFiles`'
    153.4 (and 227.0 vs 159.1 in a second run — it is *slower* as often as not),
    so the "facts come free from WIN32_FIND_DATA" reasoning does not show up as
    time. The parallel directory walk is a real ~48 ms but was declined: it puts
    693 concurrent enumerations on the boot path while the window is still
    settling, to save a quarter of a scan that now finishes in ~200 ms.
    `quick_check` stays — it is a deliberate corruption guard.
  - **The cache-hit path is ONE bulk read per session, not two lookups per
    file** (`Session.EnsurePrefetch`). Both tables are read whole on the first
    `TryGetCachedSongs`, into dictionaries keyed **`StringComparer.Ordinal`** —
    SQLite's default TEXT collation is BINARY, so `WHERE psarc_path = $p` was
    case-SENSITIVE and an `OrdinalIgnoreCase` map would serve hits the old path
    missed. Rows are held RAW and materialized on a HIT, so a stale file still
    pays neither the JSON parse nor the cover probe. Any failure leaves
    `_prefetchUsable` false and the per-file prepared statements take over —
    they are kept for exactly that, because the index is a CACHE and a degraded
    read path must still be a correct one.
    - **The snapshot is dropped for any path the session WROTE**
      (`_writtenThisSession`), and that half is not optional: what `UpsertFile`
      commits is not what it was handed (the rating carry-over can replace the
      supplied 0), and a re-parsed archive re-extracts its cover. Both were
      caught by existing tests — `Schema_version_mismatch_drops_and_rebuilds`
      and `Cover_image_path_is_revalidated_on_read` — not by inspection.
      `PurgeMissing` invalidates the whole snapshot, which in practice just
      orders the bulk read after the purge.
    - **Cover revalidation is one directory listing per album-art folder**, not
      one `File.Exists` per song. Nothing in the app prunes
      `%LOCALAPPDATA%\DotCharter\AlbumArtCache` — it shrinks only when the user
      or a disk cleaner empties it, between runs — so a session-start listing
      answers the same question. A per-lookup directory-generation check would
      have made it exact for mid-scan deletions and was measured at 142.6 ms
      against `File.Exists`'s 149.0, i.e. it costs everything it saves.
      **The cover cache is keyed by ARCHIVE IDENTITY, not by file name**
      (`LibraryCoverCache.AlbumArtCacheStem`): file name + a hash of the full
      path + the archive's last-write ticks + the song key, deriving the same
      three components as `LibraryAudioCacheStore.DirectoryNameFor` beside it.
      Until 0.60 it was file name + song key only, and `LoadCoverImageAsync`
      returns a usable cached PNG WITHOUT opening the archive — so a rebuilt
      CDLC kept its old cover forever, and two same-named archives in different
      folders sharing a song key showed each other's art. Pinned by the plugin's
      package-source tests (stems differ for same-named archives in different
      folders, follow the archive's write time, differ per song). Changing the
      stem orphans the previous generation of cached PNGs; nothing prunes them,
      as the paragraph above says.
    - **`arrangements_json` is source-generated (`PsarcIndexJsonContext`), and
      its emitted shape must stay identical to the reflection serializer's** —
      no naming policy, no case-insensitive matching. Every already-indexed row
      was written by reflection, and nothing about the archives changes when the
      app is upgraded, so a shape change would make a stale `(size, mtime)` hit
      serve DEFAULTS forever. That is the same trap the `SchemaVersion` comment
      describes; a shape change needs a schema bump too. Pinned byte-for-byte by
      `TheArrangementsJson_KeepsTheShapeTheReflectionSerializerWrote` over a
      `DomainFieldProbe` instance, so a new property is covered the day it is
      added.
    - Guards, all in the default gate and each proven by breaking it:
      `AWarmSession_ServesFromOneBulkRead_NotAQueryPerFile` (deletes every row
      through a second connection after the first answer — a per-file reader
      misses), `APathWrittenDuringTheScan_IsReReadInsteadOfServedFromTheSnapshot`
      and the JSON pin above.
  **`ReadFacts()` is the index's one READ-ONLY door, and it is deliberately
  unlike every other path in here.** It answers `LibraryIndexFacts(Songs,
  Archives)` from one `SELECT COUNT(DISTINCT song_key)` + one `COUNT(*)`, for
  callers that want to SAY how big the library is (the Home screen's one-line
  summary) without triggering a scan. It opens `SqliteOpenMode.ReadOnly` and
  **never rebuilds and never deletes** — every other entry point may drop and
  recreate the tables on a version mismatch or a corrupt file, and a readout has
  no business doing either: `song_ratings` is months of data that cannot be
  re-derived by a scan. An absent, locked or unreadable index answers `Empty`,
  which the caller renders as its fresh-install copy. `DISTINCT song_key`
  because one song appears once per pack that ships it.
  **Difficulty rating column (schema v5).** `songs.rating` caches the deterministic
  1–10 `ChartDifficultyEstimator` score so the UNPLAYED library shows a difficulty
  (`LibrarySongEntry.Difficulty`; Song Select chip / sort / band filter). A scan
  CANNOT compute it — it reads only the `.hsan`, which has no note data, and
  parsing every SNG at scan time across a whole library is prohibitive. So
  a package source's `LoadSongAsync` rates the hardest arrangement the first
  time a song is fully parsed (the one place the chart already exists) and writes
  it via the best-effort `ILibraryIndex.TrySetSongRating` (a targeted UPDATE
  on its own short connection — no-op when the song is not indexed yet); the scan
  reads it back cheaply. Cache-only, like everything else in the index: a changed
  file re-scans to rating 0 until re-loaded, and a never-loaded song stays unrated
  (0). Schema bump follows the existing drop-and-rebuild versioning.
  **That "a changed file re-scans to rating 0" sentence was FALSE until 0.60** —
  `song_ratings` is keyed by `(psarc_path, song_key)` with no `(size, mtime)`
  dimension, so the carry-over UPDATE in `UpsertFile` copied a pre-update rating
  back over the scan's 0 and the library showed the OLD chart's difficulty until
  the song was opened again. `UpsertFile` now reads the outgoing `files` row
  BEFORE its DELETE: no row (a fresh db, or the empty table a schema rebuild
  leaves) carries the rating over — that is the whole point of the table — while
  a row whose `(size, mtime)` DIFFERS means the archive was replaced, so the
  carry-over row is deleted instead. Deleting matters: leaving it would
  resurrect the stale value at the next rebuild, which has no `files` row left
  to judge it by. Pinned by
  `SqliteLibraryIndexTests.An_archive_that_changed_under_the_same_path_loses_its_carried_rating`
  and `.An_unchanged_archive_keeps_its_carried_rating_through_a_rewrite`.
  **…and `BeginScan` may not DELETE the db just because it could not open it
  (2026-09-04).** One `catch (Exception)` treated every failure as corruption
  and called `DeleteDatabaseFiles()`, `song_ratings` included — for the most
  ordinary transient failure this file has, a LOCKED db (our own
  `TrySetSongRating`'s second connection, a second app instance, an AV or
  backup hold). The window was wider than it looked because `busy_timeout` was
  set AFTER `journal_mode=WAL`, so the one statement that needs an exclusive
  lock ran with no timeout at all and failed instantly. `busy_timeout` is first
  now, and `IsTransientlyUnavailable` splits "cannot use this file right now"
  (BUSY/LOCKED/READONLY/CANTOPEN/PROTOCOL, `IOException`,
  `UnauthorizedAccessException` — scan without a cache, leave the file alone)
  from "this is not a database" (still delete + rebuild). Same three-outcome
  rule the JSON stores follow. `LibraryIndexBusyRetryTests`.
  **Every index connection is opened by `SqliteLibraryIndex.Connect`**,
  which sets BOTH busy timers to one value: SQLite's `busy_timeout` and
  Microsoft.Data.Sqlite's `DefaultTimeout`. The library retries a busy command
  until that command timeout (30 s by default), so with only `busy_timeout` set a
  locked index held a scan ~35 s instead of 5 s, and a cover lookup ~31 s
  instead of 1 s. `LibraryIndexBusyRetryTests` pins both timers and that no
  connection bypasses `Connect`.
  **`PurgeMissing` deletes `song_ratings` too, and that is load-bearing rather
  than tidiness (0.60.x).** It used to drop only `songs` + `files`, which
  manufactured a THIRD producer of the "no `files` row" state `UpsertFile` reads
  as *a schema rebuild, carry the rating over*: the archive was DELETED. A psarc
  removed from the library and later replaced at the same path (a re-downloaded
  CDLC, another cut under the same file name -- song keys are stable across
  revisions) therefore inherited the dead chart's difficulty. The same statement
  is the only thing that ever bounds the table. Pinned by
  `.A_purged_archive_does_not_leave_its_rating_for_the_next_file_at_that_path`
  with `.A_present_archive_keeps_its_carried_rating_through_a_purge` as the
  wrong-direction guard.
  `PurgeMissing` enumerates paths from BOTH `files` and `song_ratings`:
  immediately after a schema rebuild the first table is empty, so reading it
  alone misses ratings for archives deleted before that scan. Present paths and
  paths outside the scanned root keep their ratings. Pinned by
  `SqliteLibraryIndexTests.Purge_after_schema_rebuild_removes_only_missing_archives_carried_ratings`.
  **EOF is read AND write:** `Eof/EofExporter` (+ `EofPackWriter`,
  `EofNoteBuilder`, `EofTrackScaffold`) saves a whole `ChartProject` as an
  Editor-on-Fire project mirroring `eof_save_song` byte-for-byte — canonical
  15-slot scaffold (empty bonus track omitted), the ID-2 fp-beat-timing
  block whose size EXCLUDES the 4-byte block ID (every other block includes
  it), conditional pro-guitar note fields, beat-index-anchored text events.
  Lossy decisions never throw — they land on the `EofConversionReport`
  (Approximated/Dropped, same pattern as the RS tone conversion report).
  **Every `Report.Add` names WHAT was lost (#638):** an `EofLoss` member (one
  per decision, its user-facing name the resx family `EofLoss_<member>`,
  enumerated by `LocalizationSweepTests`), the time when it happened at one
  place, and a `Detail` value (a phrase, a tone, a number — never a sentence).
  `EofLossSummary` groups the notes by kind, counts a place once across the
  difficulty ladder and caps each list; the Chart quality report shows it as
  its "Editor on Fire round trip" (`ChartQualityReporter.CheckEofRoundTrip`
  runs the exporter in memory, so the places are never a stale export's), File
  ▸ Export ▸ EOF's lossy status-rail message opens it with Show, and the save
  companion logs `EofLossSummary.LogLine()`. **That section is LAZY (#685):**
  the in-memory export costs ~105–145 ms on a chart with a 20-level ladder, so
  `EofRoundTripSectionViewModel` runs it only while the section is open, on a
  `ChartProjectCloner` snapshot, off the UI thread, and drops a result that
  lands after a recheck. It opens closed unless the project is
  `ChartDocumentState.EofRoundTripProject` (an EOF export, or the user opened it;
  the save companion's `.eof` is on by default and does not count) or the report
  came from Show. A new
  `Report.Add` site needs its own member and three resx strings;
  `EofLossSummaryTests` builds a chart that hits every member.
  **A beat's ANCHOR flag (`EofConstants.BeatFlagAnchor`) is part of the beat and
  goes through `EofImporter.BuildBeatMarkers` — the import dialog must never
  hand-roll a `BeatMarker`.** The dialog had two hand-rolled copies of that
  projection and both dropped `IsAnchor`, so the imported chart's NOTES resolved
  against the anchors (the importer's own markers) and the SAVED project had
  forgotten them. It is not decoration: an anchored beat is one the charter
  pinned by hand, `TempoService` refuses to move it when re-timing, `RscpWriter`
  persists it and `EofExporter` re-emits it — so the first tempo edit after an
  import dragged pinned beats and a round trip back to `.eof` lost every flag.
  `EofBeatAnchorImportTests` pins the mapping and lints the dialog's call site.
  Round-trip parity against `EofImporter` is gated by
  `EofRoundTripParityTests` — an exporter change that breaks reimport
  equality fails the suite. Four EOF-invariants added in 0.33 (all
  bit-compared against real eof_save_song output): the OGG profile's
  `midi_offset` IS beat 0's time (0 there = EOF's tempo-map-corruption
  dialog + a 10 s desync on "repair"); handshapes are arpeggio-section
  instances WITH flag `EOF_RS_ARP_HANDSHAPE=2` (flag 0 = true arpeggio,
  keyed off `ChordTemplate.Arpeggio` both ways); `GraceFret` hints
  materialize as EOF grace pairs (short LINKNEXT slide-in + displaced
  target; the importer folds the exact shape back — **since 0.33.2 for
  CHORDS too: one grace note carrying every graced string, folded back to
  per-string `ChordNote.GraceFret`. EOF stores ONE slide end fret per note
  (the lowest fretted string's) and projects that single distance onto every
  string, so a chord grace is only representable when every graced string
  travels the SAME non-zero fretted interval — anything else is REPORTED as
  dropped, never written as a wrong shape. Writer and folder must stay
  symmetric or a save/reload duplicates or drops notes**); multi-point bend
  curves ride the tech-note custom block 7 (1 ms BEND|RS_NOTATION points,
  quarter-step MSB) and the importer rebuilds `BendValues` from them.
  **Split and Fingerless both ways (#609):** the note flag
  `EOF_PRO_GUITAR_NOTE_FLAG_SPLIT` (`0x20000000`, `song.h:64`) is
  `Chord.SplitIntoNotes`; the EXTENDED flag `EOF_PRO_GUITAR_NOTE_EFLAG_FINGERLESS`
  (`32`, `song.h:137`) is `ChordTemplate.Fingerless` — per NOTE in EOF, per
  template here, so the importer keys its template cache on frets + fingers +
  fingerless (EOF's chord list keeps the two apart too, `rs.c:231`) and DROPS
  any finger values EOF stored under the flag (it never exports them,
  `rs.c:910-914`, `:5958`); the exporter sets the eflag on every chord whose
  template carries it. A tremolo phrase covers a chord's notes on import (EOF's
  phrase status marks every note in it, `main.c:1770-1784`), and a chord tremolo
  on EVERY string exports as one; tremolo on some strings is still reported
  dropped. Pinned by `EofSplitFingerlessTests`.
  **Tones:** an arrangement names its tones as `Tone_Base` + `Tone_A..D` (keys, not
  the bodies). **DotCharter never writes a package for a tone edit.** A tone the
  player reshaped on the rig is DotCharter's data (`Audio/Dsp/Tones/SongToneEditStore`,
  `<ConfigRoot>/song-tones/`, keyed by song identity), and `RsTonePlaybackCoordinator`
  lays a song's edits over the package's converted tones on every prepare; the
  package's tone BODIES come from `PackageTones` (the plugin's `IPackageToneImporter`,
  none without it).
  **Authored tone definitions (in-editor tone authoring):** full tone BODIES a
  project owns live in `ChartProject.ToneDefinitions` (`ToneDefinition` in Core:
  key/name + raw manifest JSON). RSCP persists them as the `dcToneDefinitions`
  extension node (written only when non-empty, like `dcAnnotations`;
  `RscpWriter.BuildToneDefinitionsNode` / `RscpHeaderParser.ParseToneDefinitions`).
  Authored bodies are (re)serialized via `Audio/Dsp/Tones/RsToneJsonWriter`
  (RsTone -> manifest tone JSON, the parser's inverse); imported bodies stay
  verbatim strings. At export `RocksmithPackageBuilder.MergeToneDefinitions`
  overlays the project's authored set over any imported passthrough (authored
  wins by key); `ChartExportValidator` warns per referenced-but-undefined tone
  (`KeyToneNotDefined`; its Fix renames in place when `Editing/Tones/ToneKeyRepair`
  finds the one repair, #195) and `CountToneDefinitions` gives the defined/referenced
  summary. The pure helpers behind the in-editor Tone Designer live in
  `Audio/Dsp/Tones/RsToneAuthoring`: tone-key validation/sanitize/uniquing
  (identifier-like, case-insensitive collisions), `GearOptions` catalogue
  slices per slot type (63/450/83/17), the fixed 32-entry descriptor catalogue
  (UINames verbatim from Rocksmith2014.NET `ToneDescriptor.all` — never invent
  loc ids), knob clamp/default helpers, the DLC-Builder-parity volume range
  (−36..−0.1 dB) and `CreateDefaultTone` (first catalogue amp + RS default cab,
  writer↔parser round-trip-safe). The dialog itself is
  `DotCharter.UI/ViewModels/Dialogs/ToneDesignerDialogViewModel`.
  `Parsing/{LRC,UltraStar}/` — pure lyric codecs (no I/O). `LRC/`:
  `LrcLyricsParser` (import) + `LrcLyricsWriter` (export, enhanced word-timed).
  `[offset:]` follows the format and the players (foo_openlyrics, LyricsX): a
  POSITIVE offset makes lyrics appear SOONER (`stamp - offset`), and it moves the
  `<mm:ss.xx>` word stamps by the same amount as the `[mm:ss.xx]` line stamp
  (#331, `LrcLyricsParserTests`). The writer never emits the tag.
  `UltraStar/`: `UltraStarLyricsParser`/`UltraStarLyricsWriter` operate on the
  native-beat `UltraStarSong` model (exact round-trip); `UltraStarVocalConverter`
  bridges to the `VocalTrack` beat-grid domain (ms↔position via `TimelineMath`,
  file-beat = quarter beat, file pitch 0 = MIDI 60). The parsed model is the
  file AS WRITTEN: a `#RELATIVE:YES` song keeps its per-line beats and is
  resolved by `UltraStarSong.ToAbsoluteBeats` (USDX `NewSentence`: per voice,
  `rel += relOffset` after each `- beat relOffset`), which the converter calls
  first. Duet voice changes (`P1`/`P2`) land in `UltraStarLine.Voice`; a voice
  change ends the previous voice's line, and the writer re-emits it.
  `ToVocalTracks` returns one track per voice (named from `#P1`/`#P2`, then
  `#DUETSINGERP1`/`#DUETSINGERP2`), never a merge — `ToVocalTrack` is the first
  voice (#332, `UltraStarLyricsCodecTests`, `UltraStarVocalConverterTests`).
- **Every RS vocal reader shares ONE decoder** — `Xml/Vocals/RsXmlVocalsReader`
  (`vocals.xml`) and the package reader's SNG vocals both go through
  `Xml/Vocals/RsVocalPhraseBuilder`, which strips the trailing `-`/`+` with
  `RsVocalLyricEncoding.Decode` (the inverse of `Encode`), sets `IsWordPart` /
  `IsPhraseEnd`, and starts a new phrase after each `+` or a 5 s silence. Stored
  text carries no marker and no trailing space (the word gap comes from
  `IsWordPart`). The XML reader used to keep the markers and build one phrase
  for the whole song (#324); `RsXmlVocalsRoundtripTests` pins the XML path (the
  plugin's tests compare it with the SNG path on the same syllables).
- **A `.eof` is untrusted input too, and its two size fields are file-supplied
  (0.41).** `EofPackReader.ReadBytes`/`Skip` bounded with
  `Position + count > _data.Length`, which OVERFLOWS `int` and goes NEGATIVE for
  a declared size near `int.MaxValue` — so the comparison came out false and the
  guard did the opposite of its intent: `ReadBytes` reached
  `new byte[0x7FFFFFFF]` (an `OutOfMemoryException` from a handful of file
  bytes) and `Skip` skipped its clamp and left a NEGATIVE cursor, which
  `ReadU8`'s `Position >= Length` test then accepts. **Write the bound as
  `count > Remaining`, never `Position + count > Length`.** And
  `AllegroLzssDecoder` now stops at `MaxDecodedBytes` (256 MiB): the loop
  consumes its input monotonically so the output was never infinite, but the
  format's worst case is ~8.5× and `ToArray()` doubles the peak, so a 200 MB
  crafted file cost ~3.4 GB. Both are pinned by hostile-input tests.
- **One gesture decodes an `.eof` ONCE.** `EofImporter` read and LZSS-decoded
  the whole file twice for a single import — once for the dialog's track preview
  (`GetAvailableTracks`) and again for the import itself
  (`ImportTracksWithMetadata`) — and the decoder is a byte-at-a-time
  sliding-window loop over a payload that expands ~8.5×. Both paths go through
  `EofDecodedFileCache` (bounded to 4 entries, LRU), keyed by **(full path,
  length, last-write ticks)** so a file rewritten under us re-decodes rather
  than serving a stale chart; the stamp is re-`stat`ed on every call. **The
  returned array is SHARED — treat it as read-only** (both callers hand it
  straight to `EofPackReader`, which only reads). `EofDecodedFileCacheTests`
  pins the saving, the invalidation, the eviction, and — by source lint — that
  the importer never decodes around the cache.
- **A first open is the WEM -> WAV decode** in `Parsing/Audio/AudioFormatConverter`
  (a cached open skips it). Since #426 the NATIVE decoder does it: JuceSharp's
  `JuceWemDecoder` (ABI v42, `dca_wem_*`) rebuilds the Vorbis headers and feeds
  JUCE's libvorbis directly, and its `Read(Span<short>)` applies the 16-bit
  formula every cached WAV was written with (clamp, -1 -> -32768, else
  round-half-even of `sample * 32767`), so `WriteNativeWave` writes the same
  header and length the old managed path did, every sample within 1 LSB.
  Measured on 8 of the owner's cached WEMs (Debug, Idle priority, CPUs 8-15):
  10.2 s -> 4.4 s, 2.31x. **The managed path is GONE (#431)**: Ww2Ogg.Core and
  NVorbis were removed after a sweep of the owner's whole library decoded every
  song's WEM both ways (2026-10-08, 1,901 archives, 2,378 songs: all 2,376 Vorbis
  WEMs aoTuV with mod packets, little-endian; identical length and onset on every
  one, max float difference 1.07e-6, 16-bit samples within 1 LSB, native 2.0x
  faster; the other two are DotCharter's own PCM / IMA-ADPCM exports, which
  neither decodes and vgmstream / ffmpeg still cover).
  Pinned by `AudioFormatConverterNativeTests` (the order through the seam, and a
  REAL `.wem` fixture, `Fixtures/tone-stereo48k-inline-mod.wem`, built from
  JuceSharp's synthetic `stereo48k.ogg`, decoded natively: header, length, and
  every sample equal to the formula over the decoder's own float output).
- **A library open's worker pass is `Library/LibraryLevelImport.PrepareForEditor` (#436)**:
  the hardest-level squash (Settings ▸ Files ▸ "Hardest difficulty level only", #399)
  plus the spacing pass, one arrangement per pool thread, between the parse and the
  apply. `LibraryLevelImportTests`.
- **The playable file is the `.wav`, never the Ogg (#425).** Measured: the engine
  opens a cached WAV in ~50 ms but decodes an Ogg on EVERY open (~360 ms).
  `TryConvertWemToPlayable` is THE one method that turns a WEM into that WAV: a
  fresh WAV wins; else the NATIVE decoder; then vgmstream, then ffmpeg. A `.ogg`
  an older build left beside the WEM is deleted unread (#431). The log says which
  won ("Decoded WEM to WAV natively" with its ms, or a WARN naming why the native
  decode failed). Every WAV goes through `TryWriteStaged` (a
  `<name>.part-<guid>.wav` beside it, moved into place when complete), because
  the freshness check is only `mtime >= wem` and would serve a truncated WAV after
  a crash. A cache folder is `<id>.wem` + `<id>.wav` (+ the store's markers and
  `stems/`); nothing reads a cached `.ogg`. Pinned by
  `AudioFormatConverterPlayableTests`.
- **The WAV's bytes do NOT key the song's stems (#426).** Two decoders write
  WAVs a few LSBs apart, and the WAV is evicted and re-decoded, so a library
  song's `.dcstems` is keyed on the `.wem` beside it (`StemCacheSource`; see
  [stem-separation.md](stem-separation.md)). Keep the `<id>.wem` + `<id>.wav`
  pair in one folder: that adjacency is how the key finds the WEM.
- **Convert-on-select (#425).** `Library/Prefetch/LibraryAudioPrefetcher`
  (DI singleton, behind `ILibraryAudioPrefetcher`) is told where the library selection
  is by `SongBrowserViewModel.PrefetchAudio` — the browser's `OnSelectedSongChanged`
  and Play Mode's song list both call it. 400 ms after the selection stops moving it
  runs `LibraryService.PrepareAudio` → the owning source's `PrepareAudio` (the audio half of `LoadSongAsync`) on the
  thread pool; the debounce, one-at-a-time and cancel rules are the pure
  `SettledSelectionRunner<T>` (`SettledSelectionRunnerTests`). A source's prepare
  takes a per-cache-folder gate, so Open WAITS for a prefetch converting its song and
  then finds its WAV; a cancelled prefetch leaves no partial file.
- **The decoded-audio cache is BOUNDED, and `Library/Cache/LibraryAudioCacheStore`
  is the only thing that names, stamps, measures or removes a folder in it.**
  Opening a Rocksmith song decodes its `.wem` into
  `%LocalAppData%/DotCharter/PsarcAudioCache/{psarcName}_{pathHash16}_{lastWriteTicks}/`
  (stems expand to `<folder>/stems`). Until 0.58 **nothing ever removed a byte**:
  127.6 GB across 2,543 folders / 7,762 files on the owner's machine, which was
  not key churn (~5 duplicate folders) but every song ever opened, kept forever.
  - **The naming scheme may not change** — thousands of folders are already on
    disk and a new scheme misses every one of them. `DirectoryNameFor` is the old
    the old PSARC service's `GetAudioCacheDirectory` moved verbatim.
  - **The last-write ticks in the key mean an updated or re-downloaded archive
    ORPHANS its old folder permanently.** Those go first, before anything still
    reachable, because they cannot be hit again at any cache size. Two signals:
    the `.psarcsource` marker (archive gone / timestamp moved) and, for the
    marker-less folders written before 0.58, SUPERSESSION — two folders sharing a
    path hash are two revisions of one archive and at most one can ever be named
    again.
  - **The last-use stamp is the marker's write time, NOT the filesystem's access
    time.** Windows ships with `NtfsDisableLastAccessUpdate` on, so a cache HIT
    leaves no trace; `PrepareForUse` rewrites the marker on hit and miss alike.
    An LRU fed only by misses evicts exactly the songs the user replays.
  - **The policy is the OPPOSITE of `StemCacheStore`'s and both are deliberate.**
    A stem archive is minutes of separation already paid for (its doc comment says
    adding a cap there would be a bug); a PSARC folder is a decoded WAV worth ONE
    decode. Cap: `DefaultMaxBytes` = 8 GiB (~160 songs at the ~50 MB per song
    measured above). The decision itself is `LibraryAudioCacheEvictionPlanner` —
    pure, injectable, no filesystem and no clock.
  - **Automatic eviction is OFF by default and the composition root is the only
    place that arms it** (`enableAutomaticSweep: true` in
    `App.EditingServices.cs`). A package source falls back to a store over
    the REAL cache when nobody hands it one, and a test that builds a package in a
    temp folder and loads it went through exactly that constructor — with the
    destructive default, one gate run pruned the owner's live cache to the cap. Writing to the real root was always harmless;
    deleting from it is not. Pinned by
    `LibraryAudioCacheStoreTests.A_store_nobody_opted_in_…` and
    `UI/LibraryAudioCacheReachabilityTests.Only_the_composition_root_arms_automatic_eviction`
    (the second half matters: a safe default with nothing asserting the opt-in is
    a dead feature instead of a destructive one).
  - Settings row: Files & backups tab, "Decoded song audio", beside the other
    file/storage cards. Its three readouts are STATUS and are not in
    `FilesSettingsViewModel`'s `Tracked` list — the `BackupStatusText` fault.
  - Tests: `Services/Parsing/Psarc/LibraryAudioCacheEvictionPlannerTests` (policy,
    filesystem-free), `…/LibraryAudioCacheStoreTests` (naming, marker,
    reachability, measure/clear/sweep over a temp root),
    `UI/LibraryAudioCacheReachabilityTests` (the whole wiring chain).
  - **A song's `<id>.wav` is converted ONCE to the DEVICE rate, in place (#435):**
    `Library/Cache/LibraryDeviceRateAudio`. The engine UPsamples a song below
    the device rate on every open (44.1 kHz Rocksmith audio on a 48 kHz
    interface: ~0.3-0.4 s of a ~0.5-0.7 s open). After a library song opens,
    `ProjectSessionService.LoadAudioFile` schedules `ScheduleConversion` at the
    engine's current rate in the background; the result REPLACES `<id>.wav`.
    - **What is stored is exactly what the engine holds:** `dca_convert_audio_for_device`
      (JuceSharp ABI v43) runs the engine's own decode, upsampler and 16-bit
      quantiser and writes its int16 samples plus the preload STEP in a `dcps`
      chunk (`DeviceRateWavHeader` reads it). Every open honours the step, so the
      engine holds BIT-IDENTICAL samples even for loud masters whose upsample
      overshot full scale (`LibraryDeviceRateAudioNativeTests`). 16 bits a sample:
      1.09x the 44.1 kHz original (the rate ratio), one file per song -- the first
      version kept a 32-bit float copy BESIDE the original, ~3.2x the bytes per song.
    - **Never a wrong-rate play:** `PrepareForOpen` runs before every library open.
      A file converted for ANOTHER rate (the device changed), or a damaged one, is
      re-derived from the song's `.wem` (the archive's own audio, extracted beside
      it) through `AudioFormatConverter.TryWriteStaged`, so the open converts at
      load as before; then the new rate's conversion is scheduled. A device that
      changes rate DURING the open gets the song re-derived and re-opened. The
      engine REFUSES a damaged device-rate file, and `RecoverAfterFailedOpen`
      re-derives it and opens again. Without its `.wem` the song is left alone
      (the next pick re-extracts the `.wem` from the archive).
    - **Only a song with its `.wem` beside it is converted** (`ScheduleConversion`): only
      that can be re-derived for another rate. A `.dcsong`'s cached `song.wav` (decoded
      Opus, #708) has none and is never replaced (`A_song_without_its_wem_is_never_converted`).
    - **Not while the stem cache needs the original:** `WemKeyAdoption` proves a
      pre-#426 stem archive by the original WAV's exact bytes, so the composition
      root passes `WemKeyAdoption.HasCompleted` and nothing is replaced before that
      pass is done.
    - **`LibraryAudioCacheStore` stays the only owner:** the conversion writes a
      `<id>.part-<guid>.wav` staging file in the song's folder, checks its header
      (a device-rate file at exactly that rate) and that the original did not
      change meanwhile, and renames it over `<id>.wav`; nothing here deletes a
      song. The song is still one folder `Sweep` / `Clear` size and delete whole.
      Files outside the cache are never touched. The stem cache keys on the
      `.wem` (#426), never on these bytes.
    - Tests: `…/LibraryDeviceRateAudioTests` (fake converter and re-deriver),
      `…/LibraryDeviceRateAudioNativeTests` (the real DLL on the Null device, a real
      `.wem`), `Services/Session/LibrarySongBytesReleaseTests` (which bytes an open
      reads, re-derive before and after a refused open).
- `Library/LibraryDedupe` — pure, deterministic collapse of byte-copy
  duplicate rows (same `SongKey|Charter|Version` under different file names).
  `CollapseByIdentity(entries)` and a generic `CollapseByIdentity<T>(items,
  entrySelector)` (both list surfaces dedupe their own wrapper type). Identity
  casing mirrors `LibraryDoctorSong.Identity`/the leaderboard key (trim +
  lower-invariant per component) plus the source kind (#708: a `.dcsong` beside the
  `.psarc` it was exported from is not a copy), so the collapse covers EXACTLY what
  the doctor flags. Winner = a PC package over a Mac one (`_m.psarc`), then the
  LEXICOGRAPHICALLY SMALLEST ordinal `PsarcPath` (stable across scans). The package
  reader loads both platforms (its guide says how), so PC preference is a tie-break.
  Never collapses
  rows that differ in any identity component (a multi-song pack survives intact)
  or blank-`SongKey` rows. Applied at the list surfaces ONLY —
  `PlayModeViewModel.RebuildFromBrowser` (FromPsarc rows) and
  `SongBrowserViewModel.ApplyFilter` (the visible `Songs`, never `_allSongs`).
  Tests: `LibraryDedupeTests`.

---

- **The song's title and artist in their ORIGINAL script round-trip as
  `ChartData.JapaneseTitle` / `JapaneseArtist` (2026-08-21).** Rocksmith carries
  exactly this pair (`JapaneseSongName` / `JapaneseArtistName`) and DLC Builder
  edits it; DotCharter had nowhere to put it, so a psarc round-trip threw it away
  in BOTH directions and the `.rs2dlc` model's `JapaneseTitle` /
  `JapaneseArtistName` — declared since the file was written — were filled by
  nothing. **There is no Japanese ALBUM name**: the manifest has no field for
  one, so storing it would be a value we could never write out (owner decision).
  Written to `.rscp` as `dcJapaneseTitle` / `dcJapaneseArtist`, **as ELEMENTS**:
  `RscpWriter.AddIfNotEmpty` writes an ATTRIBUTE, which is right for the
  arrangement nodes it serves and wrong in the header, where the parser reads
  `Descendants()` — the first cut of this did exactly that and round-tripped as
  empty until `JapaneseMetadataRoundTripTests` caught it. On IMPORT the package
  reader takes the pair from the first of the OPENED song's per-arrangement manifests
  (#436 — a multi-song archive's other songs' manifests are not read: that cost
  50-200 ms per open and could lend another song's names), deliberately NOT from the
  library index (a schema
  bump DROPS AND REBUILDS it, forcing a rescan of the whole psarc library for two
  display fields) and deliberately not folded into
  `BackfillFromArrangementManifests` (that pass skips a manifest it has nothing
  else to contribute to, so an arrangement that already had its tones would never
  be read).

## `Parsing/Rscp/` — the project format (moved here from `DotCharter.Services/AGENTS.md`)

- **The header is read through ONE indexed pass, not thirty tree walks (0.49).**
  `RscpHeaderParser` asked `doc.Descendants(name).FirstOrDefault()` thirty times.
  `Descendants(name)` is a lazy pre-order walk of the WHOLE document, so a name
  the file does not carry costs a full walk — and **11 of the 30 are absent from
  a real project** (`dcJapaneseTitle`, `dcJapaneseArtist`, `dcCharter`,
  `dcGenre`, `dcReleaseDate`, `dcCoverArt`, `dcKeyAccidentals`, `dcKeyIsMinor`,
  `dcBeatMapLocked`, `dcAnnotations`, `dcToneDefinitions`), while four more
  (`bookmarks`, `dcAnnotations`, `dcToneDefinitions`, `text`) are written AFTER
  `<arrangements>` and so step past every note, chord, hand shape and anchor
  before they find anything. `RscpFirstElementIndex.Build` answers all thirty
  from one pass that stops as soon as every requested name has been seen —
  12 ms → 1.4 ms on a 12,964-element project, 17 ms → 2.2 ms on another
  (measured 2026-08-29). It walks `doc.Descendants()`, the same document-order
  sequence, and keeps the FIRST hit per name, so a header name nested inside an
  arrangement still resolves exactly as it did.
  **`RscpHeaderParser.HeaderElementNames` is the ONLY set the index answers**: a
  name added to a `header.First(...)` call and not to that array reads as ABSENT
  rather than failing loudly — silent data loss on load and then on the next
  save. It is a hand-maintained parallel list, so
  `RscpHeaderIndexCoverageTests` source-scans the parser (comments stripped) and
  fails the default gate BOTH ways: a looked-up name missing from the array, and
  a dead array entry — the latter because a name nothing uses keeps
  `RscpFirstElementIndex.Build` from ever reaching its early exit, quietly
  turning the one pass back into a full-document scan.

- **The format PROMISE — what is frozen, what happens to unknown data, when the
  version bumps — is [`docs/DCPROJ-FORMAT-GUARANTEES.md`](../../DCPROJ-FORMAT-GUARANTEES.md),
  and its code half is `Parsing/Rscp/RscpFormatContract` (0.49).** Read it before
  changing anything about the wire shape. The short version:
  - A top-level element whose name is NOT in
    `RscpFormatContract.KnownRootElements` is a node a NEWER build wrote. The
    reader captures it into `ChartProject.UnknownRscpNodes` and the writer puts
    it back LAST, so an older build no longer deletes a newer build's whole
    feature. **Add every new root node to that set in the same change** — a name
    the reader knows but the set does not is written from the model AND
    re-appended as residue, i.e. one extra copy on every save
    (`RscpForwardCompatibilityTests` fails first).
  - **Unknown ATTRIBUTES and unknown DEEP nodes are still dropped, on purpose.**
    A `.rscp` note has no identity beyond position + string, so re-attaching a
    newer build's hint after an edit would hang it on the wrong note; corrupting
    is worse than losing. That is a promise in the doc, not a TODO.
  - `<dcWriterVersion>` is written on EVERY save and is the ONE unconditional
    `dc*` element (absence has to keep meaning "written before the stamp
    existed"). `RscpChartSerializer.Load` warns when it names a newer build. The stamp retains
    prerelease identifiers from `AppVersionInfo`; the warning uses the updater's
    SemVer precedence and ignores build metadata.
  - `chartFormatVersion` stays 3. It moves only for a structural relayout, with
    a migration; additive data never bumps it.

- **The `.rscp` IS the project, so a model field with no writer is silently
  DELETED on every save.** New (or restored) fields go in as ADDITIVE `dc*`
  nodes / plain attributes that are ABSENT at their default — a chart that never
  carried the field stays byte-identical, and an older reader ignores the tail —
  appended AFTER the rscharter field order. Write BOTH halves (`RscpWriter` and
  `RscpArrangementParser`/`RscpHeaderParser`) in the same change; a one-sided
  field is the exact bug being avoided. The corpus round-trip tests assert
  COUNTS only, which is how fourteen fields shipped as save-time data loss —
  five fixed in the first pass, and in 0.35 `Arrangement.Name` (the parser had
  ALWAYS read `name`; only the writer refused), `ChartProject.IsBeatMapLocked`,
  `ChartData.KeyAccidentals`/`KeyIsMinor`, `Stem.Muted`/`Solo`,
  `ChordNote.BassPicking`/`Ignore`, `ChartLevel.FretHandMutes` and the four
  per-arrangement metadata overrides. Pin every one with a field-level assertion
  (`RscpLossyFieldRoundTripTests`, `…Tests2`), never a count.

- **…and a field can be written and then dropped by the READER, which is the same
  data loss from the other end.** `RscpWriter.BuildVocalLyricsNode` emits one
  `<vocal>` per `LyricEvent` unconditionally, and `RscpVocalsParser` used to skip
  every node whose text was empty — so clearing a syllable's text in the lyric
  inspector (an ordinary, undoable `RenameLyricAction`) DELETED the syllable, its
  span and its `Note` on the next save, and changed the phrase's lyric count,
  which desynchronises the `n` counts `ApplyExplicitPhraseSpans` reads to put the
  explicit spans back. Fixed 2026-09-04: a node that CARRIES a text attribute is a
  syllable whatever the text says; a node with NO text attribute is a legacy
  rscharter spacer and is still skipped
  (`RscpVocalPhraseRoundTripTests.ASyllableWithClearedText_SurvivesTheRoundTrip`).
  **A skip-if-empty in a parser is a delete.**

- **…and the field-level version of that is HALF A PAIR, which no coverage test
  can see, because both halves it looks for are present.** `Stem.Muted` was one
  of the fourteen fixed in 0.35. Its opposite number — is the ORIGINAL mix
  silenced while the stems play in its place? — had no model field at all, so it
  was not "a field with no writer", it was a field with no FIELD, re-derived on
  load as "muted, because stems exist". Since the mixer's own doubling guard
  makes the two mutually exclusive, saving "I want the original, not the stems"
  (all stems muted) reloaded as EVERYTHING muted: a project that opens silent
  over a healthy engine, no exception, no log line, nothing in the UI. Fixed in
  0.49 by `ChartProject.OriginalMixMuted` + the `<stems originalMuted>`
  attribute. **When you persist one side of a mutually exclusive pair, ask what
  the other side is and where IT lives** — and note the attribute is written
  unconditionally, for the reason in
  [../../DCPROJ-FORMAT-GUARANTEES.md](../../DCPROJ-FORMAT-GUARANTEES.md).

- **…and PRESENCE is not FIDELITY, so there is a whole-graph value comparison
  too: `RscpDeepValueRoundTripTests`.** Every settable property on
  `ChartProject`/`ChartData`/`Arrangement` already appeared somewhere in the
  writers, which says nothing about whether the value that comes back is the
  value that went in. It builds one deliberately rich project (every collection
  populated, fractional positions with denominators the writer cannot reproduce
  by luck, bend curves, chord templates, phrase infos, tone changes, notation
  voices, annotations, stems, vocals) and walks the two graphs by REFLECTION,
  reporting every differing path at once rather than the first. **Two paths
  are excluded and each one names its reason in the source** —
  `ChartData.BeatsPerMinute` is vestigial (nothing reads it; the beat grid is
  the answer) and `HandShape.Name` is display-only. **"It did not round-trip"
  is not a reason to add a third.**
  `ChartData.Tuning` was a THIRD exclusion until 0.44.2, excused as "vestigial,
  and the package reader is the only reader". Both halves were false —
  seven production files read it for a lane count, and the package reader's
  back-fill guard could never fire — so it is DELETED rather than excluded now
  (see [../core/guitar-and-tuning.md](../core/guitar-and-tuning.md) § "A chart
  states its tuning on the ARRANGEMENT"). **That is the shape to copy: an exclusion whose
  reason has rotted is a field to remove, not a line to keep.**

- **…and the whole-graph comparison can only see a field the FIXTURE populated,
  so the fixture is linted too (0.46): `RscpFixtureCoverageTests`.** A property
  left at its default on both sides compares equal, so a newly added domain
  field that the writer never learned about passes `RscpDeepValueRoundTripTests`
  VACUOUSLY on exactly the day it needed to fail — the same shape as the vocal
  phrase pair below, which the fixture set to its own derived value. The lint
  walks the built graph by reflection and requires every settable property of
  every Core type in it to carry a non-default value on at least one instance
  (the union, because the fixture deliberately spreads coverage across two notes
  and two chord notes so the DEFAULT branch of each writer is exercised too). It
  found five holes on the first run: `ChartData.EditMode` and
  `ChartLevel.DifficultyIndex` were set to their own defaults, and
  `JapaneseTitle`/`JapaneseArtist` were unset — all four are now real values in
  the fixture, plus a second `ChartLevel` so the level ordinal is checked.
  Exclusions carry a reason and are asserted to still name a live property:
  `GuitarNote.GraceSlide` (import-transient, no format persists it),
  `ChartData.ChartFormatVersion` (stamped as `RscpMigrations.CurrentVersion`,
  never carried), `ChartData.SingleNotes`/`Chords` (the legacy flat store the
  `.rscp` does not persist at all), `Arrangement.StringCount` (coupled to the
  fixture's six-string tuning; the non-six case is
  `RscpTuningRoundTripTests.SevenStringTuningsSurviveToo`) and the 31
  `ArrangementProperties` technique flags (own exhaustive round trip, whose
  hand-written accessor list is itself now checked against the type by
  `RscpLossyFieldRoundTripTests.TheFlagAccessorList_NamesEveryTechniqueFlagOnTheType`).
  Values the format CANONICALISES — a beat's Bpm (re-derived from the gap and
  the denominator), a showlight's `Type`, a word-part lyric's trailing `-`, a
  tuning's letters and type NAME, a lyric phrase's end — are stated in their
  canonical form by the fixture instead, so the assertion stays an equality.

- **A writer condition that is not the parser's condition is the same bug in a
  subtler shape.** `ChordTemplate.ForceArpeggioInRs` was written only INSIDE
  `if (tmpl.Arpeggio)` while the parser read `forceRS` independently — and the
  one path that produces the flag,
  `ChartSpacingEnforcer.StripRsDisplaySuffixes` folding a `-arp` NAME suffix
  into it, leaves `Arpeggio` false (it is a separate user-settable property).
  The enforcer runs on EVERY save, so the marker was created and destroyed by
  the same save, and both the `-arp` suffix `RsXmlSongWriter` re-derives and
  `SngFromDomainMapper`'s `ChordMask.Arpeggio` went with it. Write each field
  under exactly the condition its reader uses.

- **A chord's frets live in its TEMPLATE on the wire — so an UNTEMPLATED chord
  needs the additive per-note `fret` attribute or it reads back as all zeroes
  (0.41).** `RscpArrangementParser.ParseChordNote` derives every fret from the
  parent chord's template; that is the whole reason an edit moving a fret must
  repoint `TemplateId`. `RscpTemplateManager.RegenerateTemplates` ASSIGNS
  templates on `arr.Levels` ONLY (a voice chord's id is merely renumbered with
  its template, #547 — see `editing-invariants/chord-templates.md`), so the
  sidecar **notation voices** (`Arrangement
  .NotationVoices`, GP voices 2–4) are never templated — and a multi-voice score
  import lost every voice-2/3/4 chord SHAPE on the first save, leaving the sheet
  strip drawing an open strum. `RscpWriter.BuildChordElement` now takes
  `writeFret`, which `BuildNotationVoicesNode` passes `true` and the parser
  mirrors by resolving sidecar chords against NO templates (a stale `TemplateId`
  copied in from a level chord would otherwise adopt an unrelated shape's frets).
  **Do not "fix" this by giving notation voices real `ChordTemplate`s** —
  `RsXmlSongWriter` and `SngFromDomainMapper` both write `arr.ChordTemplates`
  wholesale, so notation-only shapes would ship into the artifact the GAME reads.
  Every level chord that has chord notes is templated by the time the writer
  runs, so no level `chordNote` grows the attribute and a single-voice chart
  stays byte-identical. Pinned per STRING by `RscpNotationVoiceTests`
  (`SidecarChordFrets_SurviveASave_PerString`) — the file's original round trip
  asserted `ChordNotes.Count`, which two completely different shapes both pass.

- **A LYRIC PHRASE has no element on the wire at all — it is only the run of
  `<vocal>` entries up to a terminator — so everything about the phrase ITSELF
  was re-derived, and two things did not survive that derivation (0.45).**
  - There is exactly ONE legacy `flag` attribute and `WORD_PART` took it, so a
    syllable that was BOTH a word part and the last of its phrase wrote no
    `PHRASE_END` at all. `RscpVocalsParser.ParseLyricEvents` rebuilds every
    boundary from `PHRASE_END` alone, so that phrase never closed and SWALLOWED
    the next one — the phrase COUNT dropped and the swallowed phrase's own span
    was gone. The two states are independent now: the legacy flag is unchanged
    and the phrase end also rides an additive `dcPhraseEnd="true"`, written ONLY
    when `WORD_PART` took the flag slot, and read alongside the legacy value.
    That is the writer-condition-is-not-the-reader-condition shape again.
  - `LyricPhrase.Position`/`EndPosition` had NO writer (the parser re-derived
    them from the first/last lyric) and a phrase with ZERO lyrics emitted
    nothing and was deleted. The additive `dcPhrases` node — one `phrase`
    element per phrase of the track, in order, carrying `p`/`ep` and `n` (the
    lyric COUNT, so the empty ones land between the right neighbours) — is
    written only when some phrase in that track cannot survive the derivation,
    so a plain rscharter chart stays byte-identical; the derivation is the
    fallback whenever the node is absent. A lyric flagged `IsPhraseEnd` BEFORE
    its phrase's last one is written as `PHRASE_END` and so splits the derived
    phrases; the writer then always emits the node, and the parser REGROUPS the
    lyrics by the `n` counts when every entry has one and they add up to the
    lyrics that parsed (#283), keeping the old one-entry-per-derived-phrase
    mapping for a truncated or foreign file. `ChartProjectRestorer` carried both
    all along, so UNDO preserved what a save destroyed.
  - `RscpDeepValueRoundTripTests` was blind to the second one: its fixture sets
    `phrase.Position` to the first lyric's position and `EndPosition` to the
    last lyric's end BY CONSTRUCTION, which is exactly the derived pair.
    `RscpVocalPhraseRoundTripTests` pins both, plus the two byte-identity
    guarantees.

- **The difficulty ladder: the FILE is sparse, the CHART is dense — and
  `Export/Rocksmith/RsLevelLadder` is the only converter (#323).** In an RS2014
  XML or SNG, level N carries only the phrases whose `maxDifficulty` reaches N
  (measured: on 216 base-game arrangements and the EOF DD exports in
  `TestData/EOF`, nothing sits above its phrase's max, and the top level of a
  29-level Lead holds one phrase of 32). In the chart every `ChartLevel` is the
  whole song at that difficulty, so the HARDEST level is the whole song at its
  hardest (`DotCharter.Core/Utilities/ChartLevels.Hardest`; `Levels[0]` is the thinnest
  reduction, never "the chart").
  - **Import, both paths:** the package reader keeps EVERY SNG level (it used
    to build ONE merged level, which made Live Play's dynamic difficulty inert on
    every imported song and stripped the ladder on re-export), and
    `RsXmlSongReader.ReadLevels` keeps every `<level>`; both
    then call `RsLevelLadder.FillInheritedPhrases`, which fills each level above
    a phrase's max with CLONES of that phrase's max-level content (what the game
    plays there). The fill slices by POSITION; on the base game it agrees with
    the SNG's own iteration ids on every level the plugin's owner-only fidelity
    test reads.
  - **Opt-out (#399):** Settings ▸ Files ▸ "Hardest difficulty level only"
    (`AppConfig.PsarcOpenHardestLevelOnly`, off by default) makes the song
    browser's load squash every arrangement to one level
    (`Library/LibraryLevelImport.KeepHardestOnly` over `LevelSquashService`)
    before the spacing pass. No note is lost, because the hardest level is the
    whole song, but a re-export then ships one level. The chord templates only
    the dropped levels played go with them (#634), by the editor squash's rule
    (`LevelTemplateOrphans` Capture/Apply, ids renumbered and users remapped; a
    named template survives). Pinned by
    `LibraryLevelImportTests` and `SongBrowserAudioTests.LoadSongItem_KeepsOnlyTheHardestLevel_WhenTheSettingIsOn`.
  - **Export, both writers:** `RsXmlSongWriter.Write` and
    `SngFromDomainMapper.BuildInstrumental` read levels ONLY through
    `RsLevelLadder.ForExport`, which leaves out every span above its phrase's
    SHIPPED max. The shipped max is the authored one raised to the highest level
    whose content in the phrase is non-empty and differs from the level below
    (`ChartActionStateComparer` equality) — so an unedited import re-exports
    its own ladder, and a phrase a charter edited on the hardest level ships
    with its max raised and every level up to it intact. A sparse chart (EOF
    import, older project) or an app-generated ladder (every max at the top)
    exports exactly as before. Pinned by `RsLevelLadderTests`.
  - **A max of 0 is a real value, and "no `PhraseInfo`" means FULLY levelled**
    (`LiveNoteMatcher`, `LevelSquashService`, the ladder). So the XML reader
    stores a `PhraseInfo` for EVERY `<phrase>` (it used to skip max 0) and the
    `.rscp` writer writes `maxDifficulty="0"` whenever an info exists (a file
    without the attribute still round-trips byte-identical) —
    `RscpChartSerializerRoundtripTests.A_phrase_max_difficulty_of_zero_survives_a_save`.
  - **Cost, measured 2026-10-07** on the densest base-game arrangement (27
    levels, 12,778 SNG notes): 30,915 chart sounds against 1,709 for the old
    merged level; convert 67 ms (was 7), 10.2 MiB retained (was 0.6). Whole
    base game (216 arrangements): 2.95 s and 1.0 GiB allocated (was 0.57 s and
    120 MiB). Re-measure; do not carry these forward.
  - **Known limit:** `EofExporter` still writes every chart level as an EOF
    difficulty, copies included (EOF has no phrase max to sparsify against).

- **RS-XML import gives back EOF's split chords (#609, #621, `Xml/Song/RsXmlSplitChordMerge`).**
  EOF writes a split chord as one `<note>` per string with no chord template and
  no hand shape (`rs.c:2082-2124`, `:219`). EOF's own RS import merges EVERY
  pair of notes at one timestamp (`rs_import.c:1650-1655`), and so does
  RsCharter's, but EOF also writes simultaneous notes for chords it breaks up
  for other reasons: a link landing (`rs.c:2010`), an arpeggio phrase
  (`rs.c:2300-2345`), a chordify chord (1 ms after its chord, `rs.c:2135-2210`)
  — and every one of those keeps a hand shape. **The rule:** two or more notes at
  one time on different strings, no chord of the level at that time, inside NO
  hand shape, and no note a link target (the sound before it on that string has
  `linkNext`). Measured 2026-10-10 on the 35 `TestData/EOF/*/notes.eof` against
  the `*_RS2.xml` EOF wrote beside them: 240 simultaneous-note groups, 237 link
  landings (175 inside a hand shape), and the 3 that are neither are exactly the
  3 split chords the `.eof` files hold. The chord plays a declared non-arpeggio
  template of exactly those frets, else a nameless, fingerless mint (the pick
  scrape's and RsCharter's mint). Two save-time gates stay (all or none of the
  strings fret-hand muted; a derived ignore only where every string is already
  ignored), and `RsXmlSongWriter` must still write the arrangement back as the
  same document — a level that differs keeps its notes, anything else differing
  un-merges everything (e.g. a declared template nothing else plays would turn
  split-only and drop out). Runs for an IMPORT only —
  `RsXmlSongReader.MergeSimultaneousNotes`, set by `RsXmlImporter` and
  `XmlChartSerializer`, NOT by `RscpMigrations`. On the 89
  `TestData/EOF/**/*_RS2.xml` it merges 3 split chords and every file writes back
  byte-identical, before and after `ChartSpacingEnforcer`
  (`RsXmlSplitChordMergeTests`). **Known limit:** a split chord under a hand shape
  someone drew, or one a link lands on, looks exactly like EOF's broken-up
  ordinary chord and comes back as loose notes.
