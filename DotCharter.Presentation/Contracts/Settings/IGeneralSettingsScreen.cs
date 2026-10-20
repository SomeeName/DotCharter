using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Settings;

/// <summary>
///     Settings ▸ General: the display language and the screen the app opens
///     on, the first-run wizard, the local crash report, and — behind the
///     Advanced disclosure — the log's outputs, detail, rotation and
///     categories. Every value is saved by the footer; the language takes
///     effect when it is.
///     <para>
///         Search hides rows through <see cref="HiddenKeys" />, one set of row
///         and group keys, the way the Graphics tab does.
///     </para>
/// </summary>
public interface IGeneralSettingsScreen : INotifyPropertyChanged
{
    // ── language and startup ─────────────────────────────────────────────

    /// <summary>The languages the app ships plus any community pack, in their own names.</summary>
    IReadOnlyList<string> LanguageOptions { get; }

    int SelectedLanguageIndex { get; set; }

    /// <summary>0 opens Play Mode's front door, 1 the chart editor.</summary>
    int SelectedStartupScreenIndex { get; set; }

    /// <summary>Opens the first-run wizard on its first page, over Settings.</summary>
    ICommand RunSetupAgainCommand { get; }

    // ── diagnostics ──────────────────────────────────────────────────────

    bool CrashReportOptIn { get; set; }

    // ── advanced: logging ────────────────────────────────────────────────

    bool EnableConsoleLogging { get; set; }

    bool EnableConsoleColors { get; set; }

    bool EnableFileLogging { get; set; }

    string LogFilePath { get; set; }

    bool CaptureExternalConsoleLogs { get; set; }

    /// <summary>The floor as a picker row, in severity order: Trace, Debug, Info, Warning, Error.</summary>
    int MinimumLogLevelIndex { get; set; }

    bool CoalesceRepeatedLogs { get; set; }

    int LogRepeatCoalesceWindowMs { get; set; }

    int LogRepeatSummaryIntervalMs { get; set; }

    bool RotateLogFiles { get; set; }

    double LogFileMaxMb { get; set; }

    int LogFileMaxRollovers { get; set; }

    bool LogApplication { get; set; }

    bool LogProject { get; set; }

    bool LogImport { get; set; }

    bool LogEditing { get; set; }

    bool LogInput { get; set; }

    bool LogEvents { get; set; }

    bool LogHistory { get; set; }

    bool LogSettings { get; set; }

    bool LogAudio { get; set; }

    bool LogErrors { get; set; }

    bool LogExternal { get; set; }

    // ── search ───────────────────────────────────────────────────────────

    /// <summary>The Advanced disclosure; forced open while a query keeps a row inside it.</summary>
    bool IsAdvancedOpen { get; set; }

    /// <summary>Row and group keys the current search filters OUT. Empty when there is no query.</summary>
    IReadOnlySet<string> HiddenKeys { get; }
}
