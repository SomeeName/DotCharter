using System.Net;
using System.Net.Http.Headers;

namespace DotCharter.Services.Update;

/// <summary>
///     One range response.
///     <para>
///         <b><see cref="IsPartial" /> is the whole safety property.</b> A server
///         that does not support ranges answers <c>200</c> with the ENTIRE asset —
///         414 MB — and a client that just reads the body has silently done the
///         thing it was trying to avoid, then mis-parsed it as if it were the
///         requested slice. So the transport reports the status instead of the
///         bytes, and the caller falls back to the ordinary full download.
///     </para>
/// </summary>
/// <param name="IsPartial">True only for <c>206 Partial Content</c>.</param>
/// <param name="TotalLength">Length of the whole asset per <c>Content-Range</c>, or 0 when absent.</param>
/// <param name="Body">The bytes. Empty when <paramref name="IsPartial" /> is false.</param>
public sealed record RangeFetchResponse(bool IsPartial, long TotalLength, byte[] Body);

/// <summary>
///     Fetches byte ranges of a remote asset. The seam that keeps
///     <see cref="PartialUpdateDownloader" /> testable without a network.
/// </summary>
public interface IHttpRangeFetcher
{
    /// <summary>
    ///     Requests <c>[start, endInclusive]</c>. Throws for transport failures
    ///     AND for a 206 whose body is longer than the range that was asked for;
    ///     a server that merely refuses to serve a range comes back as
    ///     <see cref="RangeFetchResponse.IsPartial" /> = false. Every one of those
    ///     ends the same way at the call site — <see cref="PartialUpdateDownloader" />
    ///     gives up and the ordinary full download runs.
    /// </summary>
    Task<RangeFetchResponse> FetchAsync(string url, long start, long endInclusive, CancellationToken cancellationToken);
}

/// <summary>
///     <see cref="IHttpRangeFetcher" /> over an <see cref="HttpClient" />.
///     <para>
///         GitHub serves ranges on release assets, but only through the API asset
///         endpoint with <c>Accept: application/octet-stream</c>, and it answers
///         with a 302 to <c>objects.githubusercontent.com</c> — so the client must
///         follow redirects and the caller must pass the API url, not
///         <c>browser_download_url</c>. Verified against a live release asset:
///         <c>206 Partial Content</c>, <c>Accept-Ranges: bytes</c>,
///         <c>Content-Range: bytes 200000000-200000099/414766549</c>.
///     </para>
///     <para>
///         <b>The body is bounded by the range that was REQUESTED.</b> A 206 is
///         still a body of the server's choosing — <c>ReadAsByteArrayAsync</c> has
///         no cap, <see cref="HttpClient.MaxResponseContentBufferSize" /> does not
///         reach a manual read off a <c>ResponseHeadersRead</c> response, and the
///         "did we get what we asked for" checks must not run only after the
///         whole thing has been materialised: a host answering a 192 KB tail probe
///         with gigabytes would be that memory before a line of our code ran.
///     </para>
/// </summary>
public sealed class HttpAssetRangeFetcher(
    HttpClient http,
    Func<string, string, HttpRequestMessage> createRequest,
    TimeSpan? budget = null) : IHttpRangeFetcher
{
    /// <summary>
    ///     Deadline for one range fetch, headers AND body. It has to live here
    ///     rather than on <see cref="HttpClient.Timeout" />: the request streams
    ///     with <c>ResponseHeadersRead</c>, where the client's timeout bounds the
    ///     header exchange and nothing after it.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _budget = budget ?? DefaultBudget;

    /// <summary>
    ///     Largest single range this fetcher will ever ask for, and therefore the
    ///     largest buffer one response can cost. Comfortably over
    ///     <c>FetchPlanLimits.MaxRequestBytes</c> (16 MiB, what the planner
    ///     coalesces up to) so the planner — not this ceiling — stays the thing
    ///     that decides request shape.
    /// </summary>
    public const long MaxRangeBytes = 32L * 1024 * 1024;

    public async Task<RangeFetchResponse> FetchAsync(
        string url, long start, long endInclusive, CancellationToken cancellationToken)
    {
        var requested = endInclusive - start + 1;
        if (requested is <= 0 or > MaxRangeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(endInclusive),
                $"Range {start}-{endInclusive} is {requested} bytes; the limit is {MaxRangeBytes:N0}.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_budget);
        var ct = deadline.Token;

        using var request = createRequest(url, "application/octet-stream");
        request.Headers.Range = new RangeHeaderValue(start, endInclusive);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.StatusCode != HttpStatusCode.PartialContent)
            // Deliberately NOT read: the body here is the whole asset, and pulling
            // it would turn a cheap probe into the full download we are avoiding.
            return new RangeFetchResponse(false, response.Content.Headers.ContentLength ?? 0, []);

        // The cheap half: a host that DECLARES more than we asked for is refused
        // before a byte is buffered.
        if (response.Content.Headers.ContentLength is { } declared && declared > requested)
        {
            throw new InvalidDataException(
                $"Range {start}-{endInclusive} asked for {requested:N0} bytes and the server " +
                $"declared {declared:N0}.");
        }

        var total = response.Content.Headers.ContentRange?.Length ?? 0;
        var body = await ReadAtMostAsync(response.Content, requested, start, endInclusive, ct)
            .ConfigureAwait(false);
        return new RangeFetchResponse(true, total, body);
    }

    /// <summary>
    ///     Reads at most <paramref name="limit" /> bytes and then proves the body
    ///     had no more — a host that declares nothing (or lies) is caught here
    ///     rather than by the machine running out of memory. The one extra byte is
    ///     the whole check: reaching the limit is legitimate, exceeding it is not.
    /// </summary>
    private static async Task<byte[]> ReadAtMostAsync(
        HttpContent content, long limit, long start, long endInclusive, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[limit];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            filled += read;
        }

        var overflow = new byte[1];
        if (await source.ReadAsync(overflow, cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new InvalidDataException(
                $"Range {start}-{endInclusive} asked for {limit:N0} bytes and the server kept sending.");
        }

        return filled == buffer.Length ? buffer : buffer[..filled];
    }
}
