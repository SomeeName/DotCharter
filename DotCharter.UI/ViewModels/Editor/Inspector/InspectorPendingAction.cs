using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     Inspector design D's inline confirmation: a card's button that changes a lot at once (Auto-fix, a reset) asks
///     first, with a sentence and Confirm / Cancel under its buttons (<c>InspectorConfirmRow</c>). The question is
///     withdrawn when what it is about moves: a project or arrangement switch, or any history step, so a Confirm never
///     applies to a chart the sentence did not describe. A disposable owner disposes it, which ends those subscriptions.
/// </summary>
public sealed partial class InspectorPendingAction : ObservableObject, IDisposable
{
    private Action? _confirm;
    private ProjectState? _state;
    private IHistoryService? _history;

    /// <summary>The question on screen, or "" when nothing waits.</summary>
    [ObservableProperty] private string _sentence = "";

    /// <summary>A question waits for Confirm or Cancel.</summary>
    public bool IsPending => _confirm != null;

    /// <summary>Withdraws the question whenever the project, the arrangement or the history moves.</summary>
    public InspectorPendingAction ClearOn(ProjectState state, IHistoryService? history)
    {
        Dispose();
        _state = state;
        _history = history;
        state.PropertyChanged += OnStateChanged;
        history?.HistoryChanged += Clear;
        return this;
    }

    /// <summary>Ends the <see cref="ClearOn" /> subscriptions: the project state and the history outlive the owner.</summary>
    public void Dispose()
    {
        _state?.PropertyChanged -= OnStateChanged;
        _history?.HistoryChanged -= Clear;
        _state = null;
        _history = null;
    }

    private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement)) Clear();
    }

    /// <summary>Puts <paramref name="sentence" /> on screen; Confirm runs <paramref name="confirm" />.</summary>
    public void Ask(string sentence, Action confirm)
    {
        _confirm = confirm;
        Sentence = sentence;
        OnPropertyChanged(nameof(IsPending));
    }

    /// <summary>Withdraws the question without running it.</summary>
    public void Clear()
    {
        if (_confirm == null) return;
        _confirm = null;
        Sentence = "";
        OnPropertyChanged(nameof(IsPending));
    }

    [RelayCommand]
    private void Confirm()
    {
        var confirm = _confirm;
        Clear();
        confirm?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Clear();
}
