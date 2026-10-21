using System.Net;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     The real loopback catcher, on a real socket (port 47828, inside the
///     firewall-allowed 47810-47829 range and not the app's own 47821, so a
///     running app does not collide with the test). A request that is not the
///     callback, or that does not satisfy the caller's gate, must be answered and
///     IGNORED: the wait ends only on the request the sign-in is waiting for, or
///     on the timeout.
/// </summary>
public sealed class HttpListenerOAuthCallbackListenerTests
{
    private const string Prefix = "http://localhost:47828/tone3000/callback/";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpStatusCode> GetAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task AStrayRequestDoesNotEndTheWait_TheRightOneDoes()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var wait = new HttpListenerOAuthCallbackListener().WaitForCallbackAsync(
            Prefix, query => query.Contains("state=GOOD", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30), Ct);

        // A wrong state, the right state on another path, and a bare knock: none ends the wait.
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(http, Prefix + "?error=access_denied&state=EVIL"));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(http, Prefix + "x/?code=c&state=GOOD"));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(http, Prefix));
        Assert.False(wait.IsCompleted, "a request that is not the callback ended the sign-in wait");

        Assert.Equal(HttpStatusCode.OK, await GetAsync(http, Prefix + "?code=the-code&state=GOOD"));

        var query = await wait.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("?code=the-code&state=GOOD", query);
    }

    [Fact]
    public async Task OnlyStrayRequests_RunToTheTimeoutAndAnswerNull()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var wait = new HttpListenerOAuthCallbackListener().WaitForCallbackAsync(
            Prefix, query => query.Contains("state=GOOD", StringComparison.Ordinal),
            TimeSpan.FromSeconds(2), Ct);

        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(http, Prefix + "?code=c&state=EVIL"));

        Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(15), Ct));
    }
}
