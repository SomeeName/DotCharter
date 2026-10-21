using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

public sealed class PrereleaseVersionTests
{
    [Theory]
    [InlineData(false, "1.0.0-beta.2", UpdateCheckStatus.UpdateAvailable)]
    [InlineData(true, "1.0.0-beta.2", UpdateCheckStatus.UpdateAvailable)]
    [InlineData(false, "1.0.0", UpdateCheckStatus.UpdateAvailable)]
    [InlineData(true, "1.0.0-beta.1+other", UpdateCheckStatus.UpToDate)]
    [InlineData(false, "1.0.0-alpha", UpdateCheckStatus.UpToDate)]
    public async Task Latest_normal_release_includes_beta_tags_on_both_feeds(bool addOn, string latest, UpdateCheckStatus expected)
    {
        using var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson($$"""
            {"tag_name":"v{{latest}}","prerelease":false,"assets":[
            {"name":"DotCharter-v{{latest}}-win-x64.zip","url":"https://api.github.com/assets/1","size":42}]}
            """);
        var feed = addOn ? new UpdateFeed("example-owner", "example-addon", true) : UpdateFeed.Public;
        using var service = GitHubUpdateService.For(feed, new Version(1, 0, 0, 0), () => "fixture-token", null,
            handler, currentVersionText: "1.0.0-beta.1+local");
        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Equal("1.0.0-beta.1", service.CurrentVersionText);
        Assert.Equal($"https://api.github.com/repos/{feed.Owner}/{feed.Repo}/releases/latest",
            Assert.Single(handler.Requests).RequestUri!.ToString());
        if (expected == UpdateCheckStatus.UpdateAvailable) Assert.Equal("v" + latest, result.Update!.TagName);
    }

    [Fact]
    public async Task Download_uses_the_beta_tag_for_checksums_and_keeps_the_payload_version()
    {
        const string version = "1.0.0-beta.2";
        const string asset = "DotCharter-v1.0.0-beta.2-win-x64.zip";
        var zip = FakeRelease.Zip(FakeRelease.HostFiles(version), asset[..^4]);
        using var handler = FakeReleaseHttp.Serve("v" + version, asset, zip,
            checksums: FakeReleaseHttp.Digests((asset, zip)));
        using var scratch = new TempDirectory();
        using var service = new GitHubUpdateService(new Version(1, 0, 0), handler: handler,
            installDir: scratch.Path, downloadDir: scratch.Path, currentVersionText: "1.0.0-beta.1", releaseSigningKey: TestReleaseSigning.PublicKey);
        var update = new UpdateInfo(new Version(1, 0, 0), "v" + version, "", "", asset,
            FakeReleaseHttp.AssetUrl, FakeReleaseHttp.AssetUrl, zip.Length);
        var downloaded = await service.DownloadUpdateAsync(update, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(asset, Path.GetFileName(downloaded));
        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/releases/tags/v" + version, StringComparison.Ordinal));
        var manifest = FakeRelease.Manifest(version, FakeRelease.HostFiles(version));
        Assert.Equal(version, PayloadManifest.FromJson(manifest.ToJson())!.Version);
    }

    [Theory]
    [InlineData("1.0.0-beta.1", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    [InlineData("1.0.0-beta.10", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    [InlineData("1.0.0-A", "1.0.0-a")]
    [InlineData("1.0.0-beta.99999999999999999999", "1.0.0-beta.100000000000000000000")]
    public void Tags_follow_semver_precedence(string lower, string higher)
    {
        var a = GitHubReleaseRules.TryParseVersionFromTag(lower)!;
        var b = GitHubReleaseRules.TryParseVersionFromTag(higher)!;
        Assert.True(GitHubReleaseRules.CompareVersions(a, b) < 0);
        Assert.True(GitHubReleaseRules.CompareVersions(b, a) > 0);
        Assert.Equal(0, GitHubReleaseRules.CompareVersions(a, a));
    }

    [Fact]
    public void Parsing_preserves_prerelease_and_ignores_build_metadata()
    {
        var version = GitHubReleaseRules.TryParseVersionFromTag("v1.0.0-beta.1+build.007");
        Assert.Equal("1.0.0-beta.1", version?.ToString());
    }

    [Theory]
    [InlineData("1.0.0-beta.1+first", "1.0.0-beta.1+second")]
    [InlineData("1.0.0+001", "1.0.0+build.9")]
    public void Build_metadata_has_no_precedence(string a, string b) =>
        Assert.Equal(0, GitHubReleaseRules.CompareVersions(a, b));

    [Theory]
    [InlineData("v1.0.0-")]
    [InlineData("v1.0.0+")]
    [InlineData("v1.0.0-beta..1")]
    [InlineData("v1.0.0-beta.01")]
    [InlineData("v01.0.0")]
    [InlineData("v1.0")]
    [InlineData("v1.0.0.1")]
    [InlineData("v1.0.0-béta")]
    [InlineData("v1.0.0+build..1")]
    [InlineData("v1.0.0+build+other")]
    public void Malformed_tags_are_rejected(string tag) => Assert.Null(GitHubReleaseRules.TryParseVersionFromTag(tag));
}
