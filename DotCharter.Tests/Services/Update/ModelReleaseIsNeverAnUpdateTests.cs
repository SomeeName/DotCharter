using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
/// #267: stem models are published as PRE-RELEASES of the app's own repository
/// (<c>models-bs-roformer-sw-v1</c>). The updater must never offer one as an app update. Two
/// things keep it so, and both are pinned: it asks GitHub for <c>/releases/latest</c>, which
/// never returns a pre-release; and a <c>models-*</c> tag is not a <c>vX.Y.Z</c> version, so even
/// a model release mistakenly published as a full release could never read as newer.
/// </summary>
public sealed class ModelReleaseIsNeverAnUpdateTests
{
    private const string ModelReleaseJson = """
        {
          "tag_name": "models-bs-roformer-sw-v1",
          "name": "Stem model: BS-RoFormer SW 13 s",
          "html_url": "https://github.com/SomeeName/DotCharter/releases/tag/models-bs-roformer-sw-v1",
          "prerelease": true,
          "body": "",
          "assets": [
            {
              "name": "bs_roformer_sw_6stem_13s_fp16.onnx",
              "url": "https://api.github.com/repos/SomeeName/DotCharter/releases/assets/613781708",
              "browser_download_url": "https://github.com/SomeeName/DotCharter/releases/download/models-bs-roformer-sw-v1/bs_roformer_sw_6stem_13s_fp16.onnx",
              "size": 357936365
            }
          ]
        }
        """;

    [Theory]
    [InlineData("models-bs-roformer-sw-v1")]
    [InlineData("models-anything-v2")]
    public void AModelsTag_IsNotAnAppVersion(string tag) =>
        Assert.Null(GitHubReleaseRules.TryParseVersionFromTag(tag));

    [Fact]
    public async Task TheCheck_AsksForTheLatestFullRelease_WhichExcludesPreReleases()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ModelReleaseJson);
        using var service = new GitHubUpdateService(new Version(0, 60, 0), handler: handler);

        await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://api.github.com/repos/SomeeName/DotCharter/releases/latest",
            Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    /// <summary>Even served as "latest", the model release is no update: no version, no offer,
    /// and nothing downloaded.</summary>
    [Fact]
    public async Task AModelRelease_ServedAsLatest_IsNeverOfferedAsAnUpdate()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(ModelReleaseJson);
        using var service = new GitHubUpdateService(new Version(0, 60, 0), handler: handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Null(result.Update);
        Assert.Single(handler.Requests);
    }
}
