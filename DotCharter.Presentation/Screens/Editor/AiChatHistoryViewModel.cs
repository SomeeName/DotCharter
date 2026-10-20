using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Ai.Chat;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Presentation.Shared;
using IAiChatHistoryStore = DotCharter.Services.Ai.Chat.IAiChatHistory;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>What the history dropdown asks of the chat pane it belongs to.</summary>
internal interface IAiChatHistoryOwner
{
    string? CurrentConversationId { get; }

    Task OpenAsync(string conversationId);

    void Forget(string conversationId);

    void Retitle(string conversationId, string title);
}

/// <summary>
///     The open chart's saved chats, listed from <see cref="IAiChatHistoryStore" /> each time the
///     dropdown opens and narrowed by <see cref="SearchText" /> without another read. Every
///     refusal is shown in words: an empty list says why, a skipped file or a refused open is the
///     notice.
/// </summary>
public sealed partial class AiChatHistoryViewModel : ObservableObject, Contracts.Editor.IAiChatHistory
{
    private readonly IAiChatHistoryStore _store;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly IAiChatHistoryOwner _owner;
    private readonly CoalescedPass _filter;
    private List<AiChatHistoryEntryViewModel> _all = [];
    private AiChatStatus _listStatus = AiChatStatus.Ok;

    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private IReadOnlyList<IAiChatHistoryEntry> _entries = [];
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _noticeText = "";
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _searchText = "";

    internal AiChatHistoryViewModel(IAiChatHistoryStore store, ILocalizationService localization, IUiDispatcher ui,
        TimeProvider time, IAiChatHistoryOwner owner)
    {
        _store = store;
        _localization = localization;
        _ui = ui;
        _time = time;
        _owner = owner;
        _filter = new CoalescedPass(Filter, ui.Post);
        _emptyText = localization["AiChat_HistoryEmpty"];
    }

    public bool HasEntries => Entries.Count > 0;

    public bool HasNotice => NoticeText.Length > 0;

    /// <summary>The pending read, so a test (and a caller that must wait) can await it.</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    internal void SetCaption(string songTitle) =>
        Caption = songTitle.Length == 0 ? _localization["AiChat_CaptionNoChart"] : _localization.Format("AiChat_Caption", songTitle);

    /// <summary>Re-marks the open chat after the pane switched conversations.</summary>
    internal void MarkCurrent()
    {
        foreach (var entry in _all) entry.IsCurrent = entry.Id == _owner.CurrentConversationId;
    }

    /// <summary>A chart switch: the old chart's list and notice must never show for the new one.</summary>
    internal void Clear()
    {
        IsOpen = false;
        NoticeText = "";
        SearchText = "";
        _all = [];
        Filter();
    }

    internal void Notice(string text) => NoticeText = text;

    internal Task ReloadAsync() => Loading = LoadAsync();

    internal Task OpenEntryAsync(AiChatHistoryEntryViewModel entry) => _owner.OpenAsync(entry.Id);

    internal async Task RenameAsync(AiChatHistoryEntryViewModel entry, string title)
    {
        var result = await _store.RenameAsync(entry.Id, title).ConfigureAwait(false);
        await _ui.InvokeAsync(() =>
        {
            if (result.Status != AiChatStatus.Ok)
            {
                NoticeText = AiChatText.Status(_localization, result.Status);
                return;
            }

            NoticeText = "";
        }).ConfigureAwait(false);
        if (result.Status == AiChatStatus.Ok) await ReloadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes after the row asked. Idempotent: a chat whose file is already gone
    ///     (<see cref="AiChatStatus.NotFound" />) counts as deleted, so its row goes and the pane forgets
    ///     it instead of refusing the same delete for ever.
    /// </summary>
    internal async Task DeleteAsync(AiChatHistoryEntryViewModel entry)
    {
        var result = await _store.DeleteAsync(entry.Id).ConfigureAwait(false);
        await _ui.InvokeAsync(() =>
        {
            if (result.Status is not (AiChatStatus.Ok or AiChatStatus.NotFound))
            {
                entry.IsConfirmingDelete = false;
                NoticeText = AiChatText.Status(_localization, result.Status);
                return;
            }

            NoticeText = "";
            Drop(entry.Id);
            _owner.Forget(entry.Id);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// An open found no file: the row goes, the notice says the chat is gone, and the
    ///     folder is listed again, since whatever removed one file may have changed others.
    /// </summary>
    internal Task GoneAsync(string conversationId)
    {
        Drop(conversationId);
        NoticeText = AiChatText.Status(_localization, AiChatStatus.NotFound);
        return ReloadAsync();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value) _ = ReloadAsync();
    }

    /// <summary>One pass per typing burst: a keystroke re-lists up to two hundred rows.</summary>
    partial void OnSearchTextChanged(string value) => _filter.Request();

    partial void OnEntriesChanged(IReadOnlyList<IAiChatHistoryEntry> value) => OnPropertyChanged(nameof(HasEntries));

    partial void OnNoticeTextChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    private async Task LoadAsync()
    {
        var listing = await _store.ListAsync().ConfigureAwait(false);
        await _ui.InvokeAsync(() => Apply(listing)).ConfigureAwait(false);
    }

    private void Apply(AiChatListing listing)
    {
        // A listing made for another chart than the open one says nothing about it.
        if (listing.ChartId != _store.OpenChartId) return;
        _listStatus = listing.Status;
        var now = _time.GetUtcNow();
        _all = listing.Conversations
            .Select(c => new AiChatHistoryEntryViewModel(this, c, _localization, AiChatText.Date(_localization, c.UpdatedUtc, now)))
            .ToList();
        MarkCurrent();
        // A rename of the open chat must reach the pane's copy, or its next save writes the old title back.
        if (_all.FirstOrDefault(e => e.IsCurrent) is { } current) _owner.Retitle(current.Id, current.Title);
        if (listing.Refused.Count > 0)
        {
            NoticeText = _localization.Format("AiChat_Notice_Skipped", listing.Refused.Count,
                AiChatText.Status(_localization, listing.Refused[0].Reason));
        }

        Filter();
    }

    private void Drop(string conversationId)
    {
        _all = [.. _all.Where(e => e.Id != conversationId)];
        Filter();
    }

    /// <summary>
    /// Always publishes a NEW list. <see cref="Entries" /> once was <c>_all</c> itself, so a
    ///     delete that removed the row from <c>_all</c> in place re-assigned the same reference: the
    ///     setter saw no change, raised nothing, and the dropdown kept showing the deleted row, whose
    ///     second delete and every open then found no file.
    /// </summary>
    private void Filter()
    {
        var query = SearchText.Trim();
        Entries = query.Length == 0
            ? [.. _all]
            : _all.Where(e => e.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                              e.ModelText.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        EmptyText = _listStatus != AiChatStatus.Ok ? AiChatText.Status(_localization, _listStatus)
            : _all.Count > 0 && query.Length > 0 ? _localization.Format("AiChat_HistoryNoMatch", query)
            : _localization["AiChat_HistoryEmpty"];
    }
}

/// <summary>One saved chat in the dropdown: open, rename in place, delete after asking.</summary>
public sealed partial class AiChatHistoryEntryViewModel : ObservableObject, IAiChatHistoryEntry
{
    private readonly ILocalizationService _localization;

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _titleDraft = "";
    [ObservableProperty] private bool _isConfirmingDelete;

    internal AiChatHistoryEntryViewModel(AiChatHistoryViewModel owner, AiChatSummary summary,
        ILocalizationService localization, string dateText)
    {
        _localization = localization;
        Id = summary.Id;
        _title = summary.Title;
        DateText = dateText;
        ModelText = AiChatModelCatalog.LabelOf(summary.ModelId);
        OpenCommand = new AsyncRelayCommand(() => owner.OpenEntryAsync(this));
        BeginRenameCommand = new RelayCommand(() =>
        {
            IsConfirmingDelete = false;
            TitleDraft = Title;
            IsRenaming = true;
        });
        CommitRenameCommand = new AsyncRelayCommand(async () =>
        {
            IsRenaming = false;
            await owner.RenameAsync(this, TitleDraft);
        });
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        DeleteCommand = new RelayCommand(() =>
        {
            IsRenaming = false;
            IsConfirmingDelete = true;
        });
        ConfirmDeleteCommand = new AsyncRelayCommand(() => owner.DeleteAsync(this));
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
    }

    public string Id { get; }

    public string DateText { get; }

    public string ModelText { get; }

    public string DeletePromptText => _localization.Format("AiChat_DeletePrompt", Title);

    public ICommand OpenCommand { get; }

    public ICommand BeginRenameCommand { get; }

    public ICommand CommitRenameCommand { get; }

    public ICommand CancelRenameCommand { get; }

    public ICommand DeleteCommand { get; }

    public ICommand ConfirmDeleteCommand { get; }

    public ICommand CancelDeleteCommand { get; }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DeletePromptText));

    public override string ToString() => Title;
}
