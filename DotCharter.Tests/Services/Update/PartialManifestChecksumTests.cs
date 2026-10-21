using System.Net;
using System.Text;
using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     <para>
///         The partial download hash-verifies every range-fetched file against
///         <c>payload-manifest.json</c> — and until 2026-09-04 nothing verified the
///         MANIFEST. It is the document that decides which bytes land on disk and
///         what each one must hash to, so trusting it unverified made every
///         per-file digest below it circular: a doctored manifest simply declares
///         the digests of whatever it wants written.
///     </para>
///     <para>
///         <c>New-Release.ps1</c> has digested it into <c>SHA256SUMS.txt</c> since
///         0.41 (that file's header comment says "digests of assets 1-2"), and the
///         FULL asset path has cross-checked itself against the same document since
///         0.48 (<see cref="FullAssetChecksumTests" />). This closes the asymmetry.
///         Anything the partial path cannot verify becomes the FULL download, which
///         is where the fail-closed policy lives.
///     </para>
/// </summary>
public sealed class PartialManifestChecksumTests : IDisposable
{
    private const string AssetName = "DotCharter-v0.43.0-win-x64.zip";

    private readonly TempDirectory _temp = new();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedManifestStopsReadingAtItsOwnLimit(bool declaresLength)
    {
        var release = BuildRelease();
        var body = new UnseekableMemoryStream(new byte[GitHubUpdateService.MaxPayloadManifestBytes + 65536]);
        var handler = Serve(release, FakeReleaseHttp.Digests((AssetName, release.Zip)));
        var inner = handler.Fallback!;
        handler.Fallback = request =>
        {
            if (request.RequestUri!.ToString() != FakeReleaseHttp.ManifestUrl) return inner(request);
            var content = new StreamContent(body);
            if (declaresLength) content.Headers.ContentLength = body.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        using var service = NewService(handler);

        var path = await service.DownloadUpdateAsync(release.Update,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AssetName, Path.GetFileName(path));
        Assert.InRange(body.BytesRead, 0,
            declaresLength ? 0 : GitHubUpdateService.MaxPayloadManifestBytes + 1);
    }

    private sealed class UnseekableMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public long BytesRead { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && CanRead) BytesRead = Position;
            base.Dispose(disposing);
        }
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>
    ///     The happy path, and the proof the new check is not simply refusing
    ///     everything: a manifest whose digest is the published one still takes the
    ///     partial download, which is a fraction of the asset.
    /// </summary>
    [Fact]
    public async Task AManifestThatMatchesTheReleaseDigestStillTakesThePartialDownload()
    {
        var release = BuildRelease();

        using var service = NewService(Serve(release,
            FakeReleaseHttp.Digests(
                (AssetName, release.Zip),
                (PayloadManifest.FileName, release.ManifestBytes))));

        var path = await service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GitHubReleaseRules.PartialAssetName(new Version(0, 43, 0)), Path.GetFileName(path));
        Assert.True(new FileInfo(path).Length < release.Zip.Length / 4,
            $"assembled payload is {new FileInfo(path).Length} of {release.Zip.Length}");
    }

    /// <summary>
    ///     The refusal. The release digests this file and the bytes are somebody
    ///     else's, so the partial path is abandoned and the FULL asset is taken —
    ///     which is a refusal, not a downgrade, because that path digests itself.
    /// </summary>
    [Fact]
    public async Task AManifestThatDisagreesWithTheReleaseDigestFallsBackToTheFullDownload()
    {
        var release = BuildRelease();

        using var service = NewService(Serve(release,
            $"{new string('a', 64)}  {PayloadManifest.FileName}\n" +
            $"{FakeRelease.Sha256(release.Zip)}  {AssetName}"));

        var path = await service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AssetName, Path.GetFileName(path));
        Assert.Equal(release.Zip.Length, new FileInfo(path).Length);
    }

    /// <summary>
    ///     …and when the full asset ALSO fails its own digest check the update is
    ///     refused outright, with nothing left on disk. This is what makes the
    ///     fallback above safe to describe as a refusal.
    /// </summary>
    [Fact]
    public async Task AMismatchingManifestOnAReleaseWhoseAssetAlsoMismatches_RefusesTheUpdate()
    {
        var release = BuildRelease();
        var wrong = new string('a', 64);

        using var service = NewService(Serve(release,
            $"{wrong}  {PayloadManifest.FileName}\n{wrong}  {AssetName}"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("not the build it claims to be", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "downloads", AssetName)),
            "a rejected asset was left on disk");
    }

    /// <summary>
    ///     Digests that do not name the manifest are not a refusal on their own —
    ///     they mean the partial path has nothing to check, so it hands over to the
    ///     full download, which the same digests DO describe.
    /// </summary>
    [Fact]
    public async Task DigestsThatDoNotListTheManifestFallBackToTheVerifiedFullDownload()
    {
        var release = BuildRelease();

        using var service = NewService(Serve(release,
            FakeReleaseHttp.Digests((AssetName, release.Zip))));

        var path = await service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AssetName, Path.GetFileName(path));
    }

    /// <summary>
    ///     The manifest GET is BOUNDED. It used to pass <c>maxBytes: null</c>,
    ///     leaving <c>MaxResponseContentBufferSize</c> as the only ceiling on a body
    ///     buffered whole off a host we do not control. An over-declared manifest is
    ///     not read, so the partial path declines and the verified full download
    ///     runs.
    /// </summary>
    [Fact]
    public async Task AManifestThatDeclaresMoreThanTheCeilingIsRefused()
    {
        var release = BuildRelease();

        var handler = Serve(release,
            FakeReleaseHttp.Digests(
                (AssetName, release.Zip),
                (PayloadManifest.FileName, release.ManifestBytes)));

        var inner = handler.Fallback!;
        handler.Fallback = request =>
        {
            if (request.RequestUri!.ToString() != FakeReleaseHttp.ManifestUrl) return inner(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(release.ManifestBytes)
            };
            response.Content.Headers.ContentLength = GitHubUpdateService.MaxPayloadManifestBytes + 1;
            return response;
        };

        using var service = NewService(handler);

        var path = await service.DownloadUpdateAsync(
            release.Update, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(AssetName, Path.GetFileName(path));
    }

    // ---- helpers -------------------------------------------------------------

    private GitHubUpdateService NewService(FakeHttpMessageHandler handler)
    {
        return new GitHubUpdateService(
            new Version(0, 42, 0), handler: handler,
            installDir: Path.Combine(_temp.Path, "install"),
            downloadDir: Path.Combine(_temp.Path, "downloads"), releaseSigningKey: TestReleaseSigning.PublicKey);
    }

    private static FakeHttpMessageHandler Serve(ReleaseFixture release, string checksums)
        => FakeReleaseHttp.Serve("v0.43.0", AssetName, release.Zip, release.ManifestBytes, checksums);

    /// <summary>
    ///     The same shape <c>GitHubUpdateServiceTests.BuildReleasePayload</c>
    ///     builds: an install one version behind, with one changed file and one
    ///     4 MB file that did not change, so a real partial download is far
    ///     smaller than the asset.
    /// </summary>
    private ReleaseFixture BuildRelease()
    {
        var installDir = Path.Combine(_temp.Path, "install");
        Directory.CreateDirectory(installDir);

        var files = FakeRelease.HostFiles("0.43.0");
        files["app.dll"] = FakeRelease.Content("app-new", 3000);
        files["runtime/big.dll"] = FakeRelease.Content("runtime", 4_000_000);

        var manifest = FakeRelease.Manifest("0.43.0", files);
        var zip = FakeRelease.Zip(files, "DotCharter-v0.43.0-win-x64");

        var installedFiles = new Dictionary<string, byte[]>(files, StringComparer.OrdinalIgnoreCase)
        {
            ["app.dll"] = FakeRelease.Content("app-old", 2500),
        };
        foreach (var (name, content) in installedFiles)
        {
            var target = Path.Combine(installDir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }

        File.WriteAllText(Path.Combine(installDir, PayloadManifest.FileName),
            FakeRelease.Manifest("0.42.0", installedFiles).ToJson());

        var update = new UpdateInfo(
            new Version(0, 43, 0), "v0.43.0", "notes", "https://example/r",
            AssetName, FakeReleaseHttp.AssetUrl, FakeReleaseHttp.AssetUrl, zip.Length,
            FakeReleaseHttp.ManifestUrl);

        return new ReleaseFixture(zip, Encoding.UTF8.GetBytes(manifest.ToJson()), update);
    }

    private sealed record ReleaseFixture(byte[] Zip, byte[] ManifestBytes, UpdateInfo Update);
}
