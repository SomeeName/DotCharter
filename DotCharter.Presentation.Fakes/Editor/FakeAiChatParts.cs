using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>A model, scope or arrangement choice.</summary>
public sealed class FakeAiChatOption(string label, string detail = "") : ObservableObject, IAiChatOption
{
    public string Label { get; } = label;

    public string Detail { get; } = detail;

    public override string ToString() => Label;
}

/// <summary>One message; <see cref="CostText" /> empty for the charter's own.</summary>
public sealed class FakeAiChatTurn(bool isUser, string role, string text, string time, string cost = "", ICommand? retry = null,
    ICommand? fix = null)
    : ObservableObject, IAiChatTurn
{
    public bool CanRetry => RetryCommand is not null;

    public bool CanFix => FixCommand is not null;

    public ICommand? FixCommand { get; } = fix;

    public ICommand? RetryCommand { get; } = retry;

    public bool IsUser { get; } = isUser;

    public string RoleText { get; } = role;

    public string Text { get; } = text;

    public string TimeText { get; } = time;

    public string CostText { get; } = cost;

    public bool HasCost => CostText.Length > 0;

    public override string ToString() => $"{RoleText}: {Text}";
}

/// <summary>The dropdown over a list that really narrows on search, shrinks on Delete and renames.</summary>
public sealed class FakeAiChatHistory : ObservableObject, IAiChatHistory
{
    private List<FakeAiChatHistoryEntry> _all;
    private IReadOnlyList<IAiChatHistoryEntry> _entries;
    private bool _isOpen;
    private string _noticeText;
    private string _searchText = "";

    public FakeAiChatHistory(string caption, IEnumerable<(string Title, string Date, string Model)> entries,
        string notice = "")
    {
        Caption = caption;
        _noticeText = notice;
        _all = entries.Select((e, i) => new FakeAiChatHistoryEntry(this, e.Title, e.Date, e.Model, i == 0)).ToList();
        _entries = _all;
    }

    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value);
    }

    public string Caption { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            Filter();
        }
    }

    public IReadOnlyList<IAiChatHistoryEntry> Entries
    {
        get => _entries;
        private set
        {
            if (!SetProperty(ref _entries, value)) return;
            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    public bool HasEntries => _entries.Count > 0;

    public string EmptyText => _all.Count > 0 && _searchText.Trim().Length > 0
        ? $"No chats match “{_searchText.Trim()}”."
        : "No earlier chats about this chart yet.";

    public string NoticeText
    {
        get => _noticeText;
        private set
        {
            if (!SetProperty(ref _noticeText, value)) return;
            OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => _noticeText.Length > 0;

    internal void Open(FakeAiChatHistoryEntry entry)
    {
        foreach (var e in _all) e.IsCurrent = ReferenceEquals(e, entry);
        NoticeText = "";
        IsOpen = false;
    }

    internal void Remove(FakeAiChatHistoryEntry entry)
    {
        _all = _all.Where(e => !ReferenceEquals(e, entry)).ToList();
        Filter();
    }

    private void Filter()
    {
        var query = _searchText.Trim();
        Entries = query.Length == 0
            ? _all
            : _all.Where(e => e.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                              e.ModelText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}

/// <summary>One earlier chat with working rename and an asked-first delete.</summary>
public sealed class FakeAiChatHistoryEntry : ObservableObject, IAiChatHistoryEntry
{
    private bool _isConfirmingDelete;
    private bool _isCurrent;
    private bool _isRenaming;
    private string _title;
    private string _titleDraft = "";

    internal FakeAiChatHistoryEntry(FakeAiChatHistory owner, string title, string date, string model, bool isCurrent)
    {
        _title = title;
        DateText = date;
        ModelText = model;
        _isCurrent = isCurrent;
        OpenCommand = new RelayCommand(() => owner.Open(this));
        BeginRenameCommand = new RelayCommand(() =>
        {
            IsConfirmingDelete = false;
            TitleDraft = Title;
            IsRenaming = true;
        });
        CommitRenameCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(TitleDraft)) Title = TitleDraft.Trim();
            IsRenaming = false;
        });
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        DeleteCommand = new RelayCommand(() =>
        {
            IsRenaming = false;
            IsConfirmingDelete = true;
        });
        ConfirmDeleteCommand = new RelayCommand(() => owner.Remove(this));
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    public string Title
    {
        get => _title;
        private set
        {
            if (!SetProperty(ref _title, value)) return;
            OnPropertyChanged(nameof(DeletePromptText));
        }
    }

    public string DateText { get; }

    public string ModelText { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    public ICommand OpenCommand { get; }

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    public string TitleDraft
    {
        get => _titleDraft;
        set => SetProperty(ref _titleDraft, value);
    }

    public ICommand BeginRenameCommand { get; }

    public ICommand CommitRenameCommand { get; }

    public ICommand CancelRenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        internal set => SetProperty(ref _isConfirmingDelete, value);
    }

    public string DeletePromptText => $"Delete “{Title}”? This cannot be undone.";

    public ICommand ConfirmDeleteCommand { get; }

    public ICommand CancelDeleteCommand { get; }

    public override string ToString() => Title;
}

/// <summary>The arrangement picker, for the gallery: the rows' checks move its text.</summary>
public sealed class FakeAiChatArrangementPicker : ObservableObject, IAiChatArrangementPicker
{
    public FakeAiChatArrangementPicker(IReadOnlyList<IAiChatArrangementChoice> choices)
    {
        Choices = choices;
        foreach (var choice in choices.OfType<FakeAiChatArrangementChoice>())
        {
            choice.Changed += () =>
            {
                OnPropertyChanged(nameof(PickerText));
                Changed?.Invoke();
            };
        }
    }

    public IReadOnlyList<IAiChatArrangementChoice> Choices { get; }

    public string PickerText => FakeAiChatArrangementChoice.PickerText(Choices);

    /// <summary>Raised after a check changed any row.</summary>
    public event Action? Changed;
}

/// <summary>A row of the arrangement picker, for the gallery: checking works like the real one's.</summary>
public sealed class FakeAiChatArrangementChoice : ObservableObject, IAiChatArrangementChoice
{
    private bool _isChecked;
    private IReadOnlyList<FakeAiChatArrangementChoice> _all = [];

    private FakeAiChatArrangementChoice(string label, bool isAll)
    {
        Label = label;
        IsAll = isAll;
    }

    public string Label { get; }

    public string Detail => "";

    public bool IsAll { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value)) return;
            Toggled();
        }
    }

    /// <summary>Raised after a check changed any row.</summary>
    public event Action? Changed;

    public static IReadOnlyList<IAiChatArrangementChoice> Build(IReadOnlyList<string> names)
    {
        var rows = new List<FakeAiChatArrangementChoice> { new("All arrangements", true) };
        rows.AddRange(names.Select(n => new FakeAiChatArrangementChoice(n, false)));
        foreach (var row in rows) row._all = rows;
        rows[1]._isChecked = true;
        return rows;
    }

    /// <summary>"Lead", "2 arrangements" or "All arrangements".</summary>
    public static string PickerText(IReadOnlyList<IAiChatArrangementChoice> rows)
    {
        var each = rows.Skip(1).Where(r => r.IsChecked).ToList();
        return each.Count == 1 ? each[0].Label : each.Count == rows.Count - 1 ? rows[0].Label : $"{each.Count} arrangements";
    }

    private void Toggled()
    {
        var each = _all.Where(r => !r.IsAll).ToList();
        if (IsAll) each.ForEach(r => r.Set(IsChecked || r == each[0]));
        else if (!each.Any(r => r.IsChecked)) Set(true);
        _all[0].Set(each.All(r => r.IsChecked));
        foreach (var row in _all) row.Changed?.Invoke();
    }

    private void Set(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));
}
