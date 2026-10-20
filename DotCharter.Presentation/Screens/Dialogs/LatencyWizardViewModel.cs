using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.Calibration;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Graphics;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Dialogs;

public enum LatencyWizardStep
{
    Intro,
    Judging,
    VisualTap,
    AudioTap,
    Results
}

/// <summary>
///     The latency / A-V calibration wizard, one instance per open. It measures the
///     two numbers a rhythm game needs by CLOSING LOOPS rather than summing component
///     latencies: the judging offset (a scheduled click looped back through the
///     input, written to <c>LivePlayLatencyOffsetMs</c>) and the A-V offset (visual
///     taps minus audio taps, so the player's own bias cancels; written to the
///     renderer-only <c>VisualLeadMs</c>, never <c>AudioDelayMs</c>, which would bias
///     judging). The math is the pure <see cref="LatencyCalibrationMath" />; this is
///     the driver. The measuring half is <c>LatencyWizardViewModel.Measure.cs</c>.
/// </summary>
public sealed partial class LatencyWizardViewModel : DialogViewModel<double?>, ILatencyWizardScreen, ICancelableDialog,
    IDisposable
{
    private readonly ICalibrationAudio _audio;
    private readonly IConfigurationService _config;
    private readonly IGraphicsDeviceSettings _graphics;
    private readonly ILocalizationService _loc;
    private readonly IAppLogger _logger;
    private readonly IUiDispatcher _ui;
    private readonly RelayCommand _measure;
    private LatencyWizardStep _step;
    private List<int> _channels = [];
    private IReadOnlyList<string> _channelLabels = [];
    private int _selectedChannelIndex = -1;
    private string _judgingStatus = "";
    private string _judgingResult = "";
    private bool _judgingMeasured;
    private string _resultsSummary = "";
    private string _resultsDetail = "";
    private bool _resultsTrusted;
    private double _avOffsetMs;
    private double _recommendedVisualLeadMs;

    public LatencyWizardViewModel(ICalibrationAudio audio, IConfigurationService config, IGraphicsDeviceSettings graphics,
        ILocalizationService loc, IAppLogger logger, IUiDispatcher ui)
    {
        _audio = audio;
        _config = config;
        _graphics = graphics;
        _loc = loc;
        _logger = logger;
        _ui = ui;
        CancelCommand = new RelayCommand(() => TryCancel());
        GoToJudgingCommand = new RelayCommand(() => Step = LatencyWizardStep.Judging);
        MeasureJudgingCommand = _measure = new RelayCommand(MeasureJudging, () => HasListeningChannels || IsMeasuringJudging);
        GoToVisualTapCommand = new RelayCommand(GoToVisualTap);
        GoToAudioTapCommand = new RelayCommand(GoToAudioTap);
        TapCommand = new RelayCommand(Tap);
        RedoTapsCommand = new RelayCommand(RedoTaps);
        BackCommand = new RelayCommand(Back);
        ComputeResultsCommand = new RelayCommand(ComputeResults);
        ApplyResultsCommand = new RelayCommand(ApplyResults);

        LoadListeningChannels(_config.LoadAppConfig());
        _judgingStatus = HasListeningChannels
            ? _loc.Format("LatencyWizard_Judging_Current", CurrentJudgingOffsetMs())
            : _loc["LivePlay_NoInput"];
    }

    public LatencyWizardStep Step
    {
        get => _step;
        private set
        {
            if (!SetProperty(ref _step, value)) return;
            foreach (var name in (string[])
                     [nameof(IsIntro), nameof(IsJudging), nameof(IsVisualTap), nameof(IsAudioTap), nameof(IsTapStep), nameof(IsResults)])
            {
                OnPropertyChanged(name);
            }
        }
    }

    public bool IsIntro => Step == LatencyWizardStep.Intro;
    public bool IsJudging => Step == LatencyWizardStep.Judging;
    public bool IsVisualTap => Step == LatencyWizardStep.VisualTap;
    public bool IsAudioTap => Step == LatencyWizardStep.AudioTap;
    public bool IsTapStep => IsVisualTap || IsAudioTap;
    public bool IsResults => Step == LatencyWizardStep.Results;

    public IReadOnlyList<string> ListeningChannels => _channelLabels;

    public bool HasListeningChannels => _channelLabels.Count > 0;

    public int SelectedListeningChannelIndex
    {
        get => _selectedChannelIndex;
        set
        {
            if (!IsMeasuringJudging) SetProperty(ref _selectedChannelIndex, value);
        }
    }

    /// <summary>The open channel the picker names; null with no open input.</summary>
    public int? SelectedOpenChannel =>
        _selectedChannelIndex >= 0 && _selectedChannelIndex < _channels.Count ? _channels[_selectedChannelIndex] : null;

    public string JudgingButtonLabel =>
        _loc[IsMeasuringJudging ? "LatencyWizard_Judging_Stop" : "LatencyWizard_Judging_Measure"];

    public string JudgingStatus { get => _judgingStatus; private set => SetProperty(ref _judgingStatus, value); }

    public bool JudgingMeasured { get => _judgingMeasured; private set => SetProperty(ref _judgingMeasured, value); }

    public string JudgingResult { get => _judgingResult; private set => SetProperty(ref _judgingResult, value); }

    public string ResultsSummary { get => _resultsSummary; private set => SetProperty(ref _resultsSummary, value); }

    public string ResultsDetail { get => _resultsDetail; private set => SetProperty(ref _resultsDetail, value); }

    public bool ResultsTrusted { get => _resultsTrusted; private set => SetProperty(ref _resultsTrusted, value); }

    public ICommand CancelCommand { get; }
    public ICommand GoToJudgingCommand { get; }
    public ICommand MeasureJudgingCommand { get; }
    public ICommand GoToVisualTapCommand { get; }
    public ICommand GoToAudioTapCommand { get; }
    public ICommand TapCommand { get; }
    public ICommand RedoTapsCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ComputeResultsCommand { get; }
    public ICommand ApplyResultsCommand { get; }

    public bool TryCancel()
    {
        StopEverything();
        Close(null);
        return true;
    }

    public void Dispose() => StopEverything();

    /// <summary>One row per OPEN channel, the guitar's (seat 0's) picked: live judging listens there, so the round trip is measured there.</summary>
    internal void LoadListeningChannels(AppConfig config)
    {
        var open = GuitarInputChannels.OpenChannelCount(config);
        _channels = [.. Enumerable.Range(0, open)];
        _channelLabels = [.. _channels.Select(c => GuitarInputChannels.JackLabel(_loc, GuitarInputChannels.JackForOpenChannel(config, c)))];
        var preferred = _channels.IndexOf(GuitarInputChannels.SeatZeroOpenChannel(config));
        _selectedChannelIndex = preferred >= 0 ? preferred : _channels.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(ListeningChannels));
        OnPropertyChanged(nameof(HasListeningChannels));
        OnPropertyChanged(nameof(SelectedListeningChannelIndex));
        _measure?.NotifyCanExecuteChanged();
    }

    private string ChannelLabel(int openChannel) =>
        _channels.IndexOf(openChannel) is var i and >= 0 ? _channelLabels[i] : GuitarInputChannels.JackLabel(_loc, openChannel);

    private void GoToVisualTap()
    {
        StopMetronome();
        _visualTaps.Clear();
        Step = LatencyWizardStep.VisualTap;
        StartMetronome(visual: true);
    }

    private void GoToAudioTap()
    {
        StopMetronome();
        _audioTaps.Clear();
        Step = LatencyWizardStep.AudioTap;
        StartMetronome(visual: false);
    }

    private void RedoTaps()
    {
        (_isVisualMetronome ? _visualTaps : _audioTaps).Clear();
        StartMetronome(_isVisualMetronome);
    }

    private void Back()
    {
        switch (Step)
        {
            case LatencyWizardStep.Judging: Step = LatencyWizardStep.Intro; break;
            case LatencyWizardStep.VisualTap: StopMetronome(); Step = LatencyWizardStep.Judging; break;
            case LatencyWizardStep.AudioTap:
            case LatencyWizardStep.Results: GoToVisualTap(); break;
        }
    }

    private void ComputeResults()
    {
        StopMetronome();
        var result = LatencyCalibrationMath.ComputeAvOffset(_visualTaps, _audioTaps);
        ResultsTrusted = result.Trusted;
        if (!result.Trusted)
        {
            ResultsSummary = _loc["LatencyWizard_Results_NeedMore"];
            ResultsDetail = "";
            Step = LatencyWizardStep.Results;
            return;
        }

        _avOffsetMs = result.AvOffsetMs;
        var mapped = LatencyCalibrationMath.MapAvOffsetToConfig(_avOffsetMs, _graphics.MaxVisualLeadMs);
        _recommendedVisualLeadMs = Math.Round(mapped.VisualLeadMs, 1);
        var offsetAbs = Math.Round(Math.Abs(_avOffsetMs), 1);
        ResultsSummary = _loc.Format("LatencyWizard_Results_Offset", Math.Round(_avOffsetMs, 1));
        ResultsDetail = mapped.AudioLeadsUncompensated
            ? _loc.Format("LatencyWizard_Results_PictureLeads", offsetAbs)
            : _loc.Format("LatencyWizard_Results_PictureLags", offsetAbs, _recommendedVisualLeadMs);
        Step = LatencyWizardStep.Results;
    }

    private void ApplyResults()
    {
        StopEverything();
        if (!ResultsTrusted || _recommendedVisualLeadMs <= 0.0)
        {
            Close(null);
            return;
        }

        var lead = Math.Clamp(_recommendedVisualLeadMs, 0, _graphics.MaxVisualLeadMs);
        SaveConfig(c => c.VisualLeadMs = lead);
        _graphics.SetVisualLeadTrimMs(lead);
        _logger.Info(AppLogCategory.Audio, $"Latency wizard: A-V offset {_avOffsetMs:0.0} ms -> VisualLeadMs {lead:0.0} ms.");
        Close(lead);
    }

    private double CurrentJudgingOffsetMs() => Math.Round(_config.LoadAppConfig().LivePlayLatencyOffsetMs, 1);

    private void SaveConfig(Action<AppConfig> mutate)
    {
        var config = _config.LoadAppConfig();
        mutate(config);
        _config.SaveAppConfig(config);
    }
}
