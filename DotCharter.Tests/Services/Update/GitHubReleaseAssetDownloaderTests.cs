using System.Net;
using System.Security.Cryptography;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Tests.Services.Update;

/// <summary>
/// #267: the 13 s BS-RoFormer SW is a release asset of the PRIVATE repository, so its download
/// goes the updater's way: the signed-in user's token as a bearer header, the release read BY
/// TAG, the api.github.com asset endpoint with <c>Accept: application/octet-stream</c>, a host
/// allow-list in front of every request, and the pinned size + SHA-256 checked before the file
/// lands. Driven over a scripted handler, so every request is inspected.
/// </summary>
public sealed class GitHubReleaseAssetDownloaderTests : IDisposable
{
    private const string Token = "ghp_test_token_never_logged";
    private const string AssetApiUrl = "https://api.github.com/repos/SomeeName/DotCharter/releases/assets/613781708";
    private static readonly GitHubReleaseAssetRef Asset = new("models-bs-roformer-sw-v1", "model.onnx");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharter_ReleaseAsset_" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _bytes = RandomNumberGenerator.GetBytes(200_000);

    public GitHubReleaseAssetDownloaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Sha => Convert.ToHexStringLower(SHA256.HashData(_bytes));

    private string Dest => Path.Combine(_dir, "model.onnx");

    private static string ReleaseJson(long size, string url = AssetApiUrl) => $$"""
        {
          "tag_name": "models-bs-roformer-sw-v1",
          "prerelease": true,
          "assets": [
            {
              "name": "model.onnx",
              "url": "{{url}}",
              "browser_download_url": "https://github.com/SomeeName/DotCharter/releases/download/models-bs-roformer-sw-v1/model.onnx",
              "size": {{size}}
            }
          ]
        }
        """;

    private static GitHubReleaseAssetDownloader Downloader(FakeHttpMessageHandler handler, string? token = Token) =>
        new(() => token, handler: handler, appVersion: new Version(0, 61, 0));

    /// <summary>The request shape: release by tag (JSON, bearer, API version), then the asset's
    /// API url as octet-stream with the same bearer. The verified file lands; no part file stays.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASignedInDownload_ReadsTheReleaseByTag_ThenTheAssetEndpoint_WithTheToken(bool runningVersion)
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length));
        handler.EnqueueBytes(_bytes);
        using var downloader = runningVersion
            ? new GitHubReleaseAssetDownloader(() => Token, handler: handler)
            : Downloader(handler);

        var path = await downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct);

        Assert.Equal(Dest, path);
        Assert.Equal(_bytes, await File.ReadAllBytesAsync(Dest, Ct));
        Assert.False(File.Exists(GitHubReleaseAssetDownloader.PartialPathFor(Dest)));
        Assert.Equal(2, handler.Requests.Count);

        Assert.Equal("https://api.github.com/repos/SomeeName/DotCharter/releases/tags/models-bs-roformer-sw-v1",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("application/vnd.github+json", handler.HeaderOf(0, "Accept"));
        Assert.Equal("Bearer " + Token, handler.HeaderOf(0, "Authorization"));
        Assert.Equal("2022-11-28", handler.HeaderOf(0, "X-GitHub-Api-Version"));
        Assert.StartsWith(runningVersion ? "DotCharter/1.1.0," : "DotCharter/0.61.0",
            handler.HeaderOf(0, "User-Agent"), StringComparison.Ordinal);

        Assert.Equal(AssetApiUrl, handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("application/octet-stream", handler.HeaderOf(1, "Accept"));
        Assert.Equal("Bearer " + Token, handler.HeaderOf(1, "Authorization"));
        Assert.Null(handler.Requests[1].Headers.Range);
    }

    [Fact]
    public async Task Progress_ReportsBytesAgainstThePinnedSize()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length));
        handler.EnqueueBytes(_bytes);
        using var downloader = Downloader(handler);
        var reports = new List<DownloadProgress>();

        await downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, new SyncProgress(reports.Add), Ct);

        Assert.NotEmpty(reports);
        Assert.All(reports, r => Assert.Equal(_bytes.Length, r.TotalBytes));
        Assert.Equal(_bytes.Length, reports[^1].BytesReceived);
    }

    /// <summary>Signed out: nothing is requested at all (an anonymous request is a 404 on a
    /// private repository), and the caller is told why.</summary>
    [Fact]
    public async Task SignedOut_RequestsNothing_AndSaysTheSignInIsMissing()
    {
        var handler = new FakeHttpMessageHandler();
        using var downloader = Downloader(handler, token: null);

        Assert.False(downloader.IsSignedIn);
        await Assert.ThrowsAsync<GitHubSignInRequiredException>(() =>
            downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct));
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(Dest));
    }

    /// <summary>The pin decides, not the release: bytes that hash differently are discarded and
    /// never reach the destination.</summary>
    [Fact]
    public async Task BytesThatDoNotHashToThePin_AreDiscarded()
    {
        var handler = new FakeHttpMessageHandler();
        var tampered = (byte[])_bytes.Clone();
        tampered[1234] ^= 0xFF;
        handler.EnqueueJson(ReleaseJson(_bytes.Length));
        handler.EnqueueBytes(tampered);
        using var downloader = Downloader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct));
        Assert.False(File.Exists(Dest));
        Assert.False(File.Exists(GitHubReleaseAssetDownloader.PartialPathFor(Dest)));
    }

    /// <summary>A re-uploaded asset of another size is a different file: refused from the
    /// release document, before a byte of it is requested.</summary>
    [Fact]
    public async Task AReleaseDeclaringAnotherSize_IsRefusedBeforeTheAssetIsFetched()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length + 1));
        using var downloader = Downloader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct));
        Assert.Single(handler.Requests);
    }

    /// <summary>The asset url is a claim the release document makes: a url off GitHub is
    /// refused, so the token never travels there.</summary>
    [Fact]
    public async Task AnAssetUrlOffGitHub_IsRefused_AndGetsNoToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length, "https://evil.example/model.onnx"));
        using var downloader = Downloader(handler);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepositoryTheTokenCannotSee_FailsWithTheStatus_AndNoToken()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.NotFound);
        using var downloader = Downloader(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>An interrupted 358 MB download resumes: the part file's length goes out as a
    /// Range, and a 206 continues it.</summary>
    [Fact]
    public async Task APartialFile_Resumes_WithARangeRequest()
    {
        await File.WriteAllBytesAsync(GitHubReleaseAssetDownloader.PartialPathFor(Dest), _bytes[..80_000], Ct);
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length));
        handler.ServeAsset(_bytes);
        using var downloader = Downloader(handler);

        await downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct);

        Assert.Equal(_bytes, await File.ReadAllBytesAsync(Dest, Ct));
        var range = Assert.Single(handler.Requests[1].Headers.Range!.Ranges);
        Assert.Equal(80_000, range.From);
        Assert.Null(range.To);
    }

    /// <summary>A server that ignores the Range sends the whole file: it is written from byte
    /// 0, never appended to the part file.</summary>
    [Fact]
    public async Task AServerIgnoringTheRange_StartsOver_RatherThanAppending()
    {
        await File.WriteAllBytesAsync(GitHubReleaseAssetDownloader.PartialPathFor(Dest), _bytes[..80_000], Ct);
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length));
        handler.ServeAsset(_bytes, honourRanges: false);
        using var downloader = Downloader(handler);

        await downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct);

        Assert.Equal(_bytes, await File.ReadAllBytesAsync(Dest, Ct));
    }

    [Fact]
    public async Task ABodyLongerThanThePin_IsRefused()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length));
        handler.EnqueueBytes([.. _bytes, 1, 2, 3]);
        using var downloader = Downloader(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct));
        Assert.False(File.Exists(Dest));
    }

    /// <summary>An installed file that verifies costs no request; one that does not is
    /// downloaded again rather than trusted.</summary>
    [Fact]
    public async Task AnInstalledFile_IsHashed_NotTrusted()
    {
        await File.WriteAllBytesAsync(Dest, _bytes, Ct);
        var idle = new FakeHttpMessageHandler();
        using (var downloader = Downloader(idle))
            await downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct);
        Assert.Empty(idle.Requests);

        await File.WriteAllBytesAsync(Dest, new byte[_bytes.Length], Ct);
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length));
        handler.EnqueueBytes(_bytes);
        using (var downloader = Downloader(handler))
            await downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(_bytes, await File.ReadAllBytesAsync(Dest, Ct));
    }

    /// <summary>The app's downloader follows the app's sign-in: a sign-in or sign-out on the
    /// auth service reaches every list of models.</summary>
    /// <summary>A public repository's asset downloads without a sign-in: no token, no
    /// Authorization header, and the file still has to match its pin.</summary>
    [Fact]
    public async Task APublicRepository_DownloadsAnonymously()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ReleaseJson(_bytes.Length));
        handler.EnqueueBytes(_bytes);
        using var downloader = new GitHubReleaseAssetDownloader(() => null, handler: handler, requiresSignIn: false);

        Assert.False(downloader.RequiresSignIn);
        await downloader.DownloadVerifiedAsync(Asset, Dest, Sha, _bytes.Length, null, Ct);

        Assert.Equal(_bytes, await File.ReadAllBytesAsync(Dest, Ct));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.HeaderOf(0, "Authorization"));
        Assert.Null(handler.HeaderOf(1, "Authorization"));
    }

    /// <summary>The app's downloader reads the PUBLIC repo, so it never waits for a sign-in the
    /// public build does not offer.</summary>
    [Fact]
    public void TheAppsDownloader_NeedsNoSignIn()
    {
        using var downloader = GitHubReleaseAssetDownloader.For(new SwitchableAuth(), null, null);

        Assert.False(downloader.RequiresSignIn);
        Assert.False(downloader.IsSignedIn);
    }

    [Fact]
    public void TheAppsDownloader_RaisesSignInChanged_WithTheAuthService()
    {
        var auth = new SwitchableAuth();
        using var downloader = GitHubReleaseAssetDownloader.For(auth, null, null);
        var raised = 0;
        downloader.SignInChanged += () => raised++;

        Assert.False(downloader.IsSignedIn);
        auth.Token = Token;
        auth.Raise();

        Assert.True(downloader.IsSignedIn);
        Assert.Equal(1, raised);
    }

    /// <summary>The updater and the model download build their GitHub requests in ONE place,
    /// so the rule that keeps the token on GitHub cannot drift between them.</summary>
    [Fact]
    public void TheUpdater_AndTheModelDownload_ShareOneRequestFactory()
    {
        foreach (var file in new[] { "GitHubUpdateService.cs", "GitHubReleaseAssetDownloader.cs" })
        {
            var src = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Update", file));
            Assert.Contains("GitHubApiRequest.Create(", src, StringComparison.Ordinal);
            Assert.Contains("GitHubApiRequest.CreateClient(", src, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Authorization\"", src, StringComparison.Ordinal);
        }
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed class SwitchableAuth : IGitHubAuthService
    {
        public string? Token { get; set; }

        public bool IsSignedIn => Token != null;

        public event Action? AuthStateChanged;

        public void Raise() => AuthStateChanged?.Invoke();

        public string? GetToken() => Token;

        public Task<GitHubDeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> WaitForDeviceAuthorizationAsync(string clientId, GitHubDeviceCode deviceCode,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void SignInWithPersonalAccessToken(string token) => throw new NotSupportedException();

        public void SignOut() => throw new NotSupportedException();

        public Task<string?> GetSignedInLoginAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
