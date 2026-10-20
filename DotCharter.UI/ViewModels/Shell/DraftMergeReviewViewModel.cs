// Chart from audio's merge card: one dense row per phrase the held draft would change, with the counts it
// would add, change and remove and the draft's confidence there; ✓ merges the phrase (one undo step), ✕ drops it,
// Merge all merges every row (one undo step), Discard closes; ↑ / ↓ / A / R from the keyboard. Its marks are the
// phrases' markers on the timeline and the song map, published the way the AI chart review publishes its own.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Editing.DraftMerge;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     The merge card over the timeline for a chart-from-audio draft held by
/// <see cref="DraftMergeReviewHost" />. Non-modal: the charter scrubs, plays and edits
///     while it is open. Each verb writes through the host — one undo node per verb — and the rows
///     follow the host's review.
/// </summary>
public sealed partial class DraftMergeReviewViewModel : ViewModelBase
{
    private readonly DraftMergeReviewHost _host;
    private readonly ChartEditorViewModel _editor;
    private readonly ProjectState _state;
    private readonly ILocalizationService _loc;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _countsText = string.Empty;
    [ObservableProperty] private string _hintText = string.Empty;
    [ObservableProperty] private string _occupiedText = string.Empty;
    [ObservableProperty] private bool _hasOccupied;
    [ObservableProperty] private DraftMergeRowViewModel? _currentRow;

    // Where the current row WAS when it left the card (merged or dropped), so ↓ goes to the row that
    // slid into its place and ↑ to the one before it; null once a row is current again.
    private int? _cursor;

    public DraftMergeReviewViewModel(DraftMergeReviewHost host, ChartEditorViewModel editor, IHistoryService history,
        ProjectState state, ILocalizationService loc)
    {
        _host = host;
        _editor = editor;
        _state = state;
        _loc = loc;
        _host.Changed += (_, _) => Load();
        history.HistoryChanged += RefreshMarks;
        state.PropertyChanged += OnStateChanged;
        Load();
    }

    /// <summary>One row per phrase still to decide, in song order.</summary>
    public ObservableCollection<DraftMergeRowViewModel> Rows { get; } = [];

    /// <summary>The highlight published last — test seam.</summary>
    internal ChordUseHighlight Published { get; private set; } = ChordUseHighlight.None;

    /// <summary>Merges every phrase still listed, as ONE undo step.</summary>
    [RelayCommand(CanExecute = nameof(HasRows))]
    private void MergeAll() => Report(_host.AcceptAll(), null);

    private bool HasRows() => Rows.Count > 0;

    /// <summary>Closes the card; the chart is not touched.</summary>
    [RelayCommand]
    private void Discard() => _host.Discard();

    internal void Merge(DraftMergeRowViewModel row) => Report(_host.Accept(row.Phrase), row);

    internal void Reject(DraftMergeRowViewModel row) => _host.Reject(row.Phrase);

    /// <summary>Go to: the row becomes the keyboard's current row and the playhead moves to it.</summary>
    internal void Show(DraftMergeRowViewModel row)
    {
        _cursor = null;
        CurrentRow = row;
        _editor.ExecuteSeek(row.Phrase.StartMs);
    }

    /// <summary>↑ / ↓: moves the current row by <paramref name="delta" /> and goes to it; stops at the ends.
    /// After the current row left, ↓ goes to the row that took its place and ↑ to the one before.</summary>
    public DraftMergeRowViewModel? Step(int delta)
    {
        if (Rows.Count == 0) return null;
        var index = CurrentRow is { } current && Rows.IndexOf(current) is >= 0 and var at
            ? Math.Clamp(at + delta, 0, Rows.Count - 1)
            : _cursor is { } cursor
                ? Math.Clamp(delta > 0 ? cursor + delta - 1 : cursor + delta, 0, Rows.Count - 1)
                : 0;
        Show(Rows[index]);
        return Rows[index];
    }

    /// <summary>A: merges the current row, as the AI review's A accepts.</summary>
    public void MergeCurrent()
    {
        if (CurrentRow is { } row) Merge(row);
    }

    /// <summary>R: drops the current row.</summary>
    public void RejectCurrent()
    {
        if (CurrentRow is { } row) Reject(row);
    }

    partial void OnCurrentRowChanged(DraftMergeRowViewModel? oldValue, DraftMergeRowViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsCurrent = false;
        if (newValue is not null) newValue.IsCurrent = true;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectState.ActiveProject) or nameof(ProjectState.ActiveArrangement))
            RefreshMarks();
    }

    private void Report(DraftMergeOutcome outcome, DraftMergeRowViewModel? row)
    {
        switch (outcome)
        {
            case DraftMergeOutcome.Merged:
                _state.SetMessage(row is null ? _loc["DraftMerge_MergedAll"] : _loc.Format("DraftMerge_Merged", row.Name));
                break;
            case DraftMergeOutcome.Stale:
                _state.SetMessage(_loc["DraftMerge_Stale"], StatusSeverity.Warning);
                break;
            case DraftMergeOutcome.Gone:
                _state.SetMessage(_loc["DraftMerge_Gone"], StatusSeverity.Warning);
                break;
        }
    }

    private void Load()
    {
        var review = _host.Review;
        var current = CurrentRow?.Phrase;
        var currentIndex = CurrentRow is { } row ? Rows.IndexOf(row) : -1;
        CurrentRow = null;
        Rows.Clear();
        if (review is not null)
        {
            foreach (var phrase in review.Phrases)
                Rows.Add(new DraftMergeRowViewModel(this, phrase, _loc));
        }

        CurrentRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Phrase, current));
        if (CurrentRow is null && currentIndex >= 0) _cursor = currentIndex;
        if (review is null) _cursor = null;
        IsOpen = review is not null;
        Subtitle = review is null ? string.Empty : _loc.Format("DraftMerge_Subtitle", review.Arrangement.Name);
        CountsText = review is null ? string.Empty : _loc.Plural("DraftMerge_CountOne", "DraftMerge_Count", Rows.Count);
        HasOccupied = review is { Occupied: > 0, ReplaceExisting: false };
        OccupiedText = HasOccupied
            ? _loc.Plural("DraftMerge_OccupiedOne", "DraftMerge_Occupied", review!.Occupied)
            : string.Empty;
        HintText = review is null
            ? string.Empty
            : string.Join(Environment.NewLine, Subtitle, _loc["DraftMerge_Hint"], _loc["DraftMerge_Keys"]);
        MergeAllCommand.NotifyCanExecuteChanged();
        RefreshMarks();
    }

    /// <summary>Re-reads the marks: after every change of the review and every history step.</summary>
    private void RefreshMarks()
    {
        var highlight = ChordUseHighlight.None;
        if (_host.Review is { IsLive: true } review)
        {
            // The phrase's live marker fades under a proposal-coloured one, as a proposed phrase does.
            var live = review.Phrases.Select(p => p.Marker).OfType<EventPoint>().ToList();
            var proposed = Rows.Select(r => new EventPoint { Position = r.Phrase.Start, Phrase = r.Name }).ToList();
            highlight = ChordUseHighlight.ForEvents(review.Arrangement, [], live, proposed);
        }

        Published = highlight;
        _editor.PublishDraftMergeHighlight(highlight);
    }
}

/// <summary>One phrase of the merge card: where it is, what merging it would do, and its verbs.</summary>
public sealed partial class DraftMergeRowViewModel : ObservableObject
{
    private readonly DraftMergeReviewViewModel _owner;

    [ObservableProperty] private bool _isCurrent;

    internal DraftMergeRowViewModel(DraftMergeReviewViewModel owner, DraftMergePhrase phrase, ILocalizationService loc)
    {
        _owner = owner;
        Phrase = phrase;
        Name = phrase.Name.Length > 0 ? phrase.Name
            : phrase.Start == FractionalPosition.Zero && phrase.End is null ? loc["DraftMerge_WholeSong"]
            : phrase.Start == FractionalPosition.Zero ? loc["DraftMerge_BeforeFirstPhrase"]
            : loc["DraftMerge_UnnamedPhrase"];
        Where = phrase.EndMs is { } end ? $"{Clock(phrase.StartMs)}–{Clock(end)}" : Clock(phrase.StartMs);
        var diff = phrase.Diff;
        Detail = string.Join(" ", new[]
        {
            diff.Added > 0 ? loc.Format("DraftMerge_AddedShort", diff.Added) : string.Empty,
            diff.Changed > 0 ? loc.Format("DraftMerge_ChangedShort", diff.Changed) : string.Empty,
            diff.Removed > 0 ? loc.Format("DraftMerge_RemovedShort", diff.Removed) : string.Empty
        }.Where(p => p.Length > 0));
        Confidence = PercentText.Format(phrase.Confidence, 0);
        IsReplace = phrase.Replaces;
        var counts = loc.Format("DraftMerge_CountsLong", diff.Added, diff.Changed, diff.Removed);
        var confidence = loc.Format("DraftMerge_ConfidenceLong", Confidence);
        AccessibleName = $"{Name} · {Where} · {counts} · {confidence}";
        Tip = string.Join(Environment.NewLine, $"{Name} · {Where}", counts, confidence,
            loc[IsReplace ? "DraftMerge_ReplacesTip" : "DraftMerge_FillsTip"], loc["DraftMerge_Show_Tip"]);
    }

    /// <summary>The phrase under review.</summary>
    internal DraftMergePhrase Phrase { get; }

    /// <summary>The row's heading: the phrase name, or what the stretch is when it has none.</summary>
    public string Name { get; }

    /// <summary>"0:12.5–0:31.0", or the start alone for the last phrase.</summary>
    public string Where { get; }

    /// <summary>"+12 ~3 −4": added, changed and removed notes, each left out at zero.</summary>
    public string Detail { get; }

    /// <summary>The draft's mean confidence in the phrase, "34%".</summary>
    public string Confidence { get; }

    /// <summary>The phrase has notes the merge would replace (the charter chose "replace existing notes").</summary>
    public bool IsReplace { get; }

    /// <summary>The row's accessible name: everything the row shows, spelled out.</summary>
    public string AccessibleName { get; }

    /// <summary>The row's tooltip: the counts and confidence in words, and what a click does.</summary>
    public string Tip { get; }

    /// <summary>✓: merges the phrase now, as one undo step.</summary>
    [RelayCommand]
    private void Merge() => _owner.Merge(this);

    /// <summary>✕: drops the phrase from the card; nothing is written.</summary>
    [RelayCommand]
    private void Reject() => _owner.Reject(this);

    /// <summary>Moves the playhead to the phrase.</summary>
    [RelayCommand]
    private void Show() => _owner.Show(this);

    public override string ToString() => AccessibleName;

    private static string Clock(double ms) =>
        TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
}
