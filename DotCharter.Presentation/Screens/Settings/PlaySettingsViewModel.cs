using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>Settings ▸ Play: the count-in, the metronome, note claps, the rock meter and the String order of a Play Mode run.</summary>
public sealed partial class PlaySettingsViewModel : ObservableObject,
    IPlaySettingsScreen, ISettingsTab, ISettingsChangeSource
{
    private static readonly SettingsTabRows Rows = new("play.",
    [
        new("Settings_Gameplay", "Settings_GameplaySubtitle",
            new("countIn", "Settings_GameplayCountIn_RowLabel", "Settings_Tip_GameplayCountIn"),
            new("metronome", "Settings_GameplayMetronome", "Settings_Tip_GameplayMetronome"),
            new("claps", "Settings_GameplayClaps", "Settings_Tip_GameplayClaps"),
            new("rockMeter", "PlayMode_RockMeter_SettingLabel", "PlayMode_RockMeter_SettingDesc"),
            new("stringOrder", "Settings_StringOrder", "Settings_Tip_PlayStringOrder"))
    ]);

    private static readonly string[] Tracked =
    [
        nameof(GameplayCountInIndex), nameof(GameplayMetronomeEnabled), nameof(GameplayClapsEnabled),
        nameof(GameplayRockMeterEnabled), nameof(StringOrderIndex)
    ];

    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly ProjectState _state;
    private readonly SettingsChangeSet _changes = new();

    [ObservableProperty] private int _gameplayCountInIndex;
    [ObservableProperty] private bool _gameplayMetronomeEnabled;
    [ObservableProperty] private bool _gameplayClapsEnabled;
    [ObservableProperty] private bool _gameplayRockMeterEnabled;
    [ObservableProperty] private int _stringOrderIndex;

    public PlaySettingsViewModel(IConfigurationService config, ILocalizationService localization, ProjectState state)
    {
        _config = config;
        _localization = localization;
        _state = state;
        _changes.TrackProperties(this, Rows.Prefix, Tracked, () => OnPropertyChanged(nameof(UnsavedSettings)));
        _changes.Rebaseline();
    }

    public IReadOnlySet<string> HiddenKeys { get; private set; } = new HashSet<string>();

    public string Key => SettingsTabKeys.Play;

    public Type? Screen => typeof(IPlaySettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    public IReadOnlyList<SettingsSearchGroup> SearchGroups => Rows.Localize(_localization);

    /// <summary>The key a view binds a whole group's visibility to.</summary>
    public static string GroupKey(int index) => Rows.GroupKey(index);

    public void Load()
    {
        var config = _config.LoadAppConfig();
        GameplayCountInIndex = GameplayCountInRows.BeatsToIndex(config.GameplayCountInBeats);
        GameplayMetronomeEnabled = config.GameplayMetronomeEnabled;
        GameplayClapsEnabled = config.GameplayClapsEnabled;
        GameplayRockMeterEnabled = config.GameplayRockMeterEnabled;
        StringOrderIndex = config.PlayModeInvertStrings ? 1 : 0;
        Rebaseline();
    }

    public void Save()
    {
        var config = _config.LoadAppConfig();
        config.GameplayCountInBeats = GameplayCountInRows.IndexToBeats(GameplayCountInIndex);
        config.GameplayMetronomeEnabled = GameplayMetronomeEnabled;
        config.GameplayClapsEnabled = GameplayClapsEnabled;
        config.GameplayRockMeterEnabled = GameplayRockMeterEnabled;
        config.PlayModeInvertStrings = StringOrderIndex == 1;
        _config.SaveAppConfig(config);
        _state.PlayModeInvertStrings = config.PlayModeInvertStrings;
        Rebaseline();
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
