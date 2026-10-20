using Avalonia;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.UI.Input.Tools;

/// <summary>
/// Drawing a NEW bar on the hand-shape lane with the right button: armed on the
///     press over empty lane, previewed while the pointer drags, committed on the release.
///     <list type="bullet">
///         <item>A DRAG (past <see cref="GestureDragThreshold" />, the one click-vs-drag
///         number) spans the two snapped ends, and the preview bar prints the grip it
///         will carry.</item>
///         <item>A plain CLICK spans from the cursor to the next fret change or chord
///         (<see cref="HandShapeLaneSpan.ClickEnd" />). A bar exactly one
///         beat long would have to be resized by the charter afterwards.</item>
///     </list>
///     Either way the bar and any template its grip mints are ONE
///     <c>IHighwayEditingService.AddHandShape</c> — one undo node. Pure of
///     <c>ToolContext</c> so the default gate can drive it.
/// </summary>
public sealed class HandShapeDrawGesture(
    IFingeringGenerationService fingering, IHighwayEditingService editing)
{
    private readonly GestureDragThreshold _threshold = new();
    private FractionalPosition _anchor;
    private TrackEditorContext? _track;

    public bool IsArmed => _track != null;

    public void Arm(TrackEditorContext track, Point origin, FractionalPosition anchor)
    {
        _track = track;
        _anchor = anchor;
        _threshold.Arm(origin);
    }

    /// <summary>
    ///     The bar a drag to <paramref name="current" /> would draw, labelled with its grip
    ///     (name and frets, in <see cref="HandShape.Name" /> — the preview is a throwaway
    ///     the renderer prints), or null while the gesture is still a click.
    /// </summary>
    public HandShape? Preview(Point pointer, FractionalPosition current)
    {
        if (_track is not { } track || !_threshold.Observe(pointer)) return null;
        if (HandShapeLaneSpan.DragSpan(track.HandShapes, _anchor, current) is not { } span) return null;

        var minted = new List<ChordTemplate>();
        var bar = HandShapeLaneSpan.BuildBar(track.Arrangement, fingering, track.Notes, track.Chords,
            span.Start, span.End, minted);
        var grip = minted.FirstOrDefault(t => t.Id == bar.TemplateId)
                   ?? ChordTemplateResolver.FindById(track.Arrangement, bar.TemplateId);
        bar.Name = HandShapeLaneSpan.GripLabel(grip, track.StringCount);
        return bar;
    }

    /// <summary>Commits the bar the gesture drew and disarms. Returns the bar, or null
    /// when nothing fits (the gesture is then a no-op, never a zero-length bar).</summary>
    public HandShape? Release(Point pointer, FractionalPosition current)
    {
        if (_track is not { } track) return null;
        var dragged = _threshold.Observe(pointer);
        Cancel();

        var span = dragged ? HandShapeLaneSpan.DragSpan(track.HandShapes, _anchor, current) : null;
        if (span == null)
        {
            // A click — or a drag that never left its grid cell, which means the same.
            if (HandShapeLaneSpan.ClickEnd(track.Arrangement, track.Notes, track.Chords, track.HandShapes,
                    track.Anchors, _anchor) is not { } end)
            {
                return null;
            }

            span = (_anchor, end);
        }

        var minted = new List<ChordTemplate>();
        var bar = HandShapeLaneSpan.BuildBar(track.Arrangement, fingering, track.Notes, track.Chords,
            span.Value.Start, span.Value.End, minted);
        editing.AddHandShape(bar, minted);
        return bar;
    }

    public void Cancel()
    {
        _track = null;
        _threshold.Reset();
    }
}
