using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Infrastructure.Appearance;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ Appearance: the string palette, the interface scale and the
///     per-colour customiser, each previewed live through <see cref="IAppearanceTheme" />
///     and <see cref="IInterfaceScale" />.
///     <para>
///         Putting the persisted theme back costs 30–80 ms (it invalidates every
///         <c>DynamicResource</c> in the app), so Revert does it only when a colour
///         was previewed since the last load or save.
///     </para>
/// </summary>
public sealed partial class AppearanceSettingsViewModel : ObservableObject,
    IAppearanceSettingsScreen, ISettingsTab, ISettingsChangeSource, IDisposable
{
    private const string Prefix = "appearance.";
    private const string ColorKeyPrefix = Prefix + "color.";

    /// <summary>The persisted values the footer counts. The colours are not among them: a colour edit is reverted, not counted.</summary>
    private static readonly string[] Tracked = [nameof(SelectedStringPaletteIndex), nameof(UiScaleChoiceIndex), nameof(AnimatedHomeHero)];

    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly IAppearanceTheme _theme;
    private readonly IInterfaceScale _scale;
    private readonly SettingsSession _session;
    private readonly IAppLogger? _logger;
    private readonly SettingsChangeSet _changes = new();
    private readonly List<AppearanceColorGroupViewModel> _groups = [];
    private string _uiScaleReadout = "";
    private bool _suppressPreview;
    private bool _previewDirty;

    [ObservableProperty] private int _selectedStringPaletteIndex;
    [ObservableProperty] private int _uiScaleChoiceIndex;
    [ObservableProperty] private bool _animatedHomeHero;

    public AppearanceSettingsViewModel(
        IConfigurationService config,
        ILocalizationService localization,
        IAppearanceTheme theme,
        IInterfaceScale scale,
        SettingsSession session,
        IAppLogger? logger = null)
    {
        _config = config;
        _localization = localization;
        _theme = theme;
        _scale = scale;
        _session = session;
        _logger = logger;
        UiScaleChoices = scale.ChoiceLabels();
        StringPaletteChoices = [.. theme.StringPalettes.Select(p => p.DisplayName)];
        BuildColorGroups();
        ResetColorsCommand = new RelayCommand(ResetColors);
        _changes.TrackProperties(this, Prefix, Tracked, () => OnPropertyChanged(nameof(UnsavedSettings)));
        _changes.Rebaseline();
        _session.PropertyChanged += OnSessionChanged;
    }

    /// <summary>Built once: a replaced list would make the combo drop its selection and write it back as a pick.</summary>
    public IReadOnlyList<string> StringPaletteChoices { get; }

    public IReadOnlyList<string> UiScaleChoices { get; }

    public string UiScaleReadout
    {
        get => _uiScaleReadout;
        private set => SetProperty(ref _uiScaleReadout, value);
    }

    public IReadOnlyList<IAppearanceColorGroup> ColorGroups => _groups;

    public ICommand ResetColorsCommand { get; }

    public IReadOnlySet<string> HiddenKeys { get; private set; } = new HashSet<string>();

    public string Key => SettingsTabKeys.Appearance;

    public Type? Screen => typeof(IAppearanceSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    /// <summary>True while the applied theme may differ from the persisted one.</summary>
    public bool IsPreviewDirty => _previewDirty;

    /// <summary>The key a view binds a whole group's visibility to: 0 is Interface, 1 the colour list.</summary>
    public static string GroupKey(int index) => Prefix + "group." + index;

    public IReadOnlyList<SettingsSearchGroup> SearchGroups =>
    [
        new(_localization["Settings_Appearance_InterfaceTitle"], null,
        [
            new(Prefix + "stringPalette", _localization["Settings_Appearance_StringColorsTitle"],
                _localization["Settings_Appearance_StringColorsSubtitle"]),
            new(Prefix + "uiScale", _localization["Settings_UiScale"], _localization["Settings_UiScaleSubtitle"]),
            new(Prefix + "animatedHomeHero", _localization["Settings_Graphics_AnimatedHomeHero"],
                _localization["Settings_Graphics_AnimatedHomeHero_Desc"])
        ]),
        new(_localization["Settings_Appearance_CustomizeTitle"], _localization["Settings_Appearance_CustomizeSubtitle"],
        [
            .. _groups.SelectMany(g => g.Rows.Select(r =>
                new SettingsSearchRow(r.SearchKey, _localization[TokenOf(r).NameKey], _localization[g.TitleKey])))
        ])
    ];

    /// <summary>Re-reads the persisted palette and scale. The applied theme is left alone, so a reload never clobbers a preview.</summary>
    public void Load()
    {
        var config = _config.LoadAppConfig();
        _suppressPreview = true;
        try
        {
            SelectedStringPaletteIndex = PaletteIndexOf(config.StringColorPresetId);
            _scale.Restore(config.UiScaleAuto, config.UiScale);
            UiScaleChoiceIndex = _scale.IndexFor(config.UiScaleAuto, _scale.Clamp(config.UiScale));
            AnimatedHomeHero = config.AnimatedHomeHero;
        }
        finally
        {
            _suppressPreview = false;
        }

        SyncUiScaleReadout();
        RefreshColors();
        _previewDirty = false;
        Rebaseline();
    }

    public void Save()
    {
        var config = _config.LoadAppConfig();
        var paletteId = SelectedPaletteId();
        config.StringColorPresetId = paletteId;
        var paletteActive = !string.Equals(paletteId, _theme.DefaultStringPaletteId, StringComparison.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(_theme.FreeFormOverrides(excludeStringTokens: paletteActive));
        config.ThemeColorOverrides = overrides;

        // Under Auto the manual factor is left as it was, so choosing a percentage
        // again restores the user's own choice rather than the window's.
        config.UiScaleAuto = _scale.IsAuto(UiScaleChoiceIndex);
        if (_scale.FactorFor(UiScaleChoiceIndex) is { } manualFactor) config.UiScale = _scale.Clamp(manualFactor);
        config.AnimatedHomeHero = AnimatedHomeHero;
        _config.SaveAppConfig(config);
        _logger?.Info(AppLogCategory.Settings, string.Create(CultureInfo.InvariantCulture,
            $"Appearance saved. stringColors={paletteId}, uiScale={config.UiScale:0.##} " +
            $"(auto={config.UiScaleAuto}, applied={_scale.AppliedFactor:0.##}), overrides={overrides.Count}."));
        _previewDirty = false;
        Rebaseline();
    }

    public void Revert()
    {
        RestorePersistedTheme();
        Load();
    }

    public void Reset() => Revert();

    public void Rebaseline()
    {
        _changes.Rebaseline();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    public void ApplySearch(string query)
    {
        var hidden = SettingsSearch.HiddenKeys(SearchGroups, query, GroupKey, out _);
        foreach (var group in _groups)
        {
            foreach (var row in group.Rows) row.IsVisible = !hidden.Contains(row.SearchKey);
            group.RefreshVisibility();
        }

        HiddenKeys = hidden;
        OnPropertyChanged(nameof(HiddenKeys));
    }

    public void Dispose() => _session.PropertyChanged -= OnSessionChanged;

    partial void OnSelectedStringPaletteIndexChanged(int value)
    {
        if (_suppressPreview || value < 0) return;
        _previewDirty = true;
        _theme.ApplyStringPalette(SelectedPaletteId());
        RefreshColors();
    }

    partial void OnUiScaleChoiceIndexChanged(int value)
    {
        if (_suppressPreview || value < 0) return;
        _scale.Apply(value);
        SyncUiScaleReadout();
    }

    private void ResetColors()
    {
        _previewDirty = true;
        _theme.ResetColors(SelectedPaletteId());
        RefreshColors();
    }

    /// <summary>A backup import replaced the persisted theme; the palette on screen is the one it replaced.</summary>
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsSession.ReloadRequests)) return;
        var config = _config.LoadAppConfig();
        _theme.ApplyPersisted(config.ThemeColorOverrides, config.StringColorPresetId);
        _previewDirty = false;
        RefreshColors();
    }

    private void RestorePersistedTheme()
    {
        if (!_previewDirty) return;
        var config = _config.LoadAppConfig();
        _theme.ApplyPersisted(config.ThemeColorOverrides, config.StringColorPresetId);
        _previewDirty = false;
    }

    private void PreviewColor(string tokenKey, string hex)
    {
        _previewDirty = true;
        _theme.SetHex(tokenKey, hex);
        var row = _groups.SelectMany(g => g.Rows).First(r => r.TokenKey == tokenKey);
        row.Refresh(row.Name, _theme.GetHex(tokenKey));
    }

    private void BuildColorGroups()
    {
        foreach (var run in _theme.Tokens.GroupBy(t => t.GroupKey))
        {
            var rows = run.Select(t => new AppearanceColorItemViewModel(t.Key, ColorKeyPrefix + t.Key, PreviewColor)).ToList();
            _groups.Add(new AppearanceColorGroupViewModel(run.Key, rows));
        }

        RefreshColors();
    }

    private void RefreshColors()
    {
        foreach (var group in _groups)
        {
            group.Title = _localization[group.TitleKey];
            foreach (var row in group.Rows) row.Refresh(_localization[TokenOf(row).NameKey], _theme.GetHex(row.TokenKey));
        }
    }

    private ThemeTokenEntry TokenOf(AppearanceColorItemViewModel row) => _theme.Tokens.First(t => t.Key == row.TokenKey);

    private void SyncUiScaleReadout() =>
        UiScaleReadout = _scale.AppliedFactor.ToString("P0", CultureInfo.CurrentCulture);

    private int PaletteIndexOf(string? id)
    {
        var palettes = _theme.StringPalettes;
        for (var i = 0; i < palettes.Count; i++)
        {
            if (string.Equals(palettes[i].Id, id, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < palettes.Count; i++)
        {
            if (string.Equals(palettes[i].Id, _theme.DefaultStringPaletteId, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private string SelectedPaletteId()
    {
        var palettes = _theme.StringPalettes;
        return SelectedStringPaletteIndex >= 0 && SelectedStringPaletteIndex < palettes.Count
            ? palettes[SelectedStringPaletteIndex].Id
            : _theme.DefaultStringPaletteId;
    }
}
