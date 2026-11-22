using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>Edit ▸ Bookmarks…. One instance per open; every verb re-reads the project's bookmarks.</summary>
public sealed class BookmarksViewModel : DialogViewModel<bool>, IBookmarksScreen
{
    public const int SlotCount = 10;

    private readonly ProjectState _projectState;
    private readonly IEditorTransport _transport;
    private readonly ILocalizationService _localization;
    private readonly IHistoryService _history;
    private readonly List<Row> _rows = [];

    public BookmarksViewModel(ProjectState projectState, IEditorTransport transport, ILocalizationService localization,
        IHistoryService history)
    {
        _projectState = projectState;
        _transport = transport;
        _localization = localization;
        _history = history;
        for (var slot = 0; slot < SlotCount; slot++) _rows.Add(new Row(this, slot, ChordFor(slot)));
        CloseCommand = new RelayCommand(() => Close(true));
        ShortcutHint = _localization["Bookmarks_ShortcutHint"];
        Refresh();
    }

    private string ChordFor(int slot) => _projectState.Keybinds.BookmarkBase.WithDigit(slot).ToDisplayString(_localization);

    public IReadOnlyList<IBookmarkRow> Slots => _rows;

    public string ShortcutHint { get; }

    public ICommand CloseCommand { get; }

    /// <summary>The time as the row prints it.</summary>
    public static string FormatTime(double timeMs) =>
        ElapsedTimeLabel.Format(timeMs, 3);

    private Bookmark? Find(int slot) => _projectState.ActiveProject?.Bookmarks.FirstOrDefault(b => b.Index == slot);

    private void Refresh()
    {
        foreach (var row in _rows) row.Update(Find(row.SlotIndex));
    }

    private void GoTo(int slot)
    {
        if (Find(slot) is not { } bookmark) return;
        // The same funnel as every deliberate seek: clamp, move the playhead and the engine, follow it.
        var time = Math.Clamp(bookmark.Time, 0, _projectState.EffectiveTimelineLengthMs);
        _projectState.CurrentTimeMs = time;
        _transport.SeekTo(time);
        _projectState.TimelineFollowsPlayhead = true;
    }

    private void SetHere(int slot)
    {
        if (_projectState.ActiveProject is not { } project) return;
        _history.Do(new SetBookmarkAction(project.Bookmarks, slot, _projectState.CurrentTimeMs, Refresh));
    }

    private void Clear(int slot)
    {
        if (_projectState.ActiveProject is not { } project) return;
        _history.Do(new SetBookmarkAction(project.Bookmarks, slot, null, Refresh));
    }

    private sealed class Row : ObservableObject, IBookmarkRow
    {
        private readonly BookmarksViewModel _owner;
        private string _timeText;
        private bool _isSet;

        public Row(BookmarksViewModel owner, int slot, string chord)
        {
            _owner = owner;
            SlotIndex = slot;
            Chord = chord;
            Slot = slot.ToString(CultureInfo.InvariantCulture);
            _timeText = owner._localization["Bookmarks_NotSet"];
            GoToCommand = new RelayCommand(() => owner.GoTo(slot), () => _isSet);
            SetCommand = new RelayCommand(() => owner.SetHere(slot));
            ClearCommand = new RelayCommand(() => owner.Clear(slot), () => _isSet);
        }

        public int SlotIndex { get; }

        public string Slot { get; }

        public string Chord { get; }

        public string TimeText
        {
            get => _timeText;
            private set => SetProperty(ref _timeText, value);
        }

        public ICommand GoToCommand { get; }

        public ICommand SetCommand { get; }

        public ICommand ClearCommand { get; }

        public void Update(Bookmark? bookmark)
        {
            _isSet = bookmark is not null;
            TimeText = bookmark is null ? _owner._localization["Bookmarks_NotSet"] : FormatTime(bookmark.Time);
            ((RelayCommand)GoToCommand).NotifyCanExecuteChanged();
            ((RelayCommand)ClearCommand).NotifyCanExecuteChanged();
        }
    }
}
