namespace DotCharter.UI.Input;

/// <summary>
///     What a BARE notch does when the edit chain claimed nothing — the pointer
///     is over no note, hand shape, lyric or preview.
/// </summary>
public enum TimelineWheelFallback
{
    /// <summary>Nothing at all. The wheel is an EDIT gesture over the lanes.</summary>
    None,

    /// <summary>Pan the viewport along time.</summary>
    Pan
}

/// <summary>What a wheel notch over the timeline is FOR, decided by modifier.</summary>
public enum TimelineWheelRoute
{
    /// <summary>Ctrl+wheel: zoom around the pointer (Follow off) or the playhead.</summary>
    Zoom,

    /// <summary>Plain (and Shift) wheel: the edit-adjust chain
    /// (<see cref="TimelineWheelAdjuster" /> — selected sustain, hand-shape
    /// length, preview fret, preview FHP fret, lyric length).</summary>
    Adjust,

    /// <summary>Alt+wheel: pan the viewport along time. Never a transport seek,
    /// never an edit.</summary>
    Pan
}

/// <summary>
///     The ONE routing decision for a wheel notch over the timeline.
///     <para>
///         Plain wheel edits and Alt pans. Flipping that (plain wheel pans, Alt edits)
///         was tried because a persisting selection means a wheel meant to scroll silently
///         rewrites the selected note's sustain, and rejected: the wheel-edits chain is the charting workflow
///         the owner has muscle memory for and it must stay the bare gesture. Pan is
///         the opt-in instead, on Alt — which keeps Alt as the coherent VIEW
///         modifier (Alt+wheel pans exactly like Alt+left-drag pans). Do not
///         re-flip this without an explicit owner decision.
///     </para>
///     <para>
///         Shift is deliberately not consulted: precision touchpads report a
///         Shift-wheel as a horizontal delta and mice report it as vertical, so
///         claiming Shift for a verb would make the gesture device-dependent.
///         Shift+wheel adjusts, same as plain.
///     </para>
/// </summary>
public static class TimelineWheelRouter
{
    public static TimelineWheelRoute Classify(bool ctrl, bool alt)
    {
        if (ctrl) return TimelineWheelRoute.Zoom;
        if (alt) return TimelineWheelRoute.Pan;
        return TimelineWheelRoute.Adjust;
    }

    /// <summary>
    ///     A bare notch the edit chain did not claim does NOTHING.
    ///     <para>
    ///         <b>Owner decision</b>, and the reason is EOF: there the
    ///         wheel is reserved for length — hand shape, note sustain, lyric —
    ///         and never moves the view. Falling back to panning would let
    ///         the same gesture edit or scroll depending on where
    ///         the pointer happens to sit, so a wheel that misses the note by a
    ///         lane would scroll the chart out from under the charter. Panning is
    ///         Alt+wheel (and Alt+left-drag), zoom is Ctrl+wheel.
    ///     </para>
    /// </summary>
    public static TimelineWheelFallback UnclaimedBareNotch => TimelineWheelFallback.None;

    /// <summary>
    ///     How many notches a wheel event is worth, from its VERTICAL delta.
    ///     <c>0</c> means "not a notch" and the handler must do nothing.
    ///     <para>
    ///         The call site read <c>e.Delta.Y &gt; 0 ? 1 : -1</c>, which turns a
    ///         delta of exactly <c>0</c> into a DOWNWARD notch. A precision
    ///         touchpad reports a horizontal two-finger scroll as
    ///         <c>Delta = (±x, 0)</c> — and, per the table above, a
    ///         <b>Shift+wheel</b> on one too, which is precisely why Shift is
    ///         deliberately not consulted. So every horizontal flick over the
    ///         lanes shortened the selected sustain (or the hovered hand shape,
    ///         or the lyric) by one grid division, and Ctrl+horizontal zoomed
    ///         OUT — an edit and a view change from a gesture that carries no
    ///         vertical intent at all.
    ///     </para>
    /// </summary>
    public static int NotchesFrom(double deltaY)
    {
        if (double.IsNaN(deltaY) || deltaY == 0) return 0;
        return deltaY > 0 ? 1 : -1;
    }
}
