using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     Which difficulty level of the active arrangement the editor shows, and the way back to the
///     hardest. A finding's Go to (the Inspector, the Chart quality report) can switch to an easier
///     level, and the editor had no control to switch back: only changing arrangement did. The
///     Tracks popover carries it, and the toolbar shows the level while it is not the hardest.
/// </summary>
public sealed partial class DifficultyLevelSelector : ObservableObject
{
    private readonly ProjectState _state;

    public DifficultyLevelSelector(ProjectState state)
    {
        _state = state;
        _state.PropertyChanged += OnStateChanged;
    }

    /// <summary>Levels of the active arrangement.</summary>
    public int LevelCount => _state.ActiveArrangement?.Levels.Count ?? 0;

    /// <summary>More than one level, so there is something to pick.</summary>
    public bool HasLevels => LevelCount > 1;

    /// <summary>The active level's place from easiest (1) to hardest (<see cref="LevelCount" />).</summary>
    public int LevelNumber => Ordered().IndexOf(_state.ActiveLevel!) + 1;

    /// <summary>The editor shows an easier level than the hardest: the toolbar says so.</summary>
    public bool IsBelowHardest => HasLevels && _state.ActiveLevel is { } level
                                           && !ReferenceEquals(level, ChartLevels.Hardest(_state.ActiveArrangement));

    [RelayCommand(CanExecute = nameof(CanGoEasier))]
    private void Easier() => Step(-1);

    [RelayCommand(CanExecute = nameof(CanGoHarder))]
    private void Harder() => Step(+1);

    [RelayCommand(CanExecute = nameof(IsBelowHardest))]
    private void Hardest()
    {
        if (ChartLevels.Hardest(_state.ActiveArrangement) is { } hardest) _state.ActiveLevel = hardest;
    }

    private bool CanGoEasier() => HasLevels && LevelNumber > 1;

    private bool CanGoHarder() => HasLevels && LevelNumber is > 0 && LevelNumber < LevelCount;

    private void Step(int direction)
    {
        var ordered = Ordered();
        var next = ordered.IndexOf(_state.ActiveLevel!) + direction;
        if (next >= 0 && next < ordered.Count) _state.ActiveLevel = ordered[next];
    }

    /// <summary>Easiest first, by <see cref="ChartLevel.DifficultyIndex" />; a tie keeps list order.</summary>
    private List<ChartLevel> Ordered() =>
        _state.ActiveArrangement is { } arrangement
            ? [.. arrangement.Levels.Select((level, i) => (level, i)).OrderBy(p => p.level.DifficultyIndex).ThenBy(p => p.i)
                .Select(p => p.level)]
            : [];

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ProjectState.ActiveLevel) or nameof(ProjectState.ActiveArrangement)
            or nameof(ProjectState.ActiveProject)))
        {
            return;
        }

        OnPropertyChanged(nameof(LevelCount));
        OnPropertyChanged(nameof(HasLevels));
        OnPropertyChanged(nameof(LevelNumber));
        OnPropertyChanged(nameof(IsBelowHardest));
        EasierCommand.NotifyCanExecuteChanged();
        HarderCommand.NotifyCanExecuteChanged();
        HardestCommand.NotifyCanExecuteChanged();
    }
}
