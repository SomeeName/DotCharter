using System.IO.Compression;
using System.Net;
using System.Text;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Services.Audio.Lyrics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Ai.Model;

/// <summary>
///     The download service end to end over a FAKE transport: a stub
///     <see cref="HttpMessageHandler" /> that answers the release listing and
///     the asset body. No network, no real asset, no GitHub.
///     <para>
///         <b>Exercising the real fetcher rather than a delegate is the point.</b>
///         The rig lane reuses <see cref="LyricModelAssetFetcher" /> rather than
///         forking it, and the one piece of hard-won knowledge that fetcher
///         carries — that the release scan must PAGE, because a model asset sits
///         on a release a hundred behind HEAD — is invisible from a
///         <c>Func&lt;string, CancellationToken, Task&lt;Stream&gt;&gt;</c> stub. A
///         fork would have re-broken the download the same way and reported it
///         as "the model is not available".
///     </para>
/// </summary>
public sealed class RigEstimatorModelDownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dc-rigdl-{Guid.NewGuid():N}");

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>
    ///     Serves N pages of releases; the named asset is on the LAST one, which
    ///     is the real-world shape (the model is published once and the app
    ///     releases past it). A request for the asset url returns the archive.
    /// </summary>
    private sealed class StubGitHub(string assetName, byte[] archive, int pages) : HttpMessageHandler
    {
        public const string AssetUrl = "https://api.github.com/repos/o/r/releases/assets/1";

        public int ListingRequests { get; private set; }
        public int AssetRequests { get; private set; }
        public string? SeenAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // A real transport observes the token; one that does not turns a
            // cancelled download into "the asset does not exist".
            cancellationToken.ThrowIfCancellationRequested();

            SeenAuthorization ??= request.Headers.TryGetValues("Authorization", out var v)
                ? string.Join("", v)
                : null;

            var url = request.RequestUri!.ToString();
            if (url == AssetUrl)
            {
                AssetRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archive)
                });
            }

            ListingRequests++;
            // "&page=", not "page=": the listing url also carries "per_page=100"
            // and a loose match parsed THAT, so every request looked like page 100.
            var marker = url.IndexOf("&page=", StringComparison.Ordinal);
            var page = marker < 0
                ? 1
                : int.Parse(new string(url[(marker + 6)..].TakeWhile(char.IsDigit).ToArray()));

            var json = page < pages
                ? """[{"assets":[{"name":"something-else.zip","url":"https://example/x"}]}]"""
                : page == pages
                    ? $$"""[{"assets":[{"name":"{{assetName}}","url":"{{AssetUrl}}"}]}]"""
                    : "[]";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static byte[] ModelArchive(string sidecarJson = """{"export":{"modelId":"m","checkpointEpoch":7}}""")
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "model.onnx", "onnx");
            Write(zip, "model.sidecar.json", sidecarJson);
            // Past the installer's minimum-plausible-size floor.
            Write(zip, "pad.bin", Pad(2 * 1024 * 1024));
        }

        return buffer.ToArray();

        static void Write(ZipArchive zip, string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());
            writer.Write(content);
        }

        static string Pad(int bytes)
        {
            var random = new Random(99);
            var builder = new StringBuilder(bytes);
            for (var i = 0; i < bytes; i++) builder.Append((char)random.Next(33, 126));
            return builder.ToString();
        }
    }

    // The synthetic archive is zipped at test time (entry timestamps move), so
    // its digest is not a constant: the tests take the catalogue entry with the
    // hash cleared and exercise the size-checked path. The published hash itself
    // is pinned by RigEstimatorModelCatalogTests, and a WRONG hash being refused
    // is pinned by RigEstimatorModelInstallerTests.
    private static RigEstimatorModelDownload SyntheticDownload { get; } =
        RigEstimatorModelCatalog.Download with { Sha256 = "" };

    private RigEstimatorModelDownloadService Service(StubGitHub handler, Func<string?>? token = null) =>
        new(
            new LyricModelAssetFetcher(token, new FakeAppLogger(), handler, owner: "o", repo: "r"),
            FakeLocalization.English(),
            new FakeAppLogger(),
            _root,
            SyntheticDownload);

    [Fact]
    public async Task ItFetchesTheCataloguedAssetAndReportsInstalled()
    {
        var handler = new StubGitHub(
            RigEstimatorModelCatalog.Download.AssetName, ModelArchive(), pages: 1);
        using var service = Service(handler);

        Assert.False(service.IsInstalled);

        var steps = new List<string>();
        var result = await service.EnsureInstalledAsync(
            new Progress<string>(steps.Add), TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.True(service.IsInstalled);
        Assert.Equal(1, handler.AssetRequests);
    }

    /// <summary>
    ///     The paging behaviour this lane INHERITS by reusing the lyric fetcher.
    ///     A single-page scan finds nothing once the model's release is old, and
    ///     the failure reads as "no such asset" — which is what made this
    ///     worth asserting from the rig side too.
    /// </summary>
    [Fact]
    public async Task AnAssetOnAnOldReleaseIsStillFound()
    {
        var handler = new StubGitHub(
            RigEstimatorModelCatalog.Download.AssetName, ModelArchive(), pages: 3);
        using var service = Service(handler);

        var result = await service.EnsureInstalledAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.Equal(3, handler.ListingRequests);
    }

    /// <summary>Token handling is the lyric path's, unchanged: a supplied token
    /// becomes a Bearer header, and a null supplier sends none.</summary>
    [Theory]
    [InlineData("gh-token-123", "Bearer gh-token-123")]
    [InlineData(null, null)]
    public async Task TheTokenIsPassedThroughExactlyAsTheLyricPathDoesIt(string? token, string? expected)
    {
        var handler = new StubGitHub(
            RigEstimatorModelCatalog.Download.AssetName, ModelArchive(), pages: 1);
        using var service = Service(handler, () => token);

        await service.EnsureInstalledAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(expected, handler.SeenAuthorization);
    }

    /// <summary>
    ///     No release carries the asset — the state of the world until the model
    ///     is published. It must be a REASON, not a throw: the settings page and
    ///     the tone panel both call this from a button.
    /// </summary>
    [Fact]
    public async Task AMissingAssetIsAReasonNotAThrow()
    {
        var handler = new StubGitHub("some-other-model.zip", ModelArchive(), pages: 1);
        using var service = Service(handler);

        var result = await service.EnsureInstalledAsync(ct: TestContext.Current.CancellationToken);

        Assert.False(result.Installed);
        Assert.NotNull(result.Reason);
        Assert.False(service.IsInstalled);
    }

    // ---- the tone library (2026-09-14) -------------------------------------------

    private static byte[] LibraryArchive()
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry(
                       RigEstimatorModelCatalog.LibraryFileName, CompressionLevel.NoCompression).Open()))
            {
                writer.Write("DRIL");
            }

            using (var writer = new StreamWriter(zip.CreateEntry("pad.bin", CompressionLevel.NoCompression).Open()))
            {
                var random = new Random(98);
                for (var i = 0; i < 2 * 1024 * 1024; i++) writer.Write((char)random.Next(33, 126));
            }
        }

        return buffer.ToArray();
    }

    private static RigEstimatorModelDownload SyntheticLibrary { get; } =
        RigEstimatorModelCatalog.Library with { Sha256 = "" };

    /// <summary>
    ///     The library goes through the SAME fetcher and installer as the model —
    ///     its own asset name, its own folder, its own file — and the path the
    ///     service reports is exactly where the matcher's provider will look.
    /// </summary>
    [Fact]
    public async Task ItFetchesTheLibraryAssetIntoItsOwnFolder()
    {
        var handler = new StubGitHub(RigEstimatorModelCatalog.Library.AssetName, LibraryArchive(), pages: 2);
        using var service = new RigEstimatorModelDownloadService(
            new LyricModelAssetFetcher(null, new FakeAppLogger(), handler, owner: "o", repo: "r"),
            FakeLocalization.English(),
            new FakeAppLogger(), _root, SyntheticDownload, SyntheticLibrary);

        Assert.False(service.IsLibraryInstalled);
        Assert.False(service.IsInstalled);
        Assert.Equal(Path.Combine(_root, "rig-library", RigEstimatorModelCatalog.LibraryFileName), service.LibraryPath);
        Assert.Equal((int)(RigEstimatorModelCatalog.Library.ApproximateBytes / (1024 * 1024)),
            service.LibraryApproximateMegabytes);

        var result = await service.EnsureLibraryInstalledAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(result.Installed, result.Reason);
        Assert.True(service.IsLibraryInstalled);
        Assert.True(File.Exists(service.LibraryPath));
        Assert.False(service.IsInstalled, "installing the library must not read as installing the model");
        Assert.Equal(1, handler.AssetRequests);
    }

    [Fact]
    public async Task TheInstalledModelDescribesItself()
    {
        var handler = new StubGitHub(
            RigEstimatorModelCatalog.Download.AssetName,
            ModelArchive("""{"export":{"modelId":"rig-estimator-clap-v2","checkpointEpoch":19}}"""),
            pages: 1);
        using var service = Service(handler);

        Assert.Null(service.InstalledModel);

        await service.EnsureInstalledAsync(ct: TestContext.Current.CancellationToken);

        var info = service.InstalledModel;
        Assert.NotNull(info);
        Assert.Equal("rig-estimator-clap-v2", info.ModelId);
        Assert.Equal(19, info.CheckpointEpoch);
    }

    /// <summary>
    ///     The service's directory is the one the estimator reads, so the UI's
    ///     "open folder" link and the estimator cannot point at two places.
    /// </summary>
    [Fact]
    public void ItsDirectoryIsTheModelDirectory()
    {
        using var service = new RigEstimatorModelDownloadService(
            new LyricModelAssetFetcher(null, new FakeAppLogger(),
                new StubGitHub("x", [], 1)),
            FakeLocalization.English());

        Assert.Equal(RigEstimatorOnnx.DefaultModelDirectory, service.ModelDirectory);
        Assert.Equal(
            (int)(RigEstimatorModelCatalog.Download.ApproximateBytes / (1024 * 1024)),
            service.ApproximateMegabytes);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var handler = new StubGitHub(
            RigEstimatorModelCatalog.Download.AssetName, ModelArchive(), pages: 1);
        using var service = Service(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.EnsureInstalledAsync(ct: cts.Token));
    }
}
