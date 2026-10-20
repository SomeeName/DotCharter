using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Sync;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Arrangement ▸ Sync sounds. One instance per open: the arrangement list is read when it opens.</summary>
public sealed class ArrangementSyncViewModel : DialogViewModel<bool>, IArrangementSyncScreen, ICancelableDialog
{
    private readonly IChartMaintenanceService _maintenance;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IReadOnlyList<Arrangement> _arrangements;
    private int _sourceIndex = -1;
    private int _targetIndex = -1;
    private string _preview = "";
    private bool _canApply;

    public ArrangementSyncViewModel(ProjectState state, IChartMaintenanceService maintenance,
        ILocalizationService localization, IAppLogger logger)
    {
        _maintenance = maintenance;
        _localization = localization;
        _logger = logger;
        _arrangements = state.ActiveProject?.Arrangements.ToList() ?? [];
        Arrangements = _arrangements.Select(a => a.Name).ToList();

        var source = state.ActiveArrangement is { } active && _arrangements.Contains(active)
            ? active
            : _arrangements.FirstOrDefault();
        _sourceIndex = source == null ? -1 : IndexOf(source);
        _targetIndex = IndexWhere(a => !ReferenceEquals(a, source));
        RefreshPreview();

        ApplyCommand = new RelayCommand(Apply);
        CancelCommand = new RelayCommand(() => TryCancel());
        SwapCommand = new RelayCommand(Swap);
        logger.Info(AppLogCategory.Events,
            $"Arrangement sync dialog opened. Source='{Source?.Name}', target='{Target?.Name}'.");
    }

    public IReadOnlyList<string> Arrangements { get; }

    public int SourceIndex
    {
        get => _sourceIndex;
        set
        {
            if (SetProperty(ref _sourceIndex, value)) RefreshPreview();
        }
    }

    public int TargetIndex
    {
        get => _targetIndex;
        set
        {
            if (SetProperty(ref _targetIndex, value)) RefreshPreview();
        }
    }

    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public bool CanApply
    {
        get => _canApply;
        private set => SetProperty(ref _canApply, value);
    }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand SwapCommand { get; }

    private Arrangement? Source => At(_sourceIndex);

    private Arrangement? Target => At(_targetIndex);

    public bool TryCancel()
    {
        if (!IsClosed) _logger.Info(AppLogCategory.Events, "Arrangement sync dialog canceled.");
        Close(false);
        return true;
    }

    private void Apply()
    {
        if (!CanApply || Source is not { } source || Target is not { } target) return;
        _maintenance.SyncArrangement(source, target);
        Close(true);
    }

    private void Swap()
    {
        (_targetIndex, _sourceIndex) = (_sourceIndex, _targetIndex);
        OnPropertyChanged(nameof(SourceIndex));
        OnPropertyChanged(nameof(TargetIndex));
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (Source is not { } source || Target is not { } target || ReferenceEquals(source, target))
        {
            Preview = _localization["ArrangementSync_PickTwo"];
            CanApply = false;
            return;
        }

        var summary = ArrangementSoundSync.Preview(source, target);
        CanApply = summary.WouldChangeAnything;
        Preview = summary.SharedSounds == 0
            ? _localization["ArrangementSync_NothingShared"]
            : _localization.Format("ArrangementSync_Preview", summary.SharedSounds, summary.SoundsChanged,
                summary.HandShapes, summary.Anchors);
    }

    private Arrangement? At(int index) => index >= 0 && index < _arrangements.Count ? _arrangements[index] : null;

    private int IndexOf(Arrangement arrangement) => IndexWhere(a => ReferenceEquals(a, arrangement));

    private int IndexWhere(Func<Arrangement, bool> match)
    {
        for (var i = 0; i < _arrangements.Count; i++)
            if (match(_arrangements[i])) return i;
        return -1;
    }
}
