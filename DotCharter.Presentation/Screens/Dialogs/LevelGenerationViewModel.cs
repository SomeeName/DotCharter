using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     Tools ▸ Generate difficulty levels. The rules live in
///     <c>LevelGenerationService</c>; this dialog asks how many levels and hands
///     the work to <see cref="IChartMaintenanceService" />, which records it as ONE
///     undoable snapshot. It stays open for the run: the modal scrim and
///     <c>GlobalChordGate</c> are what stop the live project being edited between
///     that snapshot and the apply.
/// </summary>
public sealed class LevelGenerationViewModel : DialogViewModel<bool>, ILevelGenerationScreen, ICancelableDialog
{
    /// <summary>Rocksmith's own DD ladders top out around here.</summary>
    private const int MaxLevels = 20;

    /// <summary>Stable, so a second run replaces the status-bar pill rather than stacking one.</summary>
    private const string StatusTaskId = "level-generation";

    private readonly IChartMaintenanceService _maintenance;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IStatusBarService? _statusBar;
    private double _levelCount = 5;
    private bool _isBusy;
    private double _progress;
    private string _progressLabel = "";

    public LevelGenerationViewModel(IChartMaintenanceService maintenance, ProjectState state,
        ILocalizationService localization, IAppLogger logger, IStatusBarService? statusBar = null)
    {
        _maintenance = maintenance;
        _localization = localization;
        _logger = logger;
        _statusBar = statusBar;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
        CancelCommand = new RelayCommand(() => TryCancel());

        // Regenerating over a levelled chart re-derives every level, so say what is replaced.
        if (state.ActiveArrangement is { } arrangement)
        {
            Subtitle = localization.Format("Dialog_GenerateLevels_Subtitle", arrangement.Name, arrangement.Levels.Count);
            logger.Info(AppLogCategory.Editing,
                $"Level-generation dialog opened. Arrangement='{arrangement.Name}', currentLevels={arrangement.Levels.Count}.");
        }
    }

    public string Subtitle { get; } = "";

    public double LevelCount
    {
        get => _levelCount;
        set => SetProperty(ref _levelCount, value);
    }

    public double Minimum => 2;

    public double Maximum => MaxLevels;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProgressLabel
    {
        get => _progressLabel;
        private set => SetProperty(ref _progressLabel, value);
    }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>Refused mid-run: closing would only hide a stall that is still happening.</summary>
    public bool TryCancel()
    {
        if (IsBusy) return false;
        Close(false);
        return true;
    }

    private async Task ApplyAsync()
    {
        if (IsBusy) return;
        var levels = (int)Math.Round(Math.Clamp(LevelCount, Minimum, Maximum));
        var finished = false;

        IsBusy = true;
        Progress = 0;
        ProgressLabel = _localization.Format("Dialog_GenerateLevels_Progress", 0, levels);
        _statusBar?.BeginTask(StatusTaskId, _localization["Status_GeneratingLevels"], indeterminate: false);
        try
        {
            var progress = new Progress<LevelGenerationProgress>(step =>
            {
                Progress = step.Fraction;
                ProgressLabel = _localization.Format("Dialog_GenerateLevels_Progress", step.CompletedLevels, step.TotalLevels);
                _statusBar?.ReportProgress(StatusTaskId, step.Fraction);
            });

            // Reports its own outcome through ProjectState's status line.
            await _maintenance.GenerateLevelsAsync(levels, progress);
            finished = true;
        }
        finally
        {
            _statusBar?.CompleteTask(StatusTaskId);
            IsBusy = false;
            Progress = 0;
            ProgressLabel = "";
            // Closes AFTER the work: closing first made the stall look like a frozen window.
            Close(finished);
        }
    }
}
