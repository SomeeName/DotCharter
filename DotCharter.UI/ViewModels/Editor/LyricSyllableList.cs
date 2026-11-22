using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>One syllable of the Lyrics tab's review list: its time, its words and whether auto-sync flagged it.</summary>
public sealed class LyricSyllableRow(LyricEvent lyric, string timeLabel)
{
    public LyricEvent Lyric { get; } = lyric;

    public string TimeLabel { get; } = timeLabel;

    /// <summary>Where the syllable starts when the row was read: a re-read compares it, never the label.</summary>
    internal double TimeMs { get; init; }

    public string Text { get; } = lyric.Text;

    public bool NeedsReview { get; } = lyric.ReviewReasons != LyricReviewReason.None;

    /// <summary>What a screen reader says for the row (a record's ToString would read every member).</summary>
    public override string ToString() => $"{TimeLabel} {Text}";
}

/// <summary>
///     The Lyrics tab's review list (inspector design D): every syllable of the active vocal track in time
///     order, or only those auto-sync flagged for review. A row picked is the editor's selected syllable (and the
///     playhead moves there), and the editor's selection is the list's, so the syllable card, the timeline and the
///     list always agree; Previous / Next step through the rows the filter shows.
///     <para>
///         The rows are rebuilt as ONE new list (one reset for the bound list, however long the track), and only
///         when they would read differently: a history change that left the vocal track as it was (a note edit)
///         rebuilds nothing. While the Lyrics tab is hidden (<see cref="IsShown" />) a change only marks the list
///         stale; showing the tab catches up once. Picking the row that is already picked seeks again
///         (<see cref="SeekSelected" />).
///     </para>
/// </summary>
public sealed partial class LyricSyllableList : ObservableObject
{
    /// <summary>The filter's entries, in order.</summary>
    public const int ShowAll = 0;

    public const int ShowNeedsReview = 1;

    private readonly ChartEditorViewModel _editor;
    private bool _following;
    private bool _isShown = true;
    private bool _stale;

    [ObservableProperty] private IReadOnlyList<LyricSyllableRow> _rows = [];
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private LyricSyllableRow? _selectedRow;

    public LyricSyllableList(ChartEditorViewModel editor, IHistoryService history)
    {
        _editor = editor;
        history.HistoryChanged += OnHistoryChanged;
        _editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChartEditorViewModel.ActiveVocalTrack)) return;
            if (_isShown) Refresh();
            else _stale = true;
        };
        if (_editor.Selection is INotifyPropertyChanged selection)
        {
            selection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ISelectionService.SelectedLyric)) Follow();
            };
        }

        Refresh();
    }

    /// <summary>The Lyrics tab is on screen (the inspector's tab activation sets it). Showing a stale list re-reads it.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            _isShown = value;
            if (value && _stale) Refresh();
        }
    }

    /// <summary>How many rows were built since the list was made: the test hook for "rebuilt only when needed".</summary>
    internal int BuildCount { get; private set; }

    /// <summary>The track has syllables but the filter shows none.</summary>
    public bool FilterHidesAll => Rows.Count == 0 && _editor.ActiveVocalTrack?.Phrases.Any(p => p.Lyrics.Count > 0) == true;

    public bool HasRows => Rows.Count > 0;

    partial void OnFilterIndexChanged(int value) => Refresh();

    partial void OnRowsChanged(IReadOnlyList<LyricSyllableRow> value)
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(FilterHidesAll));
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    // A row picked here selects the syllable in the editor and moves the playhead to it.
    partial void OnSelectedRowChanged(LyricSyllableRow? value)
    {
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        if (_following || value is null || ReferenceEquals(_editor.Selection.SelectedLyric, value.Lyric)) return;
        _editor.Selection.SelectedLyric = value.Lyric;
        _editor.ExecuteSeek(value.Lyric.Position.GetPositionMs(_editor.Beats));
    }

    /// <summary>Re-reads the track through the filter.</summary>
    public void Refresh()
    {
        _stale = false;
        var beats = _editor.Beats;
        Rows = [.. Shown().Select(l =>
        {
            var ms = l.Position.GetPositionMs(beats);
            return new LyricSyllableRow(l, ElapsedTimeLabel.Format(ms, 1)) { TimeMs = ms };
        })];
        BuildCount++;
        Follow();
    }

    /// <summary>The picked row again: the playhead goes back to its syllable (a click on the row already picked).</summary>
    public void SeekSelected()
    {
        if (SelectedRow is { } row) _editor.ExecuteSeek(row.Lyric.Position.GetPositionMs(_editor.Beats));
    }

    private IEnumerable<LyricEvent> Shown() =>
        (_editor.ActiveVocalTrack?.Phrases.SelectMany(p => p.Lyrics).OrderBy(l => l.Position) ?? Enumerable.Empty<LyricEvent>())
        .Where(l => FilterIndex != ShowNeedsReview || l.ReviewReasons != LyricReviewReason.None);

    private void OnHistoryChanged()
    {
        if (!_isShown) _stale = true;
        else if (ReadsTheSame()) Follow();
        else Refresh();
    }

    // The rows still say what the track holds: the same syllables, words, review flags and times, in order.
    private bool ReadsTheSame()
    {
        var beats = _editor.Beats;
        var i = 0;
        foreach (var lyric in Shown())
        {
            if (i >= Rows.Count) return false;
            var row = Rows[i++];
            if (!ReferenceEquals(row.Lyric, lyric) || row.Text != lyric.Text ||
                row.NeedsReview != (lyric.ReviewReasons != LyricReviewReason.None) ||
                row.TimeMs != lyric.Position.GetPositionMs(beats))
            {
                return false;
            }
        }

        return i == Rows.Count;
    }

    // The editor's selected syllable is the list's selected row (none when the filter hides it).
    private void Follow()
    {
        _following = true;
        try
        {
            var lyric = _editor.Selection.SelectedLyric;
            SelectedRow = lyric is null ? null : Rows.FirstOrDefault(r => ReferenceEquals(r.Lyric, lyric));
        }
        finally
        {
            _following = false;
        }
    }

    private int Index
    {
        get
        {
            for (var i = 0; i < Rows.Count; i++)
                if (ReferenceEquals(Rows[i], SelectedRow)) return i;
            return -1;
        }
    }

    private bool CanPrevious => Index > 0;

    private bool CanNext => Rows.Count > 0 && Index < Rows.Count - 1;

    /// <summary>The syllable before the selected one (of the rows the filter shows).</summary>
    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void Previous() => SelectedRow = Rows[Index - 1];

    /// <summary>The syllable after the selected one; with none selected, the first.</summary>
    [RelayCommand(CanExecute = nameof(CanNext))]
    private void Next() => SelectedRow = Rows[Index + 1];
}
