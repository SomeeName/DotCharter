# Import: Guitar Pro tabs + audio (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before
touching `Parsing/GuitarPro/` or anything under `Import/`.** The tab importers
(gpif and the GP3-5 binary reader) share one post-processing pipeline and are measured against each other and
against the hand-corrected `TestData/EOF` corpus — a rule changed on one side
and not the other is the failure this file exists to prevent.

**The Songsterr importer, a third path through the same pipeline, was deleted
with its client, dialog and tests (#820).** Where a rule below still names
`SongsterrConverter` or "the Songsterr path", it records why the rule is shared;
that path no longer exists.

Also here: the generators every tab import runs (`ChordHandShapeBuilder`,
`GpHandShapeBuilder`, `FretAnchorGenerationService`) with the measured numbers
that falsified the obvious alternatives.

See also: [editing-invariants.md](editing-invariants.md) (what the chart model
guarantees once the import is done) · [parsing-psarc.md](parsing-psarc.md)
(PSARC/EOF/rscp I/O) · [export-companions.md](export-companions.md) (the export
half of the capo and tuning rules) · `DotCharter.Core/AGENTS.md` (the fret/capo
invariant itself)

---

- **An imported chord must NAME its template — `chord.TemplateId`, set by
  `ChordHandShapeBuilder` (2026-08-22).** The importers used to set `TemplateId`
  on the HAND SHAPE only and leave every chord on the `-1` sentinel until a save
  round-trip (`RscpTemplateManager`) filled it in. But ANCHORS are generated
  during the import, and the two things that read a chord's fingering both go
  through that id — `FretAnchorGenerationService` (`chordTemplates[chord.TemplateId]`
  → `GetFinger`) and the E04 detector `IndexFingerAnchorRule`
  (`chord.TemplateId < 0 ? null : …` then `continue`). With `-1` **both were dead
  on every tab import**: the whole fingering-aware root logic (P4/P5, whose own
  comment says it stops "a [5..8] window absorbing a finger-1-on-6 chord") never
  saw a finger, and the detector for the exact fault it was then producing
  skipped every chord. One cause, two symptoms, and the second hid the first —
  owner report: a {5,7,7,6,5} barre fingered 1 at fret 5 sat under an anchor on
  fret 4 (a fret-4 window still "covers" frets 5–7) and the issues tab said
  nothing. Measured on that file: 474 Lead + 1,170 Rhythm chords with no template
  id before, **0** after; the anchor for that bar moves 4 → 5.
  **This does NOT touch the EOF corpus baseline**: `ChordHandShapeBuilder` is
  tab-import only, and the corpus path reads RS-XML, where `RsXmlSongReader`
  has always set `TemplateId` from `chordId` — so the 91.9% figure was measured
  WITH fingering and the tab importers were the outlier running blind.

## Capo: Guitar Pro writes frets RELATIVE to the track capo

- **Guitar Pro writes frets RELATIVE to the track capo — the GP importers
  convert them to ABSOLUTE at the fret read** (`GpCapoFrets.ToAbsolute`: add the
  capo to a fretted number, leave `0` alone). Every fret-read site goes through
  that one helper and they must stay in step. This is EOF's rule and the same one
  `EofProGuitarTrackParser` applies to `.eof`; see `DotCharter.Core/AGENTS.md`
  for the model-wide invariant. Until 0.34.0 both importers stored GP's number
  verbatim, so every fretted note on a capo'd track was `capo` semitones flat and
  every fret-derived artifact (chord templates, suggested names/fingering, hand
  shapes, FHP anchors) was wrong with it — measured against the EOF corpus, anchor
  exact-agreement on the 9 capo'd arrangements was 31.9% vs a 75.7% uncapoed
  baseline; after the fix, 91.9%. **The conversion must stay at the parse point**
  (`GpNoteParser.ApplyNoteAttributes` for gpif, `GpLegacyImporter.ReadNote` for
  the binary path), i.e. BEFORE anything that reads a fret VALUE — the slide-out
  targets derived as `fret ± 5`, trill upper frets, grace ornaments, and then the
  whole `GpPostProcessor` → `ChordHandShapeBuilder` → `FretAnchorGenerationService`
  chain — so every one of them is correct by construction rather than by a second
  pass. **Sentinels are not frets and must never shift**: `SlideTo` `-1`
  (resolve-to-next) and `GpNoteParser.SlideInFromBelow/Above` (`-2`/`-3`) are
  negative markers, and shifting `-2` on a capo-2 track would silently re-read as
  "slide to the open string". Sums past the model's 24-fret ceiling clamp
  (`GpCapoFrets.MaxAbsoluteFret`). Capo `0` is a strict no-op; pinned by
  `GpCapoFretTests`.
  **A SYNTHESIZED fret floors at `GpCapoFrets.MinFrettedFret` = `max(1, capo)`,
  never at a hard `1`.** Six sites invent a fret rather than reading one — the
  unpitched slide-out landing (`fret - 5`) in `GpNoteParser.ApplySlideFlags`,
  `SongsterrConverter` and all FOUR copies in
  `GpLegacyNoteEffectsHelpers.ApplySlide` (GP3/4 values 3/4, GP5 bits
  0x04/0x08), plus the slide-in origin (`fret ± distance`) in
  `GpPostProcessor.MaterializeSlideIns` — and every one of them floored at `1`,
  which is only the capo-0 form of the rule. (Since #570 `GpPickScrape.Place`
  is a seventh: both its start and its landing are synthesized, and both floor
  at the capo.) Frets are ABSOLUTE, so on a capo-5
  track a GP fret 1 arrives as 6 and the `-5` landed FIVE frets below the capo:
  `ChartExportValidator` raises `KeyNoteBelowCapo` at **Error** severity there
  (fails `CanExport`, the save companion skips), the inspector reports L01, and
  `GpCapoFrets.ToRelative` folds the fret back to `0` on re-export — pitch kept,
  gesture destroyed. The floor is the CAPO and not `capo + 1` deliberately: that
  is the exact boundary `ChartExportValidator` (`fret != 0 && fret < capo`) and
  `ArrangementAutoFixService` (repairs a sub-capo `SlideTo` to `capo`) already
  enforce. A slide-in whose origin falls below the floor is DROPPED, not clamped.
  **`MaterializeSlideIns` and `MaterializeGraceSlides` also require a FRETTED
  landing** — both wrote `SlideTo = target.Fret` unguarded, so an open target
  minted `SlideTo = 0`. `LinkSlides` drops exactly that, but only while
  RESOLVING the `-1` marker; a slide these passes SYNTHESIZE never goes through
  it, and every later pass tests `SlideTo is > 0` so none of them sees a zero
  (`SngFromDomainMapper` then exports it as a real slide). Pinned by
  `GpSlideOntoOpenStringTests`.
  **The CAPO ITSELF is a file number and goes through `GpCapoFrets.Sanitize`
  (0.44) at all THREE reads** — the binary track block's raw `ReadInt32`
  (`GpLegacyImporter.ReadTracks`, sanitized once at the header read so the note
  path and `BuildArrangement` cannot disagree), gpif's
  `<Property name="CapoFret">` (`GpArrangementParser`) and Songsterr's JSON
  `capo` (`SongsterrConverter.Convert`). All three used to admit it behind a bare
  `Math.Max(0, …)`/`> 0`, so a capo near `int.MaxValue` reached `ToAbsolute`,
  where `gpFret + capo` OVERFLOWED to a large negative and sailed straight
  through the `Math.Min(24, …)` ceiling — the one helper whose job is to keep a
  fret representable returned about −2.1 billion. `ToAbsolute` now takes the sum
  in `long` as well, so its own promise holds even for an unsanitized caller.
  `Arrangement.Capo` is load-bearing beyond the frets (the FHP anchor floor is
  `capo + 1`, and it is written to the `.rscp`), which is why the fold happens at
  the read rather than at the conversion. Every real capo is a strict no-op.

## `Parsing/GuitarPro/` — the shared tab-import pipeline

- **BOTH GP importers unfold repeats through `GpMasterBarSequence`, and neither
  may grow its own unfolder.** A Rocksmith-style chart is LINEAR, so `|:`/`:|`
  and alternate endings have to be expanded into real bars at import; the
  playback order is decided in exactly one place
  (`GpMasterBarSequence.Unfold(IReadOnlyList<BarRepeat>)` — pure, index in,
  index out) and both formats map onto its `BarRepeat` shape: gpif from
  `<Repeat start/end/count>` + `<AlternateEndings>`, the binary path from
  measure-header bits 0x04 / 0x08 / 0x10. **The binary reader READ those three
  and threw them away until 0.48**, so a `.gp5` with `|: 8 bars :|` imported
  eight bars short and every later note landed early by that much — the gpif
  twin of the same score was correct, which is what made it read as "GP5 import
  is off" rather than as a dropped repeat.
  - **The count byte is spelled differently per version**: GP5's is the TOTAL
    number of passes, GP3/GP4's counts only the EXTRA ones, so the binary
    reader adds 1 below v5 (alphaTab's `Gp3To5Importer` makes the same split).
    gpif's `count` is a total. Anything ≤ 0 means "not stated" and reads as 2;
    the unfold clamps at 64 passes because the count drives an emit loop.
  - **A close with no matching open jumps back to the top of the current block**
    (bar 0, or the bar after the previous close) — GP's own playback. An open
    with no close falls back to straight playback so no bar is lost.
  - **The BYTE stream still carries one block per AUTHORED measure**, so
    `GpLegacyImporter` reads a repeated measure ONCE at its first unfolded start
    and clones the sounds it produced to the later starts
    (`ReplayMeasureOnLaterPasses`); positions shift by the whole-bar delta and
    every span stays relative. `GpBarInfo` and every `GpTempoPoint.BarIndex` are
    indices into the UNFOLDED list on both paths (`GpMetadataParser` does the
    same mapping for gpif tempo automations), so a tempo change inside a repeat
    fires on every pass.
  - Pinned by `GpRepeatUnfoldTests`: the pure unfold cases, the binary path
    across 5.10/5.00/4.06/3.00 (bar count AND note positions), the volta
    bitmask, and a gpif twin asserting both paths agree on the bar count.
    `LegacyGpFixtureBuilder`'s `FxMeasure` can author all three header bits.

- **Read one note's `<Property>` children ONCE (0.49).**
  `GpNoteParser.ApplyNoteAttributes` used to ask the same child list up to
  TWENTY-THREE separate
  `props.Elements("Property").FirstOrDefault(p => p.Attribute("name")?.Value == "X")`
  questions per note — String, Fret, Vibrato, LeftFingering, Slapped, Popped,
  Tapped, LeftHandTapped, HopoDestination, Muted, PalmMuted, Harmonic,
  HarmonicType, Slide, Bended/Bend and the seven `Bend*` floats — each
  re-walking the list and re-reading every `name` attribute. It is one `foreach`
  with a `switch` now, `??=` per slot so the FIRST element per name wins
  (what `FirstOrDefault` returned), and it allocates nothing; `Bended` and
  `Bend` share one slot so the first of EITHER wins, matching the two-name
  predicate it replaced. **If you add a property, add a `case` — not another
  scan.** Measured on the real 11-12 MB `.gp` files in `TestData/EOF/`:
  all-track import was 154 ms (3 tracks) / 271 ms (7 tracks) before, and the
  after number could NOT be separated from box noise (see the load caveat in
  [export-companions.md](export-companions.md)) — because the scans are not
  what dominates this phase. **Nor is the DOM (#308):** the 11-12 MB is the
  `.gp` ZIP, mostly an embedded MP3; `score.gpif` itself is 54 KB-1.05 MB, and
  its `XDocument` costs 3.5-10 ms of a 25-126 ms import. 75-90 % is
  `GpArrangementParser.Parse` and the post-processing it runs; profile that in
  a Release build first.
  Byte-pinned against the pre-change parser on three gpif files and one GP3-5
  binary by `ExportBytePinTests`
  (`DotCharter.Tests/Services/Export/ExportBytePinTests.cs`), which imports
  every track and hashes the whole graph through the `.rscp` writer.

- **The arrangement TYPE comes from the track NAME through the ONE shared
  heuristic — `Parsing/ArrangementTypeHeuristics.FromTrackName` (0.58).** All
  three tab paths (gpif in `GpArrangementParser`, the binary path in
  `GpLegacyImporter.BuildArrangement`, `SongsterrConverter.Convert`) and EOF's
  `EofProGuitarTrackParser.InferArrangementType` (which now delegates) read the
  same keyword table: bass → rhythm → combo → vocal → lead, then the caller's
  fallback. Until then the GP paths were `isBass ? Bass : Lead`, so a real
  five-track GP5 with two tracks NAMED "Rhythm (…)" imported everything as
  Lead — and the export dialog's badge was HONEST about it
  (`ExportArrangementItem.TypeLabel` binds `Arrangement.ArrangementType`), so a
  psarc built from that chart shipped `pathLead` for the rhythm parts. Each
  format's content signal is the FALLBACK only: string count ≤ 5 ⇒ Bass on both
  GP paths (the gpif one must derive AFTER `ApplyTrackProperties` fills
  `StringCount`), the `instrument` field on Songsterr. Name beats content — a
  6-string track named "Bass" is a bass; the old binary rule
  (`isBass && StringCount <= 5`) demoted it to Lead. Pinned end-to-end on all
  three paths by `GpArrangementTypeTests`.
- **Score-level song metadata travels on `GpImportMetadata` (0.58):
  `Title`/`Artist`/`Album`/`Tabber`.** Both GP importers used to read those
  strings off the file and DISCARD them (gpif never asked `<Score>`; the binary
  `ReadSongAttributes` consumed them for stream sync only), so every GP import
  left "New Song" / an empty artist in Song Metadata. gpif reads them in
  `GpMetadataParser.Populate`; the binary path captures them in
  `ReadSongAttributes` → `GpLegacyParseResult`. The import dialog's
  `ApplyScoreMetadata` (AddNew mode only) copies a NON-EMPTY value onto
  `ChartData.SongName`/`Artist`/`AlbumName`/`Charter` — exactly the fields the
  `.rscp` round-trips, per the field-with-no-writer rule — and an empty one
  never erases an authored value (GP writes blank strings for unfilled score
  properties). Songsterr is deliberately untouched: its dialog already fills
  the project from the API's song metadata. Pinned by `GpScoreMetadataTests`
  (both importers) and `GpImportViewModelTests` (the apply rule).
- **Track names are normalized at every READ — `GpTrackNames.Normalize`
  (0.58): whitespace runs collapse to one space, ends trim.** The doubled
  space is IN real files' bytes (the Ave Mujica reference GP5's second track
  is literally `"Lead (6  String Drop D)"` on the wire — an authoring tool's
  template with an empty middle term), and it flowed verbatim onto
  `Arrangement.Name` and every dialog. Four read sites, keep them in step:
  `GpImporter.GetAvailableTracks` (gpif track list),
  `GpArrangementParser.Parse`, `GpLegacyImporter.ReadTracks` (the one read
  both `GetTracks` and `BuildArrangement` share) and
  `SongsterrConverter.Convert`. Normalizing at the read matters beyond
  cosmetics: the GP import dialog matches tracks to arrangements BY NAME
  (`RefreshAvailableTargets`, the technique-only/diff merges), so a
  half-normalized pair silently stops matching. Pinned by `GpTrackNameTests`.
- **All three 0.58 fixes above are ALSO probed against real artifacts** —
  `GpRealArtifactImportTests` runs the whole importer over the Ave Mujica
  "Crucifix X" pair (name-derived types on both container generations, the
  doubled-space name normalized) and the Yorushika "Amy" pair (score
  metadata, bass typing). TestData-gated: soft-skips via `Assert.SkipWhen`
  off this machine, same pattern as the 107-byte chord-diagram net in
  `GpLegacyImportTests`.
- GP XML import (0.25): bar voice slot 0 → the playable level; slots 1–3 →
  notation-only `Arrangement.NotationVoices` sidecars (merge-tied + link-slides
  passes only, NO `AdjustSustains` — authored data verbatim). **A note on a
  string the track does not have is DROPPED at parse (0.33.2)** — check
  `GpNoteParser.LastStringValid` after every `ParseNote`/`ParseChordNote`. Every
  post-pass bounds-checks such a note away, so keeping it only left an inert
  phantom sound; the GP3-5 binary reader has always guarded, and the two must
  match. GP grace beats
  carry no bar time and must NEVER advance the import cursor (advancing shifted
  whole bars late). **A grace is a whole BEAT, not a note (0.33.2).** GP graces a
  chord by putting one grace note per string on the grace beat, so
  `GpArrangementParser`'s `PendingGrace` is a per-string COLLECTION plus GP's
  `<GraceNotes>BeforeBeat|OnBeat</GraceNotes>` flavour — the old per-note/
  per-string hint model dropped every chord grace outright (the reference file's
  3-note 3/5/5 → 5/7/7 shift vanished, leaving only the landing chord at the
  origin's timestamp). Placement is the shared `GpPostProcessor.PlaceGrace`: a
  BEFORE-beat grace steals time ahead of the beat, an ON-beat grace sits ON the
  beat and PUSHES the target (what EOF and Log Charter both emit), and **on-beat
  is also the FALLBACK when a before-beat grace has no room** — the old "no room
  ⇒ give up" rule silently dropped two more reference slides whose targets abut
  the previous note. A plain (non-slide) grace still degrades to the
  notation-only `GuitarNote.GraceFret` hint; `ChordNote` had no such field, so a
  plain CHORD grace was dropped until 0.33.2 added `ChordNote.GraceFret`: the
  gpif chord path now attaches one hint per graced STRING, and a grace beat may
  MIX flavours (the "sl." strings materialize an origin chord, the plain ones
  keep hints; a slide with no room to place degrades to a hint too).
  The gpif path materializes its own graces
  (it still has the flavour flag and the whole grace beat);
  `GpPostProcessor.MaterializeGraceSlides` is the GP3-5 BINARY path's
  materializer.
  **Triplet feel (swing) is placement-only and two-sided**: a swung beat's onset
  shifts (off-beat eighth 1/2 → 2/3) AND its effective sustain is the gap to the
  next beat's SWUNG onset — never the notated rhythm, which left link-next
  sustains short and handshapes overlapping. Beats that ARE tuplets never swing
  (the tuplet already spells the subdivision; swinging one collided it with its
  own group → coincident notes + a same-fret slide). `NotationDuration` keeps
  the authored rhythm — swing never leaks into the sheet. Slide guards:
  `LinkSlides` drops a slide that resolves to its own fret (keeps the legato
  link); a slide-out (or slide-to-next) on an OPEN string is dropped at parse —
  no fretted start, and importing it emits inspector L09
  (`SlideFromOpenString`) on every save. **The LANDING has the same rule
  (0.45):** `LinkSlides` drops the slide when the next sound on that string is
  an OPEN note, exactly as it drops the same-fret case — there is no fretted
  finish to slide TO. It has to be caught there, because every later pass tests
  `SlideTo is > 0` and `SngFromDomainMapper` exports anything that is not `-1`
  as a real slide, so a `SlideTo = 0` reached the chart AND the SNG. **A resolved pitched slide gets
  `LinkNext` (0.33.2)** — RS2014 needs it to sustain the slide into its landing
  note and EOF sets it on every slide; only claimed when the slide's sustain
  actually reaches that landing sound. **Slide IN (`Slide` flags 16/32) is a
  real pitched slide (0.33.2)**: parsed as the `GpNoteParser.SlideInFromBelow/
  Above` SENTINEL and turned by `GpPostProcessor.MaterializeSlideIns` into a
  short origin note N frets below/above the target that slides into it, with the
  target pushed back by a 16th. **N is the owner setting
  `GpImportSettings.SlideInFretDistance` (0.34.1, `AppConfig.
  GpImportSlideInFretDistance`, the "slide-in distance" General-settings row),
  default 2 (Log Charter's choice; EOF uses 1), clamped 1–3 by the materializer**
  — a deliberate divergence from the 0.33 EOF cross-tool baseline, so the
  slide-in origin-fret tests assert the default 2 end-to-end while `distance=1`
  (EOF) and `3` are pinned directly on `MaterializeSlideIns`. The sentinel
  must NEVER survive — `MaterializeSlideIns` runs FIRST in `Run` and `LinkSlides`
  scrubs any leftover negative slide target (the notation-only sidecar voices run
  `LinkSlides` without `MaterializeSlideIns`). A slide-in inside a CHORD only has
  its sentinel cleared; splitting a chord into an origin chord has no reference
  output to match. **A chord where only SOME strings slide stays per-string**
  (#474): `LinkSlides` resolves each string on its own, so in 5(sl.)+8~ → 6+~8
  only the 5 gets `SlideTo = 6`; the tied 8 keeps no slide and links on, and
  `LinkLandingBreakup` writes the 6+8 landing as single notes. That is the
  official RS spelling (slides live per string in `chordNotes`; no official
  chord links into a chord). EOF does not match it: its slide is per chord,
  exported as the same fret delta on every fretted string, so it would slide
  the 8 to 9 too. Pinned by `GpPartialChordSlideTests`.
  **A PICK SCRAPE is charted the way the owner charts it (#583, #599,
  `GpPickScrape`)**: **tap + tremolo + fret-hand mute (FULL) + an unpitched slide
  (`slideUnpitchTo`) in the scrape's direction + ignore** (ignore keeps it out of
  scoring). **A scrape on several strings is ONE chord with
  `Chord.SplitIntoNotes`** (Inspector ▸ Playback ▸ "Split notes", EOF's split
  status) and `Chord.Ignore`, every technique on every chord note, so it selects
  and edits as one scrape; a one-string scrape is a single note. Every export
  writes the split chord as one `<note>` per string with no `<chord>` and no hand
  shape (`LinkLandingBreakup`, RS XML and SNG alike) — exactly the owner's
  reference (*Shinbatsu* m197, RS XML `<note … tremolo="1" mute="1" tap="1"
  slideUnpitchTo="18" ignore="1" />` twice per scrape, `.rscp` `ignore="T"
  mute="FULL" hopo="TAP" tremolo="T" … unpitchedSlide="T"`). Live Play's
  `LiveNoteMatcher` judges a chord by the CHORD's own `Ignore`, which is why the
  chord carries it. The chord plays a nameless template of its placed frets with
  NO fingers (a fret-hand-muted scrape is no grip), so the inspector raises
  neither L07 nor D11 on it (`validation-and-fixing.md`) — by the fully-muted
  rule, not by `ChordTemplate.Fingerless`, which the GP import does not set.
  **Start fret = 24 or 12 (#586)**, whichever is nearer
  the lane the convention prefers on the FHP in force at the scrape: toward the
  nut (GP "down") its 4th lane, `anchor + 3`; toward the bridge (GP "up") its
  1st lane, the anchor fret; a tie goes to 24. The start must lie on the
  track's neck (`GpPickScrape.LastFret`: gpif `FretCount`, the GP5 track
  header's fret int; a 22-fret track never starts on 24) and leave room to
  travel, so **a scrape toward the bridge never starts on the last fret — on a
  24-fret neck it always starts on 12**. Only when neither fits (a capo at 12 or
  above) does it fall back to the lane itself. **Landing**: an octave (12
  frets) away, 6 frets for a scrape shorter than a quarter note, and toward the
  nut never below fret 5; every fret floors at the capo and caps at the last
  fret. #586 left the landing rule alone: from 24 or 12 it gives `24 → 12/18`,
  `12 → 5/6` and `12 → 24/18`. Every tap+tremolo+`slideUnpitchTo` note in the owner's
  RS2 project folder (copies deduped): *Shinbatsu wo Tadaori* m197
  (FHP 21, eighths, A+D `24 → 18` ×3) and m198 (FHP 21, ~2 beats, E+A
  `24 → 12`); *Matasaburo* m160 (FHP 21, ~2 beats, E+A `24 → 12`); *Crucifix
  X* m102 (FHP 12, 4 beats, E+A `12 → 5`); *Symbol I* m75 (FHP 12, E+A
  `12 → 5`, not ignored). All five go toward the nut, all start on 24 or 12 (the
  owner: "pick scrapes are usually on the 24th fret or 12th fret"). Short-vs-long
  and A+D-vs-E+A are confounded in *Shinbatsu*; the rule keys on length. No chart
  scrapes toward the bridge: that side is the mirror. The reference's E+A sustain
  is 0.449 s where the tab's half note gives 0.480 s: the owner trimmed it by hand
  in EOF (the `.eof` stores 449 ms). **How the import applies it**:
  `GpPickScrape.Apply` (at the parse, from both readers) sets the techniques and a
  provisional fret from the tab; `GpPickScrape.IsolateScrapes` (first step of
  `GpPostProcessor.Run`) gives every scrape a sound of its own before any template
  or hand shape — the scrape strings of a strum become one split chord (or a note
  for one string), the strum's other strings stay a chord (or a note when one is
  left), and every scrape string is set fret-hand muted whatever a later tab bit
  said; the hand-shape planner skips split chords; `GpPickScrape.RunImportFhpPass`
  (in place of `ImportFhpPass.Run` in BOTH importers) lifts the scrape notes AND
  scrape chords out of the level while the anchors are generated — a tab's scrape
  fret is not a hand position and must not pull an anchor — then places each on
  the anchor in force at its time and points each scrape chord at its fingerless
  template. A post-pass rather than predicting the anchor, because the generator's
  anchor IS the hand position the preferred lane is defined against.
  `AdjustSustains` lets a split chord keep its tremolo tail even under
  `TruncateShortChords` (it ships as notes). GP stores the scrape in the
  slide BITMASK above the six ordinary bits: **gpif `Slide` flags 64 = down,
  128 = up, and the GP5 binary slide byte 0x40 / 0x80**; GP3/GP4's discrete
  slide value has none, and the Songsterr JSON carries no scrape we have seen.
  **The Guitar Pro EXPORT writes it back (#637)**: `GpExporter.BuildNote` gives
  every sound `GpPickScrape.IsChartedScrape` accepts (`IsScrape`, the predicate the
  import keys on, plus the FULL mute) alphaTab's `PickSlideDown` (toward the nut,
  `IsTowardNut`) or `PickSlideUp` — gpif flags 64 / 128 — and writes no dead note,
  no slide-out and no beat tremolo for it; a split chord leaves as the strum it
  came from. Re-imported it is the same scrape (`GpExporterPickScrapeTests`).
  The scrape outranks every other slide bit (`ApplySlideFlags` and
  `GpLegacyNoteEffectsHelpers.ApplySlide` return early), and the GP5 reader's
  later palm-mute bit must not turn its fret-hand mute into a palm mute. The GP5
  chord builder copies `Ignore` onto its `ChordNote`s — `IsScrape` keys on it (GP
  import sets `Ignore` nowhere else). **What other importers do with a split
  chord** (#609): the EOF import maps EOF's split flag
  (`EOF_PRO_GUITAR_NOTE_FLAG_SPLIT`, `0x20000000`, `song.h:64`) to
  `Chord.SplitIntoNotes` and its fingerless eflag
  (`EOF_PRO_GUITAR_NOTE_EFLAG_FINGERLESS`, `32`, `song.h:137`) to
  `ChordTemplate.Fingerless`, and `EofExporter` writes both back; an EOF tremolo
  phrase now lands on a chord's notes too, and a chord tremolo on every string
  exports as one, which is what lets a split scrape cross EOF intact
  (`EofSplitFingerlessTests`, against the owner's lines). The RS-XML IMPORT
  gives a split chord back from its loose notes (`parsing-psarc.md` § RS-XML
  import gives back EOF's split chords, #621): notes at one time, inside no hand
  shape, no link landing on them — so a scrape exported to RS XML comes back as
  the split, ignored chord it was. Pinned on
  both readers, in both directions, through the RS XML writer (field for field
  against the owner's reference lines), the SNG and the `.rscp` round trip by
  `GpPickScrapeImportTests` (the 22-fret neck through both readers too) and
  `SngLinkLandingBreakupTests`; the never-expected half by
  `LiveNoteMatcherPickScrapeTests`; the findings by `SplitChordFindingsTests`;
  drawn by `HeadlessPickScrapeRenderTests`.
  **`GpPostProcessor.RemoveDuplicateSounds` (0.33.2)**: two sounds on ONE string
  at ONE instant are physically impossible and nothing downstream deleted them
  (`ChartSpacingEnforcer` only SHORTENS sustains). A standalone note loses to a
  chord that claims the string; note-vs-note keeps the one with more playable
  information (slide/bend/link/vibrato/sustain), ties keep the first. Two CHORDS
  at one position are deliberately left alone.
  `GpPostProcessor.ClampHandShapes` clips consecutive hand shapes to never
  overlap (runs in `Run` and again after the let-ring pass — in BOTH the gpif
  and the GP3-5 binary importer; keep them symmetric); anchor generation
  treats a handshape's End as EXCLUSIVE — abutting per-chord shapes otherwise
  lock a new chord to the previous chord's root and skip its anchor.
  **A Guitar Pro ROLLED chord is collapsed to its struck notes
  (`GpBeatCarry.FilterTieRoll`, 2026-08-21).** GP spells a roll by
  repeating every already-ringing string on each beat as a tie DESTINATION and
  adding one new one; imported literally that is a stack of ever-bigger CHORDS
  plus a junk find-or-create template per stack (owner report: measure 2 of a
  real GP8 file gave a note, a 2-note chord, a 3-note chord, a 4-note chord,
  three templates and two zero-width shapes — two beats before a properly
  tabbed let-ring run of the same shape imported correctly as one bar). A beat
  MIXING tie destinations with fresh strikes is the roll signature: the
  destinations are dropped and the beat joins a **separate `tieRollSet`**, which
  `GpHandShapeBuilder.BuildLetRingHandShapes` is called on FIRST, before the
  authored let-ring set. **The two sets must stay separate** — a roll's ring-out
  can end exactly where a following let-ring run begins, and one shared set
  fuses them into a single bar that swallows the run. An all-destination beat is
  consumed only while a roll is ACTIVE and only when every string it names is
  one the roll holds; otherwise it is an ordinary tied chord and
  `MergeTiedNotes` owns it (the chord-tied-across-a-rest figure in measures 74+
  of the same file needs exactly that). Each collapsed beat's authored rhythm is
  added to the `NotationDuration` of every string still ringing — the same tie
  re-spelling `MergeTiedNotes` does, and what gives the bar its true ring-out.
  A beat that emits a CHORD ends the roll (a `ChordNote` carries no per-string
  notation duration to extend). Notation-only sidecar voices pass a null set and
  keep the authored spelling verbatim.
  **A let-ring run BREAKS where it re-frets a string it is already holding
  (`GpHandShapeBuilder.RefretsHeldString`, 2026-08-21).** The run's shape is
  aggregated "first fret wins per string", so an unsplit run declares its FIRST
  fret on that string and every later note there contradicts the very bar
  covering it — inspector E05, twice inside a single let-ring bar that ran
  across five measures of a real GP8 file (owner report). The fret hand moved;
  the bar moves with it. Re-striking a string at the SAME fret is the ordinary
  arpeggio pattern and must NOT split (over-splitting is the failure on the
  other side, and `GpLetRingRefretSplitTests` pins both directions plus the
  E05 invariant itself). The two-beat run floor is unchanged, so a split can
  leave a fragment too short to earn a shape.
  **Two more rules, both from defects that FIRST cut introduced (2026-08-22,
  found by an independent re-import check, not by the gate):**
  (a) **A run also breaks where a NEW string would stretch the grip past a hand**
  (`WidensPastAHand`, more than `MaxPlayableFretSpan` = 5 frets between the
  lowest and highest FRETTED note; open strings ask nothing of the fret hand and
  are excluded). `RefretsHeldString` only catches a string played TWICE, so a
  string the run has never touched slipped past it: a run holding {8,10,8,9,8}
  absorbed a lone fret-2 note NINE beats later and synthesized a
  `2 8 10 8 9 8` template. **The E05 check is structurally blind to this** — a
  shape built FROM the notes it covers can never contradict them — which is why
  it needs its own rule and its own test.
  (b) **A run that starts on an existing hand shape SUPERSEDES it** (removes any
  shape with the same `Position` that the run fully contains) rather than
  standing beside it. Two shapes sharing a `Position` make `ClampHandShapes`
  clamp the first to ZERO WIDTH — a bar covering nothing that still holds a
  template id and would export as an RS `handShape` with
  `startTime == endTime`. A shape that OUTLIVES the run is a different hand
  position and is left to the clamp.
  **Let-ring shapes (`GpHandShapeBuilder.BuildLetRingHandShapes`) get
  the chord-handshape treatment (0.34.1)**: the bar carries the run's duration,
  so the run's note sustains are TRIMMED to strikes (skip any note whose
  bend/slide/vibrato/tremolo/link needs the length) — otherwise the run's
  notes kept their long let-ring sustains under the bar. **The bar END is the
  authored ring-out of the run's LAST BEAT — note OR chord — clipped to the next
  played sound
  (0.34.1).** This pass runs AFTER `GpPostProcessor.Run` → `AdjustSustains`, which
  has already zeroed a sub-quarter note's playable `Sustain` — so the end is
  taken from the surviving `NotationDuration` (`BeatInfo.AuthoredSpan`), NOT the
  raw sustain. Using the sustain made a 16th-note run's bar collapse onto its
  own last note's onset (owner report, image #12: "cuts off at the start"); and
  an authored ring-out that overshoots the next sound is clipped to it, so the
  bar never overlaps the following note (owner report, image #11/#13). If no
  authored duration survived, the bar extends to that next sound instead of
  collapsing. **`AuthoredSpan` folds the CHORD's `NotationDuration` too** — the
  same `GpPostProcessor.Run` has already had `ChordHandShapeBuilder` trim a plain
  chord's ChordNote sustains to a strike, so a run ENDING on a chord had a zero
  ring-out and collapsed onto the final strum's onset in exactly the same way.
  It stayed invisible because the "extend to the next onset" fallback masks it
  whenever anything follows the run, silently substituting "until the next sound"
  for the authored length; pinned by `GpSlideRecoveryTests
  .BuildLetRingHandShapes_RunEndingOnAChord_KeepsTheChordsAuthoredRingOut`, whose
  run must therefore be the LAST thing on the level. **A let-ring run is a
  PLAIN hand shape, NEVER an arpeggio (0.34.1 owner decision).**
  `ChordTemplate.Arpeggio` is a load-bearing flag — it sets `ChordMask.Arpeggio`
  in the SNG export, picks the `.eof` arpeggio section type over the plain one,
  and draws purple arpeggio brackets in the 3D highway instead of a hand-shape
  bar — and "let ring" is a sustain instruction, not an arpeggio phrase, so the
  flag mislabelled it everywhere. The run's template is now find-or-create /
  deduped-by-shape like every other imported shape (charting-ready fingering +
  suggested name), so a run whose aggregate shape a chord already plays reuses
  that chord's template. **Their FHP root is the
  run's OWN lowest fret, never a following chord's.** A let-ring shape's End
  is clamped onto the next sound, so a CHORD sitting exactly on the (exclusive)
  End is the next hand position and is EXCLUDED from the run-root aggregate
  in `FretAnchorGenerationService.PrecomputeHandShapeRoots` (owner report: a
  {3,5,5,5} run before a {2,2,2} chord took root 2, not 3). A SINGLE note at
  End stays IN — the corpus anchors those running-arpeggio continuations
  together, and excluding them over-segments the anchor stream. The root
  aggregate only runs when the shape STARTS on a single note (the arpeggio
  signature); a chord-shape start is byte-identical (its first strum carries the
  whole shape).
  GP legacy BINARY import (`GpLegacyImporter`, .gp3/.gp4/.gp5) captures the SAME
  hints (0.25): the beat's folded rhythm becomes `NotationDuration`, a note's
  grace effect becomes `GraceFret`, and GP5's second voice (lane 1) becomes a
  `NotationVoice` sidecar (same MergeTiedNotes+LinkSlides-only discipline). Dot/
  tuplet folding is the pure `GpRhythmFraction.Fold`/`TryUnfold` (quarter-note
  units; a single-dotted triplet folds to the plain base — unfold returns the
  simplest spelling). The historical "complex GP5 files desync the beat parser"
  limit was ROOT-CAUSED in 0.33: `SkipChordDiagram` under-read the GP4/5 chord
  diagram block by one byte (the 5-byte name→base-fret gap read as 4; the block
  is 107 bytes incl. the format byte, matching EOF/Charter). Since 0.33 the
  legacy reader also captures accents (header bits 0x40 / GP5 0x02) and
  beat-level vibrato/harmonic (effects fx1 bits 0x01-0x08), folded onto the
  beat's notes like EOF does. Accent lives on `IGuitarSound` (chord builds roll
  any accented string up to `Chord.Accent`; rscp chordNote carries an additive
  `accent="T"`).
  **Legacy-vs-gpif parity rules (0.33.2). The binary reader is measured against
  our OWN gpif importer, because that is the one measured against EOF/Log
  Charter:**
  (1) **A slide value is SIGNED in every version.** GP3/4 spell the two
  slide-INs as NEGATIVE discrete values (`-1` from below, `-2` from above —
  Charter `GP5FileReader.readSlide()`, pre-500 branch); the switch handled only
  the four positive out-slides, so every pre-GP5 slide-in was dropped.
  (2) **Out beats in, deliberately.** GP5 flags slide-out and slide-in
  independently (Charter reads two slots) but `IGuitarSound.SlideTo` is ONE
  `int?`, so a slide-in only claims a note no other slide claimed — the identical
  precedence `GpNoteParser.ApplySlideFlags` applies on the gpif path. Change both
  or neither. (Charter's own downstream `GP5SoundsTransformer.addSlideIn` lets IN
  win; matching our two importers to each other is worth more than matching that.)
  (3) **A grace on a CHORD materializes at beat flush**
  (`GpLegacyImporter.ApplyChordGraces`), not in `GpPostProcessor`: the binary
  format hangs the ornament on each chord NOTE, and the chord builder copied only
  the playable fields — so every chord grace was dropped, including the reference
  file's three chord grace SLIDES (exactly the .gp5-vs-.gp `slideTo` deficit).
  A "sl." grace travelling to a different fretted position becomes a real origin
  CHORD that slides in (mirrors `GpBeatCarry.ApplyPendingGrace`);
  anything else stays the per-string `ChordNote.GraceFret` hint.
  `GpPostProcessor.MaterializeGraceSlides` still owns the SINGLE-NOTE case, so
  both take their `beforeBeat` from the same authored flag (see (5)) and place
  consistently within one file. The helper must run BEFORE the strum joins
  `chordsOut` — it scans the emitted sounds for the last end on each string and
  the target is not one.
  (4) **Triplet feel is CAPTURED but not APPLIED here.** GP5 stores it per
  measure header (`1` eighth, `2` sixteenth, else none); GP3/4 store ONE
  song-wide shuffle boolean in the pre-song block (Charter
  `readGlobalTripletFeel`) that stamps every bar. Both now fill
  `GpBarInfo.TripletFeel` with the verbatim GPIF spellings
  (`Triplet8th`/`Triplet16th`) — that string is compared BY VALUE in
  `GpArrangementParser.ApplyFeelToPosition`, so the two vocabularies must never
  diverge. **The binary importer still places every note on its notated onset:**
  swing placement lives in `GpArrangementParser.ParseVoiceBeats`, which this path
  does not go through, and it is two-sided (onset shift AND sustain to the next
  SWUNG onset) so it cannot be bolted onto a streaming per-beat reader without
  first buffering a bar's beats. A swung .gp5 still imports straight — a known
  gap, not a mapping bug.
  (5) **A grace ornament's BEFORE-beat/ON-beat flavour is real and is now READ.**
  GP5 appends a grace FLAG byte after the grace duration; Charter
  `GP5FileReader.readGrace()` reads it only for `version >= 500` and spells the
  meaning `graceBefore = (flags & 0x02) == 0`, so **bit `0x02` set = ON-beat**,
  a cleared bit = before-beat (bit `0x01` is a dead grace, already covered by the
  negative-fret clamp). **GP3/GP4 have NO such byte** — Charter initializes
  `graceBefore = true` before its version branch — so every pre-GP5 grace is
  before-beat by format definition, not by our choice. The reader used to consume
  and DISCARD that byte while `MaterializeGraceSlides` hardcoded
  `beforeBeat: true`, so one score exported as `.gp` and as `.gp5` placed its
  ornaments differently and a before-beat grace with no room ahead silently lost
  its slide. The flavour now travels as an IMPORT-ONLY side table
  (`GpLegacyImporter._onBeatGraces`, a reference-identity `HashSet<GuitarNote>`)
  — deliberately NOT a domain field, since it is fully consumed during import —
  threaded into `GpPostProcessor.Run(..., onBeatGraces)` →
  `MaterializeGraceSlides` for single notes, and passed straight to
  `ApplyChordGraces` for strums. **A null set / absent entry means BEFORE-beat**,
  which is simultaneously GP's pre-500 default and the pre-0.33.2 behaviour, so
  the gpif and Songsterr paths (which pass nothing) are unchanged — the gpif
  importer still reads its own `<GraceNotes>` element and materializes its own
  graces. Placement itself stays the single shared `GpPostProcessor.PlaceGrace`
  (including its no-room ⇒ on-beat fallback); only the flag reaches it. One
  asymmetry to keep in mind: gpif carries ONE flavour per grace BEAT while the
  binary format carries one per graced NOTE, so `ApplyChordGraces` treats a strum
  as on-beat when ANY of its sliding ornaments is.
  (6) **Bit `0x04` means two DIFFERENT things in the two fx2 bytes and they used
  to be crossed.** At the BEAT it is the tremolo BAR (whammy, followed by a bend
  chunk); at the NOTE it is tremolo PICKING (followed by a speed byte). The
  reader stamped the whammy onto every note of the beat as `Tremolo` and read +
  discarded the real picking flag — so a whammy dip imported as tremolo picking
  and a tremolo-picked note imported clean, while the gpif path was right all
  along (`<Tremolo>` on the beat IS the picking speed). Now: note fx2 `0x04` ⇒
  `IGuitarSound.Tremolo`; the whammy's bend chunk is still CONSUMED (stream sync)
  but maps to nothing — `IGuitarSound` has no bar-bend concept, and turning the
  chunk into per-string `BendValues` is a separate decision, not a bug fix.
  (7) **Trills are read and expanded through the SAME helper as gpif.** Note fx2
  `0x20` carries (upper fret, period); the reader used to `Skip(2)` it. The fret
  is capo-RELATIVE, so `GpCapoFrets.ToAbsolute` is applied at the read (the capo
  rule), and the upper fret is parked in the import-only side table
  `GpLegacyImporter._pendingTrills` (reference identity, like `_onBeatGraces`)
  because the expansion needs the note's final position/sustain and the bar end.
  At beat flush the PLAYABLE voice calls `GpArrangementParser.ExpandTrill`
  (now `internal`, shared) and a notation-only sidecar voice takes the vibrato
  glyph instead — exactly the gpif split. A trill on a CHORD note is dropped,
  which is also what gpif does (its `LastTrill` is captured and unused there).
  (8) **Beat-level SLAP and POP are real.** The GP4/5 string-effect byte behind
  fx1 `0x20` is `1` tapping / `2` slap / `3` pop; only tapping was read. `2`/`3`
  now fold onto the beat's sounds as `BassPickingType.Slap`/`Pop`, the same way
  `beatVibrato`/`beatHarmonic` fold — and the legacy chord builder copies
  `BassPicking` onto its `ChordNote`s, which it previously did not. This matters
  most on bass, exactly where it was being lost; the 0.33 "beat text `S`/`P`"
  feature stays as the manual override for files that lack the flag.
  **GP7+ spells the same two as NOTE properties (`Slapped`/`Popped`) and
  `GpNoteParser` reads them (0.35)** — it did not, so the binary reader gaining
  (8) left the two importers disagreeing on the one arrangement type the
  technique defines, which is exactly what this parity rule exists to prevent.
  Change one side, change the other.
  (9) **The beat's TAP is `HopoType.Tap`, and the note's STACCATO bit is an
  `Accent`.** Both were losses in the same reader. GP's beat string-effect type
  `1` is RIGHT-hand tapping — gpif's `Tapped` note property, which `GpNoteParser`
  maps to `HopoType.Tap` — but the binary path claimed `HopoType.HammerOn` under
  a comment saying it "matched the GP7/8 path", which it never did. Three things
  died with it: the SNG never set `RsNoteMask.Tap`, the whole P3 tap-run rule in
  `FretAnchorGenerationService` (it keys on `Hopo == HopoType.Tap`) was dead for
  every binary-format tapping passage, and `AdjustSustains` re-derived every
  DESCENDING tapped note into a `PullOff`. `Tap` needs no `tapOriginated`
  exemption for that last one — only a `HammerOn` is ever re-derived, which is
  why gpif passes that set solely for the LEFT-hand taps it normalises to
  hammer-ons on purpose. Note fx2 bit `0x01` is GP's authored staccato and was
  parsed into an empty block; Rocksmith has no staccato mark, so the charting
  convention is the ACCENT (playback already shortens exactly the accented notes
  — `JuceAudioPlaybackService`'s note-end rule), and it is read UNGATED like
  every other accent on both paths (the note header's `0x40`/GP5 `0x02` here,
  gpif's `<Accent>` element there). **There is no staccato SETTING and no
  staccato inference on any path** — do not add one to a single importer. Both
  pinned by `GpLegacyImportTests` (`BeatTap_ImportsAsTapNotHammerOn` covers the
  descending flip case too; `LegacyGpFixtureBuilder`'s `FxNote.Staccato` writes
  the flag-only bit).
  **AUTHORED left-hand fingering is read by BOTH importers and BEATS the
  suggester.** Guitar Pro stores a per-note left-hand finger and neither reader
  looked: the binary path `Skip(2)`'d the fingering bytes, the gpif path never
  asked for `<Property name="LeftFingering">`. Every imported chord template was
  therefore fingered by `ChordTemplateFingerSetter`'s guess (74.6% exact against
  a hard 88.8% ceiling) while the file had the answer. The value travels as the
  import-only `GpAuthoredFingering` side channel — (string, position) → finger,
  never a domain field — filled from the PLAYABLE voice only (`GpLegacyImporter.
  _pendingFingers` → `_authoredFingers` at beat flush, since the note read does
  not know the position yet; `GpNoteParser.LastLeftFingering` →
  `GpArrangementParser.RecordAuthoredFinger`) and threaded
  `GpPostProcessor.Run(..., authoredFingers)` → `ChordHandShapeBuilder.
  BuildAndTrim`. Rules that must not be relaxed: (a) `ChordTemplateFingerSetter`
  stays a PURE function of the fret shape (the Core invariant) — the builder
  calls it and then overrides only the strings the file fingered; (b) authored
  fingers are applied at template CREATION only, because a template found by
  shape is shared and must never be mutated, so the FIRST fingering a shape is
  seen with wins, exactly like its name; (c) the finger ids line up with ours
  already (GP `0` = thumb = our `T`, `1..4` = index..little, `-1`/`255` = none),
  and gpif spells them with the classical letters `P/I/M/A/C` — both mappings
  live in `GpAuthoredFingering`. Songsterr passes null (its payload carries no
  fingering), so that path is byte-for-byte unchanged. Still NOT read: the
  RIGHT-hand fingering byte (no model home). gpif chord DIAGRAMS feed the same
  channel (below).
  **AUTHORED chord NAMES are read by the GP3-5 BINARY importer and BEAT the
  suggester (0.44).** Guitar Pro hangs a chord DIAGRAM on the beat a strum is
  written over and its first real field is the name the arranger typed;
  `SkipChordDiagram` consumed the block purely to keep the beat stream in sync
  and dropped the name, so `ChordHandShapeBuilder` renamed every imported chord
  from `ChordNameSuggester`'s pitch-class guess — which cannot know the arranger
  meant a slash chord, an inversion, an `N.C.` marker or a particular enharmonic
  spelling. It is now `ReadChordDiagram` (byte layout UNCHANGED — this is the
  block whose 5-byte name→base-fret gap once desynced the whole stream) and the
  name travels on the import-only `GpAuthoredChordNames` side channel
  (position → name), threaded `GpPostProcessor.Run(..., authoredChordNames)` →
  `ChordHandShapeBuilder.BuildAndTrim` → its `ImportGripSource`. Same rules as
  the fingering: applied at template CREATION only (a template found by shape is
  shared and never mutated, so the FIRST name a shape is seen with wins), and a
  BLANK diagram label is not a name — GP writes unlabelled diagrams and storing
  one would replace a useful guess with nothing. The name is normalised
  (`GpAuthoredChordNames.Normalize`: control characters stripped, capped at
  `MaxNameLength` = 32) because it is Latin-1 decoded straight off the wire into
  a label the chart editor and the sheet export both render. The Songsterr
  payload is still unread and passes null.
  **gpif chord DIAGRAMS name AND finger the strums that reference them (#492).**
  `GpChordDiagrams.Read` takes the track's `DiagramCollection` (first staff's
  properties, else the track's — GP6): name, frets `baseFret + fret` (fret `0` =
  open) through `GpCapoFrets.ToAbsolute` like the notes, fingers as WORDS
  (`GpAuthoredFingering.MapDiagramFinger`: GP7/GP8 write `Thumb/Index/Middle/Ring/
  Pinky`, NOT `Annular` — a reader that only knew `Annular` saw 52 fully-fingered
  diagrams in the owner's tabs where all 258 are). A diagram for another string
  count is skipped; a fingering is kept only when it covers EVERY fretted string.
  `GpArrangementParser` calls `Record(beatNode, chord, …)` per playable-voice
  chord AFTER `ApplyPendingGrace` (an on-beat grace moves the chord, and the side
  channels are keyed by where the template builder finds it), filling the SAME
  `GpAuthoredChordNames` / `GpAuthoredFingering` — so creation-only,
  first-value-wins and the FHP pass's `IsAuthoredTemplate` mark all hold
  unchanged. The rules, measured on the owner's 12 GP7/8 tabs (1,184 referencing
  strums, 241 of them sounding a pitch their diagram lacks): the NAME needs the
  strum's sounding pitch classes ⊆ the diagram's; the FINGERING needs the
  sounding frets to EQUAL the diagram's; a fully muted string is left out of both
  (a strum dead on EVERY string compares its written frets — dead chugs are
  often the first strum of a shape and create the template). A strum with no
  reference, or a stale one, BORROWS a same-track diagram fretted exactly like it
  when every such diagram agrees (`Borrow`) — without it the name depended on
  whether the FIRST strum of the shape carried the reference. A let-ring
  arpeggio's template (`GpHandShapeBuilder.FindOrCreateTemplate`) borrows the
  same way. A note's own `LeftFingering` is recorded first and wins per string.
  Effect on those tabs: of the 158 templates fretted exactly like a diagram, the
  typed name went 44 → 158 and the authored fingering 95 → 158 (the "before"
  numbers are the suggesters agreeing by chance). Pinned by
  `GpChordDiagramTests`; the GP byte pins in `ExportBytePinTests` do NOT cover
  this (none of the pinned `.gp` files has a diagram).
  **BOTH authored side channels are keyed per TRACK, not per FILE (0.44).**
  Their keys are (string, position) and position, and two tracks of one score
  strum together constantly — so the file-wide table let the LEAD's note finger
  the RHYTHM's chord template, silently, because both `Set` methods keep the
  FIRST value for a key. `GpLegacyImporter` now holds
  `_authoredFingersByTrack` / `_authoredChordNamesByTrack`; the gpif path was
  already correct (`GpArrangementParser` clears its table inside the per-track
  loop). `LegacyGpFixtureBuilder.WriteMultiTrackFile` exists precisely because a
  single-track fixture cannot show this class of bug.
  **Songsterr shares four more of the GP rules as of 0.35** — it goes through
  the same `GpPostProcessor.Run`, so it must: (a) frets convert through
  `GpCapoFrets.ToAbsolute` at the read (above); (b) a note on a string the
  track does not have is DROPPED, not clamped — clamping fabricated a real
  low-E note AND let it claim that string's slot in the per-instant `occupied`
  set, so a legitimate second-voice note there was dropped in its place;
  (c) a slide needs a FRETTED start (`"downwards"` on fret 0 used to yield a
  slide UP to fret 1, and `shift`/`legato` left the `-1` sentinel on an open
  note — inspector L09 on every save); (d) beat `letRing` feeds
  `GpHandShapeBuilder.BuildLetRingHandShapes` + the second `ClampHandShapes`,
  and measure `marker.text` feeds `GpMarkerImport.CreateMarkerEvent` behind the
  same `GpImportSettings.MarkersToSections` gate. Still NOT read on this path:
  grace ornaments (the payload's spelling is unverified against a real
  revision), `ghost`, accents, tremolo picking, trills, tap/slap/pop, stroke
  direction and `TripletFeel` (so swing is never applied — the same gap the
  GP3-5 binary path has).
  **The GROUPING is shared with the editor (#478):
  `Services/Editing/HandShapes/ChordHandShapePlanner` is a pure planner that
  RETURNS bars (`PlannedHandShape`) and mints templates through an
  `IHandShapeGripSource`; `ChordHandShapeBuilder` keeps only what an import does
  on top — the file's fingering and names on a minted template
  (`ImportGripSource`), the `TemplateId` repoint and the strike trim. The
  editor's Tools ▸ Hand shapes ▸ Generate hand shapes (arrangement) and the D11
  auto-fix run the same planner (`ArrangementHandShapeGenerator`).** The lift is
  pinned output-identical: `ChordHandShapeBuilderEquivalenceTests` runs the
  pre-#478 body verbatim against the planner on the byte-pin GP files and a
  synthetic level, the four GP pins in `ExportBytePinTests` did not move, and on
  2026-10-09 all 52 GP files in `TestData` imported to byte-identical `.rscp`
  (18,205 hand shapes). The planner's editor-only switches
  (`ChordHandShapePlanOptions.CoverEveryChord`, `KeepAnchorsOutside`) are OFF on
  the import path; its clip at an EXISTING bar never fires there (the pass runs
  before any other shape exists).
  **Auto-handshapes (0.25): `GpPostProcessor.Run` (the pass EVERY tab importer
  shares — gpif, legacy binary, Songsterr) now runs `ChordHandShapeBuilder`
  BEFORE `AdjustSustains`.** For each imported chord it find-or-creates a
  `ChordTemplate`, lays a `HandShape` over the chord's authored
  duration, then TRIMS the chord's sustain to a short strike (RS convention: the
  handshape bar carries the duration). **Since 0.26 new templates arrive
  charting-ready**: suggested fingering (`ChordTemplateFingerSetter`) + a
  suggested name (`ChordNameSuggester`, tuning-aware) — which is why all three
  tab importers now sync `Arrangement.Tuning` (names) from the file's tuning
  offsets, not just `TuningPitches`. Find-or-create matches by FRETS alone
  (non-arpeggio): a hand-authored template with the same shape is reused
  verbatim, never mutated, never duplicated. Chords whose notes wear a sustain-visible
  technique — bend / tremolo / vibrato / slide (pitched or unpitched) — keep
  their sustain but still get a handshape; HO/PO/tap do NOT exempt. The exemption
  is the single named `ChordHandShapePlanner.HasSustainVisibleTechnique`
  (chart-semantics; owners tune it; `ChordHandShapeBuilder` forwards to it). Idempotent (no double-create on re-import).
  Behavior change: imported chord sustains are now trimmed — importer output
  differs, and consumers see chord handshapes they didn't before.
  **A handshape CLIPS at the next sound (0.33): `BuildAndTrim` ends a shape at
  the next chord/note after its run, never past it.** A technique-exempt chord
  keeps a long sustain, so without the clip its handshape swallowed whatever
  struck during it — a following chord was then suppressed (`HandShapeCoversChord`)
  and inherited THIS shape's template (a bend chord {12,12,14} made the plain
  {12,12} double-stops after it edit as the 3-note shape); stray single notes
  fell inside the bar too. Each distinct shape now keeps its own handshape.
  **Single-note passages get NO auto-handshape, and that is deliberate — do NOT
  "fix" the coverage gap by generating them (measured 2026-07-20 against the
  hand-corrected EOF corpus).** Coverage recall is 81.2% at 97.1% precision and
  **100% of the uncovered sounds are single notes**; 1,216 of the corpus's 7,107
  handshapes (17.1%) cover only single notes and we emit none of them. They are
  not derivable data. EOF emits a handshape for a single note ONLY when
  `EOF_NOTE_TFLAG_ARP_FIRST` is set (`EOF_SRC/rs.c` ~:3113), which comes solely
  from a HAND-DRAWN arpeggio/handshape phrase (`tp->arpeggio[]`, `.eof` section
  type 10/19 — an authoring structure, see `EofProGuitarTrackParser`), and the
  GP sources carry no equivalent: zero chord diagrams across all 24 corpus
  files, the only authored signal being `LetRing`, which `GpHandShapeBuilder`
  already consumes. Separability is effectively nil — leave-one-folder-out
  gradient boosting over every local feature (run length, fret span, distinct
  strings, inter-onset gaps, sustain overlap, technique flags), an UPPER BOUND
  on any hand-written rule, reaches 71.6% precision at 53.1% recall with
  per-folder precision swinging 7%–100%. Ground-truth coverage is an authoring
  STYLE, not a musical property: 0.2% of Bass single notes vs 27.6% Lead vs
  75–86% Rhythm, and 0%–64% across folders. The best hand rule found (≥4 notes,
  ≤1-fret span, ≥4 distinct strings) is in-sample only and still costs up to
  14 pp of per-folder precision on 4 of 24 folders. A wrong handshape actively
  misleads a player about where the hand goes, so the precision is worth more
  than the recall.
  **Grace slides materialize (0.33): `GpPostProcessor.MaterializeGraceSlides`**
  (first pass in `Run`) turns a "sl." grace hint (`GuitarNote.GraceFret` +
  transient `GraceSlide`) into a real short note that slides into the target
  (`SlideTo`=target fret, `LinkNext`), matching EOF/Log Charter — a plain grace
  stays a notation hint. **Two final clamp passes** run after `AdjustSustains`:
  `ClampSlideSustains` (a pitched slide's sustain ends exactly at its landing,
  never overshoots) and `ClampBendsToSustain` (no bend point past the note's
  finalized end — swing/trim could otherwise leave a bend tail on the next
  sound). **Auto-FHP (`FretAnchorGenerationService`) caps the hand span (0.33)**:
  a handshape whose internal reach exceeds a playable span (Log Charter
  `canBeExtended` zones: 4/5/6/7 frets by neck position) is NON-locking, so the
  hand climbs THROUGH a wide let-ring run into multiple anchors instead of one
  over-wide `{2, width 8}`; a simultaneous chord's own min..max span stays
  uncapped.
  **An anchor's root is NOT fixed when it is emitted — while its GROUP is still
  open, a lower note pulls that already-written anchor DOWN
  (`FretAnchorGenerationService.TryDescendGroup`). Do not "simplify" the walk
  back into a one-pass-per-position decision.** This is the one structural rule
  the generator behind the hand-corrected `TestData/EOF` corpus follows and we
  did not: EOF's `eof_generate_efficient_hand_positions_logic`
  (`EOF_SRC/rs.c:3663`) keeps a running `[current_low, current_high]` over the
  notes a group has swallowed, lets `current_low` DESCEND while the merged reach
  still fits the hand (`eof_note_can_be_played_within_fret_tolerance`), and only
  then writes the anchor — at the group's FIRST note, carrying the descended low.
  Taking the first note's fret and splitting the group the moment anything played
  below it left every group's opening anchor above the hand's real base: measured
  over the corpus's 12,279 comparable points, exact agreement **75.7% → 78.8%**
  and the "we sit higher than the charter" bias **+0.16 → −0.01** frets (HIGHER
  16.1%→9.6%, LOWER 8.2%→11.5%). The dense LEAD parts it was worst on moved most
  — Yorushika *Odorouze*/Lead 26.0% → 55.5% exact, mean +1.24 → +0.62.
  Two bounds, both measured, both load-bearing: the descend reaches back at most
  `DescendReach` = 3 STRUCK POSITIONS (unbounded, i.e. EOF's own rule, scores
  77.6% — the charter's corrections are more local than EOF's merging), and the
  merged reach may not exceed `BaseAnchorWidth` = 4, which is what EOF's
  `eof_fret_range_tolerances` defaults to across the whole neck. Slide landings
  open their own descendable group; handshape locks close it (a strum re-plants
  the hand, so its root is authored, not derived).
  **Falsified against the same corpus — do not re-try these:** (a) a post-pass
  lowering each anchor to the min fretted fret of its OWN span (4 anchors of
  12,279 changed — our spans already satisfy it, the error was in the GROUP
  boundary); (b) rooting a handshape on the min fretted across the WHOLE shape
  (4 of 5,060 P1 points, 2 fixes / 2 breaks); (c) breaking the group at RS phrase
  starts, which EOF does enforce — a phrase start falls between our anchor and
  the charter's 12.0% of the time where we are LOW vs 10.9% where we AGREE, i.e.
  the base rate; (d) letting an arpeggio handshape inherit a still-covering
  anchor instead of re-anchoring (monotonically harmful, −0.6 pp at its best).
  `FretAnchorGenerationService.RuleTrace` is the internal, null-by-default
  side-channel that attributes each emitted anchor to the rule that chose it —
  it is how (a)–(d) were falsified with numbers rather than argued about.
  **Dead ("x") chord notes (`GpPostProcessor.CleanDeadChordNotes`) are two
  different cases — do not merge them.** A dead note inside a chord that still
  has SOUNDING notes is REMOVED: GP leaves tab-arbitrary frets on it (an 11-x-11
  shape arrives as 11-1-11) and the ghost fret poisons the template, the
  auto-fingering and the FHP anchor. The rule is unconditional, with no import
  setting (#471 asked for an opt-in one; it was already always on), and runs
  after `LinkSlides` so slides stay on the remaining strings; a chord it leaves
  with ONE note becomes a single `GuitarNote`. A FULLY dead strum (every string x) is
  LEFT ALONE — frets, fingering and `MuteType.Full` all survive, matching EOF and
  Charter (`fretHandMute="1"` with the real frets, reusing the surrounding
  chords' chordId). Because template find-or-create matches on FRETS alone,
  preserving them is what makes that chordId reuse happen. **The v0.33
  `DeadStrumsAsOpenMutes` setting that zeroed those frets was REMOVED in 0.33.2**
  (AppConfig key, VM property, settings row and resx keys all gone) — it
  destroyed the authored shape and dropped a fret-0 anchor under an
  11th-position passage. Old config files carrying the key are ignored on read.
  **EOF import settings (v0.33, `GpImportSettings` — built from `AppConfig` via
  `FromConfig`, threaded through every tab-import entry point):** (1) short-note / short-chord truncation are the two
  gates on `GpPostProcessor.AdjustSustains` — the sub-quarter-note (`Sustain.BeatId
  == 0`) sustain drop. Notes default ON (= the historical behaviour; OFF preserves
  short single-note sustains); chords default OFF (the handshape bar already carries
  duration; ON also drops short SUSTAIN-VISIBLE-technique chords). A note/chord that
  needs its sustain for a technique is NEVER truncated — do not regress I08's swung
  slide. **A CHORD is one gesture: the same pass LEVELS a chord's notes to the
  LONGEST of them, deliberately.** That is a no-op on every real import — every
  site that writes a chord-note sustain writes the same value to all of them
  (the beat's rhythm at parse, `ChordHandShapeBuilder`'s strike trim, the grace
  shift), and `MergeTiedNotes` only merges chords whose notes match string for
  string, so it adds the same amount to each. RS2014 *can* spell per-string
  chord sustains, so if a future pass ever produces them, decide deliberately
  rather than letting this flatten them silently
  (`GpSlapPopAndChordSustainTests` pins it). (2) beat-text techniques: a "T"/"S"/"P" `FreeText` (gpif) / beat-text
  (legacy binary) → tap/slap/pop on the PLAYABLE voice's note (`GpMarkerImport`);
  default ON. (3) markers→sections (gpif only, default OFF): a MasterBar `<Section>`
  and non-technique beat texts → `arr.EventPoints`/`arr.Phrases` — always a phrase,
  plus a section when the trimmed name is in the RS whitelist (`GpMarkerImport.
  IsRsSectionName`, mirrors `RsManifestGenerator.GetSectionUiName`). (4) TS/tempo:
  the beat map is built by the shared `GpBeatMapBuilder.Build` (GP + Songsterr
  dialogs; NOT in `GpImportSettings` — it's a UI-consumed step): `importTimeSignatures`
  off ⇒ 4/4 for every bar; `dropMidBeatTempoChanges` on ⇒ tempo changes not on a beat
  are discarded.
  **A beat's tempo window is HALF-OPEN `[posFrac, nextFrac)` and its lower bound
  carries `OnBeatEpsilon`.** It used to be the OPEN interval, so a change
  authored exactly on a beat inside a bar — the most common spelling there is —
  equalled this window's lower bound AND the previous window's upper bound and
  was admitted by neither, while the bar-start pass only takes
  `BarPosition <= 0.0001`. The change vanished and the rest of the import drifted
  against the audio; worse, `dropMidBeatTempoChanges` became self-contradictory,
  because `IsOnBeat` KEEPS exactly those points and nothing then applied them.
  The epsilon is shared with `IsOnBeat` so the two cannot disagree, and it is
  what makes a 3/4 bar's stored `0.333333` land on beat 1 rather than one early.
  **The OPENING tempo is `GpBeatMapBuilder.InitialBpm`, never
  `TempoPoints.FirstOrDefault()`.** The list is in PARSE order, not bar order —
  `GpMetadataParser` walks `<Automations>` in document order and fans a repeated
  section's automation over every unfolded pass, and `SongsterrConverter` appends
  its whole `automations` block after the beat-level tempos — so a score whose
  bar-0 tempo lives in `automations` opened at a LATER bar's tempo, which also
  became `project.Bpm`, the transport BPM and `SongsterrSyncTempoMap`'s nominal
  BPM. Four call sites had the old spelling; `GpInitialBpmCallSiteLintTests`
  lints both dialogs so a fifth cannot appear.
- **`GpImportSettings.PreserveShortRests` (2026-08-21, `AppConfig.
  GpImportPreserveShortRests`, the "short rests" General-settings row, default
  ON) — NOT an EOF port.** A rest is the one thing a tab writes that the chart
  model cannot hold: RS drops a sub-quarter sustain, so an eighth-note /
  eighth-rest / eighth-note riff arrives as bare strikes with the articulation
  gone (owner report, measures 40-41 of a real GP8 file at 220 bpm). RS has no
  staccato mark and the charting convention spells staccato as an **ACCENT**, so
  the setting has two halves that never overlap:
  **(a) NOTES** — `GpBeatCarry.ObserveStaccatoBeat` accents a note whose
  following rest run is no longer than the note's own authored value AND no
  longer than a quarter, and only when something sounds again after it (both
  length rules keep a phrase ENDING from reading as staccato). **The decision is
  deferred to the next SOUND, not computed per bar, and that is load-bearing** —
  the rest articulating a bar's LAST note lives in the FOLLOWING bar, a different
  `ParseVoiceBeats` call; a per-bar pass silently missed exactly the note in the
  owner's own example. State rides in a per-voice-slot `StaccatoCarry`, same
  lifetime and same reason as `TieRollState`.
  **(b) CHORDS get no accent** — a hand-shape bar already carries duration, so a
  rest simply BREAKS a repeated-strum run
  (`ChordHandShapeBuilder.BuildAndTrim(splitRunsOnRests:)`) instead of being
  spanned by the 2-beat `MaxMergeGap`. Measures 74+ of the same file were one bar
  over a whole measure that covered its own rest. That parameter defaults OFF for
  a direct caller (the historical merge); the product default is the settings
  flag, and `GpPostProcessor.Run` is what passes it.

## `Import/` — YouTube, audio + metadata

- `Import/` — audio (WAV) import. `Import/Youtube/` — File ▸ Import ▸ Audio from
  YouTube (#820): `YoutubeUrl` accepts only a youtube.com / youtu.be video link,
  `YoutubeAudioDownload` downloads into the saved project's folder (or
  `%LOCALAPPDATA%\DotCharter\YouTube` for an unsaved one) via yt-dlp + ffmpeg
  (`YtDlpAudioDownloader`), and the dialog's launcher attaches the mp3 like
  Attach audio does. It sits behind the contested-features switch
  ([contested-features.md](contested-features.md)) and the one-time rights notice
  (`AppConfig.YoutubeRightsAccepted`, #821). Both tools are
  located by the pure `MediaToolResolver` (which lives under `Import/Youtube/`
  despite being shared): **configured path → exe dir → exe dir/`tools` → PATH →
  the dev-tree `tools/redist/win-x64`** — five steps, the same order the stem
  `FfmpegStemEncoder` uses. The last one is why a developer build finds ffmpeg
  without a release layout, and it was missing from this line. Releases
  bundle ffmpeg and yt-dlp (NOT ffprobe) in the app's `tools/` folder
  (`tools/release/New-Release.ps1`), so import works out of the box. **The
  shipped ffmpeg is a minimal LGPL build with ONLY the components in
  `tools/release/ffmpeg-min/components.json` (#409)**: a new argument vector
  (codec, `-f` format, filter, import extension or call site) must add what it
  needs there, or `Infrastructure/FfmpegComponentInventoryTests` fails — and a
  release ffmpeg without it would fail on every user's machine.
  `Import/Audio/AudioMetadataExtractor` reads artist/title/album/year/
  release-date/genre tags + embedded cover art. **It needs ffmpeg ALONE**
  (releases do not ship ffprobe, #152): ffprobe (`-show_format -show_streams`,
  pure `ParseFfprobeJson`) is used when it resolves and probes cleanly, else
  `ffmpeg -i <file> -f ffmetadata -` is parsed by the pure `ParseFfmetadata`
  (ffmetadata escaping, multi-line values, `[STREAM]`/`[CHAPTER]` sections
  skipped so a cover stream's title never becomes the song title). Both feed one
  `MapTags` key-precedence. Without ffprobe there is no `attached_pic` flag, so
  the cover extract is attempted always; ffmpeg extracts the picture and
  SkiaSharp resizes it to a 512×512 PNG. The `Extract` overload taking a
  `ToolResolver` + `ToolRunner` is the test seam (`AudioMetadataFfmpegOnlyTests`).
  A missing tool or bad probe degrades to `AudioMetadataResult.Empty`, never throwing into an import.
  **Every redirected child pipe must declare `StandardOutputEncoding` /
  `StandardErrorEncoding` = UTF-8 (2026-08-21), and all SEVEN sites in this
  project were missing it.** ffprobe emits UTF-8 JSON, yt-dlp UTF-8 titles — but
  with no explicit encoding .NET decodes a redirected pipe with the CONSOLE code
  page, which on Windows is a legacy single-byte page, so every non-ASCII tag
  came back as MOJIBAKE (owner report: a Japanese album/artist/title imported
  mangled). **It presents as intermittent** — a GUI process without a console
  resolves a different page than one with, so the same build can import the same
  file correctly on one launch and mangled on the next, which is also why the
  behavioural test cannot discriminate on a UTF-8 console. The deterministic
  guard is the source lint
  `Services/Infrastructure/ChildProcessEncodingTests.EveryRedirectedChildPipe_DeclaresItsEncoding`;
  it COUNTS redirects against encodings, because a file that starts two child
  processes (the Python demucs wrapper did, before #662 deleted it) can fix one
  and miss the other. **`StandardInputEncoding` is deliberately NOT
  required**: a redirected stdin is a byte pipe as often as a text one — the
  run-video exporter wrote raw frame bytes to `StandardInput.BaseStream` until
  0.59, and an encoding there would have corrupted the video. Nothing under
  `Services/` redirects stdin today.
  `Import/Audio/ImportAudioPreparer` bakes the configured leading/trailing
  silence into a new project's working `song.ogg` (ffmpeg
  `adelay=<ms>:all=1,apad=pad_dur=<s>` → libvorbis) and preserves the byte-exact
  source as `original.<ext>` (EOF model); beat 0 sits at the leading boundary
  (`ChartData.MainAudioOffsetAdded` records the baked ms). No ffmpeg / encode
  failure / silence off ⇒ verbatim `song.<ext>` copy with `SilenceBaked=false`
  and the caller shifts only the beat grid. Injected into `Session/NewProjectCreator`
  as the `PrepareImportAudio` delegate (the tag read is `Session/INewProjectSources`); pure
  `BuildSilenceFilter`/`BuildEncodeArgs` + injectable runner keep it testable.
- **Add silence on an OPEN project is a real splice (#313), never a grid shift
  alone.** `Import/Audio/AudioSilenceSplicer` (`IAudioSilenceSplicer`) writes
  NEW files and never touches the originals, because the undo step points the
  project back at them: the song as `<song>-silenceN` in the project folder,
  the stems under `stems/silence-N/` with their file names kept (the stem tools
  scan only the top of `stems/`, so the two sets never mix). A WAVE is padded
  without a re-encode (`WavSilencePadder`: zero frames for signed PCM and
  float; 8-bit, compressed and RF64 are refused); anything else goes through
  ffmpeg with the bake's filter (song → Vorbis, stem → float WAVE). All or
  nothing: a failed stem deletes every file of the run. The padded stems are
  saved to the stem cache under the padded song's key, so they are never
  re-separated. `Editing/Maintenance/ProjectSilencePadder.AddSilenceAsync` (owned by `ChartMaintenanceService`) then records ONE
  `AudioSpliceAction` (a project snapshot that also reloads the audio both
  ways); at the start `Editing/Maintenance/SilenceSpliceTimeline` moves every
  absolute-ms field (`SilenceSpliceTimelineTests` fails on a new one) and adds
  to `MainAudioOffsetAdded`. An unsaved session is refused: there is no
  project folder to write into. `StemMixerViewModel` rebuilds on
  `CurrentAudioFileName`, which is how the padded stems reach the engine.
