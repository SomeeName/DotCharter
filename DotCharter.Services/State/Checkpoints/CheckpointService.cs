using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Library;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.State.Checkpoints;

public interface ICheckpointService
{
    /// <summary>Root under which every project's checkpoint folder lives.</summary>
    string RootDirectory { get; }

    /// <summary>Newest-first checkpoints for a project identity (empty when none / unreadable).</summary>
    IReadOnlyList<CheckpointEntry> List(string? projectIdentity);

    /// <summary>
    ///     Persists <paramref name="snapshot"/> as a named checkpoint for
    ///     <paramref name="projectIdentity"/> and records it in the manifest.
    ///     The caller is expected to hand in a deep clone
    ///     (<see cref="ChartProjectCloner"/>) so a concurrent edit can't tear
    ///     the written file.
    /// </summary>
    CheckpointEntry Create(
        string? projectIdentity,
        string? name,
        ChartProject snapshot,
        string? songName = null,
        string? appVersion = null);

    /// <summary>Loads the project graph stored for a checkpoint. Throws if the snapshot file is gone.</summary>
    ChartProject Restore(string? projectIdentity, CheckpointEntry entry);

    /// <summary>Deletes a checkpoint's snapshot + manifest entry. Returns true when something was removed.</summary>
    bool Delete(string? projectIdentity, string? checkpointId);

    /// <summary>
    ///     Copies every checkpoint of <paramref name="fromIdentity" /> that <paramref name="toIdentity" />
    /// does not already hold, for Save As; the source keeps its own. The shared untitled
    ///     bucket (an empty identity) is never copied: an unsaved session cannot tell its checkpoints
    ///     from another unsaved session's. Returns how many were copied; 0, and nothing written, when
    ///     either index cannot be read.
    /// </summary>
    int CopyTo(string? fromIdentity, string toIdentity);
}

/// <summary>The identity checkpoints are keyed on, shared by the Checkpoints dialog and Save As.</summary>
public static class CheckpointIdentity
{
    /// <summary>The project's saved path, else its library song, else empty (the untitled bucket).</summary>
    public static string For(string? projectPath, string? songIdentity) =>
        !string.IsNullOrWhiteSpace(projectPath) ? projectPath : LibrarySongIdentity.NormalizeOrNull(songIdentity) ?? string.Empty;
}

/// <summary>
///     Named, restart-surviving project snapshots. Each project identity gets
///     its own folder under <c>&lt;ConfigRoot&gt;/checkpoints/&lt;key&gt;/</c>
///     holding a <c>manifest.json</c> index plus one RSCP file per checkpoint —
///     the same serializer the recovery store and manual save use, so a
///     checkpoint is a full, self-contained snapshot of the project graph.
///
///     Manifest and snapshot writes go through a temp file + atomic rename, so a
///     crash mid-write never corrupts an existing checkpoint or the index. All
///     manifest interpretation is delegated to the pure
///     <see cref="CheckpointManifestOps"/>, which is schema-tolerant by design.
/// </summary>
public sealed class CheckpointService : ICheckpointService
{
    private const string ManifestFileName = "manifest.json";
    private const string TempSuffix = ".tmp";
    private const string UntitledKey = "untitled";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly IChartSerializer _serializer;
    private readonly object _sync = new();

    public CheckpointService(string? rootDirectory = null, IChartSerializer? serializer = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(WritablePaths.ConfigRoot, FolderName);
        _serializer = serializer ?? new RscpChartSerializer();
    }

    /// <summary>Folder name under the config root. A CONSTANT because
    /// <c>PayloadReceiptFilter</c> must refuse this tree: on a portable install
    /// the config root IS the install dir, so an un-refused folder in an update
    /// payload overwrites this one — and these are whole project snapshots, i.e.
    /// the user's unpublished charting work.</summary>
    public const string FolderName = "checkpoints";

    public string RootDirectory { get; }

    /// <summary>
    ///     Stable filesystem key for a project identity: SHA-256 of the
    ///     lowercased, trimmed identity, first 16 hex chars. Works for a file
    ///     path or a logical id (e.g. <c>lib:SongKey</c>) —
    ///     it hashes the raw string, so it never mangles a non-path identity the
    ///     way <c>Path.GetFullPath</c> would.
    /// </summary>
    public static string ComputeKey(string? projectIdentity)
    {
        if (string.IsNullOrWhiteSpace(projectIdentity)) return UntitledKey;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(projectIdentity.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    public IReadOnlyList<CheckpointEntry> List(string? projectIdentity)
    {
        lock (_sync)
        {
            // An unreadable index is reported as empty HERE on purpose: listing
            // shows nothing, which is recoverable the moment the file can be read
            // again. It is the WRITE paths that must refuse — they are the ones
            // that can make the guess permanent.
            MigrateLegacyLocked(projectIdentity);
            var manifest = TryReadManifest(ProjectDirectory(projectIdentity), out _)
                           ?? new CheckpointManifest();
            return CheckpointManifestOps.OrderedForDisplay(manifest);
        }
    }

    public CheckpointEntry Create(
        string? projectIdentity,
        string? name,
        ChartProject snapshot,
        string? songName = null,
        string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            MigrateLegacyLocked(projectIdentity);
            var dir = ProjectDirectory(projectIdentity);
            Directory.CreateDirectory(dir);

            // The index is read BEFORE anything is written, and a failure to read
            // it refuses the whole operation.
            //
            // "The manifest is there but I could not read it" is not the same
            // fact as "there is no manifest", and treating it as one is
            // destructive rather than merely lossy: the new entry is added to the
            // empty guess and WriteManifest atomically replaces the real index
            // with a one-entry file. Every earlier checkpoint disappears from the
            // list, and its snapshot stays on disk as an orphan Delete can no
            // longer reach — with the status line still saying "created". One
            // antivirus or indexer hold on manifest.json is enough, which is a
            // mode this repo already designs for (AtomicFile.ReplaceWithRetry).
            //
            // Reading first is what keeps the refusal clean: refusing after the
            // snapshot had been written would leave behind exactly the orphan
            // this is protecting.
            var manifest = TryReadManifest(dir, out var failure)
                           ?? throw new IOException(
                               $"The checkpoint index '{Path.Combine(dir, ManifestFileName)}' exists but " +
                               "could not be read, so writing a new one would unlist every checkpoint " +
                               "already stored for this project. No checkpoint was created.", failure);

            var entry = CheckpointManifestOps.CreateEntry(name, songName, appVersion, DateTimeOffset.UtcNow);

            var snapshotPath = Path.Combine(dir, entry.SnapshotFileName);
            var tempPath = snapshotPath + TempSuffix;
            try
            {
                _serializer.Save(tempPath, snapshot);
                // The serializer owns its own stream, so when Save returns the
                // bytes are in the OS page cache and NOT on the platter — which
                // makes the stage-then-swap only half a pattern. A power cut can
                // retire the atomic RENAME while the contents are still in
                // flight, and the manifest then lists a checkpoint whose .rscp is
                // empty or truncated: it shows up in the dialog, it survives a
                // restart, and Restore throws on it. That is the exact failure
                // the manifest write below guards against;
                // the snapshot — the half that holds the user's actual work —
                // kept writing without the flush. AtomicFile.FlushFile is the
                // documented counterpart for writers that cannot go through
                // AtomicFile.WriteAllText/Bytes.
                AtomicFile.FlushFile(tempPath);
            }
            catch
            {
                // Same rule as the unreadable-manifest refusal above: a refusal
                // leaves the folder exactly as it found it. A half-written .tmp
                // is an orphan no Delete can reach (it is in no manifest) and it
                // accumulates one file per failed attempt.
                BestEffortDelete.File(tempPath);
                throw;
            }

            // AtomicFile, not a bare File.Move: on Windows the rename FAILS if
            // anything currently has the destination open — an antivirus or
            // indexer scan of the file this checkpoint id last wrote is enough —
            // and the throw would escape a user-initiated action and leave the
            // .tmp orphaned. Same retry every other stage-then-swap in the app
            // uses; the manifest write below already went through it.
            AtomicFile.ReplaceWithRetry(tempPath, snapshotPath);

            manifest.ProjectIdentity = projectIdentity ?? string.Empty;
            CheckpointManifestOps.Add(manifest, entry);
            WriteManifest(dir, manifest);

            return entry;
        }
    }

    public ChartProject Restore(string? projectIdentity, CheckpointEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync) MigrateLegacyLocked(projectIdentity);
        var dir = ProjectDirectory(projectIdentity);
        var snapshotPath = Path.Combine(dir, entry.SnapshotFileName);
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException($"Checkpoint snapshot not found: {snapshotPath}", snapshotPath);
        return _serializer.Load(snapshotPath);
    }

    public bool Delete(string? projectIdentity, string? checkpointId)
    {
        if (string.IsNullOrWhiteSpace(checkpointId)) return false;

        lock (_sync)
        {
            MigrateLegacyLocked(projectIdentity);
            var dir = ProjectDirectory(projectIdentity);
            // Same rule as Create, one step milder: an index we could not read is
            // not an index we may rewrite. "Nothing was removed" is true and
            // leaves every stored checkpoint reachable.
            var manifest = TryReadManifest(dir, out _);
            if (manifest is null) return false;

            var removed = CheckpointManifestOps.Remove(manifest, checkpointId);
            if (removed == null) return false;

            BestEffortDelete.File(Path.Combine(dir, removed.SnapshotFileName));
            WriteManifest(dir, manifest);
            return true;
        }
    }

    public int CopyTo(string? fromIdentity, string toIdentity)
    {
        if (string.IsNullOrWhiteSpace(fromIdentity) || string.IsNullOrWhiteSpace(toIdentity)) return 0;

        lock (_sync)
        {
            MigrateLegacyLocked(fromIdentity);
            MigrateLegacyLocked(toIdentity);
            return CopyLocked(ProjectDirectory(fromIdentity), ProjectDirectory(toIdentity), toIdentity);
        }
    }

    /// <summary>
    /// a library song's checkpoints were filed under its <c>psarc:</c> identity.
    ///     The first access under the <c>lib:</c> identity moves that folder to the new
    ///     key (a rename: nothing is deleted). When the new folder already exists, every
    ///     checkpoint it lacks is COPIED in and the old folder is removed only once each of
    ///     its checkpoints is listed under the new key; an entry already there wins. An old
    ///     index that cannot be read leaves both folders alone. Idempotent; never throws.
    /// </summary>
    private void MigrateLegacyLocked(string? projectIdentity)
    {
        if (LibrarySongIdentity.LegacyFormOf(projectIdentity?.Trim()) is not { } legacyIdentity) return;
        var legacyDir = ProjectDirectory(legacyIdentity);
        if (!Directory.Exists(legacyDir)) return;
        var dir = ProjectDirectory(projectIdentity);
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.Move(legacyDir, dir);
                if (TryReadManifest(dir, out _) is { Checkpoints.Count: > 0 } moved)
                {
                    moved.ProjectIdentity = projectIdentity!.Trim();
                    WriteManifest(dir, moved);
                }

                return;
            }

            CopyLocked(legacyDir, dir, projectIdentity!.Trim());
            var legacy = TryReadManifest(legacyDir, out _);
            var current = TryReadManifest(dir, out _);
            if (legacy is null || current is null) return;
            if (legacy.Checkpoints.All(e => CheckpointManifestOps.Find(current, e.Id) != null &&
                                            File.Exists(Path.Combine(dir, e.SnapshotFileName))))
            {
                Directory.Delete(legacyDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left where it is: the old folder still holds every checkpoint, and the
            // next access tries again.
        }
    }

    private int CopyLocked(string fromDir, string toDir, string toIdentity)
    {
        if (string.Equals(fromDir, toDir, StringComparison.OrdinalIgnoreCase)) return 0;

        // Same rule as Create: an index that is there and unreadable is never rewritten.
        var source = TryReadManifest(fromDir, out _);
        var target = TryReadManifest(toDir, out _);
        if (source is null || target is null || source.Checkpoints.Count == 0) return 0;

        Directory.CreateDirectory(toDir);
        var copied = 0;
        foreach (var entry in source.Checkpoints)
        {
            if (CheckpointManifestOps.Find(target, entry.Id) != null) continue;
            var from = Path.Combine(fromDir, entry.SnapshotFileName);
            var to = Path.Combine(toDir, entry.SnapshotFileName);
            // A snapshot already sitting at the target's name is not this one to replace.
            if (!File.Exists(from) || File.Exists(to)) continue;

            var temp = to + TempSuffix;
            try
            {
                File.Copy(from, temp, overwrite: true);
                AtomicFile.FlushFile(temp);
            }
            catch
            {
                BestEffortDelete.File(temp);
                throw;
            }

            AtomicFile.ReplaceWithRetry(temp, to);
            CheckpointManifestOps.Add(target, entry);
            copied++;
        }

        if (copied == 0) return 0;
        target.ProjectIdentity = toIdentity;
        WriteManifest(toDir, target);
        return copied;
    }

    private string ProjectDirectory(string? projectIdentity) => Path.Combine(RootDirectory, ComputeKey(projectIdentity));

    /// <summary>
    ///     The index for a project, or <c>null</c> when a manifest is THERE and
    ///     could not be read.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The distinction is the whole point. An ABSENT manifest means there
    ///         are no checkpoints, and an empty index is then the truth — that is
    ///         a brand-new project, and every caller may write. Anything else —
    ///         a lock held by an antivirus or an indexer, a permission failure,
    ///         a file whose JSON does not parse — means the index exists and its
    ///         contents are UNKNOWN, which is not a fact any caller may substitute
    ///         an empty list for.
    ///     </para>
    ///     <para>
    ///         Still never throws, so a transient failure cannot take a listing
    ///         down; the caller decides what its own failure means.
    ///     </para>
    /// </remarks>
    private CheckpointManifest? TryReadManifest(string dir, out Exception? failure)
    {
        failure = null;
        var path = Path.Combine(dir, ManifestFileName);
        try
        {
            if (!File.Exists(path)) return new CheckpointManifest();
            var manifest = JsonSerializer.Deserialize<CheckpointManifest>(AtomicFile.ReadAllText(path), JsonOptions);
            return CheckpointManifestOps.Normalize(manifest);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Raced with a delete, or the folder went away between the Exists
            // check and the open. There is nothing stored, which is the one case
            // where an empty index is a fact rather than a guess.
            return new CheckpointManifest();
        }
        catch (Exception ex)
        {
            failure = ex;
            return null;
        }
    }

    private void WriteManifest(string dir, CheckpointManifest manifest)
    {
        Directory.CreateDirectory(dir);
        var normalized = CheckpointManifestOps.Normalize(manifest);
        var path = Path.Combine(dir, ManifestFileName);
        // AtomicFile, not a hand-rolled temp+Move: the hand-rolled copy skipped
        // the fsync, so a power cut could retire the rename before the data and
        // swap a truncated manifest in — every named checkpoint then vanished
        // from the UI. AtomicFile is temp → Flush(true) →
        // Move(overwrite) with retry.
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(normalized, JsonOptions));
    }
}
