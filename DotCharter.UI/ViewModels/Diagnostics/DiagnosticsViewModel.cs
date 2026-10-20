using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Rendering.Diagnostics;
using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Diagnostics;

namespace DotCharter.UI.ViewModels.Diagnostics;

/// <summary>
///     Read-only "Session Health" dashboard: surfaces existing runtime
///     diagnostics (audio engine state/device, the two renderers' frame-time
///     rings, GC + startup phase timings, log tail, config paths) and drives the
///     redacted "Export diagnostics bundle" action. It never mutates engine
///     state — every field is a snapshot taken on open / Refresh.
///     <para>
///         Rebuilt from <c>Editor Dialogs.dc.html</c>'s <c>health()</c> surface:
///         grouped cards each carrying a status pill, metrics in a two-column
///         grid, a stacked startup bar, a severity-filtered log and a footer that
///         states the one thing worth looking at. <b>Every value comes from a
///         real channel</b>; where the mockup drew something the app cannot
///         measure (a memory GAUGE with no denominator, an attribution for what
///         caused a stall) the card simply does not draw it.
///     </para>
/// </summary>
public sealed partial class DiagnosticsViewModel : ViewModelBase
{
    private const int LogTailLines = 200;

    /// <summary>How many parsed rows the panel keeps. The tail is already capped
    /// in BYTES; this caps the number of UI elements a refresh materializes.</summary>
    private const int LogPanelRows = 120;

    /// <summary>How much of the END of the log the tail is allowed to read. The
    /// sink rolls at <c>AppConfig.LogFileMaxBytes</c> (10 MB), and reading all of
    /// it to keep 200 lines cost ~30-40 MB of transient LOH per refresh. 512 KB
    /// holds far more than 200 ordinary lines; a window that holds fewer (a tail
    /// of enormous stack traces) simply shows fewer, which is the right
    /// degradation — the cost must not depend on how large the log grew.</summary>
    private const int LogTailMaxBytes = 512 * 1024;

    private readonly IAudioPlaybackService _audio;
    private readonly IConfigurationService _configService;
    private readonly IDiagnosticsBundleExporter _exporter;
    private readonly IShellLauncher? _shell;
    private readonly IFileStorageService _fileStorage;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger _logger;

    /// <summary>Every parsed row of the current tail. <see cref="LogEntries" /> is
    /// this list through <see cref="LogFilterIndex" />, so switching the filter
    /// costs no file read.</summary>
    private IReadOnlyList<DiagnosticsLogEntry> _allLogEntries = [];

    public DiagnosticsViewModel(
        IAudioPlaybackService audio,
        IConfigurationService configService,
        IAppLogger logger,
        IDiagnosticsBundleExporter exporter,
        IFileStorageService fileStorage,
        ILocalizationService localization,
        IShellLauncher? shell = null)
    {
        _shell = shell;
        _audio = audio;
        _configService = configService;
        _logger = logger;
        _exporter = exporter;
        _fileStorage = fileStorage;
        _localization = localization;

        Refresh();
    }

    // ── audio engine ────────────────────────────────────────────────────────
    [ObservableProperty] private string _audioDriver = "";
    [ObservableProperty] private string _audioDevice = "";
    [ObservableProperty] private string _audioSampleRate = "";
    [ObservableProperty] private string _audioBuffer = "";
    [ObservableProperty] private string _audioOutputLatency = "";
    [ObservableProperty] private string _audioDeviceLost = "";
    [ObservableProperty] private HealthCardState _audioCard = HealthCardState.Unknown;

    // ── rendering ───────────────────────────────────────────────────────────
    [ObservableProperty] private string _timeline2DSummary = "";
    [ObservableProperty] private string _preview3DSummary = "";
    [ObservableProperty] private string _timeline2DWorst = "";
    [ObservableProperty] private string _preview3DWorst = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenderNote))]
    private string _renderNote = "";
    [ObservableProperty] private HealthCardState _renderCard = HealthCardState.Unknown;

    public bool HasRenderNote => !string.IsNullOrEmpty(RenderNote);

    // ── runtime & memory ────────────────────────────────────────────────────
    [ObservableProperty] private string _workingSet = "";
    [ObservableProperty] private string _managedHeap = "";
    [ObservableProperty] private string _frameworkVersion = "";
    [ObservableProperty] private string _appVersion = "";
    [ObservableProperty] private string _gcCounts = "";
    [ObservableProperty] private string _processors = "";
    [ObservableProperty] private string _operatingSystem = "";
    [ObservableProperty] private HealthCardState _runtimeCard = HealthCardState.Unknown;

    // ── startup ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _startupDi = "";
    [ObservableProperty] private string _startupServices = "";
    [ObservableProperty] private string _startupViewModels = "";
    [ObservableProperty] private string _startupKeybinds = "";
    [ObservableProperty] private string _startupWindowCtor = "";
    [ObservableProperty] private string _startupPrecompiled = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStartupNote))]
    private string _startupNote = "";
    [ObservableProperty] private HealthCardState _startupCard = HealthCardState.Unknown;
    [ObservableProperty] private bool _hasStartupData;

    /// <summary>The stacked bar's proportions, as a real
    /// <see cref="Avalonia.Controls.ColumnDefinitions" />. Built here rather than
    /// as seven bound widths because a stacked bar IS a set of star weights, and
    /// the alternative (pixel widths against an assumed card width) breaks the
    /// moment the window is resized.</summary>
    [ObservableProperty] private ColumnDefinitions _startupBarColumns = [];

    public bool HasStartupNote => !string.IsNullOrEmpty(StartupNote);

    // ── paths ───────────────────────────────────────────────────────────────
    [ObservableProperty] private string _configPath = "";
    [ObservableProperty] private string _logPath = "";
    [ObservableProperty] private string _activeLogFile = "";

    // ── recent log ──────────────────────────────────────────────────────────
    public ObservableCollection<DiagnosticsLogEntry> LogEntries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogEntries))]
    private int _logFilterIndex;

    public bool HasLogEntries => LogEntries.Count > 0;

    // ── footer ──────────────────────────────────────────────────────────────
    [ObservableProperty] private HealthCardState _headline = HealthCardState.Unknown;
    [ObservableProperty] private string _uptime = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportStatus))]
    private string _exportStatus = string.Empty;

    public bool HasExportStatus => !string.IsNullOrEmpty(ExportStatus);

    /// <summary>The plain-text dump the bundle embeds. Unchanged in SHAPE from
    /// before the card rebuild — the bundle is read by a human in a bug report,
    /// not by the dashboard.</summary>
    internal string SystemInfoForBundle => BuildSystemInfoForBundle();

    [RelayCommand]
    private void Refresh()
    {
        // Resolved ONCE: it enumerates the log directory and stats every rolled
        // sibling, and both consumers below want the same answer.
        var activeLog = LogTailReader.ResolveActiveLogFile(_logger.CurrentLogPath);

        var audioTone = RefreshAudio();
        var renderTone = RefreshRendering();
        var runtimeTone = RefreshRuntime();
        var startupTone = RefreshStartup();
        RefreshPaths(activeLog);
        RefreshLog(activeLog);

        Uptime = _localization.Format("Diagnostics_UptimeFmt",
            FormatDuration(DateTimeOffset.UtcNow - StartupDiagnostics.ProcessStartUtc));

        var finding = SessionHealthRules.Headline(audioTone, renderTone, runtimeTone, startupTone);
        Headline = new HealthCardState(finding.Tone, _localization[finding.MessageKey]);
    }

    // `SetLogFilterCommand` is DELETED (0.58). `DiagnosticsWindow`'s
    // `LogSeveritySwitch` is three RadioButtons bound `IsChecked="{Binding
    // LogFilterIndex, Mode=TwoWay, Converter=IntEqualsConverter,
    // ConverterParameter=0|1|2}"` — same capability, and it reflects state back.
    // It is also the SAFE shape here: a `RelayCommand<int>` fed a literal
    // `CommandParameter` is one of the four XAML faults that builds clean and
    // throws during layout.

    /// <summary>Opens the folder the sink is writing into — the one action on
    /// this surface that is not read-only, and it only reveals a directory.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            _shell?.OpenFolder(WritablePaths.LogRoot);
        }
        catch (Exception ex)
        {
            _logger.Warning(AppLogCategory.Application, $"Could not open the log folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportBundleAsync()
    {
        var path = await _fileStorage.SaveFilePickerAsync(
            _localization["Diagnostics_ExportTitle"],
            $"DotCharter-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}.zip",
            [new FilePickerFilter(_localization["Diagnostics_ZipFilter"], ["*.zip"])]);
        if (path == null) return;

        try
        {
            var systemInfo = BuildSystemInfoForBundle();
            // The bundle reads appconfig.json FROM DISK, and config saves are
            // coalesced — without this the archive can carry the settings as they
            // were up to a quarter second before the user pressed Export, which is
            // exactly the window a "I changed X and it still misbehaves" report
            // lands in.
            _configService.Flush();
            var result = await Task.Run(() => _exporter.Export(path, systemInfo));
            ExportStatus = _localization.Plural("Diagnostics_ExportedOne", "Diagnostics_Exported", result.EntryCount, path);
            _logger.Info(AppLogCategory.Application,
                $"Diagnostics bundle written: {result.EntryCount} entr(y/ies) -> '{path}'.");
        }
        catch (Exception ex)
        {
            ExportStatus = _localization.Format("Diagnostics_ExportFailed", ex.Message);
            _logger.Error(AppLogCategory.Application, $"Diagnostics bundle export to '{path}' failed.", ex);
        }
    }

    partial void OnLogFilterIndexChanged(int value)
    {
        _ = value;
        ApplyLogFilter();
    }

    // ---- section builders --------------------------------------------------

    private HealthTone RefreshAudio()
    {
        var config = _configService.LoadAppConfig();
        var deviceName = string.Equals(config.AudioDriver, "ASIO", StringComparison.OrdinalIgnoreCase)
            ? config.AsioOutputDeviceName
            : config.WasapiOutputDeviceName;
        if (string.IsNullOrWhiteSpace(deviceName))
            deviceName = _localization["Diagnostics_DefaultDevice"];

        // The MEASURED latency is the honest one where a calibration exists; the
        // estimate is what the driver claims. The rule reads whichever the user
        // is actually playing against.
        var latency = _audio.MeasuredOutputLatencyMs > 0
            ? _audio.MeasuredOutputLatencyMs
            : _audio.EstimatedOutputLatencyMs;

        AudioDriver = string.IsNullOrWhiteSpace(config.AudioDriver)
            ? _localization["Diagnostics_NotAvailable"]
            : config.AudioDriver;
        AudioDevice = deviceName;
        AudioSampleRate = _audio.ActiveSampleRate > 0
            ? _localization.Format("Diagnostics_HzFmt", FormatInt(_audio.ActiveSampleRate))
            : _localization["Diagnostics_NotAvailable"];
        AudioBuffer = _audio.ActualBufferFrames > 0
            ? _localization.Format("Diagnostics_FramesFmt", FormatInt(_audio.ActualBufferFrames))
            : _localization["Diagnostics_NotAvailable"];
        AudioOutputLatency = latency > 0
            ? _localization.Format("Diagnostics_MsFmt", latency.ToString("F1", CultureInfo.CurrentCulture))
            : _localization["Diagnostics_NotAvailable"];
        AudioDeviceLost = YesNo(_audio.IsDeviceLost);

        var tone = SessionHealthRules.Audio(_audio.IsDeviceLost, _audio.ActiveSampleRate, latency);
        AudioCard = CardFor(tone);
        return tone;
    }

    private HealthTone RefreshRendering()
    {
        var t2D = RenderDiagnostics.Timeline2D;
        var p3D = RenderDiagnostics.Preview3D;

        Timeline2DSummary = FormatFrameSummary(t2D);
        Preview3DSummary = FormatFrameSummary(p3D);
        Timeline2DWorst = FormatWorst(t2D);
        Preview3DWorst = FormatWorst(p3D);

        var worst = Math.Max(t2D.Count > 0 ? t2D.MaxMs : 0, p3D.Count > 0 ? p3D.MaxMs : 0);
        RenderNote = worst > SessionHealthRules.WatchWorstFrameMs
            ? _localization.Format("Diagnostics_RenderStallNote", worst.ToString("F0", CultureInfo.CurrentCulture))
            : "";

        var tone = SessionHealthRules.Rendering(t2D.Count, t2D.MaxMs, p3D.Count, p3D.MaxMs);
        RenderCard = new HealthCardState(tone, t2D.Count == 0 && p3D.Count == 0
            ? _localization["Diagnostics_Status_Idle"]
            : _localization[SessionHealthRules.StatusKey(tone)]);
        return tone;
    }

    private HealthTone RefreshRuntime()
    {
        long workingSet;
        try
        {
            workingSet = Environment.WorkingSet;
        }
        catch
        {
            workingSet = 0;
        }

        WorkingSet = SessionHealthRules.FormatBytes(workingSet);
        ManagedHeap = SessionHealthRules.FormatBytes(GC.GetTotalMemory(false));
        FrameworkVersion = RuntimeInformation.FrameworkDescription;
        OperatingSystem = RuntimeInformation.OSDescription;
        AppVersion = AppVersionString();
        GcCounts = $"{GC.CollectionCount(0)} / {GC.CollectionCount(1)} / {GC.CollectionCount(2)}";
        Processors = Environment.ProcessorCount.ToString(CultureInfo.CurrentCulture);

        var tone = SessionHealthRules.Runtime(workingSet);
        RuntimeCard = CardFor(tone);
        return tone;
    }

    private HealthTone RefreshStartup()
    {
        var phases = StartupDiagnostics.Phases;
        HasStartupData = phases.HasValue;

        var na = _localization["Diagnostics_NoData"];
        if (phases is not { } p)
        {
            StartupDi = StartupServices = StartupViewModels = na;
            StartupKeybinds = StartupWindowCtor = StartupPrecompiled = na;
            StartupBarColumns = [];
            StartupNote = "";
            var noData = SessionHealthRules.Startup(StartupPhasesView.From(null));
            StartupCard = new HealthCardState(noData, na);
            return noData;
        }

        StartupDi = Ms(p.DiMs);
        StartupServices = Ms(Math.Max(0, p.ServicesMs - p.DiMs));
        StartupViewModels = Ms(p.ViewModelMs);
        StartupKeybinds = Ms(p.KeybindsMs);
        StartupWindowCtor = Ms(p.WindowCtorMs);
        StartupPrecompiled = YesNo(p.IsPrecompiled);

        StartupBarColumns = [.. p.Segments.Select(ms => new ColumnDefinition(Math.Max(0, ms), GridUnitType.Star))];

        // The r2r caveat is printed WITH the numbers, never beside them: a phase
        // from a JIT-compiled run measures roughly twice the shipped build, and a
        // number read without that is diagnosed as a product regression.
        StartupNote = p.IsPrecompiled ? "" : _localization["Diagnostics_StartupNotPrecompiled"];

        var tone = SessionHealthRules.Startup(StartupPhasesView.From(p));
        StartupCard = new HealthCardState(tone,
            _localization.Format("Diagnostics_StartupInteractiveFmt",
                (p.InteractiveMs / 1000.0).ToString("F1", CultureInfo.CurrentCulture)));
        return tone;
    }

    private void RefreshPaths(string? activeLogPath)
    {
        ConfigPath = WritablePaths.ConfigRoot;
        LogPath = WritablePaths.LogRoot;
        ActiveLogFile = activeLogPath is null
            ? _localization["Diagnostics_NotAvailable"]
            : Path.GetFileName(activeLogPath);
    }

    private void RefreshLog(string? activeLogPath)
    {
        _allLogEntries = DiagnosticsLogParser.Parse(ReadLogTail(activeLogPath), LogPanelRows);
        ApplyLogFilter();
    }

    private void ApplyLogFilter()
    {
        var filter = (LogSeverityFilter)Math.Clamp(LogFilterIndex, 0, 2);
        LogEntries.Clear();
        foreach (var entry in _allLogEntries)
        {
            if (entry.MatchesFilter(filter))
                LogEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasLogEntries));
    }

    private HealthCardState CardFor(HealthTone tone) =>
        new(tone, _localization[SessionHealthRules.StatusKey(tone)]);

    private string FormatFrameSummary(FrameTimeSummary s)
    {
        if (s.Count == 0) return _localization["Diagnostics_NoData"];
        return _localization.Format("Diagnostics_FrameLineFormat",
            s.ApproxFps.ToString("F0", CultureInfo.CurrentCulture),
            s.P50Ms.ToString("F1", CultureInfo.CurrentCulture));
    }

    private string FormatWorst(FrameTimeSummary s) => s.Count == 0
        ? _localization["Diagnostics_NoData"]
        : _localization.Format("Diagnostics_MsFmt", s.MaxMs.ToString("F1", CultureInfo.CurrentCulture));

    /// <summary>The last <see cref="LogTailLines" /> lines, read through the
    /// shared <see cref="LogTailReader" /> so the cost is bounded
    /// by <see cref="LogTailMaxBytes" /> and not by the log's size. Calling
    /// <c>ReadToEnd()</c> on a file the sink lets grow to 10 MB and then
    /// splitting it would cost ~30-40 MB of transient LOH per refresh.</summary>
    private static string ReadLogTail(string? activeLogPath)
    {
        try
        {
            if (string.IsNullOrEmpty(activeLogPath)) return "";
            return LogTailReader.ReadTailLines(activeLogPath, LogTailLines, LogTailMaxBytes);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>The text embedded as <c>system-info.txt</c> in the exported
    /// bundle — the same facts the dashboard shows, minus the log tail (the logs
    /// travel as their own entries). Redacted again by the exporter as a safety
    /// net.</summary>
    private string BuildSystemInfoForBundle()
    {
        var phases = StartupDiagnostics.PhaseSummary ?? _localization["Diagnostics_NoData"];
        return string.Join("\n\n",
            $"[{_localization["Diagnostics_Section_Runtime"]}]\n" +
            $"{FrameworkVersion}\n{OperatingSystem}\nDotCharter {AppVersion}\n" +
            $"processors {Processors}\nworking set {WorkingSet}\nmanaged heap {ManagedHeap}\n" +
            $"server GC {YesNo(GCSettings.IsServerGC)}\nGC latency {GCSettings.LatencyMode}\n" +
            $"GC gen0/1/2 {GcCounts}",

            $"[{_localization["Diagnostics_Section_Startup"]}]\n{phases}\nuptime {Uptime}",

            $"[{_localization["Diagnostics_Section_Audio"]}]\n" +
            $"driver {AudioDriver}\ndevice {AudioDevice}\nsample rate {AudioSampleRate}\n" +
            $"buffer {AudioBuffer}\noutput latency {AudioOutputLatency}\n" +
            $"device lost {AudioDeviceLost}\ninput rate {FormatInt(_audio.InputSampleRate)}",

            $"[{_localization["Diagnostics_Section_Render"]}]\n" +
            $"2D timeline {Timeline2DSummary}, worst {Timeline2DWorst}\n" +
            $"3D preview {Preview3DSummary}, worst {Preview3DWorst}",

            $"[{_localization["Diagnostics_Section_Paths"]}]\n" +
            $"config {ConfigPath}\nlogs {LogPath}\nactive log {ActiveLogFile}");
    }

    // ---- formatting helpers ------------------------------------------------

    private string Ms(long value) =>
        _localization.Format("Diagnostics_MsFmt", value.ToString(CultureInfo.CurrentCulture));

    private string FormatInt(int value) =>
        value > 0 ? value.ToString(CultureInfo.CurrentCulture) : _localization["Diagnostics_NotAvailable"];

    private string YesNo(bool value) => _localization[value ? "Diagnostics_Yes" : "Diagnostics_No"];

    private static string AppVersionString() =>
        DotCharter.Services.Infrastructure.AppVersionInfo.Current;

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes}m {d.Seconds}s";
        if (d.TotalMinutes >= 1) return $"{d.Minutes}m {d.Seconds}s";
        return $"{d.Seconds}s";
    }
}

/// <summary>
///     One card's state: its tone plus the words in its pill. The three booleans
///     exist so the markup can drive style CLASSES
///     (<c>Classes.Warn="{Binding AudioCard.IsWatch}"</c>) — a class is how the
///     Fire layer expresses a status colour, and a converter per card would put
///     the same three-way decision in two places.
/// </summary>
public sealed record HealthCardState(HealthTone Tone, string Status)
{
    /// <summary>Before the first <c>Refresh()</c> — never rendered, but a
    /// non-null default keeps every binding from reporting a null card.</summary>
    public static readonly HealthCardState Unknown = new(HealthTone.Good, "");

    public bool IsGood => Tone == HealthTone.Good;
    public bool IsWatch => Tone == HealthTone.Watch;
    public bool IsBad => Tone == HealthTone.Bad;
}
