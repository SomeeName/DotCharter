using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using ChartData = DotCharter.Core.Domain.Project.ChartData;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using ChordTemplate = DotCharter.Core.Domain.Guitar.ChordTemplate;
using EventPoint = DotCharter.Core.Domain.Events.EventPoint;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;
using LyricEvent = DotCharter.Core.Domain.Vocals.LyricEvent;
using LyricPhrase = DotCharter.Core.Domain.Vocals.LyricPhrase;
using ToneChange = DotCharter.Core.Domain.Events.ToneChange;

namespace DotCharter.Rendering.Data;

/// <summary>
///     Mutable working set passed by the rendering pipeline into
///     <see cref="FrameSnapshot.Update" />. Aggregates the active chart slice
///     plus index ranges and selection/hover state for one frame.
/// </summary>
public class SnapshotUpdateContext
{
    public IList<FretAnchor> ActiveAnchors = Array.Empty<FretAnchor>();
    public IList<BeatMarker> ActiveBeats = Array.Empty<BeatMarker>();
    public IList<Chord> ActiveChords = Array.Empty<Chord>();
    public IList<HandShape> ActiveHandShapes = Array.Empty<HandShape>();
    public IList<GuitarNote> ActiveNotes = Array.Empty<GuitarNote>();
    public IList<ChordTemplate> ActiveTemplates = Array.Empty<ChordTemplate>();

    /// <summary>
    ///     Monotonic version of <see cref="ActiveTemplates" />' CONTENT: when
    ///     ≥ 0 and unchanged between two <see cref="FrameSnapshot.Update" />
    ///     calls (alongside the same list instance and count), the snapshot
    ///     skips re-projecting the id-indexed template table — the caller
    ///     promises a bump accompanies every template change, including the
    ///     IN-PLACE ones the editing verbs make (<c>template.Frets[s] = …</c>,
    ///     <c>template.Name = …</c>) which change neither instance nor count.
    ///     <para>
    ///         The default −1 means "unversioned": the latch falls back to
    ///         identity + count under a 250 ms staleness bound, so an in-place
    ///         edit is still picked up within a quarter second. Wire this only
    ///         if you have a counter that genuinely covers those verbs.
    ///     </para>
    /// </summary>
    public long TemplatesVersion = -1;

    public int AnchorEnd;
    public int AnchorStart;
    public int BeatEnd;
    public int BeatStart;
    public int Capo;
    public ChartData? Chart;
    public Arrangement? Arrangement;
    public int ChordEnd;
    public int ChordStart;
    public double CurrentTimeMs;

    /// <summary>
    ///     Visible time range for the collections <see cref="FrameSnapshot.Update" />
    ///     windows itself (events, tones, lyric phrases, showlights). MUST be set by
    ///     every snapshot builder — both <see cref="FrameSnapshot.BuildSnapshot" />
    ///     and the 3D preview's direct <c>Update</c> path do. Left at 0/0 these
    ///     projections collapse to (nearly) nothing.
    /// </summary>
    public double ViewStartMs;

    /// <inheritdoc cref="ViewStartMs" />
    public double ViewEndMs;

    /// <summary>Culling buffer applied on both sides of the view range
    /// (0 on the 3D path — parity with its note windowing;
    /// <c>RenderingConstants.VisibleRangeBufferMs</c> on the 2D path).</summary>
    public double VisibleRangeBufferMs;
    public string DebugText = string.Empty;

    /// <summary>The localized words for the editor's show-light chip. Null ⇒
    /// no chip: Play Mode leaves it null.</summary>
    public ShowlightChipLabels? ShowlightChipLabels;
    public IList<EventPoint> EventPoints = Array.Empty<EventPoint>();
    public int HandShapeEnd;
    public int HandShapeStart;
    public FretAnchor? HoveredAnchor;
    public Chord? HoveredChord;
    public EventPoint? HoveredEventPoint;
    public HandShape? HoveredHandShape;
    public GuitarNote? HoveredNote;
    public ToneChange? HoveredTone;
    public LyricEvent? HoveredLyric;
    public LyricEvent? SelectedLyric;
    public HashSet<(long timeMs, int stringIdx)> LinkedTargets = [];
    /// <summary>
    /// (timeMs, stringIdx) coordinates where there is an incoming note that
    /// carries a sustain-relevant technique (slide / vibrato / tremolo /
    /// bend). Marks the *source* of a LinkNext as
    /// <see cref="DotCharter.Rendering.Data.Render.NoteFlags.LinkTargetHasTechnique"/>
    /// so the chord renderer keeps drawing the sustain that connects into a
    /// technique-bearing target.
    /// </summary>
    public HashSet<(long timeMs, int stringIdx)> TechniqueOrigins = [];

    /// <summary>
    ///     The active level's chords that ship WITHOUT chord notes — Rocksmith's
    ///     frame-only repeat box. Whole-level and computed OUTSIDE this assembly
    ///     by the export's own rule (<c>DotCharter.Services.Export.Rocksmith.RepeatChords</c>,
    ///     latched by <c>RepeatChordLatch</c>); <see cref="FrameSnapshot.Update" />
    ///     only looks each WINDOWED chord up and sets
    ///     <see cref="DotCharter.Rendering.Data.Render.ChordFlags.Repeat" />.
    ///     Reference identity. Rendering never re-derives it.
    /// </summary>
    public IReadOnlySet<Chord> RepeatChords = EmptyRepeatChords;

    private static readonly IReadOnlySet<Chord> EmptyRepeatChords = new HashSet<Chord>();

    /// <summary>
    ///     The hand shapes the inspector's validation found E05/D09 against —
    ///     whole-arrangement and computed OUTSIDE this assembly from the issue list
    ///     (<c>DotCharter.Services.Editing.Validation.HandShapeFindings</c>);
    ///     <see cref="FrameSnapshot.Update" /> only looks each WINDOWED shape up and
    ///     sets <see cref="DotCharter.Rendering.Data.Render.RenderHandShape.HasFinding" />.
    ///     Reference identity. Rendering never re-derives a finding.
    /// </summary>
    public IReadOnlySet<HandShape> HandShapeFindings = EmptyHandShapeFindings;

    private static readonly IReadOnlySet<HandShape> EmptyHandShapeFindings = new HashSet<HandShape>();

    /// <summary>
    ///     The chords and hand shapes a pending bulk chord edit would change — the
    ///     chord panel's preview, resolved OUTSIDE this assembly by the edit's
    ///     own resolver (<c>ChordApplyScope.Preview</c>, published as the editor's
    ///     <c>UseHighlight</c>). <see cref="FrameSnapshot.Update" /> only looks each
    ///     WINDOWED sound up and sets <see cref="DotCharter.Rendering.Data.Render.ChordFlags.EditTarget" />
    ///     / <see cref="DotCharter.Rendering.Data.Render.RenderHandShape.IsEditTarget" />.
    ///     Reference identity; empty when no preview is showing.
    /// </summary>
    public IReadOnlySet<Chord> EditTargetChords = EmptyRepeatChords;

    /// <inheritdoc cref="EditTargetChords" />
    public IReadOnlySet<HandShape> EditTargetHandShapes = EmptyHandShapeFindings;

    /// <summary>
    ///     The live anchors a pending change removes, and the anchors it would add — the AI
    ///     chart pass review, resolved OUTSIDE this assembly off the write's own
    ///     composition (<c>AiChartApplier.Preview</c>, published as the editor's
    ///     <c>UseHighlight</c>). <see cref="FrameSnapshot.Update" /> sets
    ///     <see cref="DotCharter.Rendering.Data.Render.RenderAnchor.IsEditTarget" /> on each
    ///     windowed live one and projects the proposed ones (sorted by position) into
    ///     <see cref="FrameSnapshot.ProposedAnchors" />. Reference identity; empty when no
    ///     review is showing.
    /// </summary>
    public IReadOnlySet<FretAnchor> EditTargetAnchors = EmptyAnchors;

    /// <inheritdoc cref="EditTargetAnchors" />
    public IReadOnlyList<FretAnchor> ProposedAnchors = Array.Empty<FretAnchor>();

    private static readonly IReadOnlySet<FretAnchor> EmptyAnchors = new HashSet<FretAnchor>();

    /// <summary>
    ///     The live section / phrase markers a pending change replaces, and the markers it would
    ///     add — the AI chat's sections &amp; phrases review, resolved OUTSIDE this assembly
    ///     off the write's own composition (<c>AiStructureApplier.Preview</c>, published as the
    ///     editor's <c>UseHighlight</c>). <see cref="FrameSnapshot.Update" /> flags each projected
    ///     live marker (<see cref="DotCharter.Rendering.Data.Render.RenderEventPoint.SectionIsEditTarget" />,
    ///     <see cref="DotCharter.Rendering.Data.Render.RenderEventPoint.PhraseIsEditTarget" />) and
    ///     projects the proposed ones into <see cref="FrameSnapshot.ProposedEventPoints" />. Their
    ///     names are Rocksmith section names or RS-legal phrase names — ASCII, which the glyph
    ///     atlas is seeded with. Reference identity; empty when no review is showing.
    /// </summary>
    public IReadOnlySet<EventPoint> EditTargetSections = EmptyEvents;

    /// <inheritdoc cref="EditTargetSections" />
    public IReadOnlySet<EventPoint> EditTargetPhrases = EmptyEvents;

    /// <inheritdoc cref="EditTargetSections" />
    public IReadOnlyList<EventPoint> ProposedEventPoints = Array.Empty<EventPoint>();

    private static readonly IReadOnlySet<EventPoint> EmptyEvents = new HashSet<EventPoint>();
    public IList<LyricPhrase> LyricPhrases = Array.Empty<LyricPhrase>();
    public int NoteEnd;
    public int NoteStart;
    public IList<FretAnchor> PreviewAnchors = Array.Empty<FretAnchor>();
    public IList<Chord> PreviewChords = Array.Empty<Chord>();
    public IList<EventPoint> PreviewEventPoints = Array.Empty<EventPoint>();
    public IList<HandShape> PreviewHandShapes = Array.Empty<HandShape>();

    /// <summary>
    ///     The suggested bars of the level on screen, sorted by position — computed OUTSIDE
    ///     this assembly (<c>DotCharter.Services.Editing.HandShapes.HandShapeSuggestionLatch</c>) and
    ///     only WINDOWED here into <see cref="FrameSnapshot.SuggestedHandShapes" />. Empty when the
    ///     view hides them.
    /// </summary>
    public IList<HandShape> SuggestedHandShapes = Array.Empty<HandShape>();
    public IList<GuitarNote> PreviewNotes = Array.Empty<GuitarNote>();
    /// <summary>Sorted by Position (ChartRenderer.FillGhostBuffers sorts on
    /// rebuild) — required by the visible-range binary search.</summary>
    public IList<GuitarNote> GhostNotes = Array.Empty<GuitarNote>();
    public IList<Chord> GhostChords = Array.Empty<Chord>();
    public int GhostNoteStart;
    public int GhostNoteEnd = -1;
    public int GhostChordStart;
    public int GhostChordEnd = -1;
    // IReadOnlyList (not IEnumerable) so FrameSnapshot.Update can copy with
    // indexed for-loops — a foreach through the interface boxes the
    // ObservableCollection enumerator, ~6 allocations per snapshot per
    // renderer per frame. Both call sites assign ObservableCollections.
    /// <summary>
    /// Monotonic version of the Selected* lists' CONTENT (v0.30 leg b): when
    /// ≥ 0 and unchanged between two <see cref="FrameSnapshot.Update"/> calls,
    /// the snapshot skips re-hashing the selection into its lookup sets — the
    /// caller promises the lists' content only ever changes together with the
    /// version (wire it to <c>ISelectionService.SelectionVersion</c>). The
    /// default −1 means "unversioned": rebuild every frame, the historical
    /// behaviour every other builder keeps for free.
    /// </summary>
    public long SelectionVersion = -1;

    public IReadOnlyList<FretAnchor> SelectedAnchors = Array.Empty<FretAnchor>();
    public IReadOnlyList<Chord> SelectedChords = Array.Empty<Chord>();
    public IReadOnlyList<EventPoint> SelectedEventPoints = Array.Empty<EventPoint>();
    public IReadOnlyList<ToneChange> SelectedTones = Array.Empty<ToneChange>();
    public IReadOnlyList<HandShape> SelectedHandShapes = Array.Empty<HandShape>();
    public IReadOnlyList<GuitarNote> SelectedNotes = Array.Empty<GuitarNote>();
    public IList<ToneChange> Tones = Array.Empty<ToneChange>();
    public IList<Showlight> Showlights = Array.Empty<Showlight>();
}
