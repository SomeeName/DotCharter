# `.dcproj` — the project format promise

What the project file format guarantees between machines and app versions: which names are frozen, what a reader does with
data it does not recognise, what happens when an older build opens a newer
build's chart, and when the version number moves.

**Every clause below is either enforced by a named test or explicitly marked
PROSE ONLY.** If you change one of those tests or the code under it, update this
file in the same commit (repo docs-current rule). A promise nobody tests is
prose; a promise the code stopped keeping is worse than no promise, because a
reader stops looking.

## The format

- One XML document (NOT a zip), extension **`.dcproj`**, serializer
  `DotCharter.Services/Parsing/Rscp/RscpChartSerializer` (`Save`/`Load`),
  resolved by extension through the keyed DI registration (see
  `docs/agents/MAP.md`). The contract type is
  `Parsing/Rscp/RscpFormatContract` — the code half of this document. The
  layout is RsCharter's `chartFormatVersion` 3 plus DotCharter's additive
  extensions, so the code keeps the format family's `Rscp` name; only the
  extension says "DotCharter project".
- **The extensions are spelled in ONE place**: `Parsing/Rscp/ProjectFiles`
  (`.dcproj`, `project.dcproj`, the picker patterns, the folder-scan rule) and
  `CharterRscp` (`.rscp`, the companion name, the legacy backup name, the
  `dcWriterVersion` probe). Every picker, the drop router, the batch lint scan,
  New Project, the `.dcsong` unpacker and the save path go through them
  (`ProjectFilesTests`).
- **A `.rscp` still opens**: Charter's projects and the projects older
  DotCharter builds saved under that extension read through the same
  serializer. DotCharter's XML is never written back into a `.rscp`: the first
  save of a session opened from one writes the same-named `.dcproj` beside it
  (`ProjectSaver.SaveFromRscpAsync`). An older DotCharter file (its root carries
  `<dcWriterVersion>`) is then renamed to `<name>.rscp.bak` (then
  `.rscp.2.bak`, …; never overwritten, never deleted) and the status line says
  so once; a plain Charter project is left byte-for-byte as it is. A `.dcproj`
  already sitting there is never overwritten: the user picks a name instead.
  Pinned by `ProjectFileExtensionSessionTests`.
- **The migration keeps the project's Play Mode records.** An own chart's
  stats key is its project path, so the same save moves everything keyed by
  the old path to the new one (`ProjectPathRekey`): practice stats and best
  scores of every player profile (the leaderboards read those), setlist
  songs, and the per-song tone and drop-tuner settings. Each store is
  rewritten atomically, merges with entries already under the new path and
  never drops one; a second run moves nothing. Only a migration to the
  same-named `.dcproj` does this: a `.dcproj` the user named differently is a
  Save As, which starts fresh like every Save As. Batch lint follows the same
  rule as the save: the fixed project of a `.rscp` is written as the
  `.dcproj` beside it (an older DotCharter file is kept as `.rscp.bak`, a
  Charter project stays byte-for-byte as it is), and an existing `.dcproj` is
  never overwritten.
- **A `.dcproj` beats the `.rscp` of the same name.** In a folder holding both,
  the `.rscp` is the project's Charter companion (below), so opening, dropping
  or remembering the `.rscp` opens the `.dcproj` (`ProjectFiles.PreferProject`
  in `ProjectSessionService.LoadFileAsync`); the recent list
  (`RecentProjectsList.Prune`) and the batch lint scan
  (`ProjectFiles.WithoutShadowedCompanions`) never list the companion in the
  project's place.
- **Every written file is version 3.** `RscpWriter.BuildChartHeader` pins
  `<chartFormatVersion>3</chartFormatVersion>` unconditionally — never the
  loaded file's number — because RsCharter re-runs v1/v2→v3 migration on any
  version < 3, which would corrupt data already normalized to the v3 layout
  (`FractionalPosition` everywhere).
- **Every written file carries `<dcWriterVersion>`**, the build that wrote it.

## What is frozen

"Frozen" means: **the name will not be removed, and it will not change
meaning.** A frozen name may gain values it did not have before (a new enum
spelling, a wider numeric range); it may not be repurposed. Breaking one is a
version bump plus a migration, which is the event this document exists to make
rare.

### The root vocabulary — enforced

These are every element that may appear as a direct child of `<project>`. The
list is set-equal to `RscpFormatContract.KnownRootElements`, checked by
`RscpForwardCompatibilityTests.TheGuaranteesDoc_FreezesExactlyTheRootElementsTheContractKnows`,
and the writer is checked against the same set by
`.EveryRootElementTheWriterEmits_IsRegisteredInTheContract`.

<!-- frozen-root-elements: begin -->
Inherited from RsCharter, in its field order:
`chartFormatVersion`, `editMode`, `arrangement`, `level`, `time`,
`artistName`, `artistNameSort`, `title`, `albumName`, `albumYear`,
`musicFileName`, `stems`, `selectedStem`, `arrangementFiles`,
`beats`, `mainAudioOffsetAdded`, `barsAdded`, `showlights`,
`vocalPaths`, `arrangements`, `bookmarks`, `text`.

DotCharter additive extensions, appended after that order:
`dcWriterVersion`, `dcJapaneseTitle`, `dcJapaneseArtist`, `dcCharter`,
`dcGenre`, `dcReleaseDate`, `dcCoverArt`, `dcBeatMapLocked`,
`dcKeyAccidentals`, `dcKeyIsMinor`, `dcAnnotations`, `dcToneDefinitions`,
`dcNotationVoices`, `dcSynthPlayStyle`, `dcProjectId`, `dcProvenance`, `dcVersion`,
`dcLinkGroups`.
<!-- frozen-root-elements: end -->

**The `<project>` root carries NO attributes**, and that is frozen too
(`.TheProjectRootCarriesNoAttributes`) — it is what lets the preserved-unknown
store below be a list of elements with no attribute half. Adding a root
attribute creates a class of data an older build drops silently, so the test
fails and makes it a decision.

### The identity of a sound — PROSE ONLY

Nothing mechanical pins these, but they are what every other field hangs off,
and changing one would invalidate every project file in existence (`.dcproj`, and every `.rscp` before it):

- A note is `<soundNote>` with `p` (start), `ep` (end), `string`, `fret`;
  a chord is `<soundChord>` with `p` and `templateId`, holding `<chordNote>`
  children keyed by `string`.
- `p`/`ep` are `FractionalPosition` strings (beat id + fraction), NOT
  milliseconds — a position is meaningless without `<beats>`.
- `<beat2>` carries `t` (ms) and `sig` (`beats/denominator`); `bar` and
  `anchor` are the `"T"` flags.
- Numbers are culture-invariant and doubles always carry a decimal point
  (`RscpWriter.FormatDouble`, mirroring Java's `Double.toString`).
- `templateId` `-1` (spelled as an ABSENT attribute on `<handShape>`) means
  "no template"; `0` is a real id.

## What a reader does with data it does not recognise

**This is the part that used to be missing, and the honest answer has two
halves. They are deliberately unequal.**

### Unknown TOP-LEVEL nodes are preserved verbatim

A direct child of `<project>` whose name is not in the frozen list above is a
node a NEWER build wrote. `RscpChartSerializer.Load` captures it into
`ChartProject.UnknownRscpNodes` (one normalized XML fragment each, in document
order) and `RscpWriter.WriteDocument` writes it back out. So opening a newer
build's project in an older build and saving it does **not** delete the newer
build's top-level feature.

Three rules make that safe, and all three are pinned:

1. **Residue is written LAST**, after everything this build wrote
   (`.APreservedNodeCannotShadowARealOne_BecauseItIsWrittenLast`). The header,
   beats and vocals parsers resolve their nodes with `Descendants(name)` +
   `FirstOrDefault()`, which is document order, so a preserved node that happens
   to contain a `<title>` can never answer for the real one.
2. **A fragment whose name the WRITER already emitted is dropped, not
   appended** (`.ResavingRepeatedly_NeitherDuplicatesNorAccumulatesTheResidue`).
   Without that, a name known to the reader but missing from
   `KnownRootElements` would be written from the model *and* re-appended as
   residue — one extra copy on every single save, forever.
   **The test is against the writer's names only, captured before the residue is
   emitted** (`.SameNamedResidueNodes_AllSurviveEveryResave`). Residue nodes that
   share a name with EACH OTHER are all kept: a newer build's
   `<dcSection id="1"/><dcSection id="2"/>` is two nodes and both survive. The
   guard used to grow as it appended, so the second and later same-named roots
   were silently deleted on the first re-save.
3. **Residue survives a snapshot** (`.ASnapshotCarriesTheResidue`), because
   `ChartProjectCloner`/`ChartProjectRestorer` is what undo, torn-save
   protection and crash recovery all go through. Without it the first
   undo-able edit after opening the file would delete what the reader kept.

`ChartProject.UnknownRscpNodes` is **not an extension point**: the project
reader is its only producer and the project writer its only consumer. Nothing
in the app may interpret its contents.

### Unknown attributes and unknown DEEP nodes are DROPPED

An attribute a newer build hung on a `<soundNote>`, or an element it nested
inside an `<arrangement>`, is read by nothing and gone on the next save
(`.AnUnknownAttributeOnANote_IsDroppedOnSave_AndTheDocSaysSo`). Parsers read by
name and ignore the rest, so it is never an error
(`RscpChartSerializerRoundtripTests.Load_ToleratesUnknownElementsAndAttributes_FromANewerBuild`).

**This is a promise to drop, not an unfixed bug.** Re-attaching a per-note hint
requires an identity a project-file note does not have: the only handle is position +
string, and a user who moves, splits or deletes notes between the two saves
would get a newer build's hint hung on the wrong note. **Corrupting is worse
than losing**, so the reader loses it, and the load-time warning says so out
loud.

Unknown attributes on the `<project>` root are likewise not preserved — see the
frozen "no root attributes" rule above, which is what keeps that case from
arising.

## Forward compatibility — a newer build's file on an older build

- **It opens.** Everything the older build understands is intact.
- **It is not silently downgraded.** `RscpFormatContract.ReadWriterVersion` +
  `IsNewerThanCurrent` compare `<dcWriterVersion>` with the running build, and
  `RscpChartSerializer.Load` logs a warning naming both versions, how many
  top-level nodes are being preserved, and that anything the newer build added
  to a note, chord or arrangement will be lost on save. The comparison is
  deliberately conservative — absent, malformed or equal is NOT "newer", because
  a warning that fires on every second file is one nobody reads
  (`.IsNewerThanCurrent_OnlyFiresForAVersionThatReallyIsAhead`).
- **On save:** top-level extension nodes go back verbatim; deep additive fields
  are lost; the file is re-stamped with the SAVING build's version and stays
  `chartFormatVersion` 3.
- **A refusal was considered and rejected.** The owner runs two machines that
  are not always on the same build; a reader that refuses the newer file makes
  the chart unopenable rather than merely lossy, and the loss is now bounded
  and announced.

## Backward compatibility — an older file on a newer build

- **v1/v2 projects load via structural migration, not the declared number.**
  `RscpMigrations` detects legacy files by STRUCTURE (missing embedded
  `<arrangements>`) because the number cannot be trusted: a missing or
  unparseable `chartFormatVersion` historically defaulted to 3, so a pre-v3
  file "confidently misread" as v3 and yielded an EMPTY chart — which the next
  save then persisted (the data-loss bug the migration exists to prevent).
- Legacy sidecar arrangement files and vocals are recovered from the project
  directory; the migrated project is stamped to version 3 and reports its
  migration notes. Locked by `RscpMigrationTests`.
- **Guarantee: a v1/v2 project from any older build opens with its content
  intact on current builds, and is silently upgraded to v3 on the next save.**
- A file with no `<dcWriterVersion>` was written before the stamp existed (or by RsCharter).
  Absence is never treated as "newer".

## The versioning policy

**`chartFormatVersion` changes only for a STRUCTURAL RELAYOUT — the v2→v3 kind,
where existing data moves — and changing it is a migration-writing event.** It
never moves for new data.

New data therefore rides **additive fields**:

- A new field is an OPTIONAL attribute or element, **absent at its default**, so
  a chart that never carried it is byte-identical to what the previous build
  wrote and an older reader ignores it. The precedent is the notation
  hints: `NotationDuration`/`GraceFret` are nullable domain fields written as
  `nd`/`gf` ONLY when non-null (`RscpWriter`) and parsed if present
  (`RscpArrangementParser`). `gf` rides `chordNote` too (a chord
  grace is one hint per string) — an additive field on a NEW element, still no
  bump — and `expSus` on `chordNote` (EOF's explicit sustain status; see
  `ChordNote.ExplicitSustain`) is the same shape. So are `vibratoStrength`,
  `ignDer`, `forceRS`, `nop` (`chordTemplate`), `followed`
  (`chordTemplate`: the FHP generator minted the fingering) and every
  `dc*` node.
- A whole new FEATURE is a new root-level `dc*` node, written only when
  non-empty (`dcAnnotations`, `dcToneDefinitions`, `dcNotationVoices`), or only
  when it differs from its defaults (`dcSynthPlayStyle`). **Add it
  to `RscpFormatContract.KnownRootElements` and to the frozen list above in the
  same change** — the writer lint fails otherwise, and the failure it prevents
  is the node being written twice on the save after next.
- **A new root node must not reuse an element name that appears elsewhere in the
  document.** The readers resolve at document scope with `Descendants(name)`;
  an older build re-emits your node last so it cannot shadow anything of ITS
  own, but it has no way to protect a name it does not know about.
- Neither of those bumps the version.

### …and the exception: when ABSENCE carries meaning

**A field is absent at its default only when absent and default mean the same
thing.** When absence has to mean something else — "derive this", "unknown",
"written before this field existed" — the field is written at EVERY value,
including the default, and it stops being invisible to a differ. Two live
examples, and they are the shape to copy:

- `originalMuted` on `<stems>` (`ChartProject.OriginalMixMuted`) is
  written on every project that has stems, including when false. It has to be:
  absent must keep meaning "this file predates the attribute, derive it", and
  the derivation ("muted iff any stem is audible") is not the same as either
  literal value. Squeeze it into the write-only-when-true shape and every
  project with the original mix audible reads back as a file to be re-derived.
- `<dcWriterVersion>` is the only unconditionally-written `dc*` ELEMENT,
  for exactly the same reason: a stamp that appeared only sometimes could not
  answer "is this file newer than me?", because absence has to keep meaning
  "written before the stamp existed".
- `<dcProjectId>` (`ChartProject.ProjectId`) is the project's
  stable identity — 32 lowercase hex digits, minted when a project is created —
  and is written on every save for the same reason: absence means "written
  before the id existed", and `RscpChartSerializer.Load` then DERIVES one from
  the file's bytes (stable until the next save writes it). A malformed value
  reads as absent. The AI chat history keys conversations by it; see
  [agents/services/ai-chat-history.md](agents/services/ai-chat-history.md).

`<handShape templateId>` is the inverse spelling of the rule — the untemplated
sentinel is the ABSENT attribute, chosen because both readers already default a
missing attribute to `-1`, so absence and the intended meaning coincide and
writing `-1` would break RsCharter outright.

## Linked measures — `dcLinkGroups` (#76)

A link group keeps runs of whole measures equal across their copies. **The copies are real**:
every placement's notes stay in its arrangement's levels, so the node only names the runs
(`RscpLinkGroups`):

```xml
<dcLinkGroups v="1">
  <linkGroup id="…" letter="A" name="Chorus" rule="mc2;det;mf64;snap5;AHF" hash="…">
    <linkPlacement arr="0" p="16" ep="32"/>
  </linkGroup>
</dcLinkGroups>
```

- `arr` is the arrangement's index, `p`/`ep` the run's barlines (positions, like every
  other position in the file). `rule` is the comparison rule the copies are held equal
  under (`MeasureContentOptions.Header`, self-describing: format version, spacing gap,
  what is compared), `hash` the content hash of the copies under that rule. `v` is the
  node's own version: the node is new DATA, so `chartFormatVersion` does not move (see
  the versioning policy below); a reader that does not know `v` keeps no links from it.
- **An older build** reads the whole chart (the copies are plain notes) and keeps the
  node verbatim as residue (above). It does not keep the copies equal, so:
- **The load check** (`LinkGroupVerifier`, run by `RscpChartSerializer.Load`): every copy
  is compared under the stored rule; the copies whose content matches the stored hash stay
  linked, every other copy is UNLINKED with its content kept (copies still equal to each
  other keep a group of their own), and the load logs a warning per group. A placement
  whose arrangement index is out of range, whose barlines are not barlines any more, or
  that overlaps another is dropped the same way. Pinned by
  `RscpLinkGroupsTests.An_older_build_keeps_the_node_and_a_copy_it_edited_is_unlinked_on_load`.
- RsCharter, EOF, RS XML and Guitar Pro do not carry links; the export writes plain copies.

## Round-trip stability (same build, between machines)

- Save→Load preserves header metadata, beats, arrangements, notes/chords with
  positions/frets/sustains, notation voices and hints — locked by
  `RscpChartSerializerRoundtripTests`, `RscpNotationHintTests`,
  `RscpNotationVoiceTests`, and the parse-every-fixture
  `RscpTestDataParseAllTests`. Fidelity, not just presence, is
  `RscpDeepValueRoundTripTests` over a rich fixture whose own completeness is
  linted by `RscpFixtureCoverageTests`.
- Numeric formatting is culture-invariant (`FormatDouble` in the writer) — a
  de-DE machine and an en-US machine produce identical files.
- Saves are atomic: write `<path>.saving` → `File.Move(overwrite)` (see the
  save pipeline in `docs/agents/MAP.md`) — a crash mid-save never corrupts the
  previous file.

## The Charter companion — `<name>.rscp`

Every save also writes a Charter-compatible `<name>.rscp` beside the
`<name>.dcproj` (`SaveCompanionService.WriteCharterProject`, after the
`.dcproj` is on disk; it never fails a save):

- **It is the same document cut down to Charter's model**
  (`Export/Charter/CharterRscpProjection` over `CharterProjectSchema`, an
  allow-list tree read from Charter V0.22.44's `ChartProject` and the classes
  and converters it includes). No `dc*` node, no attribute Charter does not
  read, `chartFormatVersion` 3. What it cannot carry (links, tone definitions,
  annotations, notation voices, the extension attributes) is dropped from the
  companion only, and logged once per changed loss.
- **It is Charter's own project format, so it is never clobbered**: it goes
  through `CompanionOwnership.WriteIfUnmodified` like the `.eof` and the
  `.rs2dlc`. A Charter project at that name, or a companion the charter saved
  in Charter, is left alone.
- **It is derived and never read back as the project** while its `.dcproj`
  exists (see the precedence rule above).
- Pinned by `CharterRscpCompanionTests`: the allow-list (no element or
  attribute outside it), a DotCharter re-import of the companion with the chart
  content intact, the ownership refusal and the log-once rule. Whether Charter
  itself opens it is a manual check.

## What is deliberately NOT promised

- **Byte-identity across builds.** `<dcWriterVersion>` changes with the app, so
  a re-save always differs by at least that line. Within one build, a chart that
  uses no new feature is still byte-identical to the previous build's output.
- **Element ORDER**, beyond "residue last". Readers look nodes up by name.
- **RsCharter (Java) readability of the `.dcproj`.** DotCharter's `dc*` nodes
  and extension attributes mean nothing to Charter's XStream model, and
  `<dcWriterVersion>` is always present. Charter opens the `<name>.rscp`
  companion instead. RsCharter's own output still loads here.
- **Preservation of unknown deep attributes/elements**, and of unknown
  attributes on the root. See above; these are drops by design.

## What is NOT the format

- The companion artifacts (`<name>.rscp`, `DTRS XML/` bundle, `<project>.eof`,
  `<DlcKey>_p.psarc`) are DERIVED on save and never read back as the source of
  truth. Deleting them loses nothing; the `.dcproj` alone is the project.
