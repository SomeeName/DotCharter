# Scene, components, snapshot and assets — `Data/` (the frame snapshot); `Text/`; assets

## `Data/` — the frame snapshot

- `Data/` — `FrameSnapshot` (partial; the per-frame, GC-reused flat `Render*[]`
  projection of the visible chart slice) + `Data/Render/` POD structs +
  `*Constants`. Every projection is windowed: notes/chords/anchors/beats/
  hand-shapes/ghosts by index pairs the builder computes, events/tones/lyric
  phrases/showlights internally off `SnapshotUpdateContext.ViewStartMs/
  ViewEndMs/VisibleRangeBufferMs` — **every snapshot builder must set those
  three** (`FrameSnapshot.BuildSnapshot` and `Preview3DRenderDriver.RunSnapshotUpdate`
  do — the latter also feeds `ctx.Tones` from `ActiveArrangement.ToneChanges`;
  without it the 3D HUD tone label showed nothing). Sticky consumers (pinned
  section/phrase badges, showlight fog/beam/laser state) are fed by cached
  sticky-prefix entries prepended to the windowed arrays — see
  `FrameSnapshot.Events.cs`. Chord templates stay a FULL projection on purpose
  (`Templates[TemplateId]` is id-indexed).
  - **The repeat box is DATA, never derived here (#487).** Whether a chord shows
    as Rocksmith's frame-only repeat box is the EXPORT's decision
    (`DotCharter.Services/Export/Rocksmith/RepeatChords`: `RsChordDensity`, plus
    `ForceNoNotes`, minus link landings). Each builder gets the whole-level set
    from its own `RepeatChordLatch` (list identity + count + the editor's
    `HistoryRevision`; no timer, so playback recomputes nothing) into
    `SnapshotUpdateContext.RepeatChords`;
    `UpdateChords` looks each WINDOWED chord up and sets `ChordFlags.Repeat`.
    `ChordRenderer` (3D) and the UI's `NoteLayer` (2D) read only the flag: 3D
    draws the half-height box with no notes and no tail; 2D draws the chord DIMMED
    with its techniques at full contrast (#564, `docs/agents/ui/rendering-perf/timeline-2d.md`). The 3D preview used to run
    its own RsCharter port (`HasPriorMatchingChord`), a different algorithm that
    disagreed with the export (measured in the #487 PR). `Rendering/RepeatChordSourceLintTests` refuses a
    template comparison, a named decider or a second flag write in this assembly;
    `Rendering/RepeatChordParityTests` pins flag == shipped XML.
  - **A split chord is DATA too (#599).** `UpdateChords` copies
    `Chord.SplitIntoNotes` into `ChordFlags.SplitIntoNotes`; every export ships
    such a chord as one note per string (`LinkLandingBreakup`), so `ChordRenderer`
    (3D) draws its heads as single notes (`DrawRepickedLandingHeads`) with their
    tails and NO box, and `ChordNameRenderer` / `FingeringRenderer` show no name or
    grip for it. The 2D timeline keeps drawing it as one chord — the editor's
    selection unit (`SplitChordSnapshotTests`; pixels in
    `HeadlessPickScrapeRenderTests`).
  - **…but that full projection is LATCHED, not re-run every frame.**
    `UpdateTemplates` rebuilt the whole table on both renderers at frame rate —
    per template a struct build plus 8× `GetFret` + 8× `GetFinger` DICTIONARY
    lookups — while templates only change on a chart edit. Identity + `Count` is
    *not* a sound latch on its own, because the editing verbs mutate templates
    IN PLACE without touching the list (`FingeringGenerationService` writes
    `template.Frets`/`.Fingers`, `ArrangementAutoFixService` clamps a fret to the
    capo, `ChartSpacingEnforcer`/`DoubleStopChordNames` assign `template.Name`,
    `RefretService` replaces `template.Fingers`). So the latch has two modes: a
    versioned context (`SnapshotUpdateContext.TemplatesVersion >= 0`, the
    `SelectionVersion` contract) skips EXACTLY, and the unversioned default
    (−1 — what every caller passes today) skips on identity + count under the
    same 250 ms staleness bound the sticky-event cache uses, so 60 Hz becomes
    4 Hz and an in-place edit still lands within a quarter second. A skipped
    rebuild leaves `Templates` byte-identical, which is what keeps the id
    indexing valid. Pinned by `Rendering/FrameSnapshotTemplateLatchTests`
    (both modes, plus the in-place-edit case), which drives the bound through
    the snapshot's internal `TemplateLatchClock` seam — no wall clock.
  - **NEVER `foreach` a `SnapshotUpdateContext` collection — walk it INDEXED.**
    Every one of those fields is typed `IList<T>` and both production call sites
    (`ChartRenderer`, `Preview3DRenderDriver`) assign `ObservableCollection`s,
    which expose no public struct enumerator — so `foreach` binds to
    `IEnumerable<T>.GetEnumerator()` and BOXES one enumerator per collection per
    frame. The rule was written down in `FrameSnapshot.cs` and
    `SnapshotUpdateContext.cs` and then broken anyway: five walks over
    `PreviewNotes`/`PreviewChords`/`Chord.ChordNotes`/`ActiveAnchors` in
    `FrameSnapshot.Build.cs` + `.Events.cs` stayed `foreach`, in both
    `BuildLinkedTargets` and `BuildTechniqueOrigins` — the latter's own sibling
    loop 55 lines up was already indexed. **Measured 240 B/frame → 0.**
    `FrameSnapshotPerfGateTests.WarmRebuild_WithLivePreviewCollections_StaysWithinAllocationBudget`
    is the gate; the pre-existing budget test misses this entirely because it
    leaves the preview fields at their `Array.Empty` default, whose enumerator
    the runtime caches. **A preview-path allocation gate must assign real
    `ObservableCollection`s with content**, or it meters nothing.
  - **Dead notes (resume rewind):** after a Play Mode pause the transport backs up
    and an already-judged stretch replays. `FrameSnapshot.LivePlayResolvedThroughMs`
    (the Live Play matcher's cursor horizon, fed by `Preview3DRenderDriver`) plus the
    pure `DeadNoteMath.IsDead(noteTime, playhead, resolvedThrough)` mark those notes
    and the note/sustain/chord renderers draw them with `DeadNoteMath.Dim`. **The
    `noteTime >= playhead` half of the predicate is load-bearing** — without it a
    sustain ringing under the playhead dims during NORMAL play. The predicate is
    false outside a replay, so the renderer needs no mode state, and note colour is
    plain vertex colour, so there is no shader/`.spv` change. 3D only (the 2D editor
    renderer has no Live Play awareness).
  - **TWO flags spell "arpeggio", and the import paths set different ones —
    always ask `ChordTemplate.IsArpeggio`** (Core; it replaced this assembly's
    own `ArpeggioFlags` fold in #480). `ChordTemplate.Arpeggio` comes
    from the SNG chord mask `0x1` (the package reader's SNG mapper), the EOF parser and the
    chord-template editor; `ChordTemplate.ForceArpeggioInRs` comes from a `-arp`
    `displayName` suffix (`RsXmlSongReader`) and is re-folded from a `-arp` NAME
    on every save (`ChartSpacingEnforcer.StripRsDisplaySuffixes`). Reading either
    ALONE works on one import path and is silently dead on the other. Rendering
    read `Arpeggio` alone until v0.44, so every RS-XML-imported arpeggio — the
    primary Rocksmith import — drew a plain teal hand-shape lane instead of the
    purple bracket, on all three consumers (`HighwayRenderComponent`,
    `HandShapeRenderer`, `ChordRenderer`). The OR is not this assembly's
    invention: `RsXmlSongWriter` derives the `-arp` suffix from exactly
    `ct.Arpeggio || ct.ForceArpeggioInRs`, and `HandShapeContentRule` skips its
    declared-grip check on exactly the same predicate. **`FrameSnapshot` is the
    ONE place the answer is read** — `UpdateTemplates` folds it into
    `RenderChordTemplate.Arpeggio`, the hand-shape projection into
    `RenderHandShape.IsArpeggio`, and every renderer reads those.
    `Rendering/ArpeggioFlagsTests` lints that nothing in the assembly mentions
    `ForceArpeggioInRs`.
  - **A hand-shape bar carries what the 2D bar prints (#480):**
    `RenderHandShape.Label` (a committed bar: its template's NAME, looked up by
    id like the validator's `HandShapeTemplate`, never the stale `HandShape.Name`;
    a PREVIEW bar: `HandShape.Name`, the grip #481's draw gesture names),
    `IsArpeggio`, and `HasFinding` — copied from
    `SnapshotUpdateContext.HandShapeFindings`, the set the inspector publishes from
    its own issue list (`Services/Editing/Validation/HandShapeFindings`: E05 per
    finding, D09 through `ArrangementIssue.HandShapes`). Never derived here.
    SUGGESTED bars (#491) are a separate array, `FrameSnapshot.SuggestedHandShapes`
    (`IsSuggested`, `Label` = the grip's name), WINDOWED from
    `SnapshotUpdateContext.SuggestedHandShapes` — the list
    `Services/Editing/HandShapes/HandShapeSuggestionLatch` computes once per edit.
    Pinned by `HandShapeBarRenderDataTests`.
  - **The bulk-edit preview (#485):** `ChordFlags.EditTarget` and
    `RenderHandShape.IsEditTarget` are copied from
    `SnapshotUpdateContext.EditTargetChords` / `EditTargetHandShapes` — the set
    the chord panel resolved through `ChordApplyScope.Preview` and published as
    `ChartEditorViewModel.UseHighlight`. Reference identity; never derived here.
    The AI chart pass review (#506) rides the same path with anchors:
    `RenderAnchor.IsEditTarget` from `EditTargetAnchors` (live anchors the review
    replaces) and the separate `ProposedAnchors` array (the anchors it would add,
    windowed in `UpdateProposedAnchors`, each spanning to the next proposal).
    Pinned by `EditTargetRenderDataTests`.
  - **Linked tails JOIN (#106):** a tail is drawn on its own
    (`SustainRenderer.Render`, one tail at a time), so after the active notes and chords are
    projected `FrameSnapshot.JoinLinkedTails` (`FrameSnapshot.Links.cs`) pairs every
    `LinkNext` sound with its `LinkPrevious` target in TIME order: the source's final
    bend is carried into the target (`RenderNote.BendCarry` /
    `RenderChordNote.BendCarry`, the start value `NoteRenderMath.GetBendOffset`
    holds and moves from), and a vibrato / tremolo that only one side has eases in
    (`NoteFlags.RampIn`) or out (`RampOut`) over `RenderingConstants.LinkRampMs`
    (`SustainRenderer.WobbleEnvelope`). Two vibrato tails need no ramp: the wobble
    phase is absolute time. Both drivers get it, because it runs inside `Update`;
    a hand-built snapshot (a headless test) calls it itself. Its index is a reused
    dictionary, so a steady frame allocates nothing. `Rendering/LinkedTailJoinTests`.
  - **Links are PER STRING, and both drivers find landings through ONE walk,
    `FrameSnapshot.CollectLinkTargets` (#476).** A chord note links only when IT
    carries `LinkNext` — never because a sibling does (`Chord.HasLinkNext()` is
    chord-wide), and only with a sustain to land on. `Preview3DRenderDriver` kept
    its own chord-wide copy, so in "5 slides to 6 over a re-picked 8" the 8 was
    flagged a landing. **A landing chord** (any note `LinkPrevious`) draws no box,
    no head for a linked string, and a SINGLE-NOTE head
    (`NoteHeadRenderer.DrawHead`) plus its tail for a string re-picked there —
    the export writes the landing as loose notes (`LinkLandingBreakup`), which is
    what the game shows. `Rendering/PartialChordSlideRenderTests` (snapshot, 2D
    layer, 3D quads) and `Rendering/Headless/HeadlessPartialChordSlideTests`
    (GPU) run the owner's GP fixture through both renderers.
  - **Gameplay tone HUD:** `FrameSnapshot.ResolveActiveTone` resolves ONE
    `ActiveToneName` per frame — the rig-applied preset when the
    `GameplayToneHud` static channel's `Rig` override is set (in-game tones
    driving), else the chart's own tone (latest `ToneChange` at/before the
    playhead over the FULL `ctx.Tones`, else `Arrangement.StartingTone`/
    `ToneBase`). `ActiveToneChangedTick` bumps on a name change; `HudComponent`
    flashes a brief scale+brighten emphasis off it. Gated by
    `GameplayToneHud.Enabled` (`AppConfig.ShowGameplayToneHud`, default on,
    mirrored at startup).

## `Text/`

- `Text/` — `ChartFontAtlas`: the shared 2D glyph-atlas base (SkiaSharp
  rasterization + metrics); the Vulkan subclass only uploads.

## Assets

The 3D chart textures (`Assets/Textures/{notes,fingering,inlays}.png`) are
**`EmbeddedResource`s of THIS assembly**, named
`DotCharter.Rendering.Assets.<file>` — the same mechanism as the `.spv`
shaders — and decoded with SkiaSharp (`SKCodec`) into premultiplied RGBA8888.
No Avalonia platform is required, so the decode has no app-lifetime dependency.

They used to live in `DotCharter.UI` and be opened through a hardcoded
`avares://DotCharter/Assets/Textures/...` URI: an upward dependency on the UI
assembly's NAME that no csproj expressed, that no build step could catch, and
whose failure mode was a `Debug.WriteLine` plus silent white textures in
Release. **There is now no soft coupling: `DotCharter.Rendering` references
`DotCharter.Core` and NuGet, and nothing else.** `RenderingLayeringTests` pins
the ASSET half (the resources resolve and decode). The REFERENCE half moved in
0.41 to `DotCharter.Tests/Infrastructure/AssemblyLayeringTests` — a Theory over
Core / Localization / Analysis / Rendering / Services, each row carrying the
exact set of first-party assemblies it may reference, so this project's rule is
now "Core, and nothing else" rather than a denylist of four names that a fifth
could slip past.

Headless still substitutes 1×1 white textures (`HeadlessTextureFallback`) so
golden frames stay comparable with the baselines they were captured against —
the real sheets would load fine there now.
