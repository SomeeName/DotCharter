namespace DotCharter.Tone.Logging;

public interface IAppLogger
{
    string CurrentLogPath { get; }
    bool IsEnabled(AppLogCategory category);
    void Flush();
    void ReloadSettings();
    void Trace(AppLogCategory category, string message);
    void Debug(AppLogCategory category, string message);
    void Info(AppLogCategory category, string message);
    void Warning(AppLogCategory category, string message);
    void Warning(AppLogCategory category, string message, Exception? exception);
    void Error(AppLogCategory category, string message, Exception? exception = null);
    void External(string source, string message);

    /// <summary>
    /// Fires after every log line is emitted (Trace/Debug/Info/Warn/Error).
    /// Used by the in-app status bar to surface warnings + errors without
    /// having to add a custom Serilog sink. Handlers must not throw and
    /// must not call back into the logger.
    /// </summary>
    event Action<LogEmittedArgs>? LogEmitted;

    /// <summary>
    ///     Logs a "started" trace on creation and an Info with elapsed time on Dispose.
    ///     Use with a <c>using</c> block around a discrete operation.
    /// </summary>
    IDisposable BeginTimedOperation(AppLogCategory category, string operationName, string? details = null);

    /// <summary>
    ///     Reports an exception caught at an application-level boundary (e.g. global
    ///     unhandled exception handler or UI dispatcher).
    /// </summary>
    void LogUnhandledException(string source, Exception exception);
}
