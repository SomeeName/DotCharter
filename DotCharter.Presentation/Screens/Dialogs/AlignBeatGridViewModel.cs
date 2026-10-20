using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Tempo map ▸ Align beat grid to notes. One instance per open: the notes are measured when it opens.</summary>
public sealed class AlignBeatGridViewModel : DialogViewModel<bool>, IAlignBeatGridScreen, ICancelableDialog
{
    public const double MaxOffsetMs = 1000;

    private readonly IChartMaintenanceService _maintenance;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private readonly IReadOnlyList<double> _onsets;
    private readonly List<BeatMarker> _beats;
    private double _offsetMs;
    private string _offAfterText = "";

    public AlignBeatGridViewModel(ProjectState state, IChartMaintenanceService maintenance,
        ILocalizationService localization, IAppLogger logger)
    {
        _maintenance = maintenance;
        _localization = localization;
        _logger = logger;
        var started = Stopwatch.GetTimestamp();
        _beats = state.ActiveProject?.Beats.ToList() ?? [];
        _onsets = state.ActiveProject is { } project ? BeatGridOffsetEstimator.Onsets(project, _beats) : [];
        var estimate = BeatGridOffsetEstimator.Estimate(_onsets, _beats);

        HasEstimate = estimate != null;
        _offsetMs = estimate == null ? 0 : Math.Clamp(Math.Round(estimate.OffsetMs, 1), -MaxOffsetMs, MaxOffsetMs);
        OffNowText = MedianText(0);
        EstimateText = estimate == null
            ? localization["Dialog_AlignBeatGrid_NoEstimate"]
            : localization.Plural("Dialog_AlignBeatGrid_MeasuredOne", "Dialog_AlignBeatGrid_Measured",
                estimate.OnsetCount, Signed(_offsetMs));
        RefreshAfter();

        ApplyCommand = new RelayCommand(Apply);
        CancelCommand = new RelayCommand(() => TryCancel());
        logger.Info(AppLogCategory.Events,
            $"Align-beat-grid dialog opened. Suggested={(estimate == null ? "none" : $"{estimate.OffsetMs:0.0}ms")}, " +
            $"medianNow={estimate?.MedianOffNowMs:0.0}ms, medianAfter={estimate?.MedianOffAfterMs:0.0}ms, " +
            $"onsets={_onsets.Count}, measuredIn={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0}ms.");
    }

    public double OffsetMs
    {
        get => _offsetMs;
        set
        {
            if (SetProperty(ref _offsetMs, Math.Clamp(value, -MaxOffsetMs, MaxOffsetMs))) RefreshAfter();
        }
    }

    public string OffNowText { get; }

    public string OffAfterText
    {
        get => _offAfterText;
        private set => SetProperty(ref _offAfterText, value);
    }

    public string EstimateText { get; }

    public bool HasEstimate { get; }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    public bool TryCancel()
    {
        if (!IsClosed) _logger.Info(AppLogCategory.Events, "Align-beat-grid dialog canceled.");
        Close(false);
        return true;
    }

    private void Apply()
    {
        _logger.Info(AppLogCategory.Events, $"Align-beat-grid dialog apply. Offset={OffsetMs:0.0}ms.");
        Close(_maintenance.AlignBeatGridToNotes(OffsetMs));
    }

    private void RefreshAfter() => OffAfterText = MedianText(OffsetMs);

    private string MedianText(double offsetMs)
    {
        if (_beats.Count < 2 || _onsets.Count == 0) return _localization["Dialog_AlignBeatGrid_NoValue"];
        var median = BeatGridOffsetEstimator.MedianDistanceMs(_onsets, _beats, offsetMs);
        return _localization.Format("Dialog_AlignBeatGrid_Ms", median.ToString("0.0", CultureInfo.CurrentCulture));
    }

    private static string Signed(double ms) => ms.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
}
