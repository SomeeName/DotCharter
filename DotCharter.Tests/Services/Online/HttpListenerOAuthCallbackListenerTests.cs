using System.Net;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     The real loopback catcher, on a real socket. Each test takes the first free
///     port of the firewall-allowed 47810-47829 range other than the app's own
///     47821, so neither a running app nor a concurrent test process (a second
///     gate, a merge train) holds the port it binds. A request that is not the
///     callback, or that does not satisfy the caller's gate, must be answered and
///     IGNORED: the wait ends only on the request the sign-in is waiting for, or
///     on the timeout.
/// </summary>
public sealed class HttpListenerOAuthCallbackListenerTests
{
    private const int FirstPort = 47810;
    private const int LastPort = 47829;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpStatusCode> GetAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url, Ct);
        return response.StatusCode;
    }

    /// <summary>
    ///     Starts the listener on the first free port of the range, never the production
    ///     port. The listener binds synchronously, so a port another process holds
    ///     faults the returned task before it yields and the next port is tried; the
    ///     scan starts at a random offset so concurrent processes rarely meet.
    /// </summary>
    private static (string Prefix, Task<string?> Wait) Listen(Func<string, bool> accept, TimeSpan timeout)
    {
        var count = LastPort - FirstPort + 1;
        var offset = Random.Shared.Next(count);
        for (var i = 0; i < count; i++)
        {
            var port = FirstPort + (offset + i) % count;
            if (port == Tone3000OAuth.CallbackPort) continue;

            var prefix = $"http://localhost:{port}/tone3000/callback/";
            var wait = new HttpListenerOAuthCallbackListener().WaitForCallbackAsync(prefix, accept, timeout, Ct);
            if (wait.IsFaulted && wait.Exception!.InnerException is HttpListenerException) continue;
            return (prefix, wait);
        }

        throw new InvalidOperationException($"no free loopback port in {FirstPort}-{LastPort}");
    }

    [Fact]
    public async Task AStrayRequestDoesNotEndTheWait_TheRightOneDoes()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var (prefix, wait) = Listen(
            query => query.Contains("state=GOOD", StringComparison.Ordinal), TimeSpan.FromSeconds(30));

        // A wrong state, the right state on another path, and a bare knock: none ends the wait.
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(http, prefix + "?error=access_denied&state=EVIL"));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(http, prefix + "x/?code=c&state=GOOD"));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(http, prefix));
        Assert.False(wait.IsCompleted, "a request that is not the callback ended the sign-in wait");

        Assert.Equal(HttpStatusCode.OK, await GetAsync(http, prefix + "?code=the-code&state=GOOD"));

        var query = await wait.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal("?code=the-code&state=GOOD", query);
    }

    [Fact]
    public async Task OnlyStrayRequests_RunToTheTimeoutAndAnswerNull()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var (prefix, wait) = Listen(
            query => query.Contains("state=GOOD", StringComparison.Ordinal), TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.NotFound, await GetAsync(http, prefix + "?code=c&state=EVIL"));

        Assert.Null(await wait.WaitAsync(TimeSpan.FromSeconds(60), Ct));
    }
}
