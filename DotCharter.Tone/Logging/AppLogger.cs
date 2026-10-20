using System.Diagnostics;
using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace DotCharter.Tone.Logging;

public sealed class AppLogger : IAppLogger, IDisposable
{
    private const int FlushTimerPeriodMs = 1000;
    private readonly IRigConfigService _configurationService;

    // Track multiple pending logs by a hash of their signature
    private readonly Dictionary<int, PendingLogRun> _pendingRepeats = [];
    private readonly Timer _repeatFlushTimer;
    private readonly object _sync = new();

    // ReSharper disable once InconsistentlySynchronizedField -- intentional: hot path reads a snapshot without locking.
    private RigConfig _config;
    private bool _disposed;

    // Serilog root logger instance - Fully qualified to avoid collision
    private Logger? _serilogLogger;

    public AppLogger(IRigConfigService configurationService)
    {
        _configurationService = configurationService;
        _config = _configurationService.LoadRigConfig();
        CurrentLogPath = ResolveLogPath(_config.LogFilePath);

        // No previous logger exists on construction, so nothing to retire.
        lock (_sync) BuildSerilogPipelineLocked(_config);

        _repeatFlushTimer = new Timer(_ => FlushRepeatSummaryIfDue(), null, FlushTimerPeriodMs, FlushTimerPeriodMs);
    }

    public string CurrentLogPath { get; private set; }

    public event Action<LogEmittedArgs>? LogEmitted;

    private void RaiseLogEmitted(LogEvent ev)
    {
        var handler = LogEmitted;
        if (handler == null) return;
        try
        {
            handler(new LogEmittedArgs(
                ev.Timestamp,
                ev.Level,
                ev.Category,
                ev.Message,
                ev.ExceptionText));
        }
        catch
        {
            // Never let a subscriber crash the logger pipeline.
        }
    }

    public bool IsEnabled(AppLogCategory category) => CategoryEnabled(category);

    public void Flush()
    {
        IDisposable? retired;
        lock (_sync)
        {
            FlushAllPendingRepeatsLocked();

            // Force Serilog to flush its async background buffers to disk by
            // rebuilding the pipeline: BuildSerilogPipelineLocked swaps in a new
            // logger and disposes the previous one, which drains its buffers.
            // The SWAP must hold _sync — it is a read-modify-write of
            // _serilogLogger, which WriteEventLocked writes to. The Dispose that
            // actually drains the buffers happens below, outside the lock.
            retired = BuildSerilogPipelineLocked(_config);
        }

        // Blocking drain, deliberately outside _sync.
        retired?.Dispose();
    }

    /// <summary>
    ///     Everything about <see cref="RigConfig" /> that <see cref="BuildSerilogPipelineLocked" />
    ///     reads. Two configs with the same signature produce a byte-identical
    ///     pipeline, so rebuilding between them is pure cost.
    ///     <para>
    ///         <b>Add a field here whenever the pipeline builder starts reading
    ///         one</b>, or a settings change stops taking effect until restart.
    ///         <c>AppLoggerTests.ThePipelineSignatureNamesEveryConfigMemberTheBuilderReads</c>
    ///         is a source lint over <see cref="BuildSerilogPipelineLocked" />
    ///         that fails when it does not — this is a hand-maintained parallel
    ///         list, and it would otherwise rot silently.
    ///     </para>
    ///     Level and category filtering are deliberately absent: they are read
    ///     from <c>_config</c> per write (<c>CategoryEnabled</c>), never baked
    ///     into the pipeline, and <see cref="ReloadSettings" /> refreshes
    ///     <c>_config</c> unconditionally.
    /// </summary>
    internal readonly record struct LogPipelineSignature(
        bool EnableConsoleLogging,
        bool EnableConsoleColors,
        bool EnableFileLogging,
        bool RotateLogFiles,
        int LogFileMaxBytes,
        int LogFileMaxRollovers,
        string ResolvedLogPath);

    internal static LogPipelineSignature SignatureOf(RigConfig config, string resolvedLogPath) =>
        new(config.EnableConsoleLogging,
            config.EnableConsoleColors,
            config.EnableFileLogging,
            config.RotateLogFiles,
            config.LogFileMaxBytes,
            config.LogFileMaxRollovers,
            resolvedLogPath);

    /// <summary>The signature the live pipeline was built from. Guarded by
    /// <see cref="_sync" />.</summary>
    private LogPipelineSignature _pipelineSignature;

    /// <summary>
    ///     Re-reads the config and rebuilds the Serilog pipeline <b>only when a
    ///     logging setting actually moved</b>.
    ///     <para>
    ///         Every caller reaches here after ANY app-config write, not just a
    ///         logging one — and <c>ProjectSessionService.UpdateAppConfig</c> is
    ///         called three times on a single File ▸ Open (<c>LastRscpDir</c>,
    ///         <c>LastProjectPath</c>, the MRU list), on the UI thread, with the
    ///         load's continuation resumed there. Each unconditional rebuild
    ///         created a fresh async file sink (<c>Directory.CreateDirectory</c>
    ///         plus opening the log file) and then BLOCKED disposing the retired
    ///         one until its background pump drained — see the note in
    ///         <see cref="BuildSerilogPipelineLocked" />, which is also why this
    ///         cannot simply be made cheap. Three teardown/rebuild cycles per
    ///         open, for three strings that have nothing to do with logging.
    ///     </para>
    ///     <para>
    ///         The refresh half is NOT conditional: <c>_config</c> and
    ///         <see cref="CurrentLogPath" /> are re-read every call, so level and
    ///         category changes take effect exactly as before. Only the pipeline
    ///         swap is skipped, and only when it would have produced an identical
    ///         pipeline.
    ///     </para>
    /// </summary>
    public void ReloadSettings()
    {
        IDisposable? retired;
        lock (_sync)
        {
            FlushAllPendingRepeatsLocked();

            _config = _configurationService.LoadRigConfig();
            CurrentLogPath = ResolveLogPath(_config.LogFilePath);

            var signature = SignatureOf(_config, CurrentLogPath);
            if (signature == _pipelineSignature) return;

            // ONE rebuild, with the new config: it applies the new settings AND
            // drains the previous pipeline. (A Flush() first would
            // rebuild the whole pipeline a second time with the OLD config for
            // nothing.)
            retired = BuildSerilogPipelineLocked(_config);
        }

        retired?.Dispose();
    }

    /// <summary>Test seam: how many Serilog pipelines this logger has built.
    /// The deferral guard counts it; nothing in the product reads it.</summary>
    internal int PipelineBuildCount => Volatile.Read(ref _pipelineBuilds);

    private int _pipelineBuilds;

    private void FlushAllPendingRepeatsLocked()
    {
        var keys = _pendingRepeats.Keys.ToList();
        foreach (var key in keys) FlushPendingRepeatLocked(key, DateTimeOffset.Now, true);
    }

    public void Trace(AppLogCategory category, string message) => Write("TRACE", category, message, null);

    public void Debug(AppLogCategory category, string message) => Write("DEBUG", category, message, null);

    public void Info(AppLogCategory category, string message) => Write("INFO", category, message, null);

    public void Warning(AppLogCategory category, string message) => Write("WARN", category, message, null);

    public void Warning(AppLogCategory category, string message, Exception? exception) => Write("WARN", category, message, exception);

    public void Error(AppLogCategory category, string message, Exception? exception = null) => Write("ERROR", category | AppLogCategory.Errors, message, exception);

    public IDisposable BeginTimedOperation(AppLogCategory category, string operationName, string? details = null) => new TimedOperationScope(this, category, operationName, details);

    public void LogUnhandledException(string source, Exception exception)
    {
        Write("ERROR", AppLogCategory.Errors | AppLogCategory.Application,
            $"Unhandled exception from {source}: {exception.GetType().Name}: {exception.Message}",
            exception);
        try
        {
            Flush();
        }
        catch
        {
            // Flushing must never throw from a top-level crash handler.
        }
    }

    public void External(string source, string message)
    {
        RigConfig config;
        lock (_sync)
        {
            config = _config;
        }

        if (!config.CaptureExternalConsoleLogs) return;

        var (level, normalizedMessage) = NormalizeExternalMessage(message);
        var category = level == "ERROR" ? AppLogCategory.External | AppLogCategory.Errors : AppLogCategory.External;

        Write(level, category, $"[{source}] {normalizedMessage}", null);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            var keys = _pendingRepeats.Keys.ToList();
            foreach (var key in keys) FlushPendingRepeatLocked(key, DateTimeOffset.Now, true);
            _disposed = true;
        }

        _repeatFlushTimer.Dispose();
        _serilogLogger?.Dispose();
    }

    internal static void SetConsoleWriters(TextWriter output, TextWriter error)
    {
        // This method is called by ConsoleLogRouter to store the original streams
    }

    /// <summary>Caller MUST hold <see cref="_sync" />: this swaps and disposes
    /// <see cref="_serilogLogger" />, which <see cref="WriteEventLocked" /> writes
    /// to from every other thread.</summary>
    /// <returns>
    ///     The RETIRED logger, for the caller to dispose once it has released
    ///     <c>_sync</c>. Never dispose it inside the lock — see the note at the
    ///     swap below.
    /// </returns>
    private IDisposable? BuildSerilogPipelineLocked(RigConfig config)
    {
        // Build the new pipeline first; only dispose the previous one after we
        // successfully have a replacement, so a failure here (e.g. file
        // permissions on the new log path) does not leave us without a logger.
        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Verbose();

        const string outputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u4}] [{Category}] [T{ThreadId}] {Message:lj}{NewLine}{Exception}";

        if (config.EnableConsoleLogging)
        {
            loggerConfig.WriteTo.Async(a => a.Console(
                outputTemplate: outputTemplate,
                // Using AnsiConsoleTheme embeds raw escape codes that bypass Windows restrictions
                theme: config.EnableConsoleColors ? AnsiConsoleTheme.Code : ConsoleTheme.None,
                applyThemeToRedirectedOutput: true
            ));
        }

        if (config.EnableFileLogging)
        {
            var directory = Path.GetDirectoryName(CurrentLogPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            loggerConfig.WriteTo.Async(a => a.File(
                CurrentLogPath,
                outputTemplate: outputTemplate,
                fileSizeLimitBytes: config.RotateLogFiles && config.LogFileMaxBytes > 0 ? config.LogFileMaxBytes : null,
                retainedFileCountLimit: config.RotateLogFiles && config.LogFileMaxRollovers > 0
                    ? config.LogFileMaxRollovers
                    : null,
                rollOnFileSizeLimit: config.RotateLogFiles,
                buffered: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1)
            ));
        }

        // The swap is the part that needs the lock. Disposing the OLD logger is
        // NOT: it is already unreachable, and Serilog.Sinks.Async's Dispose
        // BLOCKS until its background pump drains. Doing that while holding
        // _sync makes the whole logger unusable for the duration — every Write
        // on every thread queues behind it — and it deadlocks outright if the
        // draining pump needs _sync to finish.
        //
        // That is not theoretical: the headless perf suite constructs a fresh
        // app per test, so this ran ~27 times in one run with 54 async pump
        // threads alive, and it wedged mid-run under lock(_sync) with two stack
        // samples a minute apart identical. Same rule as the "never hold a lock
        // across an await" contract in the services guide — a blocking Wait is
        // the synchronous form of the same mistake.
        //
        // So the caller disposes, after it has left the lock.
        var previousLogger = _serilogLogger;
        _serilogLogger = loggerConfig.CreateLogger();
        // Recorded here rather than at the call sites, so Flush()'s deliberate
        // rebuild also re-bases it — otherwise the next ReloadSettings would
        // compare against a signature the live pipeline does not match.
        _pipelineSignature = SignatureOf(config, CurrentLogPath);
        _pipelineBuilds++;
        return previousLogger;
    }

    private void Write(string level, AppLogCategory category, string message, Exception? exception)
    {
        // Fast unsynchronized pre-filter. Audio-thread Trace/Info calls would otherwise
        // allocate a LogEvent and contend on _sync (heavily contended with the
        // UI thread at >170 fps) even when the category was disabled —
        // measurably stealing milliseconds from the audio buffer deadline and
        // causing intermittent crackle. Reading the volatile _config snapshot
        // and asking CategoryEnabled BEFORE we allocate keeps the disabled
        // path fully GC-free and lock-free.
        // ReSharper disable once InconsistentlySynchronizedField -- documented hot-path snapshot read; see comment above.
        var snapshot = _config;
        if (!CategoryEnabled(category, snapshot)) return;
        if (!LevelEnabled(level, category, snapshot)) return;
        if (!snapshot.EnableConsoleLogging && !snapshot.EnableFileLogging) return;

        var logEvent = new LogEvent(
            DateTimeOffset.Now,
            level,
            category,
            Environment.CurrentManagedThreadId,
            (message).Trim(),
            exception?.ToString());

        lock (_sync)
        {
            if (_disposed) return;

            var config = _config;

            if (!CategoryEnabled(category, config)) return;
            if (!config.EnableConsoleLogging && !config.EnableFileLogging) return;

            if (config.CoalesceRepeatedLogs)
            {
                if (TryCoalesceRepeatLocked(logEvent, config))
                    return;
            }

            WriteEventLocked(logEvent);

            if (config.CoalesceRepeatedLogs)
            {
                var key = GetEventKey(logEvent);
                _pendingRepeats[key] = new PendingLogRun(logEvent);
            }
        }

        RaiseLogEmitted(logEvent);
    }

    private bool TryCoalesceRepeatLocked(LogEvent logEvent, RigConfig config)
    {
        var key = GetEventKey(logEvent);

        if (_pendingRepeats.TryGetValue(key, out var run))
        {
            var coalesceWindowMs = Math.Max(1, config.LogRepeatCoalesceWindowMs);
            var withinWindow = (logEvent.Timestamp - run.LastTimestamp).TotalMilliseconds <= coalesceWindowMs;

            if (withinWindow)
            {
                run.AddOccurrence(logEvent.Timestamp);
                return true;
            }

            FlushPendingRepeatLocked(key, logEvent.Timestamp, true);
        }

        return false;
    }

    private void FlushRepeatSummaryIfDue()
    {
        lock (_sync)
        {
            if (_disposed || _pendingRepeats.Count == 0) return;

            var now = DateTimeOffset.Now;
            var config = _config;
            var summaryIntervalMs = Math.Max(250, config.LogRepeatSummaryIntervalMs);
            var coalesceWindowMs = Math.Max(1, config.LogRepeatCoalesceWindowMs);

            var keysToFlush = new List<int>();

            foreach (var kvp in _pendingRepeats)
            {
                var run = kvp.Value;
                var elapsedSinceFirst = (now - run.FirstTimestamp).TotalMilliseconds;
                var quietFor = (now - run.LastTimestamp).TotalMilliseconds;

                if (elapsedSinceFirst >= summaryIntervalMs || quietFor >= coalesceWindowMs) keysToFlush.Add(kvp.Key);
            }

            foreach (var key in keysToFlush) FlushPendingRepeatLocked(key, now, true);
        }
    }

    private void FlushPendingRepeatLocked(int key, DateTimeOffset timestamp, bool clearPending)
    {
        if (_pendingRepeats.TryGetValue(key, out var run))
        {
            if (run.Count > 1) WriteRepeatSummaryLocked(run, timestamp);

            if (clearPending) _pendingRepeats.Remove(key);
        }
    }

    private void WriteRepeatSummaryLocked(PendingLogRun run, DateTimeOffset timestamp)
    {
        var summaryMessage = FormatRepeatSummary(
            run.Count, run.Event.Message, run.FirstTimestamp, run.LastTimestamp);

        var summaryEvent = new LogEvent(
            timestamp,
            run.Event.Level,
            run.Event.Category,
            run.Event.ThreadId,
            summaryMessage,
            run.Event.ExceptionText == null ? null : "Repeated exception details matched the first occurrence.");

        WriteEventLocked(summaryEvent);
    }

    private void WriteEventLocked(LogEvent logEvent)
    {
        if (_serilogLogger == null) return;

        var serilogLevel = logEvent.Level switch
        {
            "TRACE" => LogEventLevel.Verbose,
            "DEBUG" => LogEventLevel.Debug,
            "INFO" => LogEventLevel.Information,
            "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            _ => LogEventLevel.Information
        };

        var messageToLog = logEvent.Message;
        if (!string.IsNullOrWhiteSpace(logEvent.ExceptionText))
            messageToLog += $"{Environment.NewLine}{logEvent.ExceptionText}";

        _serilogLogger.ForContext("Category", logEvent.Category.ToString())
            .ForContext("ThreadId", logEvent.ThreadId)
            .Write(serilogLevel, "{Message}", messageToLog);
    }

    private bool CategoryEnabled(AppLogCategory category)
    {
        RigConfig config;
        lock (_sync)
        {
            config = _config;
        }

        return CategoryEnabled(category, config);
    }

    private static bool CategoryEnabled(AppLogCategory category, RigConfig config)
    {
        if (category == AppLogCategory.None) return false;
        return (config.EnabledLogCategories & category) != AppLogCategory.None;
    }

    /// <summary>
    ///     The severity half of the gate — see
    ///     <see cref="RigConfig.MinimumLogLevel" /> for why it exists separately
    ///     from the category flag.
    ///     <para>
    ///         Stays on the same allocation-free, lock-free pre-filter path as the
    ///         category check: it reads the volatile config snapshot and does at
    ///         most one dictionary probe, so an audio-thread Trace that is going to
    ///         be dropped still costs nothing.
    ///     </para>
    ///     <para>
    ///         An override wins in BOTH directions — it can lower a category's
    ///         floor below the global one to debug an area, or raise it above to
    ///         silence one that is in the way. A one-way "override may only be
    ///         stricter" rule would make the first of those impossible, which is
    ///         the use this was built for.
    ///     </para>
    ///     <para>
    ///         A category naming several flags matches no override key, and falls
    ///         through to the global floor. Callers log at a single flag, so this
    ///         is the granularity that can actually be configured; a multi-flag
    ///         call (e.g. <c>Project | Performance</c>) is deliberately governed by
    ///         the global setting rather than by an arbitrary pick between two
    ///         overrides.
    ///     </para>
    /// </summary>
    private static bool LevelEnabled(string level, AppLogCategory category, RigConfig config)
    {
        var floor = config.MinimumLogLevel;

        if (config.LogLevelOverrides.Count > 0 &&
            config.LogLevelOverrides.TryGetValue(category.ToString(), out var perCategory))
        {
            floor = perCategory;
        }

        return ToLevel(level) >= floor;
    }

    private static AppLogLevel ToLevel(string level) => level switch
    {
        "TRACE" => AppLogLevel.Trace,
        "DEBUG" => AppLogLevel.Debug,
        "INFO" => AppLogLevel.Info,
        "WARN" => AppLogLevel.Warning,
        "ERROR" => AppLogLevel.Error,
        // An unknown level is treated as Error rather than dropped: the only way
        // to reach it is a new severity nobody taught this method about, and
        // silently swallowing those is how a whole class of message disappears.
        _ => AppLogLevel.Error
    };

    private static string ResolveLogPath(string configuredPath) =>
        // Route through WritablePaths so the log file lands in a directory
        // the user actually has write access to (falling back to
        // %APPDATA%\DotCharter\logs\ when the app is installed under Program
        // Files or another non-writable location).
        WritablePaths.ResolveLogFile(configuredPath);

    private static (string Level, string Message) NormalizeExternalMessage(string message)
    {
        var normalized = message.Trim();
        if (normalized.Length == 0) return ("INFO", string.Empty);

        var level = "INFO";
        var original = normalized;

        if (TryConsumeBracket(ref normalized, out var firstBracket))
        {
            var parsedFirstLevel = NormalizeLevel(firstBracket);
            if (parsedFirstLevel != null)
            {
                level = parsedFirstLevel;
            }
            else if (LooksLikeTime(firstBracket) && TryConsumeBracket(ref normalized, out var bracketLevel))
            {
                var parsedLevel = NormalizeLevel(bracketLevel);
                if (parsedLevel != null)
                    level = parsedLevel;
                else
                    normalized = original;
            }
            else
            {
                normalized = original;
            }
        }

        return (level, normalized.Trim());
    }

    private static bool TryConsumeBracket(ref string text, out string value)
    {
        value = string.Empty;
        text = text.TrimStart();
        if (!text.StartsWith("[", StringComparison.Ordinal)) return false;

        var closeIndex = text.IndexOf(']');
        if (closeIndex <= 0) return false;

        value = text.Substring(1, closeIndex - 1);
        text = text[(closeIndex + 1)..];
        return true;
    }

    private static string? NormalizeLevel(string level)
    {
        return level.Trim().ToUpperInvariant() switch
        {
            "TRACE" => "TRACE",
            "DEBUG" => "TRACE",
            "INFO" => "INFO",
            "WARN" => "WARN",
            "WARNING" => "WARN",
            "ERROR" => "ERROR",
            "ERR" => "ERROR",
            _ => null
        };
    }

    private static bool LooksLikeTime(string value)
    {
        var text = value.Trim();
        return text.Length >= 8 &&
               char.IsDigit(text[0]) &&
               char.IsDigit(text[1]) &&
               text[2] == ':' &&
               char.IsDigit(text[3]) &&
               char.IsDigit(text[4]) &&
               text[5] == ':' &&
               char.IsDigit(text[6]) &&
               char.IsDigit(text[7]);
    }

    /// <summary>
    ///     A duration for a log line. <b>InvariantCulture, deliberately</b> — this
    ///     was an interpolated string with no format provider, so on the owner's
    ///     German machine every coalesced-repeat summary read <c>2,8ms</c> while
    ///     the same build on an English one read <c>2.8ms</c>. A log file is read
    ///     by whoever is diagnosing, not by whoever produced it, and a number that
    ///     changes shape with the player's locale is one more thing to second-guess
    ///     in a file people already misread (see <see cref="FormatRepeatSummary" />).
    /// </summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds >= 1)
            return duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
        return duration.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture) + "ms";
    }

    /// <summary>
    ///     The text of a coalesced-repeat summary.
    ///     <para>
    ///         <b>A summary line's own timestamp is the FLUSH time, not the time
    ///         anything happened</b>, and it is written up to
    ///         <c>LogRepeatSummaryIntervalMs</c> + the 1 s flush tick after the
    ///         last occurrence. That is a deliberate ordering choice — a line may
    ///         not be stamped earlier than the lines already above it — but it made
    ///         the file lie by omission, and it cost this project a whole
    ///         misdiagnosis: v0.44's tone-restore failures all occurred at
    ///         12:40:48.44x, their summaries were flushed together at
    ///         12:40:50.124, and a reader took the second cluster for a SECOND,
    ///         separate premature apply ~1.7 s later. Three unrelated module names
    ///         sharing one millisecond, with internal spans of 0 ms and 2.8 ms, is
    ///         only possible for a batch of flush summaries — but nothing in the
    ///         line said so.
    ///     </para>
    ///     <para>
    ///         So the WINDOW the occurrences actually fell in is now printed with
    ///         them: <c>first at HH:mm:ss.fff</c> plus the span. Keep both — the
    ///         span alone cannot be anchored to anything, and the anchor alone
    ///         hides how far the run stretched.
    ///     </para>
    ///     Pure and internal so the format is pinned by a test rather than by a
    ///     reader eyeballing a log file.
    /// </summary>
    internal static string FormatRepeatSummary(
        int count, string message, DateTimeOffset firstOccurrence, DateTimeOffset lastOccurrence)
    {
        var duplicateCount = count - 1;
        var duration = FormatDuration(lastOccurrence - firstOccurrence);
        var first = firstOccurrence.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return $"[x{count}] {message} ({duplicateCount} duplicate log entr{(duplicateCount == 1 ? "y" : "ies")} " +
               $"suppressed over {duration}, first at {first}).";
    }

    private static int GetEventKey(LogEvent logEvent) => HashCode.Combine(logEvent.Level, logEvent.Category, logEvent.Message, logEvent.ExceptionText);

    private sealed class PendingLogRun(LogEvent logEvent)
    {
        public LogEvent Event { get; } = logEvent;
        public DateTimeOffset FirstTimestamp { get; } = logEvent.Timestamp;
        public DateTimeOffset LastTimestamp { get; private set; } = logEvent.Timestamp;
        public int Count { get; private set; } = 1;

        public void AddOccurrence(DateTimeOffset timestamp)
        {
            Count++;
            LastTimestamp = timestamp;
        }
    }

    private readonly record struct LogEvent(
        DateTimeOffset Timestamp,
        string Level,
        AppLogCategory Category,
        int ThreadId,
        string Message,
        string? ExceptionText);

    private sealed class TimedOperationScope : IDisposable
    {
        private readonly AppLogCategory _category;
        private readonly string? _details;
        private readonly AppLogger _logger;
        private readonly string _operationName;
        private readonly long _startTimestamp;
        private bool _disposed;

        public TimedOperationScope(AppLogger logger, AppLogCategory category, string operationName, string? details)
        {
            _logger = logger;
            _category = category | AppLogCategory.Performance;
            _operationName = operationName;
            _details = details;
            _startTimestamp = Stopwatch.GetTimestamp();
            _logger.Trace(_category,
                string.IsNullOrEmpty(details)
                    ? $"Started '{operationName}'."
                    : $"Started '{operationName}' ({details}).");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
            var elapsedText = elapsed.TotalMilliseconds >= 1
                ? $"{elapsed.TotalMilliseconds:0.##}ms"
                : $"{elapsed.TotalMicroseconds:0}µs";

            _logger.Info(_category,
                string.IsNullOrEmpty(_details)
                    ? $"Finished '{_operationName}' in {elapsedText}."
                    : $"Finished '{_operationName}' ({_details}) in {elapsedText}.");
        }
    }
}
