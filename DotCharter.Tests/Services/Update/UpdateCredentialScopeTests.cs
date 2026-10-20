using System.IO.Compression;
using System.Net;
using System.Text;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The updater attached the signed-in user's GitHub token to whatever url it
///     was handed, and those urls are DATA: the asset's <c>url</c> and
///     <c>browser_download_url</c> are read out of the release JSON. `SocketsHttpHandler`
///     strips <c>Authorization</c> across a cross-host redirect, which sounds like
///     the same protection and is not — a url that simply IS somewhere else was
///     never redirected, so nothing stripped anything, and a private-repo token
///     with `Contents: Read` (or a classic `repo` scope) went to that host in a
///     header.
///     <para>
///         <b>And the allow-list gated only the HEADER, which was the smaller half
///         (2026-09-04).</b> Nothing stopped the app DOWNLOADING from that host —
///         a doctored release document could point the asset url anywhere and the
///         only backstop was a digest check the same document could defeat. The
///         predicate is <c>IsAllowedReleaseHost</c> now and
///         <c>CreateRequest</c> refuses outright.
///     </para>
/// </summary>
public sealed class UpdateCredentialScopeTests
{
    [Theory]
    [InlineData("https://api.github.com/repos/o/r/releases/latest")]
    [InlineData("https://github.com/o/r/releases/download/v1/a.zip")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2")]
    [InlineData("https://release-assets.githubusercontent.com/releases/assets/9")]
    public void AGitHubHttpsUrlIsAllowed(string url)
        => Assert.True(GitHubReleaseRules.IsAllowedReleaseHost(url));

    [Theory]
    [InlineData("http://api.github.com/repos/o/r/releases/latest")]   // cleartext
    [InlineData("https://api.github.com.evil.example/repos/o/r")]     // suffix, not the host
    [InlineData("https://evil.example/api.github.com/x")]             // path, not the host
    [InlineData("https://raw.githack.com/o/r/a.zip")]
    [InlineData("file:///C:/Windows/System32/config")]
    [InlineData("not a url")]
    [InlineData("")]
    public void AnythingElseIsNot(string url)
        => Assert.False(GitHubReleaseRules.IsAllowedReleaseHost(url));

    /// <summary>The allowlist is a list of HOSTS. A prefix or "contains" test is
    /// exactly the bug this replaces, so pin that the match is exact (bar the
    /// deliberate githubusercontent.com subdomain rule, which GitHub shards asset
    /// delivery across).</summary>
    [Fact]
    public void TheAllowlistIsMatchedAsAHostNotAsASubstring()
    {
        foreach (var host in GitHubReleaseRules.AllowedHosts)
        {
            Assert.True(GitHubReleaseRules.IsAllowedReleaseHost($"https://{host}/x"));
            Assert.False(GitHubReleaseRules.IsAllowedReleaseHost($"https://{host}.evil.example/x"));

            // `evil-<host>` is a different host — except under
            // githubusercontent.com, where the subdomain rule is deliberate:
            // GitHub owns the whole zone and shards asset delivery across it.
            if (!host.EndsWith("githubusercontent.com", StringComparison.Ordinal))
                Assert.False(GitHubReleaseRules.IsAllowedReleaseHost($"https://evil-{host}/x"));
        }
    }

    /// <summary>
    ///     …and the predicate has to be WIRED, which is the half a pure test cannot
    ///     see. The download url is whatever the release JSON's asset record says,
    ///     so this drives a real download whose asset url is at a foreign host and
    ///     asserts the app never talks to it AT ALL — not merely that the token was
    ///     withheld, which is what it asserted until 2026-09-04.
    /// </summary>
    [Fact]
    public async Task AnAssetUrlOutsideTheAllowedHostsIsRefusedOutright()
    {
        var dir = Directory.CreateTempSubdirectory("dotcharter-cred-scope-").FullName;
        try
        {
            var handler = new FakeHttpMessageHandler
            {
                Fallback = _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(BuildPayloadZip())
                }
            };

            using var service = new GitHubUpdateService(
                new Version(0, 47, 0), tokenProvider: () => "ghp_secret", handler: handler,
                installDir: dir, downloadDir: dir);

            var update = new UpdateInfo(
                new Version(0, 48, 0), "v0.48.0", "notes", "https://example/r",
                "DotCharter-v0.48.0-win-x64.zip",
                "https://evil.example/assets/1", "https://evil.example/assets/1", 0);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadUpdateAsync(
                update, cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("not an https GitHub release host", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host == "evil.example");
            Assert.Empty(Directory.GetFiles(dir, "*.zip"));
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    /// <summary>
    ///     The control, so the test above is not passing because the token provider
    ///     is simply ignored: an ALLOWED host in the same shape does carry the
    ///     header, on the release lookup and on the asset read alike.
    /// </summary>
    [Fact]
    public async Task AnAllowedHostStillCarriesTheToken()
    {
        var dir = Directory.CreateTempSubdirectory("dotcharter-cred-scope-ok-").FullName;
        try
        {
            var zip = BuildPayloadZip();
            const string assetName = "DotCharter-v0.40.0-win-x64.zip";
            var handler = FakeReleaseHttp.Serve(
                "v0.40.0", assetName, zip,
                checksums: FakeReleaseHttp.Digests((assetName, zip)));

            using var service = new GitHubUpdateService(
                new Version(0, 39, 0), tokenProvider: () => "ghp_secret", handler: handler,
                installDir: dir, downloadDir: dir);

            var update = new UpdateInfo(
                new Version(0, 40, 0), "v0.40.0", "notes", "https://example/r",
                assetName, FakeReleaseHttp.AssetUrl, FakeReleaseHttp.AssetUrl, zip.Length);

            await service.DownloadUpdateAsync(update, cancellationToken: TestContext.Current.CancellationToken);

            var toGitHub = handler.Requests.FindIndex(r => r.RequestUri!.Host == "api.github.com");
            Assert.True(toGitHub >= 0);
            Assert.Equal("Bearer ghp_secret", handler.HeaderOf(toGitHub, "Authorization"));
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    private static byte[] BuildPayloadZip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry(AppPayload.ExeName).Open();
            entry.Write(Encoding.UTF8.GetBytes("MZ"));
        }

        return buffer.ToArray();
    }
}
