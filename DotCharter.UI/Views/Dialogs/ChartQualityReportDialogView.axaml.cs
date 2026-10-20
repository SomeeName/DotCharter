using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.UI.Views.Dialogs;

public partial class ChartQualityReportDialogView : UserControl
{
    /// <summary>Bound to <see cref="IChartQualityReportScreen.FocusEofRoundTrip" />.</summary>
    public static readonly StyledProperty<bool> ScrollsToEofRoundTripProperty =
        AvaloniaProperty.Register<ChartQualityReportDialogView, bool>(nameof(ScrollsToEofRoundTrip));

    /// <summary>Bound to <see cref="IChartQualityReportScreen.HasVerdict" />.</summary>
    public static readonly StyledProperty<bool> HasVerdictProperty =
        AvaloniaProperty.Register<ChartQualityReportDialogView, bool>(nameof(HasVerdict));

    public ChartQualityReportDialogView() => InitializeComponent();

    public bool ScrollsToEofRoundTrip
    {
        get => GetValue(ScrollsToEofRoundTripProperty);
        set => SetValue(ScrollsToEofRoundTripProperty, value);
    }

    /// <summary>
    /// Opened from a lossy Editor on Fire export's Show: once the report has laid out,
    ///     scroll its round trip to the top of the body.
    /// </summary>
    public bool HasVerdict
    {
        get => GetValue(HasVerdictProperty);
        set => SetValue(HasVerdictProperty, value);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (ScrollsToEofRoundTrip) Dispatcher.UIThread.Post(ShowEofRoundTrip, DispatcherPriority.Loaded);
    }

    /// <summary>The report lands after the dialog opened and pushes the round trip down: bring it back.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HasVerdictProperty && HasVerdict && ScrollsToEofRoundTrip && IsLoaded)
            Dispatcher.UIThread.Post(ShowEofRoundTrip, DispatcherPriority.Loaded);
    }

    internal void ShowEofRoundTrip()
    {
        var section = EofRoundTripSection;
        var height = Math.Min(section.Bounds.Height, QualityBody.Viewport.Height);
        section.BringIntoView(new Rect(0, 0, section.Bounds.Width, height));
    }
}
