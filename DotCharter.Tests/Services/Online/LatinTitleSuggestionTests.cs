using System.Net;
using System.Text;
using DotCharter.Services.Online.MusicBrainz;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     "Use official names" filled the artist and left the song title alone. MusicBrainz lists one
///     song as several recordings: the top hit carries the original-script title and a twin (or an
///     English alias) carries the Latin one, and the lookup only ever read the top hit's own title.
///     These pin the whole lookup over such a result: the title comes from the twin or the alias,
///     and a result whose title is already Latin still fills it.
/// </summary>
public sealed class LatinTitleSuggestionTests
{
    private const string Artist = "ヨルシカ";
    private const string Title = "八月、某、月明かり";

    private const string ArtistJson = """
        {"artists":[{"name":"ヨルシカ","sort-name":"Yorushika","score":100,
          "aliases":[{"name":"Yorushika","locale":"en","primary":true}]}]}
        """;

    private static async Task<LatinNameSuggestion> Lookup(string recordingsJson)
    {
        using var client = new MusicBrainzClient(handler: new Router(recordingsJson), delay: (_, _) => Task.CompletedTask);
        var result = await client.FindLatinNamesAsync(Artist, Title, "", CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        return result.Value!;
    }

    [Fact]
    public async Task TheLatinTitleOfATwinRecording_FillsTheTitle()
    {
        var suggestion = await Lookup("""
            {"recordings":[
              {"id":"r1","title":"八月、某、月明かり","score":100,"artist-credit":[{"name":"ヨルシカ"}],
               "releases":[{"id":"rel1","title":"だから僕は音楽を辞めた"}]},
              {"id":"r2","title":"August, A Certain, Moonlight","score":100,"artist-credit":[{"name":"ヨルシカ"}]}
            ]}
            """);

        Assert.Equal("Yorushika", suggestion.Artist);
        Assert.Equal("August, A Certain, Moonlight", suggestion.Title);
    }

    [Fact]
    public async Task AnEnglishAliasOfTheTopHit_FillsTheTitle()
    {
        var suggestion = await Lookup("""
            {"recordings":[
              {"id":"r1","title":"八月、某、月明かり","score":100,"artist-credit":[{"name":"ヨルシカ"}],
               "aliases":[{"name":"Hachigatsu, Nanigashi, Tsukiakari","locale":"en"},{"name":"八月某月明かり","locale":"ja"}]}
            ]}
            """);

        Assert.Equal("Hachigatsu, Nanigashi, Tsukiakari", suggestion.Title);
    }

    [Fact]
    public async Task ATopHitWhoseTitleIsAlreadyLatin_StillFillsTheTitle()
    {
        var suggestion = await Lookup("""
            {"recordings":[
              {"id":"r1","title":"August, A Certain, Moonlight","score":100,"artist-credit":[{"name":"Yorushika"}]}
            ]}
            """);

        Assert.Equal("August, A Certain, Moonlight", suggestion.Title);
    }

    [Fact]
    public async Task ALowScoringLatinRecording_DoesNotRenameTheSong()
    {
        // A weak text match is somebody else's song, not this one's English name.
        using var client = new MusicBrainzClient(
            handler: new Router("""
                {"recordings":[
                  {"id":"r1","title":"八月、某、月明かり","score":100,"artist-credit":[{"name":"ヨルシカ"}]},
                  {"id":"r2","title":"Moonlight Sonata","score":40,"artist-credit":[{"name":"ヨルシカ"}]}
                ]}
                """),
            delay: (_, _) => Task.CompletedTask);

        var result = await client.FindLatinNamesAsync(Artist, Title, "", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("", result.Value!.Title);
    }

    /// <summary>Answers the artist search and the recording search with canned JSON.</summary>
    private sealed class Router(string recordings) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/artist", StringComparison.Ordinal) ? ArtistJson : recordings;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
