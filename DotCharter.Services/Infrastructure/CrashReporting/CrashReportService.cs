using System.Globalization;
using System.Runtime.InteropServices;
using DotCharter.Services.Diagnostics;

namespace DotCharter.Services.Infrastructure.CrashReporting;

public interface ICrashReportService
{
    /// <summary>Absolute directory local crash bundles are written to.</summary>
    string ReportsDirectory { get; }

    /// <summary>
    ///     If the user has opted in (<see cref="AppConfig.CrashReportOptIn" />),
    ///     assemble a redacted crash bundle for <paramref name="exception" /> and
    ///     write it to <see cref="ReportsDirectory" />. Returns the bundle path, or
    ///     <c>null</c> when opted out or on any failure. NEVER throws — it runs
    ///     from top-level unhandled-exception handlers.
    /// </summary>
    string? TryCapture(string source, Exception exception);
}

/// <summary>
///     Writes a local, redacted crash bundle on an unhandled exception when the
///     user has opted in. The bundle is deliberately LOCAL ONLY — never uploaded
///     — so the user can inspect it and attach it to a bug report themselves.
///     Impure edges (config, log file, environment, disk) live here; the assembly
///     and redaction are delegated to the pure <see cref="CrashReportBuilder" /> /
///     <see cref="SensitiveTextRedactor" />.
/// </summary>
public sealed class CrashReportService : ICrashReportService
{
    private const int MaxLogTailLines = 200;
    private const int MaxLogReadBytes = 512 * 1024;
    private const int MaxReportsRetained = 20;

    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<AppConfig?> _configProvider;
    private readonly string? _logFilePath;
    private readonly IAppLogger? _logger;

    public CrashReportService(
        Func<AppConfig?> configProvider,
        IAppLogger? logger = null,
        string? reportsDirectory = null,
        string? logFilePath = null,
        Func<DateTimeOffset>? clock = null)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _logger = logger;
        ReportsDirectory = reportsDirectory ?? Path.Combine(WritablePaths.ConfigRoot, FolderName);
        // Default to the logger's live path so the tail we bundle is the same
        // file the crash handler just flushed.
        _logFilePath = logFilePath ?? logger?.CurrentLogPath;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Folder name under the config root. A CONSTANT because
    /// <c>PayloadReceiptFilter</c> must refuse this tree — crash reports carry
    /// log tails and machine details, and on a portable install the config root
    /// IS the install dir an update payload is copied over.</summary>
    public const string FolderName = "crash-reports";

    public string ReportsDirectory { get; }

    public string? TryCapture(string source, Exception exception)
    {
        try
        {
            if (exception is null) return null;

            var config = SafeLoadConfig();
            if (config is null || !config.CrashReportOptIn) return null;

            var input = BuildInput(source, exception);
            var report = CrashReportBuilder.Build(input);

            Directory.CreateDirectory(ReportsDirectory);
            var fileName = $"crash-{input.TimestampUtc.ToUniversalTime():yyyyMMdd-HHmmss}-{ShortId()}.txt";
            var path = Path.Combine(ReportsDirectory, fileName);
            File.WriteAllText(path, report);

            PruneOldReports();

            try
            {
                _logger?.Info(AppLogCategory.Application, $"Local crash report written to '{path}'.");
            }
            catch
            {
                // Logging is best-effort here; the process is already dying.
            }

            return path;
        }
        catch (Exception ex)
        {
            // A crash-report writer must never itself throw from a top-level
            // unhandled-exception handler.
            try
            {
                _logger?.Warning(AppLogCategory.Errors, "Writing the local crash report failed.", ex);
            }
            catch
            {
                // ignore
            }

            return null;
        }
    }

    private CrashReportInput BuildInput(string source, Exception exception)
    {
        return new CrashReportInput
        {
            Source = source ?? string.Empty,
            ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            ExceptionMessage = exception.Message,
            StackTrace = exception.StackTrace ?? string.Empty,
            InnerException = FlattenInner(exception.InnerException),
            TimestampUtc = _clock(),
            AppVersion = ResolveAppVersion(),
            SystemInfo = GatherSystemInfo(),
            RecentLogLines = ReadLogTail(),
            UserName = SafeUserName()
        };
    }

    private static string? FlattenInner(Exception? inner)
    {
        if (inner is null) return null;

        var sb = new System.Text.StringBuilder();
        var depth = 0;
        for (var e = inner; e is not null && depth < 8; e = e.InnerException, depth++)
        {
            sb.Append("[[").Append(depth + 1).Append("]] ")
                .Append(e.GetType().FullName ?? e.GetType().Name)
                .Append(": ").AppendLine(e.Message);
            if (!string.IsNullOrWhiteSpace(e.StackTrace))
                sb.AppendLine(e.StackTrace.TrimEnd());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> GatherSystemInfo()
    {
        var info = new List<KeyValuePair<string, string>>();

        void Add(string key, string value) => info.Add(new KeyValuePair<string, string>(key, value));

        try { Add("OS", RuntimeInformation.OSDescription); } catch { /* ignore */ }
        try { Add("OS architecture", RuntimeInformation.OSArchitecture.ToString()); } catch { /* ignore */ }
        try { Add("Process architecture", RuntimeInformation.ProcessArchitecture.ToString()); } catch { /* ignore */ }
        try { Add(".NET runtime", RuntimeInformation.FrameworkDescription); } catch { /* ignore */ }
        try { Add("Processor count", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture)); } catch { /* ignore */ }
        try { Add("Working set (MB)", (Environment.WorkingSet / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)); } catch { /* ignore */ }
        try { Add("Culture", CultureInfo.CurrentCulture.Name); } catch { /* ignore */ }
        try { Add("64-bit process", Environment.Is64BitProcess.ToString()); } catch { /* ignore */ }

        return info;
    }

    private IReadOnlyList<string> ReadLogTail()
    {
        var path = _logFilePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Array.Empty<string>();

        try
        {
            // Diagnostics/LogTailReader is the ONE way to read the end of a log
            // — reading one any other way is a bug (see state-diagnostics.md).
            // This was a fourth hand-rolled copy of the seek-then-drop-the-partial
            // -first-line dance, and it had drifted: it re-materialised the whole
            // window through String.Replace before splitting, and it counted the
            // partial first line against the 200-line budget only when the file
            // happened to be larger than the window. The shared reader also opens
            // FileShare.ReadWrite, which is what lets the LIVE log the sink still
            // holds be read from a dying process.
            var tail = LogTailReader.ReadTailLines(path, MaxLogTailLines, MaxLogReadBytes);
            if (tail.Length == 0) return Array.Empty<string>();

            return tail.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Length > 0)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private void PruneOldReports()
    {
        try
        {
            if (!Directory.Exists(ReportsDirectory)) return;

            var files = Directory.GetFiles(ReportsDirectory, "crash-*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            foreach (var stale in files.Skip(MaxReportsRetained))
            {
                try { stale.Delete(); }
                catch { /* best-effort cleanup */ }
            }
        }
        catch
        {
            // Pruning must never break the capture path.
        }
    }

    private AppConfig? SafeLoadConfig()
    {
        try
        {
            return _configProvider();
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeUserName()
    {
        try
        {
            return Environment.UserName;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveAppVersion() => AppVersionInfo.Current;

    private static string ShortId() => Guid.NewGuid().ToString("N")[..8];
}
