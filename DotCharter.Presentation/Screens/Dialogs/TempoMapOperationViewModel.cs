using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>The single-number tempo-map operations (silence, extra measures). One instance per open.</summary>
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

    public ICommand ApplyCommand { get; }

    public ICommand CancelCommand { get; }

    public void Configure(TempoMapOperationRequest request)
    {
        _operation = request.Operation;
        var config = _config.LoadAppConfig();
        var (title, subtitle) = TitleKeys(request.Operation);
        var (label, value, minimum, maximum, format) = request.Operation switch
        {
            TempoMapOperation.DefaultStartSilence => (SilenceLabel, Math.Max(0, config.LeadingSilenceSeconds), 0d, 600d, "0.###"),
            TempoMapOperation.AddSilenceAtStart => (SilenceLabel, Math.Max(0.5, config.LeadingSilenceSeconds), 0d, 600d, "0.###"),
            TempoMapOperation.AddSilenceAtEnd => (SilenceLabel, Math.Max(0.5, config.TrailingSilenceSeconds), 0d, 600d, "0.###"),
            _ => ("Dialog_TempoMap_MeasureCount_Label", 1d, 1d, 128d, "0")
        };

        Title = _localization[title];
        Subtitle = _localization[subtitle];
        Label = _localization[label];
        Minimum = minimum;
        Maximum = maximum;
        FormatString = format;
        Value = value;
        _logger.Info(AppLogCategory.Events,
            $"Tempo-map operation dialog opened. Mode={_operation}, value={value:0.###}.");
    }

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
        }

        Close(true);
    }
}
