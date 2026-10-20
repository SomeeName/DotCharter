# The editor workspace — gutter, ribbon, minimap, dock chrome (UI deep reference)

Extracted verbatim from `DotCharter.UI/AGENTS.md`. **Read this before touching
`Views/Editor/EditorWorkspaceStyles.axaml`, `Controls/TimelineGutterControl`,
the `ROWS` ribbon toggles, `RibbonLayout`, `MeasurePillPolicy`, the minimap
strip or the Dock.Avalonia pane chrome.**

Related: the rails around it (menu bar / toolbar / status rail / popovers) are
[shell-chrome.md](shell-chrome.md); the renderer itself is
[rendering-perf/timeline-2d.md](rendering-perf/timeline-2d.md) and
`DotCharter.Rendering/AGENTS.md`.

## `Views/Editor/EditorWorkspaceStyles.axaml` — the FOURTH converted grammar

- **…and in 0.51 the WORKSPACE between those rails followed, through a
  FOURTH grammar: `Views/Editor/EditorWorkspaceStyles.axaml`.** It carries the
  timeline's left GUTTER frame (`Border.TrackRail`, whose child is the
  custom-drawn `TimelineGutterControl` — hence its `FirePadNone`) and the
  `ROWS` strip above it (`Border.RowsStrip` + `ToggleButton.RibbonRow`), the
  fixed-pane header strip (`Border.PaneHeader`/`PaneTitle`/`PaneAction`, moved
  out of `Styles/Containers.axaml`), `Border.MinimapFrame` + its
  `Border.SongMapCaption`/`TextBlock.SongMapFacts` cell, and the 3D
  preview's editor bar (`Border.HudBar` + `HudLabel`/`HudStat`/`HudAction`,
  moved out of `Preview3DView`'s own `UserControl.Styles`). It is held to the
  same lints as the other three grammars — add it to
  `UI/FireStyleClassTests`' three Theory lists if you touch them.
  - **It is included by FOUR views, and which ones is not a style choice.**
    `MainWindow.axaml` (on the timeline column's `DockPanel` and on the 3D
    preview's `PaneHeader` Border — the rail and the minimap are declared there
    and have no view of their own), plus `Preview3DView`,
    `PhraseCatalogView` and `StemMixerView` INDIVIDUALLY, because each of
    those leaves the main window: a floated pane is re-hosted in a
    Dock.Avalonia `HostWindow` and a popped-out preview in a plain `Window`,
    neither of which sees a window-scoped style. That is a fault nothing shows
    until someone drags a pane out;
    `UI/EditorWorkspaceChromeTests.EveryViewWearingAWorkspaceClass_AlsoIncludesTheGrammar`
    is the guard.
  - **Never at window scope (#412).** A style attaches to every control its
    selector's TYPE matches in the declaring scope, worn or not: at
    `Window.Styles` this grammar put ~54,000 value frames on Play Mode, the song
    browser, Settings and every prewarmed dialog. MainWindow has no
    `Window.Styles` at all, and its two includes sit on elements that do not
    contain `Preview3DSlot` or the dock (whose panes include their own copy — a
    control under both would get every rule twice).
    `UI/StyleIncludeScopeTests.MainWindow_DeclaresNoStylesAtWindowScope` and
    `Perf/StyleIncludeScopeUiPerfSmokeTests` are the guards. Keep every selector
    a CLASS selector anyway: a pane's own include covers that whole pane.
  - **The DOCK pane chrome (the `Inspektor` header, its float/pin/close
    buttons, the tab strip, the splitter) is a RESOURCE-KEY bridge in
    `Styles/Dock.axaml`, for the same reason the dropdowns are.**
    `DockFluentTheme`'s templates read `{DynamicResource Dock*Brush}` from
    inside the template, so a `dock|…/template/` style rule is a silent no-op.
    Its ~37 literals are pinned against the Fire tokens they restate by
    `UI/EditorWorkspaceChromeTests.EveryDockChromeKey_RestatesItsFireToken`.
  - **The 2D timeline's five CHROME colours moved too** (`ChartTopMarginBg`,
    `ChartBottomMarginBg`, `ChartBorder`, `ChartMinimapBorder`,
    `ChartMeasure`) — still `ThemeTokens`, deliberately NOT Fire keys, and
    `App.axaml` + `ThemePresets.DarkSlate` must carry the same five values.
    The renderer's DATA colours are untouched.

## The timeline LEFT GUTTER (`Controls/TimelineGutterControl`)

- **The timeline has a LEFT GUTTER that labels its lanes
  (`Controls/TimelineGutterControl`, 0.51)** — a caption per ribbon row, a row
  per string (identity tick + NOTE NAME + string number, `LOW`/`HIGH` on the
  outer two), then `SHAPES` / `FHP` / `TIME` over the three bottom bands, then
  the `SONG MAP` cell over the minimap. Built from
  the owner's `Chart Timeline.reference.png` and, since the rebuild, from the
  SOURCE at `Chart Editor.dc.html`. Rules it carries:
  - **The three bottom captions (0.59) sit on the SAME numbers
    `Input/TimelineZones` routes clicks by**, via the pure
    `TimelineGutterControl.BottomBandCaptionSlots` — pinned against the zone
    predicates by `UI/TimelineGutterBandCaptionTests`. The unlabeled FHP band
    is what the live audit read as "clicking the ruler created a yellow
    region"; a squeezed margin hides the band captions rather than mislabel
    them.
  - **`TimelineGrid`'s rows may not be addressed BY INDEX from code-behind, and
    that is the bug that got the first attempt reverted.**
    `MainWindowLayoutController.UpdateStackedRowSizing` collapses the stacked
    row to 0 px when the stacked panel is hidden — the DEFAULT — and it used to
    name that row `RowDefinitions[1]`. Adding the `ROWS` strip as an `Auto` row
    at the top moved the RENDERER to index 1, and the first call (from `Bind`,
    before any frame) gave the chart surface zero height: no lanes, no ruler, no
    playhead, no exception, no warning, and a log line still reporting the
    composed-Vulkan timeline as active — because it was, correctly initialised
    and drawing into nothing. **The default gate was green; a grid row height is
    invisible to every headless test in this repo.** The row is resolved through
    `Grid.GetRow(StackedTracksHost)` now and pinned by `UI/StackedRowSizingTests`.
  - **It is a SIBLING of `ChartRenderer` in column 0, never part of it.**
    Drawing it inside the renderer would put a fixed offset on the renderer's x
    axis, and every hit-tester, the marquee, the minimap drag and
    `TimelineViewAnchorEffectiveMs` map through that axis.
  - **…which means the vertical layout is now needed twice, so it is ONE
    formula: `Editing/Tracks/TimelineGutterLayout`.** `ChartRenderer` builds it
    per frame and PUBLISHES it (`GutterLayout` + `GutterLayoutChanged`); the
    gutter draws against it. It cannot subscribe to the inputs instead — the
    ribbon's real height depends on chart CONTENT (empty rows auto-collapse),
    which no property change announces. Stacked override rows do not publish.
    Pinned by `UI/TimelineGutterLayoutTests`, which sweeps the gutter's
    `LaneTopFor` against `ChartRenderContext.LaneTopFor` over every ribbon
    combination × both flips × four string counts × three heights.
  - **The publish is gated on the layout SHAPE *and* the arrangement's TUNING —
    `Editing/Tracks/GutterPublishGate` — and the tuning half is what v0.44.0
    shipped without.** Gating on the shape alone is correct for the geometry and
    silently wrong for the NAMES: swapping any six-string chart for any other
    moves no lane, so the event never fired, the gutter never invalidated, and it
    kept printing the previous chart's string names. The owner reported it as a
    Drop C# CDLC that "loads showing E A D G B E" — those six letters were the
    default E-standard project the app had started on. The gutter's own
    `TuningStampFor` comparison is the SECOND gate, not the first, and it also
    had to grow `TuningPitches` (it compared the note letters only, and
    `TuningCheckTargets` reads the offsets first). `UI/GutterPublishGateTests`.
  - **The lane names come from `TuningCheckTargets.TryBuild`, never `Build`.**
    `Build` assumes standard tuning when a chart states no offsets and names a
    tuning the table cannot decode — deliberate for the guided tuner, which has
    to aim somewhere and throws on an empty target table. A COLUMN that prints a
    note must say `?` instead.
  - **The string colours are `ChartTheme.BaseStringColors`, NOT the six fixed
    `FireString*` identities** — the timeline's palette is theme-switchable and
    has colour-blind presets, and a gutter on the fixed set would disagree with
    the notes beside it. The NAMES come from `TuningCheckTargets` (the tuner's
    own table) because a drop tuning renames every string; with no arrangement
    the lanes print `?` rather than a plausible standard tuning.
  - **A string's colour is its IDENTITY, not its lane (#745).** Every surface
    that colours by string — gutter, lanes, notes, song map, phrase preview, chord
    box/diagram, inspector chips, the 3D highway — indexes the palette through
    `DotCharter.Core/Utilities/StringColorSlots.For(index, count)` (via
    `ChartTheme.StringSlot`/`StringBrushFor`, `ChartRenderContext.StringColorFor`,
    `FretboardConstants.StringColorFor`): a 7-string's low E stays red and its low
    B is the `StringExtra` grey (#C8C8C8); an 8-string's low F# is a darker grey
    derived from it. `UI/Theming/StringColorIdentityTests` lints for a raw
    `palette[i % palette.Length]`.
  - The five ribbon toggles left the track rail so the left column could label
    the lanes instead. They spent one release as a horizontal `ROWS` pill strip
    above the timeline and are now **View-menu items** — a whole horizontal band
    of the one screen where vertical space is the product, to show five toggles
    you set once. What is pinned is not where they sit but WHAT they write:
    every one binds `ChartEditor.ActiveRibbon.*`
    (`UI/EditorWorkspaceChromeTests.TheRowToggles_DriveTheRealRibbonFlags_FromTheViewMenu`,
    which also fails if the strip comes back). `Border.RowsStrip` /
    `ToggleButton.RibbonRow` still exist in `EditorWorkspaceStyles.axaml`.
  - **The gutter column, the `SONG MAP` caption cell and `StackedTracksView`'s
    per-row header are one number written THREE times (118, the design's own
    width).** For the stacked row that is correctness — change one and its chart
    shows a different slice of the song at the same screen x. For the song map it
    is the design's single unbroken left edge.
    `TheGutterColumn_TheSongMapCaption_AndTheStackedRowHeader_AreTheSameWidth`
    fails instead.
  - **The minimap strip is `SONG MAP` + `4:12 · 84 BARS` (`Border.SongMapCaption`
    + `TextBlock.SongMapFacts`, 52 px), and its SEEK HANDLERS live on the map's
    OWN border, never on the frame.** `MinimapDragController` maps a seek as
    `e.GetPosition(sender).X / sender.Bounds.Width` across the whole song, so on
    the frame the caption's 118 px joins that denominator: every seek lands early
    by 118/W of the song and the viewport rectangle draws 118 px right of the
    click. Same class of fault as drawing the gutter inside the renderer.
    `TheMinimapSeekHandlers_AreOnTheMapNotOnTheCaptionedStrip` pins it, and the
    seek maps against **`ProjectState.EffectiveTimelineLengthMs`, never the raw
    audio length** — a chart-only project (GP import, empty-project flow) has
    `TotalAudioLengthMs == 0`, and every navigation surface that gated or
    clamped on the audio length (this seek, `ExecuteSeek`, wheel/middle-drag
    pan, Home/End) was dead there until 0.59. The effective length is the audio
    when attached, else the tempo grid's end (O(1), read per wheel notch);
    `Services/State/EffectiveTimelineLengthTests` pins the fallback and
    source-lints the navigation call sites. The
    caption text is the pure `ViewModels/Editor/SongMapCaption` (no audio ⇒ bars
    only, no beat map ⇒ duration only, neither ⇒ nothing — never `0:00 · 0 BARS`).
    The bar count is re-announced on project / arrangement / beat-map /
    audio-length change and deliberately NOT per beat.
  - **Use marks (#485): ONE path for every set of chords and hand shapes the map
    marks** — `Controls/SongMapMarks.Collect` (a chord is a tick from the bottom
    edge, a hand shape a bar spanning its length) into lists the control reuses
    every frame. Two sets go through it: the SELECTED chords and hand shapes
    (white — what the Chord shapes list's "select uses" lights; the selection
    subscription covers `SelectedHandShapes` since #485) and the chord panel's
    bulk-edit preview, `ChartEditorViewModel.UseHighlight` on the level on screen
    (the `EditTarget` colour plus a faint full-height line). The AI chart pass
    review (#506) publishes into that same highlight; its hand positions are
    ticks hanging from the TOP edge (`SongMapMarks.AppendAnchors`). Both changes repaint
    through the visibility-gated `RequestRepaint` funnel, like a selection change —
    never the throttled playhead path. A press, move or release within
    `SongMapMarks.SnapPx` of a preview mark seeks to the mark itself
    (`MinimapDragController.SnapToMark`; every seek snaps, or the release that
    flushes a dropped move would undo the press's snap). Pinned by
    `UI/Controls/SongMapMarksTests` and `Perf/MinimapUseMarksUiPerfSmokeTests`.
  - **Open hand-shape suggestions (#639) are a faint band each** — the hand-shape
    bar colour at low alpha from under the section band to the bottom edge, with a
    stronger bar along the bottom, drawn UNDER the selection and preview marks.
    `MinimapOverviewControl.DrawSuggestions` holds its own `HandShapeSuggestionLatch`
    over the lane's inputs (active level, View ▸ Show suggested hand shapes, history
    revision), so the detector reruns only on an edit; no new repaint path — an edit
    and the View toggle already reach the map through `RequestRendererRedraw`.
    `UI/Controls/SongMapSuggestionMarksTests`, `Perf/MinimapUseMarksUiPerfSmokeTests`.
  - **…and since 0.61 the strip is DRAWN on that same effective length, which
    is the half this section used to leave out.** `MinimapOverviewControl`
    derived its own chart-only fallback — a cached max over the beat grid,
    every note tail, every chord tail and every event — and that number is
    strictly LARGER than the effective length, which nothing else in the app
    agrees with. A strip's SCALE *is* the mapping its clicks are inverted
    through, so the map was drawn on one length and clicked on another: on a
    chart-only project every click landed proportionally early (a chart whose
    last note rings 5 % past its final beat put every click 5 % of the song
    before the note under the pointer), the drawn viewport rectangle sat where
    the click was not, and the right end of the strip was unreachable because
    `ExecuteSeek` clamps to the effective length anyway. It reads
    `_projectState.EffectiveTimelineLengthMs` now (the bound `TotalTimeMs` is
    only the detached-control fallback), and the `EffectiveTimelineLengthTests`
    source lint covers the control as well as the three navigation sites — with
    a second case asserting it grows no private chart-end back, which the
    "names the effective length somewhere" lint would not catch. **The cost is
    deliberate:** a sound whose tail rings past the last beat is now clipped off
    the right of the map — a region the transport cannot be seeked into.
  - **The empty-timeline overlay is a LAUNCH CARD, and its visibility is
    content-aware (0.59).** `MainWindowViewModel.IsEditorLaunchCardVisible` is
    "no chart content AND no audio AND no project path" — the whole rule is the
    static `ShowsLaunchCard`, and its content half the static
    `ProjectHasChartContent` (notes/chords on any level, or vocal phrases), both
    pinned by `UI/ViewModels/Shell/EditorLaunchCardTests`. The PATH term landed
    2026-09-08: File ▸ New Project has neither audio nor notes, so the content
    rule alone left the card over the project the user had just created;
    `CurrentProjectPath` is set by every create/open/import and cleared to ""
    only when the untitled default project is installed, and the shell
    re-raises the property on it. Its predecessor
    (`IsEditorEmptyHintVisible`) tracked `TotalAudioLengthMs` alone, so after a
    GP import the "open a project or drop an audio file here" hint sat over a
    1005-note chart. The card is HIT-TESTABLE (New / Open / Attach Audio + the
    Open Recent rows), unlike the old hint — it may only show when the timeline
    under it is genuinely empty, and a chart without audio shows the chart
    covered by nothing.
  - **`RibbonLayout` is the ribbon's WHOLE vertical formula, and every number in
    it comes off `Chart Timeline.reference.png`** (0.51): a **24 px lane pitch**
    holding a **19 px bar** (`BarHeight`, centred by `BarTopIn`/`BarBottomIn`),
    2 px top inset, **no** gap before the measure band, a 20 px band holding a
    17 px pill (`PillTopIn`), and a 3 px separator. Four lanes cost **97 px**,
    five cost 121.
    - **A bar's height lives beside the pitch it fits inside, on purpose.** It
      was `ChartEditorConstants.RenderMetrics.BadgeHeight` (20, in a 30 px row),
      i.e. 5 px of dead space above and below every section, phrase, tone and
      lyric on the surface — the owner's "the rows cost more height than they
      use", 132 px against the design's 96 for the same four lanes. Two
      constants in two files is how that ratio comes back.
      `UI/TimelineGutterLayoutTests` asserts the RATIO, a per-side padding cap
      and the ribbon's TOTAL, never the literals.
    - **Anything a lane draws BELOW its bar has 2.5 px to live in** — that is
      the whole gap now. The active section chip wore a 2 px underline there
      until 2026-09-24; the owner read it as a stray purple line, so the
      brighter fill is the only mark (`TimelineGutterLayoutTests`).
    - **A vocal phrase is ONE REGION in the lyrics lane** (`EventLayer.DrawPhraseRegion`,
      2026-09-23): a rounded band of the lyric slate behind its syllables, with a
      short accent at its start. It replaced "[" / "]" bracket ticks — the owner
      found them dated, and the closing tick sat at the phrase's end TIME, inside or
      short of a last pill widened to fit its text. The band spans the pills ACTUALLY
      drawn (`LyricPillLayout.PhraseRegion`), a phrase's last pill is laid out
      against the next phrase's region edge (`LastPillLimit`) so back-to-back phrases
      keep their pills inside their own bands, and it stays inside the lyric row: a
      full-height line means a SECTION or a guitar PHRASE. The stop before the next
      phrase's band WINS over the band's 8 px minimum (2026-09-25): at low zoom a band
      narrows, down to none, rather than merging with its neighbour. Pinned headlessly by
      `Rendering/EventLayerLyricPhraseMarkerTests` (drives the real layer into a
      plain `TimelineBatcher`, no GPU).
    - **A syllable pill stops before the next syllable** (`LyricPillLayout.Layout`,
      2026-09-23). Each pill used to widen to fit its text with no limit, so short
      syllables overlapped and the next pill covered the previous text ("shi ma u"
      read "s ma u"). A cramped pill tightens its inset; one too narrow for its text
      leaves the text out rather than printing it under its neighbour. Closer than
      4 + 2 px it stays a textless 4 px mark and may touch the next pill — a visible
      syllable beats strict separation (pinned, not excused, 2026-09-25). **The click
      target stops where the pill is drawn**: `EventInteractionHitTester.HitTestLyric`
      applies the same next-syllable / `LastPillLimit` cap, so the blank row past an
      overlong imported syllable no longer selects or right-click-DELETES it.
      `UI/Controls/LyricPillLayoutTests`, `EventInteractionHitTesterTests`.
    - **Clicking a lyric REPLACES the selection** (`EventInteractionTool.SelectLyricAlone`,
      2026-09-25), as the note lane's click does — a note left selected used to take the
      wheel (`TimelineWheelAdjuster` tries selected sounds before the lyric), so "lengthen
      the lyric" lengthened the note. The reverse holds in `SelectionService` itself:
      selecting any note-lane content (note, chord, hand shape, anchor — by click, marquee,
      select-all or menu target) drops the lyric. **Delete removes a lyric that is the whole selection**,
      from either focus area (`GlobalEditingCoordinator.BuildDeleteActions`, one undo step);
      with other content selected the focus-area rule decides and the lyric stays.
      `UI/Input/Tools/LyricClickSelectionTests`, `DeleteSelectedRelatedTests`.
  - **The measure band prints its BPM on EVERY measure with room for it** (0.51,
    reversing the 0.15 change-only rule): `MeasurePillPolicy.ShowBpm` takes the
    measure's on-screen width and falls back to change-only below
    `MinBpmMeasureWidthPx`, which is what stops a zoomed-out band becoming
    overlapping text.
    - **The time SIGNATURE follows a DIFFERENT rule and that is deliberate**: a
      change always prints, and the pinned left-edge context pill prints the
      meter only when the passage is not in the meter the chart OPENED in
      (`ShowTimeSig`, O(1) against `Beats[0]`). Under the old
      `changed || isFirstVisible` — the BPM's rule — every 4/4 chart in the
      library printed "4/4" forever on the one pill a reader looks at most.
    - **Every sticky label is PUSHED OUT by the next label of its row**
      (`Controls/Layers/Helpers/StickyLabel`, 2026-09-23): the measure pill, the
      section / phrase / tone chips and the FHP fret chip, sticky-header style.
      The measure pill's first guard was a flat 12 px against a ~120 px pill and
      drew it ON the first in-flow pill (two numbers, two tempos, two borders in
      one apparent chip); its second stood it down instead, which made it vanish
      whenever a measure was narrower than a pill. The event chips had no guard
      at all and let the next chip slide under them. `EventLayer` also finds its
      pins BEFORE drawing in-flow chips, so an event whose chip is still partly on
      screen at the edge is drawn once, as the pinned chip, not twice a few
      pixels apart; and a lyric syllable cut off at the edge keeps its text on the
      visible part (`StickyLabel.TextX`). Because the in-flow chip is suppressed, the
      PINNED chip carries the event's selected / hovered look (2026-09-25; it was
      drawn plain, so a selection vanished when its chip pinned). Pinned by
      `UI/Controls/StickyLabelTests` and, on the real layer,
      `Rendering/EventLayerPinnedLabelHitTests`.
    - **A bend's step label never covers its note head** and two labels of one
      note never overlap — the bigger step stays
      (`Controls/Layers/Helpers/BendLabelPlacement`, `UI/Controls/BendLabelPlacementTests`).
      A new label is resolved against EVERY label of the note still standing
      (`ResolveAgainst`, 2026-09-25), not just the last: a wider one pulled left by
      the sustain clamp could replace the last and land on the one before it.
      Labels draw after the heads, so a label at its first bend point hid the
      fret number ("14 ↗Full" read "1 ↗Full").
    - **Labels that hang right of a note keep off the NEXT head on its string**
      (`Controls/Layers/Helpers/NextHeadScan`: one backward pass per frame in
      `NoteLayer` over single notes and chords together — double-stops are chords
      in the snapshot). A slide's destination box is left out when that next head
      repeats the slide's fret right where the box would go (`SlideLabelPolicy`; a
      slide into a DIFFERENT fret keeps its box), and a bend label that cannot end
      before it is not drawn (`BendLabelPlacement.FitsBeforeNextHead`; the curve
      still shows the bend). `UI/Controls/SlideLabelPolicyTests`,
      `UI/Controls/BendLabelPlacementTests`.
  - **The playhead's TIME chip is CENTRED on the line** (`PlayheadLayer.BadgeX`,
    pure + clamped into the viewport), not hung off its right edge — offset, it
    reads as the time a moment ahead of the playhead.
    It sits IN the time ruler (`PlayheadLayer.BadgeY`, from
    `TimelineZones.TimeRulerHeightPx`): a fixed 20 px below the lanes put it in
    the FHP band once SHAPES and FHP grew above the ruler (2026-09-23).
  - **A section is one colour by NAME in both views** (`Controls/Layers/Helpers/SectionColors`,
    2026-09-23): the song map's band and the timeline's SECTIONS chips and drop
    lines. The chips were all one blue while the map coloured by name. Chip fills
    are darkened so their light text stays above 6:1 on every palette colour
    (`UI/Controls/SectionColorsTests` computes it).
  - **Bend curves are a light tint of their string**, not white
    (`BendLabelPlacement.CurveColor`): white under a slight bend read as a
    selection underline.
  - **A muted head is framed by four X ticks with a clear centre**
    (`HighwayNoteHeadRenderer.MuteTickSpan`, 2026-09-23), its number white like
    every other head. The X used to cross the head through the number, with a
    grey plate and a dark number patched on top: the lowest-contrast number on
    the timeline. `UI/Controls/MuteGlyphTests` measures the ticks' clearance.

## The stem mixer pane (`ViewModels/Editor/StemMixer*`)

- **Solo is a TOGGLE both ways, and a solo-scoped mute never reaches the
  project** (fixed 2026-09-04, `UI/ViewModels/Editor/StemMixerSoloReleaseTests`).
  The mixer silences the master "Original" strip while any stem is soloed, which
  is right — but it did that through `StemTrackViewModel.SetMutedSilently`, which
  also calls `PersistOriginalMixMuted`, and `OnTrackSoloChanged` did nothing at
  all on release (`if (!nowSoloed) return;`). So one click on Solo — the owner's
  most-clicked button — turned the original mix off PERMANENTLY and wrote
  `ChartProject.OriginalMixMuted = true` into the `.rscp`, where it survived a
  reload. The engine's own solo handling is transient BY DESIGN
  (`JuceAudioPlaybackService.ApplyGain` silences the master on
  `_masterMixMuted || anySolo`, and its comment says the toggle comes back as
  soon as the solo is released); the UI was the half that made it stick.
  `SetMutedForSolo` is `SetMutedSilently` minus the persistence — the same rule,
  and the same reason, as `SetMutedForRun` — and `SyncMasterToSoloState` restores
  the remembered baseline once NO stem is soloed. That baseline is reset on every
  rebuild and every stem clear, so it cannot carry one song's original-mix mute
  onto the next.
- The early return also skipped `RefreshWaveform`, so with **"waveform follows
  mix"** on, releasing a solo left the timeline drawing the soloed stem alone.
  Any change to what is AUDIBLE redraws, in both directions.
