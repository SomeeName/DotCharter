using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Infrastructure.Backup;

namespace DotCharter.Services.Diagnostics;

/// <summary>One source for the diagnostics bundle: either an on-disk file
/// (<see cref="SourceFilePath" />) or in-memory text
/// (<see cref="InlineText" />). Redacted before it is zipped when
/// <see cref="Redact" /> is set (the default).</summary>
public sealed record DiagnosticsBundleEntry
{
    /// <summary>Path inside the archive (forward-slashed, root-relative).</summary>
    public required string EntryName { get; init; }

    /// <summary>File on disk to read (mutually exclusive with
    /// <see cref="InlineText" />). Read with shared access so a live log file
    /// can be included while Serilog holds it open.</summary>
    public string? SourceFilePath { get; init; }

    /// <summary>Literal text to write (e.g. generated system info).</summary>
    public string? InlineText { get; init; }

    /// <summary>Scrub secrets from the content before zipping via
    /// <see cref="SensitiveTextRedactor" />. On by default.</summary>
    public bool Redact { get; init; } = true;
}

/// <summary>The set of entries a single bundle should contain.</summary>
public sealed record DiagnosticsBundleRequest(IReadOnlyList<DiagnosticsBundleEntry> Entries);

/// <summary>Outcome of a bundle write. <see cref="SkippedEntries" /> lists
/// entries that were refused (credential files, unsafe names, unreadable
/// sources) — mirrors <see cref="ConfigBackupResult" />.</summary>
public sealed record DiagnosticsBundleResult(int EntryCount, IReadOnlyList<string> SkippedEntries);

/// <summary>
///     Assembles a redacted "Export diagnostics bundle" zip: recent logs, the
///     app config and a generated system-info text, scrubbed of any secret and
///     with credential stores refused outright.
///     <para>
///         EVERY part goes through <see cref="SensitiveTextRedactor" /> — the
///         same single pass the crash bundle uses — so the OS user name and
///         every home path are gone too, not only token shapes.
///     </para>
/// </summary>
public interface IDiagnosticsBundleExporter
{
    /// <summary>Gathers the standard bundle inputs (system info, appconfig.json,
    /// recent logs) and writes the archive to <paramref name="archivePath" />
    /// (overwriting an existing file).</summary>
    DiagnosticsBundleResult Export(string archivePath, string systemInfoText);

    /// <summary>Pure assembly: writes exactly the requested entries to
    /// <paramref name="destination" /> (left open), redacting and refusing
    /// credential stores. This is the unit-tested seam.</summary>
    DiagnosticsBundleResult WriteBundle(Stream destination, DiagnosticsBundleRequest request);
}

/// <inheritdoc />
public sealed class DiagnosticsBundleExporter : IDiagnosticsBundleExporter
{
    /// <summary>Schema of the diagnostics archive.</summary>
    public const int SchemaMajor = 1;

    public const int SchemaMinor = 0;

    /// <summary>Manifest entry at the archive root; its presence marks the zip
    /// as a DotCharter diagnostics bundle.</summary>
    public const string ManifestEntryName = "dotcharter-diagnostics-manifest.json";

    public const string SystemInfoEntryName = "system-info.txt";

    /// <summary>Tail of each log file included, in bytes — bounds the bundle so a
    /// long-running session's multi-megabyte log does not bloat it.</summary>
    public const int MaxLogTailBytes = 2 * 1024 * 1024;

    /// <summary>Most-recent log files to include.</summary>
    public const int MaxLogFiles = 5;

    private readonly string? _configRootOverride;
    private readonly string? _logRootOverride;
    private readonly string? _userNameOverride;

    /// <param name="configRoot">Test override; defaults to
    /// <see cref="WritablePaths.ConfigRoot" />.</param>
    /// <param name="logRoot">Test override; defaults to
    /// <see cref="WritablePaths.LogRoot" />.</param>
    /// <param name="userName">Test override for the OS account name handed to
    /// <see cref="SensitiveTextRedactor" />; defaults to
    /// <see cref="Environment.UserName" />. It is the ONE environment read in
    /// the otherwise pure <see cref="WriteBundle" /> seam, and it is here
    /// because a bundle that keeps the user's account name is not redacted.</param>
    public DiagnosticsBundleExporter(string? configRoot = null, string? logRoot = null, string? userName = null)
    {
        _configRootOverride = configRoot;
        _logRootOverride = logRoot;
        _userNameOverride = userName;
    }

    private string ConfigRoot => _configRootOverride ?? WritablePaths.ConfigRoot;
    private string LogRoot => _logRootOverride ?? WritablePaths.LogRoot;
    private string UserName => _userNameOverride ?? Environment.UserName;

    public DiagnosticsBundleResult Export(string archivePath, string systemInfoText)
    {
        var fullArchivePath = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullArchivePath)!);

        var entries = new List<DiagnosticsBundleEntry>
        {
            new() { EntryName = SystemInfoEntryName, InlineText = systemInfoText }
        };

        var appConfig = Path.Combine(ConfigRoot, "appconfig.json");
        if (File.Exists(appConfig))
            entries.Add(new DiagnosticsBundleEntry { EntryName = "appconfig.json", SourceFilePath = appConfig });

        foreach (var log in RecentLogFiles())
        {
            entries.Add(new DiagnosticsBundleEntry
            {
                EntryName = "logs/" + Path.GetFileName(log),
                SourceFilePath = log
            });
        }

        using var stream = new FileStream(fullArchivePath, FileMode.Create, FileAccess.Write, FileShare.None);
        return WriteBundle(stream, new DiagnosticsBundleRequest(entries));
    }

    public DiagnosticsBundleResult WriteBundle(Stream destination, DiagnosticsBundleRequest request)
    {
        var skipped = new List<string>();
        var count = 0;

        using (var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in request.Entries)
            {
                var safeName = ConfigBackupRules.SanitizeEntryPath(entry.EntryName);
                if (safeName == null)
                {
                    skipped.Add(entry.EntryName);
                    continue;
                }

                // Credential stores never travel — refuse by the entry's file
                // name AND (for on-disk sources) the source file name. Reuses the
                // backup rules' credential sense.
                if (ConfigBackupRules.IsCredential(Path.GetFileName(safeName)) ||
                    (entry.SourceFilePath != null &&
                     ConfigBackupRules.IsCredential(Path.GetFileName(entry.SourceFilePath))))
                {
                    skipped.Add(entry.EntryName);
                    continue;
                }

                if (!TryReadContent(entry, out var content))
                {
                    skipped.Add(entry.EntryName);
                    continue;
                }

                if (entry.Redact) content = SensitiveTextRedactor.Redact(content, UserName);

                var zipEntry = zip.CreateEntry(safeName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(zipEntry.Open(), new UTF8Encoding(false)))
                {
                    writer.Write(content);
                }

                count++;
            }

            WriteManifest(zip, count);
        }

        return new DiagnosticsBundleResult(count, skipped);
    }

    private static bool TryReadContent(DiagnosticsBundleEntry entry, out string content)
    {
        if (entry.InlineText != null)
        {
            content = entry.InlineText;
            return true;
        }

        if (entry.SourceFilePath == null)
        {
            content = string.Empty;
            return false;
        }

        try
        {
            content = ReadTextTail(entry.SourceFilePath, MaxLogTailBytes);
            return true;
        }
        catch
        {
            content = string.Empty;
            return false;
        }
    }

    /// <summary>Reads a file as UTF-8 text, keeping only the last
    /// <paramref name="maxBytes" /> bytes when it is larger, and saying so when it
    /// did. The bounded read itself is the shared <see cref="LogTailReader" /> — the
    /// Session Health panel needs the same thing and must not do it with
    /// <c>ReadToEnd()</c>.</summary>
    private static string ReadTextTail(string path, int maxBytes)
    {
        var body = LogTailReader.ReadTailText(path, maxBytes, out var truncated);
        return truncated
            ? $"… (truncated to last {maxBytes / 1024} KB) …\n{body}"
            : body;
    }

    private IEnumerable<string> RecentLogFiles()
    {
        if (!Directory.Exists(LogRoot)) return [];
        try
        {
            return Directory.EnumerateFiles(LogRoot, "*.log", SearchOption.TopDirectoryOnly)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxLogFiles)
                .Select(f => f.FullName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void WriteManifest(ZipArchive zip, int entryCount)
    {
        var manifest = new DiagnosticsBundleManifest(
            SchemaMajor,
            SchemaMinor,
            DotCharter.Services.Infrastructure.AppVersionInfo.Current,
            DateTimeOffset.UtcNow,
            entryCount);

        var entry = zip.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

/// <summary>Versioned stamp written into every diagnostics bundle.</summary>
public sealed record DiagnosticsBundleManifest(
    int SchemaMajor,
    int SchemaMinor,
    string? AppVersion,
    DateTimeOffset CreatedUtc,
    int EntryCount);
