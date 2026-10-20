using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DotCharter.Services.Editing.MultiTrack;

/// <summary>
///     Tracks which non-active arrangements should be drawn on the highway as
///     dimmed "ghost" overlays. The renderer projects their notes/chords on top
///     of the active arrangement so the user can see and edit across tracks.
/// </summary>
public interface IGhostArrangementsService : INotifyPropertyChanged
{
    ObservableCollection<Arrangement> VisibleGhosts { get; }
    bool IsVisible(Arrangement arrangement);
    void Toggle(Arrangement arrangement);
    void SetVisible(Arrangement arrangement, bool visible);
    void Clear();
    event Action? Changed;
}
