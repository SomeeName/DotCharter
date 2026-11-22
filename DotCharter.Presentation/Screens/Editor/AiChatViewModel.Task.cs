// AiChatViewModel: what follows the Task picker — its measured fingering default and reason, the empty pane's line and the box's hint, the scope line and the reasoning label, and the findings door: the Issues tab's Fix / Explain with AI as chat messages, and "Ask the AI to fix these" after a pass or an Accept (a menu verb's too).
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Tone.Infrastructure.Configuration;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;

namespace DotCharter.Presentation.Screens.Editor;

public sealed partial class AiChatViewModel : IAiChatFindingsDoor
{
    public bool HasJobNote => JobNote.Length > 0;

    partial void OnSelectedArrangementChanged(IAiChatOption? value)
    {
        if (!_syncingChoices) ArrangementPicker.SelectOnly(ChosenArrangement());
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
        _send.NotifyCanExecuteChanged();
    }

    /// <summary>The task the pane's picker names; the measured default when nothing is picked.</summary>
    private AiChartJob Job() => SelectedJob is AiChatOptionItem { Key: { } key } ? AiChatJobText.Resolve(key).Job : AiFingeringDefaults.Measured;

    /// <summary>The naming style the chord names task runs in; unused by every other task.</summary>
    private ChordNamingStyle Style() => SelectedJob is AiChatOptionItem { Key: { } key } ? AiChatJobText.Resolve(key).Style : ChordNamingStyle.Eof;

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
        var single = SingleSummary();
        ScopeSummary = ArrangementPicker.ManySummary(single) ?? single;
    }

    private string SingleSummary() => Job() == AiChartJob.Tones
        ? _toneScope.Summary(Scope(), ChosenArrangement(), _selection.SelectedTones)
        : AiChatScopeLines.Single(_localization, _selection, _state, Scope(), ChosenArrangement());

    /// <summary>The pickers keep their items, and so the picks, and read their labels again.</summary>
    private void OnLanguageChanged()
    {
        AiChatJobText.Relabel([.. Jobs, .. Scopes], _localization);
        RefreshJobText();
        RefreshJobNote();
        RefreshScopeSummary();
    }

    public event Action? ShowRequested;

    /// <summary>
    ///     A palette id's job: the pane shows, the task is picked (not remembered as the default) and runs at
    ///     once with its own line as the words, on the editor's selection else the whole arrangement, leaving
    ///     the pane's scope pick alone. False, with the reason in the pane, when the pane cannot ask.
    /// </summary>
    public async Task<bool> StartJobAsync(AiChartJob job, ChordNamingStyle style = ChordNamingStyle.Eof)
    {
        ShowRequested?.Invoke();
        var key = AiChatJobText.KeyOf(job, style);
        var wanted = Jobs.FirstOrDefault(j => j is AiChatOptionItem item && item.Key.Equals(key));
        if (wanted is null || AiChatJobText.DefaultInstructionKey(job) is not { } line) return false;
        _applyingDefaultJob = true;
        try
        {
            SelectedJob = wanted;
        }
        finally
        {
            _applyingDefaultJob = false;
        }

        if (!CanAsk)
        {
            ErrorText = HasProvider ? _localization["AiChat_Busy_Findings"] : NoProviderText;
            return false;
        }

        var hasSelection = _selection.SelectedNotes.Count + _selection.SelectedChords.Count > 0;
        return await SendCoreAsync(_localization[line], hasSelection ? AiChatScopeKind.Selection : AiChatScopeKind.Arrangement);
    }

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
        try
        {
            var result = await _store.SaveAsync(conversation).ConfigureAwait(false);
            await _ui.InvokeAsync(() => Saved(conversation, result)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(AppLogCategory.Application, "Saving the applied AI chat turn failed.", ex);
            await _ui.InvokeAsync(() => Saved(conversation, new AiChatWriteResult(AiChatStatus.WriteFailed)))
                .ConfigureAwait(false);
        }
    }

    /// <summary>The scope the pass runs on, or null when the chosen one covers nothing right now.</summary>
    private AiChatScope? ChatScope(Arrangement arrangement)
    {
        if (Job() == AiChartJob.Tones)
            return AiChatScope.Tones(Scope(), arrangement, _selection.SelectedTones, _state.CurrentTimeMs, _state.Beats);
        switch (Scope())
        {
            case AiChatScopeKind.Selection:
                return AiChatScope.Selection(AiChartPassRun.RangeOf(_selection.SelectedNotes, _selection.SelectedChords),
                    arrangement);
            case AiChatScopeKind.Section:
            {
                var level = ReferenceEquals(arrangement, _state.ActiveArrangement) && _state.ActiveLevel is { } active
                    ? active
                    : arrangement.Levels.MaxBy(l => l.DifficultyIndex);
                return level is null
                    ? null
                    : AiChatScope.SectionAt(arrangement, level, TimelineMath.PositionFromMs(_state.CurrentTimeMs, _state.Beats));
            }
            default:
                return AiChatScope.WholeArrangement(arrangement);
        }
    }

    /// <summary>One scope per arrangement the request runs on; one that covers nothing there (no section, no sound) is left out.</summary>
    private List<AiChatScope> ChatScopes() => [.. ArrangementPicker.RunArrangements().Select(ChatScope).OfType<AiChatScope>()];

    private void OnFirstChosen(Arrangement? first)
    {
        _syncingChoices = true;
        try
        {
            SelectedArrangement = Arrangements.FirstOrDefault(a => ReferenceEquals(((AiChatOptionItem)a).Key, first));
        }
        finally
        {
            _syncingChoices = false;
        }
    }
}
