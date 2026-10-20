namespace DotCharter.UI.Layout;

/// <summary>
/// The AI chat column's width: what a first show uses, what a saved width may be,
///     and what is written back on close. Pure, so the rules are tested without a window.
/// </summary>
public static class AiChatColumnPolicy
{
    /// <summary>The column a first show gets: the pane's own 420 px design width less its seam.</summary>
    public const double DefaultWidthPx = 400;

    /// <summary>The narrowest the pane is built for (its gallery pages test 340 px).</summary>
    public const double MinWidthPx = 320;

    /// <summary>What the editor keeps when the splitter is dragged all the way left.</summary>
    public const double MinEditorWidthPx = 480;

    public const double SplitterPx = 6;

    /// <summary>A saved width when it is a usable one, else <see cref="DefaultWidthPx" />.</summary>
    public static double Restored(double? savedPx) =>
        savedPx is { } px && double.IsFinite(px) && px >= MinWidthPx ? px : DefaultWidthPx;

    /// <summary>
    ///     The width to persist: the column's own while it shows, else the one it had when it
    ///     was hidden (a hidden column measures 0). Null during a run, which hides the column
    ///     itself (<see cref="ShellGeometryPersistPolicy" />).
    /// </summary>
    public static double? ToPersist(double actualPx, bool visible, bool inRun, double rememberedPx)
    {
        if (inRun) return null;
        return visible && actualPx >= MinWidthPx ? actualPx : Restored(rememberedPx);
    }
}
