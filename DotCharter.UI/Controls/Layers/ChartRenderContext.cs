using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Controls.Layers;

public class ChartRenderContext
{
    public double Width { get; set; }
    public double Height { get; set; }
    public double CurrentZoom { get; set; }
    public double ViewStartMs { get; set; }
    public double ViewEndMs { get; set; }

    /// <summary>Device pixels per layout unit for this frame. <see cref="ViewStartMs" />
    /// is snapped so its scroll offset is a whole DEVICE pixel
    /// (<c>TimelineViewMath.SnappedViewStartMs</c>), so a layer that rounds an x
    /// rounds in device pixels too — rounding in layout units at 125 % would
    /// put each element's rounding back out of step with the scroll.</summary>
    public double PixelScale { get; set; } = 1.0;
    public double CenterScreenX { get; set; }

    /// <summary>Playhead screen X for this frame. Equals
    /// <see cref="CenterScreenX"/> while the viewport follows the playhead;
    /// the true (possibly off-screen) position while panned away (0.16 6.6).</summary>
    public double PlayheadScreenX { get; set; }
    public float TopMargin { get; set; }
    public float BottomMargin { get; set; }
    public float LaneAreaHeight { get; set; }
    public float LaneHeight { get; set; }
    public float FhpY { get; set; }
    public int StringCount { get; set; }

    /// <summary>v0.34 lefty flip: inverts the vertical string→lane order of
    /// the 2D timeline (the 2D analogue of the mirrored 3D highway). The time
    /// axis is untouched, so the time↔screen anchor rule holds by
    /// construction. Hit-testing MUST use the same mapping —
    /// <c>ChartHitTester.GetLaneY/GetLaneIndexAtY</c> take the same flag.</summary>
    public bool LeftyFlip { get; set; }

    /// <summary>Inverted string layout (tab convention): a vertical string flip
    /// that COMPOSES with <see cref="LeftyFlip" /> — either one alone flips the
    /// lane order, both together cancel (XOR). Hit-testing must use the same
    /// combined flip (<c>IChartViewport.FlipStrings</c>).</summary>
    public bool InvertStrings { get; set; }

    /// <summary>THE string→lane mapping for every 2D layer: top Y of the lane
    /// for <paramref name="stringIndex" />, honoring <see cref="LeftyFlip" /> and
    /// <see cref="InvertStrings" /> (they XOR). Never inline the formula —
    /// renderer and hit-testers must agree.
    /// <para>
    /// Default (no flip) puts StringIndex 0 (low E) at the TOP lane, matching
    /// the 3D highway (`FretboardConstants.StringLaneIndex`). The 3D world Y and
    /// the 2D screen Y point opposite ways, so the 2D lane INDEX is the mirror of
    /// the 3D row index — that is what makes low-E-on-top look identical in both
    /// views. `InvertStrings` then flips both to the tab layout (low E at bottom).
    /// </para></summary>
    public float LaneTopFor(int stringIndex)
        => TopMargin + (LeftyFlip ^ InvertStrings ? StringCount - 1 - stringIndex : stringIndex) * LaneHeight;
    public double CurrentTimeMs { get; set; }
    public bool DrawLabels { get; set; }

    /// <summary>Density-driven level of detail for note rendering, computed
    /// once per frame in <c>ChartRenderer.RenderTimelineFrame</c> (0.19 B1).</summary>
    public Helpers.NoteLevelOfDetail NoteLod { get; set; } = Helpers.NoteLevelOfDetail.Full;

    /// <summary>Note-attached text (fret / slide / bend labels) draws only at
    /// full LOD — below ~a glyph width per note the labels overlap into mush
    /// while still costing atlas quads.</summary>
    public bool DrawNoteLabels => DrawLabels && NoteLod == Helpers.NoteLevelOfDetail.Full;

    public WaveformSamples? WaveformData { get; set; }

    // ViewModels directly accessible
    public ChartEditorViewModel Editor { get; set; } = null!;
    public TransportViewModel Transport { get; set; } = null!;
    public FrameSnapshot Snapshot { get; set; } = null!;

    public double RendererPixelsPerMs { get; set; }
    public double HoveredTimeMs { get; set; }
    public int HoveredStringIndex { get; set; }

    /// <summary>v0.35 Stage 1: the persistent per-string edit mask
    /// (<c>ProjectState.StringEditMask</c>). Drawn on the lane headers by
    /// <c>LaneLayer</c> — the mask changes what every per-string keyboard verb
    /// does, so it must never be invisible modal state (the mistake rscharter's
    /// <c>typingNumber</c> cursor makes). EMPTY = all strings = nothing drawn.</summary>
    public StringEditMask StringEditMask { get; set; } = StringEditMask.Empty;

    /// <summary>v0.35 Stage 3: the editor's A/B loop region
    /// (<c>ProjectState.LoopRegion</c>), or null when none is marked. Drawn as a
    /// tinted band with edge posts by <c>BackgroundLayer</c> — a loop that
    /// silently re-seeks the transport must never be invisible modal state, the
    /// same rule the string mask follows.</summary>
    public DotCharter.Services.Audio.LivePlay.LivePlayLoop? LoopRegion { get; set; }

    /// <summary>Whether <see cref="LoopRegion" /> is actually looping. A marked
    /// but disabled region still draws, dimmed — the punch points are worth
    /// keeping on screen.</summary>
    public bool LoopEnabled { get; set; }

    /// <summary>Loop start punched in with no end yet — drawn as a single post
    /// so a half-marked loop is visible rather than silently pending.</summary>
    public double? LoopPendingStartMs { get; set; }

    /// <summary>True while the transport/audio engine is actually playing —
    /// gates the 0.14.3 playhead glow boost so the effect only draws on
    /// frames the playback loop renders anyway (damage discipline).</summary>
    public bool IsPlaybackActive { get; set; }

    /// <summary>True while an interactive scrub pins the continuous render
    /// loop (<c>ChartRenderer.BeginContinuousRender</c>) and playback is
    /// stopped — gates the 0.14.3 scrub trail. Goes false the instant the
    /// interaction ends, so the final (frozen) frame carries no residue.</summary>
    public bool IsScrubbing { get; set; }

    public bool IsOverrideView { get; set; }

    /// <summary>Per-track top-ribbon layout (visible rows + computed Y positions).</summary>
    public RibbonLayout Ribbon { get; set; } = RibbonLayout.Default;

    /// <summary>The project and the arrangement this frame draws (linked measures read them).</summary>
    public ChartProject? Project { get; set; }

    public Arrangement? Arrangement { get; set; }

    /// <summary>The palette's string colours, read from <see cref="ChartTheme" /> on every
    /// access. An auto-property initializer captured the ARRAY once per renderer, so a theme
    /// swap that assigned a new array never reached an existing 2D timeline.</summary>
    public Color[] StringColors => ChartTheme.BaseStringColors;

    /// <summary>String <paramref name="stringIndex" />'s colour on this chart's string count —
    /// by the string's identity, not its lane (<see cref="ChartTheme.StringSlot" />).</summary>
    public Color StringColorFor(int stringIndex) => ChartTheme.BaseStringColors[ChartTheme.StringSlot(stringIndex, StringCount)];
}

/// <summary>
///     A 2D timeline render layer. Layers emit pure batched geometry
///     (<see cref="TimelineBatcher" /> — CPU-side float buffers, no graphics
///     API calls), which the composed-Vulkan renderer
///     (<c>Timeline2DComposedRenderer</c>) — or the headless twin — replays.
///     Keep it that way: a layer must never talk to a graphics API directly
///     (that neutrality is what made the GL→Vulkan backend swap a pure
///     presentation change).
/// </summary>
public interface ITimelineRenderLayer
{
    void Render(TimelineBatcher batcher, ChartRenderContext rc);
}
