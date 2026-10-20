namespace DotCharter.UI.Views.Shell;

/// <summary>
///     How short the splitter may make the editor's top area (the sheet strip, the timeline and the
///     song map) before the bottom dock takes the rest. It has to cover what that area REFUSES to be
///     smaller than: the timeline's own <see cref="TimelineMinPx" /> row (<c>SheetSplitGrid</c>), the
///     52 px song map docked under it, and the sheet strip when open.
///     <para>
///         It was a flat 320 px, sized for a 40 px song map and short of the timeline's own 360 on its
///         own. Dragging the splitter up past that squeezed the row below the timeline's minimum, so
///         the timeline was arranged taller than its row: it painted over the song map and ran on
///         under the inspector.
///     </para>
/// </summary>
internal static class TimelineAreaMinHeight
{
    /// <summary>The timeline row's own minimum in <c>MainWindow.axaml</c> (<c>SheetSplitGrid</c>).</summary>
    public const double TimelineMinPx = 360;

    /// <summary>The song map strip's height in <c>MainWindow.axaml</c> (<c>Border.MinimapFrame</c>).</summary>
    public const double SongMapPx = 52;

    /// <summary>The splitter between the sheet strip and the timeline.</summary>
    public const double SheetSplitterPx = 6;

    public static double For(bool sheetVisible, bool songMapShown, bool gameplayPresentation) =>
        gameplayPresentation
            ? 0
            : TimelineMinPx
              + (songMapShown ? SongMapPx : 0)
              + (sheetVisible ? SheetStripHeightPolicy.MinPx + SheetSplitterPx : 0);
}
