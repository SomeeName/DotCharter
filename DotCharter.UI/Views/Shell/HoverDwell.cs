namespace DotCharter.UI.Views.Shell;

/// <summary>
///     Dwell state for a popup a hover opens (the toolbar's undo/redo history
/// previews). They opened on <c>PointerEntered</c> with no dwell, so a
///     pointer sweeping across the toolbar opened and closed two real popups per
///     pass. A hover now only ARMS the open; the caller's timer opens it when the
///     dwell elapses with the pointer still there. Pure, so the rule is tested
///     without a window.
/// </summary>
internal sealed class HoverDwell<T> where T : class
{
    /// <summary>The anchor waiting for its dwell, or null.</summary>
    public T? Pending { get; private set; }

    /// <summary>
    ///     The pointer entered <paramref name="anchor" />. True means open NOW: one of
    ///     these popups is already open, so the user is browsing them and a second
    ///     wait would only lag. False means the dwell is armed; start the timer.
    /// </summary>
    public bool Enter(T anchor, bool aPopupIsOpen)
    {
        Pending = aPopupIsOpen ? null : anchor;
        return aPopupIsOpen;
    }

    /// <summary>The pointer left <paramref name="anchor" />: a pending open for it is cancelled.</summary>
    public void Exit(T anchor)
    {
        if (ReferenceEquals(Pending, anchor)) Pending = null;
    }

    /// <summary>The dwell elapsed: the anchor to open, or null when the pointer left first.</summary>
    public T? Elapsed()
    {
        var anchor = Pending;
        Pending = null;
        return anchor;
    }
}
