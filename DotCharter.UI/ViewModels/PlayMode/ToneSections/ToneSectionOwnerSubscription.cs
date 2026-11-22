using System.ComponentModel;

namespace DotCharter.UI.ViewModels.PlayMode.ToneSections;

/// <summary>Forwards the host's existing owner notifications and releases every subscription.</summary>
internal sealed class ToneSectionOwnerSubscription : IDisposable
{
    private readonly ProjectState _state;
    private readonly Action _changed;

    public ToneSectionOwnerSubscription(ProjectState state, Action changed)
    {
        _state = state;
        _changed = changed;
        state.PropertyChanged += OnPropertyChanged;
        state.AudioLoaded += changed;
        state.AudioCleared += changed;
    }

    private void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement) or
            nameof(ProjectState.SongAudioRevision))
        {
            _changed();
        }
    }

    public void Dispose()
    {
        _state.PropertyChanged -= OnPropertyChanged;
        _state.AudioLoaded -= _changed;
        _state.AudioCleared -= _changed;
    }
}
