namespace DotCharter.Services.State.Checkpoints;

/// <summary>
///     One named whole-project snapshot recorded in a project's checkpoint
///     manifest. Serialized to JSON, so the surface is deliberately small and
///     forgiving — every field has a safe default and unknown JSON is ignored,
///     which is what keeps the manifest readable across schema drift.
/// </summary>
public sealed class CheckpointEntry
{
    /// <summary>Stable opaque id (also the snapshot file's base name).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>User-supplied display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Relative file name of the RSCP snapshot inside the project's checkpoint dir.</summary>
    public string SnapshotFileName { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Song name at capture time (context only; never load-bearing).</summary>
    public string SongName { get; set; } = string.Empty;

    /// <summary>App version at capture time (diagnostics only).</summary>
    public string AppVersion { get; set; } = string.Empty;
}

/// <summary>
///     The per-project checkpoint index. Lives next to the snapshots it
///     describes (<c>manifest.json</c> in the project's checkpoint dir).
/// </summary>
public sealed class CheckpointManifest
{
    public int SchemaVersion { get; set; } = CheckpointManifestOps.CurrentSchemaVersion;

    /// <summary>The logical project identity these checkpoints belong to (diagnostics/label only).</summary>
    public string ProjectIdentity { get; set; } = string.Empty;

    public List<CheckpointEntry> Checkpoints { get; set; } = [];
}

/// <summary>
///     Pure, side-effect-free operations over a <see cref="CheckpointManifest"/>.
///     All filesystem I/O lives in <see cref="CheckpointService"/>; everything
///     here is deterministic and unit-tested. Every method is schema-tolerant:
///     it never throws on a null/garbage manifest, it just returns the best
///     clean view it can.
/// </summary>
public static class CheckpointManifestOps
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxNameLength = 80;
    public const string SnapshotExtension = ".rscp";

    /// <summary>Default name when the user leaves the field blank.</summary>
    public const string DefaultName = "Checkpoint";

    /// <summary>
    ///     Trims, collapses inner whitespace/newlines and caps the length of a
    ///     user-entered checkpoint name, substituting a fallback when empty so
    ///     a checkpoint is never nameless.
    /// </summary>
    public static string SanitizeName(string? name, string fallback = DefaultName)
    {
        var collapsed = CollapseWhitespace(name);
        if (collapsed.Length == 0) collapsed = CollapseWhitespace(fallback);
        if (collapsed.Length == 0) collapsed = DefaultName;
        if (collapsed.Length > MaxNameLength) collapsed = collapsed[..MaxNameLength].TrimEnd();
        return collapsed;
    }

    /// <summary>
    ///     Builds a new entry with a fresh id and derived snapshot file name.
    ///     <paramref name="nowUtc"/> is injected so tests stay deterministic;
    ///     <paramref name="id"/> lets callers/tests pin the id (otherwise a new
    ///     GUID).
    /// </summary>
    public static CheckpointEntry CreateEntry(
        string? name,
        string? songName,
        string? appVersion,
        DateTimeOffset nowUtc,
        string? id = null)
    {
        var entryId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id!.Trim();
        return new CheckpointEntry
        {
            Id = entryId,
            Name = SanitizeName(name),
            SnapshotFileName = entryId + SnapshotExtension,
            CreatedUtc = nowUtc,
            SongName = CollapseWhitespace(songName),
            AppVersion = (appVersion ?? string.Empty).Trim()
        };
    }

    /// <summary>Adds (or replaces by id) an entry in place. Returns the same manifest for chaining.</summary>
    public static CheckpointManifest Add(CheckpointManifest manifest, CheckpointEntry entry)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(entry);
        manifest.Checkpoints ??= [];
        manifest.Checkpoints.RemoveAll(c => string.Equals(c.Id, entry.Id, StringComparison.Ordinal));
        manifest.Checkpoints.Add(entry);
        return manifest;
    }

    /// <summary>Removes the entry with <paramref name="id"/>. Returns the removed entry (for file cleanup) or null.</summary>
    public static CheckpointEntry? Remove(CheckpointManifest manifest, string? id)
    {
        if (manifest?.Checkpoints == null || string.IsNullOrWhiteSpace(id)) return null;
        var found = manifest.Checkpoints.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
        if (found != null) manifest.Checkpoints.Remove(found);
        return found;
    }

    /// <summary>Finds the entry with <paramref name="id"/> or null.</summary>
    public static CheckpointEntry? Find(CheckpointManifest manifest, string? id)
    {
        if (manifest?.Checkpoints == null || string.IsNullOrWhiteSpace(id)) return null;
        return manifest.Checkpoints.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));
    }

    /// <summary>Newest-first display order (stable tiebreak by name then id).</summary>
    public static IReadOnlyList<CheckpointEntry> OrderedForDisplay(CheckpointManifest? manifest)
    {
        return Normalize(manifest).Checkpoints
            .OrderByDescending(c => c.CreatedUtc)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Returns a cleaned manifest: never null, a valid schema version, a
    ///     non-null list, only entries with a usable id + snapshot file name,
    ///     no duplicate ids (first wins), and sanitized names. Any entry that
    ///     can't be trusted is dropped rather than throwing — a partially
    ///     corrupt manifest still surfaces the checkpoints that are intact.
    /// </summary>
    public static CheckpointManifest Normalize(CheckpointManifest? manifest)
    {
        var result = new CheckpointManifest
        {
            SchemaVersion = manifest is { SchemaVersion: >= 1 } ? manifest.SchemaVersion : CurrentSchemaVersion,
            ProjectIdentity = manifest?.ProjectIdentity ?? string.Empty
        };

        if (manifest?.Checkpoints == null) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Checkpoints)
        {
            if (entry == null) continue;
            if (string.IsNullOrWhiteSpace(entry.Id)) continue;
            if (!IsBareFileName(entry.SnapshotFileName)) continue;
            if (!seen.Add(entry.Id)) continue;

            result.Checkpoints.Add(new CheckpointEntry
            {
                Id = entry.Id.Trim(),
                Name = SanitizeName(entry.Name),
                SnapshotFileName = entry.SnapshotFileName.Trim(),
                CreatedUtc = entry.CreatedUtc,
                SongName = entry.SongName ?? string.Empty,
                AppVersion = entry.AppVersion ?? string.Empty
            });
        }

        return result;
    }

    /// <summary>
    ///     Whether a manifest's <see cref="CheckpointEntry.SnapshotFileName" />
    ///     may be combined with the project's checkpoint directory.
    ///     <para>
    ///         <b><see cref="Normalize" /> claimed to drop entries without a
    ///         "usable" snapshot file name and only ever checked for blank.</b>
    ///         Everything downstream does <c>Path.Combine(dir, name)</c>, and
    ///         <c>Path.Combine</c> lets a rooted or traversing second argument
    ///         REPLACE the first — so a manifest carrying
    ///         <c>..\..\appconfig.json</c> (or an absolute path) aimed
    ///         <c>CheckpointService.Delete</c>'s file removal, and
    ///         <c>Restore</c>'s read, at an arbitrary file outside the store.
    ///         The manifest is JSON on disk that a hand edit, a sync conflict or
    ///         a partially written file can shape, and the CLASS documents itself
    ///         as schema-tolerant — "tolerant" has to mean dropped, not obeyed.
    ///     </para>
    ///     <para>
    ///         Every name this app writes is <c>&lt;guid:N&gt;.rscp</c>, so
    ///         nothing legitimate is lost: the rule is one path segment, no
    ///         separators, no volume root, no <c>.</c>/<c>..</c>, no character
    ///         the platform rejects in a file name.
    ///     </para>
    /// </summary>
    public static bool IsBareFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        if (trimmed is "." or "..") return false;
        if (trimmed.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        // Belt and braces: whatever the platform's own parser makes of it must
        // still be exactly this one segment.
        return string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal)
               && !Path.IsPathRooted(trimmed);
    }

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var chars = new char[value.Length];
        var length = 0;
        var previousWasSpace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (length == 0) continue; // trim leading
                if (previousWasSpace) continue;
                chars[length++] = ' ';
                previousWasSpace = true;
            }
            else
            {
                chars[length++] = ch;
                previousWasSpace = false;
            }
        }

        if (length > 0 && chars[length - 1] == ' ') length--; // trim trailing
        return new string(chars, 0, length);
    }
}
