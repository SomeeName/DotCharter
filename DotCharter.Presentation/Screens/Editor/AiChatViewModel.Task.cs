// AiChatViewModel: what follows the Task picker — its measured fingering default and reason, the empty pane's line and the box's hint, the scope line and the reasoning label, and the findings door: the Issues tab's Fix / Explain with AI as chat messages, and "Ask the AI to fix these" after a pass or an Accept (a menu verb's too).
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Presentation.Screens.Editor;

public sealed partial class AiChatViewModel : IAiChatFindingsDoor
{
    public bool HasJobNote => JobNote.Length > 0;

    partial void OnSelectedArrangementChanged(IAiChatOption? value)
    {
        RefreshScopeSummary();
        ApplyDefaultJob();
    }

    partial void OnJobNoteChanged(string value) => OnPropertyChanged(nameof(HasJobNote));

    partial void OnSelectedModelChanged(IAiChatOption? value) => RefreshEffort();

    /// <summary>The charter's pick is remembered per arrangement type; the pane's own is not.
    /// The Tones task reads the same three scopes as TONES, so the summary follows the task.</summary>
    partial void OnSelectedJobChanged(IAiChatOption? value)
    {
        if (!_applyingDefaultJob) _defaults.Picked(Job(), IsBass());
        RefreshJobNote();
        RefreshJobText();
        RefreshScopeSummary();
    }

    /// <summary>The task the pane's picker names; the measured default when nothing is picked.</summary>
    private AiChartJob Job() => SelectedJob is AiChatOptionItem { Key: AiChartJob job } ? job : AiFingeringDefaults.Measured;

    private bool IsBass() => AiFingeringDefaults.IsBass(
        SelectedArrangement is AiChatOptionItem { Key: Arrangement a } ? a : _state.ActiveArrangement);

    /// <summary>On a fingering task, the one this arrangement type starts on; Sections and
    /// Explain stay, since a new arrangement must not undo a switch away from fingering.</summary>
    private void ApplyDefaultJob()
    {
        if (_defaults.StartOn(Job(), IsBass()) is { } wanted)
        {
            _applyingDefaultJob = true;
            try
            {
                SelectedJob = Jobs.FirstOrDefault(j => j is AiChatOptionItem { Key: AiChartJob job } && job == wanted) ?? Jobs[0];
            }
            finally
            {
                _applyingDefaultJob = false;
            }
        }

        RefreshJobNote();
    }

    private void RefreshJobNote() => JobNote = _defaults.Note(Job(), IsBass());

    /// <summary>What the picked task does and what to type; without a chart, how to get one.</summary>
    private void RefreshJobText()
    {
        EmptyText = _localization[_state.ActiveProject is null ? "AiChat_EmptyNoChart" : AiChatJobText.EmptyKey(Job())];
        InputHint = _localization[AiChatJobText.HintKey(Job())];
    }

    private void RefreshEffort() => EffortText = HasProvider ? _defaults.EffortLabel(ModelId()) : "";

    private void RefreshScopeSummary()
    {
        if (Job() == AiChartJob.Tones)
        {
            ScopeSummary = _toneScope.Summary(Scope(), ChosenArrangement(), _selection.SelectedTones);
            return;
        }

        ScopeSummary = Scope() switch
        {
            AiChatScopeKind.Selection => _selection.SelectedNotes.Count + _selection.SelectedChords.Count == 0
                ? _localization["AiChat_Summary_NothingSelected"]
                : _localization.Format("AiChat_Summary_Selection", _selection.SelectedNotes.Count, _selection.SelectedChords.Count),
            AiChatScopeKind.Section => string.IsNullOrWhiteSpace(_state.CurrentSectionDisplay)
                ? _localization["AiChat_Summary_NoSection"]
                : _localization.Format("AiChat_Summary_Section", _state.CurrentSectionDisplay),
            _ => SelectedArrangement is AiChatOptionItem { Key: Arrangement arrangement } &&
                 arrangement.Levels.MaxBy(l => l.DifficultyIndex) is { } level
                ? _localization.Format("AiChat_Summary_Arrangement", arrangement.Name, level.Notes.Count, level.Chords.Count)
                : _localization["AiChat_Summary_NoArrangement"]
        };
    }

    public event Action? ShowRequested;

    public bool CanAsk => HasProvider && !IsBusy && _chartId is not null;

    public string? PickedModelId => ModelId() is { Length: > 0 } id ? id : null;

    public async Task<bool> AskAsync(AiChatInstructionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ShowRequested?.Invoke();
        if (!CanAsk)
        {
            ErrorText = HasProvider ? _localization["AiChat_Busy_Findings"] : NoProviderText;
            return false;
        }

        var conversation = _conversation ?? _store.StartNew(ModelId());
        if (conversation is null) return false;
        ErrorText = "";
        conversation = conversation.Append(new AiChatTurn(AiChatRole.User, request.Instruction, _time.GetUtcNow()));
        Show(conversation);
        await RunAsync(conversation, request with { Conversation = conversation, ModelId = request.ModelId ?? PickedModelId });
        return true;
    }

    private async Task FixAsync()
    {
        if (_followUp is not { } offered || _conversation is null) return;
        _followUp = null;
        await AskAsync(offered.Request);
    }

    /// <summary>A review one of THIS conversation's replies proposed, or a menu verb's, was
    /// accepted: what it left in its range becomes the assistant's turn, with the fix on offer.</summary>
    private void OnApplied(AiChatApplied applied)
    {
        var conversation = applied.FromMenu && _chartId is not null ? _conversation ?? _store.StartNew(ModelId()) : _conversation;
        if (conversation is null || conversation.ChartId != _chartId ||
            (!applied.FromMenu && !conversation.Turns.Any(t => t.ReviewId == applied.ReviewId)))
        {
            return;
        }

        var turn = new AiChatTurn(AiChatRole.Assistant, applied.Text, _time.GetUtcNow());
        conversation = conversation.Append(turn);
        _followUp = applied.FollowUp is null ? null : (turn, applied.FollowUp);
        Show(conversation);
        _fix.NotifyCanExecuteChanged();
        _ = SaveAppliedAsync(conversation);
    }

    private async Task SaveAppliedAsync(AiChatConversation conversation)
    {
        var result = await _store.SaveAsync(conversation).ConfigureAwait(false);
        await _ui.InvokeAsync(() => Saved(conversation, result)).ConfigureAwait(false);
    }
}
