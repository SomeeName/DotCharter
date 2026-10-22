# `.dcsong` — DotCharter's own song package (#705, #706)

**This file is the single source of truth for the format.** The code
(`DotCharter.Services/Package/DcSong/`) cites it; the constants live in
`DcSongFormat`. A value changed there without changing this file is a format
change nobody can see.

A `.dcsong` is one file that carries everything a song needs to be played or
charted, without Rocksmith's encrypted PSARC: the chart, the song audio, a
preview clip, the cover and banner, and optionally the stems.

## Entry points

| Type | What it does |
|---|---|
| `DcSongWriter.WriteAsync(DcSongWriteRequest, IProgress<DcSongWriteProgress>?, ct)` | Writes a package. Throws `DcSongWriteException` (reason: `DcSongWriteFailure`). Returns `DcSongWriteResult` with the manifest, the size and the `DcSongWriteWarning`s |
| `DcSongPackage.Open(path)` | Opens and checks a package. Throws `DcSongReadException` (reason: `DcSongReadFailure`) |
| `DcSongPackage.LoadChart / OpenAudio / OpenPreview / OpenStem / ReadCover / ReadBanner / VerifyAll` | Hands out members, each checked against its SHA-256 first |
| `DcSongManifestJson.Write / Parse` | The manifest's wire format, strict on the way in |
| `DcSongChart.ForPackage` | The packaged chart: the project cloned, every file reference pointed into the package |
| `DcSongProjectUnpacker.UnpackAsync(package, baseDirectory, ct)` | Opens a package as a project in a NEW folder (#709, § Opening as a project). Returns `DcSongUnpackResult` |
| `DcSongLibrarySource` (`Package/DcSong/Library/`) | The Library's and Play Mode's door (#708, § Library and Play Mode): one `ILibraryPackageSource` of `LibraryService` (#886) — `ReadForIndex`, `LoadAsync`, `PrepareAudio`, `LoadCover`, `BeginScan` (`ProbeDecoder`), `BlockFor` and `Probe` |

A game package becomes a `.dcsong` in one step through the plugin that reads packages
(`Services/Plugins/IPackageSongConverter`, [contested-features.md](contested-features.md)
§ Optional plugins): the plugin loads each song the way the Library does and hands the chart,
the audio and the cover to the same `DcSongWriter`, so the file is the one the two-step path
(open the package, File ▸ Export ▸ DotCharter song) writes, minus stems.

`DcSongExporter` (`IDcSongExporter`, a singleton in `App.EditingServices`) is the
app's door, driven by File ▸ Export ▸ DotCharter song (#707,
`Presentation/Screens/Dialogs/DcSongExportViewModel`): `ProbeAsync` reports off the
UI thread whether ffmpeg is there (the dialog greys Export with that reason before
anything is chosen), the audio's size and the project's separated stems that exist on
disk (resolved with `StemFolderProbe.ResolveStemPath`, named by `DcSongStemNames`);
`WriteAsync` is a fresh `DcSongWriter` per write, so an ffmpeg installed while the app
runs is found. `DcSongSizeEstimate` is the dialog's "about N MB" (Opus at its bit rate,
FLAC guessed from the source, images not counted). File ▸ Open (and a
drop) of a `.dcsong` opens it as a project (#709, below). The Library scan lists every
`.dcsong` under the Library folder, and Library and Play Mode play it from there (#708,
below) — the export dialog writes into that folder by default.

## Layout

A zip (no zip64, one volume). Member names are fixed by role:

| Member | Role | Stored as | Required |
|---|---|---|---|
| `manifest.json` | (the manifest) | deflate | yes |
| `chart.dcproj` (format 1.0: `chart.rscp`, still read) | `chart` | deflate | yes |
| `audio/song.opus` or `audio/song.flac` | `audio` | **stored** | yes |
| `audio/preview.opus` | `preview` | **stored** | written always; read as optional |
| `images/cover.png` / `.jpg` | `cover` | stored or deflate | no |
| `images/banner.png` / `.jpg` | `banner` | stored or deflate | no |
| `stems/<name>.opus` | `stem` | **stored** | no (off by default) |

- **Audio and stems are STORED, never deflated.** That is what makes them
  seekable straight out of the file (`DcSongEntryStream`, a bounded window onto
  the package, one file handle per stream) and what keeps a member the reader
  never inflates from being a bomb. A deflated audio, preview, stem or
  unknown-role member is refused at open (`Malformed`).
- At most `MaxEntries` = 64 members and `MaxStems` = 16 stems. A stem name is
  `[a-z0-9_-]{1,32}`.
- The manifest is written first, so a reader that streams the zip meets it first.

## `manifest.json`

```json
{
  "format": "dcsong",
  "formatVersion": "1.0",
  "writer": "DotCharter <app version>",
  "song": {
    "key": "...", "projectId": "...", "title": "...", "artist": "...",
    "album": "...", "year": "...", "charter": "...",
    "lengthMs": 181234.5, "hasVocals": true,
    "arrangements": [ { "name": "Lead", "type": "Lead", "tuning": "E Standard" } ]
  },
  "audio": {
    "codec": "opus", "lossless": false, "bitrateKbps": 192,
    "previewStartMs": 45308.6, "previewLengthMs": 28000
  },
  "files": [
    { "path": "chart.dcproj", "role": "chart", "size": 12345, "sha256": "<64 lower-case hex>" },
    { "path": "stems/drums.opus", "role": "stem", "stem": "drums", "size": 1, "sha256": "..." },
    { "path": "images/cover.png", "role": "cover", "mediaType": "image/png", "size": 1, "sha256": "..." }
  ]
}
```

- **`formatVersion` is `major.minor`**, two non-negative integers. It is read
  BEFORE anything else: a newer MAJOR is refused (`NewerVersion`) whatever the
  rest of the manifest looks like; a newer MINOR is read. This build writes
  `1.1` (`DcSongFormat.CurrentMajor` / `CurrentMinor`). 1.1 stores the chart as
  `chart.dcproj` (`DcSongFormat.ChartPath`); a 1.0 package's `chart.rscp`
  (`DcSongFormat.LegacyChartPath`) is accepted as the chart role too
  (`DcSongManifestJsonTests.TheChart_IsReadUnderTheCurrentAndTheFormat10MemberName`).
  A build that reads only 1.0 refuses a 1.1 package's chart path as an invalid
  manifest, so the member rename is the one 1.1 change an older reader cannot read.
- **Every member except the manifest is listed in `files`** with its
  uncompressed `size` and the SHA-256 of its uncompressed bytes. A zip member
  the manifest does not list is refused (`EntryUnlisted`); a listed member the
  zip lacks is refused (`EntryMissing`); a size that disagrees with the zip's
  central record is refused at open (`ChecksumMismatch`).
- **A role this build does not know** (one a newer minor added) parses as
  `Other`: listed, size- and checksum-verified by `VerifyAll`, never read.
- `audio.lossless` must equal `codec == "flac"`; `bitrateKbps` is present for
  Opus only. Each role's `path` must be the fixed name above, and each role
  except `stem` and `other` appears at most once.
- `song.key` is `DcSongWriteRequest.SongKey`, else
  `RocksmithDlcKey.Create(artist, title)`.

## Audio

- **Default: Ogg Opus, 192 kb/s, stereo, 48 kHz** (`DcSongFormat.OpusBitrateKbps`),
  encoded by the stem cache's `FfmpegStemEncoder` from the editor's own decode
  (`PcmAudioLoader`), so the package plays against the chart exactly as the
  editor did. **Opus is playback-only** — chart-from-audio and stem separation
  need a lossless package, the same rule as the `.dcstems` cache.
- **Lossless: FLAC.** A WAV/FLAC/AIFF source is copied into FLAC as it is —
  its own rate and depth — so the samples are bit-exact to the source. A lossy
  source, or a lossless one ffmpeg will not take as it is, stores the editor's
  48 kHz 16-bit decode instead and the write reports
  `LossySourceInLosslessPackage` / `LosslessFromDecode`.
- **Preview:** Opus, `PreviewClip.DefaultLengthMs` long (shorter for a short
  song), starting at `DcSongWriteRequest.PreviewStartMs` or
  `PreviewClip.DefaultStartMs` (a quarter in — the Rocksmith export's choice).
- **Stems:** off by default. An `.opus` source is stored as it is; anything
  else is encoded to Opus 192 kb/s. Playback-only.
- ffmpeg maps the audio stream only (`FfmpegAudioOnly.Args`), because a user's
  FLAC may carry cover art as a picture stream.

Measured 2026-10-11 on a synthetic 180 s stereo 48 kHz song (34.6 MB WAV,
noise-rich, so FLAC compresses poorly): Opus package 4.7 MB; FLAC 26.5 MB;
Opus + four stems 21.1 MB. Open + `VerifyAll` 20–40 ms.

## The chart

The chart member is the project as `RscpChartSerializer` writes it — every
arrangement, tone, section, phrase, lyric, anchor and fingering. **Its file
references point into the package** (`DcSongChart.ForPackage`):
`AudioFileName` is the audio member, `CoverArtPath` the cover member or `""`,
each `Stem.Path` its stem member (mixer state kept), and `ArrangementFiles` is
empty. `DcSongWriteRequest.ArrangementIndexes` (null = all) keeps only the ticked
arrangements, in project order; vocal tracks always travel. A project names files on its author's machine; none travel, and an
absolute cover path would leak the author's folder layout.

**A chart that asks to recover arrangements from sibling files is refused**
(`ChartInvalid`): the project reader would resolve those names — rooted ones
included — outside the package. The writer never produces one.

## Reading untrusted input

A package comes from anywhere, so the reader never asks `ZipArchive` anything:

- The structure is read by `Update/ZipCentralDirectory`, the bounded parser the
  partial updater uses: zip64 and multi-volume refused, every offset bounded
  against the file, every name through `NormalizeEntryName` (no `..`, no rooted
  or drive or UNC name → `UnsafePath`). A duplicate name (case-folded),
  encryption, a method other than stored/deflate, or a local header that
  disagrees with the central record is `Malformed`.
- **Every read is bounded**: the manifest (`MaxManifestBytes`, 1 MB), the chart
  (`MaxChartBytes`, 64 MB) and each image (`MaxImageBytes`, 16 MB) are checked
  against the DECLARED size first, and then the inflater is read one byte past
  it — a member that produces more than it declared is `EntryTooLarge`.
- **Every member is checked against its SHA-256 before a byte of it is handed
  out** (`ChecksumMismatch`). Stored members are verified once per package
  object; the package file is held open `FileShare.Read`, so a verified member
  cannot change underneath a later read.
- An image must sniff as the `mediaType` its manifest says.
- Nothing is extracted to disk except the chart, into a temporary folder that is
  deleted after the load (the project reader takes a path). This reader does not
  expand archives, so `BoundedZipExtractor` is not involved.
- A truncated download or a non-zip is `Unreadable`.

Every `DcSongReadFailure`, `DcSongWriteFailure`, `DcSongWriteWarning` and
`DcSongWriteStage` names a resx key (`DcSong_ReadFailure_*`, …), a computed key
family registered in `LocalizationSweepTests`.

## Writing

The write is **atomic**: every member is staged in a temporary folder, the zip
is written to `<output>.writing` beside the destination, flushed to disk and
renamed over it with `AtomicFile.ReplaceWithRetry` (the swap
`PsarcWriter.WriteFile` uses). A failed or cancelled write leaves the previous
package untouched and no `.writing` file. The project is cloned on the calling
thread before the first await, so the caller may go on editing.

An explicit cover or banner that is not a PNG/JPEG fails the write
(`ImageUnsupported`); the project's own cover art that is unusable is skipped
with `CoverSkipped`, because the user did not just choose it.

## Opening as a project (#709)

File ▸ Open offers `*.dcsong` (in "Projects and audio" and its own filter), and a drop
routes it the same way (`DroppedFileRouter`: `.dcsong` → `Project`). Both end in
`ProjectSessionService.LoadFile`, which hands the extension to
`Session/DcSongProjectOpener` (built by the session, like `ProjectSaver`):

1. **One** replace prompt (`MayReplaceOpenProjectAsync`), BEFORE the folder picker; the
   load at the end is told the user already agreed, so a dirty project is asked about
   once, not twice.
2. The folder picker (`Picker_SelectProjectFolder`, starting at and remembering
   `AppConfig.LastProjectFolderDir`, like New Project and the importers).
3. `DcSongProjectUnpacker`, off the UI thread, under a cancellable status pill:
   - **A NEW folder, always:** `NewProjectFolder.Create(base, "Artist - Title")`, so
     opening a package twice gives `… (2)` and never lands on the earlier copy (an
     existing EMPTY folder is free, NewProjectFolder's rule). Linted with the other
     create-project flows by `NewProjectFolderTests`.
   - **Every file name is fixed here**, never taken from the archive: `project.dcproj`,
     `song.flac` / `song.wav`, `cover.*`, `banner.*`, `stems/<name>.wav` (a stem name
     is `[a-z0-9_-]`). Every byte comes through `DcSongPackage` (bounded,
     checksum-verified member reads); nothing is extracted wholesale.
   - **Audio:** a FLAC song is copied as it is (the engine decodes FLAC). The engine
     does NOT decode Opus (`JUCE_USE_OPUS=0`), so an Opus song is decoded by ffmpeg
     (`FfmpegStemEncoder.DecodeToWavAsync`, 48 kHz, Opus's native rate) into
     `song.wav`. **Without ffmpeg an Opus package is refused before any folder exists**
     (`DcSongReadFailure.DecoderMissing`, whose message says where to put ffmpeg); a
     failed decode is `DecodeFailed`.
   - **Stems (the decision):** a packaged stem becomes a project stem: decoded to
     `stems/<name>.wav`, declared in the project with the mixer state the package
     carried (volume, offset, mute, solo), path project-relative. That is the shape a
     project's stems already have after a stem-cache hit (Opus archive → WAV beside
     the project), so the mixer plays them at once and Tools ▸ Separate stems sees
     existing stems and asks before replacing them — the package's stems are never
     silently regenerated. The `.dcstems` cache is NOT seeded: its key is the audio's
     content signature, and a cache entry built from these stems would re-encode an
     already-lossy Opus; the package itself is the durable copy. They stay
     playback-only (Opus-derived), and nothing under `Audio/LivePlay/` reads project
     stems (`LiveDetectionAudioSourceTests`). Without ffmpeg (a FLAC package opens
     without it) the stems are left out, undeclared, and the status line says so.
   - Cover → `cover.png|jpg` and `ChartData.CoverArtPath` (a bare name, as New Project
     writes it); banner → `banner.png|jpg` (no project field reads it; it is kept for
     the next export). `ChartProject.ProjectId` is kept, so a re-export is the same
     song (the AI chat history keys on the id AND the folder, so it does not mix).
   - **A failure part-way removes what the call wrote**, and the folder when the call
     made it (a pre-existing empty folder is left standing). A tampered member is
     refused at its read (`ChecksumMismatch`) and leaves nothing behind.
4. The written `project.dcproj` opens through the ordinary load (spacing pass, audio,
   MRU, `LastProjectPath`), and the status line says `Status_DcSong_Opened`, or
   `Status_DcSong_OpenedLossy` (Warning) for an Opus package: **chart-from-audio and
   stem separation work best from lossless**, so ask for a FLAC package to use them.
   A load that failed keeps its own message.

## Library and Play Mode (#708)

A package is a library song like a `.psarc`: the same scan, the same SQLite index, the same
`LibrarySongEntry` rows, both song lists and their filters, and the same load path
(`SongBrowserViewModel.LoadSongItem`, which Play Mode reuses). `LibraryService` hands
every `.dcsong` to `DcSongLibrarySource`, one of its registered `ILibraryPackageSource`s
(`App.EditingServices`, #886):

- **Scan.** `Library/LibraryArchiveFiles.Enumerate` walks the root for `*.psarc`
  then `*.dcsong` (one `PurgeMissing` over both). A package is opened, **every member is
  checked against its SHA-256 (`VerifyAll`)**, and the chart is read; `DcSongLibraryEntry`
  builds the row: key, title, artist, album, year, charter and length from the manifest
  (the chart fills a blank), and from the chart each instrument arrangement's path
  (`RouteMask` from its type), tuning as notes (`TuningOffsetResolution`), capo, hardest-level
  note count, plus "Vocals" when it has lyrics, and the difficulty rating a `.psarc` only
  earns on its first load. A damaged, tampered or newer package throws
  `DcSongReadException`, which the scan logs as `PSARC scan skipped '<path>' … message=…`
  and does not index (retried next scan), as for a bad archive. Never a crash.
- **Its own source kind, no schema bump.** `LibrarySongEntry.SourceKind` is the file's
  extension (`LibraryArchiveFiles.KindOf`) — the one thing the scan admitted it by — and
  `SongBank` carries the audio member (`audio/song.opus|flac`), which says the codec
  (`NeedsAudioDecoder`). No index written before #708 holds a `.dcsong` row, so no stale
  row can serve a default, and the owner's library is not rescanned. **Invalidation** is
  the index's own `(size, mtime)` key; pinned by
  `DcSongLibraryScanTests.The_index_serves_an_unchanged_package_and_rereads_one_whose_file_changed`.
- **Not a duplicate of its archive.** `LibraryDedupe` and `LibraryDoctorSong.Identity` add
  the source kind: a package exported from a `.psarc` keeps its key and charter but is
  its own chart and audio, so both rows stay. The Library Doctor's probe opens a package
  with `DcSongPackage` + `VerifyAll`, never as a PSARC.
- **Audio (the decision).** The engine does not decode Opus (`JUCE_USE_OPUS=0`), and the
  session, the waveform pass, a device-change reload and stem separation all take a FILE.
  So, like a Rocksmith song's WEM, a package's song is materialised ONCE into the bounded
  decoded-audio cache, `LibraryAudioCacheStore` (`DcSongLibraryAudio`): the folder is
  `DirectoryNameFor(package)` (file name, full-path hash, last-write ticks — a changed
  package is a new folder and the old one an orphan the sweep takes first), stamped with
  the `.psarcsource` marker, swept with the rest. **FLAC: the stored member's verified bytes
  copied to `song.flac`** (the engine decodes FLAC, from memory like every library song);
  **Opus: decoded by ffmpeg to a 48 kHz `song.wav`** (`IStemEncoder.DecodeToWavAsync`).
  Every file goes through a `.part-<guid>` staging name and a rename, one gate per
  package keeps an Open and convert-on-select (`PrepareAudio`) from writing it twice, and
  nothing is written outside the cache. A cache hit opens no decoder. The `.dcsong` itself
  is never extracted wholesale; the chart is read from it on every open.
- **Device rate.** `LibraryDeviceRateAudio.ScheduleConversion` converts only a song with its
  `.wem` beside it — only that can be re-derived for another rate — so a package's
  `song.wav` / `song.flac` is never replaced (a FLAC is not a RIFF anyway).
- **Without ffmpeg.** An Opus package cannot play; it shows in the Library **disabled with
  its reason** (status "Needs ffmpeg", `SongBrowser_LoadChart_NeedsDecoder_Tip`), and Play
  Mode lists only playable songs. `BlockFor` answers from the state the last scan (or load)
  probed OFF the UI thread (`ProbeDecoder`, from `BeginScan`, first thing in `ScanAsync`); before any probe it
  answers "playable", and a load then refuses with `DecoderMissing`. A Refresh after
  installing ffmpeg replaces the row (`LibraryRescan.Merge` compares the block too). A FLAC
  package plays without ffmpeg, without its stems.
- **Stems (the decision).** Each packaged stem (Opus) is decoded to
  `<cache folder>/package-stems/<name>.wav` — not `stems/`, which is where the stem cache
  expands a library song's separated stems — and the chart's declared stem is pointed at
  that absolute path with the mixer state the package carried. They reach the stem mixer
  through `ChartProject.Stems`, so Play Mode's "mute my instrument" mutes the player's part
  by stem name. **Playback only:** nothing under `Audio/LivePlay/` names `DcSong`
  (`LiveDetectionAudioSourceTests` forbids it), and the `.dcstems` cache is not seeded.
- **The chart.** Loaded straight from the package (`LoadChart`), so sections, phrases and
  the beat grid — the song map — tones, lyrics and fingering are the chart's. Its
  `CoverArtPath` (a member name) is cleared; the Library's cover is `LoadCover`, the member
  written once into the album-art cache under `LibraryCoverCache.AlbumArtCacheStem` (a JPEG
  keeps its bytes; readers sniff). In-game tones: `RsTonePlaybackCoordinator.PackageToneSource`
  reads a `.dcsong` as the open project's own tone bodies, never as an archive. The AI tone
  anchors (the plugin's library tone anchors) read Rocksmith archives only.
- **Identity.** A package song is `lib:<SongKey>` (`Library/LibrarySongIdentity`) like any library song (stats,
  practice profiles, the stem and waveform caches key on it).
- **The row.** `ILibraryRow.SourceTag` ("DotCharter song") prints ahead of the subtitle in the
  subtitle's font and size, so the row keeps its height
  (`Perf/LibraryConstructionUiPerfSmokeTests.APackageRow_CarriesItsTag_AtTheHeightOfEveryOtherRow`);
  fake variants `Packages` and `PackageNeedsFfmpeg`.

## Tests

- `DcSongRoundTripTests` — project → package → project equals the original
  value for value (`DeepCompare`, the same ignore set as
  `RscpDeepValueRoundTripTests`); Opus length and rate; lossless bit-exact;
  progress; atomic failure. Needs ffmpeg (skips without it).
- `DcSongReaderSafetyTests` — hand-built packages, each breaking one rule:
  newer major, tampered audio and chart, size mismatch, traversal, bombs
  (declared and under-declared), too many members, unlisted and missing
  members, compressed audio, sibling-file recovery, a fake image.
- `DcSongManifestJsonTests`, `DcSongChartTests`.
- `DcSongProjectUnpackerTests` — opening as a project with hand-built packages and a
  fake decoder (no ffmpeg): FLAC copied, Opus and stems decoded with mixer state kept,
  stems left out without ffmpeg, the new-folder rules (a second open, an occupied
  folder, a pre-created empty one), Opus without ffmpeg refused before any folder,
  a failed decode and a tampered member leaving nothing.
- `DcSongOpenSaveReexportTests` — open → save → re-export carries the original
  package's chart, cover, stems and song info, Opus and FLAC. Needs ffmpeg.
- `DcSongLibraryScanTests` — beside a `.psarc`: kind, row facts, not collapsed; damaged,
  truncated, non-zip and tampered packages skipped with their reason; the index serving,
  re-reading a changed file and purging a deleted one; the decoder block.
- `DcSongLibraryLoadTests` — FLAC bytes and Opus decode into the cache, sections, stems with
  mixer state, a hit decodes nothing, no ffmpeg (refused / stems left out), tampered after
  the scan, a changed package in a new folder, convert-on-select, the cover.
- `UI/ViewModels/PlayMode/DcSongPlayModeLoadTests` — Play Mode opens a scanned package
  through the real load path; an Opus package without ffmpeg is disabled with its reason.
- `Audio/Coordination/RsTonePackageSourceTests`, `LibraryDeviceRateAudioTests.A_song_without_its_wem_is_never_converted`,
  `LibraryRescanTests.AnUnchangedPackage_WhosePlayabilityChanged_GetsANewRow`.
- `Session/DcSongProjectOpenerTests` (one prompt, picker, lossy line, refusals) and
  `Session/ProjectSessionDcSongOpenTests` (the real session's `LoadFile` and File ▸ Open
  filters).
