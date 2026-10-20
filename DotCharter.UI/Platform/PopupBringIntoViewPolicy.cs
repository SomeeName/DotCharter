using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace DotCharter.UI.Platform;

/// <summary>
///     A bring-into-view request raised INSIDE a popup stops at the popup.
///     <para>
///         Opening a dropdown makes its list bring the selected item into view.
///         The list's own scroller answers that — and then the request keeps
///         bubbling: a popup's content routes events through the
///         <see cref="Popup" />, its LOGICAL parent, so the request walks on up
///         through the ComboBox into whatever scroll host the ComboBox sits in.
///         With <c>OverlayPopups</c> on, the popup is in the same window, so that
///         host can map the item into its own content and "reveal" it — and it
///         does so while the popup is still being placed, from the item's
///         first, unplaced position. Opening the Mixer tab's MIDI synth ▸
///         Instrument dropdown scrolled the inspector up ~60 px under the
/// user's pointer, though the ComboBox was fully visible.
///     </para>
///     <para>
///         The host can never reveal popup content anyway — a popup is placed
///         against its target, not scrolled with the page — so nothing outside
///         a popup may act on a request from inside it. Requests from a host's
///         own descendants (keyboard focus moving to an off-screen control, a
///         TextBox caret) do not pass through a popup and are untouched.
///     </para>
///     <para>
///         A class handler on <see cref="Popup" />, bubble: every scroller INSIDE
///         the popup has already had the request by the time it reaches the
///         Popup element.
///     </para>
/// </summary>
public static class PopupBringIntoViewPolicy
{
    private static readonly object Gate = new();
    private static bool _installed;

    /// <summary>
    ///     Pure half: may a scroll host outside a popup act on a bring-into-view
    ///     request? Only when it did NOT come from inside the popup.
    /// </summary>
    public static bool StopsAtThePopup(bool raisedInsideThePopup) => raisedInsideThePopup;

    /// <summary>
    ///     Installs the policy for the process. Idempotent; call it before any
    ///     view is built (<c>App.Initialize</c>).
    /// </summary>
    public static void Apply()
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;
            Control.RequestBringIntoViewEvent.AddClassHandler<Popup>(
                static (popup, e) => Guard(popup, e),
                RoutingStrategies.Bubble);
        }
    }

    private static void Guard(Popup popup, RequestBringIntoViewEventArgs e)
    {
        if (e.Handled) return;
        if (!StopsAtThePopup(IsInside(popup, e.Source as Visual))) return;
        e.Handled = true;
    }

    /// <summary>The popup's content is not its VISUAL child (it lives in a popup
    /// host or the overlay layer), so "inside" is: the source is the popup's
    /// child or one of that child's visual descendants.</summary>
    private static bool IsInside(Popup popup, Visual? source) =>
        source != null && popup.Child is { } child &&
        (ReferenceEquals(source, child) || child.IsVisualAncestorOf(source));
}
