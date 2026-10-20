using System.Globalization;
using System.Text.RegularExpressions;
using DotCharter.UI.Diagnostics;

namespace DotCharter.UI.ViewModels.Diagnostics;

/// <summary>How a Session Health card reads at a glance.</summary>
public enum HealthTone
{
    /// <summary>Nothing to do.</summary>
    Good,

    /// <summary>Working, but a number crossed a stated line.</summary>
    Watch,

    /// <summary>Broken right now.</summary>
    Bad
}

/// <summary>Which log rows the Recent Log panel shows.</summary>
public enum LogSeverityFilter
{
    All,
    Warnings,
    Errors
}

/// <summary>Severity of one parsed log row.</summary>
public enum LogSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
///     One row of the log tail, split into the four columns the panel prints.
///     Parsing is a projection of the sink's own output template
///     (<c>AppLogger</c>: <c>{Timestamp} [{Level:u4}] [{Category}] [T{ThreadId}]
///     {Message}</c>) — nothing here invents a field the line does not carry.
/// </summary>
public sealed record DiagnosticsLogEntry(string Time, LogSeverity Severity, string Category, string Message)
{
    /// <summary>The level token as the sink writes it, for the LEVEL column.
    /// Kept separate from <see cref="Severity" />: the panel prints what the log
    /// says, and only the FILTER folds VERB/DBUG/INFO together.</summary>
    public string Level { get; init; } = "INFO";

    /// <summary>Style-class flags for the row's LEVEL cell. A class is how a
    /// severity colour is expressed on the Fire layer, and the markup binds
    /// these directly rather than carrying a converter per column.</summary>
    public bool IsWarning => Severity == LogSeverity.Warning;

    public bool IsError => Severity == LogSeverity.Error;

    public bool MatchesFilter(LogSeverityFilter filter) => filter switch
    {
        LogSeverityFilter.Warnings => Severity is LogSeverity.Warning or LogSeverity.Error,
        LogSeverityFilter.Errors => Severity == LogSeverity.Error,
        _ => true
    };
}

/// <summary>
///     Turns a raw log tail into rows. Pure and allocation-bounded — the caller
///     already caps how many BYTES it read.
/// </summary>
public static partial class DiagnosticsLogParser
{
    /// <summary>
    ///     `yyyy-MM-dd HH:mm:ss.fff zzz [VERB] [Audio] [T22] message`.
    ///     Serilog's <c>{Level:u4}</c> writes VERB / DBUG / INFO / WARN / EROR /
    ///     FATL, so the level token is four letters and the match is anchored on
    ///     the bracket structure rather than on the level vocabulary — a new
    ///     level must show up as an ordinary row, never as an unparsed line.
    /// </summary>
    [GeneratedRegex(
        @"^\d{4}-\d{2}-\d{2} (?<time>\d{2}:\d{2}:\d{2}\.\d{3})[^\[]*" +
        @"\[(?<level>[A-Z]{3,5})\] \[(?<cat>[^\]]*)\] \[T\d+\] (?<msg>.*)$",
        RegexOptions.Compiled)]
    private static partial Regex LineShape();

    /// <summary>
    ///     Newest FIRST — the panel is read top-down and the interesting line is
    ///     always the last one written.
    ///     <para>
    ///         A line that does not match the sink's shape is a CONTINUATION (an
    ///         exception's stack trace is many such lines) and is folded into the
    ///         entry above it. Dropping them would hide the one thing anybody
    ///         opens this panel for; promoting them to rows of their own would
    ///         make one exception look like forty events.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<DiagnosticsLogEntry> Parse(string? tail, int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(tail) || maxEntries <= 0) return [];

        var entries = new List<DiagnosticsLogEntry>();
        var continuation = new List<string>();

        foreach (var raw in tail.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var m = LineShape().Match(line);
            if (!m.Success)
            {
                if (entries.Count > 0) continuation.Add(line.Trim());
                continue;
            }

            FlushContinuation();

            var level = m.Groups["level"].Value;
            entries.Add(new DiagnosticsLogEntry(
                m.Groups["time"].Value,
                SeverityOf(level),
                m.Groups["cat"].Value,
                m.Groups["msg"].Value)
            {
                Level = level
            });
        }

        FlushContinuation();

        entries.Reverse();
        return entries.Count > maxEntries ? entries.GetRange(0, maxEntries) : entries;

        void FlushContinuation()
        {
            if (continuation.Count == 0 || entries.Count == 0) { continuation.Clear(); return; }
            var last = entries[^1];
            entries[^1] = last with { Message = last.Message + " ⏎ " + string.Join(" ⏎ ", continuation) };
            continuation.Clear();
        }
    }

    /// <summary>WARN is a warning; EROR and FATL are errors; everything quieter
    /// is information. An unknown token reads as information rather than
    /// disappearing — the filter must never silently swallow a row.</summary>
    public static LogSeverity SeverityOf(string? level) => level switch
    {
        "WARN" => LogSeverity.Warning,
        "EROR" or "FATL" => LogSeverity.Error,
        _ => LogSeverity.Info
    };
}

/// <summary>
///     The rules behind Session Health's status pills and its "one thing worth
///     looking at" footer. PURE, so every threshold is stated in one place and
///     testable without a booted app or an audio device.
/// </summary>
/// <remarks>
///     Every INPUT here is a real diagnostic channel. The THRESHOLDS are
///     judgement calls and are named constants for exactly that reason. Nothing in this file invents a metric;
///     where the mockup drew one the app cannot measure, the card simply does
///     not draw it.
/// </remarks>
public static class SessionHealthRules
{
    /// <summary>Output latency a player feels while playing along. 40 ms is
    /// roughly two 512-frame buffers at 44.1 kHz — past it, timing feedback
    /// stops being trustworthy, which is what this app is FOR.</summary>
    public const double WatchOutputLatencyMs = 40;

    /// <summary>A single frame this long is a visible hitch, not jitter. The 2D
    /// timeline and the 3D highway both target 60 fps (16.7 ms), so a quarter of
    /// a second is ~15 dropped frames in a row.</summary>
    public const double WatchWorstFrameMs = 250;

    /// <summary>Working set past which the session is worth watching. The app
    /// legitimately holds decoded stems and ONNX models, so this is high on
    /// purpose — it flags "something is not being released", not "the app is
    /// using memory".</summary>
    public const long WatchWorkingSetBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    ///     The audio engine. Device lost is the only BAD state the app can
    ///     report about itself; no device open at all is a watch, because a
    ///     chart-only session legitimately has none.
    /// </summary>
    public static HealthTone Audio(bool deviceLost, int activeSampleRate, double outputLatencyMs)
    {
        if (deviceLost) return HealthTone.Bad;
        if (activeSampleRate <= 0) return HealthTone.Watch;
        return outputLatencyMs > WatchOutputLatencyMs ? HealthTone.Watch : HealthTone.Good;
    }

    /// <summary>
    ///     Rendering. A renderer that has drawn NO frames is idle, not unhealthy
    ///     — the 3D preview is closed by default and a session that never opened
    ///     it must not read as broken.
    /// </summary>
    public static HealthTone Rendering(int frames2D, double worst2DMs, int frames3D, double worst3DMs)
    {
        if (frames2D == 0 && frames3D == 0) return HealthTone.Good;
        var worst = Math.Max(frames2D > 0 ? worst2DMs : 0, frames3D > 0 ? worst3DMs : 0);
        return worst > WatchWorstFrameMs ? HealthTone.Watch : HealthTone.Good;
    }

    /// <summary>Runtime + memory.</summary>
    public static HealthTone Runtime(long workingSetBytes) =>
        workingSetBytes >= WatchWorkingSetBytes ? HealthTone.Watch : HealthTone.Good;

    /// <summary>
    ///     Startup. The rule is NOT "the numbers are large" — it is whether the
    ///     numbers mean anything at all. A run that is not ReadyToRun JIT-compiles
    ///     its whole startup path and measures roughly twice the shipped build, so
    ///     its phases are a JIT measurement; the card says so instead of inviting
    ///     someone to optimise a number no user ever pays.
    /// </summary>
    public static HealthTone Startup(StartupPhasesView phases) =>
        phases.HasData && phases.IsPrecompiled ? HealthTone.Good : HealthTone.Watch;

    /// <summary>What a card's pill says next to its dot, given its tone.</summary>
    public static string StatusKey(HealthTone tone) => tone switch
    {
        HealthTone.Bad => "Diagnostics_Status_Failing",
        HealthTone.Watch => "Diagnostics_Status_Watch",
        _ => "Diagnostics_Status_Healthy"
    };

    /// <summary>
    ///     The footer's ONE thing worth looking at: the worst card, named with
    ///     its own reason. Ordered worst-first and then by how much the user can
    ///     act on it — a lost device is actionable, a JIT-timed startup is not.
    /// </summary>
    public static HealthFinding Headline(
        HealthTone audio, HealthTone rendering, HealthTone runtime, HealthTone startup)
    {
        (HealthTone Tone, string Key)[] ordered =
        [
            (audio, "Diagnostics_Finding_Audio"),
            (rendering, "Diagnostics_Finding_Rendering"),
            (runtime, "Diagnostics_Finding_Runtime"),
            (startup, "Diagnostics_Finding_Startup")
        ];

        foreach (var tone in new[] { HealthTone.Bad, HealthTone.Watch })
        {
            foreach (var (cardTone, key) in ordered)
            {
                if (cardTone == tone)
                    return new HealthFinding(tone, key);
            }
        }

        return new HealthFinding(HealthTone.Good, "Diagnostics_Finding_None");
    }

    /// <summary>A byte count as the dashboard prints it.</summary>
    public static string FormatBytes(long bytes) => bytes <= 0
        ? "0 MB"
        : (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.CurrentCulture) + " MB";
}

/// <summary>The footer line: which tone, and the resx key naming its reason.</summary>
public readonly record struct HealthFinding(HealthTone Tone, string MessageKey);

/// <summary>
///     The startup phases as the RULES see them — a value the tests can build
///     without booting an app. <see cref="HasData" /> false is the honest state
///     before the window has opened (and in any headless host, which never
///     opens one).
/// </summary>
public readonly record struct StartupPhasesView(bool HasData, bool IsPrecompiled)
{
    public static StartupPhasesView From(StartupPhases? phases) =>
        phases is { } p ? new StartupPhasesView(true, p.IsPrecompiled) : new StartupPhasesView(false, false);
}
