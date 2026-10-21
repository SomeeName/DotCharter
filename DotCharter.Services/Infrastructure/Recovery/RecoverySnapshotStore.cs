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
    ///     Serialize <paramref name="snapshot"/> to <c>&lt;key&gt;.rscp.tmp</c>,
    ///     write the SIDECAR, and only then atomically rename the snapshot into
    ///     place as <c>&lt;key&gt;.rscp</c>. A crash mid-write leaves only a
    ///     <c>.tmp</c>, which is never considered recoverable — and a snapshot
    ///     that IS visible always has its metadata beside it.
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
        Directory.CreateDirectory(RootDirectory);

        var snapshotPath = GetSnapshotPath(metadata.OriginalProjectPath);
        var tempPath = snapshotPath + TempSuffix;
        _serializer.Save(tempPath, snapshot);
        // The serializer owns its own stream, so this is exactly the case
        // AtomicFile.FlushFile exists for: without it the stage-then-swap is only
        // half done — the rename is atomic, but a power cut can persist the
        // RENAME while the snapshot's bytes are still in the page cache, leaving
        // an atomically-swapped-in EMPTY .rscp where the crash evidence should
        // be. That is precisely the file whose whole job is to survive a crash.
        AtomicFile.FlushFile(tempPath);

        // THE SIDECAR GOES FIRST, and the snapshot becomes visible only after it
        // lands. The file name is a one-way HASH of the project path
        // (ComputeKey), so the sidecar is the ONLY record of which project a
        // snapshot belongs to — and of the original, usually relative, audio
        // reference, since the snapshot's own AudioFileName was rewritten
        // absolute on the way in. Writing it second meant a crash between the two
        // renames (the exact moment snapshots exist for) left a `.rscp` with no
        // sidecar, which ListSessions correctly surfaces as an UNTITLED session:
        // the player's titled project comes back with Save pointing at nothing
        // and the audio path baked absolute, and IsRecoverable can never gate it,
        // so it re-offers itself on every launch until it is discarded by hand.
        //
        // AtomicFile.WriteAllText stages a randomly named sibling ".tmp", forces
        // it to the platter and renames with the bounded retry — the same three
        // steps the snapshot does by hand — and removes its own stage when the
        // swap fails. DeleteSnapshot/DeleteSession still sweep the fixed
        // "<path>.tmp" name an older build left behind; the catch below sweeps
        // OUR temp, so a failed sidecar leaves the previous (still described)
        // snapshot standing rather than a staged orphan beside it.
        var sidecarPath = SidecarPathFor(snapshotPath);
        try
        {
            AtomicFile.WriteAllText(sidecarPath, JsonSerializer.Serialize(metadata, JsonOptions));
        }
        catch
        {
            BestEffortDelete.File(tempPath);
            throw;
        }

        AtomicFile.ReplaceWithRetry(tempPath, snapshotPath);
    }

    public IReadOnlyList<RecoverableSession> ListSessions()
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
            var metadata = TryReadSidecar(sidecarPath)
                           // A missing/corrupt sidecar loses the original-path link but
                           // not the work: surface the snapshot as an untitled session
                           // instead of silently ignoring it.
                           ?? new RecoverySnapshotMetadata
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
        if (session.IsUntitled) return true;

        var originalPath = session.Metadata.OriginalProjectPath;
        if (!File.Exists(originalPath)) return true;

        var snapshotWrite = SafeLastWriteUtc(session.SnapshotPath);
        var originalWrite = SafeLastWriteUtc(originalPath);
        if (snapshotWrite == null || originalWrite == null) return true;
        return snapshotWrite > originalWrite;
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

    private static string SidecarPathFor(string snapshotPath) => Path.ChangeExtension(snapshotPath, null) + SidecarSuffix;

    private static RecoverySnapshotMetadata? TryReadSidecar(string sidecarPath)
    {
        try
        {
            if (!File.Exists(sidecarPath)) return null;
            // AtomicFile.ReadAllText, not File.ReadAllText: this file is written
            // by AtomicFile, and a plain read denies DELETE — so an autosave
            // landing while the startup dialog enumerates sessions has its rename
            // refused and burns the bounded retry, and past the budget the write
            // is dropped. Sharing read/write/delete lets the rename proceed while
            // this handle keeps reading the whole OLD sidecar.
            return JsonSerializer.Deserialize<RecoverySnapshotMetadata>(AtomicFile.ReadAllText(sidecarPath));
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
