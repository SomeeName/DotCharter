using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Infrastructure.Recovery;

/// <summary>
///     Sidecar metadata written next to every crash-recovery snapshot so the
///     startup dialog can show WHAT crashed and Save can be re-pointed at the
///     ORIGINAL project file after a recover.
/// </summary>
public sealed class RecoverySnapshotMetadata
{
    /// <summary>Full path of the real project file, or empty for an untitled project.</summary>
    public string OriginalProjectPath { get; set; } = string.Empty;

    /// <summary>
    ///     The project's audio reference as it appears in the ORIGINAL file
    ///     (usually a relative file name). The snapshot itself stores an
    ///     absolute audio path so it stays playable from the recovery folder;
    ///     this field restores the original relative reference after recover.
    /// </summary>
    public string OriginalAudioFileName { get; set; } = string.Empty;

    public string SongName { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>Digest of the snapshot bytes this metadata describes. Empty in legacy sidecars.</summary>
    public string SnapshotSha256 { get; set; } = string.Empty;

    /// <summary>The committed predecessor, retained until the next snapshot swap succeeds.</summary>
    public RecoverySnapshotMetadata? PreviousGeneration { get; set; }
}

/// <summary>One on-disk crash snapshot (rscp + sidecar) found by the store.</summary>
public sealed class RecoverableSession
{
    public required string SnapshotPath { get; init; }
    public required string SidecarPath { get; init; }
    public required RecoverySnapshotMetadata Metadata { get; init; }
    public bool IsUntitled => string.IsNullOrWhiteSpace(Metadata.OriginalProjectPath);
}

public interface IRecoverySnapshotStore
{
    string RootDirectory { get; }

    /// <summary>Where the snapshot for <paramref name="originalProjectPath"/> lives (whether or not it exists).</summary>
    string GetSnapshotPath(string? originalProjectPath);

    /// <summary>
    ///     Serialize <paramref name="snapshot"/> to <c>&lt;key&gt;.rscp.&lt;random&gt;.tmp</c>,
    ///     write a sidecar describing both the staged and committed generations,
    ///     then atomically rename the snapshot into place as <c>&lt;key&gt;.rscp</c>.
    ///     Recovery selects only the metadata whose digest matches the visible snapshot.
    /// </summary>
    void WriteSnapshot(ChartProject snapshot, RecoverySnapshotMetadata metadata);

    /// <summary>All snapshots on disk (no gating; <c>.tmp</c> files excluded).</summary>
    IReadOnlyList<RecoverableSession> ListSessions();

    /// <summary>
    ///     Crash-evidence gating: an untitled snapshot is always recoverable;
    ///     a titled one only when the original file is gone or the snapshot is
    ///     NEWER than the original's last write (otherwise the on-disk project
    ///     already contains everything the snapshot does).
    /// </summary>
    bool IsRecoverable(RecoverableSession session);

    /// <summary>Deletes only a readable, unchanged listed generation whose original is provably newer.</summary>
    bool DeleteIfSuperseded(RecoverableSession session) => false;

    /// <summary>Best-effort delete of the snapshot + sidecar (+ stray tmp) for a project path.</summary>
    void DeleteSnapshot(string? originalProjectPath);

    /// <summary>Best-effort delete of a listed session's files.</summary>
    void DeleteSession(RecoverableSession session);

    /// <summary>Load a snapshot back into a project (used by tests and diagnostics; the app recovers via the normal project-load path).</summary>
    ChartProject LoadSnapshot(string snapshotPath);
}

/// <summary>
///     Crash-recovery snapshot storage under
///     <c>WritablePaths.ConfigRoot/recovery/&lt;hash-of-project-path&gt;.rscp</c>
///     (+ <c>.rscp.json</c> sidecar). Snapshots are full RSCP serializations of a
///     deep-cloned project — the existence of one at startup is the crash
///     evidence, its content is the recovered work.
/// </summary>
public sealed class RecoverySnapshotStore : IRecoverySnapshotStore
{
    public const string UntitledKey = "untitled";
    private const string SnapshotExtension = ".rscp";
    private const string SidecarSuffix = ".rscp.json";
    private const string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IChartSerializer _serializer;
    private readonly Lock _sync = new();

    public RecoverySnapshotStore(string? rootDirectory = null, IChartSerializer? serializer = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(WritablePaths.ConfigRoot, "recovery");
        _serializer = serializer ?? new RscpChartSerializer();
    }

    public string RootDirectory { get; }

    /// <summary>
    ///     Stable filesystem key for a project path: SHA-256 of the lowercased
    ///     full path, first 16 hex chars — collision-safe enough for a handful
    ///     of concurrent sessions and keeps the file name short and valid.
    /// </summary>
    public static string ComputeKey(string? originalProjectPath)
    {
        if (string.IsNullOrWhiteSpace(originalProjectPath)) return UntitledKey;

        string normalized;
        try
        {
            normalized = Path.GetFullPath(originalProjectPath);
        }
        catch
        {
            normalized = originalProjectPath;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public string GetSnapshotPath(string? originalProjectPath) => Path.Combine(RootDirectory, ComputeKey(originalProjectPath) + SnapshotExtension);

    public void WriteSnapshot(ChartProject snapshot, RecoverySnapshotMetadata metadata)
    {
        lock (_sync) WriteSnapshotLocked(snapshot, metadata);
    }

    private void WriteSnapshotLocked(ChartProject snapshot, RecoverySnapshotMetadata metadata)
    {
        Directory.CreateDirectory(RootDirectory);

        var snapshotPath = GetSnapshotPath(metadata.OriginalProjectPath);
        var tempPath = snapshotPath + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        try
        {
            _serializer.Save(tempPath, snapshot);
            // The serializer owns its stream: force the staged bytes to disk
            // before publishing the generation that names them.
            AtomicFile.FlushFile(tempPath);

            var sidecarPath = SidecarPathFor(snapshotPath);
            var previous = File.Exists(snapshotPath)
                ? MatchingMetadata(snapshotPath, TryReadSidecar(sidecarPath)) ?? new RecoverySnapshotMetadata
                {
                    TimestampUtc = SafeLastWriteUtc(snapshotPath) ?? DateTimeOffset.UtcNow
                }
                : null;
            if (previous != null)
            {
                previous.SnapshotSha256 = SnapshotHash(snapshotPath);
                previous.PreviousGeneration = null;
            }

            var generation = new RecoverySnapshotMetadata
            {
                OriginalProjectPath = metadata.OriginalProjectPath,
                OriginalAudioFileName = metadata.OriginalAudioFileName,
                SongName = metadata.SongName,
                TimestampUtc = metadata.TimestampUtc,
                AppVersion = metadata.AppVersion,
                SnapshotSha256 = SnapshotHash(tempPath),
                PreviousGeneration = previous
            };
            // The sidecar keeps both candidates across the swap. A crash at either
            // rename therefore leaves a matching chart/metadata pair on disk.
            AtomicFile.WriteAllText(sidecarPath, JsonSerializer.Serialize(generation, JsonOptions));
            AtomicFile.ReplaceWithRetry(tempPath, snapshotPath);
        }
        catch
        {
            BestEffortDelete.File(tempPath);
            throw;
        }
    }

    public IReadOnlyList<RecoverableSession> ListSessions()
    {
        lock (_sync) return ListSessionsLocked();
    }

    private IReadOnlyList<RecoverableSession> ListSessionsLocked()
    {
        if (!Directory.Exists(RootDirectory)) return Array.Empty<RecoverableSession>();

        var sessions = new List<RecoverableSession>();
        foreach (var file in Directory.EnumerateFiles(RootDirectory, "*" + SnapshotExtension))
        {
            // Explicit extension check: Win32-style patterns can over-match,
            // and a "<key>.rscp.tmp" torn write must never be listed.
            if (!string.Equals(Path.GetExtension(file), SnapshotExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            var sidecarPath = SidecarPathFor(file);
            var sidecar = TryReadSidecar(sidecarPath);
            var metadata = MatchingMetadata(file, sidecar);
            // A hashed sidecar cannot describe any other bytes, even when its
            // original path and audio reference happen to look plausible.
            if (sidecar is { SnapshotSha256.Length: > 0 } && metadata == null) continue;
            // Preserve legacy snapshots without a readable sidecar as untitled work.
            metadata ??= new RecoverySnapshotMetadata
            {
                TimestampUtc = SafeLastWriteUtc(file) ?? DateTimeOffset.UtcNow
            };

            // The sidecar is not trusted to say where Save writes: a path that is not
            // a plain local .rscp leaves the session untitled (Save As), work intact.
            if (!string.IsNullOrWhiteSpace(metadata.OriginalProjectPath) &&
                !RecoveredProjectPath.IsAdoptable(metadata.OriginalProjectPath))
            {
                metadata.OriginalProjectPath = string.Empty;
            }

            sessions.Add(new RecoverableSession
            {
                SnapshotPath = file,
                SidecarPath = sidecarPath,
                Metadata = metadata
            });
        }

        return sessions;
    }

    public bool IsRecoverable(RecoverableSession session)
    {
        if (!File.Exists(session.SnapshotPath)) return false;
        if (!string.IsNullOrEmpty(session.Metadata.SnapshotSha256) &&
            !ReferenceEquals(MatchingMetadata(session.SnapshotPath, session.Metadata), session.Metadata))
        {
            return false;
        }
        if (session.IsUntitled) return true;

        var originalPath = session.Metadata.OriginalProjectPath;
        if (!File.Exists(originalPath)) return true;

        var snapshotWrite = SafeLastWriteUtc(session.SnapshotPath);
        var originalWrite = SafeLastWriteUtc(originalPath);
        if (snapshotWrite == null || originalWrite == null) return true;
        return snapshotWrite > originalWrite;
    }

    public bool DeleteIfSuperseded(RecoverableSession session)
    {
        lock (_sync)
        {
            if (!IsSuperseded(session)) return false;
            DeleteSession(session);
            return true;
        }
    }

    private bool IsSuperseded(RecoverableSession session)
    {
        if (session.IsUntitled || string.IsNullOrEmpty(session.Metadata.SnapshotSha256) ||
            !RecoveredProjectPath.IsAdoptable(session.Metadata.OriginalProjectPath))
        {
            return false;
        }
        try
        {
            using var snapshot = new FileStream(session.SnapshotPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var original = new FileStream(session.Metadata.OriginalProjectPath, FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(snapshot)), session.Metadata.SnapshotSha256,
                    StringComparison.Ordinal))
            {
                return false;
            }

            var saved = _serializer.Load(session.Metadata.OriginalProjectPath);
            var captured = _serializer.Load(session.SnapshotPath);
            if (saved.ProjectId != captured.ProjectId) return false;
            var snapshotWrite = SafeLastWriteUtc(session.SnapshotPath);
            var originalWrite = SafeLastWriteUtc(session.Metadata.OriginalProjectPath);
            return snapshotWrite != null && originalWrite != null && originalWrite >= snapshotWrite;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or
                                   InvalidOperationException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    public void DeleteSnapshot(string? originalProjectPath)
    {
        var snapshotPath = GetSnapshotPath(originalProjectPath);
        BestEffortDelete.File(snapshotPath);
        BestEffortDelete.File(SidecarPathFor(snapshotPath));
        BestEffortDelete.File(snapshotPath + TempSuffix);
        BestEffortDelete.File(SidecarPathFor(snapshotPath) + TempSuffix);
    }

    public void DeleteSession(RecoverableSession session)
    {
        BestEffortDelete.File(session.SnapshotPath);
        BestEffortDelete.File(session.SidecarPath);
        BestEffortDelete.File(session.SnapshotPath + TempSuffix);
        BestEffortDelete.File(session.SidecarPath + TempSuffix);
    }

    public ChartProject LoadSnapshot(string snapshotPath) => _serializer.Load(snapshotPath);

    private static RecoverySnapshotMetadata? MatchingMetadata(string snapshotPath, RecoverySnapshotMetadata? metadata)
    {
        if (metadata == null || string.IsNullOrEmpty(metadata.SnapshotSha256)) return metadata;
        string hash;
        try
        {
            hash = SnapshotHash(snapshotPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (string.Equals(hash, metadata.SnapshotSha256, StringComparison.Ordinal)) return metadata;
        var previous = metadata.PreviousGeneration;
        return previous != null && string.Equals(hash, previous.SnapshotSha256, StringComparison.Ordinal)
            ? previous
            : null;
    }

    private static string SnapshotHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SidecarPathFor(string snapshotPath) => Path.ChangeExtension(snapshotPath, null) + SidecarSuffix;

    /// <summary>Test seam: runs while a sidecar read holds its handle open, so a
    /// test can publish a snapshot from another store at exactly that moment.</summary>
    internal Action? SidecarOpenForTesting { get; set; }

    private RecoverySnapshotMetadata? TryReadSidecar(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath)) return null;
            // AtomicFile.OpenRead, not File.OpenRead: this file is written
            // by AtomicFile, and a plain read denies DELETE — so an autosave
            // landing while the startup dialog enumerates sessions has its rename
            // refused and burns the bounded retry, and past the budget the write
            // is dropped. Sharing read/write/delete lets the rename proceed while
            // this handle keeps reading the whole OLD sidecar.
            using var stream = AtomicFile.OpenRead(sidecarPath);
            SidecarOpenForTesting?.Invoke();
            return JsonSerializer.Deserialize<RecoverySnapshotMetadata>(stream);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeLastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
        }
    }
}
