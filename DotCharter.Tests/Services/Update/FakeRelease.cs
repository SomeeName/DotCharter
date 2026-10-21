using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     Builds a release payload the way <c>New-Release.ps1</c> does — a
///     folder-wrapped zip plus the <c>payload-manifest.json</c> that describes it —
///     so the partial-update tests read the same shape the real updater does.
/// </summary>
internal static class FakeRelease
{
    public const string SelfContainedConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.9"}]}}""";

    /// <summary>The five files <c>AppPayload.Validate</c> demands, so a payload
    /// assembled from a plan is applicable rather than merely small.</summary>
    public static Dictionary<string, byte[]> HostFiles(string tag) => new(StringComparer.OrdinalIgnoreCase)
    {
        [AppPayload.ExeName] = Content($"exe-{tag}", 4096),
        [AppPayload.RuntimeConfigName] = Encoding.UTF8.GetBytes(SelfContainedConfig),
        [AppPayload.DepsName] = Content($"deps-{tag}", 512),
        [AppPayload.HostFxrName] = Content("hostfxr", 8192),
        [AppPayload.CoreClrName] = Content("coreclr", 16384),
    };

    /// <summary>
    ///     Deterministic pseudo-random bytes. <b>Deliberately incompressible</b>: a
    ///     repeating pattern deflates to almost nothing, and then the whole fake
    ///     asset fits inside the downloader's tail probe — which would make every
    ///     "only a fraction was fetched" assertion vacuously true.
    /// </summary>
    public static byte[] Content(string seed, int size)
    {
        var bytes = new byte[size];
        var state = seed.Aggregate(2166136261u, (h, c) => (h ^ c) * 16777619u) | 1u;
        for (var i = 0; i < size; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)state;
        }

        return bytes;
    }

    public static string Sha256(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static PayloadManifest Manifest(string version, IReadOnlyDictionary<string, byte[]> files)
        => new(version, InstallLayout.SelfContained,
            files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(f => new PayloadFile(f.Key, Sha256(f.Value), f.Value.Length))
                .ToList());

    /// <summary>The release zip. <paramref name="rootFolder" /> mirrors the real
    /// asset, which wraps everything in <c>DotCharter-vX.Y.Z-win-x64/</c>.</summary>
    public static byte[] Zip(IReadOnlyDictionary<string, byte[]> files, string? rootFolder = null)
    {
        var prefix = string.IsNullOrEmpty(rootFolder) ? string.Empty : rootFolder.TrimEnd('/') + "/";

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
            {
                using var entry = archive.CreateEntry(prefix + name, CompressionLevel.Optimal).Open();
                entry.Write(content);
            }
        }

        return stream.ToArray();
    }
}

/// <summary>
///     Serves a whole GitHub release over <see cref="FakeHttpMessageHandler" />:
///     the release document for a tag, its <c>SHA256SUMS.txt</c>, its
///     <c>payload-manifest.json</c> and the asset itself (with ranges).
///     <para>
///         It exists because the updater's digest check fails CLOSED at or above
///         <c>GitHubUpdateService.FirstDigestPublishingVersion</c>: a partial-update
///         test can no longer script one response and let the rest fall through,
///         because the release lookup now happens FIRST and would eat it.
///     </para>
/// </summary>
internal static class FakeReleaseHttp
{
    /// <summary>Pass as <c>signature</c> to serve a release with no signature asset.</summary>
    public const string Unsigned = "<unsigned>";

    public const string AssetUrl = "https://api.github.com/repos/o/r/releases/assets/1";
    public const string ManifestUrl = "https://api.github.com/repos/o/r/releases/assets/2";
    public const string ChecksumUrl = "https://api.github.com/repos/o/r/releases/assets/3";
    public const string SignatureUrl = "https://api.github.com/repos/o/r/releases/assets/4";

    /// <summary>A <c>sha256sum</c>-format manifest over the given assets.</summary>
    public static string Digests(params (string Name, byte[] Bytes)[] assets)
        => string.Join("\n", assets.Select(a => $"{FakeRelease.Sha256(a.Bytes)}  {a.Name}"));

    /// <summary>
    ///     <paramref name="checksums" /> is the <c>SHA256SUMS.txt</c> body, or null
    ///     for a release that publishes none — which a modern release may not do,
    ///     and being able to SAY that is the point of the parameter. It is served
    ///     the way <c>New-Release.ps1</c> publishes it: bound to <paramref name="tag" />
    ///     and signed with <see cref="TestReleaseSigning" />'s key, unless
    ///     <paramref name="signature" /> says otherwise (<see cref="Unsigned" /> for
    ///     none at all).
    /// </summary>
    public static FakeHttpMessageHandler Serve(
        string tag,
        string assetName,
        byte[] assetZip,
        byte[]? manifestBytes = null,
        string? checksums = null,
        bool honourRanges = true,
        string? signature = null)
    {
        if (checksums != null) checksums = TestReleaseSigning.Bind(checksums, tag);
        var sig = checksums == null ? null : signature ?? TestReleaseSigning.Sign(checksums);
        if (sig == Unsigned) sig = null;

        var assets = new List<string>
        {
            $$"""{"name":"{{assetName}}","url":"{{AssetUrl}}","size":{{assetZip.Length}}}"""
        };
        if (manifestBytes != null)
        {
            assets.Add(
                $$"""{"name":"{{PayloadManifest.FileName}}","url":"{{ManifestUrl}}","size":{{manifestBytes.Length}}}""");
        }

        if (checksums != null)
            assets.Add($$"""{"name":"SHA256SUMS.txt","url":"{{ChecksumUrl}}","size":{{checksums.Length}}}""");
        if (sig != null)
            assets.Add($$"""{"name":"SHA256SUMS.txt.sig","url":"{{SignatureUrl}}","size":{{sig.Length}}}""");

        var releaseJson = $$"""{"tag_name":"{{tag}}","assets":[{{string.Join(",", assets)}}]}""";

        var handler = new FakeHttpMessageHandler();
        handler.ServeAsset(assetZip, honourRanges);
        var serveAsset = handler.Fallback!;

        handler.Fallback = request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/releases/tags/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                };
            }

            if (url == ManifestUrl && manifestBytes != null)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(manifestBytes)
                };
            }

            if (url == SignatureUrl && sig != null)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(sig, Encoding.ASCII)
                };
            }

            if (url == ChecksumUrl)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(checksums ?? "", Encoding.ASCII)
                };
            }

            return serveAsset(request);
        };

        return handler;
    }
}

/// <summary>
///     The release-signing key of the tests: a fixed Ed25519 key that is NOT
///     the pinned one, so a test service is built with
///     <c>releaseSigningKey: TestReleaseSigning.PublicKey</c> and the fakes sign
///     what they serve with it.
/// </summary>
internal static class TestReleaseSigning
{
    public static readonly byte[] PrivateKey = SHA256.HashData(Encoding.ASCII.GetBytes("dotcharter test release key"));

    public static readonly byte[] PublicKey = ReleaseSignature.PublicKeyOf(PrivateKey);

    /// <summary>A second key: what an attacker with the release page but not the key signs with.</summary>
    public static readonly byte[] OtherPrivateKey = SHA256.HashData(Encoding.ASCII.GetBytes("somebody else"));

    /// <summary>Appends the <c># release:</c> line New-Release.ps1 writes, unless the
    /// body already carries one.</summary>
    public static string Bind(string checksums, string tag)
        => checksums.Contains(ReleaseSignature.ReleaseLinePrefix, StringComparison.Ordinal)
            ? checksums
            : checksums.TrimEnd('\n') + "\n" + ReleaseSignature.ReleaseLine(tag) + "\n";

    public static string Sign(string checksums, byte[]? privateKey = null)
        => ReleaseSignature.Sign(Encoding.ASCII.GetBytes(checksums), privateKey ?? PrivateKey);
}

/// <summary>A scratch directory that cleans itself up.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dc-partial-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose() => BestEffortDelete.Directory(Path);
}

/// <summary>
///     <see cref="IHttpRangeFetcher" /> over a byte array. The seam that lets the
///     downloader's refusal rules be tested without a network — including the one
///     that matters most, a server answering 200 instead of 206.
/// </summary>
internal sealed class InMemoryRangeFetcher(byte[] asset) : IHttpRangeFetcher
{
    /// <summary>Every range asked for, in order.</summary>
    public List<ByteRange> Requests { get; } = [];

    /// <summary>Bytes actually handed back.</summary>
    public long BytesServed { get; private set; }

    /// <summary>Answer without a 206 — what a server that ignores Range does.</summary>
    public bool IgnoreRanges { get; set; }

    /// <summary>Throw on the Nth call (1-based), simulating a dropped connection.</summary>
    public int ThrowOnCall { get; set; }

    /// <summary>Report a different total length than the caller expects.</summary>
    public long TotalLengthOverride { get; set; }

    /// <summary>Refuse a range longer than this, as <see cref="HttpAssetRangeFetcher" />
    /// refuses one over <see cref="HttpAssetRangeFetcher.MaxRangeBytes" />. 0 = no cap.</summary>
    public long MaxRangeBytes { get; set; }

    public Task<RangeFetchResponse> FetchAsync(
        string url, long start, long endInclusive, CancellationToken cancellationToken)
    {
        Requests.Add(new ByteRange(start, endInclusive + 1));
        if (MaxRangeBytes > 0 && endInclusive - start + 1 > MaxRangeBytes)
            throw new ArgumentOutOfRangeException(nameof(endInclusive), "range over the per-request cap");

        if (Requests.Count == ThrowOnCall)
            throw new HttpRequestException("connection reset");

        var total = TotalLengthOverride > 0 ? TotalLengthOverride : asset.Length;
        if (IgnoreRanges) return Task.FromResult(new RangeFetchResponse(false, total, []));

        var length = (int)Math.Min(endInclusive - start + 1, asset.Length - start);
        var body = asset.AsSpan((int)start, length).ToArray();
        BytesServed += body.Length;
        return Task.FromResult(new RangeFetchResponse(true, total, body));
    }
}
