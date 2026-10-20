using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free General tab in every state the screen can be in.</summary>
public sealed partial class FakeGeneralSettingsScreen : ObservableObject, IGeneralSettingsScreen
{
    [ObservableProperty] private int _selectedLanguageIndex = 1;
    [ObservableProperty] private int _selectedStartupScreenIndex;
    [ObservableProperty] private bool _crashReportOptIn;
    [ObservableProperty] private bool _enableConsoleLogging = true;
    [ObservableProperty] private bool _enableConsoleColors = true;
    [ObservableProperty] private bool _enableFileLogging = true;
    [ObservableProperty] private string _logFilePath = "logs/dotcharter.log";
    [ObservableProperty] private bool _captureExternalConsoleLogs = true;
    [ObservableProperty] private int _minimumLogLevelIndex = 2;
    [ObservableProperty] private bool _coalesceRepeatedLogs = true;
    [ObservableProperty] private int _logRepeatCoalesceWindowMs = 2000;
    [ObservableProperty] private int _logRepeatSummaryIntervalMs = 1000;
    [ObservableProperty] private bool _rotateLogFiles = true;
    [ObservableProperty] private double _logFileMaxMb = 10;
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

    private FakeGeneralSettingsScreen(IReadOnlyList<string> languages) => LanguageOptions = languages;

    public static IReadOnlyList<FakeVariant<IGeneralSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("AdvancedOpen", AdvancedOpen),
        new("LoggingOff", LoggingOff)
    ];

    public static FakeGeneralSettingsScreen Default() => new(["System language", "English", "Deutsch", "日本語"]);

    /// <summary>No language list yet and every log output off.</summary>
    public static FakeGeneralSettingsScreen Empty() => new([])
    {
        SelectedLanguageIndex = -1,
        EnableConsoleLogging = false,
        EnableFileLogging = false,
        LogFilePath = ""
    };

    public static FakeGeneralSettingsScreen LongText() =>
        new(["Systemsprache (automatisch aus den Windows-Einstellungen übernommen)", "Deutsch", "日本語（システムの言語設定に従う）"])
        {
            SelectedLanguageIndex = 0,
            SelectedStartupScreenIndex = 1,
            LogFilePath = @"D:\Benutzerdaten\Protokolle\DotCharter\sehr-langer-ordnername\dotcharter-diagnose.log",
            IsAdvancedOpen = true
        };

    /// <summary>The built-in languages plus community packs, with the log open.</summary>
    public static FakeGeneralSettingsScreen Full() =>
        new(["System language", "English", "Deutsch", "日本語", "Français (community)", "Español (community)",
            "Português do Brasil (community)", "Polski (community)", "Svenska (community)"])
        {
            SelectedLanguageIndex = 4,
            CrashReportOptIn = true,
            IsAdvancedOpen = true
        };

    /// <summary>The Advanced disclosure open, so the logging rows are on screen.</summary>
    public static FakeGeneralSettingsScreen AdvancedOpen()
    {
        var screen = Default();
        screen.IsAdvancedOpen = true;
        return screen;
    }

    /// <summary>Console and file logging off, which disables the rows that depend on them.</summary>
    public static FakeGeneralSettingsScreen LoggingOff()
    {
        var screen = AdvancedOpen();
        screen.EnableConsoleLogging = false;
        screen.EnableFileLogging = false;
        screen.CoalesceRepeatedLogs = false;
        return screen;
    }

    public IReadOnlyList<string> LanguageOptions { get; }

    public ICommand RunSetupAgainCommand { get; } = new RelayCommand(() => { });

    public IReadOnlySet<string> HiddenKeys { get; } = new HashSet<string>();
}
