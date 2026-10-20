using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>
///     A services-free <see cref="IAiChatScreen" />: a short chat about a verse riff, the empty pane,
///     German and Japanese turns with a long local model name, a sixty-turn session with forty
///     earlier chats, a reply being worked on, a failed reply, no provider, the history dropdown open
///     (refusing another chart's chat, narrowed by a search, asking before a delete), a long message
///     being typed, the empty pane on Ask / explain, and an unanswered reply offering Retry. Send
///     really appends the message and starts working, Retry sends it again, Cancel stops, New chat
///     empties the pane, and each task has its own empty line and hint.
/// </summary>
public sealed class FakeAiChatScreen : ObservableObject, IAiChatScreen
{
    private const string NewChatTitle = "New chat";
    private readonly RelayCommand _cancel;
    private readonly RelayCommand _retry;
    private readonly RelayCommand _send;
    private string _errorText = "";
    private string _inputText = "";
    private bool _isBusy;
    private IAiChatOption? _selectedArrangement;
    private IAiChatOption? _selectedJob;
    private IAiChatOption? _selectedModel;
    private IAiChatOption? _selectedScope;
    private string _title;
    private IReadOnlyList<IAiChatTurn> _turns;

    private FakeAiChatScreen(IReadOnlyList<IAiChatTurn> turns, FakeAiChatHistory history, bool hasProvider = true,
        IReadOnlyList<IAiChatOption>? models = null, string? title = null, int selectedModel = 1)
    {
        _turns = turns;
        History = history;
        HasProvider = hasProvider;
        Models = hasProvider ? models ?? FakeAiChatData.ClaudeModels() : [];
        _selectedModel = Models.Count > selectedModel ? Models[selectedModel] : Models.FirstOrDefault();
        Jobs = FakeAiChatData.Jobs();
        _selectedJob = Jobs[0];
        Scopes = FakeAiChatData.Scopes();
        _selectedScope = Scopes[1];
        Arrangements = FakeAiChatData.Arrangements();
        _selectedArrangement = Arrangements[0];
        _title = title ?? (turns.Count > 0 ? turns[0].Text : NewChatTitle);
        _send = new RelayCommand(Send, () => HasProvider && !IsBusy && !string.IsNullOrWhiteSpace(InputText));
        _cancel = new RelayCommand(() => IsBusy = false, () => IsBusy);
        _retry = new RelayCommand(Retry, () => !IsBusy);
        NewChatCommand = new RelayCommand(NewChat);
    }

    public static IReadOnlyList<FakeVariant<IAiChatScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy),
        new(FakeVariantNames.Error, Error),
        new("NoProvider", NoProvider),
        new("HistoryOpen", HistoryOpen),
        new("HistoryRefused", HistoryRefused),
        new("HistorySearch", HistorySearch),
        new("DeleteConfirm", DeleteConfirm),
        new("Typing", Typing),
        new("EmptyExplain", EmptyExplain),
        new("Unanswered", Unanswered),
        new("FindingsLeft", FindingsLeft)
    ];

    /// <summary>Two questions about a verse riff and their answers, with cost lines.</summary>
    public static FakeAiChatScreen Default() => new(FakeAiChatData.Conversation(), ChartHistory());

    /// <summary>A chart nobody has chatted about yet.</summary>
    public static FakeAiChatScreen Empty() => new([], ChartHistory([]));

    public static FakeAiChatScreen LongText() =>
        new(FakeAiChatData.LongConversation(),
            new FakeAiChatHistory("Chats über „Ein sehr langer Songtitel mit Untertitel (Live in Tokio, 1998)“",
                FakeAiChatData.LongHistory()),
            models:
            [
                new FakeAiChatOption("Qwen3-Coder-480B-A35B-Instruct (LM Studio, lokal)", "qwen3-coder-480b-a35b-instruct"),
                new FakeAiChatOption("gpt-oss-120b", "gpt-oss-120b")
            ],
            title: FakeAiChatData.GermanTitle, selectedModel: 0);

    /// <summary>A long evening: thirty exchanges in the pane and forty earlier chats.</summary>
    public static FakeAiChatScreen Full() =>
        new(FakeAiChatData.LongSession(30), ChartHistory(FakeAiChatData.ManyChats(40)));

    public static FakeAiChatScreen Busy()
    {
        var screen = Default();
        screen.InputText = "Now do the same for the chorus, but keep the open G chords.";
        screen.Send();
        return screen;
    }

    public static FakeAiChatScreen Error()
    {
        var screen = Busy();
        screen.IsBusy = false;
        screen.ErrorText = "The reply did not arrive: Claude Code stopped with exit code 1 (not signed in). " +
                           "Run claude login in a terminal, then send again.";
        return screen;
    }

    /// <summary>Settings ▸ AI names no provider: the picker is disabled and says why.</summary>
    public static FakeAiChatScreen NoProvider() => new([], ChartHistory([]), hasProvider: false);

    public static FakeAiChatScreen HistoryOpen()
    {
        var screen = Default();
        screen.History.IsOpen = true;
        return screen;
    }

    /// <summary>The picker after a chat whose chart identity did not match was refused.</summary>
    public static FakeAiChatScreen HistoryRefused()
    {
        var screen = new FakeAiChatScreen(FakeAiChatData.Conversation(),
            ChartHistory(FakeAiChatData.History(),
                "That chat belongs to another chart (Neon Skyline), so it was not opened. Only this chart's chats are listed."));
        screen.History.IsOpen = true;
        return screen;
    }

    /// <summary>The dropdown narrowed by a search: two of the four chats match "bar".</summary>
    public static FakeAiChatScreen HistorySearch()
    {
        var screen = HistoryOpen();
        screen.History.SearchText = "bar";
        return screen;
    }

    /// <summary>The dropdown asking before it deletes the second chat.</summary>
    public static FakeAiChatScreen DeleteConfirm()
    {
        var screen = HistoryOpen();
        screen.History.Entries[1].DeleteCommand.Execute(null);
        return screen;
    }

    /// <summary>A three-line message in the box, so the composer's growth shows.</summary>
    public static FakeAiChatScreen Typing()
    {
        var screen = Default();
        screen.InputText = "Mark the sections: intro, verse, chorus, bridge, solo and outro.\n" +
                           "The solo starts at bar 97.\nSplit it into phrases of four bars.";
        return screen;
    }

    /// <summary>The empty pane on Ask / explain: that task's own line and hint.</summary>
    public static FakeAiChatScreen EmptyExplain()
    {
        var screen = Empty();
        screen.SelectedJob = screen.Jobs[3];
        return screen;
    }

    /// <summary>An Ask / explain question whose reply came back with no answer: the reply offers Retry.</summary>
    public static FakeAiChatScreen Unanswered()
    {
        var screen = Default();
        screen.SelectedJob = screen.Jobs[3];
        screen.InputText = "What's wrong in bar 34? The export report flags a fingering there.";
        screen.Send();
        screen.IsBusy = false;
        screen.Turns = [.. screen._turns, new FakeAiChatTurn(false, FakeAiChatData.Sonnet, FakeAiChatData.NoAnswer, "14:07", "", screen._retry)];
        return screen;
    }

    /// <summary>A pass that left findings in its range: the reply lists them and offers to fix them.</summary>
    public static FakeAiChatScreen FindingsLeft()
    {
        var screen = Default();
        var fix = new RelayCommand(() => screen.IsBusy = true, () => !screen.IsBusy);
        screen.Turns = [.. screen._turns, new FakeAiChatTurn(false, FakeAiChatData.Sonnet, FakeAiChatData.FindingsLeft, "14:09",
            "2,310 in · 214 out", fix: fix)];
        return screen;
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public bool HasProvider { get; }

    public string NoProviderText => HasProvider
        ? ""
        : "No AI provider is set up. Choose one under Settings ▸ AI, then pick a model here.";

    public string ProviderText => HasProvider ? FakeAiChatData.ProviderText : "";

    public ICommand OpenAiSettingsCommand { get; } = new RelayCommand(() => { });

    public IReadOnlyList<IAiChatOption> Models { get; }

    public IAiChatOption? SelectedModel
    {
        get => _selectedModel;
        set => SetProperty(ref _selectedModel, value);
    }

    public string EffortText => HasProvider ? "Reasoning: High" : "";

    public IReadOnlyList<IAiChatOption> Jobs { get; }

    public IAiChatOption? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!SetProperty(ref _selectedJob, value)) return;
            OnPropertyChanged(nameof(JobNote));
            OnPropertyChanged(nameof(HasJobNote));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(InputHint));
        }
    }

    /// <summary>The fake keeps each task's reason in its Detail.</summary>
    public string JobNote => _selectedJob?.Detail ?? "";

    public bool HasJobNote => JobNote.Length > 0;

    public IReadOnlyList<IAiChatOption> Scopes { get; }

    public IAiChatOption? SelectedScope
    {
        get => _selectedScope;
        set
        {
            if (!SetProperty(ref _selectedScope, value)) return;
            OnPropertyChanged(nameof(ScopeSummary));
        }
    }

    public IReadOnlyList<IAiChatOption> Arrangements { get; }

    public IAiChatOption? SelectedArrangement
    {
        get => _selectedArrangement;
        set => SetProperty(ref _selectedArrangement, value);
    }

    public string ScopeSummary => _selectedScope?.Detail ?? "";

    public IReadOnlyList<IAiChatTurn> Turns
    {
        get => _turns;
        private set
        {
            if (!SetProperty(ref _turns, value)) return;
            OnPropertyChanged(nameof(HasTurns));
        }
    }

    public bool HasTurns => _turns.Count > 0;

    public string EmptyText => JobWords.Empty;

    public string InputHint => JobWords.Hint;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            _send.NotifyCanExecuteChanged();
            _retry.NotifyCanExecuteChanged();
            _cancel.NotifyCanExecuteChanged();
        }
    }

    public string BusyText => "Working… reading Verse 2, bars 17–32";

    public ICommand CancelCommand => _cancel;

    public bool HasError => _errorText.Length > 0;

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (!SetProperty(ref _errorText, value)) return;
            OnPropertyChanged(nameof(HasError));
        }
    }

    public string InputText
    {
        get => _inputText;
        set
        {
            if (!SetProperty(ref _inputText, value)) return;
            _send.NotifyCanExecuteChanged();
        }
    }

    public ICommand SendCommand => _send;

    public ICommand NewChatCommand { get; }

    public IAiChatHistory History { get; }

    private static FakeAiChatHistory ChartHistory(IEnumerable<(string, string, string)>? entries = null, string notice = "") =>
        new("Chats about Midnight Run", entries ?? FakeAiChatData.History(), notice);

    private (string Empty, string Hint) JobWords =>
        FakeAiChatData.JobText[Jobs.Select((job, i) => (job, i)).FirstOrDefault(j => j.job == _selectedJob).i];

    /// <summary>The unanswered reply goes, and the same message is worked on again.</summary>
    private void Retry()
    {
        Turns = [.. _turns.Take(_turns.Count - 1)];
        ErrorText = "";
        IsBusy = true;
    }

    private void Send()
    {
        var text = InputText.Trim();
        if (!HasTurns) Title = text;
        Turns = [.. _turns, new FakeAiChatTurn(true, "You", text, "14:06")];
        InputText = "";
        ErrorText = "";
        IsBusy = true;
    }

    private void NewChat()
    {
        IsBusy = false;
        ErrorText = "";
        InputText = "";
        Turns = [];
        Title = NewChatTitle;
    }
}
