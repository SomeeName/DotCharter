using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Session;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     View ▸ Tab string order: the editor's String order (Settings ▸ Editor) as
///     a check next to the 2D timeline. On means Tab (low string at the bottom),
///     off means Rocksmith. It writes the same <c>AppConfig.InvertStrings</c> the
///     settings tab does and leaves Play Mode's own String order alone.
/// </summary>
public sealed class StringOrderToggle : ObservableObject
{
    private readonly ProjectState _state;
    private readonly IProjectSessionService _session;

    public StringOrderToggle(ProjectState state, IProjectSessionService session)
    {
        _state = state;
        _session = session;
        _state.PropertyChanged += OnStateChanged;
    }

    /// <summary>True for Tab order; setting it applies live and saves the config.</summary>
    public bool IsTab
    {
        get => _state.InvertStrings;
        set
        {
            if (_state.InvertStrings == value) return;
            _state.InvertStrings = value;
            var config = _session.AppConfig;
            if (config.InvertStrings == value) return;
            config.InvertStrings = value;
            _session.SaveAppConfig();
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectState.InvertStrings)) OnPropertyChanged(nameof(IsTab));
    }
}
