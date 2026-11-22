using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Tempo map ▸ Start &amp; end: the five edge operations (silence, extra measures) behind one choice;
/// also Tempo map ▸ Insert measures at playhead, alone. One instance per open.</summary>
public sealed class TempoMapOperationViewModel : DialogViewModel<bool>, ITempoMapOperationScreen,
    IDialogSetup<TempoMapOperationRequest>, ICancelableDialog
{
    private readonly IChartMaintenanceService _maintenance;
    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;
    private TempoMapOperation _operation;
    private string _title = "";
    private string _subtitle = "";
    private string _label = "";
    private double _value;
    private double _minimum;
    private double _maximum = 9999;
    private string _formatString = "0.###";
    private string _operationLabel = "";
    private TempoMapOperationChoice? _selected;
    private double _beatsPerMeasure = 4;
    private int _beatUnit = 4;
    private double _bpm = 120;

    public TempoMapOperationViewModel(IChartMaintenanceService maintenance, IConfigurationService config,
        ILocalizationService localization, IAppLogger logger)
    {
        _maintenance = maintenance;
        _config = config;
        _localization = localization;
        _logger = logger;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync);
        CancelCommand = new RelayCommand(() => TryCancel());
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string OperationLabel
    {
        get => _operationLabel;
        private set => SetProperty(ref _operationLabel, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    public double Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public double Minimum
    {
        get => _minimum;
        private set => SetProperty(ref _minimum, value);
    }

    public double Maximum
    {
        get => _maximum;
        private set => SetProperty(ref _maximum, value);
    }

    public string FormatString
    {
        get => _formatString;
        private set => SetProperty(ref _formatString, value);
    }

    public bool ShowsMeasureTempo { get; private set; }

    public double BeatsPerMeasure
    {
        get => _beatsPerMeasure;
        set => SetProperty(ref _beatsPerMeasure, value);
    }

    public IReadOnlyList<int> BeatUnits { get; private set; } = DefaultBeatUnits;

    public int BeatUnit
    {
        get => _beatUnit;
        set => SetProperty(ref _beatUnit, value);
    }

    public double Bpm
    {
        get => _bpm;
        set => SetProperty(ref _bpm, value);
    }

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    public IReadOnlyList<TempoMapOperationChoice> Choices { get; private set; } = [];

    public bool ShowsChoices { get; private set; } = true;

    public TempoMapOperationChoice? SelectedChoice
    {
        get => _selected;
        set
        {
            if (value is null || ReferenceEquals(value, _selected)) return;
            _selected = value;
            OnPropertyChanged();
            ShowOperation(value.Operation);
        }
    }

    public void Configure(TempoMapOperationRequest request)
    {
        // Insert measures at playhead is not an edge verb: it opens alone, under its own title.
        var alone = request.Operation == TempoMapOperation.InsertMeasuresAtPlayhead;
        Title = _localization[alone ? TitleKeys(request.Operation).Title : "Dialog_TempoMap_StartEnd_Title"];
        OperationLabel = _localization["Dialog_TempoMap_Operation_Label"];
        Choices = [.. Enum.GetValues<TempoMapOperation>()
            .Where(operation => alone ? operation == request.Operation : operation != TempoMapOperation.InsertMeasuresAtPlayhead)
            .Select(operation => new TempoMapOperationChoice(operation, _localization[TitleKeys(operation).Title]))];
        ShowsChoices = !alone;
        ShowsMeasureTempo = alone;
        if (alone) ShowPlayheadBarTempo();
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(ShowsChoices));
        OnPropertyChanged(nameof(ShowsMeasureTempo));
        SelectedChoice = Choices.First(choice => choice.Operation == request.Operation);
    }

    /// <summary>Fills the subtitle, label, range and starting value for <paramref name="operation" />.</summary>
    private void ShowOperation(TempoMapOperation operation)
    {
        _operation = operation;
        var config = _config.LoadAppConfig();
        var (_, subtitle) = TitleKeys(operation);
        var (label, value, minimum, maximum, format) = operation switch
        {
            TempoMapOperation.DefaultStartSilence => (SilenceLabel, Math.Max(0, config.LeadingSilenceSeconds), 0d, 600d, "0.###"),
            TempoMapOperation.AddSilenceAtStart => (SilenceLabel, Math.Max(0.5, config.LeadingSilenceSeconds), 0d, 600d, "0.###"),
            TempoMapOperation.AddSilenceAtEnd => (SilenceLabel, Math.Max(0.5, config.TrailingSilenceSeconds), 0d, 600d, "0.###"),
            _ => ("Dialog_TempoMap_MeasureCount_Label", 1d, 1d, 128d, "0")
        };

        Subtitle = _localization[subtitle];
        Label = _localization[label];
        Minimum = minimum;
        Maximum = maximum;
        FormatString = format;
        Value = value;
        _logger.Info(AppLogCategory.Events,
            $"Tempo-map operation dialog opened. Mode={_operation}, value={value:0.###}.");
    }

    /// <summary>Starts the time signature and tempo rows on the bar at the playhead.</summary>
    private void ShowPlayheadBarTempo()
    {
        var tempo = _maintenance.PlayheadBarTempo() ?? new MeasureInsertTempo(4, 4, 120);
        BeatUnits = DefaultBeatUnits.Contains(tempo.NoteDenominator)
            ? DefaultBeatUnits
            : [.. DefaultBeatUnits.Append(tempo.NoteDenominator).Order()];
        OnPropertyChanged(nameof(BeatUnits));
        BeatsPerMeasure = tempo.BeatsPerMeasure;
        BeatUnit = tempo.NoteDenominator;
        Bpm = Math.Round(tempo.Bpm, 3);
    }

    private static readonly int[] DefaultBeatUnits = [2, 4, 8, 16];

    public bool TryCancel()
    {
        if (!IsClosed) _logger.Info(AppLogCategory.Events, "Tempo-map operation dialog canceled.");
        Close(false);
        return true;
    }

    private const string SilenceLabel = "Dialog_TempoMap_SilenceSeconds_Label";

    private static (string Title, string Subtitle) TitleKeys(TempoMapOperation operation) => operation switch
    {
        TempoMapOperation.DefaultStartSilence =>
            ("Dialog_TempoMap_DefaultStartSilence_Title", "Dialog_TempoMap_DefaultStartSilence_Subtitle"),
        TempoMapOperation.AddSilenceAtStart =>
            ("Dialog_TempoMap_AddSilenceAtStart_Title", "Dialog_TempoMap_AddSilenceAtStart_Subtitle"),
        TempoMapOperation.AddSilenceAtEnd =>
            ("Dialog_TempoMap_AddSilenceAtEnd_Title", "Dialog_TempoMap_AddSilenceAtEnd_Subtitle"),
        TempoMapOperation.AddMeasuresAtStart =>
            ("Dialog_TempoMap_AddMeasuresAtStart_Title", "Dialog_TempoMap_AddMeasuresAtStart_Subtitle"),
        TempoMapOperation.InsertMeasuresAtPlayhead =>
            ("Dialog_TempoMap_InsertMeasuresAtPlayhead_Title", "Dialog_TempoMap_InsertMeasuresAtPlayhead_Subtitle"),
        _ => ("Dialog_TempoMap_AddMeasuresAtEnd_Title", "Dialog_TempoMap_AddMeasuresAtEnd_Subtitle")
    };

    private async Task ApplyAsync()
    {
        var value = Value;
        _logger.Info(AppLogCategory.Events, $"Tempo-map operation dialog apply. Mode={_operation}, value={value:0.###}.");
        switch (_operation)
        {
            case TempoMapOperation.DefaultStartSilence:
                _maintenance.SetDefaultStartSilence(value);
                break;
            case TempoMapOperation.AddSilenceAtStart:
                await _maintenance.AddSilenceAtBeginningAsync(value);
                break;
            case TempoMapOperation.AddSilenceAtEnd:
                await _maintenance.AddSilenceAtEndAsync(value);
                break;
            case TempoMapOperation.AddMeasuresAtStart:
                _maintenance.AddMeasuresAtStart((int)Math.Round(value));
                break;
            case TempoMapOperation.AddMeasuresAtEnd:
                _maintenance.AddMeasuresAtEnd((int)Math.Round(value));
                break;
            case TempoMapOperation.InsertMeasuresAtPlayhead:
                _maintenance.InsertMeasuresAtPlayhead((int)Math.Round(value),
                    new MeasureInsertTempo((int)Math.Round(BeatsPerMeasure), BeatUnit, Bpm));
                break;
        }

        Close(true);
    }
}
