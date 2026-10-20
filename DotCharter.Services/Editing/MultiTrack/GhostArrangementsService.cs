using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Services.Editing.MultiTrack;

public sealed class GhostArrangementsService : ObservableObject, IGhostArrangementsService
{
    private readonly HashSet<Arrangement> _visible = [];

    public ObservableCollection<Arrangement> VisibleGhosts { get; } = [];

    public event Action? Changed;

    public bool IsVisible(Arrangement arrangement) => _visible.Contains(arrangement);

    public void Toggle(Arrangement arrangement) => SetVisible(arrangement, !_visible.Contains(arrangement));

    public void SetVisible(Arrangement arrangement, bool visible)
    {
        if (visible)
        {
            if (!_visible.Add(arrangement)) return;
            VisibleGhosts.Add(arrangement);
        }
        else
        {
            if (!_visible.Remove(arrangement)) return;
            VisibleGhosts.Remove(arrangement);
        }

        OnPropertyChanged(nameof(VisibleGhosts));
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_visible.Count == 0) return;
        _visible.Clear();
        VisibleGhosts.Clear();
        OnPropertyChanged(nameof(VisibleGhosts));
        Changed?.Invoke();
    }
}
