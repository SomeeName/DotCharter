# Export: Wwise-free audio + the save companions

One file per subject under `export-companions/`. Read the one you need:

- [wem-and-audio-decode.md](export-companions/wem-and-audio-decode.md) — why we encode .wem ourselves, the two CDLC breakers, WwiseImaAdpcm, PcmAudioLoader
- [save-companions.md](export-companions/save-companions.md) — save companions (SaveCompanionService)
- [export-cost-and-open-items.md](export-companions/export-cost-and-open-items.md) — export cost measured, the four non-reproducible things, still open
- [export-boundary-rules.md](export-companions/export-boundary-rules.md) — the export-boundary rules: linked chords, chord names, repeated chords, phrase names, chordId, error invention, arrangement properties, tuning, capo, songLength, tone events, average tempo, tuning stated twice, metadata, GP export
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

- **Golden parity is against the REAL F#** (`DdGoldenParityTests`, fixtures in
  `DotCharter.Tests/Services/Export/DynamicDifficulty/Fixtures/`): both level-count
  modes, the fixes, and fixes-then-DD; `DdLevelCountModelTests` holds the tree
  evaluator to 2,000 ML.NET scores bit for bit. Chord IDs are compared by template
  content: the F# runs phrases in parallel and numbers new templates by thread timing.
- **Skipped on purpose:** `removeExtraBeats` and the `removebeats` event's beat
  removal (the event itself is consumed). Both move or delete BEATS, which belong to
  the project and every arrangement in the package; the `songLength` rule in
  [export-boundary-rules.md](export-companions/export-boundary-rules.md) resolves chart vs audio length at the boundary instead. Nothing else conflicts:
  the fixes' phrase-name filter is the same character set `RsPhraseName` already
  wrote, `removeRedundantAnchors` keys on fret AND width like our writer, and the
  crowd events it adds are the ones `SngFromDomainMapper.AddCrowdEvents` adds when
  absent.
- **Strings past the sixth:** the generator works lowest-string-first with templates
  as wide as the guitar ("the highest strings" are the top three); the RS7X block is
  built from the generated copy, so it matches by construction
  (`DynamicDifficultyExportTests`).
- **Known limits:** the domain has no linked difficulties, so `newLinkedDiffs` from the
  phrase search do not reach the SNG; the SNG mapper derives hero levels as
  `max/4`, `max/2` where DLC Builder rounds half to even; position-only fret-hand-mute
  markers are not kept on the generated levels (DLC Builder's levels have none either).

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
