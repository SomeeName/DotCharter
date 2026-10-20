namespace DotCharter.Services.Update;

/// <summary>A half-open byte interval of the remote asset.</summary>
public sealed record ByteRange(long Start, long EndExclusive)
{
    public long Length => EndExclusive - Start;

    /// <summary>Inclusive end, the spelling an HTTP <c>Range</c> header wants.</summary>
    public long EndInclusive => EndExclusive - 1;

    public bool Contains(long start, long length) => start >= Start && start + length <= EndExclusive;
}

/// <summary>One payload file to reconstruct, and where its bytes live in the asset.</summary>
/// <param name="File">The manifest record — the hash the result is VERIFIED against.</param>
/// <param name="Entry">The archive's own description of the member.</param>
/// <param name="Extent">
///     Everything belonging to this member: local header, its name and extra
///     field, the compressed bytes and any trailing data descriptor. Bounded by
///     the next member's local header, so it is exact rather than guessed — the
///     local extra field's length is not knowable from the central record.
/// </param>
public sealed record PlannedFile(PayloadFile File, ZipDirectoryEntry Entry, ByteRange Extent);

/// <summary>Why no partial fetch will be attempted. Each one falls back to the full zip.</summary>
public enum FetchPlanRefusal
{
    None,

    /// <summary>The install already matches the release, byte for byte.</summary>
    NothingToFetch,

    /// <summary>The archive has no member for a file the manifest lists.</summary>
    ArchiveMissingFile,

    /// <summary>A member's declared size disagrees with the manifest's.</summary>
    ArchiveDisagreesWithManifest,

    /// <summary>A member is neither stored nor deflated.</summary>
    UnsupportedCompression,

    /// <summary>Members overlap, or one claims more room than it has.</summary>
    MalformedLayout,

    /// <summary>The partial fetch would not be meaningfully smaller than the full zip.</summary>
    NotWorthIt
}

/// <summary>What a partial update will fetch and what it will delete.</summary>
/// <param name="Files">Every payload file to reconstruct.</param>
/// <param name="Ranges">The HTTP requests to make, coalesced and in file order.</param>
/// <param name="Deletions">Files the release DROPPED (see <see cref="PayloadDeletions" />).</param>
public sealed record UpdateFetchPlan(
    IReadOnlyList<PlannedFile> Files,
    IReadOnlyList<ByteRange> Ranges,
    IReadOnlyList<string> Deletions)
{
    /// <summary>Bytes actually requested, including the slack inside coalesced ranges.</summary>
    public long RequestedBytes => Ranges.Sum(r => r.Length);

    /// <summary>Bytes the reconstructed files will occupy on disk.</summary>
    public long PayloadBytes => Files.Sum(f => f.File.Size);
}

/// <summary>Outcome of planning. <see cref="Plan" /> is null unless the refusal is None.</summary>
public sealed record FetchPlanResult(FetchPlanRefusal Refusal, UpdateFetchPlan? Plan, string? Detail = null)
{
    public bool Succeeded => Refusal == FetchPlanRefusal.None && Plan != null;
}

/// <summary>Tunables for <see cref="UpdateFetchPlanner" />.</summary>
/// <param name="MaxGapBytes">
///     Junk we will fetch rather than open a second connection. A release zip
///     stores related files next to each other, so a small gap is very often
///     cheaper than another TLS round trip to the CDN.
/// </param>
/// <param name="MaxRequestBytes">Ceiling on one coalesced request — this is the peak buffer.</param>
/// <param name="MaxTotalBytes">
///     Past this the partial fetch is not worth its complexity and the full zip is
///     the honest answer.
/// </param>
public sealed record FetchPlanLimits(
    long MaxGapBytes = 64 * 1024,
    long MaxRequestBytes = 16L * 1024 * 1024,
    long MaxTotalBytes = long.MaxValue);

/// <summary>
///     Decides which bytes of a release asset this install actually needs.
///     <para>
///         <b>Why a planner and not just "fetch each file".</b> Every request is a
///         round trip, and a release payload changes a few hundred small files at a
///         time. Coalescing members that sit next to each other in the archive
///         turns a few hundred requests into a handful, and the wasted bytes in the
///         gaps are far cheaper than the connections they replace.
///     </para>
///     <para>
///         <b>Why the extent is bounded by the NEXT member.</b> A member's data
///         starts after its local header, whose extra field can be any length and
///         is not the length the central record gives. Rather than guess a slack,
///         the plan claims everything up to the next member's local header (the
///         central directory for the last one) — which is exact, contains at most a
///         16-byte data descriptor of waste, and doubles as an overlap check: two
///         members whose extents cross is a malformed layout and a refusal.
///     </para>
///     Pure. Every refusal means "download the full zip", never "fail the update".
/// </summary>
public static class UpdateFetchPlanner
{
    /// <summary>
    ///     Plans the fetch that turns <paramref name="installed" /> into
    ///     <paramref name="release" />, out of an archive described by
    ///     <paramref name="archiveEntries" />.
    /// </summary>
    /// <param name="installed">What this machine already has.</param>
    /// <param name="release">What the release publishes.</param>
    /// <param name="archiveEntries">The release asset's central directory.</param>
    /// <param name="directoryOffset">Where that directory starts — the end of the last member.</param>
    /// <param name="limits">Coalescing + size rules.</param>
    public static FetchPlanResult Plan(
        PayloadManifest installed,
        PayloadManifest release,
        IReadOnlyList<ZipDirectoryEntry> archiveEntries,
        long directoryOffset,
        FetchPlanLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(archiveEntries);

        limits ??= new FetchPlanLimits();

        // The SAME diff the packaging side used, so "which files does this install
        // need" has exactly one answer in this codebase — including the
        // AlwaysInclude host pair, which AppPayload.Validate demands and a purely
        // content-diffed set would omit.
        var delta = PayloadDelta.Between(installed, release);
        var wanted = delta.ToSend.ToList();
        if (wanted.Count == 0)
            return new FetchPlanResult(FetchPlanRefusal.NothingToFetch, null);

        var extents = ComputeExtents(archiveEntries, directoryOffset);
        if (extents == null)
        {
            return new FetchPlanResult(FetchPlanRefusal.MalformedLayout, null,
                "members overlap or run past the central directory");
        }

        var root = ResolveArchiveRoot(archiveEntries);
        var byPath = new Dictionary<string, ZipDirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archiveEntries)
        {
            if (entry.IsDirectory) continue;
            if (root.Length > 0 && !entry.Name.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            byPath[entry.Name[root.Length..]] = entry;
        }

        var files = new List<PlannedFile>(wanted.Count);
        foreach (var file in wanted)
        {
            if (!byPath.TryGetValue(file.Path, out var entry))
                return new FetchPlanResult(FetchPlanRefusal.ArchiveMissingFile, null, file.Path);

            if (entry.CompressionMethod is not (ZipDirectoryEntry.Stored or ZipDirectoryEntry.Deflate))
            {
                return new FetchPlanResult(FetchPlanRefusal.UnsupportedCompression, null,
                    $"{file.Path} uses method {entry.CompressionMethod}");
            }

            // The archive and the manifest describe the same release. If they
            // disagree about a file's size one of them is not what we think it is,
            // and reconstructing from the wrong one is exactly the silent
            // corruption this whole path must not have.
            if (entry.UncompressedSize != file.Size)
            {
                return new FetchPlanResult(FetchPlanRefusal.ArchiveDisagreesWithManifest, null,
                    $"{file.Path}: archive says {entry.UncompressedSize} bytes, manifest says {file.Size}");
            }

            files.Add(new PlannedFile(file, entry, extents[entry.Name]));
        }

        var ranges = Coalesce(files.Select(f => f.Extent), limits.MaxGapBytes, limits.MaxRequestBytes);
        var requested = ranges.Sum(r => r.Length);
        if (requested > limits.MaxTotalBytes)
        {
            return new FetchPlanResult(FetchPlanRefusal.NotWorthIt, null,
                $"{requested:N0} bytes of a {limits.MaxTotalBytes:N0}-byte budget");
        }

        files.Sort((a, b) => a.Extent.Start.CompareTo(b.Extent.Start));
        return new FetchPlanResult(FetchPlanRefusal.None,
            new UpdateFetchPlan(files, ranges, delta.Deleted));
    }

    /// <summary>
    ///     Merges ranges that are adjacent or nearly so. Input order does not
    ///     matter; output is sorted and non-overlapping.
    /// </summary>
    public static IReadOnlyList<ByteRange> Coalesce(
        IEnumerable<ByteRange> ranges, long maxGapBytes, long maxRequestBytes)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        var sorted = ranges.Where(r => r.Length > 0).OrderBy(r => r.Start).ToList();
        var merged = new List<ByteRange>();

        foreach (var range in sorted)
        {
            if (merged.Count == 0)
            {
                merged.Add(range);
                continue;
            }

            var last = merged[^1];
            if (range.Start - last.EndExclusive <= maxGapBytes &&
                Math.Max(last.EndExclusive, range.EndExclusive) - last.Start <= maxRequestBytes)
            {
                merged[^1] = last with { EndExclusive = Math.Max(last.EndExclusive, range.EndExclusive) };
                continue;
            }

            merged.Add(range);
        }

        return merged;
    }

    /// <summary>
    ///     The prefix a release zip wraps its payload in (<c>"folder/"</c>), or
    ///     <c>""</c> when members sit at the root. Same rule as
    ///     <see cref="AppPayload.ResolveArchiveRoot" />, over names rather than an
    ///     open archive.
    /// </summary>
    public static string ResolveArchiveRoot(IReadOnlyList<ZipDirectoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
        {
            if (string.Equals(entry.Name, AppPayload.ExeName, StringComparison.OrdinalIgnoreCase))
                return string.Empty;
        }

        foreach (var entry in entries)
        {
            if (!entry.Name.EndsWith("/" + AppPayload.ExeName, StringComparison.OrdinalIgnoreCase)) continue;

            var prefix = entry.Name[..^AppPayload.ExeName.Length];
            // Only a SINGLE top-level folder is unwrapped; a nested hit is not the root.
            if (!prefix.TrimEnd('/').Contains('/')) return prefix;
        }

        return string.Empty;
    }

    /// <summary>
    ///     Bounds each member by the next one's local header. Null when the layout
    ///     is not a plain forward sequence of members — overlapping extents, one
    ///     that cannot hold its own header plus its declared bytes, or a duplicate
    ///     member name (which would make "the entry for this path" ambiguous).
    /// </summary>
    private static Dictionary<string, ByteRange>? ComputeExtents(
        IReadOnlyList<ZipDirectoryEntry> entries, long directoryOffset)
    {
        var ordered = entries.OrderBy(e => e.LocalHeaderOffset).ToList();
        var extents = new Dictionary<string, ByteRange>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var end = i + 1 < ordered.Count ? ordered[i + 1].LocalHeaderOffset : directoryOffset;
            if (end <= entry.LocalHeaderOffset) return null;

            var minimum = ZipCentralDirectory.LocalHeaderFixedBytes + entry.CompressedSize;
            if (end - entry.LocalHeaderOffset < minimum) return null;

            if (!extents.TryAdd(entry.Name, new ByteRange(entry.LocalHeaderOffset, end))) return null;
        }

        return extents;
    }
}
