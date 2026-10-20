# `Editing/` — the chart-model invariants (deep reference) — `Editing/Catalog/` (the phrase catalog) and `Editing/Tempo/` (the beat grid)

## `Editing/Catalog/` — the named phrase catalog

  - `Editing/Catalog/` — the **named phrase catalog** (EOF's fret catalog):
    `IPhraseCatalogService` over a folder of one-arrangement `.rscp` fragments
    at `<ConfigRoot>/phrases/`. Four invariants, all paid for above:
    (1) **A catalog entry IS a tiny `.rscp`** (`PhraseCatalogFragment`) — the
    project serializer already knows every field of every sound, and this repo
    has shipped fourteen one-sided rscp fields; a hand-written phrase DTO would
    be a fifteenth waiting to happen. Never add a parallel phrase format.
    (2) **Positions are stored relative to the phrase's first element**, as
    `FractionalPosition` (beat + fraction) — so a phrase keeps its RHYTHM and
    lands correctly in a song at another tempo. The synthetic 120 BPM grid in
    the fragment exists only to make the file a legal project; nothing reads it
    back.
    (3) **An entry carries template SHAPES, never ids** (ids are positional and
    `RscpTemplateManager` renumbers them every save). Capture resolves every
    chord/hand shape into the fragment's own template list; paste resolves them
    again into the destination through `ChordTemplateResolver` — the same
    `incomingTemplates` path a cross-arrangement clipboard paste uses. A minted
    template keeps the SOURCE template's authored fingering (safe only because
    a just-minted template is still private); a shape already resolved keeps
    the fingering it was first seen with. A hand shape with no resolvable
    template and no chord under it is DROPPED at capture, because it carries no
    information. **The reason this file used to give — "the rscp writer coerces
    `TemplateId -1` to `0`, i.e. to somebody else's shape" — is dead**: that
    coercion was removed in 0.41 and the sentinel is now an ABSENT attribute
    (see lines 37–48 of this same file, which have said so since). The drop
    itself still happens; only its justification was stale.
    (4) **Structure is not a riff**: event points, phrases, sections and tone
    changes are deliberately NOT captured. Pasting them would rewrite the
    destination's structure (the rscp writer even appends unknown phrase names
    to `Arrangement.Phrases` while serializing).
    `PhrasePasteCompatibility` is the pure gate in front of every paste: frets
    are pasted VERBATIM (never transposed — that is `RefretService`'s job), so
    it BLOCKS what the export gate would reject (a string the destination does
    not have, a fret outside 0..24, a non-zero fret under the destination capo)
    and WARNS where the phrase stays legal but stops sounding like itself (a
    tuning difference on a string the phrase actually plays; a capo difference
    when the phrase has open strings). It must never stay silent.
    **Every paste** means the clipboard too, since #327: plain Paste and Paste
    Special used to commit with no check, so guitar content landed on strings 5–6
    of a 4-string bass. `Copy` records the source arrangement on the payload
    (`ClipboardPayload.Source`, a `PhraseSourceContext`), both clipboard paths
    run `PhrasePasteCompatibility.Evaluate(payload, source, target)`, and
    `GlobalEditingCoordinator.ReportPaste` puts the verdict on the status rail
    (`ClipboardPasteCompatibilityTests`).
    `phrases/` is on **both** `PayloadReceiptFilter.ExcludedTopDirs` and
    `ConfigBackupRules.IncludedTopDirs` — see the `PayloadReceiptFilter.IsLocalState`
    footgun in [infrastructure-update.md](../infrastructure-update.md).

## `Editing/Tempo/` — the beat grid

Four invariants, all of them shipped broken and all of them now in the default
gate. The verbs live in two places: `Services/Editing/Tempo/` (`TempoService`,
`BeatScaleService`, `BeatResyncService`) and
`DotCharter.Services/Editing/Maintenance/ChartMaintenanceService.TempoMap.cs` (the Tempo Map
menu: each verb records one project snapshot; the grid arithmetic it runs on the
AFTER copy is `Services/Editing/Tempo/TempoMapBeats`, pure and static, and Add
silence is `Editing/Maintenance/ProjectSilencePadder`). Position re-anchoring is `Core/Utilities/ChartProjectPositions` — never a
hand-written walk (`BeatScaleService` is the one sanctioned copy, held to the
same inventory).

- **A tempo written on a beat governs what FOLLOWS it; the beat itself never
  moves.** `TempoService.RecalculateBeatTimes(beats, startIndex)` walks from
  `startIndex + 1`. It used to start ON `startIndex` and re-derive that beat's
  time from its predecessor — a no-op on a grid whose spacing matches its
  printed BPM, and a silent yank on every imported or hand-dragged one, where
  the `Bpm` field is derived FROM the times and only approximates them.
  (`TempoServiceTests.UpdateBpm_DoesNotMoveTheBeatItIsWrittenOn`)
- **Un-anchoring is the ONE case where the edited beat moves**, and it needs its
  own verb for that reason: `RetimeBeatFromTempo(beats, index)` puts the beat on
  the grid its predecessor implies and touches no BPM. The Beat dialog calls it
  only when the anchor was CLEARED. (`Presentation/Dialogs/BeatViewModelTests`)
- **The first beat of the grid is a bar line, and every verb that changes WHICH
  marker sits at index 0 hands the line over rather than dropping it.** Insert
  Beat and Remove Beat at index 0 both did drop it, and
  `Core/ChartPositionLabel.Measure` plus the status bar count bars by walking
  `IsMeasure` from index 0 — so bar 1 absorbed the extra beat and every later bar
  number in the song shifted, with nothing failing (a grid with no line at index
  0 is still a legal ascending list). (`TempoMapDownbeatTests`, which also pins
  insert→remove at one pivot as an exact identity on the grid)
- **A beat DROPPED by a rewrite hands its `IsMeasure` and `IsAnchor` to the next
  beat kept.** `BeatScaleService.BuildHalfTimeBeats` keeps every other beat and
  the phase comes from where the RANGE starts, not from where the bar lines are,
  so a range beginning off-beat deleted every downbeat inside it — and the
  renumbering that followed ran past the scaled range to the end of the song.
  Half-time never moves a beat's `Time`, only deletes, so the transfer changes
  which beat carries the line, not what it means.
  (`BeatScaleServiceTests`)
- **"Lock beat map" is a promise, and `ChartMaintenanceService.ApplyMidiDraft`
  was the one writer that broke it** — it CLEARS `project.Beats`, so a locked
  chart lost the grid every position is relative to. The lock covers the grid,
  not the import: skip the grid half and keep the draft, as
  `GpImportApplier.ApplyTempoAndKey` does. Grep `IsBeatMapLocked` before
  adding any beat-grid writer. (`MidiDraftBeatMapLockTests`)
- **The lock flag is PERSISTED** (`RscpWriter` emits `dcBeatMapLocked`), so
  toggling it is a document change and marks the project dirty. It cannot take a
  history node — the menu checkmark reads the flag directly and an undo would
  leave the tick stale — so `ChartMaintenanceService.ToggleBeatMapLock` uses
  `IProjectSessionService.MarkProjectDirty`, the "a change the history cannot
  represent" case. (`BeatMapLockDirtyTests`)

- **Align beat grid to notes moves the GRID, never a timed element** (#403).
  `Editing/Tempo/BeatGridShift.Apply` reads every position in ms on the old grid
  and re-places it at that ms on the shifted one through
  `ChartProjectPositions.Remap`; the two relative spans that walk skips
  (`GuitarNote.Sustain`, `ChordNote.Sustain`) are re-derived from their absolute
  END, because a sustain's length in beats changes across a tempo change. The
  grid grows where the shift uncovers an element the old grid covered — beats
  prepended at the first interval (the last one clamped to 0:00, never before
  it) or appended at the last — and a beat pushed before 0:00 is dropped; the
  first beat stays a bar line, a short leading bar says so in `BeatsInMeasure`.
  The verb is `ChartMaintenanceService.AlignBeatGridToNotes` (one
  `ProjectSnapshotAction`, refused on a locked map); the suggested offset is
  `Editing/Tempo/BeatGridOffsetEstimator` — the median distance of the onsets
  to the nearest 1/4- or 1/3-beat line, and of the near-equal shifts the one
  with the most onsets ON a beat (a quarter-beat shift aligns straight
  sixteenths just as well). (`BeatGridShiftTests` — a reflection walk over every
  `FractionalPosition` in a fully populated project — `BeatGridOffsetEstimatorTests`,
  `AlignBeatGridTests`)

- **Align to recording moves BEAT TIMES only, and notes follow because they are
  grid-relative** (#784). `Editing/Tempo/RecordingAlignment/`: `RecordingAlignmentPlan`
  reads the chart on the calling thread (beat times, which beats are FIXED — outside
  the range, or `BeatMarker.IsAnchor` when the charter keeps hand-placed beats — the
  expected sounds of every instrumental arrangement's highest level, and the
  sections); `RecordingAlignmentService.AnalyzeAsync` decodes off the UI thread
  (`RecordingAlignmentAudio`: the instrument stems already beside the song, else the
  mix — never a separation) and runs `DotCharter.Analysis/BeatMap/RecordingAligner`;
  nothing is written before `Apply`, which refuses a STALE proposal (any beat time
  changed since the analysis) and writes through `IBeatResyncService` as ONE
  `MutateBeatsAction` (bars, time signatures and anchors copied; tempo re-derived
  per changed interval). `RecordingAlignmentComposer` turns a SKIPPED section into
  a stretch between its aligned neighbours, so the grid stays strictly ascending.
  It does not re-derive sustains or ms-keyed markers.
  (`RecordingAlignmentServiceTests`, `RecordingAlignmentPlanTests`, `RecordingAlignerTests`,
  `RecordingAlignerDenseMixTests`; the aligner's design and corpus numbers are in `DotCharter.Analysis/AGENTS.md`)

**Still open, deliberately** (measured 2026-09-04, not fixed here):
`AddMeasuresAtStart` prepends beats at NEGATIVE times when the grid already
starts near 0, and a scale leaves `BeatsInMeasure`/`NoteDenominator` stale (a
double-timed 4/4 bar holds eight beats and still says 4/4).
