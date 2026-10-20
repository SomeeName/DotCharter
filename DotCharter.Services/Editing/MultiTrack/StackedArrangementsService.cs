using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DotCharter.Services.Editing.MultiTrack;

public sealed class StackedArrangementsService : ObservableObject, IStackedArrangementsService
{
    private readonly HashSet<Arrangement> _stacked = [];

    public ObservableCollection<Arrangement> StackedArrangements { get; } = [];

    public event Action? Changed;

    public bool IsStacked(Arrangement arrangement) => _stacked.Contains(arrangement);

    public void Toggle(Arrangement arrangement) => SetStacked(arrangement, !_stacked.Contains(arrangement));

    public void SetStacked(Arrangement arrangement, bool stacked)
    {
        if (stacked)
        {
            if (!_stacked.Add(arrangement)) return;
            StackedArrangements.Add(arrangement);
        }
        else
        {
            if (!_stacked.Remove(arrangement)) return;
            StackedArrangements.Remove(arrangement);
        }

        OnPropertyChanged(nameof(StackedArrangements));
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_stacked.Count == 0) return;
        _stacked.Clear();
        StackedArrangements.Clear();
        OnPropertyChanged(nameof(StackedArrangements));
        Changed?.Invoke();
    }
}
