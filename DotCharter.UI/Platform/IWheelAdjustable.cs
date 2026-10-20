using Avalonia.Input;

namespace DotCharter.UI.Platform;

/// <summary>
///     A control that would turn a mouse-wheel notch into a value edit. Inside a
///     scroll host it never gets a plain notch — <see cref="SliderWheelPolicy" />
///     scrolls the host instead — only the ones whose modifiers it asks for.
/// </summary>
public interface IWheelAdjustable
{
    /// <summary>
    ///     Whether a notch with these modifiers is an edit of this control rather
    ///     than a scroll of the panel it sits in. Never true for
    ///     <see cref="KeyModifiers.None" />: a bare notch is how a panel scrolls,
    ///     and the pointer crossing a control mid-scroll is not aiming at it.
    /// </summary>
    bool TakesWheel(KeyModifiers modifiers);
}
