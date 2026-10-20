using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The sixteen toggles that live on <c>ProjectState</c> (the renderers read
///     them there), the settings the footer counts, and the search.
/// </summary>
public sealed partial class GraphicsSettingsViewModel
{
    private const string Prefix = "graphics.";

    [ObservableProperty] private bool _isAdvancedOpen;

    public IReadOnlySet<string> HiddenKeys { get; private set; } = new HashSet<string>();

    public bool EnableAnchorPreview { get => _state.EnableAnchorPreview; set => _state.EnableAnchorPreview = value; }
    public bool EnableAnchorSweep { get => _state.EnableAnchorSweep; set => _state.EnableAnchorSweep = value; }
    public bool EnableHighwayNearFade { get => _state.EnableHighwayNearFade; set => _state.EnableHighwayNearFade = value; }
    public bool EnableNoteHitExplosions { get => _state.EnableNoteHitExplosions; set => _state.EnableNoteHitExplosions = value; }
    public bool EnableEnhancedHitFeedback { get => _state.EnableEnhancedHitFeedback; set => _state.EnableEnhancedHitFeedback = value; }
    public bool EnableHitSparks { get => _state.EnableHitSparks; set => _state.EnableHitSparks = value; }
    public bool EnableBeatPulse { get => _state.EnableBeatPulse; set => _state.EnableBeatPulse = value; }
    public bool EnableDepthFog { get => _state.EnableDepthFog; set => _state.EnableDepthFog = value; }
    public bool EnableVignette { get => _state.EnableVignette; set => _state.EnableVignette = value; }
    public bool EnableHighwayLighting { get => _state.EnableHighwayLighting; set => _state.EnableHighwayLighting = value; }
    public bool EnableBloom { get => _state.EnableBloom; set => _state.EnableBloom = value; }
    public bool EnableStringShimmer { get => _state.EnableStringShimmer; set => _state.EnableStringShimmer = value; }
    public bool EnableFilmicTonemap { get => _state.EnableFilmicTonemap; set => _state.EnableFilmicTonemap = value; }
    public bool EnableShowlightBeams { get => _state.EnableShowlightBeams; set => _state.EnableShowlightBeams = value; }
    public bool EnableShowlightFog { get => _state.EnableShowlightFog; set => _state.EnableShowlightFog = value; }
    public bool EnableCrowdBackground { get => _state.EnableCrowdBackground; set => _state.EnableCrowdBackground = value; }

    /// <summary>
    ///     Every name this screen passes straight through to <c>ProjectState</c>.
    ///     The state is the store, and Play Mode's own settings modal writes the
    ///     same properties, so a change from either side has to reach the rows.
    /// </summary>
    private static readonly HashSet<string> StateBacked = new(StringComparer.Ordinal)
    {
        nameof(EnableAnchorPreview), nameof(EnableAnchorSweep), nameof(EnableHighwayNearFade),
        nameof(EnableNoteHitExplosions), nameof(EnableEnhancedHitFeedback), nameof(EnableHitSparks),
        nameof(EnableBeatPulse), nameof(EnableDepthFog), nameof(EnableVignette), nameof(EnableHighwayLighting),
        nameof(EnableBloom), nameof(EnableStringShimmer), nameof(EnableFilmicTonemap),
        nameof(EnableShowlightBeams), nameof(EnableShowlightFog), nameof(EnableCrowdBackground)
    };

    /// <summary>The settings the footer counts: persisted values only, never status.</summary>
    private static readonly string[] Tracked =
    [
        nameof(RenderScalePercent), nameof(SelectedMaxFps), nameof(IdleFps), nameof(SelectedPresentMode),
        nameof(SelectedMsaa), nameof(AnimatedHomeHero), nameof(AnchorPreviewLeadMs), nameof(VisualLeadMs),
        nameof(HighwayScrollSpeedOverrideEnabled), nameof(HighwayScrollSpeed), nameof(HighwayDrawDistanceZ),
        nameof(CameraDistancePercent), nameof(Show3DFingering), nameof(Timeline2DPolish),
        nameof(Timeline2DLaneTint), nameof(InvertStrings)
    ];

    private void TrackSettings()
    {
        var type = GetType();
        foreach (var name in Tracked.Concat(StateBacked))
        {
            var property = type.GetProperty(name)!;
            _changes.Track(Prefix + name, () => property.GetValue(this));
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && StateBacked.Contains(name)) OnPropertyChanged(name);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && (StateBacked.Contains(name) || Tracked.Contains(name)))
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(UnsavedSettings)));
    }

    private void LoadEffects(AppConfig config)
    {
        _state.EnableAnchorPreview = config.EnableAnchorPreview;
        _state.EnableAnchorSweep = config.EnableAnchorSweep;
        _state.EnableHighwayNearFade = config.EnableHighwayNearFade;
        _state.EnableNoteHitExplosions = config.EnableNoteHitExplosions;
        _state.EnableEnhancedHitFeedback = config.EnableEnhancedHitFeedback;
        _state.EnableHitSparks = config.EnableHitSparks;
        _state.EnableBeatPulse = config.EnableBeatPulse;
        _state.EnableDepthFog = config.EnableDepthFog;
        _state.EnableVignette = config.EnableVignette;
        _state.EnableHighwayLighting = config.EnableHighwayLighting;
        _state.EnableBloom = config.EnableBloom;
        _state.EnableStringShimmer = config.EnableStringShimmer;
        _state.EnableFilmicTonemap = config.EnableFilmicTonemap;
        _state.EnableShowlightBeams = config.EnableShowlightBeams;
        _state.EnableShowlightFog = config.EnableShowlightFog;
        _state.EnableCrowdBackground = config.EnableCrowdBackground;
    }

    private void SaveEffects(AppConfig config)
    {
        config.EnableAnchorPreview = _state.EnableAnchorPreview;
        config.EnableAnchorSweep = _state.EnableAnchorSweep;
        config.EnableHighwayNearFade = _state.EnableHighwayNearFade;
        config.EnableNoteHitExplosions = _state.EnableNoteHitExplosions;
        config.EnableEnhancedHitFeedback = _state.EnableEnhancedHitFeedback;
        config.EnableHitSparks = _state.EnableHitSparks;
        config.EnableBeatPulse = _state.EnableBeatPulse;
        config.EnableDepthFog = _state.EnableDepthFog;
        config.EnableVignette = _state.EnableVignette;
        config.EnableHighwayLighting = _state.EnableHighwayLighting;
        config.EnableBloom = _state.EnableBloom;
        config.EnableStringShimmer = _state.EnableStringShimmer;
        config.EnableFilmicTonemap = _state.EnableFilmicTonemap;
        config.EnableShowlightBeams = _state.EnableShowlightBeams;
        config.EnableShowlightFog = _state.EnableShowlightFog;
        config.EnableCrowdBackground = _state.EnableCrowdBackground;
    }

    public IReadOnlyList<SettingsSearchGroup> SearchGroups =>
    [
        Group("Display", "renderScale", "maxFps", "idleFps", "presentMode", "msaa", "animatedHomeHero"),
        Group("AnchorPreview", "anchorPreview", "anchorSweep", "anchorLead"),
        Group("HighwayView", "scrollSpeedOverride", "scrollSpeed", "drawDistance", "cameraDistance",
            "show3DFingering", "invertStrings"),
        Group("Timing", "visualLead"),
        Group("Timeline2D", "timeline2DPolish", "timeline2DLaneTint"),
        Group("Highway", "highwayNearFade", "noteHitExplosions", "enhancedHitFeedback", "hitSparks", "beatPulse",
            "depthFog", "vignette", "highwayLighting", "bloom", "stringShimmer", "filmicTonemap"),
        Group("Showlights", "showlightBeams", "showlightFog"),
        Group("Background", "crowdBackground")
    ];

    /// <summary>The last three groups sit behind the Advanced disclosure.</summary>
    private const int FirstAdvancedGroup = 5;

    public void ApplySearch(string query)
    {
        HiddenKeys = SettingsSearch.HiddenKeys(SearchGroups, query, GroupKey, out var kept);
        OnPropertyChanged(nameof(HiddenKeys));
        if (query.Length > 0 && kept.Skip(FirstAdvancedGroup).Any(k => k)) IsAdvancedOpen = true;
    }

    /// <summary>The key a view binds a whole group's visibility to.</summary>
    public static string GroupKey(int index) => Prefix + "group." + index;

    private SettingsSearchGroup Group(string name, params string[] rows) =>
        new(_localization["Settings_Graphics_" + name + "Title"], _localization["Settings_Graphics_" + name + "Subtitle"],
            [.. rows.Select(Row)]);

    private SettingsSearchRow Row(string key) => key switch
    {
        "show3DFingering" => new(Prefix + key, _localization["Settings_Show3DFingering"],
            _localization["Settings_Tip_Show3DFingering"]),
        _ => new(Prefix + key, _localization[RowLabelKey(key)], _localization[RowLabelKey(key) + "_Desc"])
    };

    /// <summary>"anchorLead" → <c>Settings_Graphics_AnchorLead</c>; the resx keys are the rows' own titles.</summary>
    private static string RowLabelKey(string key) =>
        "Settings_Graphics_" + char.ToUpperInvariant(key[0]) + key[1..];
}
