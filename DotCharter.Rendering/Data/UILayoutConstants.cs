namespace DotCharter.Rendering.Data;

/// <summary>
///     Constants for UI layout and positioning
/// </summary>
public static class UiLayoutConstants
{
    // Highway margins. NOTE: TopMargin is not a constant here (there is no fixed
    // normal / no-lyrics pair); it is computed per-track by
    // <see cref="DotCharter.UI.Editing.Tracks.RibbonLayout"/> from the
    // visible ribbon rows. The bottom margin is still fixed.
    // Sized to stack the handshape bar (offset 2, h 12) and the FHP/anchor
    // lane (offset 18, h 18) ABOVE the time ruler (ticks H-20): the FHP lane
    // ends at laneBottom+36 = H-22, a 2px gap over the ruler. Was 40 while the
    // two lanes overlapped at a single Y (see ChartEditorConstants.FhpYOffset).
    public const double HighwayBottomMargin = 58.0;

    // Chord box dimensions (2D timeline rendering)
    public const float ChordBoxWidth = 22f;
    public const float ChordBoxHalfWidth = 11f;
    public const float ChordBoxHeight = 16f;
    public const float ChordBoxHalfHeight = 8f;
}
