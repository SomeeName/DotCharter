using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The partial download hashes EVERY file it fetches against the release's
///     <c>payload-manifest.json</c>. The FULL download — the path every install
///     older than v0.41.0 takes, the path every range refusal falls back to, and
///     the path that actually moves the 414 MB — had no content check at all:
///     <c>AppPayload.CheckArchiveBounds</c> asks whether the bytes are a readable
///     zip of a plausible member count, and nothing asked whether they were the
///     asset we published. Those bytes arrive over a redirect chain
///     (<c>objects.githubusercontent.com</c>) and are then robocopied over the
///     install.
///     <para>
///         The digests have been published as <c>SHA256SUMS.txt</c> since 0.41,
///         and since 2026-09-04 the check fails CLOSED at or above that version
///         (0.41). It used to
///         proceed with a warning whenever the manifest was unreachable or did
///         not list the asset — and both are states an attacker who controls the
///         release document can ARRANGE, which made the one content check on the
///         path optional at the attacker's discretion. The manifest now must
///         also carry a valid publisher signature (<c>ReleaseSigningTests</c>), so
///         the pre-0.41 escape hatch is gone: an unsigned release is refused.
///     </para>
/// </summary>
public sealed class FullAssetChecksumTests : IDisposable
{
    private const string SelfContainedConfig =
        """{"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.9"}]}}""";

    private const string AssetName = "DotCharter-v0.48.0-win-x64.zip";
    private const string AssetUrl = FakeReleaseHttp.AssetUrl;

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-asset-digest-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public async Task AFullAssetThatMatchesThePublishedDigestIsAccepted()
    {
        var zip = BuildPayloadZip();
        var digest = Sha256Hex(zip);

        using var service = NewService(Handler(zip, $"{digest}  {AssetName}"));

        var path = await service.DownloadUpdateAsync(
            Update(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
        Assert.Equal(zip.Length, new FileInfo(path).Length);
    }

    /// <summary>
    ///     The refusal. The release names this asset and the bytes are somebody
    ///     else's — nothing may survive on disk to be applied by hand or picked up
    ///     by a later resume.
    /// </summary>
    [Fact]
    public async Task AFullAssetThatDoesNotMatchThePublishedDigestIsRefusedAndDeleted()
    {
        var zip = BuildPayloadZip();
        var wrong = new string('a', 64);

        using var service = NewService(Handler(zip, $"{wrong}  {AssetName}"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadUpdateAsync(
            Update(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("not the build it claims to be", error.Message, StringComparison.Ordinal);
        Assert.Contains(wrong, error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "downloads", AssetName)),
            "a rejected asset was left on disk");
    }

    /// <summary>
    ///     The fail-CLOSED half. A modern release that publishes no digests is
    ///     indistinguishable from one whose digests an attacker suppressed, so it is
    ///     refused rather than applied with a warning nobody reads.
    /// </summary>
    [Fact]
    public async Task AModernReleaseThatPublishesNoDigestsIsRefused()
    {
        var zip = BuildPayloadZip();

        using var service = NewService(Handler(zip, checksums: null));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadUpdateAsync(
            Update(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
    }

    /// <summary>…and the same for digests that exist but do not name this asset —
    /// renaming the asset out of the manifest was the cheaper of the two ways
    /// around the old check.</summary>
    [Fact]
    public async Task AModernReleaseWhoseDigestsDoNotListTheAssetIsRefused()
    {
        var zip = BuildPayloadZip();

        using var service = NewService(Handler(zip, $"{Sha256Hex(zip)}  something-else.zip"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadUpdateAsync(
            Update(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("does not list", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "downloads", AssetName)),
            "a rejected asset was left on disk");
    }

    /// <summary>…and an unreachable digest host, which is the state a TLS or DNS
    /// attacker arranges for free.</summary>
    [Fact]
    public async Task AModernReleaseWhoseDigestsAreUnreachableIsRefused()
    {
        var zip = BuildPayloadZip();

        var handler = new FakeHttpMessageHandler
        {
            Fallback = request => request.RequestUri!.ToString().Contains("/releases/tags/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") }
                : Bytes(zip)
        };

        using var service = NewService(handler);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadUpdateAsync(
            Update(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The escape hatch that is gone. Releases before v0.41.0 published
    ///     no digests; an install that verifies signatures only ever updates to a
    ///     NEWER, signed release, so a release with nothing to check is refused
    ///     whatever its version says.
    /// </summary>
    [Fact]
    public async Task APreDigestReleaseIsRefusedNow()
    {
        var zip = BuildPayloadZip();

        var handler = new FakeHttpMessageHandler
        {
            Fallback = request => request.RequestUri!.ToString().Contains("/releases/tags/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") }
                : Bytes(zip)
        };

        using var service = new GitHubUpdateService(
            new Version(0, 39, 0), handler: handler,
            installDir: EnsureInstallDir(), downloadDir: Path.Combine(_dir, "downloads"),
            releaseSigningKey: TestReleaseSigning.PublicKey);

        var update = new UpdateInfo(
            new Version(0, 40, 0), "v0.40.0", "notes", "https://example/r",
            "DotCharter-v0.40.0-win-x64.zip", AssetUrl, AssetUrl, 0);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadUpdateAsync(
            update, cancellationToken: TestContext.Current.CancellationToken));
    }

    // ---- helpers -------------------------------------------------------------

    private GitHubUpdateService NewService(FakeHttpMessageHandler handler)
    {
        return new GitHubUpdateService(
            new Version(0, 47, 0), handler: handler,
            installDir: EnsureInstallDir(), downloadDir: Path.Combine(_dir, "downloads"),
            releaseSigningKey: TestReleaseSigning.PublicKey);
    }

    private string EnsureInstallDir()
    {
        var install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(install);
        return install;
    }

    /// <summary>No <c>ManifestAssetApiUrl</c>, so the partial path declines
    /// immediately and this is the FULL download under test.</summary>
    private static UpdateInfo Update()
        => new(new Version(0, 48, 0), "v0.48.0", "notes", "https://example/r",
            AssetName, AssetUrl, AssetUrl, 0);

    /// <summary>The release as New-Release.ps1 publishes it: digests bound to the
    /// tag and signed (<see cref="FakeReleaseHttp.Serve" />).</summary>
    private static FakeHttpMessageHandler Handler(byte[] asset, string? checksums)
        => FakeReleaseHttp.Serve("v0.48.0", AssetName, asset, checksums: checksums);

    private static HttpResponseMessage Bytes(byte[] payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return response;
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A real zip, because the bounds check ahead of the digest check
    /// reads the end-of-central-directory record off it.</summary>
    private static byte[] BuildPayloadZip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, AppPayload.ExeName, "MZ");
            Add(zip, AppPayload.RuntimeConfigName, SelfContainedConfig);
            Add(zip, AppPayload.DepsName, "{}");
            Add(zip, AppPayload.HostFxrName, "MZ");
            Add(zip, AppPayload.CoreClrName, "MZ");
        }

        return buffer.ToArray();

        static void Add(ZipArchive zip, string name, string content)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
    }
}
