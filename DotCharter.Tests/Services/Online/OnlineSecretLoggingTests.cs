using System.Net;
using System.Text;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
///     No credential the clients under <c>Online/</c> handle may reach the log
///     sink — not an OAuth access or refresh token, not a client secret, not an
///     <c>Authorization</c> header value.
///     <para>
///         The reason it is a TEST and not a code review: the log line that
///         leaks is never the one that prints a token on purpose. It is a
///         catch-all that interpolates <c>ex.Message</c>, or a diagnostic that
///         echoes a request URL, or a "rejected — here is what the server said"
///         line that turns out to quote the request back. Every one of those is
///         added by someone fixing an unrelated bug, and none of them look
///         wrong. So each client is driven through its real failure paths with
///         distinctive secrets and the WHOLE log is searched.
///     </para>
///     <para>
///         The secrets below are deliberately long, unique and free of
///         substrings that occur naturally, so a hit is unambiguous and a near
///         miss cannot mask a real leak.
///     </para>
/// </summary>
public sealed class OnlineSecretLoggingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AccessToken = "ACCESSTOKEN-zzqqxx-9f3c1d7b40aa";
    private const string RefreshToken = "REFRESHTOKEN-zzqqxx-51bd08e2c6f4";
    private const string Password = "PASSWORD-zzqqxx-hunter2-correct-horse";
    private const string AnonKey = "ANONKEY-zzqqxx-FAKE-anon-0000";
    private const string ClientId = "CLIENTID-zzqqxx-3f7a2b91";

    private static readonly string[] EverySecret =
        [AccessToken, RefreshToken, Password, AnonKey, ClientId];

    /// <summary>Fails naming the offending line — a bare "false" here would send
    /// the next reader hunting through five clients.</summary>
    private static void AssertNothingLeaked(FakeAppLogger log)
    {
        foreach (var entry in log.Entries)
        {
            foreach (var secret in EverySecret)
            {
                Assert.False(entry.Message.Contains(secret, StringComparison.OrdinalIgnoreCase),
                $"A secret reached the log sink.\n  level: {entry.Level}\n  line:  {entry.Message}");
            }
        }

        // …and the guard is only meaningful if the run actually logged. A client
        // that logged NOTHING would pass this test while leaking after one edit.
        Assert.NotEmpty(log.Entries);
    }

    // ── TONE3000: the OAuth client ───────────────────────────────────────────

    [Fact]
    public async Task Tone3000Client_LogsSearchAndDownloadWithoutTheBearerToken()
    {
        var log = new FakeAppLogger();
        // The rejection branch is the interesting one: it quotes the server's
        // own message AND the request URL into a WARNING, with a token attached
        // to the request.
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                $$"""{ "error": "your token {{AccessToken}} is malformed" }""",
                Encoding.UTF8, "application/json")
        });

        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>(AccessToken),
            logger: log, handler: handler);

        var search = await client.SearchAsync(new Tone3000SearchQuery("plexi"), Ct);
        await client.GetModelsAsync(41, Ct);
        using var dest = new MemoryStream();
        await client.DownloadModelAsync(
            new Tone3000Model(9, 41, "Standard", "nam", "standard", "wavenet",
                "https://cdn.tone3000.com/models/9.nam", 0),
            dest, maxBytes: 1024, ct: Ct);

        AssertNothingLeaked(log);
        // The reason survives the scrub — it is what makes a 400 diagnosable.
        Assert.True(log.HasEntryContaining("WARN", "is malformed"));
        Assert.Contains("is malformed", search.Message, StringComparison.Ordinal);
        // …and the typed message the UI shows is scrubbed too: a token on
        // screen is no better than a token in a file.
        Assert.DoesNotContain(AccessToken, search.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A rejection quotes the server's message on purpose (it is the one
    ///     line the owner can act on). That makes the SERVER an untrusted
    ///     source of log content — this pins that a server which echoes the
    ///     caller's token back still does not get it written to disk.
    /// </summary>
    [Fact]
    public async Task Tone3000Client_ServerEchoingTheTokenBack_DoesNotGetItLogged()
    {
        var log = new FakeAppLogger();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                $$"""{ "error": "rejected", "message": "bearer {{AccessToken}}" }""",
                Encoding.UTF8, "application/json")
        });
        using var client = new Tone3000Client(
            accessTokenProvider: _ => Task.FromResult<string?>(AccessToken), logger: log, handler: handler);

        var result = await client.SearchAsync(new Tone3000SearchQuery("plexi"), Ct);

        Assert.Equal(OnlineError.NotSignedIn, result.Error);
        AssertNothingLeaked(log);
    }

    [Fact]
    public async Task Tone3000AuthService_SignInAndRefresh_NeverLogTokensOrTheClientId()
    {
        var log = new FakeAppLogger();
        var config = new AppConfig { Tone3000ClientId = ClientId };
        var store = new InMemoryTone3000TokenStore();

        // Sign-in: the authorize URL carries the client id and the callback the
        // authorization code, and the token response carries both tokens.
        var handler = new ScriptedHandler(request =>
            request.RequestUri!.AbsoluteUri.EndsWith("/user", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "id": "u1", "username": "capturist" }""",
                        Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                          { "access_token": "{{AccessToken}}",
                            "refresh_token": "{{RefreshToken}}", "expires_in": 3600 }
                          """, Encoding.UTF8, "application/json")
                });

        // The service starts LISTENING before it opens the browser (so the
        // redirect can never race an unbound port), so the fake listener has to
        // wait for the authorize URL before it can echo the matching state back.
        var browserOpened = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openedUrl = string.Empty;
        using var auth = new Tone3000AuthService(
            () => config, store, log, handler,
            openBrowser: url =>
            {
                openedUrl = url;
                browserOpened.TrySetResult(url);
                return true;
            },
            listener: new ScriptedCallbackListener(async () =>
                "?code=AUTHCODE-zzqqxx&state=" + StateOf(await browserOpened.Task)));

        var signIn = await auth.SignInAsync(Ct);
        Assert.True(signIn.Ok);

        AssertNothingLeaked(log);
        // The browser URL legitimately contains the client id (that is what a
        // public OAuth client id is FOR) — it just must not be logged.
        Assert.Contains(ClientId, Uri.UnescapeDataString(openedUrl), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tone3000AuthService_RefreshRejected_LogsTheFailureWithoutTheRefreshToken()
    {
        var log = new FakeAppLogger();
        var config = new AppConfig { Tone3000ClientId = ClientId };
        var store = new InMemoryTone3000TokenStore();
        store.Save(new Tone3000SessionData(
            AccessToken, RefreshToken, DateTimeOffset.UtcNow.AddMinutes(-5), "u1", "capturist"));

        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{ "error": "that refresh token is revoked" }""",
                Encoding.UTF8, "application/json")
        });
        using var auth = new Tone3000AuthService(() => config, store, log, handler);

        var token = await auth.GetAccessTokenAsync(Ct);

        Assert.Null(token); // expired + refused ⇒ signed out
        AssertNothingLeaked(log);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static string StateOf(string authorizeUrl)
    {
        var marker = "&state=";
        var idx = authorizeUrl.IndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? string.Empty : authorizeUrl[(idx + marker.Length)..];
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class ScriptedCallbackListener(Func<Task<string?>> query) : IOAuthCallbackListener
    {
        public Task<string?> WaitForCallbackAsync(string prefix, TimeSpan timeout, CancellationToken ct) =>
            query();
    }
}
