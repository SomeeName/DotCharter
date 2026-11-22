using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     Nudges the selected notes, chords, hand shapes and anchors earlier or later in time.
///     The Edit menu, the command palette and the keys all come here; the outcome is said on
///     the status rail, and the menu items are disabled while nothing is selected.
/// </summary>
public sealed partial class TimeNudgeViewModel : ObservableObject
{
    private readonly IHighwayEditingService _editing;
    private readonly ILocalizationService _localization;
    private readonly ProjectState _projectState;
    private readonly TransportViewModel _transport;

    public TimeNudgeViewModel(IHighwayEditingService editing, ISelectionService selection,
        TransportViewModel transport, ProjectState projectState, ILocalizationService localization)
    {
        _editing = editing;
        _transport = transport;
        _projectState = projectState;
        _localization = localization;
        selection.SelectionChanged += (_, _) => RaiseCanExecuteChanged();
    }

    /// <summary>True when something a time nudge can move is selected.</summary>
    public bool HasSelection => _editing.HasTimeMovableSelection;

    /// <summary>Why a nudge cannot run (nothing movable is selected), or null.</summary>
    public string? DisabledReason => HasSelection ? null : _localization[EditingAvailability.KeyNoMovableSelection];

    /// <summary>
    ///     Moves the selection earlier (-1) or later (+1) by <paramref name="milliseconds" />
    ///     (0 = one grid step) and reports the outcome on the status rail.
    /// </summary>
    public void Nudge(int direction, int milliseconds)
    {
        if (DisabledReason is { } reason)
        {
            _projectState.SetMessage(reason, StatusSeverity.Warning);
            return;
        }

        var result = _editing.MoveSelectedInTime(direction, milliseconds, _transport.GridDivisor);
        var (text, severity) = TimeNudgeStatus.Describe(result, _localization);
        _projectState.SetMessage(text, severity);
    }

    private void RaiseCanExecuteChanged()
    {
        OnPropertyChanged(nameof(DisabledReason));
        EarlierCommand.NotifyCanExecuteChanged();
        LaterCommand.NotifyCanExecuteChanged();
        Earlier10MsCommand.NotifyCanExecuteChanged();
        Later10MsCommand.NotifyCanExecuteChanged();
        Earlier1MsCommand.NotifyCanExecuteChanged();
        Later1MsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Earlier() => Nudge(-1, 0);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Later() => Nudge(1, 0);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Earlier10Ms() => Nudge(-1, 10);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Later10Ms() => Nudge(1, 10);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Earlier1Ms() => Nudge(-1, 1);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Later1Ms() => Nudge(1, 1);
}
