using Avalonia;

namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
///     "Has the pointer moved far enough to mean a DRAG?" — the latch the three
///     event-ribbon gestures share.
///     <para>
///         Setting <c>HasDragged = true</c> on the FIRST pointer
///         move of the press would be wrong: <c>EventInteractionTool.OnPointerReleased</c>
///         commits whenever that flag is set, so one pixel of hand tremor while
///         CLICKING would commit a real, undoable edit — and none of the
///         three edits is small:
///         <list type="bullet">
///             <item>a section/phrase marker would be re-SNAPPED (<c>DragState.GetDelta</c>
///             snaps the reference position to the grid, so the delta is non-zero
///             the moment the marker sits off-grid — which every RS-XML/psarc
///             import produces), so clicking an imported marker to select it would move
///             it to the nearest grid line;</item>
///             <item>a lyric would be snapped to the grid or to a waveform ONSET;</item>
///             <item>a beat marker would be retimed and the whole segment's BPM
///             recalculated, with a <c>ChartSpacingEnforcer.EnforceProject</c>
///             pass behind it.</item>
///         </list>
///         The highway is not affected, and that is not luck: its snap goes
///         through <c>SoundMagnetSnap</c>, which pulls back onto the very sound
///         under the pointer. Nothing magnets a marker, a lyric or a beat.
///     </para>
///     <para>
///         The threshold is <see cref="MarqueeHitTester.DragThresholdPx" /> — the
///         same 3 px the marquee already distinguishes a click from a rubber-band
///         with, so one number decides it everywhere.
///     </para>
/// </summary>
public sealed class GestureDragThreshold
{
    private Point _origin;

    /// <summary>True once the pointer has passed the threshold. LATCHING: a drag
    /// that wanders back inside the threshold is still a drag — otherwise the
    /// release would cancel an edit the user has already watched happen.</summary>
    public bool HasDragged { get; private set; }

    /// <summary>Begin a gesture at the press position. Clears the latch.</summary>
    public void Arm(Point origin)
    {
        _origin = origin;
        HasDragged = false;
    }

    /// <summary>Feed a pointer position; returns whether this is a drag yet.</summary>
    public bool Observe(Point current)
    {
        if (!HasDragged && MarqueeHitTester.IsDrag(_origin, current)) HasDragged = true;
        return HasDragged;
    }

    /// <summary>Drop the latch at the end of the gesture.</summary>
    public void Reset() => HasDragged = false;
}
