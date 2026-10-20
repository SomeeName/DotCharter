using DotCharter.Services.Update;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     The update feed (#925): the public repo unless a loaded plugin registered its own
///     <see cref="IUpdateFeed" />; the check asks THAT repo; and a feed that needs a sign-in is
///     never asked anonymously - the user is told to sign in instead. The composition half (the
///     app resolves the feed the plugin registered) is <c>UI/UpdateFeedCompositionTests</c>.
/// </summary>
public sealed class UpdateFeedTests
{
    /// <summary>A stand-in for a plugin's private feed; the real coordinates live only in the plugin.</summary>
    private static readonly UpdateFeed AddOnFeed = new("example-owner", "example-addon", RequiresSignIn: true);

    private const string UpToDateRelease = """{"tag_name":"v0.71.0","assets":[]}""";

    [Fact]
    public void Without_an_add_on_feed_the_public_repo_is_read()
    {
        var feed = UpdateFeed.Select([]);

        Assert.Same(UpdateFeed.Public, feed);
        Assert.Equal(GitHubUpdateService.DefaultOwner, feed.Owner);
        Assert.Equal(GitHubUpdateService.DefaultRepo, feed.Repo);
        Assert.False(feed.RequiresSignIn);
    }

    /// <summary>The public repo is read anonymously: no sign-in, so no OAuth client id.</summary>
    [Fact]
    public void The_public_feed_needs_no_sign_in_and_carries_no_client_id()
    {
        Assert.False(UpdateFeed.Public.RequiresSignIn);
        Assert.Null(UpdateFeed.Public.SignInClientId);
        Assert.Null(AddOnFeed.SignInClientId);
        Assert.Equal("Iv23.example", new UpdateFeed("o", "r", true, "Iv23.example").SignInClientId);
    }

    /// <summary>
    ///     The product carries no GitHub OAuth / GitHub App client id: a feed that needs the
    ///     sign-in brings its own (<see cref="IUpdateFeed.SignInClientId" />). GitHub's client ids
    ///     start <c>Iv1.</c>, <c>Iv23</c> or <c>Ov23</c>; no product source may hold one.
    /// </summary>
    [Fact]
    public void No_product_source_holds_a_GitHub_client_id()
    {
        var root = TestPaths.RepoRoot();
        var clientId = new System.Text.RegularExpressions.Regex("\"(Iv1\\.|Iv23|Ov23)[A-Za-z0-9]{6,}\"");
        var scanned = 0;
        var hits = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(root, "DotCharter.*"))
        {
            if (Path.GetFileName(project) == "DotCharter.Tests") continue;
            foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var segments = relative.Split(Path.DirectorySeparatorChar);
                if (segments.Contains("obj") || segments.Contains("bin")) continue;
                scanned++;
                if (clientId.IsMatch(File.ReadAllText(file))) hits.Add(relative);
            }
        }

        Assert.True(scanned > 1000, $"only {scanned} product files scanned: the lint is looking in the wrong place");
        Assert.Empty(hits);
        Assert.Null(typeof(GitHubAuthService).GetField("BuiltInClientId"));
    }

    [Fact]
    public void An_add_on_feed_wins_over_the_public_repo()
    {
        Assert.Same(AddOnFeed, UpdateFeed.Select([AddOnFeed]));
        var second = new UpdateFeed("other-owner", "other-addon");
        Assert.Same(AddOnFeed, UpdateFeed.Select([AddOnFeed, second]));
    }

    [Fact]
    public async Task The_check_asks_the_selected_feed_with_the_sign_in_token()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(UpToDateRelease);
        using var service = GitHubUpdateService.For(AddOnFeed, new Version(0, 71, 0), () => "token-abc", null, handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal(AddOnFeed, service.Feed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/example-owner/example-addon/releases/latest", request.RequestUri!.ToString());
        Assert.Equal("Bearer token-abc", handler.HeaderOf(0, "Authorization"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_private_feed_without_a_sign_in_is_not_asked_and_says_sign_in(bool blankToken)
    {
        var handler = new FakeHttpMessageHandler();
        using var service = GitHubUpdateService.For(
            AddOnFeed, new Version(0, 71, 0), blankToken ? () => "  " : null, null, handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.SignInRequired, result.Status);
        Assert.Contains("sign in to GitHub", result.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_public_feed_is_still_asked_anonymously()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(UpToDateRelease);
        using var service = GitHubUpdateService.For(UpdateFeed.Public, new Version(0, 71, 0), null, null, handler);

        var result = await service.CheckForUpdateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Contains($"/repos/{GitHubUpdateService.DefaultOwner}/{GitHubUpdateService.DefaultRepo}/releases/latest",
            request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Null(handler.HeaderOf(0, "Authorization"));
    }
}
