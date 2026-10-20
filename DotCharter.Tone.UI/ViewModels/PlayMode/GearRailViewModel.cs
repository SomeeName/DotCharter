using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.ViewModels.PlayMode;

/// <summary>
/// The pedalboard's gear rail (AmpliTube-style gear browser, replacing the old
/// add-dropdown): category icon tabs derived from <see cref="DspModuleCategory"/>,
/// a search box and a grid of gear cards — each a custom-drawn miniature of the
/// module's real skin. Clicking a card adds the
/// module at the chain end via the owning <see cref="PedalboardViewModel"/>'s
/// existing AddNode path; dragging one onto the rack inserts at the drop slot.
/// Filtering is the pure <see cref="GearRailFilter"/> (unit-tested); only the
/// tabs whose category actually has catalogue modules are shown.
/// </summary>
public sealed partial class GearRailViewModel : ObservableObject
{
    private readonly IReadOnlyList<DspModuleType> _catalog;
    private readonly Action<DspModuleType> _addAtEnd;
    private readonly Action<DspModuleType, string, int>? _addModelAtEnd;
    private readonly IRigConfigService? _config;
    private readonly ILocalizationService _loc;

    /// <summary>The starred module type ids, ordinal. Seeded from
    /// <c>AppConfig.RigFavoriteGear</c> and written straight back on every
    /// toggle — a star the player has to set twice is worse than no star.</summary>
    private readonly HashSet<string> _favorites = new(StringComparer.Ordinal);

    public GearRailViewModel(IReadOnlyList<DspModuleType> catalog, ILocalizationService localization, Action<DspModuleType> addAtEnd,
        Action<DspModuleType, string, int>? addModelAtEnd = null,
        Action<Action>? searchPost = null,
        IRigConfigService? config = null)
    {
        _catalog = catalog;
        _loc = localization;
        _addAtEnd = addAtEnd;
        _addModelAtEnd = addModelAtEnd;
        _config = config;
        LoadFavorites();
        // Typing coalesces into ONE rebuild per dispatcher turn (the
        // CoalescedFilterPass rule — a
        // rebuild is a full catalogue scan, ~840 card view-models over the
        // 100-module catalogue and a
        // culture-sensitive sort, and it would run per keystroke). A tab click is a
        // discrete gesture and rebuilds at once. Without a post (the tests) the
        // pass runs inline.
        _searchPass = new Shell.CoalescedFilterPass(Rebuild, searchPost ?? (action => action()));
        BuildKindTabs();
        BuildTabs();
        _selectedTab = Tabs.Count > 0 ? Tabs[0] : null;
        Rebuild();
    }

    /// <summary>The browser's top-level FORM tabs — stomp / amp / cab / rack.
    /// Only kinds the catalogue actually has appear, so a tab can never open on
    /// an empty drawer.</summary>
    public ObservableCollection<GearKindTab> KindTabs { get; } = [];

    /// <summary>The lit form tab, or null for "every form" — which is the
    /// DEFAULT, deliberately: the curated order puts the flagship amps and cabs
    /// first, and opening the browser on STOMP would bury them behind a click.
    /// Clicking the lit tab again clears back to null.</summary>
    [ObservableProperty] private GearKindTab? _selectedKind;

    partial void OnSelectedKindChanged(GearKindTab? value)
    {
        foreach (var tab in KindTabs) tab.IsActive = ReferenceEquals(tab, value);
        Rebuild();
    }

    // `SelectKindCommand` — the STOMP/AMP/CAB/RACK form tabs — is DELETED (0.58).
    // The gear panel filters by FIRE IDENTITY now, not by form:
    // `AmpFxRigView`'s chip row binds `AmpFxRigViewModel.CategoryFilters` →
    // `FilterCategoryCommand`, and no markup has named `KindTabs`/`SelectedKind`
    // since the rebuild. `GearRailFilter.KindOf`/`MatchesKind` stay — they are
    // pure and unit-tested.

    /// <summary>All category tabs: the All tab first, then every
    /// <see cref="DspModuleCategory"/> (enum order) that has at least one
    /// catalogue module.</summary>
    public ObservableCollection<GearRailTab> Tabs { get; } = [];

    /// <summary>The gear cards passing the current tab + search.</summary>
    public ObservableCollection<GearCardViewModel> Cards { get; } = [];

    public bool HasCards => Cards.Count > 0;

    /// <summary>True when the native catalogue has any modules at all (hides
    /// the whole rail on a build without the DSP ABI).</summary>
    public bool HasAnyGear => _catalog.Count > 0;

    [ObservableProperty] private GearRailTab? _selectedTab;

    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnSelectedTabChanged(GearRailTab? value) => Rebuild();

    private readonly Shell.CoalescedFilterPass _searchPass;

    /// <summary>The typing funnel — read by the test that pins the coalescing.</summary>
    internal Shell.CoalescedFilterPass SearchPass => _searchPass;

    partial void OnSearchTextChanged(string value) => _searchPass.Request();

    // ---- favourites (per install, AppConfig.RigFavoriteGear) -------------------

    /// <summary>
    ///     While true the rebuilt <see cref="Cards" /> hold ONLY starred units —
    ///     the gear library's Favourites tab. False is the whole catalogue, which
    ///     is what the rail has always shown.
    /// </summary>
    [ObservableProperty] private bool _showFavoritesOnly;

    partial void OnShowFavoritesOnlyChanged(bool value) => Rebuild();

    /// <summary>Raised after a star is set or cleared. The Fire screen's gear rows
    /// carry their own <c>IsFavorite</c> flag (they are an ItemsControl inside an
    /// ItemsControl, so the row cannot reach this), and this is what tells it to
    /// re-read them without rebuilding the row templates (100 catalogue rows;
    /// the palette itself is virtualized).</summary>
    public event Action? FavoritesChanged;

    /// <summary>Whether <paramref name="moduleTypeId"/> is starred.</summary>
    public bool IsFavorite(string? moduleTypeId) => GearRailFilter.IsFavorite(_favorites, moduleTypeId);

    /// <summary>Whether anything is starred at all — what an empty-state note on
    /// the Favourites tab binds to.</summary>
    public bool HasFavorites => _favorites.Count > 0;

    /// <summary>
    ///     Stars / unstars a catalogue module and persists the set at once. A star
    ///     is a one-click gesture, so it is written on the click rather than at
    ///     some later flush — <see cref="IRigConfigService" /> coalesces the
    ///     FILE write itself, so this costs no disk I/O on the UI thread.
    /// </summary>
    [RelayCommand]
    private void ToggleFavorite(string? moduleTypeId)
    {
        if (string.IsNullOrEmpty(moduleTypeId)) return;
        if (!_favorites.Remove(moduleTypeId)) _favorites.Add(moduleTypeId);

        PersistFavorites();
        OnPropertyChanged(nameof(HasFavorites));
        FavoritesChanged?.Invoke();
        // On the Favourites tab the un-starred row has to LEAVE the list; on the
        // Browse tab the structure did not change and only the star did.
        if (ShowFavoritesOnly) Rebuild();
    }

    private void LoadFavorites()
    {
        if (_config == null) return;
        try
        {
            foreach (var id in _config.LoadRigConfig().RigFavoriteGear ?? [])
            {
                if (!string.IsNullOrWhiteSpace(id))
                    _favorites.Add(id.Trim());
            }
        }
        catch { /* a favourites list is a shortcut — never let a config read break the rail */ }
    }

    private void PersistFavorites()
    {
        if (_config == null) return;
        try
        {
            // Update, not Load+Save: this writes a COLLECTION member, and an
            // unlocked read-modify-write races a concurrent serialize.
            var ids = _favorites.OrderBy(id => id, StringComparer.Ordinal).ToList();
            _config.UpdateRigConfig(cfg => cfg.RigFavoriteGear = ids);
        }
        catch { /* a favourites list is a shortcut — never let a config write break the rail */ }
    }

    /// <summary>Adds a card's module at the chain end (the click path).</summary>
    [RelayCommand]
    private void AddGear(GearCardViewModel? card)
    {
        if (card == null) return;
        _addAtEnd(card.Type);
    }

    // `AddGearModelCommand` is DELETED (0.58). The model PICKERS are not on the
    // gear card — `AmpFxRigView.axaml` says so at the card template: they live on
    // the module editor's name plate, as `RigDockUnit.ModelRail` bound two-way to
    // `PedalModelSelectorViewModel.SelectedOption`. So adding is `AddGearCommand`
    // and choosing the model happens afterwards, on the unit.
    //
    // The per-option `GearModelOption.AddCommand` below still routes through
    // `_addModelAtEnd`, which is what keeps that callback (and
    // `PedalboardViewModel.AddGearWithModel`) live.

    private void BuildKindTabs()
    {
        var present = new HashSet<GearRailKind>();
        foreach (var m in _catalog)
            present.Add(GearRailFilter.KindOf(m.Id));

        KindTabs.Clear();
        foreach (var k in Enum.GetValues<GearRailKind>())
        {
            if (!present.Contains(k)) continue;
            KindTabs.Add(new GearKindTab(k, KindLabel(_loc, k), KindGlyph(k)));
        }
    }

    private void BuildTabs()
    {
        var present = new HashSet<DspModuleCategory>();
        foreach (var m in _catalog)
            present.Add(DspModuleVisuals.Categorize(m.Id));

        Tabs.Clear();
        Tabs.Add(new GearRailTab(null, _loc["GearRail_All"], "▦"));
        foreach (var c in Enum.GetValues<DspModuleCategory>())
        {
            if (!present.Contains(c)) continue;
            Tabs.Add(new GearRailTab(c, CategoryLabel(_loc, c), CategoryGlyph(c)));
        }
    }

    private void Rebuild()
    {
        Cards.Clear();
        var tab = SelectedTab?.Category;
        var kind = SelectedKind?.Kind;

        // Curated-quality order: the flagship gear (amps/cabs → drives →
        // time/mod → utility) surfaces first via the shared GearCuration seam,
        // ties broken by display name for a stable A–Z within a rank.
        var matches = new List<GearCardViewModel>();
        foreach (var m in _catalog)
        {
            var category = DspModuleVisuals.Categorize(m.Id);
            // The kind tab is a FORM filter over the character/search filters —
            // an amp is never on the STOMP tab whatever its character is.
            if (!GearRailFilter.MatchesKind(kind, m.Id)) continue;
            // …and the Favourites tab is a filter over ALL of them: a search on
            // the Favourites tab searches the favourites.
            if (!GearRailFilter.MatchesFavorites(ShowFavoritesOnly, _favorites, m.Id)) continue;
            // Match the module OR any of its named models (so "Plexi" surfaces the
            // amp card even though the module is just "Amp").
            if (!GearRailFilter.Matches(m.DisplayName, m.Id, category, tab, SearchText)
                && !MatchesModelLabel(m, tab, category, SearchText))
            {
                continue;
            }

            matches.Add(new GearCardViewModel(m, CategoryLabel(_loc, category), _loc, _addModelAtEnd));
        }

        matches.Sort((a, b) =>
        {
            var byRank = GearCuration.RankOf(a.ModuleTypeId).CompareTo(GearCuration.RankOf(b.ModuleTypeId));
            return byRank != 0
                ? byRank
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        });

        foreach (var card in matches)
            Cards.Add(card);

        OnPropertyChanged(nameof(HasCards));
    }

    /// <summary>Localized label for a module visual family (gear-rail tabs +
    /// card subtitles).</summary>
    public static string CategoryLabel(ILocalizationService localization, DspModuleCategory category) =>
        localization[category switch
        {
            DspModuleCategory.Dynamics => "GearRail_Cat_Dynamics",
            DspModuleCategory.Eq => "GearRail_Cat_Eq",
            DspModuleCategory.Filter => "GearRail_Cat_Filter",
            DspModuleCategory.Drive => "GearRail_Cat_Drive",
            DspModuleCategory.Amp => "GearRail_Cat_Amp",
            DspModuleCategory.Cab => "GearRail_Cat_Cab",
            DspModuleCategory.Modulation => "GearRail_Cat_Modulation",
            DspModuleCategory.Time => "GearRail_Cat_Time",
            DspModuleCategory.Pitch => "GearRail_Cat_Pitch",
            _ => "GearRail_Cat_Utility"
        }];

    /// <summary>True when the module's named model labels match the search on the
    /// current tab (feeds "Plexi" → the Amp card). Pure filter in
    /// <see cref="GearRailFilter.MatchesModelLabel"/>.</summary>
    private static bool MatchesModelLabel(DspModuleType m, DspModuleCategory? tab,
        DspModuleCategory category, string query)
        => GearRailFilter.MatchesModelLabel(category, tab, GearCardViewModel.ModelLabelsOf(m), query);

    /// <summary>Localized label for a gear FORM tab (stomp / amp / cab / rack).</summary>
    public static string KindLabel(ILocalizationService localization, GearRailKind kind) =>
        localization[kind switch
        {
            GearRailKind.Amp => "GearRail_Kind_Amp",
            GearRailKind.Cab => "GearRail_Kind_Cab",
            GearRailKind.Rack => "GearRail_Kind_Rack",
            _ => "GearRail_Kind_Stomp"
        }];

    /// <summary>Compact glyph per gear form (text glyphs, no bitmaps).</summary>
    public static string KindGlyph(GearRailKind kind) => kind switch
    {
        GearRailKind.Amp => "▤",
        GearRailKind.Cab => "▥",
        GearRailKind.Rack => "⑂",
        _ => "▯"
    };

    /// <summary>Compact tab glyph per family (text glyphs, no bitmaps).</summary>
    public static string CategoryGlyph(DspModuleCategory category) => category switch
    {
        DspModuleCategory.Dynamics => "◧",
        DspModuleCategory.Eq => "≡",
        DspModuleCategory.Filter => "◠",
        DspModuleCategory.Drive => "⚡",
        DspModuleCategory.Amp => "▤",
        DspModuleCategory.Cab => "▥",
        DspModuleCategory.Modulation => "∿",
        DspModuleCategory.Time => "◷",
        DspModuleCategory.Pitch => "♯",
        _ => "◌"
    };
}

/// <summary>One gear-browser FORM tab (stomp / amp / cab / rack).</summary>
public sealed partial class GearKindTab : ObservableObject
{
    public GearKindTab(GearRailKind kind, string label, string glyph)
    {
        Kind = kind;
        Label = label;
        Glyph = glyph;
    }

    public GearRailKind Kind { get; }
    public string Label { get; }
    public string Glyph { get; }

    /// <summary>Lit state, driven by the rail (the tabs are toggles, not a
    /// ListBox — a ListBox cannot express "no form filter").</summary>
    [ObservableProperty] private bool _isActive;
}

/// <summary>One gear-rail category tab.</summary>
public sealed class GearRailTab
{
    public GearRailTab(DspModuleCategory? category, string label, string glyph)
    {
        Category = category;
        Label = label;
        Glyph = glyph;
    }

    /// <summary>Null = the All tab.</summary>
    public DspModuleCategory? Category { get; }

    public string Label { get; }
    public string Glyph { get; }
}

/// <summary>One gear card: a catalogue module type plus the display metadata
/// its card miniature needs.</summary>
public sealed class GearCardViewModel
{
    private readonly ILocalizationService _loc;

    public GearCardViewModel(DspModuleType type, string categoryLabel, ILocalizationService localization,
        Action<DspModuleType, string, int>? addModel = null)
    {
        Type = type;
        _loc = localization;
        CategoryLabel = categoryLabel;

        // "Browse by model" (ABI v24): if the module declares a MODEL param
        // (amp voicing, cab/drive model), expose one option per model so the rail
        // can add it PRE-SET to that voicing ("Plexi 100" adds amp voicing=10).
        // Each option carries its OWN add command (no ancestor/ElementName binding
        // from inside the flyout popup). A module with no declared model param —
        // a chorus, a freeze — gets no flyout at all, however many labelled params
        // it has: see DspModuleVisuals.ModelParamId.
        var modelParam = ModelParamOf(type);
        var options = modelParam?.ValueLabels is { Count: > 0 } labels
            ? labels.Select((l, i) => new GearModelOption(type, modelParam.Id, i, l)).ToList()
            : [];
        if (addModel != null)
        {
            foreach (var o in options)
                o.AddCommand = new RelayCommand(() => addModel(o.Type, o.ParamId, o.Index));
        }

        Models = options;
    }

    public DspModuleType Type { get; }
    public string ModuleTypeId => Type.Id;
    public string DisplayName => Type.DisplayName is { Length: > 0 } n ? n : Type.Id;
    public int ParamCount => Type.Params.Count;
    public string CategoryLabel { get; }

    /// <summary>The module's named models (amp voicings, cab speaker models);
    /// empty for a module with no labelled model param.</summary>
    public IReadOnlyList<GearModelOption> Models { get; }

    public bool HasModels => Models.Count > 0;

    /// <summary>Localized "Browse models…" affordance label.</summary>
    public string ModelsLabel => _loc["GearRail_BrowseModels"];

    /// <summary>Tooltip for the model-browse flyout button (amp vs cab).</summary>
    public string ModelsTooltip => ModuleTypeId == "cab"
        ? _loc["GearRail_CabModelTip"]
        : _loc["GearRail_AmpModelTip"];

    /// <summary>
    /// The module's <b>model</b> param (amp voicing, cab/drive model), or null when
    /// the module has none. Resolved from the module's DECLARED model param id
    /// (<see cref="DspModuleVisuals.ModelParamId"/>) — deliberately NOT "the first
    /// labelled param": every discrete param carries value labels now (a tremolo
    /// shape, a wah mode, a freeze footswitch), and none of those is a model the
    /// rail may pre-set when it adds the pedal. Returns null unless the declared
    /// param is actually present AND labelled, so a native catalogue change
    /// degrades to "no model browsing" rather than a wrong pick.
    /// </summary>
    public static DspParamInfo? ModelParamOf(DspModuleType type)
    {
        if (DspModuleVisuals.ModelParamId(type.Id) is not { } modelParamId) return null;
        foreach (var p in type.Params)
        {
            if (string.Equals(p.Id, modelParamId, StringComparison.Ordinal)
                && p.ValueLabels is { Count: > 0 })
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>The module's model label strings (for search matching), or null.</summary>
    public static IReadOnlyList<string>? ModelLabelsOf(DspModuleType type)
        => ModelParamOf(type)?.ValueLabels;

    /// <summary>Card tooltip: the split/merge routing nodes carry an explanation
    /// of parallel branches (adding a splitter auto-pairs a merger); every other
    /// module falls back to its family label.</summary>
    public string Tooltip => ModuleTypeId switch
    {
        SplitRegionLayout.SplitterId => _loc["GearRail_Splitter_Tip"],
        SplitRegionLayout.MergerId => _loc["GearRail_Merger_Tip"],
        _ => _loc["GearRail_CardTip"]
    };
}

/// <summary>One "add this module pre-set to a named model" option in the gear
/// rail's browse-by-model flyout — the module type, the labelled param id and the
/// 0-based model index (== the param value) plus its ABI label.</summary>
public sealed class GearModelOption
{
    public GearModelOption(DspModuleType type, string paramId, int index, string label)
    {
        Type = type;
        ParamId = paramId;
        Index = index;
        Label = label;
    }

    public DspModuleType Type { get; }
    public string ParamId { get; }
    public int Index { get; }
    public string Label { get; }

    /// <summary>Adds the module pre-set to THIS model — bound directly by the
    /// gear-rail flyout item so there's no ancestor/ElementName binding across
    /// the popup boundary. Null when the rail has no model-add callback.</summary>
    public System.Windows.Input.ICommand? AddCommand { get; set; }
}
