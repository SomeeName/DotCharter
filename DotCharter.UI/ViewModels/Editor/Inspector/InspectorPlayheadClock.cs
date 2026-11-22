using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
/// The Note tab footer's playhead time (inspector design D): time only, <c>m:ss.cc</c>, beside the queue's
///     arrows. It refreshes exactly when <c>InspectorViewModel.PlayheadLabel</c> does (the same two predicates):
///     it follows the playhead while stopped and catches up once when playback stops; during playback it holds
///     still instead of re-formatting at the playhead's 250 Hz (the status rail runs the live clock). Exposed as
///     <c>InspectorViewModel.Clock</c>.
/// </summary>
public sealed class InspectorPlayheadClock : ObservableObject, IDisposable
{
    private static readonly PropertyChangedEventArgs TimeLabelChangedArgs = new(nameof(TimeLabel));

    private readonly ProjectState _projectState;
    private readonly TransportViewModel _transport;

    public InspectorPlayheadClock(ProjectState projectState, TransportViewModel transport)
    {
        _projectState = projectState;
        _transport = transport;
        _projectState.PropertyChanged += OnProjectStateChanged;
        _transport.PropertyChanged += OnTransportChanged;
    }

    /// <summary>Where the playhead is: "1:24.37".</summary>
    public string TimeLabel => Format(_projectState.CurrentTimeMs);

    /// <summary>Minutes unpadded, seconds and hundredths padded; a negative time reads 0:00.00.</summary>
    internal static string Format(double ms)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 10:00}");
    }

    public void Dispose()
    {
        _projectState.PropertyChanged -= OnProjectStateChanged;
        _transport.PropertyChanged -= OnTransportChanged;
    }

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (InspectorViewModel.ProjectStateChangeRefreshesPlayheadLabel(e.PropertyName, _transport.IsPlaying))
            OnPropertyChanged(TimeLabelChangedArgs);
    }

    private void OnTransportChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransportViewModel.IsPlaying)) OnPropertyChanged(TimeLabelChangedArgs);
    }
}
