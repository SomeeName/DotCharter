using System.Net;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The updater talks to a host it does not control. The release JSON names the
///     asset url and the handler follows redirects, so every byte the update path
///     reads is chosen by somebody else — and until 0.45 none of those reads had a
///     ceiling. <c>Online/</c> had already learned this (see
///     <c>OnlineHttpPolicyTests</c>); <c>Update/</c> has the same exposure with
///     more consequence, because the full download goes straight to the user's
///     disk.
///     <para>
///         Every test here drives a STUB <see cref="HttpMessageHandler" /> — no
///         network access and no real host. Nothing asserts that anything was
///         FAST; the timeout tests assert only that a deadline exists at all, and
///         a hang would fail them rather than pass them.
///     </para>
/// </summary>
public sealed class UpdateNetworkBoundsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    // ── fixtures ─────────────────────────────────────────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return respond(request, ct);
        }
    }

    /// <summary>
    ///     A body that keeps going after the bytes it declared. The overrun is
    ///     FINITE on purpose: a genuinely endless stream would make an unfixed
    ///     build hang rather than fail, and a hang is not a test result.
    /// </summary>
    private sealed class OverrunStream(byte[] declared, long overrunBytes) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => declared.Length + overrunBytes;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => FillFrom(buffer.AsSpan(offset, count));

        private int FillFrom(Span<byte> buffer)
        {
            var remaining = Length - _position;
            if (remaining <= 0) return 0;

            var take = (int)Math.Min(buffer.Length, remaining);
            for (var i = 0; i < take; i++)
            {
                var at = _position + i;
                buffer[i] = at < declared.Length ? declared[(int)at] : (byte)'x';
            }

            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromResult(FillFrom(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Headers arrive; the body never does. The exact shape a half-open
    /// connection presents, and the one <c>HttpClient.Timeout</c> never covered
    /// because these responses stream with <c>ResponseHeadersRead</c>.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Short enough that a hang fails the test rather than the suite, long
    /// enough that a healthy stub still completes inside it.</summary>
    private static readonly TimeSpan TestBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>A release asset that is a real, applicable zip — so the only thing
    /// a test can be failing on is the bound under test. ONE instance: a zip
    /// carries entry timestamps, and the signed digest is over these bytes.</summary>
    private static byte[] RealAssetZip() => RealAsset.Value;

    private static readonly Lazy<byte[]> RealAsset = new(() =>
        FakeRelease.Zip(FakeRelease.HostFiles("v9.9.9"), "DotCharter-v9.9.9-win-x64"));

    /// <summary>
    ///     <b>At an allowed host, with signed digests of <see cref="RealAssetZip" />.</b>
    ///     The updater refuses a download url outside the GitHub release hosts
    ///     outright, and a release without signed digests — both correct, and
    ///     both would answer every case here before the stream bound under test ever
    ///     ran. These cases are about the BYTES.
    /// </summary>
    private static UpdateInfo UpdateFor(long assetSizeBytes) => new(
        new Version(0, 40, 0), "v0.40.0", "notes", "https://example.invalid/release",
        "DotCharter-v0.40.0-win-x64.zip",
        "https://api.github.com/repos/o/r/releases/assets/1",
        "https://api.github.com/repos/o/r/releases/assets/1",
        assetSizeBytes);

    /// <summary>
    ///     Answers the release-document lookup with a release whose signed
    ///     <c>SHA256SUMS.txt</c> digests <see cref="RealAssetZip" /> — and forwards
    ///     everything else, so each stub below sees exactly the requests it always
    ///     saw and its <c>Calls</c> count still means what it meant.
    /// </summary>
    private sealed class SignedReleaseDocument(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private static readonly string Sums = TestReleaseSigning.Bind(
            FakeReleaseHttp.Digests(("DotCharter-v0.40.0-win-x64.zip", RealAssetZip())), "v0.40.0");

        private static readonly string Signature = TestReleaseSigning.Sign(Sums);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/releases/tags/", StringComparison.Ordinal))
            {
                return Text($$"""
                    {"tag_name":"v0.40.0","assets":[
                      {"name":"SHA256SUMS.txt","url":"{{FakeReleaseHttp.ChecksumUrl}}","size":{{Sums.Length}}},
                      {"name":"SHA256SUMS.txt.sig","url":"{{FakeReleaseHttp.SignatureUrl}}","size":{{Signature.Length}}}]}
                    """);
            }

            if (url == FakeReleaseHttp.ChecksumUrl) return Text(Sums);
            if (url == FakeReleaseHttp.SignatureUrl) return Text(Signature);
            return base.SendAsync(request, ct);
        }

        private static Task<HttpResponseMessage> Text(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private GitHubUpdateService ServiceOver(HttpMessageHandler handler) =>
        new(new Version(0, 1, 0), handler: new SignedReleaseDocument(handler),
            downloadDir: _dir.Path, installDir: _dir.Path, releaseSigningKey: TestReleaseSigning.PublicKey);

    private static HttpResponseMessage Streamed(Stream body, long? declaredLength)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private string DownloadedFile => Path.Combine(_dir.Path, "DotCharter-v0.40.0-win-x64.zip");

    // ── the full download is bounded by what the host DECLARED ───────────────

    /// <summary>
    ///     The bug in one sentence: <c>Content-Length</c> fed the progress bar and
    ///     nothing else, and the copy loop ran to EOF. A host that declared a
    ///     plausible size and then kept sending had the whole overrun written to
    ///     the user's disk — <see cref="AppPayload.CheckArchiveBounds" /> only ever
    ///     ran once the file had already landed.
    /// </summary>
    [Fact]
    public async Task AHostThatKeepsSendingPastItsDeclaredLength_WritesExactlyWhatItDeclared()
    {
        var asset = RealAssetZip();
        const int overrun = 8 * 1024 * 1024;
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Streamed(new OverrunStream(asset, overrun), asset.Length)));
        using var service = ServiceOver(handler);

        var path = await service.DownloadUpdateAsync(UpdateFor(asset.Length), cancellationToken: Ct);

        Assert.Equal(DownloadedFile, path);
        Assert.Equal(asset.Length, new FileInfo(path).Length);
    }

    /// <summary>A declared size past the ceiling is refused before a byte is read —
    /// the cheap half of the same bound, and the only one that can stop a host
    /// that declares honestly and means it.</summary>
    [Fact]
    public async Task ADeclaredSizePastTheCeiling_IsRefusedBeforeAnythingIsWritten()
    {
        var asset = RealAssetZip();
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Streamed(new MemoryStream(asset), GitHubUpdateService.MaxAssetBytes + 1)));
        using var service = ServiceOver(handler);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DownloadUpdateAsync(UpdateFor(asset.Length), cancellationToken: Ct));

        Assert.Contains("ceiling", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(DownloadedFile));
    }

    /// <summary>A host that declares nothing at all still gets the ceiling — and
    /// with no declared length there is nothing to hold it to, so hitting the
    /// ceiling is itself the refusal.</summary>
    [Fact]
    public async Task AHostThatDeclaresNothing_IsStillCeilinged()
    {
        var asset = RealAssetZip();
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Streamed(new OverrunStream(asset, 4096), declaredLength: null)));
        // AssetSizeBytes = 0 too: nothing anywhere claims a length.
        using var service = ServiceOver(handler);

        // Under the ceiling, so the stream is allowed through whole — and it is the
        // signed DIGEST that refuses those extra bytes, never the byte ceiling.
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DownloadUpdateAsync(UpdateFor(0), cancellationToken: Ct));
        byte[] landed = [.. asset, .. Enumerable.Repeat((byte)'x', 4096)];
        Assert.Contains($"got {FakeRelease.Sha256(landed)}", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The other direction: a host that declared more than it sent used to
    /// leave a truncated zip that only failed later. It fails HERE now, and the
    /// partial file is deleted.</summary>
    [Fact]
    public async Task ATruncatedBody_FailsTheDownloadAndLeavesNoFile()
    {
        var asset = RealAssetZip();
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Streamed(new MemoryStream(asset, 0, asset.Length / 2), asset.Length)));
        using var service = ServiceOver(handler);

        var ex = await Assert.ThrowsAsync<IOException>(
            () => service.DownloadUpdateAsync(UpdateFor(asset.Length), cancellationToken: Ct));

        Assert.Contains("Truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(DownloadedFile));
    }

    /// <summary>The control: an honest host is untouched by all of the above.
    /// Without this the bounds could be "passing" by refusing everything.</summary>
    [Fact]
    public async Task AnHonestAsset_StillDownloadsWhole()
    {
        var asset = RealAssetZip();
        var reported = new List<double>();
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Streamed(new MemoryStream(asset), asset.Length)));
        using var service = ServiceOver(handler);

        var path = await service.DownloadUpdateAsync(
            UpdateFor(asset.Length), new SyncProgress(reported.Add), Ct);

        Assert.Equal(asset, await File.ReadAllBytesAsync(path, Ct));
        Assert.Contains(1.0, reported);
    }

    /// <summary>
    ///     Collects progress on the CALLING thread. <see cref="Progress{T}" />
    ///     posts its callback to a synchronization context or the thread pool,
    ///     so the final 1.0 can still be in flight when the await returns — and
    ///     under a saturated pool (the full parallel gate) it reliably is. That
    ///     made <see cref="AnHonestAsset_StillDownloadsWhole" /> pass alone and
    ///     fail in the gate, which reads exactly like a flaky bound.
    /// </summary>
    private sealed class SyncProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    // ── the range fetch is bounded by the range it ASKED for ────────────────

    private static HttpResponseMessage Partial(Stream body, long from, long to, long total, long? declaredLength)
    {
        var content = new StreamContent(body);
        content.Headers.ContentLength = declaredLength;
        content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, total);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }

    private static HttpAssetRangeFetcher FetcherOver(HttpMessageHandler handler) =>
        new(new HttpClient(handler), (url, accept) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            return request;
        });

    /// <summary>
    ///     The request is bounded — a 192 KB tail probe, or a coalesced extent the
    ///     planner capped at 16 MiB — but the RESPONSE was read with a bare
    ///     <c>ReadAsByteArrayAsync</c>, which has no cap, on a
    ///     <c>ResponseHeadersRead</c> response the client's
    ///     <c>MaxResponseContentBufferSize</c> never reaches. A 206 carrying
    ///     gigabytes was gigabytes of memory before the "is this the slice we
    ///     asked for" checks ran at all.
    /// </summary>
    [Fact]
    public async Task A206ThatKeepsSendingPastTheRequestedRange_IsRefusedWithoutDrainingIt()
    {
        const int requested = 1024;
        var overrun = new OverrunStream(new byte[requested], 8 * 1024 * 1024);
        using var handler = new StubHandler((_, _) =>
            // Declares nothing, so only the read itself can stop it.
            Task.FromResult(Partial(overrun, 0, requested - 1, 1 << 20, declaredLength: null)));
        var fetcher = FetcherOver(handler);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => fetcher.FetchAsync("https://example.invalid/asset", 0, requested - 1, Ct));

        Assert.Contains("kept sending", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The point of the bound: it stopped one byte past the range rather than
        // buffering the 8 MiB behind it.
        Assert.True(overrun.Position <= requested + 1,
            $"read {overrun.Position} bytes for a {requested}-byte range");
    }

    /// <summary>The cheap half: a 206 that DECLARES more than the range asked for
    /// never gets read at all.</summary>
    [Fact]
    public async Task A206ThatDeclaresMoreThanTheRangeAskedFor_IsRefusedBeforeReading()
    {
        const int requested = 1024;
        var overrun = new OverrunStream(new byte[requested], 8 * 1024 * 1024);
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Partial(overrun, 0, requested - 1, 1 << 20, declaredLength: 8 * 1024 * 1024)));
        var fetcher = FetcherOver(handler);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => fetcher.FetchAsync("https://example.invalid/asset", 0, requested - 1, Ct));

        Assert.Contains("declared", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, overrun.Position);
    }

    /// <summary>A range past the fetcher's own ceiling is refused before a request
    /// is even made — the planner coalesces to 16 MiB, so nothing legitimate is
    /// anywhere near it.</summary>
    [Fact]
    public async Task ARangeLargerThanTheFetcherWillEverAskFor_IsRefusedWithoutASingleRequest()
    {
        using var handler = new StubHandler((_, _) =>
            throw new InvalidOperationException("no request should have been made"));
        var fetcher = FetcherOver(handler);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fetcher.FetchAsync(
                "https://example.invalid/asset", 0, HttpAssetRangeFetcher.MaxRangeBytes, Ct));

        Assert.Equal(0, handler.Calls);
    }

    /// <summary>The control: an honest 206 still comes back whole, and a slice
    /// SHORTER than the range asked for is handed over as-is — the downloader has
    /// its own "that is not what I asked for" rule and must keep seeing it.</summary>
    [Fact]
    public async Task AnHonest206_StillComesBackWhole()
    {
        var slice = FakeRelease.Content("slice", 4096);
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Partial(new MemoryStream(slice), 0, slice.Length - 1, 1 << 20, slice.Length)));
        var fetcher = FetcherOver(handler);

        var response = await fetcher.FetchAsync("https://example.invalid/asset", 0, slice.Length - 1, Ct);

        Assert.True(response.IsPartial);
        Assert.Equal(1L << 20, response.TotalLength);
        Assert.Equal(slice, response.Body);
    }

    /// <summary>A short body is NOT an error here: the downloader compares the
    /// length against the range it planned and refuses there, which is what keeps
    /// "the server sent less than we asked for" a fallback rather than a
    /// throw.</summary>
    [Fact]
    public async Task A206ShorterThanTheRangeAskedFor_IsHandedBackShortRatherThanThrowing()
    {
        var slice = FakeRelease.Content("short", 512);
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Partial(new MemoryStream(slice), 0, 4095, 1 << 20, slice.Length)));
        var fetcher = FetcherOver(handler);

        var response = await fetcher.FetchAsync("https://example.invalid/asset", 0, 4095, Ct);

        Assert.True(response.IsPartial);
        Assert.Equal(slice, response.Body);
    }

    // ── every exchange has a DEADLINE, headers and body ─────────────────────

    private GitHubUpdateService ImpatientServiceOver(HttpMessageHandler handler) =>
        new(new Version(0, 1, 0), handler: new SignedReleaseDocument(handler), downloadDir: _dir.Path, installDir: _dir.Path,
            apiTimeout: TestBudget, downloadTimeout: TestBudget, rangeTimeout: TestBudget,
            releaseSigningKey: TestReleaseSigning.PublicKey);

    /// <summary>
    ///     The regression: the client's 30 s <c>Timeout</c> bounded the HEADER
    ///     exchange and said so in its own comment, with no replacement for the
    ///     body. Headers arrive, so nothing has timed out; the body then never
    ///     comes and the update waits forever, under a token the UI never gives a
    ///     deadline to.
    /// </summary>
    [Fact]
    public async Task AStalledDownloadBody_TimesOutInsteadOfHangingForever()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Streamed(new StallingStream(), 1 << 20)));
        using var service = ImpatientServiceOver(handler);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => service.DownloadUpdateAsync(UpdateFor(1 << 20), cancellationToken: Ct));

        Assert.Contains("stalled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(DownloadedFile));
    }

    /// <summary>The header exchange stays bounded too — the one case the old
    /// client-level timeout did cover, kept covered now that it is infinite and
    /// the budget is explicit.</summary>
    [Fact]
    public async Task AStalledDownloadHeaderExchange_StillTimesOut()
    {
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var service = ImpatientServiceOver(handler);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => service.DownloadUpdateAsync(UpdateFor(1 << 20), cancellationToken: Ct));

        Assert.Contains("did not answer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The update CHECK is bounded as well, and degrades to a typed
    /// failure with a reason rather than "a task was canceled".</summary>
    [Fact]
    public async Task AStalledUpdateCheck_FailsWithAReasonRatherThanHanging()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            }));
        using var service = ImpatientServiceOver(handler);

        var result = await service.CheckForUpdateAsync(Ct);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("did not answer", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A stalled RANGE fetch costs one short budget. It does not have to
    /// be graceful — <see cref="PartialUpdateDownloader" /> turns any throw here
    /// into a full download.</summary>
    [Fact]
    public async Task AStalledRangeFetch_GivesUpOnItsOwnBudget()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Partial(new StallingStream(), 0, 1023, 1 << 20, declaredLength: null)));
        var fetcher = new HttpAssetRangeFetcher(
            new HttpClient(handler),
            (url, _) => new HttpRequestMessage(HttpMethod.Get, url),
            TestBudget);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.FetchAsync("https://example.invalid/asset", 0, 1023, Ct));
    }

    /// <summary>The control for all of the above: a healthy exchange is nowhere
    /// near these budgets, so nothing here is "passing" by timing out on
    /// everything.</summary>
    [Fact]
    public async Task AHealthyExchange_IsNotAffectedByTheBudgets()
    {
        var asset = RealAssetZip();
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(Streamed(new MemoryStream(asset), asset.Length)));
        using var service = ImpatientServiceOver(handler);

        var path = await service.DownloadUpdateAsync(UpdateFor(asset.Length), cancellationToken: Ct);

        Assert.Equal(asset, await File.ReadAllBytesAsync(path, Ct));
    }
}
