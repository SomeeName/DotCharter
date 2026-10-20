# Sheet export/render, Coordinators, Styles + Theming (UI deep reference)

Extracted verbatim from `DotCharter.UI/AGENTS.md` — these were bullets under
`## Layout`. **Read the relevant one before touching the sheet exporters, the
coordinators, `Styles/` or `Theming/`.**

**Where these live since wave 0 (2026-09-19):** the old `DotCharter.UI/Sheet/`
is `DotCharter.Services/Export/Sheet/` (the view, `Controls/Sheet/SheetMusicView`,
stays in the UI, with its paint layers and Skia draw op in `SheetMusicViewLayers.cs` —
the cursor, selection and hover overlays share ONE `SheetOverlayLayer` base that holds
the page-mode state and the measure, so each keeps only its `Render` — its pointer
gestures and GP-style keys in `SheetMusicViewInput.cs`, and its pure hit lookups in
`SheetHitIndex.cs`), and the old `DotCharter.UI/Coordinators/` is split across
`DotCharter.Services/Audio/Coordination/` (playback, tones, tone preview, stem
waveforms), `Session/` (`ProjectSessionService`), `Editing/` + `Editing/Maintenance/`
+ `Editing/Tracks/` and `Infrastructure/Recovery/` (`CrashRecoveryService`). The
rules below are unchanged by the move; the file names in them are the new ones.
The coordinators reach the UI only through seams: `IUiDispatcher` (posts keep
their priorities — `PostNormal` is the Normal `Dispatcher.UIThread.Post`),
`IChartEditorSurface` / `IEditorTransport` (implemented by `ChartEditorViewModel`
/ `TransportViewModel`), `IMessagePrompt` and `IGameplayToneHud`.

## Sheet (`Services/Export/Sheet/`), the coordinators, `Styles/`, `Theming/`

- Sheet (`DotCharter.Services/Export/Sheet/`) — `AlphaTexExporter` (domain → alphaTex; emits chord names via `{ch}`,
  ties held notes across barlines), `SheetMusicRenderer` (AlphaTab + AlphaSkia →
  SKImage chunks; `RenderAsync` = Horizontal strip, `RenderPagedAsync` = Page
  layout for print), and `SheetPdfExporter` (paginated A4/Letter PDF via
  `SKDocument.CreatePdf` with a title block + tuning legend).
  **Beaming invariant:** AlphaTab auto-beams runs of consecutive `:8`/`:16`
  beats, so beaming is purely a function of emitting CLEAN uniform durations.
  All exporter rhythm math therefore runs on the EXACT fractional beat grid
  (`FractionalPosition` → measure-local ticks, 48 ticks per quarter so 64ths AND
  triplets are exact) — NEVER on millisecond round-trips or a measure-wide
  average `msPerTick`; ms-noise from imported charts is absorbed by `SnapTick`
  (±1 tick onto the 16th/16th-triplet/32nd lines). Off-grid quantization emits
  dotted/tied slivers that suppress beaming (runs render as individual flagged
  notes). `AlphaTexBeamingTests` guards it.
  **…and the exact grid is only half of it: EVERY BAR MUST EMIT EXACTLY ITS
  TIME SIGNATURE (0.44).** The tick grid was exact while the code that CUT it
  into glyphs was not, so bars silently emitted 193 of their 192 ticks and
  AlphaTab engraved the surplus as the very sliver the grid exists to prevent.
  **1 tick (1/192 beat) is the ONE value no standard duration expresses**, and
  all three defects were the same reflex — round it UP and "absorb" the
  overshoot. The three rules that now hold the line, each proved by breaking it:
  - `ChooseChunk` **never returns more ticks than it was asked for, and never
    leaves a remainder of 1** (it skips a candidate that would). A 46-tick head
    used to split 36 + 9 + 2 = 47. It may now fall back to a TUPLET value to
    close an off-grid tail exactly — normally forbidden as filler, but a bar
    that sums beats a bar that does not.
  - **A head may never write past the barline.** `gapTicks` floors at `MinTicks`
    (3), so an onset landing within a 64th of the barline wrote a 3-tick head
    into a 2-tick remainder; `durTicks` is now capped by `totalTicks - cursor`
    as the LAST word, after every widening rule. An onset with under 2 ticks of
    room is handed to the NEXT bar (leave `state.SlotCursor` pointing at it)
    rather than squeezed in or dropped.
  - **A gap under 2 ticks is not a rest.** Emitting one made `EmitRests` round
    up; it is absorbed and the head starts at the cursor. `EmitRests` also never
    splits so as to leave 1 behind.
  `AlphaTexIntegrityTests` guards it — an adversarial set (overlapping sustains,
  triplets, 7/8, over-long hints, the three real shapes) plus **1,500 fuzzed
  charts** over six meters with onsets and sustains anywhere on the 1/192 grid.
  The fuzz is what found all three, at seeds 1, 378 and 645; keep it.
  **The beat map's `DurationTicks` IS the emitted glyph's tick value**, which is
  what makes "the bar map sums to the bar" a real proof and not a proxy.
  **Capo: emitted frets are CAPO-RELATIVE, `max(fret − capo, 0)`.** AlphaTab
  ADDS `\capo` to every fret when it derives pitch (probe-verified: fret 5 under
  `\capo 3` reports `RealValue` 48). DotCharter frets are ABSOLUTE on the
  un-capoed neck with fret 0 = open, so the sounding offset is `max(fret, capo)`
  — the derivation shared with `ChordNameSuggester`, `RefretPlanner.SoundingMidi`
  and `LivePitchAcceptance.SoundingMidi`. Emitting the absolute fret engraved every
  FRETTED note *capo* semitones sharp while OPEN strings stayed correct, which is
  why it read as "the notation is slightly off" rather than as a bug. `StaffMap`
  is the single place a domain (string, fret) becomes an alphaTex one — never
  emit a raw fret. Capo 0 is a strict no-op.
  **…and `StaffMap.String` CLAMPS, which is right for the token and wrong as a
  filter (0.61).** Narrowing an arrangement from 7 strings to 6 changes
  `StringCount` and rewrites no notes, so a leftover string-7 note clamped onto
  the same alphaTex string as the real high-E note beside it and the beat emitted
  `(3.1 4.1)` — two digits on one tab line. `CollectPlayed` takes the `StaffMap`
  and DROPS anything `IsOnTheNeck` rejects; the slot keeps the sound, so it stays
  clickable, and a slot whose sounds are all off the neck emits a rest.
  **Where the BARLINES are: `BeatMarker.IsMeasure`, not a count of
  `BeatsInMeasure` (0.61).** That flag is what every other consumer counts bars
  by (`ChartPositionLabel`, `LiveBarNumbers`, `SnapService`,
  `ChartSpacingEnforcer`, `PhraseSectionGenerator`, `SngFromDomainMapper`) and
  every importer sets it. For a regular grid the two agree exactly, which is why
  counting looked fine — they diverge on an IRREGULAR grid, and a chart starting
  on a PICKUP is the common case: two pickup beats put the first barline two
  beats early and every barline after it, so the sheet's bar 3 was the
  timeline's bar 2 and clicks resolved into the wrong bar. A bar now runs from
  one flagged downbeat to the next and DECLARES the span it holds (a two-beat
  pickup engraves as a two-beat bar — how a pickup is written, and what keeps
  "every bar emits its time signature" a real claim). **The flags are used only
  when they ARE a barline set** — `MeasureFlagsAreUsable` wants ≥2 of them and no
  gap over twice the largest numerator, because `TempoService` marks beat 0 and
  nothing else and a half-finished set would engrave the song as a few enormous
  bars; otherwise the count-based grouping stands unchanged.
  **The time-signature DENOMINATOR is normalized in `BuildMeasures`, once, before
  both the tick math and the `\ts` token (0.61).** `NoteDenominator` is an
  unvalidated int and the beat dialog offers 1..64 in steps of ONE, so `\ts (4 3)`
  is a keystroke away — and the 1.8.3 parser REJECTS it, so the whole pane fell
  to its error state and the PDF export threw. Same set and fallback as
  `GpExporter.NormalizeDenominator` so the two exports of a malformed meter agree.
  **A bar of RESTS is content.** `EmitState.HasContent` gates Export's "nothing
  was emitted, give the importer one beat" fallback, and only the note/tie paths
  set it — so a level with a beat grid and no sounds appended `:4 r` INSIDE its
  last bar (4800 ticks in a 3840-tick bar), on every chart whose active level is
  still empty. `EmitRests` marks it now; the fallback still covers "no beat grid
  at all".
  **The `\tuning` header goes through `TuningOffsetResolution.Resolve`, never
  `TuningPitches` directly (0.61).** A chart states its tuning twice — offsets and
  note LETTERS — and a letters-only arrangement has an EMPTY offsets vector, so
  the raw read engraved it as E standard while the PDF's own legend (which reads
  the letters) printed the real tuning on the same page. The RS-XML, rscp, EOF and
  GP writers were moved onto the resolution when it was written; the sheet was the
  straggler.
  **Escaping: `\` is escaped BEFORE `"`, and control characters fold to spaces.**
  The 1.8.3 lexer honours `\\` and `\"` inside a string literal and treats every
  other `\x` as a hard parse error, so a title, artist or chord name carrying one
  backslash (`AC\DC`, a pasted path) threw `UnsupportedFormatError` and took the
  WHOLE document down — no notation at all, from one character of metadata. A
  TRAILING backslash escaped the closing quote and ran the string on into the
  score. Order matters: doing the quote pass first re-breaks it, because the `\`
  it inserts would then be doubled.
  **Engraving rules the exporter owns (0.18, don't regress):** staff metadata is
  always emitted — real `\tuning` (standard-pitch tables + `TuningOffset.Step`,
  alphaTex lists strings highest-first, octaves are scientific), `\capo`,
  `\instrument` (33 bass / 27 guitar — its side effect is AlphaTab's GP-style
  −12 display transposition, so notation reads an octave up like every tab
  book), `\clef f4` for bass arrangements and `\ks` from
  `ChartData.KeyAccidentals`. Durations: a real sustain is honored (quantized to
  the 16th grid; remainder of the gap becomes beat-aligned rests; only a REAL
  sustain ties across a barline), a zero sustain fills to the next beat line or
  onset (dense runs stay uniform) but never ties across the barline — the old
  gap-fill rendered a short hit before a 30-bar break as 30 tied whole notes.
  Coincident onsets merge into ONE beat (interval/chord — they used to be
  silently dropped); pure Rocksmith `LinkNext` chains render as ties (`-.s`);
  chord names print on harmony CHANGE only; triplet gaps emit real `{tu 3}`
  tuplets (exact-match-first in `ChooseChunk`; triplets never used as filler).
  The model side of AlphaTab: `Note.String` is 1 = LOWEST (reverse of alphaTex
  input, i.e. `String − 1` == domain string index).
  **Notation hints (0.18):** `GuitarNote.NotationDuration` / `Chord.NotationDuration`
  (optional `Fraction`, quarter-note units; rscp attribute `nd`, backward-tolerant)
  carry the AUTHORED rhythm from score imports (GP XML path; captured
  BEFORE `GpPostProcessor.AdjustSustains` zeroes short sustains, SUMMED when
  tied beats merge). When present the hint WINS over sustain- and fill-derived
  durations (capped by the gap to the next onset so edited charts can't overlap)
  — an imported tab renders as authored and stays synced to chart edits; new or
  edited notes without a hint fall back to the derived rules above.
  `RscpNotationHintTests` + `AlphaTexNotationHintTests` guard the round trip.
  **Duration editing (0.25):** the sheet pane's note-value palette + the GP-style
  +/− keys WRITE these hints. `IHighwayEditingService.SetSelectedNotationDuration`
  (absolute, null clears) and `ScaleSelectedNotationDuration` (×factor, clamped to
  the whole..64th ladder, null hint counts as a quarter) mutate NotationDuration on
  every selected note AND chord as ONE `CompositeAction` undo step — a chord and
  its notes are both sheet beats. **Both `ChartActionStateComparer.GuitarNotesEqual`
  AND `ChordsEqual` now compare `NotationDuration`** (and `GraceFret` on notes): a
  duration-only mutation otherwise reads as a no-op and `MutateElementsAction.HasEffect`
  silently drops it — so any future notation-hint edit MUST be in those comparers.
  The palette (`SheetMusicPaneViewModel.ApplyDuration`) folds dotted (×3/2) / triplet
  (×2/3) modifiers into `Fraction(4, denominator)`; dotted/triplet are mutually
  exclusive INPUT flags (they do not transform the selection on their own — our data
  is a single fraction, not base+dot). The sheet's +/− keys are handled in
  `SheetMusicViewInput.TryHandleKey` (called from `SheetMusicView.OnKeyDown`) and
  deliberately SHADOW the global playback-speed +/−
  (`SpeedUp/SpeedDown` = OemPlus/OemMinus) **only while the sheet is focused AND a
  sound is selected** — empty selection falls through to speed; the sheet holds no
  TextBox so this control handler never fires over a text input.
  `SheetMusicPaneViewModelTests` + `SheetDurationRenderTests` guard it (the render
  test proves the changed note value repaints the staff).
  **Hover + multi-select (0.25):** the strip hovers the exact note head / tab digit
  under the cursor via a thin `HoverOverlay` (accent token, subtler alpha than the
  selection band) — it reuses the SAME `TryHitBeat` + string-lattice mapping as
  click-select, resolves the precise tab-digit Y cell, and is deliberately
  view-LOCAL (NOT pushed into `ISelectionService.Hovered*`; hover moves at pointer
  rates and no other view consumes the sheet's hover). Ctrl+click is additive:
  `SelectSoundsRequested` now carries an `additive` flag and
  `SheetMusicPaneViewModel.SelectSounds` toggles the clicked sound in/out of the
  shared multi-selection (a Ctrl+click on blank space is a no-op). Drag on the
  strip stays reserved for scrub-seek — a rubber-band MARQUEE was deferred (it
  would need a modifier-gated gesture + its own hit-test to avoid shadowing scrub).
  **Paged layout (0.25):** `SheetMusicView.PageMode` (bound to
  `AppConfig.SheetMusicPageMode`, toolbar toggle) switches between the single
  horizontal system (`RenderAsync` / `LayoutMode.Horizontal`, the default
  synced-follow editing flow) and GP8-style PAGE view (`RenderPagedAsync` /
  `LayoutMode.Page`). Page mode renders at a FIXED page width (the viewport) with
  zoom driving glyph size (zooming re-wraps to more, taller systems), displays at
  1 device px → 1 DIP so the page fills the width and scrolls VERTICALLY, and
  playhead-follow scrolls to the active system (the cursor is a segment on that
  system's Y band, not a full-height line). Click-mapping, selection and hover
  work in BOTH layouts through the measure/beat bounds — `FindMeasureIndex` does a
  rect-containment lookup (Y picks the system, then X the bar) in page mode vs. an
  X-only scan in the strip; NEVER a screen-position heuristic. Because string Y is
  not constant across stacked systems, the renderer emits a PER-SYSTEM tab lattice
  (`RenderResult.SystemLattices`); `MapSourceYToString` resolves the system by Y
  band first, then the string within it (interval-pick / insert stay correct).
  `SheetPageLayoutRenderTests` proves the page wraps into ≥2 stacked systems with
  ≥2 per-system lattices while the strip stays one row. STRIP remains the default;
  its render path + overlay sizing are unchanged (page mode is a gated branch).
  **Multi-voice (0.25):** `Arrangement.NotationVoices` (notation-ONLY sidecar
  voices from GP multi-voice imports — the chart level stays single-voice
  playable) render as additional alphaTex voices on the same staff. The parser
  contract, probe-verified against the bundled AlphaTab 1.8: in multi-voice
  output EVERY voice — the first included — is prefixed `\voice` and lists ALL
  its bars in sequence (without the leading marker a single-bar first voice
  swallows voice 2 as bar 2); bar metadata (`\clef`/`\ks`/`\ts`/`\tempo`) is
  emitted by the FIRST voice only (it is master-bar state — re-emitting doubles
  every tempo marker); every voice walks all measures so bar counts always
  match; chord-name labels stay voice-0-only. Single-voice charts emit no
  `\voice` token at all — their output is byte-identical to before.
  **The beat map is VOICE 0 ONLY**: `ExportResult.BeatMap` records the chart
  voice, and `SheetMusicRenderer.ExtractBeatBounds` filters AlphaTab's bounds
  to `Voice.Index == 0` (and non-grace beats) so the per-bar index zip in
  `SheetHitIndex.BuildBeatLookup` stays 1:1 — sidecar glyphs are not click
  targets. `AlphaTexMultiVoiceTests` + `SheetMultiVoiceRenderTests` guard it.
  **Bends + graces (0.25):** chart `BendValues` (Step in SEMITONES) emit as
  per-note `b (v0 v1 …)` with values in QUARTER-steps (4 = whole tone,
  parser-verified; clamp 0..12): leading 0 when the first point sits after the
  onset, plateaus collapsed, lone at-onset point = held prebend `(v v)` — so
  bend/release/prebend curves draw above the staff exactly like GP.
  `GuitarNote.GraceFret` emits a 16th before-beat grace beat (`{gr}`) ahead of
  the head; grace beats consume NO bar time (parser-verified) and are NOT in
  the beat map (renderer bounds filter grace out — clicks stay 1:1).
  **A CHORD grace is ONE grace beat holding every graced string (0.33.2):**
  GP graces a strum with one grace note per string, so `AppendGraceBeat` walks
  `slot.Chords → ChordNotes` as well and emits the parenthesised multi-note form
  `(f.s{gr} f.s{gr} …)` — parser-verified, `{gr}` is accepted per-note and the
  beat still consumes no bar time. A string may be graced only ONCE per beat and
  single notes keep their "first hint wins" rule, so a slot with no chord grace
  emits byte-identically (one hint stays the bare `f.s{gr}`).
  `AlphaTexBendGraceTests` guards all of it.
  **Engraving fixes (0.25, don't regress):** tab-only rhythm mode is
  `TabRhythmMode.ShowWithBars` — despite the name, `ShowWithBeams` renders
  every beat as a DETACHED flag (the shipped "nothing ever beams in the tab
  strip" bug); `ShowWithBars` is the mode that beams runs like standard
  notation (`SheetTabBeamingRenderTests` guards it structurally, and the
  guard provably fails on the old value). Plain chart slides emit `ss`
  (shift slide — the clean GP-style diagonal connector); `sl` is LEGATO and
  adds a slur arc + "sl." label per slide, which stacked into tangled
  overlapping curves on slide runs. `NotationElement.EffectDynamics` is
  suppressed — the domain has no dynamics, and AlphaTab's default forte
  rendered a stray "f" under the first beat of every score.
  **What the sheet still DROPS (0.44 audit — walked the whole domain model).**
  Emitted and verified: bends, slides (`ss`), unpitched slides (`sou`),
  harmonics (`nh`/`ph` — the domain has only None/Normal/Pinch, so complete),
  palm/full mute, vibrato, accents, hammer-on/pull-off (on the ORIGIN),
  **tap (`tt`, 0.44)**, tremolo, chord names, **chord fingering (`lf N`, 0.44 —
  it lives on the `ChordTemplate`, so it is resolved in `BuildSlots` where the
  template index is in scope; alphaTex numbers fingers 1..5 thumb..little and the
  domain 0..4, hence the +1)**, capo, tuning, key/clef, time-signature and tempo
  changes (a whole-number marking from the bar's MEAN beat tempo, only when it is
  `SheetTempoMarking.StepBpm` from the last marking, #286), grace notes, `LinkNext` ties, notation-duration hints, multi-voice.
  Still lost, and each needs more than a token:
  - **`SlideTo`'s TARGET fret** — slides emit only a bool, so `ss` draws to
    whatever note happens to follow on that string. A slide-out with no following
    note draws nothing. `sld`/`slu` are NOT in the bundled 1.8.3 parser
    (probe-verified: both throw), so this needs a real slide-out model.
  - **`BassPickingType` Slap/Pop** — `slap`/`pop` also throw in 1.8.3.
  - **`GraceSlide`** — the grace beat emits, its slide flag does not.
  - **Sections/phrases** (no rehearsal marks) and **lyrics** (no vocal line).
  - **Repeats** — the domain has no repeat structure, so `\ro`/`\rc` have nothing
    to read; bars are always written out longhand.
  - `Ignore` / `IgnoreDerived` / `PassOtherNotes` / `ForceNoNotes` /
    `SplitIntoNotes` are Rocksmith gameplay flags with no notation meaning and
    are dropped DELIBERATELY. `isLetRing` is hard-wired false because the domain
    carries no let-ring field — honest, not a bug.
  **`SheetPdfExporter.ExportToFileAsync` is the only way to write the PDF to a
  PATH, and it is ATOMIC** (renders into a sibling `.tmp`, swaps with
  `AtomicFile.ReplaceWithRetry`, deletes the temp on any throw). The call site
  used to open the user's chosen path with `FileMode.Create` — which truncates on
  OPEN — and only then start rendering, so a cancelled render, an unrenderable
  chart or a full disk left a zero-byte ".pdf" looking like a finished export,
  and exporting OVER a good PDF destroyed it before its replacement existed.
  `SheetPdfAtomicWriteTests` guards it (an already-cancelled token fails the
  render before AlphaSkia, which keeps the test deterministic and display-free).
  **…and the PDF is engraved in a DIFFERENT ink from the strip.**
  `SheetMusicRenderer.InkPalette` is `Screen` (light `#E8E8E8` glyphs, for the
  `SKColor(28,28,32)` backdrop `SheetDrawOp` fills behind a chunk) or `Print`
  (black, for paper). The chunks are TRANSPARENT and `WritePdf` never fills a
  page background, so before this the exporter reused the screen ink and put
  every staff line, note head, stem and fret digit on white at ~1.15:1 — blank
  paper — while its own title block, drawn in `SKColors.Black`, came out fine.
  `SheetPdfExporter` passes `InkPalette.Print`; nothing else may.
  `SheetInkPaletteTests` pins the contrast of both inks against the ground each
  is read on, that the screen values did not move, and that the exporter asks
  for print while `SheetMusicView` does not. It has to assert the palette
  DECISION rather than pixels: `SheetPdfExporterTests` records that the notation
  raster goes through AlphaSkia (native) and cannot be produced deterministically
  headless, which is exactly why this shipped unseen.
  **`SheetMusicView` unsubscribes all three singletons in
  `OnDetachedFromVisualTree`** — `ProjectState.PropertyChanged`,
  `ISelectionService.SelectionChanged`, `IHistoryService.HistoryChanged` — plus
  `_debounce.Stop()` and `_renderCts?.Cancel()`. `ViewSubscriptionLeakGuardTests`
  only proves the OVERRIDE EXISTS; it is blind to an override that exists and
  forgets one `-=`, which is the shape the leak actually takes once a fourth
  subscription is added to attach. `SheetMusicViewDetachTests` names each
  subscription (a source lint, comments stripped, with a companion assert that
  each `-=` it demands has a real `+=` so the list cannot drift). It must stay a
  source lint: building the view builds its `ItemsControl` content, which needs a
  dispatcher and throws under the full gate.
  **`SheetMusicView.RebuildAsync` is fire-and-forget**, so it must never throw: the
  `AlphaTexExporter.Export` call belongs INSIDE its try (a chart the exporter chokes
  on otherwise leaves the pane stuck on "Rendering sheet music…" forever, unlogged),
  and `OnDetachedFromVisualTree` cancels `_renderCts` — stopping the debounce alone
  only prevents the NEXT rebuild, so every float/redock during a rebuild burned a
  full AlphaTab layout + raster for a dead view.
  **It also SKIPS the layout + raster when nothing it would draw changed
  (0.61).** `Export/Sheet/SheetRenderKey` is the exported AlphaTex folded with the
  geometry — width, height, DPI, zoom, page/strip mode, staves mode — and an
  unchanged key keeps the chunks already on the canvas; the cursor and the
  selection highlights are overlays over those same chunks, so they still
  follow. **The export stays on the UI thread before the first `await`** (it
  reads the live graph) and runs whether or not the render is skipped: it is how
  the answer is found. Four ordering rules make the skip safe and each was a
  deliberate move: the measure TIME spans (`_measureSpans`, what the cursor maps
  song time through) are adopted BEFORE the check, because the AlphaTex carries
  no beat times and a beat nudged a few ms leaves it byte-identical (#286); the key is recorded ONLY where `SetResult` put chunks on the
  canvas and cleared on every failure path, the "Rendering…" status is set AFTER
  the check (there is no dispatcher turn in that stretch, so it could never have
  been drawn during the export anyway), and `_renderCts.Cancel()` moved BELOW the
  check so a skipped rebuild cannot kill an in-flight render for the very key it
  matched. **The theme is deliberately NOT in the key** — the strip always
  renders `SheetMusicRenderer.InkPalette.Screen`; the palette is a parameter of
  the PDF path. If the strip ever takes its ink from the theme, that stamp
  becomes a field. Pinned by `AlphaTexExportStabilityTests`.
- The coordinators (Services since wave 0) — glue between services and VMs.
  - **`AudioPlaybackCoordinator` owns the playhead pipeline AND the
    metronome/clap scheduler.** Two rules there, both paid for:
    1. **Claps/ticks are SCHEDULED ahead on
       `IAudioPlaybackService.RawPlayheadMs`, never triggered on a crossing of
       the `PositionChanged` playhead.** That playhead is latency-COMPENSATED
       (what the user is hearing), so a click fired when it crossed a note had to
       re-cross the output buffer and landed a full output latency LATE — a
       systematic bias that makes claps useless as a timing reference. The scan
       walks a 120 ms lookahead window on the RAW playhead and hands each
       beat/onset its own chart time to `TryScheduleClick`, which places the
       voice in the same output samples as the note's audio (ABI v26 render
       clock) so both reach the ear together, whatever the latency is. **Get the
       sign backwards and you double the original bug.** A chord and its notes
       share an onset — the merge window keeps that to ONE clap.
    2. **The handler runs on the audio timer thread and indexes live UI-thread
       `ObservableCollection`s**, and `System.Timers.Timer` swallows exceptions —
       an unguarded scan meant a chart edit during playback silently killed the
       whole tick. The scans are individually
       try/caught so the playhead bookkeeping and UI publish still run; the
       section/phrase/lyric cursors are UI-thread-only and must NOT be reset from
       the timer thread (they clamp + walk both ways, so they self-correct after
       a seek).
  - **`RsTonePlaybackCoordinator`** (0.16 "Use in-game tones", Phase 2) drives
    the in-house DSP rig from a song's converted Rocksmith tones:
    `PrepareForSong` (pre-play, dry-convert + badge summary), `BeginRun`/`EndRun`
    (snapshot the manual chain, apply the base tone, then live-switch presets via
    `DcRigApplier` as `ProjectState.CurrentTimeMs` crosses each `ToneChange`).
    - It subscribes to the SAME coalesced ≤60 Hz playhead — **no second timer** —
      and applies OFF the UI thread on a latest-wins worker; the manual chain
      (incl. VST slots) is restored verbatim on `EndRun`.
    - Per-SEAT auto tones (0.58) went with couch co-op on 2026-09-19 —
      restore from tag `pre-cut-2026-09`. Only seat 0 follows the chart's tone
      timeline.
    - Active when `AppConfig.UseInGameTones` is on and the arrangement exposes
      parseable tones, with a **per-song override**
      (`AppConfig.SongTonePreferences`, keyed by `PlaySongItem.SongIdentity`, set
      from the pre-play tones card). `BeginRun` consults it FIRST: 'My tone'
      applies the chosen user tone profile once via `IDspTonePresetService` and
      never drives; 'In-game tones' forces driving even with the global toggle
      off; entries reset to 'Global setting' are removed from the dict.
    - **The global toggle is LIVE mid-run**:
      `PlayModeViewModel.OnUseInGameTonesChanged` calls `BeginRun`/`EndRun` while
      Playing/Paused (both idempotent) — config must be saved before `BeginRun`
      (it re-reads it) and `BeginRun` must precede `RefreshInGameToneState` (a
      fresh in-flight prepare makes it skip).
    - While the off-thread `PrepareForSongAsync` conversion is still running,
      `BeginRun` skips — the coordinator exposes `IsPrepareInFlight` and the VM
      shows it as the 'Tones loading…' badge on the pre-play tones card (never a
      silent no-drive).
    - **Base tone comes from the loaded arrangement**
      (`FirstNonEmpty(StartingTone, ToneBase, ToneA)`) — for library PSARC songs
      the package reader's SNG mapper populates these from the manifest's
      `Tone_Base`/`Tone_A..D` (threaded via `LibraryArrangementEntry.ToneBase`/
      `Tones`). If that base is ever left empty again, a single-tone song exposes
      NO tones (`HasInGameTones` false → card hidden) and a multi-tone song's
      intro never gets its rig — the 0.16 "tones don't switch" bug.
    - Tone-key matching is case- AND whitespace-tolerant (`NormalizeKey` trims;
      dict is `OrdinalIgnoreCase`); unmatched names log the available keys.
    - Every applied preset is published to the
      `DotCharter.Rendering.Data.GameplayToneHud` channel (`SetRig`/`ClearRig`),
      through `IGameplayToneHud` (Services may not reference Rendering; the UI's
      `Platform/RenderingGameplayToneHud` forwards)
      so the 3D HUD shows the actually-applied tone (live proof switching fires,
      even without input monitoring).
    - **`EndRun` is called on the UI thread and therefore RETURNS IMMEDIATELY**:
      it signals the stop and clears the run state, but awaiting the apply worker
      and running the restore apply happen on a background finisher
      (`RestoreCompletion`, internal test seam) — the old `_worker.Wait(750ms)` +
      `lock (_applyGate)` on the UI thread froze the window when
      finishing/quitting a song with a late tone change, and the "bounded" wait
      bounded nothing because the gate that followed had no timeout. **Never take
      `_applyGate` on the UI thread.**
    - Because the restore now lands after `EndRun` returns, a re-entered run must
      not re-snapshot the live chain (still the previous run's TONE rig):
      `BeginRun` bumps a run generation and adopts the pending restore's
      snapshot, and the finisher re-checks that generation INSIDE `_applyGate` so
      a superseded restore can never stomp the new run's tone.
  - **The chart editor's TONE PREVIEW (0.56) is that same coordinator with a
    different tone SOURCE, and it is deliberately not a second one.**
    `PrepareForProject(ChartProject?, Arrangement?)` converts the OPEN PROJECT's
    own authored `ChartProject.ToneDefinitions` (what the Tone Designer writes
    and the rscp `dcToneDefinitions` node carries) instead of a package's tone
    JSON; `BeginPreview`/`EndPreview` then drive through the identical
    snapshot → worker → scanner → `DcRigApplier` path. The reason it lives here
    rather than in its own coordinator is the SNAPSHOT: there is exactly one
    "the charter's manual chain" and one `_runGeneration`, so two owners would
    each snapshot it and the second would save the FIRST one's tone rig as if
    the player had built it. Four rules fell out of it:
    - **`RsToneDriver` names the single owner** (`None`/`PlayModeRun`/
      `EditorPreview`/`SongToneEdit`) and a Play Mode run WINS. `SongToneEdit`
      is the Get Ready screen's song-tone edit (`BeginToneEdit`/`EndToneEdit`,
      `RsTonePlaybackCoordinator.ToneEdit.cs`): one package tone held on the rig
      through the same `StartDriving` snapshot with a schedule that never
      switches; a run started mid-edit takes the rig over from it exactly as it
      does from the preview. `BeginRun` used to open with
      `if (IsDriving) return true`, which on a live preview reported success,
      built no schedule of its own and played the whole song on the PREVIEW's
      tone timeline; it now stops the preview first. Symmetrically `EndRun`
      only ends a RUN — Play Mode's teardown paths call it unconditionally and
      an editor preview is not theirs to stop.
    - **The two tone sources keep SEPARATE preset maps**, with `_activePresets`
      pointing at whichever is driving. Sharing one map means a
      `PrepareForSong` while the preview drives swaps it under the live scanner
      and every later apply silently logs "no converted preset" and no-ops.
      The split is by SOURCE (package vs project), not by driver: a Play Mode
      run of an OWN chart converts the project's bodies and therefore drives
      from the project maps, which is what `_preparedFromProject` records for
      `BeginRun` to read.
    - **A run falls back to the OPEN PROJECT's authored tones when there is no
      package.** `PrepareForSong*(psarcPath: null, …)` used to mean "no tones"
      full stop, so a chart authored in this app — tones dialled in the Tone
      Designer, tone changes placed on its timeline — played completely dry in
      Play Mode while the chart editor's preview drove those exact bodies one
      tab away. A package song still converts from the PACKAGE: the project's
      bodies are a fallback, never an override. Consequently only a song with
      no tone bodies from EITHER source short-circuits `PrepareForSongAsync`
      inline — an own chart with authored tones has real conversion work and
      takes the background path, and `PlayModeViewModel` shows the
      'Tones loading…' badge for it too.
      - **"Own chart" is not only a chart authored here — the commonest case is
        an IMPORTED CDLC that was saved.** Load a psarc, edit it, save it: the
        `.rscp` carries the package's tone bodies (`ImportToneDefinitions` →
        `dcToneDefinitions`) but has no psarc PATH, so playing it takes the
        project branch. Before 0.44.0 that read as "my CDLC lost its tones and
        its auto tone switches", which is what the owner reported — the file was
        never damaged, only the playback source was wrong. Pinned end to end
        (import → `.rscp` → reload → run → both switches) by the plugin's
        edited-tones playback test (#889), over the
        committed REAL RS tone bodies rather than a synthetic gear stub.
      - **The `PrepareForSong` doc comment still said `psarcPath` null ⇒
        `HasTones=false` for a release after the behaviour changed**, and that
        one stale line got the fixed bug re-diagnosed as a live one. If you
        change which source wins, change that comment in the same edit.
    - **Availability is answered honestly and printed.** No project, no native
      rig (`NullDspChainService` — a build without the `dca_engine_dsp_*` entry
      points hosts no rig at all) or no tone the arrangement references ⇒ the
      toolbar toggle is DISABLED and its tooltip is the reason
      (`Toolbar_TonePreview_No{Project,Rig,Tones}`). The couch co-op
      `CoopRigApplier` rule applied before the fact.
    - **A run's schedule is fixed; the EDITOR's is the thing being authored, so
      it re-arms IN PLACE — never by stop/start.**
      `RefreshPreviewSchedule(bool reapplyCurrentTone = false)` rebuilds only
      the `RsToneScanner` from the arrangement as it stands now and queues an
      apply only when the tone under the playhead actually differs from
      `_activeToneKey`. Going through `EndPreview` + `BeginPreview` to pick an
      edit up restores the charter's manual chain and rebuilds the tone rig on
      top of it for EVERY gesture — audible, and exactly the teardown
      `DcRigApplier` exists to avoid. `reapplyCurrentTone` covers what the key
      comparison cannot see (a tone BODY rewritten under an unchanged NAME) and
      also re-points `_activePresets` at the maps the latest
      `PrepareForProject` published — without that re-point the apply worker
      keeps the dictionary captured at `BeginPreview` and an edited tone
      converts perfectly and reaches nothing. **The apply lands on the worker,
      not inline**: this fires from editing gestures, and a native chain diff on
      the UI thread is the `_applyGate` rule.
    - **The editor half is `Services/Audio/Coordination/ChartTonePreviewController`** (+ the
      `IGuitarMonitorMixer` contract `TransportViewModel` implements),
      constructed lazily by `ToneRigViewModel.TonePreview` (exposed as
      `MainWindowViewModel.ToneRig`; the coordinator arrives as a `Func<T>`
      factory) and driven by `ToneRig.ToggleTonePreviewCommand` from `ToolbarView`'s quick cluster. Three
      signals re-arm it, and they cost different things: `ToneDefinitions`
      changed ⇒ `Refresh()` (re-convert the bodies); `ToneChanges` changed ⇒
      `RefreshSchedule()` (only the ms timeline moved); and
      **`IHistoryService.HistoryChanged` ⇒ `RefreshSchedule()`, which is the
      ONLY signal a MOVED marker raises at all** — a marker's position is set on
      the object, so no collection event fires, and a dragged tone change (or a
      tempo edit under one) used to need the toggle cycled before it was heard.
      Drop the history service from the ctor and that gesture goes silent again;
      `TonePreviewReachabilityTests` lints the ctor call for it. `Refresh()` also
      switches off when `_tones.IsPreviewing` has gone false under it (a Play
      Mode run took the rig) — a toggle left lit over a rig it no longer drives
      is the same lie as a live-looking disabled one. Leaving the chart editor
      tab turns it off. **Disposing does NOT turn it off — it ABANDONS it**
      (`RsTonePlaybackCoordinator.AbandonPreview`, #116): the controller is
      disposed only at shutdown, AFTER the audio engine, and `TurnOff`'s restore
      rebuilt the charter's board into detached chains (two WARN lines per
      module, 76 % of a real log's warnings). Pinned by `UI/TonePreviewReachabilityTests` (verbs, not
      just types), `Services/Audio/Coordination/ChartTonePreviewControllerTests` and the
      preview cases in `Services/Audio/Coordination/RsTonePlaybackCoordinatorTests`.
    - **The applied tone has a MANAGED read-out too (0.57).** `ActiveToneChanged`
      and `CurrentToneName` had zero consumers in the whole UI project: the only
      reader of the applied tone was `GameplayToneHud` → the 3D highway HUD,
      which in the editor needs the 3D preview panel open AND the Play Mode
      `ShowGameplayToneHud` setting on. So charting in the 2D timeline with the
      preview running, a switch that fired and a switch that silently found no
      preset looked identical. `StatusBarViewModel.ActiveToneName` /
      `ActiveToneApproximated` back a chip on the bottom rail.
      **`DrivingStopped` exists for that chip** — a read-out fed only by applies
      freezes on the last tone it saw and goes on claiming a rig nobody drives;
      an apply of `""` is not a thing, so the stop is its own event, raised from
      `StopDriving` for BOTH drivers and swallowed-and-logged (a read-out may
      never take a teardown down). `SetActiveTone` posts through `IUiDispatcher`
      because `ActiveToneChanged` fires on the apply WORKER. It is wired in
      `ToneRigViewModel.BuildTonePreview`, deliberately NOT by injecting the
      coordinator into `StatusBarViewModel` — that view model is built during
      shell DI, so taking the coordinator there would construct
      the whole DSP chain during shell DI, on the startup path, for a chip that
      means nothing until something drives the rig.
    - **The tone BODIES for a library song arrive at LOAD**, not by hand:
      the plugin's package source fills
      `ChartProject.ToneDefinitions` from the loaded arrangements' own psarc
      manifests. Before that the preview was unavailable on every psarc-opened
      song — the slots and tone changes imported, the bodies did not, and Play
      Mode reading the package directly made the gap invisible from that side.
- `Styles/` — the theme, split into `Resources` (tokens) → `ControlThemes` → `Typography` → `Buttons` → `Inputs` → `Containers` → `Chrome`, aggregated by `Theme.axaml`. Colours/brushes/icons are `DynamicResource`; the fixed size tokens are `StaticResource` (`UI/FixedTokenReferenceTests`, see `DotCharter.UI/AGENTS.md`).
  Since 0.15.5 A1 the tokens include a spacing scale (`Space*` doubles for
  `StackPanel.Spacing`, directional `Gap*` thicknesses for margins, semantic
  `PaddingDialog/ScreenInset/PaddingOverlayCard/PaddingCard[Lg]/PaddingRow/PaddingChip`)
  and the full `FontSize*` type scale (`Micro`…`Countdown`) — **no literal
  `FontSize="NN"` in axaml**; dialog shells inherit padding from
  `Border.DialogCard`; dense in-row buttons use `Classes="Subtle|Primary Compact"`.
  - **Idle Background of any control that fades a Background in on hover must be
    `{DynamicResource HoverFadeStart}`, NEVER `Transparent`.** Avalonia's
    `Transparent` keyword is `#00FFFFFF` — transparent WHITE. A
    `BrushTransition Property="Background"` interpolates all four ARGB channels
    linearly, so fading from `Transparent` to an opaque dark hover brush raises
    alpha while RGB is still near-white → a bright-white wash for the first
    frames before RGB settles ("first hover flashes white then normal", owner
    report, v0.32.x). `HoverFadeStart` (transparent BLACK, `#00000000`, in
    `Resources.axaml`) keeps RGB dark the whole fade — identical at rest
    (alpha 0), hit-testable like any non-null brush. Applied to every
    control-level `Background="Transparent"` setter in the theme (WindowBtn /
    ToolbarBtn / ToolbarToggleBtn / IconBtn / ResetBtn + the Buttons / Chrome /
    Containers / Inputs equivalents).
- `Theming/` — runtime palette switching (`ChartTheme`). **THE APP HAS ONE
  THEME (0.62): `ThemePresets.Default`, "Graphite", complete at all 86 tokens.**
  The five sibling presets, `GetById`, and the community theme packs
  (`CommunityThemePacks` + `Services/Theming/CommunityThemePackFiles`,
  `<ConfigRoot>/themes/*.json`) are DELETED along with the Appearance tab's
  picker and `AppConfig.ThemePresetId` — the owner's call, "one theme for now
  for easy of coding". Do not reintroduce a preset without one: every surface is
  designed against known values now rather than against a range.
  `StringColorPresets` STAYS and is a different thing: colour-blind-safe
  overrides for the seven `String*` tokens, persisted as
  `AppConfig.StringColorPresetId` and merged over `ThemeColorOverrides` when the
  theme is applied, so both the 2D timeline and the 3D fretboard repaint. The
  per-token user overrides stay too — customising a colour is not choosing a
  theme.
- **"Which colours has the user changed away from the preset?" has ONE answer:
  `Theming/ThemeOverrides` (0.61).** It was computed in two places that
  disagreed. `SaveAppearance` diffed the live colours against the preset;
  `ApplySelectedTheme` — which the Appearance tab's colour-blind string-preset
  ComboBox ran on every change — re-applied the preset with ONLY the seven
  `String*` overrides, i.e. a full reset of the Customize grid. The Save that
  followed then diffed the now-identical colours and persisted an EMPTY
  override table, so the customization was gone from disk too. Play Mode's twin
  of the same row always merged the persisted overrides first, which is why one
  surface preserved the palette and the other destroyed it.
  `ForStringColorPreset` drops the `String*` tokens from the free-form set on
  purpose: bake the applied Okabe-Ito colours in and "Default (theme)" could
  never restore the theme's own strings again. `ApplySelectedTheme` is still the
  RESET path (the preset list and "Reset to preset"). Guard:
  `UI/Theming/ThemeOverridesTests`.
- **A custom-drawn control may not COPY a themed colour into a field.**
  `ChartTheme.Apply` repaints its shared brush INSTANCES in place, so a control
  drawing with `ChartTheme.GoldBrush` / `.StringBrushes` follows the palette for
  free; `new SolidColorBrush(ChartTheme.GoldColor)` in a `static readonly` field
  captures it once, at type init, and never changes again. Both offenders looked
  correct on a cold start (the types are first touched after the startup theme
  is applied) and then ignored every later change with no exception and no log
  line: `MinimapOverviewControl`'s seven string brushes, phrase marker and
  waveform stroke, and `FretAnchorLayer`'s six amber channel bytes off
  `ChartTheme.AnchorColor`. Guard: `UI/Theming/ChartThemeStaticPaletteTests` —
  reference equality for the minimap's brushes, plus a lint over `Controls/**`
  requiring anything seeded from a colour `Apply` ASSIGNS (the list is derived
  from `Apply`'s own source) to be repainted by that file's `OnThemeChanged`.
