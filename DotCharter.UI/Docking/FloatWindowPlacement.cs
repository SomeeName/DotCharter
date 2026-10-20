using Avalonia;

namespace DotCharter.UI.Docking;

/// <summary>
///     Whether a persisted float-window origin still lands somewhere the user can
///     reach it.
///     <para>
///         <b>The bug this exists to end.</b>
///         <c>DockLayoutController.SanitizeBound</c> already rejects the shapes
///         JSON invents — NaN, infinity, 1e12 — because "a window restored at NaN
///         is nowhere and the pane simply reads as lost". A PERFECTLY VALID
///         coordinate reads identically: float the Inspector onto a second
///         monitor at x = 2560, quit, unplug the monitor (or dock the laptop
///         elsewhere, or plug the second screen in on the other side so it
///         becomes negative), and next launch the pane is restored at 2560 with
///         nothing there to draw it on. The View menu says the Inspector is
///         open, the dock is empty, and the only cure is View → Reset Layout —
///         which the user has no reason to suspect. The sanitizer's ±20,000
///         clamp is a garbage filter, not a placement rule, and it never asked
///         the machine what monitors exist.
///     </para>
///     Pure: the placement decision is geometry over a screen list, so it is
///     assertable in the default gate without a windowing platform.
/// </summary>
internal static class FloatWindowPlacement
{
    /// <summary>The strip of window the user must be able to hit to move it back
    /// — a title bar's worth. Deliberately small: this is a "can I get at it at
    /// all" test, not a "is it fully visible" one, so a window the user
    /// deliberately parked half off the edge of a screen stays where they put
    /// it.</summary>
    public const double GrabWidth = 160;

    public const double GrabHeight = 32;

    /// <summary>
    ///     Whether a window placed at <paramref name="x" />/<paramref name="y" />
    ///     would have its title-bar strip on one of <paramref name="screens" />.
    ///     <para>An EMPTY screen list means "the platform did not say" — headless,
    ///     a test, a top-level that is not up yet — and is answered YES. Refusing
    ///     to restore a float window because the screen list was unavailable
    ///     would be a worse bug than the one this prevents.</para>
    /// </summary>
    public static bool IsReachable(double x, double y, IReadOnlyList<PixelRect> screens)
    {
        if (screens.Count == 0) return true;
        var grab = new Rect(x, y, GrabWidth, GrabHeight);
        foreach (var screen in screens)
        {
            if (screen.Width <= 0 || screen.Height <= 0) continue;
            if (grab.Intersects(new Rect(screen.X, screen.Y, screen.Width, screen.Height))) return true;
        }

        return false;
    }
}
