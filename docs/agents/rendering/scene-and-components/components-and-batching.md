# Scene, components, snapshot and assets — `Components/`; `Batching/`

## `Components/`

- `Components/` — the `IRenderable` scene elements (board, highway — incl. the
  0.14.2 beat-pulse edges + anchor-zone sweep, notes, hit
  explosions, enhanced hit feedback (0.14.2 flash/sustain shimmer, stateless,
  Live-Play-suppressed), live-play feedback, depth fog (0.14.2 — MUST stay
  last in the transparent list), text overlay — incl. the 0.14.2 dimmed
  upcoming-anchor numbers, HUD);
  `Components/NoteRendering/` holds note-head / sustain / chord / bend / hand-shape
  / fingering draw helpers. The 0.14.2 effects are all vertex-color/geometry
  work through existing shader modes — no shader (.spv) changes.
  - **Show lights = the stage lights** (`Components/StageLightsRenderer`, NOT a
    scene element: `Fretboard3DEngine` draws it in the screen-space background
    pass, so the whole 3D scene — board, notes — covers it). Six lamp heads
    (mode 8) on the top edge, three per side with the middle clear; soft cones
    (mode 1, every edge faded to zero alpha) down to blurred floor pools below the
    projected horizon; two laser fans from the side edges at horizon height; a
    fog-coloured haze band along the horizon (falling off fast below it),
    notched out over the whole highway — `Data/HighwaySpan` projects the edges
    of every anchor in view, because the lane fill is translucent — plus a
    faint fog wash over the sky. What shows is the pure
    `Data/StageLights.Resolve` (fog → haze + wash; beam → cones, pools and heads,
    BEAMS_OFF → dim grey heads and no cones, plus beams → 35 % brighter; LASERS_ON
    → fans tinted by the beam colour, cyan with beams off; crowd energy →
    brightness; a new event fades in over 150 ms); where and how it moves (sway,
    sweep + scissor, breathing) is `Data/StageLightsShape`; colours are
    `Data/ShowlightPalette`. **Everything runs on SONG time**
    (`snapshot.CurrentTimeMs`) — never the wall clock, so it freezes on pause;
    `StageLightsTests` lints the three files for it. The `EnableShowlightFog`
    toggle gates the haze, `EnableShowlightBeams` the cones, pools and lasers.
    Tests: `StageLightsTests`, `StageLightsShapeTests`, `ShowlightPaletteTests`,
    and `HeadlessStageLightsTests` (one GPU frame per state; asserts heads, haze
    and cones land, the far highway centre stays dark, and a 15-fret anchor's
    lanes show no haze through them).
  - **The editor's show-light chip** (`Components/ShowlightChipComponent`, a
    `HudElements` entry plus a plate the engine draws in an ortho quad pass):
    `Fog: … · Beams: … · Lasers: …` top-left. The text is
    `FrameSnapshot.ShowlightChipText`, composed from the localized
    `ShowlightChipLabels` the UI hands in (`DotCharter.UI/Controls/ShowlightChipLabelSource`,
    keys `Preview3D_ShowlightChip*` / `Preview3D_ShowlightColor_*`); the driver
    passes null while `_gameplayPresentation` (Play Mode), which means no chip.
    The HUD's section/phrase/tone/lyric block moves down while the chip shows.
    `ShowlightChipTests`.
  - **Board-face depth stack (`BoardGeometryComponent`) — keep the opaque
    classes a hair apart or they z-fight.** Everything on the near fretboard
    face sits near z≈0 in the OPAQUE (depth-writing) pass; two classes at the
    SAME z fight for the depth buffer where they overlap (owner report 2026-07:
    fret bars AND charter inlay dots were both at `-0.01`). The deterministic
    stack (more-negative = nearer the camera): capo `-0.05` → fret bars
    `FretBarZ=-0.01` → inlay dots `InlayZ=-0.005` → string plane `0`. Offsets
    are sub-pixel at the ~2.5-unit camera distance (geometry does not move); they
    only break the tie. Do not re-collapse two classes onto one z. The
    transparent pass's landing-target frames (`NoteHeadRenderer.AnticipationZ`,
    `-0.0025`) take a slot between the inlays and the strings: at `0` they
    z-fought the depth-written strings and broke into teeth
    (`AnticipationFrameDepthTests`).
  - **Shader mode is the vertex TYPE argument, not a z.** `QuadBatcher`'s last
    float selects the `highway.frag` branch. Open-string bars were staged with a
    constant named `NoteHeadZOffset = 5` — mode 5, the chord-pane near-fade — so
    they faded out at the strike line; they are `OpenNoteBarVertexType` (mode 1)
    now. Modes 2, 5, 9, 10 and 11 fade toward the board by design (lanes, panes,
    sustains); a note head must not use them.
  - **Active-string highlight (`BoardGeometryComponent.RenderStrings`):** a
    string an upcoming note/chord-note will be played on brightens + thickens as
    that note nears (intensity 0→1 across the `AnchorPreviewLeadMs` window — the
    app's one look-ahead knob). Derived from the string's OWN colour (colour-
    blind presets intact); allocation-free windowed scan; a string with nothing
    upcoming renders byte-for-byte as before.
  - **Note stems (`NoteRendering/NoteStemRenderer`, staged before the head):** a
    single fretted note stands on one stem down to the floor (the fret wires' bottom
    end) plus a floor marker across its fret; an open note's bar gets a stem under
    each end and no marker. Chords get neither (Rocksmith frames them with the chord
    box). `NoteStemTests` pins all three.
  - **Persistent marker-fret numbers (`TextOverlayComponent`):** the inlay-marker
    frets (3,5,7,9,12,15,17,19,21,24) are labelled dimly full-length (active-hue
    token at low alpha, the existing dimmed-preview convention); the active FHP
    span still draws its frets bright and overrides the dim markers it covers.
  - **Chord names, Rocksmith-style (`NoteRendering/ChordNameRenderer` +
    `Components/ChordNameComponent`, 0.41; actually VISIBLE since 0.42.2):**
    the shape's name floats above the neck at the shape's own depth, so it flies
    in with the chord. Four rules:
    **(a) 3D TEXT MAY ONLY BE STAGED FROM THE `UiElements` PASS.** This is the
    rule that cost three releases. The label was drawn from
    `NotesRenderComponent`, which is in `TransparentElements` — and the
    transparent pass runs BEFORE `_textBatcher.SetState(FontAtlas, sceneMvp)`,
    so glyphs were staged against a null atlas (frame 1: `DrawText3D` returns
    immediately) and thereafter against the previous frame's ORTHO HUD matrix.
    Nothing was ever drawn, with no exception and no log line, and three
    successive "fixes" moved the label's x, y and scale — none of which could
    have mattered. If 3D text does not appear, check the scene list FIRST.
    **(b) the x is relative to the ANCHOR governing that shape, never to fret 0**
    (the camera follows the hand, so a fixed world x is off-screen above the
    first position) and never to the anchor under the PLAYHEAD (the hand may be
    somewhere else entirely by the time the chord arrives). It matches
    `CalculateCameraMvp`'s `middleFhpPosition` exactly — the span's own EDGES are
    not reliably on screen;
    **(c) chords win over hand shapes** — they share a template, so labelling
    both double-draws the same text at the same depth; a hand shape only labels
    itself when no chord starts inside it (a let-ring or arpeggio run);
    **(d) the name comes from the TEMPLATE**, so the naming rules apply for free —
    most visibly a two-string double stop has no name (`DoubleStopNameOptions`)
    and so draws no label, which is also what Rocksmith does. Chord names need no
    atlas pre-registration: `ChartFontAtlas` seeds ASCII.
    Pinned by `HeadlessChordNameTests` (GPU golden frame — proves it RENDERS and
    lands in the viewport) and `ChordNameLabelTests` (default gate — proves WHERE
    it goes once it draws). The geometry test alone passed through both invisible
    releases; it takes both.
  - **Rule (a) RECURRED, and is now a LINT rather than a comment.**
    `NoteRendering/BendRenderer.RenderPeaks` — the bend-peak labels
    ("v 1/2", "v Full", …) — called `DrawText3D` directly, and it was reached from
    `NotesRenderComponent` AND `NoteRendering/ChordRenderer` (since #605 only
    through `SustainRenderer`, which stages chord tails too), both in
    `TransparentElements`. Same silent failure, same three-hop invisibility.
    `Rendering/Text3DPassTests.NoGeometryPassComponentReachesDrawText3D` walks the
    static call graph out of every component the engine registers in
    `Opaque`/`TransparentElements` and fails if any of it reaches `DrawText3D`;
    it names the offending type.
  - **If a geometry pass KNOWS where a label goes, defer it — do not recompute
    it.** `RenderContext.Defer3DLabel(in Deferred3DLabel)` queues text into a
    context-owned, GC-reused array and `Components/BendLabelComponent`
    (`UiElements`) drains it after `SetState(atlas, sceneMvp)`. Chord names
    recompute their geometry inside a Ui component instead, which is right for
    them; a bend peak's position falls out of the sustain tail's per-segment walk
    (string lane, capo/open-string branch, anchor-relative note X) and has two
    entry points, so recomputing would mean duplicating that walk twice and
    keeping both copies in step. **`Fretboard3DEngine.RenderFrame` clears the
    queue as it sets the context up**, before the geometry passes fill it — a
    frame that bails before `RenderUi` must not leak labels into the next one.
  - **Fingering overlay (`NoteRendering/FingeringRenderer`, off by default —
    `AppConfig.Show3DFingering`, Settings ▸ Graphics or the `Toggle3DFingering`
    key, F8):** numbered discs on the neck at each finger's fret, a capsule for
    a barre. **ONE grip at a time** (`TrySelectGrip`): among hand shapes and
    bare chords that have not ended and start within `LeadMs` (750 ms, the
    first 250 ms a fade-in), the earliest — so a held shape keeps its numbers
    until it ends. Until 2026-09-24 it drew every shape/chord inside a 200–250 ms
    window and BEFORE the heads, so a paused editor showed nothing and the gems
    covered the numbers at the strike ("fingering does not render"). Three rules:
    it is drawn LAST in `NotesRenderComponent` (the pass writes no depth, order
    is occlusion — `NotesRenderComponentOrderTests`); barre caps follow the
    LANES, not string indices (inverted strings); number quads swap U under
    `LeftyFlip` so they are not mirror writing. Single notes have no finger
    data in the domain model, so they draw nothing. Pinned by
    `FingeringOverlayTests` (staged geometry through `RenderCore`, which skips
    the static gate).
  - **Every sustain/slide tail, single-note AND chord, is staged by ONE pass,
    LOWEST FIRST** (`SustainRenderer.Render`; #587, #605). A tail is a flat
    ribbon at its string's height in the no-depth-write transparent pass, and
    the camera looks down on every string, so where two tails overlap the HIGHER
    one is the nearer. Snapshot order (time, then string) drew the A tail over
    the low E's; then, with chord tails staged inside `ChordRenderer` after every
    single-note tail, a chord's A tail still did. The order is
    `SustainRenderer.DrawKey`: `2 * lane` on a string (`SustainRenderer.Lane`,
    follows `InvertStrings`), odd in the gap above it. A BEND is ordered per
    segment by the height it is drawn at (a full bend reaches the next string,
    a 1 1/2-step bend passes it), so its peak marker is staged once, in the
    tail's highest pass; vibrato is left out (its wobble never reaches the next
    string). Each pass walks the notes and chords once and returns the next
    occupied key: no buffer, no sort, no allocation. `ChordRenderer` stages NO
    tail; it keeps the per-string rule (`TailsInView` + `ShowsTail`: repeats,
    hand shapes, links, split chords). Consequence: every tail is under every
    head, chord box and hand-shape bracket, as single-note tails always were.
    That is right for a head or box AT or BEHIND a tail, and a slight tint where
    a far box overlaps the near stretch of an earlier chord's tail (no depth
    write, so no order is exact there). Pinned by `SustainTailDrawOrderTests`
    and the GPU `HeadlessTailOverlapRenderTests`.
  - **Heads, arpeggio brackets and chord boxes are staged by ONE walk,
    FARTHEST FIRST** (`NotesRenderComponent.RenderHeadsFarthestFirst`, merging
    `NoteHeadRenderer.RenderNote`, `HandShapeRenderer.RenderHandShape` and
    `ChordRenderer.RenderChord` from the ends of the time-ordered snapshot
    lists; a tie keeps head → bracket → box). The pass writes no depth, so a
    farther object staged after a nearer one paints over it: single heads used
    to be staged nearest first and all of them before every chord, so a far gem
    showed through the near gem in front of it and a far chord covered a near
    single note — in the live app too, the headless renderer drives the same
    `Fretboard3DEngine`. Pinned by `NoteHeadDrawOrderTests` (staged z order)
    and the GPU `HeadlessNoteOcclusionRenderTests`.

### Where the hand is: `Data/AnchorFretSpan`

- **Every "where is the hand" answer comes out of ONE clamp
  (`Data/AnchorFretSpan`), and the rule is CLAMP THE FRET FIRST, then derive the
  width from the clamped value.** Six call sites used to re-derive it by hand:
  three clamped only the LOWER bound (`Math.Max(1, fret)` beside
  `Math.Min(24, fret + width - 1)`), so a fret-0 anchor drew the FHP lane fill,
  the anchor sweep and the fret-number rail ONE FRET NARROWER than the beat
  lines, the chord box, the open-note bar and the camera; two clamped nothing and
  drew the hand-shape box and the arpeggio bracket from
  `GetLinearFretPosition(-1)`, a whole fret LEFT of the nut.
  `NoteRenderMath.GetAnchorFretRange`/`GetAnchorSpan` and
  `HighwayRenderComponent.GetAnchorSpan` are now one-line delegations to it.
- A fret-0 anchor IS malformed (the inspector's L02 `CapoAnchorRangeRule` flags
  it) but three importers still admit one — the same reason `HighwayLaneOpacity`
  was extracted, after a fret-0 anchor threw the whole frame away. **The renderer
  must never be what enforces chart validity, and it must never disagree with
  itself about where the hand is.** Pinned by `Rendering/AnchorFretSpanTests`,
  which asserts the pure rule and then that a fret-0 anchor renders
  byte-identically to a fret-1 one at two real call sites.

## `Batching/`

- `Batching/` — `QuadBatcher` (13-float 3D vertex) + `TextBatcher` feed the
  Vulkan path via `IVulkanRenderService.DrawBatch`/`DrawTextBatch`. **Both
  streams are INDEXED QUADS (0.15.6 / plan 5.9):** every quad is 4 unique
  vertices expanded through ONE shared static index buffer
  (`QuadIndexMath.BuildPattern`, written once at pipeline init; per-draw
  addressing via `vkCmdDrawIndexed`'s `vertexOffset`, so the vertex buffer
  binds once per stream per frame). Batches must stay whole-quad-aligned —
  `QuadBatcher.AddVertex` is private on purpose; add a new `AddQuad*` overload
  instead of exposing raw vertices. `VulkanRenderer` also elides redundant
  per-draw pipeline/descriptor-set binds (tracked per frame, reset in
  `BeginFrame`; a pipeline-LAYOUT switch invalidates the cached set binding —
  highway vs text layouts have incompatible push-constant ranges). The
  13-float vertex keeps the normal attribute deliberately: the fragment
  lighting model (plan 5.6) consumes it — it is NOT dead bandwidth.

