using Avalonia.Input;

namespace DotCharter.UI.Input;

/// <summary>
///     What the pointer over the timeline should LOOK like — pure, off the same
///     <see cref="TimelineZones" /> the ToolCoordinator routes by, so the
///     cursor can never promise a different gesture than the click delivers.
///     No permanent crosshair over the whole timeline grid (the event rows,
///     the editing bands and the ruler included).
///     <para>
///         Deliberate omission: NO resize cursor on a note's sustain edge —
///         there is no sustain edge-drag gesture (sustain is Alt+wheel / the
///         nudge keys), and a size cursor there would advertise one.
///     </para>
/// </summary>
public static class TimelineCursorPolicy
{
    public static StandardCursorType For(
        double y, double viewportHeight, double topMargin, double bottomMargin, bool panning)
    {
        if (panning) return StandardCursorType.SizeAll;
        if (TimelineZones.IsTimeRuler(y, viewportHeight)) return StandardCursorType.Arrow;
        if (y < topMargin) return StandardCursorType.Arrow; // event/ribbon rows: pick & drag chips
        if (TimelineZones.IsHandShapeBand(y, viewportHeight, bottomMargin) ||
            TimelineZones.IsFhpBand(y, viewportHeight, bottomMargin))
        {
            return StandardCursorType.SizeWestEast; // length-editing bands
        }

        return StandardCursorType.Cross; // the lanes: precise placement
    }

    /// <summary>One cached <see cref="Cursor" /> per type — a cursor per
    /// pointer-move is an allocation at input rate.</summary>
    public static Cursor Cached(StandardCursorType type)
    {
        var index = (int)type;
        var cached = Cache;
        if (index >= cached.Length) return new Cursor(type);
        return cached[index] ??= new Cursor(type);
    }

    private static readonly Cursor?[] Cache = new Cursor?[64];
}

/// <summary>
///     Which press starts a viewport PAN. Middle-drag was the only free pan and
///     had zero affordance (P1-4); Alt+left-drag is the discoverable alias.
///     Space+drag was REJECTED: Space is the play/pause chord and fires on
///     KeyDown at the window tunnel, so holding it to pan would toggle
///     playback. Alt is already the editor's "manipulate the view/value"
///     modifier (Alt+wheel), and Alt+left conflicts with nothing — the plain
///     left verbs use Ctrl/Shift only.
/// </summary>
public static class TimelinePanGesture
{
    public static bool IsPanStart(bool middlePressed, bool leftPressed, bool alt) =>
        middlePressed || (leftPressed && alt);
}
