using System.Net;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

public sealed class GitHubAuthServiceTests : IDisposable
{
    private const string DeviceCodeJson = """
        {
          "device_code": "dev-code-123",
          "user_code": "ABCD-1234",
          "verification_uri": "https://github.com/login/device",
          "expires_in": 900,
          "interval": 5
        }
        """;

    private readonly string _dir;
    private readonly List<TimeSpan> _delays = [];

    public GitHubAuthServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dc-auth-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private GitHubAuthService CreateService(FakeHttpMessageHandler handler)
    {
        return new GitHubAuthService(
            new GitHubTokenStore(_dir),
            handler: handler,
            delay: (span, _) =>
            {
                _delays.Add(span);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task StartDeviceFlow_ParsesCodesIntervalAndExpiry()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(DeviceCodeJson);
        using var service = CreateService(handler);

        var device = await service.StartDeviceFlowAsync("client-id-1", TestContext.Current.CancellationToken);

        Assert.Equal("dev-code-123", device.DeviceCode);
        Assert.Equal("ABCD-1234", device.UserCode);
        Assert.Equal("https://github.com/login/device", device.VerificationUri);
        Assert.Equal(5, device.IntervalSeconds);
        Assert.True(device.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(10));

        Assert.Contains("github.com/login/device/code", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("client_id=client-id-1", handler.RequestBodies[0]);
        Assert.Contains("scope=repo", handler.RequestBodies[0]);
    }

    /// <summary>
    ///     What the token can do. The only thing the app does with it is READ one
    ///     repository's releases and download their assets. A GitHub App (client id
    ///     "Iv...") gets its reach from the read-only permissions configured on the app and
    ///     ignores a requested scope, so none is asked for; an OAuth app has no read-only
    ///     scope for a private repo, so the classic "repo" scope is the narrowest that works.
    /// </summary>
    [Theory]
    [InlineData("Iv23liExample", false)]
    [InlineData("Iv1.8a61f9b3a7aba766", false)]
    [InlineData("Ov23liExample", true)]
    [InlineData("0123456789abcdef0123", true)]
    public async Task StartDeviceFlow_AsksNoScopeOfAGitHubApp_AndRepoOfAnOAuthApp(string clientId, bool asksRepo)
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson(DeviceCodeJson);
        using var service = CreateService(handler);

        await service.StartDeviceFlowAsync(clientId, TestContext.Current.CancellationToken);

        Assert.Contains("client_id=" + clientId, handler.RequestBodies[0]);
        if (asksRepo) Assert.Contains("scope=repo", handler.RequestBodies[0]);
        else Assert.DoesNotContain("scope", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_PendingSlowDownThenSuccess()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":"authorization_pending"}""");
        handler.EnqueueJson("""{"error":"slow_down","interval":10}""");
        handler.EnqueueJson("""{"error":"authorization_pending"}""");
        handler.EnqueueJson("""{"access_token":"gho_flow_token","token_type":"bearer","scope":"repo"}""");
        using var service = CreateService(handler);
        var device = new GitHubDeviceCode("dev-code-123", "ABCD-1234", "https://github.com/login/device",
            5, DateTimeOffset.UtcNow.AddMinutes(15));

        var token = await service.WaitForDeviceAuthorizationAsync(
            "client-id-1", device, TestContext.Current.CancellationToken);

        Assert.Equal("gho_flow_token", token);
        Assert.Equal(4, handler.Requests.Count);
        // Poll cadence: initial 5 s interval, then slow_down raises it to 10 s.
        Assert.Equal(TimeSpan.FromSeconds(5), _delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), _delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(10), _delays[2]);
        Assert.Equal(TimeSpan.FromSeconds(10), _delays[3]);
        // Poll body carries the device-code grant.
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code",
            handler.RequestBodies[0]);

        if (OperatingSystem.IsWindows())
        {
            // Token must be persisted and reported through the auth state.
            Assert.True(service.IsSignedIn);
            Assert.Equal("gho_flow_token", new GitHubTokenStore(_dir).Load());
        }
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_AccessDenied_Throws()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":"access_denied"}""");
        using var service = CreateService(handler);
        var device = new GitHubDeviceCode("dev-code-123", "ABCD-1234", "uri", 5,
            DateTimeOffset.UtcNow.AddMinutes(15));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.WaitForDeviceAuthorizationAsync("client-id-1", device, TestContext.Current.CancellationToken));
        Assert.False(service.IsSignedIn);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_ExpiredToken_ThrowsTimeout()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"error":"expired_token"}""");
        using var service = CreateService(handler);
        var device = new GitHubDeviceCode("dev-code-123", "ABCD-1234", "uri", 5,
            DateTimeOffset.UtcNow.AddMinutes(15));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.WaitForDeviceAuthorizationAsync("client-id-1", device, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_AlreadyExpiredCode_ThrowsWithoutPolling()
    {
        var handler = new FakeHttpMessageHandler();
        using var service = CreateService(handler);
        var device = new GitHubDeviceCode("dev-code-123", "ABCD-1234", "uri", 5,
            DateTimeOffset.UtcNow.AddSeconds(-1));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.WaitForDeviceAuthorizationAsync("client-id-1", device, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void PatSignInAndSignOut_UpdateStateAndRaiseEvent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI token storage is Windows-only.");

        var handler = new FakeHttpMessageHandler();
        using var service = CreateService(handler);
        var events = 0;
        service.AuthStateChanged += () => events++;

        service.SignInWithPersonalAccessToken("  ghp_pasted_token  ");
        Assert.True(service.IsSignedIn);
        Assert.Equal("ghp_pasted_token", service.GetToken());
        Assert.Equal(1, events);

        service.SignOut();
        Assert.False(service.IsSignedIn);
        Assert.Null(service.GetToken());
        Assert.Equal(2, events);
    }

    [Fact]
    public async Task GetSignedInLogin_UsesBearerTokenAndParsesLogin()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI token storage is Windows-only.");

        var handler = new FakeHttpMessageHandler();
        handler.EnqueueJson("""{"login":"SomeeName","id":1}""");
        using var service = CreateService(handler);
        service.SignInWithPersonalAccessToken("ghp_user_token");

        var login = await service.GetSignedInLoginAsync(TestContext.Current.CancellationToken);

        Assert.Equal("SomeeName", login);
        Assert.Equal("Bearer ghp_user_token", handler.HeaderOf(0, "Authorization"));
        Assert.Contains("api.github.com/user", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetSignedInLogin_WithoutToken_ReturnsNullWithoutRequest()
    {
        var handler = new FakeHttpMessageHandler();
        using var service = CreateService(handler);

        Assert.Null(await service.GetSignedInLoginAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    // ---- the poll rides out a server having a moment ----------------------
    //
    // PostFormAsync calls EnsureSuccessStatusCode, and the poll loop had no
    // catch — so ONE 502 from GitHub's edge ended a sign-in the user was in the
    // middle of, with "Response status code does not indicate success" and no
    // way back but starting over. A 5xx says nothing about this flow.

    [Fact]
    public async Task WaitForDeviceAuthorization_RidesOutATransientServerError()
    {
        var handler = new FakeHttpMessageHandler();
        handler.EnqueueStatus(HttpStatusCode.BadGateway);
        handler.EnqueueJson("""{"access_token":"gho_FAKE_after_502","token_type":"bearer"}""");
        using var service = CreateService(handler);

        var token = await service.WaitForDeviceAuthorizationAsync(
            "client-id-1", Device(), TestContext.Current.CancellationToken);

        Assert.Equal("gho_FAKE_after_502", token);
        Assert.Equal(2, handler.Requests.Count);
        // The retry waits: the poll interval, then a 5 s backoff, then the poll
        // interval again — a failed poll must not become a hot loop.
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)],
            _delays);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_BacksOffExponentiallyAndGivesUpBounded()
    {
        var handler = new FakeHttpMessageHandler();
        for (var i = 0; i < 8; i++) handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        using var service = CreateService(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.WaitForDeviceAuthorizationAsync(
                "client-id-1", Device(), TestContext.Current.CancellationToken));

        // Five retries, then the sixth consecutive failure gives up — a hard
        // outage must terminate rather than poll until the code expires.
        Assert.Equal(6, handler.Requests.Count);
        // 5 s poll, then 5 / 10 / 20 / 40 / 60 (capped) backoffs, each followed
        // by another poll interval.
        Assert.Equal(
            [5, 5, 5, 10, 5, 20, 5, 40, 5, 60, 5],
            _delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_AnAnswerResetsTheTransientStreak()
    {
        var handler = new FakeHttpMessageHandler();
        for (var i = 0; i < 5; i++) handler.EnqueueStatus(HttpStatusCode.BadGateway);
        handler.EnqueueJson("""{"error":"authorization_pending"}""");
        for (var i = 0; i < 5; i++) handler.EnqueueStatus(HttpStatusCode.BadGateway);
        handler.EnqueueJson("""{"access_token":"gho_survived","token_type":"bearer"}""");
        using var service = CreateService(handler);

        var token = await service.WaitForDeviceAuthorizationAsync(
            "client-id-1", Device(), TestContext.Current.CancellationToken);

        // Eleven polls: five failures, a real answer that resets the streak,
        // five more failures, then the token. Without the reset the tenth
        // failure would be the sixth consecutive one and abort the flow.
        Assert.Equal("gho_survived", token);
        Assert.Equal(12, handler.Requests.Count);
    }

    [Fact]
    public async Task WaitForDeviceAuthorization_A4xxIsTerminalNotTransient()
    {
        var handler = new FakeHttpMessageHandler();
        for (var i = 0; i < 4; i++) handler.EnqueueStatus(HttpStatusCode.BadRequest);
        using var service = CreateService(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.WaitForDeviceAuthorizationAsync(
                "client-id-1", Device(), TestContext.Current.CancellationToken));

        // GitHub answering about THIS flow is never retried.
        Assert.Single(handler.Requests);
    }

    private static GitHubDeviceCode Device() =>
        new("dev-code-123", "ABCD-1234", "https://github.com/login/device", 5,
            DateTimeOffset.UtcNow.AddMinutes(15));
}
