using Avalonia.Controls;

namespace DotCharter.Tone.UI.Views.PlayMode.RigEditors;

/// <summary>The amp family editor: the hardware head with its face knobs and lamp.</summary>
public partial class RigAmpFaceView : UserControl
{
    /// <summary>The gear column's design width (the Grid's MaxWidth) and the
    /// capture card's floor. Kept here rather than derived from the columns so
    /// the arithmetic cannot feed back into the layout that calls it.</summary>
    internal const double GearMaxWidth = 1180;
    internal const double CaptureMinWidth = 300;
    internal const double ColumnGap = 18; // FireGapLeftLg on the card

    public RigAmpFaceView() => InitializeComponent();

    /// <summary>The capture library takes whatever the gear does not: at the
    /// owner's ~1650 px dock that is ~450 px beside a 1180 px head; in a narrow
    /// dock it keeps its 300 px floor and the head gives way. Pure in
    /// <see cref="CaptureWidthFor" /> so the rule is pinned without a layout.</summary>
    private void OnColumnsSizeChanged(object? sender, SizeChangedEventArgs e)
        => RigCapturePanel.Width = CaptureWidthFor(e.NewSize.Width);

    internal static double CaptureWidthFor(double hostWidth)
        => Math.Max(CaptureMinWidth, hostWidth - GearMaxWidth - ColumnGap);
}
