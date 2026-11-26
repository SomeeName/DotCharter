# Export: Wwise-free audio + the save companions

One file per subject under `export-companions/`. Read the one you need:

- [wem-and-audio-decode.md](export-companions/wem-and-audio-decode.md) — why we encode .wem ourselves, the two CDLC breakers, WwiseImaAdpcm, PcmAudioLoader
- [save-companions.md](export-companions/save-companions.md) — save companions (SaveCompanionService)
- [export-cost-and-open-items.md](export-companions/export-cost-and-open-items.md) — export cost measured, the four non-reproducible things, still open
- [export-boundary-rules.md](export-companions/export-boundary-rules.md) — the export-boundary rules: linked chords, chord names, repeated chords, phrase names, moveR phrases, chordId, error invention, arrangement properties, tuning, capo, songLength, tone events, average tempo, tuning stated twice, metadata, GP export
- [extended-range-export.md](export-companions/extended-range-export.md) — automatic RS Extended Range packages and XML for 7-/8-string guitar and 5-/6-string bass, stock identity, limits and legacy XML reading
- [dlc-builder-parity.md](export-companions/dlc-builder-parity.md) — the export dialog's DLC Builder surfaces: Version, test export, release file name, Auto volume

**Play-only content is never exported (#822).** A chart a source marked play-only
carries `ChartProvenance.PlayOnly` on the project and
each arrangement (stamped by the source that imports it, e.g. an optional plugin;
saved as `<dcProvenance>playOnly</dcProvenance>` / `<dcArrangementProvenance>`;
the value earlier builds wrote, `officialRocksmith`, still reads as play-only). Every exporter calls `Export/PlayOnlyExportGuard`
at its OWN entry point: the builders throw `PlayOnlyContentException`, the save
companions and the `DTRS XML/` bundle skip silently (a companion may not fail a
save), a paste of play-only content into an unrestricted arrangement is Blocked
(`PhrasePasteCompatibility.KeyPlayOnly`), and the menus show
`Export_PlayOnly`. Play Mode, practice and viewing (the sheet pane's
`AlphaTexExporter`) are not guarded. **A new exporter joins
`PlayOnlyExportGuardCoverageTests.GuardedEntryPoints`** — the lint fails on any
`*Exporter` / `*PackageBuilder` type or `Export*` command outside that list.

**The export validator's findings each carry a decision** — the verb that fixes
it (`Export/Validation/ExportFindingFixes`, read through
`ExportValidationFinding.Fix`) or the reason there is none. A new
`ChartExportValidator.Key*` fails `ExportFindingFixesTests` until it is in one
table. The verbs and the chart's "Apply DLC Builder fixes":
[editing-invariants/dlc-builder-fixes.md](editing-invariants/dlc-builder-fixes.md).

**DLC Builder's build steps run on every export's COPY (#952).** DLC Builder fixes
every arrangement it builds (`ArrangementImprover.applyAll`) and then, for one with a
single difficulty level, generates dynamic difficulty (`Generator.generateForArrangement`,
phrase search at 80 %, Simple level counts). `Export/DynamicDifficulty/` ports both
from Rocksmith2014.NET (MIT): `DdImprover`, `DdGenerator` and its choosers, and
`DdLevelCountModel` (the ML.NET `MLModel.zip` FastTree ensemble, evaluated without
ML.NET). `DlcBuilderBuildSteps.Apply` writes the arrangement with `RsXmlSongWriter`
(so every rule in this folder has already applied), runs the steps on that XML, reads
it back with `RsXmlSongReader` and grafts the levels, templates, phrases and event
points onto a clone, at the chart's own beat grid. The package builder
(`RocksmithBuildOptions.DlcBuilderSteps`), the companion psarc and the DTRS XML bundle
(so the `.rs2dlc` names XML that already has them) all run it; the project is never
touched. Settings ▸ Files: `AppConfig.ExportApplyDlcBuilderFixes`,
`ExportGenerateDynamicDifficulty` (both on), `ExportDdLevelCountMode` (Simple; MLModel
selectable). A step that throws ships the arrangement as charted, with a warning.

**The DLC Builder project export mirrors the packages (#1036).** The DTRS XML bundle and
the `.rs2dlc` apply `LowBassTuningFix.ForPackage` like the `.psarc` (the dialog's box, or
`AppConfig.ExportFixLowBassTuning` on save), and a chart with RS Extended Range paths gets a
standard project DLC Builder builds plus an `_rsxr` one. `Export/DlcBuilder/Rs2DlcEdition.cs`
(`Rs2DlcEditions.Entry`, `SiblingFiles`) is the ONE place that decides which arrangement goes in
which project and which bundle file it reads; the bundle writer writes exactly those files.
A 7-/8-string guitar's STOCK TWIN (`Rs2DlcEditions.HasStockTwin`) is a stock arrangement of the
same type whose name starts with its own ("Doloris" -> "Doloris (6 String)", trimmed,
case-insensitive), each stock arrangement pairing once; a twin replaces the projection in the
standard project, any other extended guitar is projected there. In the standard project a path
left without a Main (an Alternate twin, say) ships its first arrangement as Main, as the standard
`.psarc` does (`Rs2DlcProjectBuilder.PromoteLonePathMains`, #1043); the chart never changes. When the chart loses its extended
paths, save and export remove the old `_rsxr.rs2dlc` only if `CompanionOwnership` says we wrote it
unchanged (`RemoveStaleExtendedRangeProject`).
`Rs2DlcLowBassAndExtendedRangeTests` checks the standard project against DLC Builder's I41 and
I36 rules.

- **Golden parity is against the REAL F#** (`DdGoldenParityTests`, fixtures in
  `DotCharter.Tests/Services/Export/DynamicDifficulty/Fixtures/`): both level-count
  modes, the fixes, and fixes-then-DD; `DdLevelCountModelTests` holds the tree
  evaluator to 2,000 ML.NET scores bit for bit. Chord IDs are compared by template
  content: the F# runs phrases in parallel and numbers new templates by thread timing.
  The contract test and its expected F# output are compared unchanged.
- **Skipped on purpose:** `removeExtraBeats` and the `removebeats` event's beat
  removal (the event itself is consumed). Both move or delete BEATS, which belong to
  the project and every arrangement in the package; the `songLength` rule in
  [export-boundary-rules.md](export-companions/export-boundary-rules.md) resolves chart vs audio length at the boundary instead. Nothing else conflicts:
  the fixes' phrase-name filter is the same character set `RsPhraseName` already
  wrote, `removeRedundantAnchors` keys on fret AND width like our writer, and the
  crowd events it adds are the ones `SngFromDomainMapper.AddCrowdEvents` adds when
  absent.
- **Strings past the sixth:** the generator works lowest-string-first with templates
  as wide as the guitar ("the highest strings" are the top three); positional XML
  and RS Extended Range records are written from that generated copy
  (`DynamicDifficultyExportTests`).
- **Conversion fidelity:** reduced chord notes and templates keep strings from the same
  end. Two steps are deliberately NOT the F#'s (#636). The muted-string cleanup
  (`DdMutedChordCleanup`) never edits an authored template: a chord with muted and
  played strings is rebound to a deduplicated reduced copy, and its own strummed
  hand shape follows only when no chord or sounding note under it needs the full
  grip (arpeggios, foreign-template shapes and shared ranges keep their template).
  Level generation then reduces a hand shape and the chords under it as ONE decision
  (`DdTemplateRequestApplier.ApplyLevel`): the F# request is applied unchanged
  unless its grip would lack a string that a chord on a reduced grip
  (`DdChordTemplate.ReducedFrom`) sounds under the shape at that level, in which
  case the shape takes its full grip cut to exactly the strings sounded under it.
  Charts without mixed-muted chords generate exactly as before
  (`DdGoldenParityTests`); DLC Builder's own narrowing can still leave a shape
  without a string a note under it sounds, and that stays for parity. Pinned by
  `DdMutedGripInvariantTests` and `DdMutedHandShapeTests`.
  A third: the F# ends the last phrase at `songLength` and starts the first at its
  marker, so a sound past either (END written on its beat after the song length, a
  note after END, one before the first marker) fell out of every generated level.
  `DdPhraseData.For` stretches the first and last phrases over every sound of the
  level (`DdContentPastSongEndTests`, chart + XML + SNG).
  Chord-to-note conversion retains Ignore, and each level retains its authored
  `fretHandMutes` XML (copied to every generated level, including empty phrases).
  Chord-name normalization only changes musical minor qualities and trailing
  `CONV` / `-arp` / `-nop` tokens; embedded text in authored names is preserved.
  These corrections deliberately differ from the F# port and are covered by
  `DdConversionRegressionTests`.
- **Known limits:** the domain has no linked difficulties, so `newLinkedDiffs` from the
  phrase search do not reach the SNG; the SNG mapper derives hero levels as
  `max/4`, `max/2` where DLC Builder rounds half to even.

**What the package writer shares with everything else lives in `Export/Rocksmith/`,
never on `RocksmithPackageBuilder`** (#884, ahead of the PSARC reader leaving this
repository): the DLC key rules are `Export/RocksmithDlcKey`, the default tone
slots `RsToneSlots.Resolve` (SNG + RS-XML writers, the validator, the Tone
Designer's prefill), the custom `MasterID_RDV` floor `RsMasterIds.CustomMasterIdFloor`
(also the reader's provenance test) and the manifest metadata
`RsPackageMetadataFactory.Create` (builder + validator).

**What a round trip changes ON PURPOSE (#907).** Reading a custom package,
re-exporting it and reading it back changes six things, every one of them a
writer rule, not a loss. The round-trip fidelity number (the package-reader
split baseline, #899, which reads packages back and so lives with the plugin, #889)
projects them out by name and counts what is left as REAL loss; the plugin's
round-trip tests prove each one on a self-made chart and that none of them hides
a loss.

| Name | What the writer does | Where |
|---|---|---|
| `double-stop-names` | a two-string shape ships unnamed, and so does every hand shape over it | `SngFromDomainMapper.AddTemplate` + `ChordNameSuggester.SuppressesAutoName` (`DoubleStopNameOptions.Default`); [export-boundary-rules.md](export-companions/export-boundary-rules.md) § two-string shape |
| `time-signature-events` | a `TS:n/d` event is derived from the beat map at every signature change; only an ADDED one is forgiven | `SngFromDomainMapper.AddTimeSignatureEvents` |
| `link-landing-breakup` | a chord a link lands on ships as loose notes | `Parsing/Xml/Song/LinkLandingBreakup`; [export-boundary-rules.md](export-companions/export-boundary-rules.md) § linked chord |
| `tone-definition-order` | tone bodies are written in slot order (base, A-D); the reader collects them in archive-manifest order. The set is the data | `RocksmithPackageBuilder.ResolveToneDefinitions` |
| `single-vocal-track` | a package carries ONE Vocals arrangement, the first with lyrics | `VocalsExportPolicy.PackageTrack` |
| `pitchless-lyric-note` | a lyric with no pitch (255) is written as 254; both mean "no pitch" | `SngFromDomainMapper.BuildVocals`, `Export/Lyrics/RsVocalsSecondsWriter.PitchlessNote` |

A new entry needs the writer code that makes the change on purpose, a quirk in
the plugin's round-trip test that provokes it, and a row here. Never add one
to turn a real loss green. `-Detail` writes `fidelity-detail.txt` beside the
baseline (outside the repo): what still differs, as content-free categories.
