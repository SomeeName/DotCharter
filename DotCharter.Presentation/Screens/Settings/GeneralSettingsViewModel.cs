using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     Settings ▸ General: language, startup screen, the first-run wizard, the
///     crash report and, behind Advanced, the log. Saving applies the language
///     and reloads the logger's filter; nothing here previews live.
/// </summary>
public sealed partial class GeneralSettingsViewModel : ObservableObject,
    IGeneralSettingsScreen, ISettingsTab, ISettingsChangeSource
{
    private const string DefaultLogFilePath = "logs/dotcharter.log";

    private static readonly SettingsTabRows Rows = new("general.",
    [
        new("Settings_LanguageRegion_Title", "Settings_LanguageRegion_Subtitle",
            new("language", "Settings_Language", "Settings_Tip_Language"),
            new("startupScreen", "Settings_StartupScreen", "Settings_Tip_StartupScreen"),
            new("runSetup", "Settings_RunSetupAgain", "Settings_Tip_RunSetupAgain")),
        new("Settings_Diagnostics", "Settings_Diagnostics_Subtitle",
            new SettingsRowSpec("crashReport", "Settings_CrashReportOptIn", "Settings_CrashReportOptIn_Hint")),
        new("Settings_Logging", "Settings_LogDescription",
            new("consoleLogging", "Settings_ConsoleOutput", "Settings_Tip_ConsoleOutput"),
            new("consoleColors", "Settings_ColoredLevels", "Settings_Tip_ColoredLevels"),
            new("captureLibraries", "Settings_CaptureLibraryLogs", "Settings_Tip_CaptureLibraryLogs"),
            new("fileLogging", "Settings_LogFile", "Settings_Tip_LogFile"),
            new("logFilePath", "Settings_LogFilePath", "Settings_Tip_LogFilePath"),
            new("rotation", "Settings_FileRotation", "Settings_Tip_FileRotation"),
            new("maxMb", "Settings_MaxMb"),
            new("keep", "Settings_Keep"),
            new("minLevel", "Settings_MinLogLevel", "Settings_Tip_MinLogLevel"),
            new("noiseControl", "Settings_NoiseControl", "Settings_Tip_NoiseControl"),
            new("repeatWindow", "Settings_WindowMs", "Settings_Tip_RepeatTiming"),
            new("repeatSummary", "Settings_SummaryMs", "Settings_RepeatTiming"),
            new("categories", "Settings_Categories", "Settings_Tip_Categories"))
    ]);

    /// <summary>Logging is the last group, behind the Advanced disclosure.</summary>
    private const int FirstAdvancedGroup = 2;

    /// <summary>The persisted values the footer counts: every settable contract property but the disclosure.</summary>
    private static readonly string[] Tracked =
    [
        nameof(SelectedLanguageIndex), nameof(SelectedStartupScreenIndex), nameof(CrashReportOptIn),
        nameof(EnableConsoleLogging), nameof(EnableConsoleColors), nameof(EnableFileLogging), nameof(LogFilePath),
        nameof(CaptureExternalConsoleLogs), nameof(MinimumLogLevelIndex), nameof(CoalesceRepeatedLogs),
        nameof(LogRepeatCoalesceWindowMs), nameof(LogRepeatSummaryIntervalMs), nameof(RotateLogFiles),
        nameof(LogFileMaxMb), nameof(LogFileMaxRollovers), nameof(LogApplication), nameof(LogProject),
        nameof(LogImport), nameof(LogEditing), nameof(LogInput), nameof(LogEvents), nameof(LogHistory),
        nameof(LogSettings), nameof(LogAudio), nameof(LogErrors), nameof(LogExternal)
    ];

    private readonly IConfigurationService _config;
    private readonly ILocalizationService _localization;
    private readonly IFirstRunSetupPrompt _setup;
    private readonly IAppLogger? _logger;
    private readonly SettingsChangeSet _changes = new();

    [ObservableProperty] private int _selectedLanguageIndex;
    [ObservableProperty] private int _selectedStartupScreenIndex;
    [ObservableProperty] private bool _crashReportOptIn;
    [ObservableProperty] private bool _enableConsoleLogging = true;
    [ObservableProperty] private bool _enableConsoleColors = true;
    [ObservableProperty] private bool _enableFileLogging = true;
    [ObservableProperty] private string _logFilePath = DefaultLogFilePath;
    [ObservableProperty] private bool _captureExternalConsoleLogs = true;
    [ObservableProperty] private int _minimumLogLevelIndex = (int)AppLogLevel.Info;
    [ObservableProperty] private bool _coalesceRepeatedLogs = true;
    [ObservableProperty] private int _logRepeatCoalesceWindowMs = 2000;
    [ObservableProperty] private int _logRepeatSummaryIntervalMs = 1000;
    [ObservableProperty] private bool _rotateLogFiles = true;
    [ObservableProperty] private double _logFileMaxMb = 10.0;
    [ObservableProperty] private int _logFileMaxRollovers = 5;
    [ObservableProperty] private bool _logApplication = true;
    [ObservableProperty] private bool _logProject = true;
    [ObservableProperty] private bool _logImport = true;
    [ObservableProperty] private bool _logEditing = true;
    [ObservableProperty] private bool _logInput = true;
    [ObservableProperty] private bool _logEvents = true;
    [ObservableProperty] private bool _logHistory = true;
    [ObservableProperty] private bool _logSettings = true;
    [ObservableProperty] private bool _logAudio = true;
    [ObservableProperty] private bool _logErrors = true;
    [ObservableProperty] private bool _logExternal = true;
    [ObservableProperty] private bool _isAdvancedOpen;

    public GeneralSettingsViewModel(
        IConfigurationService config,
        ILocalizationService localization,
        IFirstRunSetupPrompt setup,
        IAppLogger? logger = null)
    {
        _config = config;
        _localization = localization;
        _setup = setup;
        _logger = logger;
        RunSetupAgainCommand = new RelayCommand(() => _setup.Open());
        _changes.TrackProperties(this, Rows.Prefix, Tracked, () => OnPropertyChanged(nameof(UnsavedSettings)));
        _changes.Rebaseline();
    }

    public IReadOnlyList<string> LanguageOptions => [.. _localization.LanguageOptions.Select(o => o.DisplayName)];

    public ICommand RunSetupAgainCommand { get; }

    public IReadOnlySet<string> HiddenKeys { get; private set; } = new HashSet<string>();

    public string Key => SettingsTabKeys.General;

    public Type? Screen => typeof(IGeneralSettingsScreen);

    public IReadOnlyList<string> UnsavedSettings => _changes.Differences();

    public IReadOnlyList<SettingsSearchGroup> SearchGroups => Rows.Localize(_localization);

    /// <summary>The key a view binds a whole group's visibility to.</summary>
    public static string GroupKey(int index) => Rows.GroupKey(index);

    public void Load()
    {
        var config = _config.LoadAppConfig();
        OnPropertyChanged(nameof(LanguageOptions));
        SelectedLanguageIndex = LanguageIndexOf(config.LanguageCulture);
        SelectedStartupScreenIndex = config.StartupScreen == StartupScreen.Editor ? 1 : 0;
        CrashReportOptIn = config.CrashReportOptIn;

        EnableConsoleLogging = config.EnableConsoleLogging;
        EnableConsoleColors = config.EnableConsoleColors;
        EnableFileLogging = config.EnableFileLogging;
        LogFilePath = string.IsNullOrWhiteSpace(config.LogFilePath) ? DefaultLogFilePath : config.LogFilePath;
        CaptureExternalConsoleLogs = config.CaptureExternalConsoleLogs;
        MinimumLogLevelIndex = (int)config.MinimumLogLevel;
        CoalesceRepeatedLogs = config.CoalesceRepeatedLogs;
        LogRepeatCoalesceWindowMs = config.LogRepeatCoalesceWindowMs > 0 ? config.LogRepeatCoalesceWindowMs : 2000;
        LogRepeatSummaryIntervalMs = config.LogRepeatSummaryIntervalMs > 0 ? config.LogRepeatSummaryIntervalMs : 1000;
        RotateLogFiles = config.RotateLogFiles;
        LogFileMaxMb = config.LogFileMaxBytes > 0 ? config.LogFileMaxBytes / 1024.0 / 1024.0 : 10.0;
        LogFileMaxRollovers = config.LogFileMaxRollovers > 0 ? config.LogFileMaxRollovers : 5;

        var enabled = config.EnabledLogCategories;
        LogApplication = Has(enabled, AppLogCategory.Application);
        LogProject = Has(enabled, AppLogCategory.Project);
        LogImport = Has(enabled, AppLogCategory.Import);
        LogEditing = Has(enabled, AppLogCategory.Editing);
        LogInput = Has(enabled, AppLogCategory.Input);
        LogEvents = Has(enabled, AppLogCategory.Events);
        LogHistory = Has(enabled, AppLogCategory.History);
        LogSettings = Has(enabled, AppLogCategory.Settings);
        LogAudio = Has(enabled, AppLogCategory.Audio);
        LogErrors = Has(enabled, AppLogCategory.Errors);
        LogExternal = Has(enabled, AppLogCategory.External);
        Rebaseline();
    }

    public void Save()
    {
        var config = _config.LoadAppConfig();
        config.LanguageCulture = SelectedLanguageCulture();
        config.StartupScreen = SelectedStartupScreenIndex == 1 ? StartupScreen.Editor : StartupScreen.PlayMode;
        config.CrashReportOptIn = CrashReportOptIn;

        config.EnableConsoleLogging = EnableConsoleLogging;
        config.EnableConsoleColors = EnableConsoleColors;
        config.EnableFileLogging = EnableFileLogging;
        config.LogFilePath = string.IsNullOrWhiteSpace(LogFilePath) ? DefaultLogFilePath : LogFilePath;
        config.CaptureExternalConsoleLogs = CaptureExternalConsoleLogs;
        config.MinimumLogLevel = (AppLogLevel)Math.Clamp(MinimumLogLevelIndex, 0, (int)AppLogLevel.Error);
        config.CoalesceRepeatedLogs = CoalesceRepeatedLogs;
        config.LogRepeatCoalesceWindowMs = Math.Max(1, LogRepeatCoalesceWindowMs);
        config.LogRepeatSummaryIntervalMs = Math.Max(250, LogRepeatSummaryIntervalMs);
        config.RotateLogFiles = RotateLogFiles;
        config.LogFileMaxBytes = Math.Max(1, (int)Math.Round(LogFileMaxMb * 1024.0 * 1024.0));
        config.LogFileMaxRollovers = Math.Max(1, LogFileMaxRollovers);
        config.EnabledLogCategories =
            If(LogApplication, AppLogCategory.Application) | If(LogProject, AppLogCategory.Project) |
            If(LogImport, AppLogCategory.Import) | If(LogEditing, AppLogCategory.Editing) |
            If(LogInput, AppLogCategory.Input) | If(LogEvents, AppLogCategory.Events) |
            If(LogHistory, AppLogCategory.History) | If(LogSettings, AppLogCategory.Settings) |
            If(LogAudio, AppLogCategory.Audio) | If(LogErrors, AppLogCategory.Errors) |
            If(LogExternal, AppLogCategory.External);

        _config.SaveAppConfig(config);
        _localization.SetConfiguredCulture(config.LanguageCulture);
        _logger?.ReloadSettings();
        _logger?.Info(AppLogCategory.Settings,
            $"General settings saved. language={config.LanguageCulture}, startup={config.StartupScreen}, " +
            $"console={config.EnableConsoleLogging}, file={config.EnableFileLogging}, level={config.MinimumLogLevel}, " +
            $"categories={config.EnabledLogCategories}.");
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
        HiddenKeys = Rows.Hidden(_localization, query, out var kept);
        OnPropertyChanged(nameof(HiddenKeys));
        if (query.Length > 0 && kept.Skip(FirstAdvancedGroup).Any(k => k)) IsAdvancedOpen = true;
    }

    private int LanguageIndexOf(string? culture)
    {
        var options = _localization.LanguageOptions;
        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].ConfigValue, culture, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private string SelectedLanguageCulture()
    {
        var options = _localization.LanguageOptions;
        return SelectedLanguageIndex >= 0 && SelectedLanguageIndex < options.Count
            ? options[SelectedLanguageIndex].ConfigValue
            : LocalizationService.SystemLanguage;
    }

    private static bool Has(AppLogCategory enabled, AppLogCategory category) =>
        (enabled & category) != AppLogCategory.None;

    private static AppLogCategory If(bool on, AppLogCategory category) => on ? category : AppLogCategory.None;
}
