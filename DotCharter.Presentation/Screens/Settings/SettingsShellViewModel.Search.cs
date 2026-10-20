using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Tone.Presentation.Shared;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The cross-tab search: one pass per typing burst, over what every tab
///     declares — a tab the user has never opened is searched exactly like the
///     one on screen. The dropdown lists at most <see cref="MaxDropdownRows" />
///     hits and the count line reports the real total.
/// </summary>
public sealed partial class SettingsShellViewModel
{
    public const int MaxDropdownRows = 40;

    private readonly List<SettingsSearchHitViewModel> _results = [];
    private CoalescedPass _searchPass = null!;
    private string _searchQuery = "";
    private bool _isSearchOpen;
    private int _total;
    private int _highlight = -1;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value ?? "")) return;
            OnPropertyChanged(nameof(HasSearchQuery));
            _searchPass.Request();
        }
    }

    public bool HasSearchQuery => _searchQuery.Length > 0;

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set => SetProperty(ref _isSearchOpen, value);
    }

    public IReadOnlyList<ISettingsSearchHit> SearchResults => _results;

    public bool HasSearchResults => _total > 0;

    public string SearchResultCountText =>
        _localization.Plural("Settings_SearchResultCountOne", "Settings_SearchResultCount", _total);

    public ICommand ClearSearchCommand { get; private set; } = null!;

    public ICommand HighlightNextResultCommand { get; private set; } = null!;

    public ICommand HighlightPreviousResultCommand { get; private set; } = null!;

    public ICommand ActivateResultCommand { get; private set; } = null!;

    /// <summary>The pass, for a test that wants the burst to run without a dispatcher turn.</summary>
    public void RunSearchNow() => _searchPass.RunImmediately();

    private void InitializeSearch()
    {
        _searchPass = new CoalescedPass(RunSearch, _ui.Post);
        ClearSearchCommand = new RelayCommand(() => SearchQuery = "");
        HighlightNextResultCommand = new RelayCommand(() => Highlight(_highlight + 1));
        HighlightPreviousResultCommand = new RelayCommand(() => Highlight(_highlight - 1));
        ActivateResultCommand = new RelayCommand(() =>
        {
            if (_results.Count == 0) return;
            Go(_results[_highlight < 0 ? 0 : _highlight]);
        });
    }

    private void RunSearch()
    {
        var query = _searchQuery.Trim();
        foreach (var tab in _tabs) tab.ApplySearch(query);

        var hits = SettingsSearch.Hits(
            _items.Select(i => new SettingsSearchSource(i.Tab.Key, i.Title, i.Tab.SearchGroups)), query);

        _results.Clear();
        _highlight = -1;
        _total = hits.Count;
        foreach (var hit in hits.Take(MaxDropdownRows)) _results.Add(new SettingsSearchHitViewModel(hit, Go));

        OnPropertyChanged(nameof(SearchResults));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(SearchResultCountText));
        IsSearchOpen = query.Length > 0;
    }

    private void Highlight(int index)
    {
        if (_results.Count == 0) return;
        var count = _results.Count;
        var next = ((index % count) + count) % count;
        if (_highlight >= 0 && _highlight < count) _results[_highlight].IsHighlighted = false;
        _highlight = next;
        _results[next].IsHighlighted = true;
    }

    private void Go(SettingsSearchHitViewModel hit)
    {
        Select(hit.Hit.TabKey);
        IsSearchOpen = false;
    }
}
