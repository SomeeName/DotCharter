// FrameSnapshot: the public per-frame state the renderers read, the per-frame settings and the Update entry point.
using DotCharter.Rendering.Data.Render;
using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using DotCharter.Core.Graphics;

namespace DotCharter.Rendering.Data;

/// <summary>
///     Per-frame, render-friendly projection of the visible chart slice. Stores
///     fixed-layout struct arrays (with associated count fields) so the GPU
///     uploaders can copy contiguous spans without per-element marshalling.
/// </summary>
/// <remarks>
///     The snapshot is split across partial files for readability:
///     <list type="bullet">
///         <item><c>FrameSnapshot.cs</c> — public state + entry point</item>
///         <item><c>FrameSnapshot.Build.cs</c> — visible-range computation and dispatch</item>
///         <item><c>FrameSnapshot.Notes.cs</c> — note/chord/bend population</item>
///         <item>
///             <c>FrameSnapshot.Events.cs</c> — events, lyrics, tones, templates,
///             anchors, beats, hand-shapes
///         </item>
///     </list>
/// </remarks>
public partial class FrameSnapshot
{
    private readonly Dictionary<FractionalPosition, double> _positionCache = [];
    private readonly HashSet<FretAnchor> _selAnchorsCache = [];
    private readonly HashSet<Chord> _selChordsCache = [];
    private readonly HashSet<EventPoint> _selEventsCache = [];
    private readonly HashSet<ToneChange> _selTonesCache = [];
    private readonly HashSet<HandShape> _selHandShapesCache = [];
    private readonly HashSet<GuitarNote> _selNotesCache = [];

    /// <summary>Last applied <see cref="SnapshotUpdateContext.SelectionVersion"/>
    /// (v0.30 leg b). MinValue forces the first Update to build the sets.</summary>
    private long _lastSelectionVersion = long.MinValue;

    public int AnchorCount;
    public RenderAnchor[] Anchors = [];

    public int BeatCount;
    public RenderBeat[] Beats = [];
    public int BendValueCount;
    public RenderBendValue[] BendValues = [];
    public int Capo;
    public int ChordCount;
    public int ChordNoteCount;
    public RenderChordNote[] ChordNotes = [];
    public RenderChord[] Chords = [];
    public double CurrentTimeMs;
    public string DebugText = string.Empty;
    public int EventPointCount;
    public RenderEventPoint[] EventPoints = [];
    public int HandShapeCount;
    public RenderHandShape[] HandShapes = [];
    public int LyricPhraseCount;
    public RenderLyricPhrase[] LyricPhrases = [];
    public int LyricWordCount;
    public RenderLyricWord[] LyricWords = [];
    public int NoteCount;
    public RenderNote[] Notes = [];
    public int PreviewAnchorCount;
    public RenderAnchor[] PreviewAnchors = [];

    /// <summary>The anchors a pending change would ADD, windowed.</summary>
    public int ProposedAnchorCount;

    /// <inheritdoc cref="ProposedAnchorCount" />
    public RenderAnchor[] ProposedAnchors = [];
    public int PreviewChordCount;
    public int PreviewChordNoteCount;
    public RenderChordNote[] PreviewChordNotes = [];
    public RenderChord[] PreviewChords = [];
    public int PreviewEventPointCount;
    public RenderEventPoint[] PreviewEventPoints = [];

    /// <summary>The section / phrase markers a pending change would add, windowed.</summary>
    public int ProposedEventPointCount;

    /// <inheritdoc cref="ProposedEventPointCount" />
    public RenderEventPoint[] ProposedEventPoints = [];
    public int PreviewHandShapeCount;
    public RenderHandShape[] PreviewHandShapes = [];
    public int SuggestedHandShapeCount;
    public RenderHandShape[] SuggestedHandShapes = [];
    public int PreviewNoteCount;
    public RenderNote[] PreviewNotes = [];

    public int GhostNoteCount;
    public RenderNote[] GhostNotes = [];
    public int GhostChordCount;
    public int GhostChordNoteCount;
    public RenderChordNote[] GhostChordNotes = [];
    public RenderChord[] GhostChords = [];
    public int StringCount;
    public int TemplateCount;
    public RenderChordTemplate[] Templates = [];
    public int ToneCount;
    public RenderToneChange[] Tones = [];

    // ---- Gameplay tone HUD (the single active tone name shown in the HUD) ----
    /// <summary>The tone to display in the HUD right now: the rig-applied preset
    /// when the in-game-tone coordinator is driving, otherwise the arrangement's
    /// own current tone (latest ToneChange at/before the playhead, else the base
    /// tone). Empty ⇒ nothing to show / HUD disabled.</summary>
    public string ActiveToneName = "";
    /// <summary>True when <see cref="ActiveToneName"/> came from the driven rig
    /// (in-game tones on) rather than the raw chart tone.</summary>
    public bool ActiveToneFromRig;
    /// <summary>The applied preset couldn't reproduce the source tone 1:1.</summary>
    public bool ActiveToneApproximated;
    /// <summary><see cref="Environment.TickCount64"/> when the active tone last
    /// changed — the HUD animates a brief emphasis flash off this.</summary>
    public long ActiveToneChangedTick;
    private string _activeTonePrev = "";

    public int ShowlightCount;
    public RenderShowlight[] Showlights = [];

    /// <summary>The editor's show-light chip, e.g. "Fog: Green · Beams: off ·
    /// Lasers: off", resolved at the playhead from
    /// <see cref="SnapshotUpdateContext.ShowlightChipLabels" />. Empty ⇒ no chip
    /// (Play Mode, or no labels handed in).</summary>
    public string ShowlightChipText = "";

    // User-settings flags forwarded to renderer components per frame.
    public bool EnableNoteHitExplosions = true;
    public bool EnableHighwayNearFade = true;
    public bool EnableShowlightBeams = true;
    public bool EnableShowlightFog = true;

    // 0.14.2 gameplay-readability aids: dimmed upcoming-anchor fret numbers +
    // floor sweep toward the upcoming zone (Fretboard3DEngine computes the
    // shared AnchorPreviewInfo per frame from these).
    public bool EnableAnchorPreview = true;
    public bool EnableAnchorSweep = true;
    public int AnchorPreviewLeadMs = 1000;

    // Highway pace + board length. Rocksmith units: LOWER scroll speed = FASTER
    // (see HighwaySpeedPolicy). Together these set how many ms of chart are on
    // the board, so anything that FEEDS the snapshot must size itself from
    // HighwaySpeedPolicy.HorizonMs(...) rather than a fixed look-ahead.
    public float HighwayScrollSpeed = 1.0f;
    public float HighwayDrawDistanceZ = HighwaySpeedPolicy.DefaultDrawDistanceZ;

    // 3D preview camera fill (percent of the tuned default; higher = board fills
    // more / camera closer). Read by Fretboard3DEngine.CalculateCameraMvp.
    public int Preview3DCameraFill = GraphicsSettingsPolicy.DefaultCameraFillPercent;

    // Last judged hit's early/late direction (LiveTimingDirection ordinal; 0 =
    // None = nothing to say) and when it was judged, so the HUD can flash it over
    // the highway and fade it out.
    //
    // These are DRIVER-OWNED and rewritten EVERY frame. The staging and render
    // snapshots ping-pong (Preview3DRenderDriver swaps them), so a value written
    // once and left alone is only observed on alternate frames — the label would
    // strobe at half the frame rate. Never make these "sticky" in the snapshot.
    public byte LastTimingDirection;
    public double LastTimingAtMs;

    // Last judged hit's timing TIER (LiveTimingTier ordinal; 0 = None = nothing
    // to flash) and when it was judged, so the HUD can flash PERFECT/GREAT/GOOD
    // over the highway and fade it out. None is deliberate silence, not "on time":
    // misses and CQT pull-verified hits confirm WHAT was played, never WHEN the
    // attack landed, so they carry no honest tier and must not flash one.
    //
    // Same ping-pong hazard as the direction fields above: DRIVER-OWNED and
    // rewritten EVERY frame, because a value written once is only observed on
    // alternate frames and would strobe at half rate. Never make these "sticky"
    // in the snapshot.
    public byte LastTimingTier;
    public double LastTimingTierAtMs;

    // 0.14.2 visual-quality effects (each individually toggleable).
    public bool EnableBeatPulse = true;
    public bool EnableDepthFog = true;
    public bool EnableVignette = true;
    public bool EnableEnhancedHitFeedback = true;
    public bool EnableCrowdBackground = true;

    // 0.15.6 highway fidelity (plan 5.6): in-shader directional + fill
    // lighting from the streamed vertex normals (HighwayEffects.Lighting).
    public bool EnableHighwayLighting = true;

    // 0.15.6 highway fidelity (plan 5.5): bloom post chain (threshold extract
    // → gaussian mips → additive composite). Default OFF: it adds real GPU
    // work (post passes + composite draw), unlike the vertex-level effects.
    public bool EnableBloom;

    // 0.15.6 highway fidelity (plan 5.7): both stateless, both suppressed in
    // Live Play like the other time-based effects.
    public bool EnableStringShimmer = true;
    public bool EnableHitSparks = true;

    /// <summary>Left-handed mode (v0.34): mirrors the highway's fret axis via
    /// a post-projection X-negate in the camera MVP (winding-safe — both
    /// scene pipelines cull None) plus the <c>TextBatcher</c> glyph-local
    /// counter-flip that keeps in-world text readable. Profile-level; staged
    /// per frame like the Enable* toggles.</summary>
    public bool LeftyFlip;

    /// <summary>Inverted string layout (tab convention): flips the VERTICAL
    /// string→row order of the highway (low E to the bottom). Independent of
    /// <see cref="LeftyFlip"/> (a horizontal fret-axis mirror). Every per-string
    /// Y goes through <c>FretboardConstants.StringLaneIndex</c> with this flag.
    /// Staged per frame like the Enable* toggles; default OFF.</summary>
    public bool InvertStrings;

    // 0.15.6 highway fidelity (approved follow-up): ACES-approx filmic
    // tonemap applied at composite time (Sampled output mode, no swapchain
    // format change). Default OFF — it changes the whole picture's grading.
    public bool EnableFilmicTonemap;

    /// <summary>Room energy 0..1 driving the stage background
    /// (<see cref="CrowdBackgroundPalette" />). Fed from the Live Play
    /// CrowdMeter by the snapshot producer; resting neutral otherwise.</summary>
    public float CrowdEnergy = CrowdBackgroundPalette.NeutralEnergy;

    // Live Play state forwarded per frame. While active, the time-based
    // note-hit explosions are suppressed and hit/miss feedback markers take
    // over as the only playhead feedback.
    public bool LivePlayActive;
    public int LivePlayFeedbackCount;
    public RenderLivePlayFeedback[] LivePlayFeedback = [];

    /// <summary>Chart time through which every Live Play sound is already judged
    /// and can never be re-judged (the matcher's cursor horizon). Only a
    /// resume-rewind replay puts the playhead BEHIND it — the notes in between are
    /// dead and render dimmed (<see cref="DeadNoteMath"/>). NegativeInfinity = none.</summary>
    public double LivePlayResolvedThroughMs = double.NegativeInfinity;

    /// <summary>Resets the per-frame Live Play feedback slice. Call before
    /// draining the verdict queue for the new frame.</summary>
    public void ClearLivePlayFeedback() => LivePlayFeedbackCount = 0;

    /// <summary>Appends one freshly judged Live Play sound for this frame.</summary>
    public void AddLivePlayFeedback(in RenderLivePlayFeedback ev)
    {
        EnsureCapacity(ref LivePlayFeedback, LivePlayFeedbackCount + 1);
        LivePlayFeedback[LivePlayFeedbackCount++] = ev;
    }

    private static void EnsureCapacity<T>(ref T[] array, int requiredSize)
    {
        if (array.Length < requiredSize)
        {
            var newSize = Math.Max(array.Length > 0 ? array.Length * 2 : 64, requiredSize);
            Array.Resize(ref array, newSize);
        }
    }

    private double GetCachedPositionMs(FractionalPosition pos, IList<BeatMarker> beats)
    {
        if (_positionCache.TryGetValue(pos, out var cachedValue)) return cachedValue;
        var computed = pos.GetPositionMs(beats);
        _positionCache[pos] = computed;
        return computed;
    }

    /// <summary>
    ///     Repopulates this snapshot from <paramref name="ctx" />. Re-uses backing
    ///     arrays where possible; <see cref="EnsureCapacity{T}" /> grows them with
    ///     a doubling strategy.
    /// </summary>
    public void Update(SnapshotUpdateContext ctx)
    {
        // Force recalculate position cache every frame so dragging the BeatMap
        // instantly pushes notes — the cache is only valid for one frame.
        _positionCache.Clear();

        // Indexed copies — foreach through the interface would box one
        // enumerator per collection per frame (see SnapshotUpdateContext).
        // Idle skip (v0.30 leg b): with a versioned context (SelectionVersion
        // ≥ 0) the six lookup sets are only re-hashed when the version moved —
        // during playback with a live selection that turns an O(selection)
        // per-frame cost into a long-compare. Unversioned contexts (−1, the
        // default) rebuild every frame exactly as before.
        var selectionUnchanged = ctx.SelectionVersion >= 0 &&
                                 ctx.SelectionVersion == _lastSelectionVersion;
        _lastSelectionVersion = ctx.SelectionVersion;
        if (!selectionUnchanged)
        {
            _selNotesCache.Clear();
            var selNotes = ctx.SelectedNotes;
            for (var i = 0; i < selNotes.Count; i++)
                _selNotesCache.Add(selNotes[i]);

            _selChordsCache.Clear();
            var selChords = ctx.SelectedChords;
            for (var i = 0; i < selChords.Count; i++)
                _selChordsCache.Add(selChords[i]);

            _selHandShapesCache.Clear();
            var selHandShapes = ctx.SelectedHandShapes;
            for (var i = 0; i < selHandShapes.Count; i++)
                _selHandShapesCache.Add(selHandShapes[i]);

            _selAnchorsCache.Clear();
            var selAnchors = ctx.SelectedAnchors;
            for (var i = 0; i < selAnchors.Count; i++)
                _selAnchorsCache.Add(selAnchors[i]);

            _selEventsCache.Clear();
            var selEvents = ctx.SelectedEventPoints;
            for (var i = 0; i < selEvents.Count; i++)
                _selEventsCache.Add(selEvents[i]);

            _selTonesCache.Clear();
            var selTones = ctx.SelectedTones;
            for (var i = 0; i < selTones.Count; i++)
                _selTonesCache.Add(selTones[i]);
        }

        CurrentTimeMs = ctx.CurrentTimeMs;
        Capo = ctx.Capo;
        // The arrangement is the only tuning a chart has (see the note on
        // ChartData) — there is no project-level fallback in the middle
        // of this expression, since a second copy would go stale.
        StringCount = ctx.Arrangement?.Tuning is { Length: > 0 } t ? t.Length : 6;
        DebugText = ctx.DebugText;
        BendValueCount = 0;

        UpdateEventsAndLyrics(ctx);
        UpdateTemplates(ctx);
        UpdateCoreTimeline(ctx);
        UpdateNotes(ctx.ActiveNotes, ctx.NoteStart, ctx.NoteEnd, ref Notes, out NoteCount, ctx.LinkedTargets,
            ctx.ActiveBeats, ctx);
        UpdateNotes(ctx.PreviewNotes, 0, ctx.PreviewNotes.Count - 1, ref PreviewNotes, out PreviewNoteCount,
            ctx.LinkedTargets, ctx.ActiveBeats, ctx);
        UpdateChords(ctx.ActiveChords, ctx.ChordStart, ctx.ChordEnd, ref Chords, out ChordCount, ref ChordNotes,
            out ChordNoteCount, ctx.LinkedTargets, ctx.ActiveBeats, ctx);
        JoinLinkedTails();
        UpdateChords(ctx.PreviewChords, 0, ctx.PreviewChords.Count - 1, ref PreviewChords, out PreviewChordCount,
            ref PreviewChordNotes, out PreviewChordNoteCount, ctx.LinkedTargets, ctx.ActiveBeats, ctx);

        UpdateNotes(ctx.GhostNotes, ctx.GhostNoteStart, ctx.GhostNoteEnd, ref GhostNotes, out GhostNoteCount,
            ctx.LinkedTargets, ctx.ActiveBeats, ctx, true);
        UpdateChords(ctx.GhostChords, ctx.GhostChordStart, ctx.GhostChordEnd, ref GhostChords, out GhostChordCount,
            ref GhostChordNotes, out GhostChordNoteCount, ctx.LinkedTargets, ctx.ActiveBeats, ctx, true);

        UpdatePreviewHandShapesAndAnchors(ctx);
        UpdateSuggestedHandShapes(ctx);
        UpdateShowlights(ctx);
    }
}
