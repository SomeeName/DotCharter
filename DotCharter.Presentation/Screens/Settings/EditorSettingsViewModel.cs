using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Core.Utilities;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Graphics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ Editor: how the timeline reads (String order, the 2D timeline's
///     look), editing rules, the paste filter and new-project defaults. Saving
///     re-applies the minimum note distance to the open project and redraws the
///     chart, because that rule is enforced on the chart itself rather than read
///     at the next edit.
/// </summary>
public sealed partial class EditorSettingsViewModel : ObservableObject,
    IEditorSettingsScreen, ISettingsTab, ISettingsChangeSource
{
    private static readonly SettingsTabRows Rows = new("editor.",
    [
        new("Settings_TimelineView_Title", "Settings_TimelineView_Subtitle",
            new("stringOrder", "Settings_StringOrder", "Settings_Tip_StringOrder"),
            new("timeline2DPolish", "Settings_Graphics_Timeline2DPolish", "Settings_Graphics_Timeline2DPolish_Desc"),
            new("timeline2DLaneTint", "Settings_Graphics_Timeline2DLaneTint", "Settings_Graphics_Timeline2DLaneTint_Desc")),
        new("Settings_ChartEditing", "Settings_ChartEditingSubtitle",
            new("fastInput", "Settings_FastChartingInput", "Settings_Tip_FastChartingInput"),
            new("syncHandShape", "Settings_SyncHandShapeToChord", "Settings_Tip_SyncHandShapeToChord"),
            new("syncAnchor", "Settings_SyncAnchorToFingering", "Settings_Tip_SyncAnchorToFingering"),
            new("autoNameChords", "Settings_AutoNameNewChords", "Settings_Tip_AutoNameNewChords"),
            new("suppressDoubleStop", "Settings_SuppressDoubleStopNames", "Settings_Tip_SuppressDoubleStopNames"),
            new("keepPowerChords", "Settings_KeepPowerChordNames", "Settings_Tip_KeepPowerChordNames"),
            new("musicalChordNames", "Settings_MusicalChordNames", "Settings_Tip_MusicalChordNames"),
            new("tappedHarmonicOffset", "Settings_TappedHarmonicFretOffset", "Settings_Tip_TappedHarmonicFretOffset"),
            new("lyricWordGap", "Settings_LyricSpanWordGap", "Settings_Tip_LyricSpanWordGap"),
            new("lyricPhraseGap", "Settings_LyricSpanPhraseGap", "Settings_Tip_LyricSpanPhraseGap"),
            new("minNoteDistance", "Settings_MinNoteDistance", "Settings_Tip_MinNoteDistance"),
            new("minNoteDistanceMs", "Settings_Milliseconds", "Settings_Tip_Milliseconds"),
            new("minNoteDistanceDivisor", "Settings_FractionDenominator", "Settings_Tip_FractionDenominator")),
        new("Settings_CopyPaste_Title", "Settings_CopyPaste_Subtitle",
            new SettingsRowSpec("paste", "Settings_PasteIncludesLabel")),
        new("Settings_NewProject", "Settings_NewProjectDescription",
            new("charterName", "Settings_DefaultCharterName", "Settings_Tip_DefaultCharterName"),
            new("silencePadding", "Settings_SilencePadding", "Settings_Tip_SilencePadding"),
            new("leadingSilence", "Settings_LeadingSilence", "Settings_Tip_LeadingSilence"),
            new("trailingSilence", "Settings_TrailingSilence", "Settings_Tip_TrailingSilence"))
    ]);

    /// <summary>The persisted values the footer counts: every settable contract property.</summary>
    private static readonly string[] Tracked =
    [
        nameof(UseFastChartingInput), nameof(SyncHandShapeToChordTemplate), nameof(SyncAnchorToChordFingering),
        nameof(AutoNameNewChords), nameof(SuppressDoubleStopChordNames), nameof(KeepPowerChordNames),
        nameof(MusicalChordNames), nameof(TappedHarmonicFretOffset),
        nameof(LyricSpanWordGap),
        nameof(LyricSpanPhraseGap), nameof(MinNoteDistanceModeIndex), nameof(MinNoteDistanceMs),
        nameof(MinNoteDistanceDivisor), nameof(PasteIncludeNotes), nameof(PasteIncludeChords),
        nameof(PasteIncludeHandShapes), nameof(PasteIncludeFretAnchors), nameof(PasteIncludeEvents),
        nameof(PasteIncludeTones), nameof(DefaultCharterName), nameof(PadSilenceOnNewProject),
        nameof(LeadingSilenceSeconds), nameof(TrailingSilenceSeconds), nameof(StringOrderIndex),
        nameof(Timeline2DPolish), nameof(Timeline2DLaneTint)
    ];

    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly ProjectState _state;
    private readonly IChartEditorSurface _editor;
    private readonly IGraphicsDeviceSettings _device;
    private readonly IAppLogger? _logger;
    private readonly SettingsChangeSet _changes = new();

    [ObservableProperty] private bool _useFastChartingInput;
    [ObservableProperty] private bool _syncHandShapeToChordTemplate = true;
    [ObservableProperty] private bool _syncAnchorToChordFingering;
    [ObservableProperty] private bool _autoNameNewChords = true;
    [ObservableProperty] private bool _suppressDoubleStopChordNames = true;
    [ObservableProperty] private bool _keepPowerChordNames;
    [ObservableProperty] private bool _musicalChordNames;
    [ObservableProperty] private int _tappedHarmonicFretOffset = 12;
    [ObservableProperty] private bool _lyricSpanWordGap = true;
    [ObservableProperty] private bool _lyricSpanPhraseGap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMinNoteDistanceInMilliseconds))]
    private int _minNoteDistanceModeIndex = 1;

    [ObservableProperty] private double _minNoteDistanceMs = 10.0;
    [ObservableProperty] private int _minNoteDistanceDivisor = 64;
    [ObservableProperty] private bool _pasteIncludeNotes = true;
    [ObservableProperty] private bool _pasteIncludeChords = true;
    [ObservableProperty] private bool _pasteIncludeHandShapes = true;
    [ObservableProperty] private bool _pasteIncludeFretAnchors = true;
    [ObservableProperty] private bool _pasteIncludeEvents = true;
    [ObservableProperty] private bool _pasteIncludeTones = true;
    [ObservableProperty] private string _defaultCharterName = "";
    [ObservableProperty] private bool _padSilenceOnNewProject = true;
    [ObservableProperty] private double _leadingSilenceSeconds = 10.0;
    [ObservableProperty] private double _trailingSilenceSeconds = 2.0;
    [ObservableProperty] private int _stringOrderIndex;
    [ObservableProperty] private bool _timeline2DPolish = true;
    [ObservableProperty] private bool _timeline2DLaneTint;

    public EditorSettingsViewModel(
        IConfigurationService config,
        ILocalizationService localization,
        ProjectState state,
        IChartEditorSurface editor,
        IGraphicsDeviceSettings device,
        IAppLogger? logger = null)
    {
        _config = config;
        _localization = localization;
        _state = state;
        _editor = editor;
        _device = device;
        _logger = logger;
        _changes.TrackProperties(this, Rows.Prefix, Tracked, () => OnPropertyChanged(nameof(UnsavedSettings)));
        _changes.Rebaseline();
    }

    public bool IsMinNoteDistanceInMilliseconds => MinNoteDistanceModeIndex == 0;

    public IReadOnlySet<string> HiddenKeys { get; private set; } = new HashSet<string>();

    public string Key => SettingsTabKeys.Editor;

    public Type? Screen => typeof(IEditorSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    public IReadOnlyList<SettingsSearchGroup> SearchGroups => Rows.Localize(_localization);

    /// <summary>The key a view binds a whole group's visibility to.</summary>
    public static string GroupKey(int index) => Rows.GroupKey(index);

    public void Load()
    {
        var config = _config.LoadAppConfig();
        UseFastChartingInput = config.UseFastChartingInput;
        SyncHandShapeToChordTemplate = config.SyncHandShapeToChordTemplate;
        SyncAnchorToChordFingering = config.SyncAnchorToChordFingering;
        AutoNameNewChords = config.AutoNameNewChords;
        SuppressDoubleStopChordNames = config.SuppressDoubleStopChordNames;
        KeepPowerChordNames = config.KeepPowerChordNames;
        MusicalChordNames = config.ChordNamingStyle == ChordNamingStyle.Musical;
        TappedHarmonicFretOffset = Math.Clamp(config.TappedHarmonicFretOffset, 0, 24);
        LyricSpanWordGap = config.LyricSpanWordGap;
        LyricSpanPhraseGap = config.LyricSpanPhraseGap;
        MinNoteDistanceModeIndex = config.MinNoteDistanceMode switch
        {
            MinNoteDistanceMode.Milliseconds => 0,
            MinNoteDistanceMode.BeatFraction => 2,
            _ => 1
        };
        MinNoteDistanceMs = config.MinNoteDistanceMs;
        MinNoteDistanceDivisor = config.MinNoteDistanceDivisor > 0 ? config.MinNoteDistanceDivisor : 64;

        PasteIncludeNotes = config.PasteIncludeNotes;
        PasteIncludeChords = config.PasteIncludeChords;
        PasteIncludeHandShapes = config.PasteIncludeHandShapes;
        PasteIncludeFretAnchors = config.PasteIncludeFretAnchors;
        PasteIncludeEvents = config.PasteIncludeEvents;
        PasteIncludeTones = config.PasteIncludeTones;

        DefaultCharterName = config.DefaultCharterName;
        PadSilenceOnNewProject = config.PadSilenceOnNewProject;
        LeadingSilenceSeconds = Math.Max(0, config.LeadingSilenceSeconds);
        TrailingSilenceSeconds = Math.Max(0, config.TrailingSilenceSeconds);

        StringOrderIndex = config.InvertStrings ? 1 : 0;
        Timeline2DPolish = config.Timeline2DPolishEnabled;
        Timeline2DLaneTint = config.Timeline2DLaneTint;
        Rebaseline();
    }

    public void Save()
    {
        var config = _config.LoadAppConfig();
        config.UseFastChartingInput = UseFastChartingInput;
        config.SyncHandShapeToChordTemplate = SyncHandShapeToChordTemplate;
        config.SyncAnchorToChordFingering = SyncAnchorToChordFingering;
        config.AutoNameNewChords = AutoNameNewChords;
        config.SuppressDoubleStopChordNames = SuppressDoubleStopChordNames;
        config.KeepPowerChordNames = KeepPowerChordNames;
        config.ChordNamingStyle = MusicalChordNames ? ChordNamingStyle.Musical : ChordNamingStyle.Eof;
        config.TappedHarmonicFretOffset = Math.Clamp(TappedHarmonicFretOffset, 0, 24);
        config.LyricSpanWordGap = LyricSpanWordGap;
        config.LyricSpanPhraseGap = LyricSpanPhraseGap;
        config.MinNoteDistanceMode = MinNoteDistanceModeIndex switch
        {
            0 => MinNoteDistanceMode.Milliseconds,
            2 => MinNoteDistanceMode.BeatFraction,
            _ => MinNoteDistanceMode.MeasureFraction
        };
        config.MinNoteDistanceMs = Math.Max(0, MinNoteDistanceMs);
        config.MinNoteDistanceDivisor = Math.Max(1, MinNoteDistanceDivisor);

        config.PasteIncludeNotes = PasteIncludeNotes;
        config.PasteIncludeChords = PasteIncludeChords;
        config.PasteIncludeHandShapes = PasteIncludeHandShapes;
        config.PasteIncludeFretAnchors = PasteIncludeFretAnchors;
        config.PasteIncludeEvents = PasteIncludeEvents;
        config.PasteIncludeTones = PasteIncludeTones;

        config.DefaultCharterName = DefaultCharterName.Trim();
        config.PadSilenceOnNewProject = PadSilenceOnNewProject;
        config.LeadingSilenceSeconds = Math.Max(0, LeadingSilenceSeconds);
        config.TrailingSilenceSeconds = Math.Max(0, TrailingSilenceSeconds);

        config.InvertStrings = StringOrderIndex == 1;
        config.Timeline2DPolishEnabled = Timeline2DPolish;
        config.Timeline2DLaneTint = Timeline2DLaneTint;

        _config.SaveAppConfig(config);
        ChartSpacingEnforcer.EnforceProject(_state.ActiveProject, config);
        _editor.RequestRedraw();
        _state.SetMessage(_localization["Status_EditorSettingsApplied"]);
        _logger?.Info(AppLogCategory.Settings,
            $"Editor settings saved. Min note distance mode={config.MinNoteDistanceMode}, " +
            $"divisor={config.MinNoteDistanceDivisor}, ms={config.MinNoteDistanceMs:0.###}.");
        Rebaseline();
    }

    /// <summary>The timeline turns as the order is picked; Revert puts the saved one back.</summary>
    partial void OnTimeline2DPolishChanged(bool value) => ApplyTimelineVisuals();

    partial void OnTimeline2DLaneTintChanged(bool value) => ApplyTimelineVisuals();

    /// <summary>Live, like the String order: the timeline shows a flip at once, and Revert puts it back.</summary>
    private void ApplyTimelineVisuals()
    {
        _device.SetTimelineVisuals(Timeline2DPolish, Timeline2DLaneTint);
        _editor.RequestRedraw();
    }

    partial void OnStringOrderIndexChanged(int value)
    {
        _state.InvertStrings = value == 1;
        _editor.RequestRedraw();
    }

    public void Revert() => Load();

    public void Reset() => Load();

    public void Rebaseline()
    {
        _changes.Rebaseline();
        OnPropertyChanged(nameof(UnsavedSettings));
    }

    public void ApplySearch(string query)
    {
        HiddenKeys = Rows.Hidden(_localization, query, out _);
        OnPropertyChanged(nameof(HiddenKeys));
    }
}
