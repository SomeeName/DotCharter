using Avalonia.Controls;

namespace DotCharter.UI.Views.PlayMode.Screens;

public partial class SongSelectDetailRailView : UserControl
{
    /// <summary>What the picked song's details keep of the rail at the least:
    /// the pinned Play button and a scrollable strip of the details above it.</summary>
    public const double DetailsReserve = 170;

    /// <summary>The share of the rail the setlist panel may always take, however
    /// short the window.</summary>
    public const double SetlistPanelShare = 0.45;

    public SongSelectDetailRailView()
    {
        InitializeComponent();
        SongSelectRailGrid.SizeChanged += (_, e) =>
            SongSelectSetlistHost.MaxHeight = SetlistPanelMaxHeight(e.NewSize.Height)
                                              - SongSelectSetlistHost.Margin.Top;
    }

    /// <summary>The setlist panel's height cap for a rail
    /// <paramref name="railHeight" /> tall: everything but
    /// <see cref="DetailsReserve" />, and never less than
    /// <see cref="SetlistPanelShare" /> of the rail. Past the cap the panel's
    /// middle scrolls; its header, Add and Play stay put.</summary>
    public static double SetlistPanelMaxHeight(double railHeight) =>
        Math.Max(0, Math.Max(railHeight * SetlistPanelShare, railHeight - DetailsReserve));
}
