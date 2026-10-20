using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DotCharter.Services.Editing.MultiTrack;

/// <summary>
///     Tracks which non-active arrangements the user has pinned to the
///     stacked-tracks panel. Mirrors <see cref="IGhostArrangementsService"/>
///     but drives the second timeline column instead of ghost overlays.
/// </summary>
public interface IStackedArrangementsService : INotifyPropertyChanged
{
    ObservableCollection<Arrangement> StackedArrangements { get; }
    bool IsStacked(Arrangement arrangement);
    void Toggle(Arrangement arrangement);
    void SetStacked(Arrangement arrangement, bool stacked);
    void Clear();
    event Action? Changed;
}
