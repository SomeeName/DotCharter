using System.Text;
using DotCharter.Services.Diagnostics;

namespace DotCharter.Services.Infrastructure.CrashReporting;

/// <summary>
///     The inputs a crash bundle is assembled from. A plain data record so the
///     assembly + redaction logic in <see cref="CrashReportBuilder" /> stays pure
///     and unit-testable — the impure gathering (reading the log file, probing
///     the environment) lives in <see cref="CrashReportService" />.
/// </summary>
public sealed record CrashReportInput
{
    /// <summary>Where the crash surfaced ("AppDomain" | "TaskScheduler").</summary>
    public string Source { get; init; } = string.Empty;

    public string ExceptionType { get; init; } = string.Empty;
    public string ExceptionMessage { get; init; } = string.Empty;
    public string StackTrace { get; init; } = string.Empty;

    /// <summary>Flattened inner-exception chain text, if any.</summary>
    public string? InnerException { get; init; }

    public DateTimeOffset TimestampUtc { get; init; }
    public string AppVersion { get; init; } = string.Empty;

    /// <summary>Ordered "Key: Value" system facts (OS, runtime, memory, …).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> SystemInfo { get; init; } =
        Array.Empty<KeyValuePair<string, string>>();

    /// <summary>The recent tail of the app log (oldest first).</summary>
    public IReadOnlyList<string> RecentLogLines { get; init; } = Array.Empty<string>();

    /// <summary>OS account name to scrub from the bundle (defence-in-depth).</summary>
    public string? UserName { get; init; }
}

/// <summary>
///     Pure assembler for a local crash bundle: lays out a plain-text report
///     (header → exception → system info → log tail) and runs the whole thing
///     through <see cref="SensitiveTextRedactor" /> as a final pass so nothing
///     identifying or credential-ish reaches disk. No I/O, no environment reads —
///     everything comes from the <see cref="CrashReportInput" />.
/// </summary>
public static class CrashReportBuilder
{
    /// <summary>Marker line every bundle starts with (also a test anchor).</summary>
    public const string Header = "DotCharter crash report";

    public static string Build(CrashReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sb = new StringBuilder(4096);

        sb.Append("=== ").Append(Header).AppendLine(" ===");
        sb.Append("Generated (UTC): ")
            .AppendLine(input.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'"));
        AppendLabelled(sb, "App version", input.AppVersion);
        AppendLabelled(sb, "Source", input.Source);
        sb.AppendLine();

        sb.AppendLine("--- Exception ---");
        AppendLabelled(sb, "Type", input.ExceptionType);
        AppendLabelled(sb, "Message", input.ExceptionMessage);
        sb.AppendLine("Stack trace:");
        sb.AppendLine(string.IsNullOrWhiteSpace(input.StackTrace) ? "(none)" : input.StackTrace.TrimEnd());
        if (!string.IsNullOrWhiteSpace(input.InnerException))
        {
            sb.AppendLine();
            sb.AppendLine("Inner exception(s):");
            sb.AppendLine(input.InnerException!.TrimEnd());
        }

        sb.AppendLine();
        sb.AppendLine("--- System info ---");
        if (input.SystemInfo.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var kv in input.SystemInfo)
                AppendLabelled(sb, kv.Key, kv.Value);
        }

        sb.AppendLine();
        sb.Append("--- Recent log (last ").Append(input.RecentLogLines.Count).AppendLine(" line(s)) ---");
        if (input.RecentLogLines.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var line in input.RecentLogLines)
                sb.AppendLine(line);
        }

        // Single final redaction pass over the whole assembled bundle so every
        // free-text field (message, stack, log tail, system info) is scrubbed
        // uniformly. The fixed section headers contain nothing the redactor matches.
        return SensitiveTextRedactor.Redact(sb.ToString(), input.UserName);
    }

    private static void AppendLabelled(StringBuilder sb, string label, string? value) => sb.Append(label).Append(": ").AppendLine(string.IsNullOrEmpty(value) ? "(unknown)" : value);
}
