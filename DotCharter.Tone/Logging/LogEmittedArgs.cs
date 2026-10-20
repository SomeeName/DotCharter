namespace DotCharter.Tone.Logging;

/// <summary>
/// Payload broadcast by <see cref="IAppLogger.LogEmitted"/>. Carries the
/// fields the in-app status bar needs to render a warning/error toast
/// without having to introspect the Serilog pipeline.
/// </summary>
public readonly record struct LogEmittedArgs(
    DateTimeOffset Timestamp,
    string Level,
    AppLogCategory Category,
    string Message,
    string? ExceptionText);
