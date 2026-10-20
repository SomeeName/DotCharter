using DotCharter.Rendering.Data;

namespace DotCharter.Rendering.Engines;

/// <summary>
///     The anchor-follow camera's per-view easing state.
///     <para>
///         This must not be fields on <see cref="Fretboard3DEngine" />, or
///         a split highway could not exist: two players on
///         one engine would have shared one <c>_camX</c>/<c>_fretSpan</c>, so
///         both halves would have panned to whichever player's hand moved last.
///         The state is per VIEW, not per engine — everything else the engine
///         owns (pipelines, textures, the glyph atlas, the scene graph) is
///         genuinely shared and must NOT be duplicated per player.
///     </para>
/// </summary>
public sealed class HighwayCameraState
{
    internal float CamX = 2.5f;
    internal float FretSpan = 4f;

    /// <summary>True while this view's easing has not converged on its target.
    /// The render loop's idle gate keeps rendering full-rate while ANY view is
    /// animating.</summary>
    public bool IsAnimating { get; internal set; }

    /// <summary>Forgets the eased position so the next frame snaps toward the
    /// new target from the default framing. Used when a view is re-pointed at a
    /// different arrangement (a co-op player swapping parts) — otherwise the
    /// camera glides across the whole neck from the previous chart's hand
    /// position.</summary>
    public void Reset()
    {
        CamX = 2.5f;
        FretSpan = 4f;
        IsAnimating = true;
    }
}

/// <summary>
///     One player's highway inside a shared frame: what to draw
///     (<see cref="Snapshot" />), where to draw it (<see cref="Rect" />, from
///     <see cref="SplitHighwayLayout" />) and its own camera easing.
///     <para>
///         Reused frame to frame — the camera state has to survive, and
///         allocating per frame on the render thread is what the snapshot
///         ping-pong exists to avoid.
///     </para>
/// </summary>
public sealed class HighwayViewport
{
    /// <summary>The chart slice this view draws. Null skips the view (the frame
    /// still renders the others) — a player whose snapshot failed to build must
    /// never blank the other player's half.</summary>
    public FrameSnapshot? Snapshot { get; set; }

    /// <summary>Where in the frame this view lands, in the same pixel space the
    /// caller passes to <c>RenderFrame</c>.</summary>
    public HighwayViewportRect Rect { get; set; }

    /// <summary>This view's own anchor-follow camera. Never shared.</summary>
    public HighwayCameraState Camera { get; } = new();
}
