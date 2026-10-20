using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Core.Utilities;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Ai.Chat;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.State;
using DotCharter.Tone.Diagnostics;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using IAiChatHistoryStore = DotCharter.Services.Ai.Chat.IAiChatHistory;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>
///     The AI chat pane over the OPEN chart. It reads the provider and its models from Settings ▸ AI,
///     says what the next message covers, and keeps every message in the chart's own chat history.
///     <para>
///         Send runs the message as an instruction to the chart pass on the chosen scope
/// (<see cref="IAiChatChartActions" />): its proposals are HELD for the review card and
///         the reply says what they are. Nothing here writes the chart; only the card does.
///     </para>
/// </summary>
public sealed partial class AiChatViewModel : ObservableObject, IAiChatScreen, IAiChatHistoryOwner
{
    private readonly IConfigurationService _config;
    private readonly ProjectState _state;
    private readonly ISelectionService _selection;
    private readonly IAiChatHistoryStore _store;
    private readonly IAiChatChartActions _actions;
    private readonly IUiDispatcher _ui;
    private readonly ILocalizationService _localization;
    private readonly TimeProvider _time;
    private readonly AsyncRelayCommand _send;
    private readonly AsyncRelayCommand _retry;
    private readonly RelayCommand _cancel;
    private readonly AiChatHistoryViewModel _history;
    private AiChatConversation? _conversation;
    private CancellationTokenSource? _saving;
    private string? _chartId;
    private readonly AiChatToneScope _toneScope;
    private readonly AiChatTaskDefaults _defaults;
    private bool _applyingDefaultJob;
    private (AiChatTurn Turn, AiChatInstructionRequest Request)? _unanswered;
    private (AiChatTurn Turn, AiChatInstructionRequest Request)? _followUp;
    private readonly AsyncRelayCommand _fix;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _hasProvider;
    [ObservableProperty] private string _noProviderText = "";
    [ObservableProperty] private string _providerText = "";
    [ObservableProperty] private IReadOnlyList<IAiChatOption> _models = [];
    [ObservableProperty] private IAiChatOption? _selectedModel;
    [ObservableProperty] private IAiChatOption? _selectedJob;
    [ObservableProperty] private IAiChatOption? _selectedScope;
    [ObservableProperty] private IReadOnlyList<IAiChatOption> _arrangements = [];
    [ObservableProperty] private IAiChatOption? _selectedArrangement;
    [ObservableProperty] private string _scopeSummary = "";
    [ObservableProperty] private string _jobNote = "";
    [ObservableProperty] private string _effortText = "";
    [ObservableProperty] private IReadOnlyList<IAiChatTurn> _turns = [];
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _inputHint = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string _inputText = "";

    public AiChatViewModel(IConfigurationService config, ProjectState state, ISelectionService selection,
        AiChatServices services, IUiDispatcher ui, ILocalizationService localization)
    {
        _config = config;
        _state = state;
        _selection = selection;
        _store = services.History;
        _actions = services.Actions;
        _ui = ui;
        _localization = localization;
        _toneScope = new AiChatToneScope(state, localization);
        _time = services.Time;
        _history = new AiChatHistoryViewModel(_store, localization, ui, _time, this);
        _defaults = new AiChatTaskDefaults(config, localization);
        _send = new AsyncRelayCommand(SendAsync, CanSend);
        _retry = new AsyncRelayCommand(RetryAsync, () => !IsBusy && _unanswered is not null);
        _cancel = new RelayCommand(() => _saving?.Cancel(), () => IsBusy);
        _fix = new AsyncRelayCommand(FixAsync, () => !IsBusy && _followUp is not null);
        _actions.Applied += (_, e) => _ui.Post(() => OnApplied(e));
        NewChatCommand = new RelayCommand(NewChat);
        OpenAiSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());
        Jobs = AiChatJobText.Options(localization);
        _selectedJob = Jobs[0];
        Scopes =
        [
            new AiChatOptionItem(localization["AiChat_Scope_Selection"], "", AiChatScopeKind.Selection),
            new AiChatOptionItem(localization["AiChat_Scope_Section"], "", AiChatScopeKind.Section),
            new AiChatOptionItem(localization["AiChat_Scope_Arrangement"], "", AiChatScopeKind.Arrangement)
        ];
        _selectedScope = Scopes[1];
        _title = localization["AiChat_NewChat"];
        _state.PropertyChanged += OnStateChanged;
        _selection.SelectionChanged += (_, _) => RefreshScopeSummary();
        RefreshProvider();
        OnChartChanged();
        ApplyDefaultJob();
    }

    /// <summary>Settings ▸ AI's door; the shell answers it by switching to its Settings tab.</summary>
    public event Action? OpenSettingsRequested;

    public ICommand OpenAiSettingsCommand { get; }

    public IReadOnlyList<IAiChatOption> Jobs { get; }

    public IReadOnlyList<IAiChatOption> Scopes { get; }

    public bool HasTurns => Turns.Count > 0;

    public string BusyText => _localization["AiChat_Working"];

    public ICommand CancelCommand => _cancel;

    public bool HasError => ErrorText.Length > 0;

    public ICommand SendCommand => _send;

    public ICommand NewChatCommand { get; }

    public Contracts.Editor.IAiChatHistory History => _history;

    public string? CurrentConversationId => _conversation?.Id;

    /// <summary>Re-reads Settings ▸ AI: the shell calls it when the Settings tab closes.</summary>
    public void RefreshProvider()
    {
        var settings = _config.LoadAppConfig().Ai ?? AiProviderSettings.Disabled;
        HasProvider = AiChatModelCatalog.IsUsable(settings);
        NoProviderText = HasProvider ? "" : _localization["AiChat_NoProvider"];
        ProviderText = HasProvider ? _localization[AiChatText.ProviderKey(settings.Kind)] : "";
        var previous = (SelectedModel as AiChatOptionItem)?.Key as string;
        Models = [.. AiChatModelCatalog.For(settings).Select(m => new AiChatOptionItem(m.Label, m.Id, m.Id))];
        var wanted = previous ?? AiChatModelCatalog.DefaultId(settings);
        SelectedModel = Models.FirstOrDefault(m => (string)((AiChatOptionItem)m).Key == wanted) ?? Models.FirstOrDefault();
        RefreshEffort();
        _send.NotifyCanExecuteChanged();
    }

    async Task IAiChatHistoryOwner.OpenAsync(string conversationId)
    {
        var result = await _store.LoadAsync(conversationId).ConfigureAwait(false);
        // a row whose file is gone goes too, or every click on it fails the same way.
        if (result.Status == AiChatStatus.NotFound)
        {
            await _ui.InvokeAsync(() => _history.GoneAsync(conversationId)).Unwrap().ConfigureAwait(false);
            return;
        }

        await _ui.InvokeAsync(() =>
        {
            if (result.Status != AiChatStatus.Ok || result.Conversation is null)
            {
                _history.Notice(AiChatText.Status(_localization, result.Status));
                return;
            }

            _history.Notice("");
            Show(result.Conversation);
            _history.IsOpen = false;
        }).ConfigureAwait(false);
    }

    void IAiChatHistoryOwner.Forget(string conversationId)
    {
        if (_conversation?.Id == conversationId) NewChat();
    }

    void IAiChatHistoryOwner.Retitle(string conversationId, string title)
    {
        if (_conversation is null || _conversation.Id != conversationId || _conversation.Title == title) return;
        _conversation = _conversation with { Title = title };
        Title = title;
    }

    partial void OnTurnsChanged(IReadOnlyList<IAiChatTurn> value) => OnPropertyChanged(nameof(HasTurns));

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnInputTextChanged(string value) => _send.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        _send.NotifyCanExecuteChanged();
        _retry.NotifyCanExecuteChanged();
        _cancel.NotifyCanExecuteChanged();
        _fix.NotifyCanExecuteChanged();
    }

    partial void OnSelectedScopeChanged(IAiChatOption? value) => RefreshScopeSummary();

    private bool CanSend() =>
        HasProvider && !IsBusy && _chartId is not null && !string.IsNullOrWhiteSpace(InputText);

    private async Task SendAsync()
    {
        var text = InputText.Trim();
        var conversation = _conversation ?? _store.StartNew(ModelId());
        if (conversation is null || text.Length == 0) return;
        ErrorText = "";
        var scope = ChatScope();
        if (scope is null)
        {
            // Never silently the whole arrangement: the scope summary already says what is missing.
            ErrorText = ScopeSummary;
            return;
        }

        conversation = conversation.Append(new AiChatTurn(AiChatRole.User, text, _time.GetUtcNow()));
        Show(conversation);
        InputText = "";
        await RunAsync(conversation, new AiChatInstructionRequest(text, scope, conversation, ModelId(), Job()));
    }

    /// <summary>A reply with no answer, sent again: the SAME request, in that reply's place.</summary>
    private async Task RetryAsync()
    {
        if (_unanswered is not { } unanswered || _conversation is not { Turns: [.., var last] } conversation) return;
        if (!ReferenceEquals(last, unanswered.Turn)) return;
        _unanswered = null;
        ErrorText = "";
        conversation = conversation with { Turns = [.. conversation.Turns.SkipLast(1)] };
        Show(conversation);
        await RunAsync(conversation, unanswered.Request);
    }

    private async Task RunAsync(AiChatConversation conversation, AiChatInstructionRequest request)
    {
        using var working = _saving = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            // The pass starts here, on the UI thread, as its contract asks; the user's turn is
            // written while the model works, and kept even when the run is stopped.
            var reply = _actions.RunInstructionAsync(request, working.Token);
            var saved = await _store.SaveAsync(conversation).ConfigureAwait(false);
            await _ui.InvokeAsync(() => Saved(conversation, saved)).ConfigureAwait(false);
            var answer = await reply.ConfigureAwait(false);
            var answered = conversation.Append(answer.ToTurn(_time.GetUtcNow()));
            // No answer (the run failed, or another was in flight): the reply offers Retry.
            var retry = answer.Status is AiChartRunStatus.Failed or AiChartRunStatus.Busy ? request : null;
            await _ui.InvokeAsync(() => Answered(answered, retry, answer.FollowUp)).ConfigureAwait(false);
            if (answered.ChartId == _store.OpenChartId)
            {
                var result = await _store.SaveAsync(answered, working.Token).ConfigureAwait(false);
                await _ui.InvokeAsync(() => Saved(answered, result)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await _ui.InvokeAsync(() => ErrorText = _localization["AiChat_Stopped"]).ConfigureAwait(false);
        }
        finally
        {
            _saving = null;
            await _ui.InvokeAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private void Answered(AiChatConversation conversation, AiChatInstructionRequest? retry,
        AiChatInstructionRequest? followUp)
    {
        // The chart changed while the model worked: the reply belongs to the old one.
        if (conversation.ChartId != _chartId || _conversation?.Id != conversation.Id) return;
        _unanswered = retry is null ? null : (conversation.Turns[^1], retry);
        _followUp = followUp is null ? null : (conversation.Turns[^1], followUp);
        Show(conversation);
    }

    private void Saved(AiChatConversation conversation, AiChatWriteResult result)
    {
        if (conversation.ChartId != _chartId) return;
        if (result.Status == AiChatStatus.Ok)
        {
            _history.MarkCurrent();
            return;
        }

        ErrorText = _localization.Format("AiChat_NotSaved", AiChatText.Status(_localization, result.Status));
    }

    /// <summary>The scope the pass runs on, or null when the chosen one covers nothing right now.</summary>
    private AiChatScope? ChatScope()
    {
        var arrangement = SelectedArrangement is AiChatOptionItem { Key: Arrangement a } ? a : _state.ActiveArrangement;
        if (arrangement is null) return null;
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

    private void Show(AiChatConversation conversation)
    {
        _conversation = conversation;
        // Only the LAST turn can be sent again: a message after it, or another chat, retires Retry.
        if (conversation.Turns.Count == 0 || !ReferenceEquals(conversation.Turns[^1], _unanswered?.Turn)) _unanswered = null;
        if (_followUp is { } offered && !conversation.Turns.Any(t => ReferenceEquals(t, offered.Turn))) _followUp = null;
        Title = conversation.Title.Length > 0 ? conversation.Title : _localization["AiChat_NewChat"];
        var you = _localization["AiChat_You"];
        var model = AiChatModelCatalog.LabelOf(conversation.ModelId);
        Turns = [.. conversation.Turns.Select(t => (IAiChatTurn)new AiChatTurnItem(t.Role == AiChatRole.User,
            t.Role == AiChatRole.User ? you : model, t.Text, AiChatText.Time(t.At), AiChatText.Tokens(_localization, t.Usage),
            ReferenceEquals(t, _unanswered?.Turn) ? _retry : null) { FixCommand = ReferenceEquals(t, _followUp?.Turn) ? _fix : null })];
        _history.MarkCurrent();
    }

    private void NewChat()
    {
        _saving?.Cancel();
        _conversation = null;
        _unanswered = null;
        _followUp = null;
        Turns = [];
        ErrorText = "";
        InputText = "";
        Title = _localization["AiChat_NewChat"];
        _history.MarkCurrent();
    }

    private string ModelId() => (SelectedModel as AiChatOptionItem)?.Key as string ?? "";

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectState.ActiveProject):
                OnChartChanged();
                break;
            case nameof(ProjectState.ActiveArrangement):
                SelectActiveArrangement();
                break;
            case nameof(ProjectState.CurrentSectionDisplay):
                if (Scope() == AiChatScopeKind.Section && Job() != AiChartJob.Tones) RefreshScopeSummary();
                break;
            case nameof(ProjectState.CurrentTimeMs):
                // The tone in force at the playhead, only while that is the scope, and the line is
                // rewritten only when that tone change is another one: this fires at the playhead's rate.
                if (Scope() == AiChatScopeKind.Section && Job() == AiChartJob.Tones && _toneScope.InForceChanged(ChosenArrangement()))
                    RefreshScopeSummary();
                break;
        }
    }

    private void OnChartChanged()
    {
        var project = _state.ActiveProject;
        _chartId = project?.ProjectId;
        NewChat();
        _history.Clear();
        _history.SetCaption(project?.Chart.SongName ?? "");
        RefreshJobText();
        Arrangements = project is null
            ? []
            : [.. project.Arrangements.Select(a => new AiChatOptionItem(a.Name, "", a))];
        SelectActiveArrangement();
        RefreshScopeSummary();
        _send.NotifyCanExecuteChanged();
    }

    private void SelectActiveArrangement() =>
        SelectedArrangement = Arrangements.FirstOrDefault(a => ReferenceEquals(((AiChatOptionItem)a).Key, _state.ActiveArrangement))
                              ?? Arrangements.FirstOrDefault();

    private Arrangement? ChosenArrangement() => SelectedArrangement is AiChatOptionItem { Key: Arrangement a } ? a : null;

    private AiChatScopeKind Scope() => SelectedScope is AiChatOptionItem { Key: AiChatScopeKind scope } ? scope : AiChatScopeKind.Section;
}
