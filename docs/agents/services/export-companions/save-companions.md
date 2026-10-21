# Export: Wwise-free audio + the save companions — save companions (SaveCompanionService)

## Save companions (`SaveCompanionService`)

Every save writes derived artifacts beside the `.rscp`: the RS2014 XML bundle
(pre-existing), a DLC Builder `.rs2dlc` inside it, an Editor on Fire `.eof`, a
Rocksmith `.psarc`. (The CustomsForge upload plan and its browser fill script
were removed in #866: the site's form changes too often to keep a filler in step.)

**They are derived, and none of them may fail a save.** The `.rscp` is written
atomically *before* any of them runs, so a companion exception must be caught —
otherwise `SaveProjectCoreAsync`'s outer catch reports "project save failed" for a
project that saved perfectly.

- **The DTRS XML bundle** (`ProjectDocumentService.SaveDlcBuilderXmlBundle`) — written
  to a `DTRS XML.saving` staging folder, then swapped in by **renaming** the old
  bundle to `DTRS XML.old` and renaming staging into place (`SwapInBundle`). Never
  `Directory.Delete(export, recursive)` first: a recursive delete is not atomic, so
  one locked file half-deletes the bundle and the cleanup then removes staging too —
  both copies gone. A rename is all-or-nothing, so the worst case is a leftover
  `.old` (cleaned up on the next save) beside an intact previous bundle.
  **A 7-/8-string guitar goes into the bundle as its six-string projection (#911)**:
  DLC Builder is the consumer and its SNG build indexes six-wide arrays. The writer is
  `RsXmlSongWriter { SixStringProjection = true }`; each arrangement that dropped
  something logs one warning (counts of notes and chords). The `.rs2dlc` agrees: its six
  tuning slots are the six STOCK strings in package order (`Rs2DlcProjectBuilder.TuningVector`
  through `Rs7StringLayout.ToDomain`), not the chart's first six. Six-string guitars and
  basses are written as before.
- **`.eof`** — a plain binary write (a few ms), so it runs inside the existing save
  worker on every save. **EOF resolves its audio relative to the folder the `.eof`
  lives in**, and `EofExporter` names it `<audiostem>.ogg`. So the companion also
  transcodes the song to a sibling `.ogg` (once, mtime-cached, via ffmpeg) —
  without it EOF opens the project and prompts for missing audio.
- **`.psarc`** — converts every arrangement to SNG and copies the whole audio
  track. It runs **detached, after** the save returns, so it never puts seconds of
  latency on Ctrl+S. Builds **coalesce**: a save landing while one is in flight
  replaces the pending snapshot rather than queueing a build, so holding Ctrl+S
  does not build a backlog. It **stages to a temp file and swaps**, so a build
  cancelled by a newer save (or by shutdown) leaves the previous psarc intact
  rather than truncated.

- **`.rs2dlc`** (`Export/DlcBuilder/`) — the DLC Builder PROJECT file, written
  to the **project root** beside the `.rscp` and the audio, naming the bundle's
  arrangements through `DTRS XML/`. It is what makes the folder open directly in
  DLC Builder and what gets dropped onto CustomsForge Ignition4's own "Import
  rs2dlc" control (which autofills the metadata it carries, with no code of ours
  running in a browser).
  **It must NOT be written inside `DTRS XML/`**: that folder is swapped in by
  renaming the old one to `DTRS XML.old` and renaming staging into place, so
  anything written inside it is moved aside on the next save and deleted on the
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

### Two companions are somebody else's project file — never clobber them

**The `.rs2dlc` and the `.eof` are the NATIVE PROJECT FORMATS of DLC Builder and
Editor on Fire.** The charter opens them there, edits them, and saves. Everything
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

So both go through **`Export/CompanionOwnership.WriteIfUnmodified`**: it
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
- The stamps live in one **`.dotcharter-companions.json`** beside the project,
  keyed by file name. **Not hidden on
  purpose**: it is the only visible answer to "why has my `.rs2dlc` stopped
  updating". It is a cache, never a source of truth.
- **The `DTRS XML` bundle and the `.psarc` stay unconditional** — pure derived
  output, nobody hand-edits them, and the bundle is swapped in wholesale by
  rename. Do not "make them consistent". **Atomicity is a different guarantee
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
  `GpExporter.Export` (`File.WriteAllBytes`) and the two lyric exports in
  `LyricsInspectorViewModel` (`.lrc` / UltraStar `.txt`). `CompanionWritesAreAtomicTests`
  covers the first directly.

  **Anything under `Export/` that opens the destination for WRITE is a bug**;
  the whole list goes through `AtomicFile`.

Guarded by `Services/Export/CompanionOwnershipTests` (12 cases; the guard, the
two call sites and the upgrade case each fail independently).

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

