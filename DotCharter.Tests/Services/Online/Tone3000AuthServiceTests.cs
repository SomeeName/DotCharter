using System.Net;
using System.Text;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tests.Services.Online;

/// <summary>
/// Tests for the TONE3000 OAuth pieces: the pure PKCE/URL/callback helpers
/// (<see cref="Tone3000OAuth" />, incl. the RFC 7636 S256 vector) and the
/// <see cref="Tone3000AuthService" /> flow against a scripted handler, a fake
/// browser and a fake loopback listener — no sockets, no real browser.
/// </summary>
public sealed class Tone3000AuthServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InvalidatingSessionClearsStoredCredentialsAndNotifiesOnce()
    {
        var store = new InMemoryTone3000TokenStore();
        store.Save(new Tone3000SessionData("at", "rt", DateTimeOffset.MaxValue, "u", "name"));
        using var service = NewService(new ScriptedHandler((_, _) => Json("{}")), store);
        Assert.True(service.IsSignedIn);
        var changes = 0;
        service.AuthStateChanged += () => changes++;

        service.InvalidateSession("unauthorized");
        service.InvalidateSession("unauthorized");

        Assert.False(service.IsSignedIn);
        Assert.Null(store.Load());
        Assert.Null(await service.GetAccessTokenAsync(Ct));
        Assert.Equal(1, changes);
    }

    // ── pure OAuth helpers ────────────────────────────────────────────────────

    [Fact]
    public void CodeChallenge_MatchesRfc7636Vector()
    {
        // Appendix B of RFC 7636.
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Tone3000OAuth.ComputeCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void CodeVerifier_IsUnreservedAndLongEnough()
    {
        var verifier = Tone3000OAuth.CreateCodeVerifier();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.All(verifier, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~', $"bad char '{c}'"));
        Assert.NotEqual(verifier, Tone3000OAuth.CreateCodeVerifier());
    }

    [Fact]
    public void AuthorizeUrl_CarriesAllPkceParameters()
    {
        var url = Tone3000OAuth.BuildAuthorizeUrl("client-1", "challenge-x", "state-y");

        Assert.StartsWith("https://www.tone3000.com/api/v1/oauth/authorize?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=client-1", url);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A47821%2Ftone3000%2Fcallback%2F", url);
        Assert.Contains("code_challenge=challenge-x", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=state-y", url);
    }

    [Fact]
    public void CallbackPort_IsInsideTheFirewallAllowedRange() => Assert.InRange(Tone3000OAuth.CallbackPort, 47810, 47829);

    [Fact]
    public void ParseCallback_HappyPath_ReturnsCode()
    {
        var code = Tone3000OAuth.TryParseCallback("?code=abc123&state=st", "st", out var error);
        Assert.Equal("abc123", code);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("?code=abc&state=WRONG", "state")]      // CSRF / stray request
    [InlineData("?error=access_denied&state=st", "access_denied")]
    [InlineData("?state=st", "no authorization code")]
    [InlineData(null, "state")]                          // empty query
    // A stranger's ?error= is a STATE failure, not the provider's answer: the
    // loopback listener answers whatever knocks on localhost:47821, and any page
    // the user has open can knock. Reading `error` before checking `state` let
    // one such request kill a sign-in that was going fine.
    [InlineData("?error=access_denied&state=WRONG", "state")]
    [InlineData("?error=access_denied", "state")]        // no state at all
    public void ParseCallback_Rejections_ReturnNullWithError(string? query, string expectedFragment)
    {
        var code = Tone3000OAuth.TryParseCallback(query, "st", out var error);
        Assert.Null(code);
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    // ── auth service flow ─────────────────────────────────────────────────────

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;
        public readonly List<(HttpRequestMessage Request, string Body)> Requests = [];

        public ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) =>
            _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return _respond(request, body);
        }
    }

    private sealed class FakeListener : IOAuthCallbackListener
    {
        private readonly Func<Task<string?>> _query;
        public string? Prefix;
        public Func<string, bool>? Accept;
        public CancellationToken LastToken;

        public FakeListener(Func<Task<string?>> query) => _query = query;

        public Task<string?> WaitForCallbackAsync(
            string prefix, Func<string, bool> accept, TimeSpan timeout, CancellationToken ct)
        {
            Prefix = prefix;
            Accept = accept;
            LastToken = ct;
            return _query();
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Tone3000AuthService NewService(
        ScriptedHandler handler,
        InMemoryTone3000TokenStore store,
        Func<string>? callbackQuery = null,
        List<string>? openedUrls = null,
        string clientId = "cid-1",
        Func<DateTimeOffset>? nowUtc = null,
        bool browserOpens = true,
        Action<FakeListener>? listenerOut = null)
    {
        // The fake listener replays the state the browser URL carried (once the
        // browser HAS been opened), so the round-trip exercises the real state
        // check without racing the listener-before-browser ordering.
        var browserOpened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var urls = openedUrls ?? [];
        return new Tone3000AuthService(
            () => new AppConfig { Tone3000ClientId = clientId },
            store,
            handler: handler,
            openBrowser: url =>
            {
                urls.Add(url);
                if (!browserOpens) return false;
                browserOpened.TrySetResult(url);
                return true;
            },
            listener: Capture(new FakeListener(callbackQuery != null
                ? () => Task.FromResult<string?>(callbackQuery())
                : async () =>
                {
                    var url = await browserOpened.Task;
                    var state = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query).Get("state");
                    return $"?code=auth-code-1&state={state}";
                }), listenerOut),
            nowUtc: nowUtc);
    }

    private static FakeListener Capture(FakeListener listener, Action<FakeListener>? sink)
    {
        sink?.Invoke(listener);
        return listener;
    }

    [Fact]
    public async Task SignIn_NotConfigured_FailsTyped()
    {
        var store = new InMemoryTone3000TokenStore();
        using var service = NewService(
            new ScriptedHandler((_, _) => Json("{}")), store, clientId: "");

        var result = await service.SignInAsync(Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.NotConfigured, result.Error);
        Assert.False(service.IsConfigured);
    }

    /// <summary>
    ///     The loopback listener must not end the wait on a stray request: the
    ///     sign-in hands it a gate that admits only a query carrying THIS flow's
    ///     state (the one in the authorize URL), so any page that knocks on the
    ///     port can neither complete nor abort the sign-in.
    /// </summary>
    [Fact]
    public async Task SignIn_GatesTheListenerOnItsOwnState()
    {
        var urls = new List<string>();
        FakeListener? listener = null;
        using var service = NewService(new ScriptedHandler((_, _) => Json("{}")),
            new InMemoryTone3000TokenStore(), openedUrls: urls, browserOpens: false, listenerOut: l => listener = l);

        await service.SignInAsync(Ct);

        var state = System.Web.HttpUtility.ParseQueryString(new Uri(urls.Single()).Query).Get("state")!;
        Assert.NotNull(listener?.Accept);
        Assert.True(listener.Accept($"?code=c&state={Uri.EscapeDataString(state)}"));
        Assert.True(listener.Accept($"?error=access_denied&state={Uri.EscapeDataString(state)}"));
        Assert.False(listener.Accept("?code=c&state=someone-elses"));
        Assert.False(listener.Accept("?error=access_denied"));
        Assert.False(listener.Accept(""));
    }

    [Theory]
    [InlineData("?code=c&state=abc", "abc", true)]
    [InlineData("code=c&state=abc", "abc", true)]
    [InlineData("?state=abc", "abc", true)]
    [InlineData("?code=c&state=abd", "abc", false)]
    [InlineData("?code=c&state=ABC", "abc", false)]
    [InlineData("?code=c", "abc", false)]
    [InlineData("", "abc", false)]
    [InlineData(null, "abc", false)]
    public void CarriesState_AdmitsOnlyTheExpectedState(string? query, string expected, bool carries)
        => Assert.Equal(carries, Tone3000OAuth.CarriesState(query, expected));

    /// <summary>
    ///     Network audit P2-12: when the browser could not be opened, the
    ///     method returned and ABANDONED the listener's wait — the HttpListener
    ///     then held port 47821 for the whole 5-minute callback timeout, so a
    ///     retry inside that window found its own port taken. The listener's
    ///     token is the method's own and is cancelled on that exit.
    /// </summary>
    [Fact]
    public async Task SignIn_BrowserFailsToOpen_ReleasesTheCallbackListener()
    {
        var store = new InMemoryTone3000TokenStore();
        FakeListener? listener = null;
        using var service = NewService(new ScriptedHandler((_, _) => Json("{}")), store,
            browserOpens: false, listenerOut: l => listener = l);

        var result = await service.SignInAsync(Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.InvalidRequest, result.Error);
        Assert.NotNull(listener);
        Assert.True(listener!.LastToken.IsCancellationRequested,
            "the abandoned callback wait must be cancelled, or the loopback port stays held for the timeout");
    }

    [Fact]
    public async Task SignIn_HappyPath_ExchangesCode_PersistsSession_RaisesEvent()
    {
        var store = new InMemoryTone3000TokenStore();
        var handler = new ScriptedHandler((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
                ? Json("""{ "access_token": "at-1", "refresh_token": "rt-1", "expires_in": 3600 }""")
                : Json("""{ "user": { "id": "u-9", "username": "capturist" } }"""));
        var opened = new List<string>();
        using var service = NewService(handler, store, openedUrls: opened);
        var events = 0;
        service.AuthStateChanged += () => events++;

        var result = await service.SignInAsync(Ct);

        Assert.True(result.Ok);
        Assert.Equal("capturist", result.Value!.UserName);
        Assert.True(service.IsSignedIn);
        Assert.Equal("capturist", service.UserName);
        Assert.Equal(1, events);

        // The persisted session carries both tokens.
        var session = store.Load()!;
        Assert.Equal("at-1", session.AccessToken);
        Assert.Equal("rt-1", session.RefreshToken);

        // The browser URL carried PKCE, and the exchange carried the verifier
        // whose S256 hash matches the challenge sent to the browser.
        var authorizeUrl = Assert.Single(opened);
        var challenge = System.Web.HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query)
            .Get("code_challenge");
        var tokenBody = handler.Requests
            .Single(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/oauth/token")).Body;
        var verifier = System.Web.HttpUtility.ParseQueryString(tokenBody).Get("code_verifier");
        Assert.Equal(challenge, Tone3000OAuth.ComputeCodeChallenge(verifier!));
        Assert.Contains("grant_type=authorization_code", tokenBody);
        Assert.Contains("code=auth-code-1", tokenBody);

        Assert.Equal("at-1", await service.GetAccessTokenAsync(Ct));
    }

    [Fact]
    public async Task SignIn_StateMismatch_FailsUnauthorized_AndStoresNothing()
    {
        var store = new InMemoryTone3000TokenStore();
        var handler = new ScriptedHandler((_, _) => Json("{}"));
        using var service = NewService(handler, store,
            callbackQuery: () => "?code=stolen&state=FORGED");

        var result = await service.SignInAsync(Ct);

        Assert.False(result.Ok);
        Assert.Equal(OnlineError.Unauthorized, result.Error);
        Assert.Null(store.Load());
        Assert.Empty(handler.Requests); // never exchanged the forged code
    }

    [Fact]
    public async Task GetAccessToken_Expired_RefreshesAndPersists()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTone3000TokenStore();
        store.Save(new Tone3000SessionData("old-at", "rt-1", now - TimeSpan.FromMinutes(1), "u", "name"));
        var handler = new ScriptedHandler((_, _) =>
            Json("""{ "access_token": "new-at", "expires_in": 3600 }"""));
        using var service = NewService(handler, store, nowUtc: () => now);

        var token = await service.GetAccessTokenAsync(Ct);

        Assert.Equal("new-at", token);
        var body = handler.Requests.Single().Body;
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains("refresh_token=rt-1", body);
        // Refresh omitted a new refresh token → the old one is kept.
        var session = store.Load()!;
        Assert.Equal("rt-1", session.RefreshToken);
        Assert.Equal("new-at", session.AccessToken);
    }

    [Fact]
    public async Task GetAccessToken_RefreshRejected_SignsOut()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTone3000TokenStore();
        store.Save(new Tone3000SessionData("old-at", "rt-1", now - TimeSpan.FromMinutes(1), "u", "name"));
        var handler = new ScriptedHandler((_, _) =>
            Json("""{ "error": "invalid_grant" }""", HttpStatusCode.BadRequest));
        using var service = NewService(handler, store, nowUtc: () => now);

        var token = await service.GetAccessTokenAsync(Ct);

        Assert.Null(token);
        Assert.False(service.IsSignedIn);
        Assert.Null(store.Load());
    }

    /// <summary>
    ///     A refresh that could not be COMPLETED is not a refresh that was
    ///     REFUSED. A 5xx (or a 429, or a body we cannot parse) says nothing
    ///     about the refresh token, and signing out over it destroys the DPAPI
    ///     session — a full browser sign-in the user cannot get back by retrying,
    ///     paid for one bad minute on the far side. Only 400/401 (RFC 6749 §5.2),
    ///     or a 200 carrying an OAuth <c>error</c>, is the provider rejecting the
    ///     grant; <see cref="GetAccessToken_RefreshRejected_SignsOut" /> is that
    ///     half.
    /// </summary>
    [Theory]
    [InlineData(503, "{}")]                            // the far side is down
    [InlineData(429, """{ "error": "slow_down" }""")]  // rate limited; the token is fine
    [InlineData(500, """{ "error": "boom" }""")]
    [InlineData(200, "not json at all")]               // a captive portal's error page
    public async Task GetAccessToken_RefreshThatCouldNotBeCompleted_KeepsTheSession(
        int status, string body)
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTone3000TokenStore();
        store.Save(new Tone3000SessionData("old-at", "rt-1", now - TimeSpan.FromMinutes(1), "u", "name"));
        var handler = new ScriptedHandler((_, _) => Json(body, (HttpStatusCode)status));
        using var service = NewService(handler, store, nowUtc: () => now);

        var token = await service.GetAccessTokenAsync(Ct);

        Assert.Null(token);                 // no usable token right now…
        Assert.True(service.IsSignedIn);    // …but still signed in
        var kept = store.Load();
        Assert.NotNull(kept);
        Assert.Equal("rt-1", kept!.RefreshToken);
    }

    /// <summary>…and because the session survived, the very next call heals.</summary>
    [Fact]
    public async Task GetAccessToken_AfterATransientRefreshFailure_TheNextCallSucceeds()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTone3000TokenStore();
        store.Save(new Tone3000SessionData("old-at", "rt-1", now - TimeSpan.FromMinutes(1), "u", "name"));
        var calls = 0;
        var handler = new ScriptedHandler((_, _) => ++calls == 1
            ? Json("{}", HttpStatusCode.ServiceUnavailable)
            : Json("""{ "access_token": "new-at", "expires_in": 3600 }"""));
        using var service = NewService(handler, store, nowUtc: () => now);

        Assert.Null(await service.GetAccessTokenAsync(Ct));
        Assert.Equal("new-at", await service.GetAccessTokenAsync(Ct));
        Assert.Equal("new-at", store.Load()!.AccessToken);
    }

    [Fact]
    public async Task GetAccessToken_StillValid_NoNetworkCall()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var store = new InMemoryTone3000TokenStore();
        store.Save(new Tone3000SessionData("at-1", "rt-1", now + TimeSpan.FromHours(1), "u", "name"));
        var handler = new ScriptedHandler((_, _) => Json("{}"));
        using var service = NewService(handler, store, nowUtc: () => now);

        Assert.Equal("at-1", await service.GetAccessTokenAsync(Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SignOut_ClearsSession_AndTokenAccess()
    {
        var store = new InMemoryTone3000TokenStore();
        store.Save(new Tone3000SessionData("at", "rt", DateTimeOffset.MaxValue, "u", "name"));
        using var service = NewService(new ScriptedHandler((_, _) => Json("{}")), store);
        Assert.True(service.IsSignedIn);

        service.SignOut();

        Assert.False(service.IsSignedIn);
        Assert.Null(store.Load());
        Assert.Null(await service.GetAccessTokenAsync(Ct));
    }
}
