using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Tempo map ▸ Align to recording. One instance per open; nothing is written before Apply.</summary>
public sealed class AlignToRecordingViewModel : DialogViewModel<bool>, IAlignToRecordingScreen, ICancelableDialog, IDisposable
{
    private readonly ProjectState _state;
    private readonly IRecordingAlignmentService _service;
    private readonly ILocalizationService _loc;
    private readonly IAppLogger _logger;
    private readonly string _audioPath;
    private readonly RelayCommand _apply;
    private readonly AsyncRelayCommand _analyze;
    private readonly int[] _bars;
    private CancellationTokenSource? _cts;
    private RecordingAlignmentProposal? _proposal;
    private IReadOnlyList<IAlignSectionRow> _sections = [];
    private bool _isWholeSong = true;
    private bool _isFromBar;
    private bool _isLoopRegion;
    private double _fromBar = 1;
    private bool _keepAnchors = true;
    private bool _isBusy;
    private string _statusText = "";
    private bool _statusIsProblem;
    private string _summaryText = "";

    public AlignToRecordingViewModel(ProjectState state, IRecordingAlignmentService service, IPlaybackTransport playback,
        DialogBasics basics)
    {
        _state = state;
        _service = service;
        _loc = basics.Localization;
        _logger = basics.Logger;
        _audioPath = playback.CurrentAudioPath;
        var beats = state.ActiveProject?.Beats.ToList() ?? [];
        _bars = RecordingAlignmentPlan.Bars(beats);
        BarCount = _bars.Length == 0 ? 1 : _bars[^1];
        HasLoopRegion = state.LoopRegion.HasValue && beats.Count > 0;
        LoopRegionText = state.LoopRegion is { } loop && beats.Count > 0
            ? _loc.Format("Dialog_AlignToRecording_Bars", BarAt(beats, loop.StartMs), BarAt(beats, loop.EndMs))
            : _loc["Dialog_AlignToRecording_NoLoop"];
        var anchors = beats.Count(b => b.IsAnchor);
        AnchorsText = anchors == 0
            ? _loc["Dialog_AlignToRecording_AnchorsNone"]
            : _loc.Plural("Dialog_AlignToRecording_AnchorsOne", "Dialog_AlignToRecording_AnchorsMany", anchors);
        SourceText = string.IsNullOrEmpty(_audioPath) ? _loc["Dialog_AlignToRecording_NoAudio"] : Path.GetFileName(_audioPath);

        _analyze = new AsyncRelayCommand(AnalyzeAsync, () => !IsBusy);
        _apply = new RelayCommand(Apply, () => _proposal != null && !IsBusy && _sections.Any(s => s.IsApplied));
        CancelCommand = new RelayCommand(() => TryCancel());
        if (string.IsNullOrEmpty(_audioPath)) SetStatus(_loc["Dialog_AlignToRecording_NoAudio"], true);
        _logger.Info(AppLogCategory.Events, $"Align-to-recording dialog opened. beats={beats.Count}, anchors={anchors}, audio={!string.IsNullOrEmpty(_audioPath)}.");
    }

    public string SourceText { get; }

    public bool IsWholeSong
    {
        get => _isWholeSong;
        set => SetRange(value, 0);
    }

    public bool IsFromBar
    {
        get => _isFromBar;
        set => SetRange(value, 1);
    }

    public bool IsLoopRegion
    {
        get => _isLoopRegion;
        set => SetRange(value && HasLoopRegion, 2);
    }

    public bool HasLoopRegion { get; }

    public double FromBar
    {
        get => _fromBar;
        set
        {
            if (!SetProperty(ref _fromBar, Math.Clamp(Math.Round(value), 1, BarCount))) return;
            IsFromBar = true;
            Invalidate();
        }
    }

    public double BarCount { get; }

    public string LoopRegionText { get; }

    public bool KeepAnchors
    {
        get => _keepAnchors;
        set
        {
            if (SetProperty(ref _keepAnchors, value)) Invalidate();
        }
    }

    public string AnchorsText { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            _analyze.NotifyCanExecuteChanged();
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

    public bool HasResult => _proposal != null;

    public IReadOnlyList<IAlignSectionRow> Sections
    {
        get => _sections;
        private set => SetProperty(ref _sections, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public ICommand AnalyzeCommand => _analyze;

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

        if (!IsClosed) _logger.Info(AppLogCategory.Events, "Align-to-recording dialog canceled.");
        Close(false);
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private void SetRange(bool value, int which)
    {
        if (!value) return;
        var changed = SetProperty(ref _isWholeSong, which == 0, nameof(IsWholeSong)) |
                      SetProperty(ref _isFromBar, which == 1, nameof(IsFromBar)) |
                      SetProperty(ref _isLoopRegion, which == 2, nameof(IsLoopRegion));
        if (changed) Invalidate();
    }

    private RecordingAlignmentRange Range() =>
        IsFromBar ? RecordingAlignmentRange.FromBarNumber((int)FromBar)
        : IsLoopRegion && _state.LoopRegion is { } loop ? RecordingAlignmentRange.Span(loop.StartMs, loop.EndMs)
        : RecordingAlignmentRange.WholeSong;

    private async Task AnalyzeAsync()
    {
        if (_state.ActiveProject is not { } project) return;
        Invalidate();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        SetStatus(_loc["Dialog_AlignToRecording_Analyzing"], false);
        try
        {
            var analysis = await _service.AnalyzeAsync(project, Range(), KeepAnchors, _audioPath, _cts.Token);
            if (analysis.Proposal is { } proposal) Show(proposal);
            else SetStatus(FailureText(analysis.Failure), true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(_loc["Dialog_AlignToRecording_Canceled"], false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(RecordingAlignmentProposal proposal)
    {
        var rows = proposal.Sections.Where(s => s.FreeBeats > 0).Select(s => new AlignSectionRow(s, Title(s), _loc)).ToList();
        foreach (var row in rows) row.PropertyChanged += (_, _) => RefreshSummary();
        _proposal = proposal;
        Sections = rows;
        OnPropertyChanged(nameof(HasResult));
        SetStatus("", false);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        if (_proposal == null) return;
        var applied = _sections.Count(s => s.IsApplied);
        SummaryText = _loc.Format(_proposal.Source == RecordingAlignmentSource.Stems
            ? "Dialog_AlignToRecording_SummaryStems"
            : "Dialog_AlignToRecording_SummaryMix", applied, _sections.Count);
        _apply.NotifyCanExecuteChanged();
    }

    private void Apply()
    {
        if (_proposal == null || _state.ActiveProject is not { } project) return;
        var accepted = _sections.OfType<AlignSectionRow>().Where(r => r.IsApplied).Select(r => r.Index).ToHashSet();
        var status = _service.Apply(project, _proposal, accepted);
        _logger.Info(AppLogCategory.Events, $"Align-to-recording apply: {status}, sections={accepted.Count}/{_sections.Count}.");
        switch (status)
        {
            case RecordingAlignmentApplyStatus.Applied:
                _state.SetMessage(_loc.Plural("Status_AlignToRecordingAppliedOne", "Status_AlignToRecordingApplied", accepted.Count));
                Close(true);
                break;
            case RecordingAlignmentApplyStatus.NoChange:
                Close(false);
                break;
            case RecordingAlignmentApplyStatus.Stale:
                Invalidate();
                SetStatus(_loc["Dialog_AlignToRecording_Stale"], true);
                break;
            default:
                SetStatus(_loc[status == RecordingAlignmentApplyStatus.Locked ? "Status_BeatMapLocked" : "Dialog_AlignToRecording_Invalid"], true);
                break;
        }
    }

    private void Invalidate()
    {
        if (_proposal == null) return;
        _proposal = null;
        Sections = [];
        SummaryText = "";
        OnPropertyChanged(nameof(HasResult));
        _apply.NotifyCanExecuteChanged();
    }

    private void SetStatus(string text, bool problem)
    {
        StatusText = text;
        StatusIsProblem = problem;
    }

    private string Title(RecordingAlignmentSectionResult s) =>
        string.IsNullOrWhiteSpace(s.Span.Name)
            ? _loc.Format("Dialog_AlignToRecording_SectionUnnamed", s.Span.FirstBar, s.Span.LastBar)
            : _loc.Format("Dialog_AlignToRecording_Section", CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.Span.Name), s.Span.FirstBar,
                s.Span.LastBar);

    private string FailureText(RecordingAlignmentFailure failure) => failure switch
    {
        RecordingAlignmentFailure.NoAudio => _loc["Dialog_AlignToRecording_NoAudio"],
        RecordingAlignmentFailure.DecodeFailed => _loc["Dialog_AlignToRecording_DecodeFailed"],
        RecordingAlignmentFailure.Locked => _loc["Status_BeatMapLocked"],
        RecordingAlignmentFailure.TooFewBeats => _loc["Dialog_AlignToRecording_TooFewBeats"],
        _ => _loc["Dialog_AlignToRecording_NothingToAlign"]
    };

    private int BarAt(IReadOnlyList<BeatMarker> beats, double ms)
    {
        var i = 0;
        while (i + 1 < beats.Count && beats[i + 1].Time <= ms) i++;
        return _bars[i];
    }
}
