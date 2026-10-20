using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Project;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Ai.Match;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Opens the AI tone creator from the Tone designer.</summary>
/// <param name="EditedKey">The tone being edited, which Replace writes into; null when none is selected.</param>
/// <param name="EditedName">Its display name.</param>
/// <param name="Preview">The designer's preview target, which Audition plays the proposal through; null for none.</param>
public sealed record ToneCreatorRequest(string? EditedKey, string? EditedName, ITonePreviewTarget? Preview)
    : IDialogRequest<ToneCreatorRequest, IToneCreatorScreen, string?>;

/// <summary>
///     The Tone designer's AI tone creator, one instance per open. The provider call runs off the UI thread through
///     <see cref="IToneCreator" /> (Rocksmith catalogue, reject never repair) and can be stopped; Accept writes the
///     proposal as ONE undo step (<see cref="DotCharter.Services.Editing.Tones.ToneCreatorAccept" />, in
///     <c>ToneCreatorViewModel.Accept.cs</c>); Cancel writes nothing. Check with audio runs
///     <see cref="IRsToneFeedbackLoop" /> over the span at the playhead and keeps its best as the proposal, saying who
///     judged it: Rocksmith itself, or the clone only and why.
/// </summary>
public sealed partial class ToneCreatorViewModel : DialogViewModel<string?>, IToneCreatorScreen, IDialogSetup<ToneCreatorRequest>,
    ICancelableDialog, IDisposable
{
    private readonly ChartEditDialogServices _edit;
    private readonly IToneCreator _creator;
    private readonly IRsToneFeedbackLoop _loop;
    private readonly AsyncRelayCommand _check;
    private readonly ChartProject? _project;
    private readonly Arrangement? _arrangement;
    private readonly string _instrument = ToneStems.Guitar;
    private ToneCreatorWindow? _window;
    private readonly AsyncRelayCommand _create;
    private readonly AsyncRelayCommand _refine;
    private readonly RelayCommand _stop;
    private CancellationTokenSource? _call;
    private ITonePreviewTarget? _preview;
    private ToneCreatorProposal? _proposal;
    private string _description = "";
    private string _refineText = "";
    private string _status = "";
    private bool _useSongEvidence;
    private bool _isBusy;
    private bool _isAuditioning;

    public ToneCreatorViewModel(ChartEditDialogServices edit, IToneCreator creator, IRsToneFeedbackLoop loop)
    {
        _edit = edit;
        _creator = creator;
        _loop = loop;
        _check = new AsyncRelayCommand(CheckAsync, () => !_isBusy && CanCheck);
        _create = new AsyncRelayCommand(() => AskAsync(null, null), () => !_isBusy && CanCreate);
        _refine = new AsyncRelayCommand(() => AskAsync(_proposal?.Tone, _refineText), () => !_isBusy && _proposal is not null &&
            !string.IsNullOrWhiteSpace(_refineText));
        _stop = new RelayCommand(() => _call?.Cancel(), () => _isBusy);
        AcceptCommand = new RelayCommand(Accept, () => !_isBusy && AcceptUnavailableReason.Length == 0);
        CancelCommand = new RelayCommand(() => Finish(null));

        _project = edit.State.ActiveProject;
        if (_project is null)
        {
            _status = edit.Localization["Status_Export_NoProject"];
            return;
        }

        _arrangement = edit.State.ActiveArrangement is { ArrangementType: not ArrangementType.Vocal } active
            ? active
            : _project.Arrangements.FirstOrDefault(a => a.ArrangementType != ArrangementType.Vocal);
        if (_arrangement is null)
        {
            _status = edit.Localization["Dialog_Tones_NoArrangements"];
            return;
        }

        _instrument = ToneCreatorEvidence.InstrumentOf(_project, _arrangement);
        _window = ToneCreatorEvidence.AtPlayhead(edit.State, _arrangement);
        _isSaveAsNew = true;
    }

    public string Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value ?? "")) _create.NotifyCanExecuteChanged();
        }
    }

    public int MaxLength => AiToneContextText.MaxLength;

    public bool IsRocksmithGearOnly
    {
        get => true;
        set => OnPropertyChanged();
    }

    /// <summary>Refused in the chart's designer: there is no DotCharter-rig to Rocksmith-tone conversion to save it through.</summary>
    public bool IsDotCharterRig
    {
        get => false;
        set => OnPropertyChanged();
    }

    public bool CanUseDotCharterRig => false;

    public string DotCharterRigReason => _edit.Localization["Dialog_ToneCreator_ScopeRigReason"];

    public bool UseSongEvidence
    {
        get => _useSongEvidence;
        set
        {
            if (SetProperty(ref _useSongEvidence, value && CanUseSongEvidence)) _create.NotifyCanExecuteChanged();
            else if (value && !CanUseSongEvidence) OnPropertyChanged();
        }
    }

    public bool CanUseSongEvidence => _window is not null;

    public string SongEvidenceText => _window is { } w
        ? w.ToneName is { } tone
            ? _edit.Localization.Format("Dialog_ToneCreator_EvidenceTone", ToneStemNames.Display(_instrument, _edit.Localization),
                Clock(w.FromMs), Clock(w.ToMs), tone)
            : _edit.Localization.Format("Dialog_ToneCreator_Evidence", ToneStemNames.Display(_instrument, _edit.Localization), Clock(w.FromMs),
                Clock(w.ToMs))
        : _edit.Localization["Dialog_ToneCreator_EvidenceNone"];

    public ICommand CreateCommand => _create;

    public ICommand StopCommand => _stop;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            _create.NotifyCanExecuteChanged();
            _refine.NotifyCanExecuteChanged();
            _check.NotifyCanExecuteChanged();
            _stop.NotifyCanExecuteChanged();
            ((RelayCommand)AcceptCommand).NotifyCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0;

    public bool HasProposal => _proposal is not null;

    public IReadOnlyList<IToneProposalRow> ProposalRows { get; private set; } = [];

    public string Rationale => _proposal?.Rationale ?? "";

    public string EvidenceNote => _proposal is null ? ""
        : _proposal.Measured ? _edit.Localization.Format("Dialog_ToneCreator_Measured", ToneStemNames.Display(_instrument, _edit.Localization))
        : _edit.Localization["Dialog_ToneCreator_WordsOnly"];

    public bool IsAuditioning
    {
        get => _isAuditioning;
        set
        {
            if (value && !CanAudition) value = false;
            if (!SetProperty(ref _isAuditioning, value))
            {
                OnPropertyChanged();
                return;
            }

            if (value) _preview!.Apply(_proposal!.Tone);
            else _preview?.Stop();
        }
    }

    public bool CanAudition => _preview is { IsAvailable: true } && _proposal is not null;

    public string AuditionUnavailableReason =>
        _preview is not { IsAvailable: true } ? _edit.Localization["Dialog_Tones_PreviewUnavailable"]
        : _proposal is null ? _edit.Localization["Dialog_ToneCreator_CreateFirst"]
        : "";

    public string RefineText
    {
        get => _refineText;
        set
        {
            if (SetProperty(ref _refineText, value ?? "")) _refine.NotifyCanExecuteChanged();
        }
    }

    public ICommand RefineCommand => _refine;

    public ICommand CheckCommand => _check;

    /// <summary>The check compares renders against the song, so it needs the span at the playhead.</summary>
    public bool CanCheck => _window is not null && _arrangement is not null;

    public string CheckUnavailableReason => CanCheck ? "" : _edit.Localization["Dialog_ToneCreator_CheckNeedsSong"];

    public bool HasCheckResult => CheckRounds.Count > 0;

    public string CheckJudgeText { get; private set; } = "";

    public IReadOnlyList<string> CheckRounds { get; private set; } = [];

    public ICommand AcceptCommand { get; }

    public ICommand CancelCommand { get; }

    private bool CanCreate => _arrangement is not null && (!string.IsNullOrWhiteSpace(_description) || _useSongEvidence);

    public void Configure(ToneCreatorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _preview = request.Preview;
        _editedKey = _project is not null && request.EditedKey is { } key && AiToneApplier.Find(_project, key) is { } edited
            ? edited.Key
            : null;
        _editedName = string.IsNullOrWhiteSpace(request.EditedName) ? _editedKey : request.EditedName;
        if (_editedKey is not null) IsReplaceEdited = true;
        // The edited tone's own placement is a better span to measure than wherever the playhead is.
        if (_editedKey is not null && _arrangement is not null)
        {
            _window = ToneCreatorEvidence.AtPlayhead(_edit.State, _arrangement, _editedKey) ?? _window;
            OnPropertyChanged(nameof(SongEvidenceText));
            OnPropertyChanged(nameof(CanUseSongEvidence));
            OnPropertyChanged(nameof(CanCheck));
            OnPropertyChanged(nameof(CheckUnavailableReason));
            _check.NotifyCanExecuteChanged();
        }
        RaiseAll();
    }

    /// <summary>Esc cancels: nothing is written.</summary>
    public bool TryCancel()
    {
        Finish(null);
        return true;
    }

    /// <summary>Stops a call in flight and puts the preview target's own tone back.</summary>
    public void Dispose()
    {
        _call?.Cancel();
        IsAuditioning = false;
    }

    /// <summary>One ask (a Refine carries the current proposal and the change), off the UI thread, cancellable.</summary>
    private Task AskAsync(RsTone? previous, string? refinement) =>
        CallAsync("Dialog_ToneCreator_Asking", _useSongEvidence ? _window : null, previous, refinement, async (ask, ct) =>
        {
            var proposal = await Task.Run(() => _creator.ProposeAsync(ask, ct), ct);
            if (IsClosed) return;
            SetProposal(proposal);
            Status = "";
            if (previous is not null) RefineText = "";
        });

    /// <summary>
    ///     The audio check over the span at the playhead, starting from the current proposal when there is one. The
    ///     creator's own Audition is stopped first: an audition through Rocksmith routes the guitar through the game,
    ///     and the check never renders through a game a live preview is playing through.
    /// </summary>
    private Task CheckAsync()
    {
        IsAuditioning = false;
        return CallAsync("Dialog_ToneCreator_Checking", _window, _proposal?.Tone, null, async (ask, ct) =>
        {
            RsToneLoopResult result;
            try
            {
                result = await Task.Run(() => _loop.RunAsync(ask, ct), ct);
            }
            catch (StemsNotCachedException)
            {
                Status = _edit.Localization["Dialog_ToneCreator_CheckNoStem"];
                return;
            }

            if (IsClosed) return;
            ShowCheck(result);
            if (result.Best is { Refined: { } tone } best)
            {
                SetProposal(new ToneCreatorProposal(tone, best.Rationale, best.ModelId, true));
                Status = "";
            }
            else
            {
                Status = _edit.Localization["Dialog_ToneCreator_CheckNothing"];
            }
        });
    }

    private async Task CallAsync(string busyKey, ToneCreatorWindow? window, RsTone? previous, string? refinement,
        Func<ToneCreatorAsk, CancellationToken, Task> work)
    {
        if (_project is null || _arrangement is null) return;
        _call?.Cancel();
        using var call = new CancellationTokenSource();
        _call = call;
        IsBusy = true;
        Status = _edit.Localization[busyKey];
        var ask = new ToneCreatorAsk(_description, _instrument, "ai_tone", TargetName, ToneCreatorEvidence.ContextOf(_project, _arrangement),
            _project.Chart.SongName, _project.Chart.Artist, _arrangement.Name, window, previous, refinement);
        try
        {
            await work(ask, call.Token);
        }
        catch (OperationCanceledException)
        {
            Status = _edit.Localization["Dialog_ToneCreator_Stopped"];
        }
        catch (RsToneCatalogException ex)
        {
            _edit.Logger.Warning(AppLogCategory.Project, $"AI tone creator: the reply was refused — {ex.Message}");
            Status = _edit.Localization.Format("Dialog_ToneCreator_Refused", ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _edit.Logger.Warning(AppLogCategory.Project, $"AI tone creator: the call failed — {ex.Message}");
            Status = _edit.Localization.Format("Dialog_ToneCreator_Failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_call, call)) _call = null;
            IsBusy = false;
        }
    }

    /// <summary>The check's provenance on screen: who judged the kept tone, and one line per round.</summary>
    private void ShowCheck(RsToneLoopResult result)
    {
        CheckJudgeText = ToneCreatorCheckText.Judge(_edit.Localization, result);
        CheckRounds = result.Rounds.Select(r => ToneCreatorCheckText.Round(_edit.Localization, r)).ToList();
        _edit.Logger.Info(AppLogCategory.Project,
            $"AI tone check: judge={result.Judge}, clone-only reason={result.CloneOnlyReason ?? "none"}, {string.Join(" | ", CheckRounds)}");
        OnPropertyChanged(nameof(CheckJudgeText));
        OnPropertyChanged(nameof(CheckRounds));
        OnPropertyChanged(nameof(HasCheckResult));
    }

    private void SetProposal(ToneCreatorProposal proposal)
    {
        _proposal = proposal;
        ProposalRows = Rows(proposal.Tone);
        if (string.IsNullOrWhiteSpace(_newToneName)) _newToneName = proposal.Tone.Name;
        if (_isAuditioning) _preview?.Apply(proposal.Tone);
        _edit.Logger.Info(AppLogCategory.Project,
            $"AI tone creator proposed {AiToneText.Summary(proposal.Tone)} ({proposal.ModelId}, measured={proposal.Measured}).");
        RaiseAll();
    }

    private IReadOnlyList<IToneProposalRow> Rows(RsTone tone)
    {
        var loc = _edit.Localization;
        var rows = new List<IToneProposalRow>(14);
        rows.AddRange(tone.PrePedals.Select(p => new ProposalRow(loc.Format("Dialog_Tones_SlotPre", p.SlotIndex), AiToneText.Gear(p))));
        rows.Add(new ProposalRow(loc["Dialog_Tones_SlotAmp"], AiToneText.Gear(tone.Amp)));
        rows.Add(new ProposalRow(loc["Dialog_Tones_SlotCabinet"], AiToneText.Gear(tone.Cabinet)));
        rows.AddRange(tone.Racks.Select(p => new ProposalRow(loc.Format("Dialog_Tones_SlotRack", p.SlotIndex), AiToneText.Gear(p))));
        rows.AddRange(tone.PostPedals.Select(p => new ProposalRow(loc.Format("Dialog_Tones_SlotLoop", p.SlotIndex), AiToneText.Gear(p))));
        rows.Add(new ProposalRow(loc["Dialog_Tones_Volume"],
            tone.VolumeDb.ToString("0.0 'dB'", System.Globalization.CultureInfo.CurrentCulture)));
        return rows;
    }

    private void Finish(string? key)
    {
        if (IsClosed) return;
        _call?.Cancel();
        IsAuditioning = false;
        Close(key);
    }

    private sealed record ProposalRow(string Role, string Gear) : IToneProposalRow;
}
