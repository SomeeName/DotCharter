# Export: Wwise-free audio + the save companions — the export-boundary rules: linked chords, chord names, repeated chords, phrase names, moveR phrases, chordId, error invention, arrangement properties, tuning, capo, songLength, tone events, average tempo, tuning stated twice, metadata, GP export

## A linked chord is legal to author — the LANDING is fixed at the export boundary

- **A LINKED CHORD IS LEGAL TO AUTHOR. What RS cannot spell is the LANDING, and
  that is fixed at the EXPORT boundary — never in the chart and never by nagging
  the user.** Tying or sliding one chord into the next is ordinary guitar. But in
  RS2014 a `<chord>` means a STRUM, and a shape you slid into was never
  re-picked, so the game mis-displays it and DLC Builder rejects it as I03 "note
  is linked to a chord" (`InstrumentalChecker.isLinkedToChord`, which inspects
  CHORD NOTES too). **EOF solves this exactly where we now do** — it keeps the
  linked chord in the project and splits it while writing the XML: *"Identify
  chords that follow a note with linkNext status, which will need to be broken
  into single notes so that they display correctly in-game"*
  (`EOF_SRC/rs.c:2010`, `EOF_NOTE_TFLAG_TEMP`/`EOF_NOTE_TFLAG_SPLIT_CHORD`). Its
  output proves it: over the 98 hand-corrected exports in `TestData/EOF` there
  are **493** chord notes carrying `linkNext` and **not one** lands on a chord —
  all 493 land on loose notes at gap 0, 492 of those landings carry 2-4
  SIMULTANEOUS notes, and a hand shape covers the landing in 493 of 493.
  `Parsing/Xml/Song/LinkLandingBreakup` is our version: a PURE projection
  consumed instead of `level.Notes`/`level.Chords` by **both** export paths —
  `RsXmlSongWriter.WriteLevel` and `SngFromDomainMapper.ConvertLevel`.
  **Both, because the SNG is what the GAME reads.** The XML consumed it from the
  day it was written and the SNG builder did not, so every `.psarc` this app
  produced — the save companion and the package export alike — shipped exactly
  the chord the XML refuses to write, while the whole justification above is
  about what the game displays. The two are pinned against each other by
  `Services/Export/SngLinkLandingBreakupTests.TheSngAndTheRsXml_SpellTheSameSoundsAsChords`,
  which compares the CHORD/NOTE split of the two outputs rather than either
  one's absolute counts, so it cannot rot when one path changes. **A chord
  with `SplitIntoNotes` (Inspector ▸ Playback ▸ "Split notes") goes through the
  same projection (#599)** — EOF's split status, `EOF_PRO_GUITAR_NOTE_FLAG_SPLIT`
  (`song.h:64`), which EOF breaks into single notes at RS export
  (`rs.c:2082-2087`). Until #599 the flag was saved in the `.rscp` and shown in
  the Inspector but NO exporter read it: a split chord shipped as a `<chord>`.
  `SngLinkLandingBreakupTests.ASplitChord_ShipsAsSingleNotesInTheSng` and
  `GpPickScrapeImportTests.ReferenceScene_ExportsTheOwnersNoteLines_FieldForField`
  pin both paths; the `.eof` companion carries the flag both ways since #609
  (`EofSplitFingerlessTests`). Templates,
  hand shapes and anchors keep coming from the level itself on both paths — the
  shape stays visible, it just stops claiming a strum. **A split chord counts as
  its notes in every other export decision too (#621, `LinkLandingBreakup.IsSplit`),
  as EOF does:** a template only split chords play is left out of
  `<chordTemplates>` and the SNG (`RsChordTemplateExport`, which both writers
  build their list from; later `chordId`s move down with it), the repeat decision
  judges the chord after a split chord against its notes (`RsChordDensity`,
  reason `SplitIntoNotes`), and `ArrangementPropertiesDeriver` counts its
  techniques but not its shape — the last one diverges from EOF, which derives
  the properties before it splits (`rs.c:1872`). Pinned by
  `SplitChordsAsNotesTests`; the six RsCharter SNG pins this moved are explained
  by `ExportBytePinTests.UnpackedSng_WithTheSplitOnlyTemplatesPutBack_IsTheHashBefore621`.
  Two rules that
  fell out of getting this wrong first:
  **(a) do not "fix" this at import.** Splitting in `GpPostProcessor` destroyed
  the authored chord for every chord grace slide (five cross-tool tests) and
  changed the chart the user sees; the defect is a SPELLING, so it belongs to the
  writer. **(b) do not report it in the inspector.** `LinkNextRule` used to raise
  `NoteLinkedToChord` — 27 findings on one real chart for something the export
  already handles. It now resolves the landing chord's note on that string as the
  link TARGET, so the fret/timing/bend checks still run against what actually
  ships. The rule does still walk CHORD NOTES (RS2014.NET's `checkChords` does,
  and a linked chord is how the tab importers and the paste path spell a strum
  ringing into the next one) via `LinkSource` — a chord note has no position of
  its own and borrows its parent chord's.
  **(c) do not "fix" it in AUTO-FIX either — that one SHIPPED, and it damaged real
  charts (removed in 0.40).** `ArrangementAutoFixService.UnlinkSoundsLinkedIntoChords`
  cleared `LinkNext` on every sound whose sustain landed on a chord claiming the
  same string, and it is GONE. It read as safe because it "only dropped a flag",
  but it left the SUSTAIN untouched: the tie that used to render as a link became
  a bare sustain running visibly past the end of its own hand shape, and every
  linked slide chord in the chart came apart at once. One run on the owner's chart
  cleared **27** links — exactly the 27 findings the removed `LinkNextRule` used to
  raise, i.e. it was "repairing" precisely what the export already handles. The
  test is INVERTED rather than deleted
  (`AChordNoteLinkedIntoAChord_KeepsBothItsLinkAndItsSustain`), so re-adding the
  pass fails the gate.
  **…and it had a SECOND copy that survived that removal for four releases.**
  `ChartImprovementService.RemoveInvalidLinkNextPatterns` — Tools ▸ Improve Chart
  — cleared `LinkNext` on any note whose sustain landed on a chord claiming the
  same string, i.e. the identical pass under another name, and
  `RemoveUnnecessaryLinkNextTargets`' own doc comment pointed at it as "what
  clears a link into a chord". Only the UNPITCHED-slide branch (inspector `I09`,
  a real reported defect) remains there.
  `ChartImprovementServiceTests.ImproveProject_KeepsANoteLinkedIntoAChord_LinkAndSustainTogether`
  is that half's inverted keeper. **When a destructive pass is deleted, grep the
  whole solution for its BEHAVIOUR, not just its name.**

## A two-string shape ships with no chord name, and BOTH writers have to agree

- **A TWO-STRING shape ships with no chord name, and BOTH writers have to agree
  (0.41).** The rule, the predicates and the reasoning live in
  `DotCharter.Core/AGENTS.md`; what matters here is that it is enforced at the
  export boundary as well as at the namer, because a name also arrives from a
  psarc import, a cross-arrangement paste, or a chart authored before the setting
  existed. `RsXmlSongWriter.ChordNameOptions` and `SngFromDomainMapper`'s
  `chordNameOptions` both default to `DoubleStopNameOptions.Default` — the same
  value `AppConfig.SuppressDoubleStopChordNames` ships as — so a caller that does
  not thread the setting still writes the shipped behaviour rather than a third
  one, and the SNG the GAME reads cannot disagree with the XML DLC Builder reads.
  Two ordering traps, both pinned: the strip runs BEFORE `displayName`'s `-arp`
  suffix is derived (else an arpeggio double stop ships as the bare marker
  `-arp`), and the Nop bit (SNG mask 0x2, the `-nop` displayName) comes from the
  `ChordTemplate.NopInRs` FIELD (#489), so the strip cannot drop it. `RsManifestGenerator` has always excluded nameless
  templates, so a stripped double stop correctly leaves the manifest's
  `ChordTemplates` list. `Editing/Fixing/DoubleStopChordNames` is the chart-wide
  tool (Tools → Remove Double-Stop Chord Names, one undo node) for names already
  on disk.

## A repeated chord ships with NO chord notes — the `highDensity` attribute does nothing

- **Rocksmith draws the compact repeat from the ABSENCE of `<chordNote>`
  children, not from `highDensity="1"`.** `XmlToSngNote.fs` sets
  `NoteMask.ChordPanel` — the bit that draws the full box and the chord name —
  from `chord.HasChordNotes` alone. `highDensity` is a custom-file convention
  that DLC Builder translates into that absence in
  `InstrumentalArrangement.FixHighDensity`, **and `PackageBuilder.fs` only calls
  it `if xml.Version < 8uy`.** DotCharter writes version 8, so nothing ever
  converted: up to v0.48.0 every repeated chord shipped `highDensity="1"` AND
  its children, and the game drew a full panel for each. Measured on the owner's
  own charts — Yorushika Lead 474 chords / 335 repeats / **474** with children;
  Rhythm 1,165 / 861 / **1,165**. The two tools that get this right do it two
  different ways: **EOF** writes version 7 and lets DLC Builder convert, and
  **RsCharter** writes version 8 with the children already dropped (23 of 80 on
  the same chart). EOF has our case too — `eof_rs2_export_version_8`
  (EOF_SRC/rs.c:2957) drops both the attribute and the subtags "to suit DLC
  Builder's handling".
- **So the decision is one shared type and BOTH writers consume it.**
  `Export/Rocksmith/RsChordDensity` ports
  `eof_note_has_high_chord_density` (EOF_SRC/rs.c:4919) rule for rule;
  `RsXmlSongWriter` omits the children and the attribute, and
  `SngFromDomainMapper` clears `ChordPanel` for the same chords, so a psarc built
  here and one DLC Builder builds from our XML agree. `Chord.ForceNoNotes` goes
  the same way — the SNG mapper had honoured it for releases while the XML writer
  never read it at all.
- **The editor shows the same answer (#487).** `Export/Rocksmith/RepeatChords`
  is the shipped decision for one level — `RsChordDensity` through the writer's
  own chord-id table (`RsChordTemplateExport`, which `RsXmlSongWriter.BuildChordTemplateExport`
  builds), plus `ForceNoNotes`, minus the chords `LinkLandingBreakup` writes as
  notes — and `RepeatChordLatch` hands it to both render loops as data. Never
  add a second repeat rule in a renderer.
- **The rule returns a REASON, and the set is derived from it (#564).**
  `RsChordDensity.Explain` / `RepeatChords.Explain` give every chord a
  `RepeatChordVerdict` (`RepeatChordReason` + the string a per-string reason
  names); `Compute` keeps the verdicts that `Ships`. The inspector's "Repeat:
  yes/no — why" line (`UI/ViewModels/Editor/Inspector/RepeatVerdictText`) prints
  that verdict, so it cannot disagree with the export. A new reason needs a case
  in `Services/Export/RepeatChordVerdictTests` (`EveryReason_HasACase`) and a
  `Inspector_RepeatReason_<Name>` key in all three resx
  (`UI/ViewModels/Editor/InspectorRepeatVerdictTextTests`).
- **Dropping the children loses nothing**: every technique that lives on a
  `<chordNote>` already disqualifies a chord from compacting, and the shape
  itself lives in the chord template. `RsXmlSongReader` rebuilds the notes from
  that template when a `<chord>` has none — which also fixes reading OFFICIAL
  files, where every repeat is spelled this way and used to import as a chord
  with zero notes.
- **EOF's sustain rule is a STATUS FLAG, not a length.**
  `EOF_PRO_GUITAR_NOTE_EFLAG_SUSTAIN` (EOF_SRC/song.h:131) is a per-note
  checkbox that holds a repeat at its full panel; DotCharter had ported it as
  `cn.Sustain > Zero`, which every charted chord satisfies, so the density
  computation could never fire on real content. It is `ChordNote.ExplicitSustain`
  now, stated by the RS-XML and SNG readers exactly where EOF's importer states
  it (rs_import.c:370-395) and carried in the `.rscp` as the additive attribute
  `expSus`.
- Pinned by `Services/Export/CompactRepeatChordTests` (export, read-back, SNG
  parity, `.rscp` round trip) and the compact-repeat half of
  `Services/Parsing/Xml/RsXmlSongWriterExportFidelityTests`, whose predicate is
  now "has no `<chordNote>` children" — it used to read the attribute, which is
  why every one of those tests passed while the shipped charts were wrong.

## A phrase name is made legal for Rocksmith at the export boundary, never in the chart

- **Rocksmith accepts only `[a-zA-Z0-9 _#]` in a phrase name; the chart accepts
  anything.** `Export/Rocksmith/RsPhraseName` is the ONE sanitizer, called by
  `RsXmlSongWriter` (the `<phrase name>` attribute) and
  `SngFromDomainMapper.BuildPhrasesAndIterations` (whose names the psarc
  manifest copies). A legal name is returned unchanged, so an imported RS chart
  round-trips byte-identically; otherwise accents are transliterated
  (`Übergang` → `Ubergang`), dashes/brackets/separators become one space
  (`Solo (fast)` → `Solo fast`), the rest is dropped, and an empty result is
  `phrase{n}`. `SanitizeAll` keeps rewritten names UNIQUE (`Riff!`, `Riff?` next
  to `Riff` → `Riff 2`, `Riff 3`), judged on the 31 characters the SNG holds.
- **`PhraseInfos` (solo, max difficulty) is keyed by the AUTHORED name**, so both
  writers look it up with the authored name and only write the sanitized one.
- **Do not move it back into `ChartSpacingEnforcer`.** It used to run there, on
  the live project on every save and load (#322): it erased `サビ` to `""`,
  merged distinct phrases and orphaned every `PhraseInfos` entry, so the solo
  flag and the difficulty ceiling silently fell back to their defaults. The
  inspector says which names the game will see differently instead — `PhraseRule`,
  `D10` (`PhraseNameRewrittenForRocksmith`), with the export's own spelling.
  Pinned by `Services/Export/RsPhraseNameTests` (both writers) and
  `ChartSpacingEnforcerTests.EnforceProject_KeepsAuthoredPhraseNames_AndTheirPhraseInfoStillResolves`.

## A phrase between beats: the chart holds the real time, `moveR<N>` exists only in files

- **A phrase (and the section on it) may start on a note between two beats.** The
  editor places it there like any marker: the event dialog and the drag snap to
  the grid and then to the nearest sound (`Editing/Snapping/SoundMagnetSnap`).
  The chart stores that position; nothing in the model is called `moveR`.
- **`moveR<N>` is the file form**: the phrase written on the beat before, its
  iteration and the section on that beat really starting at the N-th note
  counted FROM the beat, the note ON the beat being the 1st ("at or after":
  DLC Builder's `Utils.getTimes` keeps `Time >= startTime`, EOF's
  `eof_get_pos_num_notes_after_timestamp` skips only `pos < pos`). Notes at one
  time count once. There is no moveL. `Parsing/MoverPhrases/MoverPhraseName`
  reads both spellings — DLC Builder: name STARTS WITH `mover`, any case, rest an
  integer; EOF: the first `mover` anywhere, then digits only, value above 0, and
  the text before it is the phrase's own name (`riff moveR2`).
- **Import resolves it.** RS XML: `Parsing/Xml/Song/RsXmlPhraseMover.ResolveForImport`,
  run first in `RsXmlSongReader.ReadDocument`, is DLC Builder's `PhraseMover.improve`
  on the sparse levels (count in the level at the phrase's `maxDifficulty`; in each
  level up to it an anchor on the old time is copied to the new one when none is
  there, and removed when no note or chord is left on it) — but only for an
  iteration still ON a beat, because an XML converted from a package DLC Builder
  built has it on the note already under the same name. EOF:
  `Parsing/Eof/EofMoverPhrases.Resolve`, for an RS phrase attached to a beat
  (never a floating one), counting the hardest level, with the same anchor rule.
  The moved phrase loses the `moveR` name (a kept one would be moved AGAIN by
  DLC Builder): its own name, else the section it starts, else `phrase`, unique.
  A moveR without enough notes stays on its beat under its name, and the
  validation's mover rules still see it. The package import lives in the reader
  plugin; it should run the same resolver.
- **Export.** SNG/package: the real time, always. `.eof`: a phrase between beats
  that starts on a note goes on the beat before as plain `moveR<N>` (a name before
  the token would stop DLC Builder moving it in EOF's own XML), its section too;
  the phrase's name is lost and reported (`EofLoss.MoverPhrase`) and comes back as
  the section's name. RS XML: the real time by default; `AppConfig.RsXmlMoverPhrases`
  (Settings ▸ Files) writes `RsXmlPhraseMover.ToMoverForm` instead, for the saved
  `.xml` and the DTRS XML bundle. **Why real time is the default**: no DLC Builder
  check asks for a phrase or section on a beat (`InstrumentalChecker` only checks
  `mover1` on a note, and excuses mover phrases from `PhraseChangeOnLinkNextNote`
  and two anchor checks), and DLC Builder applies `PhraseMover` only in
  `ArrangementImprover.applyAll` — with "apply improvements" off
  (`applyMinimum`) a moveR phrase would ship on the beat.
- **Both writers fall back to the real time** when moveR cannot say it exactly: no
  note starts there, another phrase or section sits between the beat and it, or a
  level has an anchor on the beat and none on the note (the move would change the
  anchors). So a trip through either form comes back unchanged — pinned by
  `Services/Parsing/MoverPhrases/MoverPhraseRoundTripTests` (EOF → DotCharter → EOF,
  DotCharter → XML → `PhraseMover` → DotCharter, the SNG time) and
  `RsXmlPhraseMoverTests` (the `PhraseMover` cases).

## A chart with sections but no `noguitar` gets EOF's closing one, in BOTH writers

- **EOF adds a `noguitar` section on the first beat after the last note whenever
  the chart has none** (`eof_rs_export_common`, EOF_SRC/rs.c). `Export/EndNoGuitarSection`
  is that rule, asked by `RsXmlSongWriter.WriteSections` and
  `SngFromDomainMapper.ExtractSections`, so the XML and the SNG agree. It adds
  nothing to a chart without notes, without any section (the generator or the
  SNG's riff stub owns those), with a `noguitar` anywhere, or with a section
  already on that beat. The chart itself is never touched; an RS-XML read back
  owns the section. Pinned by `Services/Export/EndNoGuitarSectionTests`.
- **The validator reads a `noguitar` SECTION as well as a noguitar-named phrase**
  (`ArrangementValidationContext.BuildNoguitarIntervals`): EOF charts carry it on
  the section and name the phrase freely. I28 and the empty-phrase exemption
  both follow from it.

## `chordId` is an INDEX into `<chordTemplates>`, not a template id

- **RS-XML's `chordId` is POSITIONAL.** `RsXmlSongReader.ReadChordTemplates`
  assigns `Id = id++` while walking the list, so the attribute means "the Nth
  template" — and `RsXmlSongWriter` wrote `chord.TemplateId` straight through
  for both `<chord chordId>` and `<handShape chordId>`. That only agreed by
  COINCIDENCE: `ChordTemplateResolver.NextId` is `max + 1` and the undo paths
  remove templates by INSTANCE, so one deleted template makes every later id
  non-contiguous and shifts every shape onto the wrong chord. The SNG path never
  had the bug — it resolves through `SngFromDomainMapper`'s `TemplateIndexById`
  — and the two artifacts are built from the same chart, so they disagreed.
  `RsChordTemplateExport` now owns the emitted list plus the id→index map, and
  BOTH writers build from it (`RsXmlSongWriter.BuildChordTemplateExport`,
  `SngFromDomainMapper.BuildChordTemplates` — one table since #621),
  including for the unresolvable cases (which used to be written as
  `chordId="0"` — an unrelated shape — where the SNG deliberately does something
  else): a chord with chord notes but no live template gets a synthesized
  nameless template appended, a chord with neither is DROPPED, and a hand shape
  pointing at nothing ships as `-1`. Pinned by
  `Services/Export/RsXmlChordIdIndexTests`, whose sharp case is ids `0` and `5`
  where the chord must emit index `1`.

## An export transform must never invent an error the editor never showed

- **An export transform must never invent an error the editor never showed.**
  `RsXmlSongWriter.BuildAnchorExports` collapsed consecutive same-fret
  anchors on the FRET ALONE and kept `Math.Max` of the two widths — dropping the
  later anchor and retroactively widening the earlier one. **The width is part
  of the key**: Rocksmith derives which finger plays a fret as
  `1 + (fret - anchor.fret) - (anchor.width - 4)`, so a same-fret width change is
  a real hand-span change. The collapse turned an authored 12/w4 into 12/w5 and
  made DLC Builder report I32 "finger used changes during slide" for a slide that
  is clean in the model — **an error the export invented.** Measured over the 98
  hand-corrected exports in `TestData/EOF`, EOF itself emits 60 consecutive
  same-fret pairs whose widths DIFFER, 42 of them inside one phrase. The rule is
  now fret AND width, which is what `ChartSpacingEnforcer.RemoveRedundantAnchors`
  already applied at edit time — **the two must agree, or the shipped chart has
  hand positions the editor never showed.** The phrase-boundary break is
  unchanged and still load-bearing.

## No exporter reads `Arrangement.Properties` — it resolves through `ArrangementPropertiesDeriver`

- **No exporter reads `Arrangement.Properties` — it resolves through
  `ArrangementPropertiesDeriver.Derive` (0.35).** `RsXmlSongWriter` used to own a
  private SET-ONLY derivation on a local clone (so an authored `0` was promoted
  to `1` and never cleared) while `RsManifestGenerator` copied the raw field —
  and the manifest is the artifact the GAME reads, so a chart authored in
  DotCharter, which had no way to set a flag at all, told Rocksmith it used no
  techniques and `CalculateDifficulties` floored `techCoeff` accordingly. Both
  now read the one resolved object; the psarc path caches it on
  `RsPackageArrangement.Properties` so the manifest's flags and the difficulty
  weights cannot look at different data. `RsPackageArrangement.RouteMaskValue`
  resolves the AUTHORED route first and only falls back to the arrangement NAME
  for vocals — the base game names both of Satch Boogie's guitar arrangements
  "Combo" and only the path flags separate them, which is the same reason
  the package reader's manifest parser refuses that fallback. The
  provenance split (authored / derived / undecidable) and the `ManualOverride`
  pin are specified in `DotCharter.Core/AGENTS.md`; the predicates are measured
  against the hand-authored `TestData/EOF` corpus by
  `ArrangementPropertiesExportTests.OnTheEofCorpus_TheDerivationReproducesEofsOwnAnswer`
  — 21 flags exact over 89 arrangements, the rest a documented one-sided
  divergence. **A new predicate has to face that corpus**; it is what caught the
  barre rule ignoring authored fingering on 30 of them.

## Tuning: every exporter goes through `TuningOffsetResolution`

- **Every writer that emits a TUNING goes through `TuningOffsetResolution`,
  never `Arrangement.TuningPitches` directly.** A chart states its tuning twice —
  note LETTERS and per-string OFFSETS — and `RsXmlSongWriter`, `GpExporter`,
  `SngFromDomainMapper` (both sites) and `Rs2DlcProjectBuilder` all read the
  offsets with a `0` default, so an arrangement carrying only letters shipped as
  E standard from all four at once. Authored offsets always win, so nothing that
  has them changed; the invariant and the 4-/6-string-only restriction on the
  fallback are in `DotCharter.Core/AGENTS.md`.
- **The rule is "writes a tuning", NOT "lives under `Export/`" — that scoping is
  what left two writers behind for six releases.** `Parsing/Eof/EofExporter`
  (the save companion that runs on EVERY Ctrl+S — a letters-only arrangement
  opened in Editor on Fire as E standard) and `Parsing/Rscp/RscpWriter` (which IS
  the project file: an empty offsets vector wrote `type="CUSTOM"
  tuningValues=""`, and the parser filled that with zeros, so the letters were
  destroyed on the first save) both read the offsets directly because the
  original sweep was scoped by folder. Both now resolve.
  `Services/Export/TuningResolutionReachabilityTests` lints the whole
  `*Writer.cs` / `*Exporter.cs` / `*FromDomainMapper.cs` / `*ProjectBuilder.cs`
  set under `DotCharter.Services`, comments stripped, and carries a companion
  test that it really scans the six writers it names — a lint that matches
  nothing is worth nothing.

## A 7-string GUITAR ships in the rs7string layout with the RS7X block (#736, #777)

- **The owner's `rs7string` game mod plays a guitar arrangement in 7-string mode
  when its SNG tuning has 7 entries.** The package keeps strings 0..5 as the
  six-string E A D G B e and puts the extra strings AFTER them (6 = 7th string,
  B1; 7 = 8th, F#1), while a chart numbers strings LOWEST first.
  `Export/Rocksmith/Rs7StringLayout` (`ToSng` / `ToDomain`) is the one mapping;
  reader and writer both use it.
- **The RS XML of a 7-/8-string guitar uses the SAME order (#904, the mod's
  "RS XML extension for 7/8-string guitar" v1)**: `RsXmlSongWriter` writes a
  `Rs7Projection` copy of the arrangement (renumbering only at this boundary, plus
  the package's chord rules: a template keeps a stock column, names are printable
  ASCII of at most 30), so `strings="N"`, `string0..N-1`, `fret6/7`, `finger6/7`,
  note and chordNote `string` are package-order and indices 0..5 are the
  six-string projection; `standardTuning` is 0 for more than six strings.
  `RsXmlSongReader` applies the inverse once after reading
  (`RsXmlSongStringOrder`). **A lowest-first 7-string XML from before #904 is NOT
  read specially** (re-export it from the `.rscp`). Six-string guitars and basses
  stay byte-identical. Six-wide tools: EOF aborts the import on a note with
  `string` > 5; Rocksmith2014.NET reads it but its XML-to-SNG converter indexes
  six-wide arrays. Pinned by `RsXmlRs7StringOrderTests`.
- **The six-string projection for those tools (#911)**: `Rs7Projection.TryCreate(arr,
  sixStringOnly: true)` runs the split-mode breakup, then drops every note on a package
  string above 5, strips the extra template columns (also on the hand-mute templates),
  and trims the copy to six strings and six tuning entries, so `RsXmlSongWriter` writes no
  `strings` attribute and no `string6+`; `standardTuning` derives from the six-string
  copy. `Rs7Projection.Loss` (`SixStringLoss`: notes dropped, chords that sounded only
  there, chords that lost some strings) feeds `RsXmlSongWriter.LastLoss` and one log
  WARNING per arrangement. Who uses it: the DTRS XML bundle and the `.rs2dlc` tuning ALWAYS
  (DLC Builder); Save As `.xml` only with `AppConfig.RsXmlSixStringProjection` ("RS XML
  for 6-string tools", Settings > Files; default off, the spec XML above). The rs7string
  package (SNG) and the `.rscp` keep every string. Charter (Lordszynencja) reads more than
  six strings but numbers them lowest first, so the spec order is wrong there too
  (finding on #911). Pinned by `RsXmlSixStringProjectionTests`.
- **An 8-string guitar is not exported** (`Rs7StringLayout.ExportRefusal`): a
  validator ERROR (`KeyEightStringMod`), and `SngFromDomainMapper.BuildInstrumental`
  throws the same reason, which the builder reports as the skipped arrangement. The
  format model stays general (`RsExtensionBlock.ForStrings(8)` spells the 8-string
  block). **A bass never gets a block** (RS7X v1 reserves the bass instrument): a
  5-/6-string bass keeps the stock six-slot spelling, which official Rocksmith bass
  charts use.
- **Chord columns past the sixth string go into the RS7X block** (the mod's
  spec "RS7X: the SNG extension block", v1, in the rs7string repo; `Export/Sng/Model/RsExtensionBlock`, written by `Export/Sng/RsExtensionBlockWriter`, read by the package reader): appended INSIDE the
  zlib body right after MetaData (the stock loader stops reading there), sections
  `STRS`, `CHDT`, `CHNT` in that order even when empty, written for EVERY
  arrangement with more than six strings and for nothing else (a 6-string guitar or
  4-string bass SNG stays byte-for-byte stock). `Rs7BlockBuilder` collects it while
  the mapper writes the stock sections: one `CHDT` row per emitted template, one
  `CHNT` row per ChordNotes record ADDED, so the counts match by construction.
  Writer rules it keeps: the six stock columns stay a projection (no extended pitch
  in a free stock MIDI slot); templates are never merged on their stock columns
  alone; a template keeps a stock column (`Rs7Projection` turns a chord on extra
  strings ONLY into single notes and leaves such a template out with its hand
  shapes); a ChordNotes record is created when ANY string has technique data and
  is deduplicated on stock + extended data — an OPEN 7th string makes one too, as
  an open stock string does, because the game draws the open gem from that bit
  (rs7string #18 stage 2; the spec text of 2026-10-13 11:32 still says otherwise,
  `rs7chart` and its packages were changed at 12:22); `name[31]` stays 0 and a 7-string chord name is cut to 30
  printable ASCII characters; section `StringMask` bit 6 counts chord notes too.
- **`SngFromDomainMapper` never sees the chart order**: `Rs7Projection` copies the
  arrangement into package order first and runs `LinkLandingBreakup` itself; the
  mapper writes strings 0..5 and hands 6+ to `Rs7BlockBuilder`.
- **The block is the DEFAULT; "split" is an option** (#743,
  `RocksmithBuildOptions.Rs7ChordMode`, the export dialog's "Split 7th-string
  chords" box, shown only for a 7-string guitar, for an rs7string build from before
  the block). Split writes a chord on an extra string as rs7chart's split mode does:
  native columns stay a chord (a single note when only one is left), every extra
  string becomes a simultaneous single note, no template keeps an extra column
  (the block's `CHDT` is all -1), and a template only such chords played is left
  out of the SNG (the export copy only). The save companion always builds the
  default.
- **No `chords-ext.json` sidecar is written any more** (#777): the block replaced
  it. An export deletes the ones an older build of the same package left
  (`Rs7ChordSidecar.RemoveStale`, exact names only); the IMPORT still reads them
  for older packages (below).
- **The reference packages are rebuilt BYTE FOR BYTE** (#743, #777, by the
  plugin's reference-rebuild tests, skipped without `DOTCHARTER_RS7_DIR`): the
  control package as a whole file; the 7-string and rs7chart's split, pairs,
  triads, full-chord and 6-string control packages per entry (other writers made
  their containers; the test pins why), and for rs7chart's three chord packages the
  stock sections and the RS7X block each byte-identical on their own. The random
  ids and the time stamp are pinned through `RocksmithBuildOptions.Identity`
  (`RocksmithBuildIdentity`, internal, test-only). Named divergences, each matched
  exactly once: rs7chart's `barreChords` / `doubleStops` / `SongDiff*` (it ports
  the old barre rule and judges adjacency by package index — `KnownDivergences`);
  the pairs package's chord NAMES (two-string shapes ship unnamed under
  `DoubleStopNameOptions.Default`; `SngRules`); and `rs7test_7str`, which predates
  the block (the rebuild must end in exactly the empty 7-string block).
- **The manifest adds `Rs7String6` / `Rs7String7` after `string5` — never
  `string6`**, which overflows the game's six-int song-list reader.
- **Every package is ENCRYPTED by default, 7-/8-string included (#804)**
  (`RocksmithBuildOptions.PlainPackage` null/false: TOC flag 4, SNG flags 3; the
  RS7X block sits inside the zlib body, which the game decrypts first). PLAIN
  (TOC unencrypted, SNG flags 1; loads only with the RS7String 7-string
  mod) is the export dialog's explicit Package option (`ExportTargetViewModel`). Without the
  user's keys in appconfig.json EVERY package is plain with a warning, the
  picker is disabled on Plain and the dialog says so (#756, `RocksmithKeysTests`;
  the key rules are in [../parsing-psarc.md](../parsing-psarc.md)). A Mac package
  is encrypted only. Pinned by `Rs7PackageExportTests`, `ExportPackageChoiceTests`.
  A stale sidecar is looked for beside the
  FINAL path (`ChordSidecarPackagePath`; the save companion builds to a staging
  name). Pinned by `Rs7PackageExportTests`, `Rs7BlockExportTests`,
  `Rs7ExtensionBlockFormatTests`, `Rs7PlainFixtureTests` (the read-back halves are the plugin's).
- **The Mac package (`_m.psarc`, #782) differs from the PC one in four things and
  nothing else** (`Export/Package/RsPlatformLayout`): SNGs in `songs/bin/macos/`
  encrypted with the Mac SNG key (`RocksmithSngWriter.WritePackedMac`; same body,
  same little-endian layout); banks and wems in `audio/mac/` (the same bytes); the
  aggregate graph's bank tag `macos` for `dx9`, plus a `macos` tag on each SNG item.
  Manifests, xblock, album art, flat models, showlights, toolkit.version and appid
  are identical. Sources: Rocksmith2014.NET `Platform.fs` `getPathPart`,
  `GraphItem.fs` `sng`/`bnk`, `PlatformConverter.fs` `pcToMac`; checked against
  official PC/Mac pairs (the plugin's layout-parity test, skips without the install).
  **A Mac package is always encrypted** (TOC + Mac SNG key, `RocksmithKeys.CanWriteMac`)
  and **never for a 7-/8-string guitar** (rs7string is Windows-only):
  `RocksmithPlatformTargets.Resolve` refuses a Mac-only build and drops Mac with a
  warning from PC + Mac; the dialog says why first (`RocksmithMacBlock`). PC + Mac
  builds every shared entry once and writes `<name>_p.psarc` + `<name>_m.psarc`
  (`RocksmithBuildResult.PackagePaths`). **The PC package is pinned byte for byte to
  the pre-#782 builder** (`RocksmithPlatformPackageTests`, every id and the SNG IV
  pinned through `RocksmithBuildIdentity`, synthetic keys) — a change that moves a
  PC byte must update that pin on purpose.
- **`ChartExportValidator` makes a 7-string guitar a WARNING naming the mod**
  (`KeyExtendedRangeGuitar`), an 8-string guitar an ERROR (above), and a string past the arrangement's own count stays an Error
  (`SngStringLimits.MaxNoteStringIndex`).
- **The song file's fixed widths are `Export/Sng/SngFormatLimits`**: 36 difficulty
  levels (the per-section string-mask table), 32,767 notes plus chords per level (note
  ids and links are 16-bit), chord-template frets and fingers in signed bytes, and the
  string counts a package spells (guitar 8, bass 6, `Rs7StringLayout.MaxStringsFor`).
  The validator reports each as an Error (`KeyTooManyLevels`, `KeyTooManyNotes`,
  `KeyFingerOutOfRange`, `KeyStringCountUnsupported`, `KeyStringBeyondTuning`); the mapper
  throws `InvalidDataException` on levels, notes and string count, and clamps a template
  fret or finger. A Guitar Pro export asks the narrower `ValidateGuitarPro` (a sound on a
  string the tuning lacks) before the picker opens. Fixed-width and length-prefixed names
  (`RocksmithSngWriter.WriteFixedString`, `EofPackWriter.WriteString`) cut through
  `Infrastructure/Utf8Truncation`, on a code-point boundary. Pinned by `ExportBoundsTests`.

## Capo at the export boundary

- **GP EXPORT applies the exact inverse (`GpCapoFrets.ToRelative`) — both halves
  must stay in place or neither.** `GpExporter.BuildScore` writes `Staff.Capo`,
  so Guitar Pro reads everything it emits as capo-RELATIVE; writing our absolute
  numbers there put every fretted note `capo` semitones sharp. It used to write
  `Math.Max(0, fret)`, which round-tripped only because the importer was wrong in
  the same direction — fixing import alone (0.34.0) made the export a real,
  silent regression. **There are exactly TWO fret write sites**, both taking an
  explicit `capo` parameter threaded `BuildScore → FillBar → EmitEvent →
  AddSounds → BuildNote` and `FillBar → EmitGrace`: the note fret in `BuildNote`
  and the grace-hint fret in `EmitGrace`. **Never hoist that capo to a
  static/instance field** — the save companions build detached, so two exports
  can overlap and shared state would stamp one export's capo onto the other's
  frets. `SlideTo` is NOT a third site: it only picks an `AT.SlideOutType`
  direction (a comparison, capo-invariant) and alphaTab resolves a slide's
  destination from the following note, which is already converted.
  **A sub-capo fret (`1..capo`, malformed — inspector `L01` /
  `ChartExportValidator.KeyNoteBelowCapo`) is written as `0`**, not as a negative
  fret: the capo already stops the string higher up, so such a sound's offset is
  `max(fret, capo) == capo` — exactly what the open string sounds — and folding
  it to `0` is the only choice that both preserves the pitch and stays a legal
  tab number. Fret `== capo` folds too, and is what
  `ArrangementAutoFixService.FixCapoFrets` actually produces. This deliberately
  diverges from `.eof`, whose `EofNoteBuilder.EncodeFret` must clamp the same
  case UP to relative fret `1` (a reported, semitone-sharp approximation) because
  the format cannot spell it. Capo `0` is a strict no-op — byte-identical to the
  `Math.Max(0, fret)` it replaced. Pinned by `GpExporterCapoTests`, which asserts
  on the WRITTEN gpif: a re-import round trip adds the capo straight back and
  passes either way, which is exactly how the bug hid.

- **The other export paths need no capo transform, verified 0.34.0**: RS2014 XML,
  SNG and rscp are ABSOLUTE like our model (each carries the capo as its own
  field — `<capo>`, `MetaData.CapoFretId`, `capo=` — and writes raw frets, and
  each reader is symmetric). `.eof` is the only other RELATIVE format and
  `EofNoteBuilder.EncodeFret` already subtracts. `SngFromDomainMapper`'s two capo
  uses (grep `var capo = (sbyte)Math.Max` and the `capo > 0 &&` ternary just
  below it — `:431`/`:442` as of 2026-08-08) are a sounding-PITCH calculation
  (`max(fret, capo)`) for chord MIDI, not a stored-fret transform — don't "fix"
  it.

## `songLength` is `min(chart length, audio length) - 1 ms` — and the audio length is PASSED IN

  **`songLength` is `min(chart length, audio length) - 1 ms` (EOF_SRC\rs.c:590-593,
  :1762) — and the audio length has to be PASSED IN.** `ChartProject` deliberately
  carries no duration (it lives on `ProjectState.TotalAudioLengthMs`), so
  `IChartSerializer.Save`, `IProjectDocumentService.SaveChart`/
  `SaveDlcBuilderXmlBundle` and `RsXmlSongWriter.Write`/`WriteTo` all take a
  trailing optional `double? audioLengthMs`, threaded down from
  `ProjectSaver.SaveProjectCoreAsync` (read on the UI thread alongside the
  save snapshot). **Never resolve it from ambient state instead** — the same
  serializers write batch-lint output and recovery/checkpoint snapshots for charts
  that are NOT the open session, and stamping the open song's audio length on those
  is silently wrong. `null` is a real, correct value: EOF's no-audio-loaded branch,
  which falls back to the chart's own length. The clamp matters in the other
  direction — a chart LONGER than its audio must end with the audio or it never
  reaches an end in-game. RSCP ignores the argument (no song-length field).

- **The psarc path takes it as `RocksmithBuildOptions.SongLengthMs`, and the
  COMPANION path did not pass it at all until 0.61.** `ISaveCompanionService
  .BuildPsarcAsync` had no parameter for it, so the SNG mapper's
  `ResolveSongLengthMs` fell back to the chart's own extent — measured on the test
  fixture, a 9 s song shipped as 15.5 s, and it disagreed with the DTRS XML bundle
  the SAME save writes beside it. `ProjectSessionService` already held
  `work.AudioLengthMs` and could have passed it. Pinned by
  `Services/Export/SaveCompanionServiceTests
  .The_companion_psarc_takes_its_song_length_from_the_audio`.

## An SNG tone EVENT is an index into the manifest's slot table

- **`RsTone.ToneId` names no tone — it indexes the `Tone_A`..`Tone_D` list the
  arrangement manifest declares**, so the SNG and the manifest must be built from
  ONE list. `RsManifestGenerator` reads `RsPackageArrangement.ToneSlots` (which
  honours `RocksmithBuildOptions.ToneMap`, the override the tone designer
  supplies) while `SngFromDomainMapper` resolved its own from the arrangement's
  `ToneA`..`D` plus its tone-change names. A reordering override therefore shipped
  a psarc whose every tone change selected the tone at that index in the OTHER
  list — invisible in the editor, audible in game. `BuildInstrumental` now takes
  the slot table (`toneSlots`); `RsToneSlots.Resolve` remains the fallback when no
  override exists. Pinned by `Services/Export/RocksmithPackageBuilderTests
  .Tone_change_events_index_the_slot_table_the_manifest_declares`.

## `SongAverageTempo` is DERIVED too — from the beat grid, not `ChartProject.Bpm`

- **Same class as `songLength`.** `RsPackageMetadataFactory.Create` set
  `AverageTempo = (float)Math.Max(1.0, project.Bpm)` and `RsManifestGenerator`
  wrote it to `SongAverageTempo` — the number the GAME'S SONG LIST shows —
  while `RsXmlSongWriter` derived `<averageTempo>` from the beat grid.
  `ChartProject.Bpm` is the NOMINAL/initial tempo, so on any tempo-mapped chart
  the two artifacts built from ONE chart described different songs. Both now go
  through `RsXmlSongWriter.AverageTempo(beats)` (made `internal` for exactly
  this), which is also what `SngFromDomainMapper.AverageTempoBpm` computes.
  Pinned by `Services/Export/RsPackageAverageTempoTests`.

## A chart states its tuning TWICE, and every reader fills BOTH

- **`Arrangement.TuningPitches` holds the per-string OFFSETS and
  `Arrangement.Tuning` holds the note LETTERS** (see `TuningOffsetResolution`'s
  remarks — the offsets win when both exist, which is why a half-filled pair
  fails silently). `RsXmlSongReader.ReadTuning` filled only the offsets and was
  the ONE outlier: `GpArrangementParser`, `GpLegacyImporter`,
  `RscpArrangementParser` and the package reader's SNG mapper all call
  `TuningUtilities.GetTuningNames(StringCount, offsets)`. An `.xml` import
  therefore kept `Arrangement`'s default `{E,A,D,G,B,e}` whatever the file said —
  wrong lane-gutter labels, tuner, tuning chip, `ChordNameSuggester` and sheet
  export, and on a 4-string bass a SIX-entry letter array beside a four-entry
  offsets vector, i.e. the two statements disagreeing about the string count.
  Pinned by `Services/Parsing/Xml/RsXmlTuningNameReadTests`.

## Artist / album / year live on the ARRANGEMENT too, and the manifest has to look there

- **`Arrangement.ArtistName` / `ArtistNameSort` / `AlbumName` / `AlbumYear` are
  real model fields, filled by `RsXmlSongReader`, and `RsXmlSongWriter` PREFERS
  them over the chart-level values** (`FirstNonBlank(arr.X, project.Chart.X)`).
  `RsPackageMetadataFactory.Create` read only `project.Chart.*`, so a
  chart whose metadata arrived on the arrangement — the normal shape after a
  psarc or RS-XML import — shipped as **"Unknown Artist" with an empty album**
  in the manifest strings the GAME'S SONG LIST shows, while the XML in the same
  bundle carried the real ones. `RsPackageMetadataFactory.Create` now falls through the first
  non-vocal arrangement the way the XML writer does; the authored sort name
  rides `RsPackageMetadata.ArtistSortOverride` (empty keeps the old
  article-stripping `SortValue`). Note this also feeds `RocksmithDlcKey.Create` when no
  key is given — that is intended, the key should name the real artist.
  Pinned by `Services/Export/RsPackageArrangementMetadataTests`.

## GP export makes authored notation WIN

- **`Export/GuitarPro/GpExporter`** (GP7 via alphaTab). GP export
  (0.25) makes authored notation WIN: a note/chord's `NotationDuration` sets the
  beat's duration/dots/tuplet (`GpRhythmFraction.TryUnfold`, else a tied chain on
  the 48-tpb grid), a `GraceFret` becomes a before-beat grace beat (since
  0.33.2 a CHORD's per-string `ChordNote.GraceFret` hints all ride ONE grace beat
  — GP's own chord-grace shape, which the importer reads straight back), and
  `NotationVoices` sidecars export as GP voices 2+. Bars must be authored-full —
  Guitar Pro renormalizes a bar whose beats don't fill it (an isolated triplet
  won't survive; a complete 3-note group will). The capo half of the same
  exporter is [above](#capo-at-the-export-boundary).
- **One tick grid per BAR, in that bar's beats.** `TicksPerBeat` (48) counts the
  bar's own beat (an eighth in 6/8), while `NotationDuration` counts quarter
  notes, so an authored length goes through `QuarterTicks(nd, denominator)`;
  counting the quarter as a beat wrote every rhythm of an x/8 bar at half length.
  **A sound longer than the rest of its bar CONTINUES into the next bar(s) as
  tied beats** (`BarCarry`), as Guitar Pro writes it — cut at the bar line, the
  re-import read the gap as a staccato rest and accented the note. **A
  hammer-on/pull-off marks its ORIGIN, the previous sound on the destination's
  string** (`Legato.Of`), wherever it is — not just the next event of the same
  bar. Pinned by `GpExporterBarLineTests` and `GpMeterGridImportTests`; measured
  over the private corpus with `tools/tab-corpus` (round trip).
- **Techniques (#93).** A hammer-on with NO earlier sound on its string has no
  origin to mark: it goes out as Guitar Pro's LEFT-hand tapping (`LeftHandTapped`),
  which the importer reads back as a hammer-on. Right-hand tap, slap and pop are
  BEAT techniques in Guitar Pro (`Beat.Tap` → `Tapped` on every note, `Beat.Slap` /
  `Beat.Pop` → `Slapped` / `Popped` on the beat), so any sound of a slot carrying
  one marks the beat. alphaTab writes a harmonic as `HarmonicType` + `HarmonicFret`
  only (no `Harmonic` switch), so the importer keys on either. Pinned by
  `GpExporterTechniqueRoundTripTests`.
- **Every written length is an EXACT tied chain** (`GpTickChain`, #93). Longest
  value first, over and over, could not spell some lengths (7 ticks: 6, then 1
  that no value writes, though 4 + 3 is exact) and DROPPED the tick, so the bar
  came out short and every later onset in it moved. `TrySpell` keeps the
  longest-first spelling when it is exact and otherwise finds the shortest exact
  chain; `Fit` picks a sound's written length as the writable length nearest its
  sustain whose rest up to the next onset is writable too (a one-tick rest is
  held instead). Pinned by `GpTickChainTests`.

## The DLC key is ASCII, and every package name comes from it

- **The DLC key is `[A-Za-z0-9]` only (#747) — `char.IsLetterOrDigit` is true
  for kana and kanji and must never pick a key character.** The game does not
  list a package whose key (and so its sng/manifest/xblock/bnk/graph/showlights/
  art names) holds any other letter. `Export/RocksmithDlcKey.Create` (behind
  `RocksmithDlcKey.Create`, which the export dialog, the save
  companion's `<key>_p.psarc` and `DcSongWriter` all call) folds Latin
  diacritics ("Beyoncé" → `Beyonce`), drops the rest, and appends an 8-hex
  SHA-256 of the ORIGINAL artist + title when a letter was dropped or the key is
  under 6 — stable and unique; an all-ASCII artist + title keep their old key
  byte for byte. A TYPED non-ASCII key is refused (`SanitizeDlcKey` throws, the
  dialog's `HasInvalidDlcKey` blocks), never rewritten.
  `RocksmithPackageBuilder.EnsureEntryNamesFromKey` refuses to write an archive
  with a non-ASCII entry name or a non-fixed name that does not carry the key.
  Below it, `PsarcWriter` THROWS on a non-ASCII entry name (it used to write the
  name list with `Encoding.ASCII`, which turns a kana into `?`, so the list and
  the manifests disagreed) and `SoundBankWriter.Generate` refuses a non-ASCII
  bank name (its `Song_<key>` STID and `Play_<key>` event must spell what the
  manifest's `SongEvent` names) — pinned by `PsarcWriterAsciiNamesTests`.
  The DISPLAY fields are never romanised: `ChartExportValidator` warns
  (`KeyDisplayNameGlyphs`) when the title, artist, artist sort or album holds a
  character past Latin Extended-B (`RocksmithDlcKey.LacksGameGlyphs`). The
  `.rs2dlc` keeps DLC Builder's own key rule (`Rs2DlcStrings`, already ASCII).
  Pinned by `RocksmithDlcKeyTests` and the Japanese-title cases in
  `RocksmithPackageBuilderTests`.
