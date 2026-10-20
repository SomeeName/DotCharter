namespace DotCharter.Tone.UI.Views.PlayMode;

/// <summary>
///     Pure math for the rig board's wheel gestures. The board is a single
///     horizontal row, so a plain vertical wheel notch is translated into
///     horizontal travel (Ctrl+wheel zooms instead, via the view model's own
///     clamped <c>Zoom</c>). Kept pure so the direction and the clamp are
///     default-gate testable without a pointer.
/// </summary>
internal static class RigBoardWheelMath
{
    /// <summary>
    ///     Horizontal pixels per wheel notch. One notch ≈ half a typical unit
    ///     card (cards are 182–~300 px wide), so a chain walks card-by-card in
    ///     two notches without teleporting past anything.
    /// </summary>
    public const double NotchPx = 96.0;

    /// <summary>
    ///     The next horizontal offset for a vertical wheel delta. Wheel DOWN
    ///     (negative delta) travels RIGHT — the reading direction of the
    ///     chain — and the result is clamped into <c>[0, max]</c>.
    /// </summary>
    public static double NextOffsetX(double current, double wheelDeltaY, double max)
        => Math.Clamp(current - wheelDeltaY * NotchPx, 0, Math.Max(0, max));
    public static double FitZoom(double current, double viewportWidth, double viewportHeight,
        double extentWidth, double extentHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || extentWidth <= 0 || extentHeight <= 0) return current;
        var factor = Math.Min(viewportWidth / extentWidth, viewportHeight / extentHeight);
        return Math.Clamp(current * factor, .5, 1);
    }
}
