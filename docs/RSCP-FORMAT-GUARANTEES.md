# `.rscp` v3 — the format promise

What the project file format guarantees between machines and app versions: which names are frozen, what a reader does with
data it does not recognise, what happens when an older build opens a newer
build's chart, and when the version number moves.

**Every clause below is either enforced by a named test or explicitly marked
PROSE ONLY.** If you change one of those tests or the code under it, update this
file in the same commit (repo docs-current rule). A promise nobody tests is
prose; a promise the code stopped keeping is worse than no promise, because a
reader stops looking.

## The format

- One XML document (NOT a zip), extension `.rscp`, serializer
  `DotCharter.Services/Parsing/Rscp/RscpChartSerializer` (`Save`/`Load`),
  resolved by extension through the keyed DI registration (see
  `docs/agents/MAP.md`). The contract type is
  `Parsing/Rscp/RscpFormatContract` — the code half of this document.
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
`dcNotationVoices`, `dcSynthPlayStyle`, `dcProjectId`, `dcProvenance`, `dcVersion`.
<!-- frozen-root-elements: end -->

**The `<project>` root carries NO attributes**, and that is frozen too
(`.TheProjectRootCarriesNoAttributes`) — it is what lets the preserved-unknown
store below be a list of elements with no attribute half. Adding a root
attribute creates a class of data an older build drops silently, so the test
fails and makes it a decision.

### The identity of a sound — PROSE ONLY

Nothing mechanical pins these, but they are what every other field hangs off,
and changing one would invalidate every `.rscp` in existence:

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

`ChartProject.UnknownRscpNodes` is **not an extension point**: the `.rscp`
reader is its only producer and the `.rscp` writer its only consumer. Nothing
in the app may interpret its contents.

### Unknown attributes and unknown DEEP nodes are DROPPED

An attribute a newer build hung on a `<soundNote>`, or an element it nested
inside an `<arrangement>`, is read by nothing and gone on the next save
(`.AnUnknownAttributeOnANote_IsDroppedOnSave_AndTheDocSaysSo`). Parsers read by
name and ignore the rest, so it is never an error
(`RscpChartSerializerRoundtripTests.Load_ToleratesUnknownElementsAndAttributes_FromANewerBuild`).

**This is a promise to drop, not an unfixed bug.** Re-attaching a per-note hint
requires an identity a `.rscp` note does not have: the only handle is position +
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

## What is deliberately NOT promised

- **Byte-identity across builds.** `<dcWriterVersion>` changes with the app, so
  a re-save always differs by at least that line. Within one build, a chart that
  uses no new feature is still byte-identical to the previous build's output.
- **Element ORDER**, beyond "residue last". Readers look nodes up by name.
- **RsCharter (Java) readability of every file.** DotCharter's `dc*` nodes are
  unknown elements to XStream. Every other `dc*` node is absent when unused, so
  a plain chart stayed loadable there; `<dcWriterVersion>` is always present, so
  a DotCharter-saved project is a DotCharter project. RsCharter's
  own output still loads here — that direction is the one that matters.
- **Preservation of unknown deep attributes/elements**, and of unknown
  attributes on the root. See above; these are drops by design.

## What is NOT the format

- The companion artifacts (`DTRS XML/` bundle, `<project>.eof`,
  `<DlcKey>_p.psarc`) are DERIVED on save and never read back as the source of
  truth. Deleting them loses nothing; the `.rscp` alone is the project.
