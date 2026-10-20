using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// The chart editor's default timeline-over-dock split: 2 : 3, the owner's
///     pick from side-by-side captures (about 360 / 550 px at 1920x1080 — six string
///     lanes and the song map above, the Inspector's whole body and a readable 3D
///     preview below). ONE source for the three places that set it — the
///     <c>MainSplitGrid</c> rows in <c>MainWindow.axaml</c> (<c>x:Static</c>),
///     <c>MainWindowLayoutController</c>'s starting and fallback dock height, and
///     View ▸ Reset Layout — so "reset" means "back to how it started".
/// </summary>
public static class EditorSplitDefaults
{
    public static GridLength TimelineRow { get; } = new(2, GridUnitType.Star);

    public static GridLength DockRow { get; } = new(3, GridUnitType.Star);
}
