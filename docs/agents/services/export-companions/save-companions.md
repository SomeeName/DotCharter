# Export: Wwise-free audio + the save companions — save companions (SaveCompanionService)

## Save companions (`SaveCompanionService`)

`Infrastructure/Storage/ProjectFolderLayout` owns the layout (#725):

- `export/`: `<project>.rscp`, `<project>.eof`, `<project>.rs2dlc`, `DTRS XML/`,
  and the artist/title/version-named package. Export dialogs default here for saved projects,
  even when another project exported elsewhere. A per-project dialog draft wins;
  `LastExportDir` is only an unsaved default and the Browse picker's start folder.
  `Export/Rocksmith/RocksmithPackageName.For` supplies the filename to the companion,
  dialog, version rename and test export. Releases use `<Artist>_<Title>_v<Version>_p.psarc`;
  extended PC packages insert `_rsxr` immediately before `_p.psarc`. Mac uses `_m.psarc`
  and never carries `_rsxr`. Sanitization and authored artist-sort preference stay shared
  with `RocksmithReleaseName`. A version edit follows the derived name; a chosen path stays.
  After successful publication, unchanged owned older release versions and the other
  range variant are recycled, including packages written by a previous service instance.
  A foreign or edited destination is never replaced by the automatic companion.
- `export/song.ogg`, `song_preview.ogg` and `cover.png` are portable references.
  `ExportCompanionMedia` materializes them; the preview starts at the project's
  preview position. `ExportAssetLink` copies direct user `.ogg`/`.png` sources;
  only DotCharter-rendered cache files use the existing Windows hard-link helper,
  with a copy fallback and atomic publication. Editing export media cannot alter
  the user's source. Cached transcodes live under the
  metadata folder, so removing `export/` loses no source data. Charter companions
  carry their chart internally, without external XML or stem references.
- `.dotcharter/` has the Windows Hidden attribute. It holds `companions.json`,
  `ai-chats/`, `test-packages/` and `cache/`. `ManagedAudioCache` gives each source one WEM slot;
  codec detection and content/decode-policy receipts decide reuse. Every preview
  WAV is here too. `backups/` and `stems/` remain visible and unchanged.
- `Session/ProjectFolderTidy` runs on open, without a prompt. It imports legacy
  ownership, moves hash-matching companions and XML referenced by a stamped
  `.rs2dlc`, validates old chat identities, and recycles exact legacy
  cache/removed-feature names. Known automatic-name and valid authored-project-key PC/Mac/extended packages
  move into `export/` under the new release filename, without recycling or overwriting.
  These exact legacy-key packages are stamped after the move so the next save replaces them.
  Original `.rscp.bak` / `.rscp.N.bak` bytes move to unique `backups/` names.
  Preservation uses the non-replacing `AtomicFile.MoveAside` mode, so a destination
  created after name selection also wins without losing either file.
  Generic `<stem>.wem`, `<stem>.preview.wem` and `<stem>.preview.wav` require
  ownership stamps too: another tool can produce those names. Unverifiable
  files stay. `ProjectTidyNotices` persists one notice per candidate in
  `.dotcharter/tidy-notices.json`; locks are still retried on the next open.
  A pending XML move retains its legacy
  `.rs2dlc` reference for that retry. A successful
  tidy schedules detached regeneration of the portable companions. All removals
  use `Infrastructure/Storage/RecycleBin`. Its native probe requires a plain local
  fixed NTFS path, explicit enabled/quota settings, current bin usage and room for
  the complete candidate. Missing settings, policy vetoes, cloud/reparse paths,
  uncertain sizes and oversized items are left and logged once; there is no UI.
  Explorer's per-volume registry schema is an implementation detail, so unknown
  schemas/defaults never qualify. `OwnedDlcBuilderBundle` verifies removal before
  reusing a staging directory. Unsupported/skipped chat deletion returns a refusal.
  `RocksmithBuildOptions.ProjectDirectory` fixes cache ownership to the project,
  including external package destinations; raw callers without it use the source root.
- `ProjectExportAudio.ForSnapshot` is the single working-audio seam for #726.
  The dialog defaults through this seam too; a missing working file never
  selects a sibling WEM/WAV by its name. This change does not pad audio or alter playback.

Test filenames use `<Artist>_<Title>_test_p.psarc` with the game closed and
`<Artist>_<Title>_test_v<N>_p.psarc` while it runs; range/platform suffixes follow
the release rule. The key, title suffix ` vN` and internal version `test` keep their
existing behavior. `RocksmithTestExport` recognizes both new names and old lower-case
key names for numbering and identifying cleanup candidates. Cleanup requires a matching
project receipt in the selected test folder.
`RocksmithTestPackageOwnership` keeps exact paths and hashes in the saved project's
`.dotcharter/test-packages/<projectId>.json`, even for an external game folder.
Every final platform is checked immediately before the staged package set commits.
Only successfully approved/published paths are stamped. Cleanup follows a durable publication
receipt, rechecks the bytes of candidates with receipts, excludes current outputs and uses the
guarded Recycle Bin. Failed recycling retains the candidate for retry. Unsaved projects have only
same-instance transient receipts and never clean earlier packages. The legacy build helper
without an ownership context retains its superseded arguments.

`SaveCompanionServiceTests`, `ProjectFolderTidyTests` and `ExportAssetLinkTests`
cover the layout, ownership, locks, links/copies and a complete save/export/chat
folder listing.

Every save writes derived artifacts in the chart folder's `export/`: a Charter-compatible
`.rscp`, the RS2014 XML bundle, a project-named DLC Builder `.rs2dlc`, an Editor on
Fire `.eof`, a Rocksmith `.psarc`. (The CustomsForge upload plan and its browser fill script
were removed in #866: the site's form changes too often to keep a filler in step.)

**They are derived, and none of them may fail a save.** The `.dcproj` is written
atomically *before* any of them runs, so a companion exception must be caught —
otherwise `SaveProjectCoreAsync`'s outer catch reports "project save failed" for a
project that saved perfectly.

Measurement stays at the existing points: attachment (`AttachmentVolumeMeasurement`),
new-project creation, export-dialog prefill, and the companion's own detached
`SaveCompanionService.VolumesForAsync` after the project write. The psarc measures
the actual main source and its own generated clip, so a gain from an explicit
dialog preview cannot be reused for different audio. Current signatures are
checked before decoding; failures have no settled-input memo and may retry once
on the next save. Alternate encodes use `SaveCompanionService.ResolveAudio`.

`Export/Audio/ProjectVolumeAdoption` captures only one queued work item's guard.
After detached work, including an encoder failure after successful measurement,
it checks source identity on the worker and unchanged live inputs on the UI thread.
Automatic fields land without a dirty mark or undo action; the next actual
save/Save As/backup/recovery write persists them. The first project/cheap companion
write may carry old metadata; the psarc uses its freshly measured own-source gains.
No writer waits for measurement and no late result mutates a serialized snapshot.
With the psarc companion disabled, saves add no new measuring point; attach and
export remain the existing opportunities. Manual overrides always win.

The export command joins a pending prefill for its current audio/preview/start
inputs before capturing build options, under the existing building state and
cancel token. Changed inputs start fresh work. Completed or failed tasks are not
treated as settled inputs; measurement consults current file signatures again.

Both exit paths drain the queue through `ProjectSessionService.DrainSaveCompanions`
or session disposal. Hard termination calls the drain before killing the process,
without disposing in-process VST instances. `DetachedCompanionBuilder.Stop` cancels
the psarc and waits within its existing bound for queued cheap companions. Status
dispatch waits also cancel, so a worker cannot depend on the closing UI thread to
finish draining. Covered by `DetachedCompanionBuilderTests` and
`RecoveryCleanShutdownWiringTests.Hard_shutdown_drains_companions_before_killing_the_process`.

- **The Charter `.rscp`** (`SaveCompanionService.WriteCharterProject`) — the
  first companion, written right after the `.dcproj` swap as `export/<project>.rscp`. It is
  the same `RscpWriter` document cut down by `Export/Charter/CharterRscpProjection`
  to `CharterProjectSchema`: a tree of every element and attribute Charter's
  XStream model reads, transcribed from Charter V0.22.44 (`ChartProject.java`,
  the model classes it includes and the converters in `io/rsc/xml/converters`,
  each named in the schema's doc comment). Everything else — `dc*` nodes, the
  extension attributes on known elements — is dropped from the companion only
  and logged once per changed loss. **When Charter's model changes, re-read
  those files and update the schema with the version.** It is Charter's own
  project format, so it goes through `CompanionOwnership` (below): a Charter
  project at `<name>.rscp` is never overwritten. A session opened from a `.rscp`
  moves to the `.dcproj` on its first save, and an older DotCharter `.rscp`
  there is renamed to `.rscp.bak` BEFORE this runs, so the companion takes its
  name. Pinned by `CharterRscpCompanionTests` (allow-list, re-import,
  ownership, log-once); whether Charter opens it is a manual check.
- **The DTRS XML bundle** (`ProjectDocumentService.SaveDlcBuilderXmlBundle`) — written
  to a `DTRS XML.saving` staging folder (each output is stamped as produced),
  then swapped in by **renaming** the old
  bundle to `DTRS XML.old` and renaming staging into place (`SwapInBundle`). Never
  `Directory.Delete(export, recursive)` first: a recursive delete is not atomic, so
  one locked file half-deletes the bundle and the cleanup then removes staging too —
  both copies gone. A rename is all-or-nothing, so the worst case is a leftover
  `.old` (cleaned up on the next save) beside an intact previous bundle.
  **A 7-/8-string guitar goes into the bundle with every string by default** and as its
  six-string projection (#911) when `AppConfig.RsXmlSixStringProjection` is on (a stock
  DLC Builder's SNG build indexes six-wide arrays). The writer is
  `RsXmlSongWriter { SixStringProjection = config.RsXmlSixStringProjection }`; each projected
  arrangement that dropped something logs one warning (counts of notes and chords). The
  `.rs2dlc` follows the same setting: off, a 7-/8-string guitar's tuning lists every string in
  positional order (the XML's `string0`..`stringN`); on, its six slots are the six STOCK strings
  in package order (`Rs2DlcProjectBuilder.TuningVector` through `Rs7StringLayout.ToDomain`),
  not the chart's first six. An extended bass uses positional offsets, padded to six slots for a five-string part. Refused arrangements have neither bundle XML nor a `.rs2dlc` entry.
- **`.eof`** — a plain binary write (a few ms), queued with the other companions
  after every save. **EOF resolves its audio relative to the folder the `.eof`
  lives in**, and `EofExporter` names it `<audiostem>.ogg`. So the companion also
  prepares `export/song.ogg` through the shared media writer (content-cached,
  via ffmpeg when conversion is needed). Without it EOF prompts for missing
  audio. The encode writes a unique `.writing.ogg` in `.dotcharter/cache/`, then
  atomically replaces the cached Ogg only
  after success, and recycles failed staging files. A timeout, failure or exception
  preserves the previous audio and its timestamp, so the next save can repair it.
  Audio resolution validates the reference with `ProjectRelativePath` before any
  probe and uses `ProjectAudioLocator` for the declared reference only. A missing
  working file never selects a same-stem alternate encode, including on automatic saves.
  Refused UNC, device, drive-relative and network-drive references are never probed.
  Pinned by `SaveCompanionServiceTests`.
- **`.psarc`** — converts every arrangement to SNG and copies the whole audio
  track. It runs **detached, after** the save returns, so it never puts seconds of
  latency on Ctrl+S. Builds **coalesce**: a save landing while one is in flight
  replaces the pending snapshot rather than queueing a build, so holding Ctrl+S
  does not build a backlog. It **stages to a temp file and swaps**, so a build
  cancelled by a newer save (or by shutdown) leaves the previous psarc intact
  rather than truncated.
  A successful release from the Rocksmith export dialog to the exact companion
  path records the same ownership stamp, so subsequent saves can refresh it.

- **`.rs2dlc`** (`Export/DlcBuilder/`) — the DLC Builder PROJECT file, written
  to **`export/`**, named after the project file and naming the bundle's
  arrangements through `DTRS XML/`. It is what makes the folder open directly in
  DLC Builder and what gets dropped onto CustomsForge Ignition4's own "Import
  rs2dlc" control (which autofills the metadata it carries, with no code of ours
  running in a browser).
  **It must NOT be written inside `DTRS XML/`**: that folder is swapped in by
  renaming the old one to `DTRS XML.old` and renaming staging into place, so
  anything written inside it is moved aside on the next save and recycled on the
  one after. It still runs **after** `SaveDlcBuilderXmlBundle` and refuses when
  the bundle is absent, because a project file naming XML that does not exist
  reads in DLC Builder as an EMPTY project rather than as a missing file.
  **The same project is also written on request** by `DlcBuilderProjectWriter` (the Rocksmith
  export dialog's "Export DLC Builder project"): the bundle and the `.rs2dlc` into a folder the user
  picked, regardless of the companion setting, replacing what is there. Both writers take the file name,
  the vocals/charter fallbacks and the audio / cover references from `DlcBuilderProjectFile`.

### `ExportIncludeVocals` has ONE reader: `Export/VocalsExportPolicy`

**Every export artifact honours the flag, and none of them reads it directly.**
Until 2026-09-04 the psarc/SNG path was the only one that did — it consumed
`RocksmithBuildOptions.IncludeVocals` — while the `.rs2dlc`, the DTRS XML
bundle and the `.eof` each rebuilt the "which vocal
tracks count" test for themselves (`track.Phrases.Any(p => p.Lyrics.Count > 0)`,
spelled four times) and consulted no setting at all. A charter who turned vocals
off still got lyric XML in the bundle, Vocals arrangements named in the
`.rs2dlc`, PART VOCALS filled in the `.eof` and an upload plan advertising
lyrics the psarc did not contain.

`Export/VocalsExportPolicy` is now the one decision: `IncludeVocals(config)`
(a null config keeps `AppConfig`'s own default, so a missing container entry
never silently DROPS vocals), `HasLyrics(track)`, `ShouldWrite(track, include)`
and `TracksToWrite(project, include)`. `SaveCompanionService` sources the flag
once per companion; `ProjectDocumentService.SaveDlcBuilderXmlBundle` resolves
`IConfigurationService` from its provider for the same answer.
`VocalsExportPolicyTests` covers each writer AND lints that no file outside the
policy (and the export dialog, which WRITES the checkbox) names
`ExportIncludeVocals`.

**A package carries ONE Vocals arrangement** (#283): the first lyric-bearing
track, `VocalsExportPolicy.PackageTrack`, read by the psarc, the `.rs2dlc` and
the `.eof` alike, because Rocksmith plays a single Vocals arrangement (the
`.rs2dlc` used to name every lyric-bearing track). The DTRS XML bundle still
writes a file per track: it is the charter's source folder, not a package.

### Three companions are somebody else's project file — never clobber them

**The `.rscp`, the `.rs2dlc` and the `.eof` are the NATIVE PROJECT FORMATS of
Charter, DLC Builder and Editor on Fire.** The charter opens them there, edits them, and saves. Everything
they add lives in fields DotCharter does not author and cannot round-trip. The
worst of it was TONES: `Rs2DlcProjectDto.Tones` and the per-arrangement
`BaseTone`/`Tones` were always written **empty**, so rewriting the file was not
a merge that lost them, it was a blank that replaced them. Owner report,
2026-08-27: hand-assigned tones gone after a Ctrl+S made for an unrelated
reason, with nothing in the UI to say so.

**The tone list is POPULATED as of 2026-09-16** (the W6 tone generator).
`Rs2DlcProjectBuilder.BuildTones` emits one entry per
`ChartProject.ToneDefinitions` body — the definition's `Json` already IS DLC
Builder's tone object (`RsToneJsonWriter` writes the manifest shape: `GearList`
keyed by slot name, per-gear `Type`/`KnobValues`/`Key`/`Category`, knob keys
`<GearKey>_<Pot>`, `Volume` as a 3-decimal invariant string), so it is parsed
and handed to the serializer verbatim rather than re-modelled. The
per-arrangement `BaseTone` carries the base and `Tones` carries the four
switchable slots (the base is not repeated). It stays `[]` only when the project
owns no tone definitions. **It was empty before because the app had no tone
bodies to put there — not because the field is optional**: a project naming a
`BaseTone` with `Tones: []` is one DLC Builder cannot build. The shape is pinned
against a tone and an arrangement copied verbatim out of a DLC-Builder-saved
project, in
`DotCharter.Tests/Services/Export/Fixtures/dlcbuilder-tone-and-arrangement.json`
(`Rs2DlcToneBodyTests`). **None of this weakens the rule below** — the stamp is
what protects a hand edit, and it is unchanged.

So all three go through **`Export/CompanionOwnership.WriteIfUnmodified`**: it
overwrites only when the file on disk hashes to exactly what we last wrote there,
and stamps the result. Anything else — edited, replaced, or never stamped — is
left alone and the refusal is logged (`"Leaving the .rs2dlc alone: …"`).

- **The failure direction is deliberate.** An absent, unreadable or corrupt stamp
  means DON'T write. A wrong skip costs a stale derived file the charter can
  delete; a wrong write costs hand-authored data with no undo. That also makes
  the upgrade case right: every project that already has a `.rs2dlc` is unstamped,
  so the first save after this change refuses rather than clobbering.
- **Deleting the companion is how the charter hands it back** — the next save
  recreates and re-stamps it. There is no setting and no prompt.
- **Consequence to state plainly when it comes up:** once edited, the file stops
  following the chart. Add an arrangement and DLC Builder will not see it until
  the charter deletes the `.rs2dlc`. That is the trade the owner asked for; a
  field-level merge was NOT built, because any field we forgot would go on being
  lost silently, which is the bug itself.
- The stamps live in **`.dotcharter/companions.json`**, keyed by chart-relative
  path. The metadata directory is hidden on Windows. A legacy top-level record is
  imported before moving owned files. It is a cache, never a source of truth.
- **The `DTRS XML` bundle and the `.psarc` are derived output.** The companion
  package also checks the last published hash before replacing an existing file. The bundle
  is swapped in wholesale by rename. `OwnedDlcBuilderBundle` records generated XML
  and refuses to move/recycle a bundle containing foreign bytes. **Atomicity is a different guarantee
  and that one DOES apply to them** — see below.

- **Ownership is a different guarantee from ATOMICITY, and until 2026-08-28 only
  the first existed.** The stamp stops us overwriting a file we do not own; it
  does nothing about how we write one we DO. Both writers truncated first —
  `EofExporter.ExportToFile` was a bare `File.WriteAllBytes` and
  `Rs2DlcWriter.WriteTo` a bare `File.WriteAllText` — so a crash, a kill or a
  power cut inside the save worker left the charter's `.eof` or `.rs2dlc` empty,
  and neither is regenerable from anything DotCharter holds. Both go through
  `AtomicFile` now (write sibling temp → flush to platter → rename), matching
  `CompanionOwnership.WriteStamps` beside them. Pinned by
  `Services/Export/CompanionWritesAreAtomicTests`, which holds the destination
  open for the whole call with `FileShare.Read | FileShare.Delete` — read and
  delete sharing, no WRITE sharing — so a truncating writer is denied and an
  atomic rename is not.

  **More writers joined them in 0.61**, all bare truncating writes over a
  file the charter may well be replacing from a previous export:
  `GpExporter.Export` (`File.WriteAllBytes`) and the two lyric exports
  (`LyricsFileService.WriteAsync`: `.lrc` / UltraStar `.txt`). `CompanionWritesAreAtomicTests`
  covers the first directly.

  **Anything under `Export/` that opens the destination for WRITE is a bug**;
  the whole list goes through `AtomicFile`.

Guarded by `Services/Export/CompanionOwnershipTests` (the ownership guard, native companion call sites
and upgrade case each fail independently).

Config (all default on): `SaveCompanionEof`, `SaveCompanionEofAudio`,
`SaveCompanionPsarc`, `SaveCompanionPsarcCodec`, `SaveCompanionRs2Dlc`.

### `.rs2dlc`: three encoding facts, all invisible in C#

The schema is transcribed from the git-ignored reference checkout
(`Rocksmith2014.NET/src/Rocksmith2014.DLCProject/{DLCProject,DLCKey,StringValidator}.fs`)
and checked against its real
`tests/Rocksmith2014.IntegrationTests/project/Integration_Test.rs2dlc`. A writer
that gets any of these wrong produces a file DLC Builder reads back as defaults
with **no error anywhere**, so each has its own test in
`Services/Export/Rs2DlcProjectTests`:

1. The arrangement list is an F# discriminated union serialized by
   `JsonFSharpConverter` in its default AdjacentTag form —
   `{"Case":"Instrumental","Fields":[{…}]}`. A flat object deserializes to
   nothing.
2. Enums are written as **numbers** (`"Name": 0`, `"RouteMask": 1`). Adding a
   `JsonStringEnumConverter` to `Rs2DlcWriter`'s options breaks it silently.
3. Upstream writes with `ignoreNull = true`, so an unset field is **absent**,
   never `null`. An empty ARRAY still writes as `[]`.

**The DLC key and the arrangement ids are DERIVED from a stable hash**, where
upstream mints both randomly: a random key churns the folder on every Ctrl+S and
makes DLC Builder read each import as a different DLC.

**Its tuning vector comes from `TuningOffsetResolution`, like every other
exporter's** — a chart states its tuning as note LETTERS and as per-string
OFFSETS, and reading the offsets alone exported an arrangement that only had
letters as E standard. See `DotCharter.Core/AGENTS.md` for the full invariant.

**`Export/DlcBuilder/DlcBuilderBundle` owns the bundle's folder and file names**,
and `ProjectDocumentService` delegates to it. Two writers now have to agree on
them — the one that PUTS the XML there and the one that POINTS AT it — and a
second hand-written copy of the naming is the fault described above.

**Every ffmpeg invocation on the save path goes through `Export/Audio/MediaProcess.Run`
(drains stdout+stderr, bounded timeout, kills the process tree on expiry).** Never
hand-roll `Process.Start` + `WaitForExit()` with redirected pipes here: a Windows
anonymous pipe holds ~4 KB, so an undrained child blocks writing while we block
waiting — and because these run inside the save worker, one deadlock leaves
`_saveInProgress` stuck and every later Ctrl+S silently returns false for the rest
of the session.

