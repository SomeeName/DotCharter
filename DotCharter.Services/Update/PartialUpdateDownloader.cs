using System.IO.Compression;
using System.Security.Cryptography;

namespace DotCharter.Services.Update;

/// <summary>How a partial fetch ended. Everything but <see cref="Fetched" /> means
/// "download the full zip instead" — never "the update failed".</summary>
public enum PartialUpdateOutcome
{
    /// <summary>A verified payload is staged and ready for the ordinary apply path.</summary>
    Fetched,

    /// <summary>The server answered 200 (or no <c>Content-Range</c>) — it will not serve ranges.</summary>
    NotRangeCapable,

    /// <summary>The asset is not a zip we will address: zip64, malformed, unsupported compression.</summary>
    ArchiveNotUnderstood,

    /// <summary>The install already matches the release.</summary>
    NothingToFetch,

    /// <summary>The needed bytes are too close to the full asset to bother.</summary>
    NotWorthIt,

    /// <summary>A staged file did not hash to what the release manifest says.</summary>
    VerificationFailed,

    /// <summary>The connection failed. A retry is the caller's business; the fallback is not.</summary>
    TransportFailed
}

/// <summary>Outcome plus what it cost.</summary>
/// <param name="Outcome">What happened. Anything but <see cref="PartialUpdateOutcome.Fetched" /> is a fallback.</param>
/// <param name="Reason">Human-readable detail for the log, or null on success.</param>
/// <param name="BytesFetched">Bytes actually pulled over the wire.</param>
/// <param name="FilesWritten">Payload files reconstructed and verified.</param>
/// <param name="Deletions">
///     Files the release DROPPED, or empty when the local inventory is not
///     entitled to say (<see cref="InstalledPayload.CanDeriveDeletions" />).
/// </param>
public sealed record PartialUpdateResult(
    PartialUpdateOutcome Outcome,
    string? Reason,
    long BytesFetched,
    int FilesWritten,
    IReadOnlyList<string> Deletions)
{
    public bool Succeeded => Outcome == PartialUpdateOutcome.Fetched;

    public static PartialUpdateResult Refused(PartialUpdateOutcome outcome, string? reason)
        => new(outcome, reason, 0, 0, []);
}

/// <summary>
///     Downloads ONLY the bytes an install is missing, straight out of the one full
///     zip every release already publishes.
///     <para>
///         <b>Why this replaced delta-patch assets (0.45).</b> A release is ~414 MB.
///         The previous scheme published a second, small "covering patch" asset
///         diffed against the last few releases; anything older still downloaded
///         the whole thing, and widening the window meant publishing more assets on
///         every release page forever. It did not scale, and the shape of the fix
///         is that the asset does not have to be a new file at all — GitHub serves
///         HTTP <c>Range</c> on release assets, so the full zip is already
///         addressable. Content-addressing it against
///         <c>payload-manifest.json</c> makes the download proportional to what
///         CHANGED, from ANY prior version, with no extra release artifacts.
///     </para>
///     <para>
///         <b>The fallback is what makes it safe, and it is not optional.</b> Any
///         refusal anywhere — a 200 instead of a 206, a zip64 archive, an
///         unsupported compression method, a short response, a hash that does not
///         match — returns an outcome rather than throwing, and the caller runs the
///         untouched full-zip path. Verification happens against the release
///         manifest BEFORE anything is handed on, so a partial payload that is
///         wrong is never applied; it is merely a slower update.
///     </para>
///     <para>
///         Everything read out of the archive is treated as hostile: sizes and
///         offsets are bounded by <see cref="ZipCentralDirectory" />, inflation is
///         capped per file at the size the manifest declares, and every staged path
///         is resolved and prefix-checked against the staging root — the same rule
///         <see cref="Infrastructure.Storage.BoundedZipExtractor" /> applies to an
///         archive on disk.
///     </para>
/// </summary>
public sealed class PartialUpdateDownloader(
    IHttpRangeFetcher fetcher,
    IAppLogger? logger = null,
    long maxRequestBytes = HttpAssetRangeFetcher.MaxRangeBytes)
{
    /// <summary>How much of the asset's tail to pull on the first request. The
    /// end-of-central-directory record is 22 bytes plus a comment, and a ~900-file
    /// release's whole central directory is well under this — so one request
    /// usually yields both.</summary>
    public const int TailProbeBytes = 192 * 1024;

    private const int CopyBufferBytes = 81920;

    private readonly IHttpRangeFetcher _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));

    private readonly long _maxRequestBytes = maxRequestBytes > 0
        ? maxRequestBytes
        : throw new ArgumentOutOfRangeException(nameof(maxRequestBytes));

    /// <summary>
    ///     Reconstructs into <paramref name="stagingDirectory" /> every file
    ///     <paramref name="installed" /> is missing from <paramref name="release" />.
    ///     The directory is the caller's to delete on any non-<see cref="PartialUpdateOutcome.Fetched" />
    ///     outcome.
    /// </summary>
    /// <param name="assetUrl">The API asset url — <c>browser_download_url</c> does not carry auth.</param>
    /// <param name="assetLength">Size of the asset, from the release JSON.</param>
    /// <param name="release">The release's payload manifest — the hashes everything is verified against.</param>
    /// <param name="installed">What this machine already has.</param>
    /// <param name="stagingDirectory">Where the reconstructed payload is written.</param>
    /// <param name="limits">Coalescing + size rules; defaults to half the asset as the budget.</param>
    /// <param name="progress">Fraction of the planned bytes fetched, in [0..1].</param>
    /// <param name="cancellationToken">Cancellation propagates; nothing else does.</param>
    public async Task<PartialUpdateResult> FetchAsync(
        string assetUrl,
        long assetLength,
        PayloadManifest release,
        InstalledPayload installed,
        string stagingDirectory,
        FetchPlanLimits? limits = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetUrl);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);

        if (assetLength <= 0)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                "the release reports no asset size");
        }

        // Half the asset is the limit: past that, the runtime
        // itself has moved and the full zip is the honest answer.
        limits ??= new FetchPlanLimits(MaxTotalBytes: assetLength / 2);

        var tailLength = (int)Math.Min(assetLength, TailProbeBytes);
        var tailStart = assetLength - tailLength;

        var probe = await TryFetchAsync(assetUrl, tailStart, assetLength - 1, cancellationToken).ConfigureAwait(false);
        if (probe.Failure != null) return probe.Failure;
        var tail = probe.Response!;

        if (tail.TotalLength > 0 && tail.TotalLength != assetLength)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"the asset is {tail.TotalLength:N0} bytes, the release says {assetLength:N0}");
        }

        if (tail.Body.Length != tailLength)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"asked for {tailLength:N0} tail bytes and got {tail.Body.Length:N0}");
        }

        var eocd = ZipCentralDirectory.TryReadEndOfCentralDirectory(tail.Body, assetLength);
        if (!eocd.Succeeded)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"end-of-central-directory: {eocd.Refusal}");
        }

        var record = eocd.Record!;

        var directory = await ReadDirectoryAsync(
            assetUrl, record, tail.Body, tailStart, cancellationToken).ConfigureAwait(false);
        if (directory.Failure != null) return directory.Failure;

        var parsed = ZipCentralDirectory.Parse(
            directory.Bytes, record, assetLength, AppPayload.MaxArchiveEntries);
        if (!parsed.Succeeded)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"central directory: {parsed.Refusal}");
        }

        var planned = UpdateFetchPlanner.Plan(
            installed.Manifest, release, parsed.Entries, record.DirectoryOffset, limits);
        if (!planned.Succeeded)
        {
            return PartialUpdateResult.Refused(MapRefusal(planned.Refusal),
                planned.Detail ?? planned.Refusal.ToString());
        }

        var plan = planned.Plan!;
        logger?.Info(AppLogCategory.Application,
            $"Partial update: {plan.Files.Count} file(s), {plan.PayloadBytes / (1024.0 * 1024.0):N1} MB of " +
            $"payload in {plan.Ranges.Count} range request(s) " +
            $"({plan.RequestedBytes / (1024.0 * 1024.0):N1} MB on the wire vs " +
            $"{assetLength / (1024.0 * 1024.0):N0} MB full).");

        // The probe and the directory are part of the download too — accounting for
        // them is what keeps "we fetched X instead of 414 MB" an honest number.
        var overhead = tail.Body.Length + directory.SeparatelyFetched;

        return await ReconstructAsync(
                assetUrl, plan, installed, stagingDirectory, overhead, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    // ---- steps ------------------------------------------------------------

    private async Task<(PartialUpdateResult? Failure, byte[] Bytes, long SeparatelyFetched)> ReadDirectoryAsync(
        string assetUrl, ZipEndOfCentralDirectory record, byte[] tail, long tailStart,
        CancellationToken cancellationToken)
    {
        var size = (int)record.DirectorySize;

        // The tail probe usually already covers it — one request for the whole
        // structure is the common case.
        if (record.DirectoryOffset >= tailStart)
        {
            var offset = (int)(record.DirectoryOffset - tailStart);
            if (offset + size <= tail.Length)
                return (null, tail.AsSpan(offset, size).ToArray(), 0);
        }

        var response = await TryFetchAsync(
                assetUrl, record.DirectoryOffset, record.DirectoryOffset + size - 1, cancellationToken)
            .ConfigureAwait(false);
        if (response.Failure != null) return (response.Failure, [], 0);
        if (response.Response!.Body.Length != size)
        {
            return (PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"asked for a {size:N0}-byte central directory and got {response.Response.Body.Length:N0}"), [], 0);
        }

        return (null, response.Response.Body, size);
    }

    private async Task<PartialUpdateResult> ReconstructAsync(
        string assetUrl, UpdateFetchPlan plan, InstalledPayload installed, string stagingDirectory,
        long alreadyFetched, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        var root = Path.GetFullPath(stagingDirectory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        long fetched = 0;
        var written = 0;

        foreach (var range in plan.Ranges)
        {
            var response = await TryFetchAsync(assetUrl, range.Start, range.EndInclusive, cancellationToken)
                .ConfigureAwait(false);
            if (response.Failure != null) return response.Failure;

            var body = response.Response!.Body;
            if (body.Length != range.Length)
            {
                return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                    $"asked for {range.Length:N0} bytes at {range.Start:N0} and got {body.Length:N0}");
            }

            fetched += body.Length;

            foreach (var file in plan.Files)
            {
                if (!range.Contains(file.Extent.Start, file.Extent.Length)) continue;

                var problem = WriteOne(file, body, (int)(file.Extent.Start - range.Start), root, prefix);
                if (problem != null) return problem;
                written++;
            }

            if (plan.RequestedBytes > 0)
                progress?.Report(Math.Clamp((double)fetched / plan.RequestedBytes, 0.0, 1.0));
        }

        if (written != plan.Files.Count)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"{plan.Files.Count - written} planned file(s) fell outside every fetched range");
        }

        progress?.Report(1.0);
        return new PartialUpdateResult(
            PartialUpdateOutcome.Fetched, null, alreadyFetched + fetched, written,
            installed.CanDeriveDeletions ? plan.Deletions : []);
    }

    /// <summary>Inflates one member out of a fetched buffer and verifies it against
    /// the manifest. Null on success.</summary>
    private static PartialUpdateResult? WriteOne(
        PlannedFile file, byte[] buffer, int extentOffset, string root, string prefix)
    {
        var extentLength = (int)file.Extent.Length;
        var dataOffset = ZipCentralDirectory.TryReadLocalDataOffset(
            buffer.AsSpan(extentOffset, extentLength), file.Entry.CompressedSize);
        if (dataOffset == null)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"{file.File.Path}: no usable local header");
        }

        string target;
        try
        {
            target = Path.GetFullPath(Path.Combine(root, file.File.Path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"{file.File.Path}: not a usable path");
        }

        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"{file.File.Path}: resolves outside the staging directory");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var start = extentOffset + dataOffset.Value;
        var compressedLength = (int)file.Entry.CompressedSize;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long produced = 0;
        try
        {
            using var compressed = new MemoryStream(buffer, start, compressedLength, writable: false);
            using Stream source = file.Entry.CompressionMethod == ZipDirectoryEntry.Deflate
                ? new DeflateStream(compressed, CompressionMode.Decompress)
                : compressed;
            using var output = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes);

            var chunk = new byte[CopyBufferBytes];
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                produced += read;
                // Bounded by the size the MANIFEST declares, not by anything the
                // archive says — a member that keeps inflating is a bomb, and the
                // hash check below could not run until it stopped.
                if (produced > file.File.Size)
                {
                    return PartialUpdateResult.Refused(PartialUpdateOutcome.VerificationFailed,
                        $"{file.File.Path}: inflates past its declared {file.File.Size:N0} bytes");
                }

                hasher.AppendData(chunk, 0, read);
                output.Write(chunk, 0, read);
            }
        }
        catch (InvalidDataException ex)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"{file.File.Path}: {ex.Message}");
        }

        if (produced != file.File.Size)
        {
            return PartialUpdateResult.Refused(PartialUpdateOutcome.VerificationFailed,
                $"{file.File.Path}: {produced:N0} bytes, the manifest says {file.File.Size:N0}");
        }

        var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return string.Equals(hash, file.File.Sha256, StringComparison.OrdinalIgnoreCase)
            ? null
            : PartialUpdateResult.Refused(PartialUpdateOutcome.VerificationFailed,
                $"{file.File.Path}: hashed {hash}, the manifest says {file.File.Sha256}");
    }

    // ---- transport --------------------------------------------------------

    /// <summary>
    ///     One range of the asset, split into requests of at most the per-request cap.
    /// The fetcher refuses a bigger range on purpose - it is what stops a
    ///     server that ignores <c>Range</c> from streaming the whole asset into memory -
    ///     and a changed member can be far bigger: v0.60.0's <c>DotCharter.Tone.UI.dll</c>
    ///     and <c>tools/ffmpeg.exe</c> are 160 MB each, so before this every such update
    ///     fell back to the full 892 MB download.
    /// </summary>
    private async Task<(PartialUpdateResult? Failure, RangeFetchResponse? Response)> TryFetchAsync(
        string url, long start, long endInclusive, CancellationToken cancellationToken)
    {
        var length = endInclusive - start + 1;
        if (length <= _maxRequestBytes)
            return await TryFetchOneAsync(url, start, endInclusive, cancellationToken).ConfigureAwait(false);
        if (length > Array.MaxLength)
        {
            return (PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                $"a {length:N0}-byte range does not fit in memory"), null);
        }

        var joined = new byte[length];
        long total = 0;
        for (var at = start; at <= endInclusive; at += _maxRequestBytes)
        {
            var end = Math.Min(at + _maxRequestBytes - 1, endInclusive);
            var piece = await TryFetchOneAsync(url, at, end, cancellationToken).ConfigureAwait(false);
            if (piece.Failure != null) return piece;
            var body = piece.Response!.Body;
            if (body.Length != end - at + 1)
            {
                return (PartialUpdateResult.Refused(PartialUpdateOutcome.ArchiveNotUnderstood,
                    $"asked for {end - at + 1:N0} bytes at {at:N0} and got {body.Length:N0}"), null);
            }

            body.CopyTo(joined, at - start);
            total = piece.Response.TotalLength;
        }

        return (null, new RangeFetchResponse(true, total, joined));
    }

    private async Task<(PartialUpdateResult? Failure, RangeFetchResponse? Response)> TryFetchOneAsync(
        string url, long start, long endInclusive, CancellationToken cancellationToken)
    {
        RangeFetchResponse response;
        try
        {
            response = await _fetcher.FetchAsync(url, start, endInclusive, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Application,
                $"Partial update: range {start}-{endInclusive} failed, falling back to the full download.", ex);
            return (PartialUpdateResult.Refused(PartialUpdateOutcome.TransportFailed, ex.Message), null);
        }

        return response.IsPartial
            ? (null, response)
            : (PartialUpdateResult.Refused(PartialUpdateOutcome.NotRangeCapable,
                "the server answered without a 206 Partial Content"), null);
    }

    private static PartialUpdateOutcome MapRefusal(FetchPlanRefusal refusal) => refusal switch
    {
        FetchPlanRefusal.NothingToFetch => PartialUpdateOutcome.NothingToFetch,
        FetchPlanRefusal.NotWorthIt => PartialUpdateOutcome.NotWorthIt,
        _ => PartialUpdateOutcome.ArchiveNotUnderstood
    };
}
