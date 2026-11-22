using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>One line of the lists; the count is in the text.</summary>
public sealed record FixChartChangeRow(string Text) : IFixChartChange;

/// <summary>
///     Tools ▸ Fix chart…, one instance per open. On opening it runs the two existing operations on a
///     snapshot of the chart (<see cref="IChartMaintenanceService.PreviewFixChartAsync" />, off the UI thread) and lists the
///     counts per kind, so nothing changes before Apply. The Polish list follows what Apply would do:
///     counted after the Errors group when that is ticked, on its own when not. Apply runs the ticked groups as ONE undo step
///     through <see cref="IChartMaintenanceService.FixChart" />.
/// </summary>
public sealed class FixChartViewModel : DialogViewModel<bool>, IFixChartScreen, IDialogSetup<FixChartRequest>, ICancelableDialog
{
    private readonly ChartEditDialogServices _edit;
    private readonly IChartMaintenanceService _maintenance;
    private readonly RelayCommand _apply;
    private FixChartPreview? _both;
    private FixChartPreview? _polishAlone;
    private bool _fixErrors = true;
    private bool _fixPolish = true;
    private bool _isChecking = true;
    private int _generation;
    private IReadOnlyList<IFixChartChange> _errorsChanges = [];
    private IReadOnlyList<IFixChartChange> _polishChanges = [];
    private string _errorsSummary;
    private string _polishSummary;
    private string _totalSummary;
    private int _shownTotal;

    public FixChartViewModel(ChartEditDialogServices edit, IChartMaintenanceService maintenance)
    {
        _edit = edit;
        _maintenance = maintenance;
        _apply = new RelayCommand(Apply, () => CanApply);
        CancelCommand = new RelayCommand(() => TryCancel());
        ApplyCommand = _apply;
        _errorsSummary = _polishSummary = _totalSummary = L("Dialog_FixChart_Checking");
    }

    /// <summary>The check in flight, or the last one; for tests.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public bool FixErrors
    {
        get => _fixErrors;
        set
        {
            if (!SetProperty(ref _fixErrors, value)) return;
            // The Polish counts depend on whether the Errors group runs first.
            Pending = ShowAsync();
        }
    }

    public bool FixPolish
    {
        get => _fixPolish;
        set
        {
            if (SetProperty(ref _fixPolish, value)) Refresh();
        }
    }

    public string ErrorsSummary => _errorsSummary;

    public IReadOnlyList<IFixChartChange> ErrorsChanges => _errorsChanges;

    public bool HasErrorsChanges => _errorsChanges.Count > 0;

    public string PolishSummary => _polishSummary;

    public IReadOnlyList<IFixChartChange> PolishChanges => _polishChanges;

    public bool HasPolishChanges => _polishChanges.Count > 0;

    public bool IsChecking => _isChecking;

    public string TotalSummary => _totalSummary;

    public bool CanApply => !_isChecking && Groups != FixChartGroups.None && _shownTotal > 0;

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    private FixChartGroups Groups =>
        (_fixErrors ? FixChartGroups.Errors : FixChartGroups.None) | (_fixPolish ? FixChartGroups.Polish : FixChartGroups.None);

    public void Configure(FixChartRequest request)
    {
        _fixErrors = request.Preselect.HasFlag(FixChartGroups.Errors);
        _fixPolish = request.Preselect.HasFlag(FixChartGroups.Polish);
        OnPropertyChanged(string.Empty);
        Pending = ShowAsync();
    }

    public bool TryCancel()
    {
        Close(false);
        return true;
    }

    private string L(string key) => _edit.Localization[key];

    /// <summary>Lands the lists for the groups as ticked now; runs the copy's check first when the numbers for them are not known yet.</summary>
    private async Task ShowAsync()
    {
        var generation = ++_generation;
        try
        {
            if (_both == null || (!_fixErrors && _polishAlone == null)) SetChecking(true);
            _both ??= await _maintenance.PreviewFixChartAsync(FixChartGroups.Both);
            if (!_fixErrors) _polishAlone ??= await _maintenance.PreviewFixChartAsync(FixChartGroups.Polish);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _edit.Logger.Error(AppLogCategory.Editing, "The Fix chart preview failed.", ex);
            _both = FixChartPreview.Empty;
            _polishAlone = FixChartPreview.Empty;
        }

        if (generation != _generation) return; // a newer tick answers
        SetChecking(false);
    }

    private void SetChecking(bool value)
    {
        _isChecking = value;
        OnPropertyChanged(nameof(IsChecking));
        Refresh();
    }

    /// <summary>Rebuilds the lists and the totals from the cached previews and the ticks.</summary>
    private void Refresh()
    {
        var loc = _edit.Localization;
        if (_isChecking || _both == null)
        {
            _errorsChanges = [];
            _polishChanges = [];
            _errorsSummary = _polishSummary = _totalSummary = L("Dialog_FixChart_Checking");
            _shownTotal = 0;
        }
        else
        {
            var polish = (_fixErrors ? _both.Polish : _polishAlone?.Polish) ?? [];
            _errorsChanges = [.. _both.Errors.Select(Row)];
            _polishChanges = [.. polish.Select(Row)];
            var errorsTotal = _both.Errors.Sum(c => c.Count);
            var polishTotal = polish.Sum(c => c.Count);
            _errorsSummary = Summary(errorsTotal);
            _polishSummary = Summary(polishTotal);
            _shownTotal = (_fixErrors ? errorsTotal : 0) + (_fixPolish ? polishTotal : 0);
            _totalSummary = Groups == FixChartGroups.None ? L("Dialog_FixChart_NothingTicked")
                : _shownTotal == 0 ? L("Dialog_FixChart_NothingToChange")
                : loc.Plural("Dialog_FixChart_TotalOne", "Dialog_FixChart_Total", _shownTotal);
        }

        OnPropertyChanged(nameof(ErrorsChanges));
        OnPropertyChanged(nameof(PolishChanges));
        OnPropertyChanged(nameof(HasErrorsChanges));
        OnPropertyChanged(nameof(HasPolishChanges));
        OnPropertyChanged(nameof(ErrorsSummary));
        OnPropertyChanged(nameof(PolishSummary));
        OnPropertyChanged(nameof(TotalSummary));
        OnPropertyChanged(nameof(CanApply));
        _apply.NotifyCanExecuteChanged();
    }

    private string Summary(int total) => total == 0
        ? L("Dialog_FixChart_NothingToChange")
        : _edit.Localization.Plural("Dialog_FixChart_GroupTotalOne", "Dialog_FixChart_GroupTotal", total);

    /// <summary>The operation's label is a stable English id; its words are the "FixChart_Change_" resource of the same name.</summary>
    private FixChartChangeRow Row(FixChartChange change) =>
        new(_edit.Localization.Format(FixChartChangeKey.For(change.Label), change.Count));

    private void Apply()
    {
        if (!CanApply) return;
        _maintenance.FixChart(Groups);
        Close(true);
    }
}

/// <summary>Resource key of an operation's change label: "song-end cleanup" becomes "FixChart_Change_SongEndCleanup".</summary>
public static class FixChartChangeKey
{
    public static string For(string label)
    {
        var parts = label.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        return "FixChart_Change_" + string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
