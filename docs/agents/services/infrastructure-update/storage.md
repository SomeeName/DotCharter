# `Infrastructure/` + `Update/` — config, backup, crash reports, library doctor, auto-update (deep reference) — `Infrastructure/Storage/`: three footguns with their own helper

## `Infrastructure/Storage/` — three footguns with their own helper

- **`AtomicFile` is the ONLY durable-state writer, and its swap is a POSIX
  rename first, `File.Move` second** (`Infrastructure/Storage/WindowsPosixRename`,
  2026-08-15). The v0.31 belief that a `FileShare.Delete` reader
  (`AtomicFile.ReadAllText`) lets `File.Move(overwrite: true)` through was
  measured FALSE — `MoveFileEx` is denied by ANY open handle on the destination
  — so a save colliding with our own load only ever won in the reader's
  open/close gaps, and under load lost all 16 retries
  (`UnauthorizedAccessException` out of `ReplaceWithRetry`; that was the
  "torn-file" test flake, which never tore a file). The POSIX-semantics rename
  (`FileRenameInfoEx`, `REPLACE_IF_EXISTS | POSIX_SEMANTICS`) replaces a
  destination held by share-delete handles — they keep reading the old bytes
  whole — and falls back to `File.Move` on any failure (non-NTFS, older
  Windows, a holder that did not share delete), whose exceptions the bounded
  retry still absorbs for AV/indexer holds. Pinned by `AtomicFileTests
  .ReplaceWithRetry_SucceedsWhileAShareDeleteReaderHoldsTheDestination` and
  `.WindowsPosixRename_IsRefusedByAHolderThatDoesNotShareDelete`. **Read a
  file `AtomicFile` writes through `AtomicFile.ReadAllText`/`ReadAllBytes`** —
  it is the pair that makes the collision a non-event, and
  `EveryAtomicFileWriter_ReadsItsOwnFileThroughAtomicFileToo` lints it.
  **The XML half of that pair is `SafeXml.OpenRead`** (2026-09-04), which is how
  every project/chart document is opened — `SafeXml.LoadFromFile` used a plain
  `File.OpenRead`, so a load overlapping a save of the same `.rscp` burned the
  writer's whole retry budget and past it DROPPED the write. Pinned by
  `SafeXmlShareModeTests`.
  **And a swap that gives up SWEEPS its stage** (same date): `ReplaceWithRetry`
  deletes the temp before rethrowing, and `ProjectSessionService` deletes its
  own `<path>.saving` when the serialize threw before ever reaching the rename.
  A leftover stage is dead weight — nothing reads one back, the destination
  still holds the previous version — and one AV hiccup used to leave
  `MySong.rscp.saving` beside `MySong.rscp` for ever, looking like a newer save.
  `AtomicFileTests.AFailedSwap_ReportsAndLeavesNoStagedFileBehind` +
  `.ReplaceWithRetry_ThatGivesUp_SweepsTheStageItWasHanded`.

- **A new project's folder is resolved through
  `Infrastructure/Storage/NewProjectFolder`, never `Directory.CreateDirectory`.**
  All three create-project flows (New Project, EOF import,
  opening a `.dcsong` as a project) DERIVE the folder name from artist + title, so the same song yields the same
  name every time — and `CreateDirectory` is a no-op on an existing folder, so
  the working-audio copy and the `SaveChart` that follow landed straight on the
  earlier import's `project.rscp` and `song.*`. Re-importing a song you had
  already imported destroyed it, with no prompt and no backup. The helper counts
  up (`… (2)`, `… (3)`) and treats an existing but EMPTY folder as free (users
  pre-create the destination in the picker, and there is no data behind it).
  Pinned by `NewProjectFolderTests`, including a source lint that all four flows
  still route through it.

- **An untrusted zip is expanded ONLY through
  `Infrastructure/Storage/BoundedZipExtractor`, never
  `ZipFile.ExtractToDirectory`.** Several paths take archives from outside —
  `PlayerProfileService.ImportProfile` (whose bundle any session member can
  relay), the **app-update apply path** (`UpdateInstaller.ApplyDownloadedUpdate`,
  which every apply path funnels through), the
  **update-archive validation** (`AppPayload.ValidateArchive`, whose
  `archive.Entries` enumeration is the materialise-per-member cost this extractor
  exists to avoid) and **config-backup import** (`ConfigBackupService.Import` — a
  backup archive is a file the USER received from somewhere: a second machine, a
  cloud drive, a friend) and the **lyric-model install**
  (`Audio/Lyrics/LyricModelInstaller`, the sixth). The last three were added in
  0.41 (see the `Update/` rules above); the first two used
  to decide on numbers the SENDER wrote.
  **The rule is now a source lint** (0.45):
  `BoundedZipExtractorTests.NoServiceCodeExpandsAnArchiveOutsideTheBoundedExtractor`
  bans `ZipFile.ExtractToDirectory` anywhere in `DotCharter.Services` and
  allowlists the one `entry.ExtractToFile` site BY PATH with the reason it
  cannot traverse (`StemArchive` — this app's own `.dcstems` cache, `entry.Name`;
  the CUDA wheel extractor that was the second went with the CUDA runtime, #151).
  A seventh path has to be argued for there. It exists because both previous
  breaches read perfectly reasonably at the call site.
  **Zip slip covers ROOTED names, not just `..`** — `Path.Combine` silently
  DISCARDS its first argument when the second is rooted, so `C:\Windows\…`,
  `/x`, `\x` and `\\server\share\x` reach past a destination without a single
  `..` in them. `ExtractBounded`'s one `StartsWith(prefix)` refuses all of them,
  and the prefix carries a trailing separator so `…/outsider` cannot pass for
  `…/out`; both are pinned now (`ARootedOrTraversingMemberIsRefused`,
  `ASiblingDirectoryWithTheSameNamePrefixIsRefused`) — they were not. A member
  can never be a SYMLINK: every member is written through a plain `FileStream`
  and .NET's zip reader has no link concept.
  **`ConfigBackupService`'s zip-slip and
  allowlist guards were already correct and are unchanged** — they still decide
  WHICH members land where, via `ExtractBounded`'s `selectRelativePath` hook
  (returning `null` skips a member and records it, instead of refusing the whole
  archive), while the extractor decides how many bytes may leave the inflater. Do not
  reintroduce a `Entries.Sum(e => e.Length)` guard: that sum is CHECKED, so a
  zip64 archive overflows it into an `OverflowException` no call site catches,
  and merely reaching `Entries` materialises one object per member (a 200 MB
  archive of empty members costs millions before any per-entry check runs).
  The extractor counts the bytes as they leave the inflater and aborts
  mid-member, and bounds the member count straight from the
  end-of-central-directory record before the archive is opened. It does NOT
  exist because an under-declared member can smuggle extra bytes past
  extraction — measured on .NET 10 it cannot, the runtime hands the declared
  size to `DeflateStream` and the inflater stops there
  (`BoundedZipExtractorTests.AnUnderDeclaredMemberIsTruncatedByTheRuntime`).
  Callers delete their partial output on refusal.
  The `.dcsong` reader (`Package/DcSong/DcSongPackage`) READS members and
  expands nothing, so it is not on this list; its own caps and checksums are in
  [../dcsong-format.md](../dcsong-format.md).

## User folders: `UserDataFolders` is the only way to `%APPDATA%` / `%LOCALAPPDATA%` (#439)

- **`Infrastructure/Storage/UserDataFolders`** (in `DotCharter.Tone`) is the ONE
  place product code asks where the per-user folders are: `RoamingAppData`
  stands in for `GetFolderPath(ApplicationData)`, `LocalAppData` for
  `GetFolderPath(LocalApplicationData)`. `WritablePaths` (config, logs), the
  PSARC audio / stem / waveform / album-art caches, the stem, lyric and NAM model
  folders, the AI-match stores and the silent-audio cache all start from them;
  `DotCharter.Rendering` cannot reference Tone, so `DotCharter.UI.Program` hands
  `VulkanPipelineManager.PipelineCacheDirectory` its folder (unset, the
  pipeline cache stays in memory).
- **`DOTCHARTER_DATA_ROOT=<dir>`** moves both under `<dir>\Roaming` and
  `<dir>\Local` (created on demand), and `WritablePaths` then ignores a
  portable `appconfig.json` beside the install and never falls back to the
  install folder or temp. Unset, every path is exactly what `GetFolderPath`
  returns. `GetFolderPath` ignores environment variables, which is why this
  exists: a test launch could not be pointed anywhere else, and one re-keyed
  and then deleted the owner's stem archives. `App` logs one
  `Data root override: …` line when it is active.
- `OtherProgramsLocalAppData` is the REAL `%LOCALAPPDATA%`, never redirected, for
  FINDING another program's per-user install (`Programs\Common\VST3`, the
  Codex CLI) — read-only.
- Enforced by `Infrastructure/UserDataFolderLintTests` (comment-stripped: a
  direct `SpecialFolder.(Local)ApplicationData`, `GetEnvironmentVariable("APPDATA")`
  or `%APPDATA%` expansion anywhere but the resolver fails), and
  `Services/Infrastructure/UserDataFoldersTests` resolves every such path under
  a test-scoped root (`UseDataRootForTesting`, an `AsyncLocal`) and keeps its
  list in step with the files that use the resolver: a new call site fails it
  until it is listed.
