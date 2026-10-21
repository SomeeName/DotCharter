using Avalonia.Controls;

namespace DotCharter.UI.Views.Shell;

/// <summary>
/// The chart editor's default timeline-over-dock split: 3 : 4. ONE source for the
///     three places that set it — the <c>MainSplitGrid</c> rows in
///     <c>MainWindow.axaml</c> (<c>x:Static</c>), <c>MainWindowLayoutController</c>'s
///     starting and fallback dock height, and View ▸ Reset Layout — so "reset" means
///     "back to how it started".
///     <para>
///         <b>Why 3 : 4.</b> The owner's pick is
///         the LOOK at 1920x1080: about 360 px of timeline (412 with the song map) over
///         550 of dock. 2 : 3 never produced it — its timeline share of the ~968 px
///         split is 385, under the top area's 412 floor, so the floor wins and the dock
///         takes every other pixel. And the Auto UI scale holds that split at ~968 px
///         on every taller window (1440p is 1.35x), so under 2 : 3 the timeline sits on
///         its 360 px floor at 1080p AND 1440p and never grows. 3 : 4 is that same 412 : 550 as a
///         ratio: identical at 1080p, and both rows grow together on any taller split.
///     </para>
/// </summary>
public static class EditorSplitDefaults
{
    public static GridLength TimelineRow { get; } = new(3, GridUnitType.Star);

    public static GridLength DockRow { get; } = new(4, GridUnitType.Star);
}
