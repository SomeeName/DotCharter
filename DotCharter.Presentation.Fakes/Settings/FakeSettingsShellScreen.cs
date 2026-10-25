using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free settings shell: a rail, a search with canned hits, a footer count.</summary>
public sealed class FakeSettingsShellScreen : ObservableObject, ISettingsShellScreen
{
    private readonly List<FakeRailItem> _items = [];
    private string _searchQuery;
    private bool _isSearchOpen;
    private int _highlight = -1;
    private FakeRailItem? _selected;

    private FakeSettingsShellScreen(IReadOnlyList<ISettingsRailSection> sections, string query,
        IReadOnlyList<FakeSearchHit> hits, int unsaved, string unsavedText, string countText)
    {
        Sections = sections;
        _searchQuery = query;
        _isSearchOpen = hits.Count > 0;
        SearchResults = hits;
        SearchResultCountText = countText;
        HasUnsavedChanges = unsaved > 0;
        UnsavedChangesText = unsavedText;
        foreach (var item in sections.SelectMany(s => s.Items).OfType<FakeRailItem>())
        {
            _items.Add(item);
            item.Select = Select;
        }

        _selected = _items.FirstOrDefault(i => i.IsSelected);
        TabScreens = [typeof(IKeybindsSettingsScreen), typeof(IUpdatesSettingsScreen)];
        ClearSearchCommand = new RelayCommand(() => SearchQuery = "");
        HighlightNextResultCommand = new RelayCommand(() => Highlight(_highlight + 1));
        HighlightPreviousResultCommand = new RelayCommand(() => Highlight(_highlight - 1));
        ActivateResultCommand = new RelayCommand(() => IsSearchOpen = false);
        ResetSectionCommand = new RelayCommand(() => { }, () => HasUnsavedChanges);
        CancelCommand = new RelayCommand(() => { });
        SaveCommand = new RelayCommand(() => { }, () => HasUnsavedChanges);
    }

    public static IReadOnlyList<FakeVariant<ISettingsShellScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    public static FakeSettingsShellScreen Default() =>
        new(EnglishRail(selected: "general"), "", [], 0, "No unsaved changes", "");

    public static FakeSettingsShellScreen Empty() =>
        new([], "", [], 0, "No unsaved changes", "");

    public static FakeSettingsShellScreen LongText() =>
        new(GermanRail(), "Tastenbelegung", [
                new FakeSearchHit("Standard-Tastenbelegung wiederherstellen — Donaudampfschifffahrtsgesellschaft",
                    "Tastenbelegung", "Tastenbelegung"),
                new FakeSearchHit("非常に長い設定名のテストケースです。キーバインドを既定に戻す", "ナビゲーション", "キーバインド")
            ],
            3, "3 ungespeicherte Änderungen", "2 Treffer");

    public static FakeSettingsShellScreen Full()
    {
        var hits = new List<FakeSearchHit>();
        for (var i = 0; i < 40; i++)
            hits.Add(new FakeSearchHit($"Seek to the next beat ({i + 1})", i % 2 == 0 ? "Navigation" : "Transport", "Keybinds"));
        var shell = new FakeSettingsShellScreen(EnglishRail(selected: "keybinds"), "seek", hits, 12, "12 unsaved changes",
            "64 results");
        shell.Highlight(2);
        return shell;
    }

    public IReadOnlyList<ISettingsRailSection> Sections { get; }

    public IReadOnlyList<Type> TabScreens { get; }

    public Type? CurrentScreen => _selected?.Screen;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value)) OnPropertyChanged(nameof(HasSearchQuery));
        }
    }

    public bool HasSearchQuery => _searchQuery.Length > 0;

    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        set => SetProperty(ref _isSearchOpen, value);
    }

    public IReadOnlyList<ISettingsSearchHit> SearchResults { get; }

    public bool HasSearchResults => SearchResults.Count > 0;

    public string SearchResultCountText { get; }

    public ICommand ClearSearchCommand { get; }

    public ICommand HighlightNextResultCommand { get; }

    public ICommand HighlightPreviousResultCommand { get; }

    public ICommand ActivateResultCommand { get; }

    public string UnsavedChangesText { get; }

    public bool HasUnsavedChanges { get; }

    public ICommand ResetSectionCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand SaveCommand { get; }

    private void Select(FakeRailItem item)
    {
        if (_selected is { } previous) previous.IsSelected = false;
        _selected = item;
        item.IsSelected = true;
        OnPropertyChanged(nameof(CurrentScreen));
    }

    private void Highlight(int index)
    {
        if (SearchResults.Count == 0) return;
        var count = SearchResults.Count;
        var next = ((index % count) + count) % count;
        if (_highlight >= 0) ((FakeSearchHit)SearchResults[_highlight]).IsHighlighted = false;
        _highlight = next;
        ((FakeSearchHit)SearchResults[next]).IsHighlighted = true;
    }

    private static List<ISettingsRailSection> EnglishRail(string selected) =>
    [
        new FakeRailSection("Workspace",
        [
            new FakeRailItem("general", "General", "Language, editing rules, import", "IconCog", selected == "general"),
            new FakeRailItem("appearance", "Appearance", "Theme, string colours, interface scale", "IconPalette", selected == "appearance")
        ]),
        new FakeRailSection("System",
        [
            new FakeRailItem("graphics", "Graphics", "Effects, frame cap, render scale", "IconMonitor", selected == "graphics"),
            new FakeRailItem("audio", "Audio", "Devices, latency, monitoring", "IconVolumeHigh", selected == "audio")
        ]),
        new FakeRailSection("Configuration",
        [
            new FakeRailItem("keybinds", "Keybinds", "Every editor shortcut", "IconKeyboard", selected == "keybinds", typeof(IKeybindsSettingsScreen)),
            new FakeRailItem("tone3000", "TONE3000", "Sign in to browse captures", "IconAmp", selected == "tone3000"),
            new FakeRailItem("ai", "AI & models", "Tone design, stem separation and lyric sync models", "IconSparkles", selected == "ai"),
            new FakeRailItem("updates", "Updates", "Version checks and GitHub sign-in", "IconUpdate", selected == "updates", typeof(IUpdatesSettingsScreen))
        ])
    ];

    private static List<ISettingsRailSection> GermanRail() =>
    [
        new FakeRailSection("ARBEITSBEREICH",
        [
            new FakeRailItem("general", "Allgemein", "Sprache, Bearbeitungsregeln, Import-Voreinstellungen und Sicherungen", "IconCog", true),
            new FakeRailItem("appearance", "Erscheinungsbild", "Farbschema, Saitenfarben, Oberflächenskalierung", "IconPalette", false)
        ]),
        new FakeRailSection("System",
        [
            new FakeRailItem("graphics", "Grafik", "Effekte, Bildratenbegrenzung, Renderskalierung", "IconMonitor", false),
            new FakeRailItem("audio", "Audio", "Geräte, Latenz, Abhören des Instruments", "IconVolumeHigh", false)
        ]),
        new FakeRailSection("設定",
        [
            new FakeRailItem("keybinds", "Tastenbelegung", "Jedes Tastenkürzel des Editors", "IconKeyboard", false, typeof(IKeybindsSettingsScreen)),
            new FakeRailItem("updates", "アップデート", "バージョン確認とGitHubサインイン", "IconUpdate", false, typeof(IUpdatesSettingsScreen))
        ])
    ];

    private sealed class FakeRailSection(string title, IReadOnlyList<ISettingsRailItem> items) : ObservableObject, ISettingsRailSection
    {
        public string Title { get; } = title;

        public IReadOnlyList<ISettingsRailItem> Items { get; } = items;
    }

    private sealed class FakeRailItem : ObservableObject, ISettingsRailItem
    {
        private bool _isSelected;

        public FakeRailItem(string key, string title, string description, string iconKey, bool isSelected, Type? screen = null)
        {
            Key = key;
            Title = title;
            Description = description;
            IconKey = iconKey;
            Screen = screen;
            _isSelected = isSelected;
            SelectCommand = new RelayCommand(() => Select?.Invoke(this));
        }

        public string Key { get; }

        public Type? Screen { get; }

        public Action<FakeRailItem>? Select { get; set; }

        public string Title { get; }

        public string Description { get; }

        public string IconKey { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public ICommand SelectCommand { get; }
    }

    private sealed class FakeSearchHit(string label, string groupName, string tabName) : ObservableObject, ISettingsSearchHit
    {
        private bool _isHighlighted;

        public string Label { get; } = label;

        public string GroupName { get; } = groupName;

        public bool HasGroupName => GroupName.Length > 0;

        public string TabName { get; } = tabName;

        public bool IsHighlighted
        {
            get => _isHighlighted;
            set => SetProperty(ref _isHighlighted, value);
        }

        public ICommand GoCommand { get; } = new RelayCommand(() => { });
    }
}
