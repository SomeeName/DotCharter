using System.Net;
using System.Text;
using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio.Lyrics;

/// <summary>
///     Resolving a lyric-model archive to a GitHub release asset.
///     <para>
///         <b>The paging is the whole point of this file.</b> The models are
///         published ONCE under their own asset names so that later releases need
///         not re-upload 400 MB of unchanged weights — and that design only works
///         if the scan reaches the release carrying them. It did not: the search
///         was one page of 30 while <c>lyric-align-v1.zip</c> sat on
///         <c>v0.41.0</c>, more than a hundred releases back, so lyric auto-sync
///         could not fetch its own model at all and said only "the model is not
///         available". Nothing covered the lookup, because the pure
///         <c>FindAssetUrl</c> was tested over a single page that always
///         contained the answer.
///     </para>
/// </summary>
public sealed class LyricModelAssetFetcherTests
{
    private const string Asset = "lyric-align-v1.zip";

    /// <summary>A listing page with <paramref name="count" /> releases, none of them the asset.</summary>
    private static string PageWithout(int count) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(i =>
            $$"""{"tag_name":"v0.{{i}}.0","assets":[{"name":"DotCharter-win-x64.zip","url":"u{{i}}"}]}""")) + "]";

    private static string PageWith(string url) =>
        $$"""[{"tag_name":"v0.41.0","assets":[{"name":"{{Asset}}","url":"{{url}}"}]}]""";

    /// <summary>Answers each listing request from a queue and records the URLs it saw.</summary>
    private sealed class ListingHandler(params string[] pages) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.Query);

            var index = Requested.Count - 1;
            var body = index < pages.Length ? pages[index] : "[]";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static async Task<Exception?> FetchAsync(ListingHandler handler)
    {
        using var fetcher = new LyricModelAssetFetcher(handler: handler);
        try
        {
            await using var stream = await fetcher
                .FetchAsync(Asset, TestContext.Current.CancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    ///     <b>The regression.</b> An asset on a release beyond the first page has
    ///     to be found. This fails outright against a single-page scan: the fetch
    ///     never gets past listing, and the request log shows exactly one page.
    /// </summary>
    [Fact]
    public async Task AnAssetOnALaterPageIsStillFound()
    {
        var handler = new ListingHandler(PageWithout(100), PageWith("https://api.example/asset/1"));

        // The download itself then 200s with our JSON body — what matters here is
        // that resolution got far enough to ASK for the asset at all.
        var error = await FetchAsync(handler);

        Assert.Null(error);
        Assert.True(handler.Requested.Count >= 3,
            $"expected two listing pages and the asset fetch, saw {handler.Requested.Count}.");
        Assert.Contains("page=1", handler.Requested[0], StringComparison.Ordinal);
        Assert.Contains("page=2", handler.Requested[1], StringComparison.Ordinal);
    }

    /// <summary>
    ///     A hit on the first page costs exactly one listing request — the walk
    ///     must not scan five pages for an asset it has already found.
    /// </summary>
    [Fact]
    public async Task AnAssetOnTheFirstPageCostsOneListing()
    {
        var handler = new ListingHandler(PageWith("https://api.example/asset/1"));

        var error = await FetchAsync(handler);

        Assert.Null(error);
        Assert.Equal(2, handler.Requested.Count);
    }

    /// <summary>
    ///     …and an EMPTY page ends the walk. A young repository must not burn five
    ///     requests, and the rate limit, listing nothing.
    /// </summary>
    [Fact]
    public async Task AnEmptyPageStopsTheWalk()
    {
        var handler = new ListingHandler(PageWithout(3), "[]");

        var error = await FetchAsync(handler);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(2, handler.Requested.Count);
    }

    /// <summary>The page-end test, as the walk itself reads it.</summary>
    [Theory]
    [InlineData("[]", true)]
    [InlineData("not json", true)]
    [InlineData("{}", true)]
    [InlineData("""[{"tag_name":"v1"}]""", false)]
    public void AnEmptyOrUnusablePageReadsAsTheEndOfTheList(string json, bool expected)
        => Assert.Equal(expected, LyricModelAssetFetcher.IsEmptyPage(json));

    /// <summary>The pure asset lookup: newest first, exact name, case-insensitive.</summary>
    [Fact]
    public void TheAssetLookupTakesTheFirstExactNameMatch()
    {
        var json = """
            [
              {"assets":[{"name":"other.zip","url":"wrong"}]},
              {"assets":[{"name":"LYRIC-ALIGN-V1.ZIP","url":"right"}]},
              {"assets":[{"name":"lyric-align-v1.zip","url":"older"}]}
            ]
            """;

        Assert.Equal("right", LyricModelAssetFetcher.FindAssetUrl(json, Asset));
        Assert.Null(LyricModelAssetFetcher.FindAssetUrl(json, "lyric-align-v2.zip"));
    }
}
