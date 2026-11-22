using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Tempo;
using DotCharter.Services.Editing.Tempo.Estimate;
using DotCharter.Services.State;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Tempo map ▸ Estimate BPM. One instance per open; the chart changes only when Apply runs.</summary>
public sealed class EstimateBpmViewModel : DialogViewModel<bool>, IEstimateBpmScreen, ICancelableDialog, IDisposable
{
    private readonly ChartEditDialogServices _edit;
    private readonly ITempoEstimateService _service;
    private readonly ITempoService _tempo;
    private readonly IEditorTransport _transport;
    private readonly ILocalizationService _loc;
    private readonly string _audioPath;
    private readonly AsyncRelayCommand _estimate;
    private readonly RelayCommand _apply;
    private CancellationTokenSource? _cts;
    private TempoEstimate? _result;
    private bool _isWholeSong = true;
    private bool _isMarkedPart;
    private bool _useEstimate = true;
    private bool _useHalf;
    private bool _useDouble;
    private bool _isBusy;
    private string _statusText = "";
    private bool _statusIsProblem;
    private string _targetText = "";

    public EstimateBpmViewModel(ChartEditDialogServices edit, ITempoEstimateService service, ITempoService tempo,
        IEditorTransport transport, IPlaybackTransport playback)
    {
        _edit = edit;
        _service = service;
        _tempo = tempo;
        _transport = transport;
        _loc = edit.Localization;
        _audioPath = playback.CurrentAudioPath ?? "";
        var loop = edit.State.LoopRegion;
        HasMarkedPart = loop.HasValue && loop.Value.EndMs > loop.Value.StartMs;
        MarkedPartText = loop is { } l && HasMarkedPart
            ? _loc.Format("Dialog_EstimateBpm_Range_MarkedDesc", Seconds(l.StartMs), Seconds(l.EndMs))
            : _loc["Dialog_EstimateBpm_NoMarkers"];
        SourceText = string.IsNullOrEmpty(_audioPath) ? _loc["Dialog_EstimateBpm_NoAudio"] : Path.GetFileName(_audioPath);
        _estimate = new AsyncRelayCommand(EstimateAsync, () => !IsBusy && !string.IsNullOrEmpty(_audioPath));
        _apply = new RelayCommand(Apply, () => _result != null && !IsBusy);
        CancelCommand = new RelayCommand(() => TryCancel());
        edit.Logger.Info(AppLogCategory.Events, $"Estimate-BPM dialog opened. audio={!string.IsNullOrEmpty(_audioPath)}, markers={HasMarkedPart}.");
    }

    public string SourceText { get; }

    public bool IsWholeSong
    {
        get => _isWholeSong;
        set => SetRange(value, true);
    }

    public bool IsMarkedPart
    {
        get => _isMarkedPart;
        set => SetRange(value && HasMarkedPart, false);
    }

    public bool HasMarkedPart { get; }

    public string MarkedPartText { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            _estimate.NotifyCanExecuteChanged();
            _apply.NotifyCanExecuteChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool StatusIsProblem
    {
        get => _statusIsProblem;
        private set => SetProperty(ref _statusIsProblem, value);
    }

    public bool HasResult => _result != null;

    public string BpmText => _result == null ? "" : Format(_result.Bpm);

    public string HalfText => _result?.Half is { } half ? Format(half) : "";

    public string DoubleText => _result?.Double is { } twice ? Format(twice) : "";

    public bool HasHalf => _result?.Half != null;

    public bool HasDouble => _result?.Double != null;

    public bool UseEstimate
    {
        get => _useEstimate;
        set => Choose(value, 0);
    }

    public bool UseHalf
    {
        get => _useHalf;
        set => Choose(value && HasHalf, 1);
    }

    public bool UseDouble
    {
        get => _useDouble;
        set => Choose(value && HasDouble, 2);
    }

    public string TargetText
    {
        get => _targetText;
        private set => SetProperty(ref _targetText, value);
    }

    public ICommand EstimateCommand => _estimate;

    public ICommand ApplyCommand => _apply;

    public ICommand CancelCommand { get; }

    /// <summary>Esc during a run cancels the run and keeps the dialog; otherwise it closes unchanged.</summary>
    public bool TryCancel()
    {
        if (IsBusy)
        {
            _cts?.Cancel();
            return false;
        }

        if (!IsClosed) _edit.Logger.Info(AppLogCategory.Events, "Estimate-BPM dialog canceled.");
        Close(false);
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private void SetRange(bool value, bool whole)
    {
        if (!value) return;
        var changed = SetProperty(ref _isWholeSong, whole, nameof(IsWholeSong)) |
                      SetProperty(ref _isMarkedPart, !whole, nameof(IsMarkedPart));
        if (changed) Invalidate();
    }

    private void Choose(bool value, int which)
    {
        if (!value) return;
        SetProperty(ref _useEstimate, which == 0, nameof(UseEstimate));
        SetProperty(ref _useHalf, which == 1, nameof(UseHalf));
        SetProperty(ref _useDouble, which == 2, nameof(UseDouble));
    }

    private async Task EstimateAsync()
    {
        Invalidate();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        SetStatus(_loc["Dialog_EstimateBpm_Listening"], false);
        double? start = null;
        double? end = null;
        if (IsMarkedPart && _edit.State.LoopRegion is { } loop)
        {
            start = loop.StartMs;
            end = loop.EndMs;
        }

        try
        {
            var outcome = await _service.EstimateAsync(_audioPath, start, end, _cts.Token);
            if (outcome.Estimate is { } estimate) Show(estimate);
            else SetStatus(FailureText(outcome.Failure), true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(_loc["Dialog_EstimateBpm_Canceled"], false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(TempoEstimate estimate)
    {
        _result = estimate;
        Choose(true, 0);
        var beat = TempoEstimateApplier.StartBeatIndex(_edit.Editor.Beats, estimate.FromMs);
        TargetText = _loc.Format("Dialog_EstimateBpm_Target", beat + 1);
        SetStatus("", false);
        RaiseResult();
        _apply.NotifyCanExecuteChanged();
    }

    private void Apply()
    {
        if (_result == null || _edit.State.ActiveProject is not { } project) return;
        if (_edit.RefuseIfBeatMapLocked("Estimate BPM"))
        {
            Close(false);
            return;
        }

        var bpm = _useHalf ? _result.Half : _useDouble ? _result.Double : _result.Bpm;
        var editor = _edit.Editor;
        var plan = bpm is { } chosen ? TempoEstimateApplier.Plan(_tempo, editor.Beats, chosen, _result.FromMs) : null;
        if (bpm == null || plan == null)
        {
            SetStatus(_loc["Dialog_EstimateBpm_Invalid"], true);
            return;
        }

        var applied = _edit.History.Do(new MutateBeatsAction(editor.Beats, editor.Beats.ToList(), plan, () =>
        {
            ChartSpacingEnforcer.EnforceProject(project, _edit.Config.LoadAppConfig());
            if (editor.Beats.Count > 0 && editor.Beats[0].Bpm > 0)
            {
                project.Bpm = editor.Beats[0].Bpm;
                _transport.Bpm = editor.Beats[0].Bpm;
            }

            editor.RequestRedraw();
        }));
        _edit.Logger.Info(AppLogCategory.Events, $"Estimate-BPM apply. Bpm={bpm:0.##}, from={_result.FromMs:0}ms, applied={applied}.");
        if (applied) _edit.State.SetMessage(_loc.Format("Status_EstimateBpmApplied", Format(bpm.Value)));
        Close(applied);
    }

    private void Invalidate()
    {
        if (_result == null) return;
        _result = null;
        RaiseResult();
        _apply.NotifyCanExecuteChanged();
    }

    private void RaiseResult()
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(BpmText));
        OnPropertyChanged(nameof(HalfText));
        OnPropertyChanged(nameof(DoubleText));
        OnPropertyChanged(nameof(HasHalf));
        OnPropertyChanged(nameof(HasDouble));
    }

    private void SetStatus(string text, bool problem)
    {
        StatusText = text;
        StatusIsProblem = problem;
    }

    private string FailureText(TempoEstimateFailure failure) => failure switch
    {
        TempoEstimateFailure.NoAudio => _loc["Dialog_EstimateBpm_NoAudio"],
        TempoEstimateFailure.DecodeFailed => _loc["Dialog_EstimateBpm_DecodeFailed"],
        _ => _loc["Dialog_EstimateBpm_NoTempo"]
    };

    private string Format(double bpm) => _loc.Format("Dialog_EstimateBpm_Bpm", bpm.ToString("0.##", CultureInfo.CurrentCulture));

    private static string Seconds(double ms) => (ms / 1000.0).ToString("0.0", CultureInfo.CurrentCulture);
}
