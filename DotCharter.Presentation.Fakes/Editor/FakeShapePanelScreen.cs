using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Editor;

namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>
///     A services-free <see cref="IShapePanelScreen" /> over an old imported chart: duplicates,
///     near-duplicates, unused shapes, hand shapes that drifted from their chords and unnamed
///     grips. Search really filters; Merge…, Merge here and Delete really drive the one
///     confirmation bar, and Cancel really clears it.
/// </summary>
public sealed class FakeShapePanelScreen : ObservableObject, IShapePanelScreen
{
    private readonly string _emptyText;
    private readonly List<FakeShapePanelRow> _all;
    private IReadOnlyList<IShapePanelRow> _rows;
    private string _searchText = "";
    private bool _showAttentionOnly;
    private FakeShapePanelRow? _source;
    private string _pendingText = "";
    private string _pendingConfirmLabel = "";
    private bool _isPendingDestructive;

    private FakeShapePanelScreen(Func<FakeShapePanelScreen, List<FakeShapePanelRow>> rows, string emptyText, int cleanUps)
    {
        _emptyText = emptyText;
        _all = rows(this);
        _rows = _all;
        CleanUpLabel = $"Clean up ({cleanUps})";
        HasCleanUp = cleanUps > 0;
        var attention = _all.Count(r => r.HasIssue);
        Summary = _all.Count == 0 ? "" : $"{_all.Count} shapes · {attention} need attention";
        AttentionFilterLabel = $"Needs attention ({attention})";
        HasAttentionFilter = attention > 0;
        CleanUpCommand = new RelayCommand(() => { });
        RepeatPreviousChordCommand = new RelayCommand(() => { });
        // One chord of the first used shape without a twin is selected, so its card offers Split off selection alone
        // (beside Merge it reserves a second row, which the default-size card count does not budget for).
        _all.FirstOrDefault(r => !r.IsUnused && !_all.Any(o => !ReferenceEquals(o, r) && o.FretsText == r.FretsText))?.CanSplitSelection = true;
        foreach (var row in _all)
        {
            row.SelectionChanged += RaiseSelection;
            row.CanMerge = !row.IsUnused && _all.Any(o => !ReferenceEquals(o, row) && o.FretsText == row.FretsText);
        }

        EditSelectedCommand = new RelayCommand(() => { });
        MergeSelectedCommand = new RelayCommand(MergeSelected);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);
        ClearSelectionCommand = new RelayCommand(() =>
        {
            foreach (var row in _all) row.IsSelected = false;
        });
        ConfirmPendingCommand = new RelayCommand(ClearPending);
        CancelPendingCommand = new RelayCommand(ClearPending);
    }

    public static IReadOnlyList<FakeVariant<IShapePanelScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Dragging", Dragging),
        new("ConfirmMerge", ConfirmMerge),
        new("ConfirmDelete", ConfirmDelete),
        new("Search", Search),
        new("NeedsAttention", NeedsAttention),
        new("MultiSelect", MultiSelect),
        new("ConfirmMergeSelected", ConfirmMergeSelected)
    ];

    /// <summary>An old chart after an import: nineteen shapes, ten needing attention.</summary>
    public static FakeShapePanelScreen Default() => new(FakeShapePanelData.OldChart, "No shape matches the search.", 10);

    /// <summary>No chart open.</summary>
    public static FakeShapePanelScreen Empty() => new(_ => [], "Open a chart to see its chord shapes.", 0);

    public static FakeShapePanelScreen LongText() => new(FakeShapePanelData.LongNames, "Keine Form passt zur Suche.", 12);

    /// <summary>A long imported arrangement: ninety shapes, a third of them duplicates.</summary>
    public static FakeShapePanelScreen Full() => new(p => FakeShapePanelData.Many(p, 90), "No shape matches the search.", 41);

    /// <summary>“A5 (2)” dragged over “A5”: the source dims, the target lights, the hint says what a drop does.</summary>
    public static FakeShapePanelScreen Dragging()
    {
        var screen = Default();
        var source = screen._all.First(r => r.Title == "A5 (2)");
        screen.BeginMerge(source);
        screen._all.First(r => r.Title == "A5").IsMergeTarget = true;
        return screen;
    }

    /// <summary>Dropped: the bar says what moves where before anything changes.</summary>
    public static FakeShapePanelScreen ConfirmMerge()
    {
        var screen = Default();
        screen.BeginMerge(screen._all.First(r => r.Title == "A5 (2)"));
        screen.MergeInto(screen._all.First(r => r.Title == "A5"));
        return screen;
    }

    /// <summary>Delete on a shape still in use: the bar names where its uses go.</summary>
    public static FakeShapePanelScreen ConfirmDelete()
    {
        var screen = Default();
        screen.Delete(screen._all.First(r => r.Title == "Am" && r.Uses < 5));
        return screen;
    }

    /// <summary>The three copies of the unnamed lead grip ticked: the bulk bar offers Edit together, Merge into one, Delete.</summary>
    public static FakeShapePanelScreen MultiSelect()
    {
        var screen = Default();
        foreach (var row in screen._all.Where(r => r.FretsText == "x x 9 8 11 x")) row.IsSelected = true;
        return screen;
    }

    /// <summary>Merge into one on the ticked copies: the bar says which one stays and what moves.</summary>
    public static FakeShapePanelScreen ConfirmMergeSelected()
    {
        var screen = MultiSelect();
        screen.MergeSelectedCommand.Execute(null);
        return screen;
    }

    public static FakeShapePanelScreen Search()
    {
        var screen = Default();
        screen.SearchText = "A";
        return screen;
    }

    /// <summary>The "Needs attention" toggle on: only the cards with a problem, one of them with its actions shown by the capture.</summary>
    public static FakeShapePanelScreen NeedsAttention()
    {
        var screen = Default();
        screen.ShowAttentionOnly = true;
        return screen;
    }

    public string Summary { get; }

    public bool HasAttentionFilter { get; }

    public string AttentionFilterLabel { get; }

    public bool ShowAttentionOnly
    {
        get => _showAttentionOnly;
        set
        {
            if (!SetProperty(ref _showAttentionOnly, value)) return;
            ApplyFilter();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ApplyFilter();
        }
    }

    private void ApplyFilter() =>
        Rows = _all.Where(r => (!_showAttentionOnly || r.HasIssue)
                               && (_searchText.Length == 0
                                   || r.Title.Contains(_searchText, StringComparison.OrdinalIgnoreCase)
                                   || r.FretsText.Contains(_searchText, StringComparison.Ordinal))).ToList();

    public IReadOnlyList<IShapePanelRow> Rows
    {
        get => _rows;
        private set
        {
            if (!SetProperty(ref _rows, value)) return;
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    public bool HasRows => _rows.Count > 0;

    public string EmptyText => _all.Count == 0 ? _emptyText : "No shape matches the search.";

    public bool HasCleanUp { get; }

    public string CleanUpLabel { get; }

    public ICommand CleanUpCommand { get; }

    public ICommand RepeatPreviousChordCommand { get; }

    public bool IsChoosingMergeTarget => _source is not null && !HasPending;

    public string MergeHintText => _source is null ? "" : $"Drop “{_source.Title}” on the shape it should become";

    public bool HasSelection => _all.Any(r => r.IsSelected);

    public string SelectionText
    {
        get
        {
            var picked = _all.Where(r => r.IsSelected).ToList();
            return picked.Count == 0 ? "" : $"{picked.Count} shapes selected · {picked.Sum(r => r.Chords)} chords";
        }
    }

    public ICommand EditSelectedCommand { get; }

    public ICommand MergeSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    public bool HasPending => _pendingText.Length > 0;

    public string PendingText => _pendingText;

    public string PendingConfirmLabel => _pendingConfirmLabel;

    public bool IsPendingDestructive => _isPendingDestructive;

    public ICommand ConfirmPendingCommand { get; }

    public ICommand CancelPendingCommand { get; }

    internal void BeginMerge(FakeShapePanelRow source)
    {
        _source?.IsMergeSource = false;
        _source = source;
        source.IsMergeSource = true;
        RaiseState();
    }

    internal void MergeInto(FakeShapePanelRow target)
    {
        if (_source is null || ReferenceEquals(_source, target)) return;
        foreach (var row in _all) row.IsMergeTarget = ReferenceEquals(row, target);
        SetPending($"Merge “{_source.Title}” into “{target.Title}”: its {_source.UsesText} move onto “{target.Title}”, "
                   + $"and “{_source.Title}” is removed. One undo step.", "Merge", destructive: false);
    }

    internal void Delete(FakeShapePanelRow row)
    {
        if (row.IsUnused) return;
        foreach (var other in _all) other.IsMergeSource = ReferenceEquals(other, row);
        SetPending($"“{row.Title}” is used by {row.UsesText}. Deleting it moves them onto the nearest shape, "
                   + "“Am” (same frets), and keeps their notes.", $"Delete and move {row.Uses} uses", destructive: true);
    }

    private void MergeSelected()
    {
        var picked = _all.Where(r => r.IsSelected).OrderByDescending(r => r.Uses).ToList();
        if (picked.Count < 2) return;
        var keep = picked[0];
        SetPending($"Merge {picked.Count} shapes into “{keep.Title}” ({keep.UsesText}): {picked.Skip(1).Sum(r => r.Chords)} chords "
                   + $"move onto it and {picked.Count - 1} copies are removed. Fingers and name are the kept shape's. One undo step.",
            "Merge", destructive: false);
    }

    private void DeleteSelected()
    {
        var picked = _all.Where(r => r.IsSelected).ToList();
        if (picked.Count == 0) return;
        SetPending($"Delete {picked.Count} shapes? {picked.Sum(r => r.Chords)} chords use them and move onto the nearest shape with the "
                   + "same frets; their notes stay.", $"Delete {picked.Count} shapes", destructive: true);
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
    }

    private void SetPending(string text, string confirm, bool destructive)
    {
        _pendingText = text;
        _pendingConfirmLabel = confirm;
        _isPendingDestructive = destructive;
        RaiseState();
    }

    private void ClearPending()
    {
        _pendingText = "";
        _pendingConfirmLabel = "";
        _isPendingDestructive = false;
        _source = null;
        foreach (var row in _all)
        {
            row.IsMergeSource = false;
            row.IsMergeTarget = false;
        }

        RaiseState();
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
        OnPropertyChanged(nameof(PendingConfirmLabel));
        OnPropertyChanged(nameof(IsPendingDestructive));
        OnPropertyChanged(nameof(IsChoosingMergeTarget));
        OnPropertyChanged(nameof(MergeHintText));
        foreach (var row in _all)
        {
            row.CanMergeHere = IsChoosingMergeTarget && _source is not null && !ReferenceEquals(row, _source)
                               && row.FretsText == _source.FretsText;
        }
    }
}
