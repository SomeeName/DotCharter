using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     TONE3000 sign-in state + token lifetime for the NAM-capture browser.
///     Same resilience contract as the rest of <c>Online/</c>: sign-in returns
///     a typed <see cref="OnlineResult{T}"/>, token refresh degrades to
///     "signed out", nothing throws to the UI.
/// </summary>
public interface ITone3000AuthService
{
    /// <summary>True when <see cref="RigConfig.Tone3000ClientId"/> is set. When
    /// false the UI shows a "not configured" hint naming tone3000.com/settings —
    /// the owner mints the developer/publishable client id there.</summary>
    bool IsConfigured { get; }

    bool IsSignedIn { get; }

    /// <summary>Display name of the signed-in user (empty when signed out).</summary>
    string UserName { get; }

    event Action? AuthStateChanged;

    /// <summary>Runs the OAuth 2.0 + PKCE flow: opens the system browser to the
    /// authorize URL, catches the redirect on the loopback listener
    /// (port 47821), exchanges the code and persists the session (DPAPI).</summary>
    Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default);

    void SignOut();

    /// <summary>
    ///     Drops the stored session because the SERVER refused it, not because
    ///     the user asked.
    ///     <para>
    ///         A token can stop working while its LOCAL expiry is still in the
    ///         future — revoked at tone3000.com, invalidated by a password
    ///         change, or simply rejected. <see cref="GetAccessTokenAsync" />
    ///         never notices: it hands the unexpired token straight back and no
    ///         refresh is attempted, so <see cref="IsSignedIn" /> stayed TRUE for
    ///         the rest of the process and every surface went on printing
    ///         "Signed in as X" over a session that answered 401 to everything.
    ///         The caller that SAW the 401 is the only one that knows.
    ///     </para>
    ///     <para>Idempotent, and a no-op when already signed out, so a page of
    ///     failing requests raises one state change rather than twenty.</para>
    /// </summary>
    void InvalidateSession(string reason);

    /// <summary>A currently valid access token (refreshing when expired), or
    /// null when signed out / refresh failed. Never throws.</summary>
    Task<string?> GetAccessTokenAsync(CancellationToken ct = default);
}

/// <summary>Seam for the loopback redirect catcher so the auth flow tests
/// without opening sockets. The real implementation is
/// <see cref="HttpListenerOAuthCallbackListener"/>.</summary>
public interface IOAuthCallbackListener
{
    /// <summary>Listens on <paramref name="prefix"/> until a request arrives that is for the
    /// callback path AND whose raw query string ("?code=…&amp;state=…") satisfies
    /// <paramref name="accept"/>, and returns that query string; null on timeout.
    /// Every other request is answered with a 404 and ignored, so a stray page that
    /// knocks on the loopback port cannot end the sign-in.</summary>
    Task<string?> WaitForCallbackAsync(string prefix, Func<string, bool> accept, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
///     Real loopback catcher: one <see cref="HttpListener"/> bound to the
///     TONE3000 callback prefix (<c>http://localhost:47821/…</c> — inside the
///     firewall-allowed 47810–47829 range; HTTP.SYS permits non-admin
///     localhost listeners). Answers the browser with a tiny "you can close
///     this window" page and resolves with the query string of the first request
///     that is for the callback path and passes the caller's gate; every other
///     request gets a 404 and the wait goes on until the timeout.
/// </summary>
public sealed class HttpListenerOAuthCallbackListener : IOAuthCallbackListener
{
    public async Task<string?> WaitForCallbackAsync(
        string prefix, Func<string, bool> accept, TimeSpan timeout, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            // Only the request the sign-in is waiting for ends the wait: the
            // callback path, carrying the caller's state. Anything else that
            // knocks on the loopback port is answered with a 404 and ignored, so
            // a stray page cannot complete or abort the sign-in; the timeout is
            // the only other way out.
            var callbackPath = new Uri(prefix).AbsolutePath;
            while (true)
            {
                var context = await listener.GetContextAsync()
                    .WaitAsync(timeoutCts.Token).ConfigureAwait(false);

                var url = context.Request.Url;
                var query = url?.Query ?? string.Empty;
                var isCallback = url != null &&
                                 string.Equals(url.AbsolutePath, callbackPath, StringComparison.OrdinalIgnoreCase) &&
                                 accept(query);
                if (!isCallback)
                {
                    await RespondAsync(context, HttpStatusCode.NotFound, "Not found.").ConfigureAwait(false);
                    continue;
                }

                await RespondAsync(context, HttpStatusCode.OK,
                    "<!doctype html><html><body style=\"font-family:sans-serif;background:#16181d;color:#e6e8ec;" +
                    "display:flex;align-items:center;justify-content:center;height:100vh\">" +
                    "<div><h2>DotCharter</h2><p>Sign-in received &mdash; you can close this window.</p></div>" +
                    "</body></html>").ConfigureAwait(false);
                return query;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try { listener.Stop(); } catch { /* teardown best-effort */ }
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, HttpStatusCode status, string html)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, CancellationToken.None).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            // The peer went away before the answer: nothing to keep.
        }
    }
}

/// <summary>
///     <see cref="ITone3000AuthService"/> implementation. The token values are
///     deliberately kept out of every log line; the persisted session lives
///     DPAPI-encrypted in <see cref="DpapiTone3000TokenStore"/>. Browser
///     opening + the loopback listener are injectable seams so the exchange
///     logic tests with a scripted handler and no real browser/socket.
/// </summary>
public sealed class Tone3000AuthService : ITone3000AuthService, IDisposable
{
    /// <summary>How long the sign-in waits for the user to approve in the browser.</summary>
    private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Refresh this long before the reported expiry so an in-flight
    /// request never rides a token that dies mid-download.</summary>
    private static readonly TimeSpan ExpirySlack = TimeSpan.FromMinutes(2);

    private readonly Func<RigConfig> _config;
    private readonly ITone3000TokenStore _store;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly Func<string, bool> _openBrowser;
    private readonly IOAuthCallbackListener _listener;
    private readonly Func<DateTimeOffset> _nowUtc;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private readonly object _sessionLock = new();
    private Tone3000SessionData? _session;
    private bool _sessionLoaded;

    public Tone3000AuthService(
        Func<RigConfig> configProvider,
        ITone3000TokenStore store,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        Func<string, bool>? openBrowser = null,
        IOAuthCallbackListener? listener = null,
        Func<DateTimeOffset>? nowUtc = null)
    {
        _config = configProvider;
        _store = store;
        _logger = logger;
        _http = OnlineHttp.CreateClient(TimeSpan.FromSeconds(30), handler);
        _openBrowser = openBrowser ?? DefaultOpenBrowser;
        _listener = listener ?? new HttpListenerOAuthCallbackListener();
        _nowUtc = nowUtc ?? (() => DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshGate.Dispose();
    }

    public event Action? AuthStateChanged;

    public bool IsConfigured => GetClientId().Length > 0;

    public bool IsSignedIn => Session != null;

    public string UserName => Session?.UserName ?? string.Empty;

    public async Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default)
    {
        var clientId = GetClientId();
        if (clientId.Length == 0)
        {
            return OnlineResult<Tone3000User>.Fail(OnlineError.NotConfigured,
                "No TONE3000 client id configured. Create one at tone3000.com/settings.");
        }

        try
        {
            var verifier = Tone3000OAuth.CreateCodeVerifier();
            var state = Tone3000OAuth.CreateState();
            var authorizeUrl = Tone3000OAuth.BuildAuthorizeUrl(
                clientId, Tone3000OAuth.ComputeCodeChallenge(verifier), state);

            // Start listening BEFORE the browser opens so the redirect can
            // never race an unbound port. The listener gets a token THIS method
            // owns, cancelled on every exit below: the early "browser could not
            // be opened" return must not abandon the wait, or the HttpListener
            // would then hold port 47821 for the whole CallbackTimeout (5 min), so a
            // retry inside that window found the port taken (network audit
            // P2-12).
            using var callbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var callbackTask = _listener.WaitForCallbackAsync(
                Tone3000OAuth.RedirectUri, query => Tone3000OAuth.CarriesState(query, state),
                CallbackTimeout, callbackCts.Token);

            if (!_openBrowser(authorizeUrl))
            {
                callbackCts.Cancel();
                return OnlineResult<Tone3000User>.Fail(OnlineError.InvalidRequest,
                    "The system browser could not be opened.");
            }

            _logger?.Info(AppLogCategory.Application, "TONE3000 sign-in started (browser opened).");

            var query = await callbackTask.ConfigureAwait(false);
            if (query == null)
            {
                return OnlineResult<Tone3000User>.Fail(OnlineError.Network,
                    "The sign-in was not completed in time.");
            }

            var code = Tone3000OAuth.TryParseCallback(query, state, out var parseError);
            if (code == null)
                return OnlineResult<Tone3000User>.Fail(OnlineError.Unauthorized, parseError);

            var tokens = (await ExchangeAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = Tone3000OAuth.RedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier
            }, ct).ConfigureAwait(false)).Tokens;
            if (tokens == null)
            {
                return OnlineResult<Tone3000User>.Fail(OnlineError.Unauthorized,
                    "TONE3000 rejected the sign-in code exchange.");
            }

            var user = await FetchUserAsync(tokens.Value.AccessToken, ct).ConfigureAwait(false)
                       ?? new Tone3000User(string.Empty, string.Empty);

            var session = new Tone3000SessionData(
                tokens.Value.AccessToken,
                tokens.Value.RefreshToken,
                _nowUtc() + TimeSpan.FromSeconds(tokens.Value.ExpiresInSeconds),
                user.Id,
                user.UserName);
            SetSession(session);
            _logger?.Info(AppLogCategory.Application,
                $"TONE3000 sign-in completed{(user.UserName.Length > 0 ? $" as '{user.UserName}'" : string.Empty)}.");
            return OnlineResult<Tone3000User>.Success(user);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OnlineResult<Tone3000User>.Fail(OnlineError.Network, "Cancelled.");
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"TONE3000 sign-in failed: {ex.GetType().Name}: {ex.Message}");
            return OnlineResult<Tone3000User>.Fail(OnlineError.Network, ex.Message);
        }
    }

    public void SignOut()
    {
        lock (_sessionLock)
        {
            _store.Clear();
            _session = null;
            _sessionLoaded = true;
        }

        _logger?.Info(AppLogCategory.Application, "TONE3000 sign-out — stored session removed.");
        AuthStateChanged?.Invoke();
    }

    /// <inheritdoc />
    public void InvalidateSession(string reason)
    {
        lock (_sessionLock)
        {
            // Already gone: say nothing. A page of failing requests must not
            // raise twenty state changes, and it must not log twenty lines.
            if (_session == null && _sessionLoaded) return;
            _store.Clear();
            _session = null;
            _sessionLoaded = true;
        }

        _logger?.Warning(AppLogCategory.Application,
            $"TONE3000 session invalidated by the server ({reason}) — stored session removed.");
        AuthStateChanged?.Invoke();
    }

    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var session = Session;
        if (session == null) return null;
        if (_nowUtc() + ExpirySlack < session.AccessTokenExpiresUtc)
            return session.AccessToken;

        // Expired (or about to): refresh, single-flight.
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            session = Session;
            if (session == null) return null;
            if (_nowUtc() + ExpirySlack < session.AccessTokenExpiresUtc)
                return session.AccessToken; // another caller already refreshed

            if (string.IsNullOrEmpty(session.RefreshToken))
            {
                SignOut();
                return null;
            }

            var exchange = await ExchangeAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken,
                ["client_id"] = GetClientId()
            }, ct).ConfigureAwait(false);
            if (exchange.Tokens == null)
            {
                // A failed refresh is TWO different facts and only one of them is
                // "signed out". A 400/401 is the provider REJECTING this refresh
                // token — the stored session is worthless and must go. Anything
                // else (a 500, a 502 from a CDN, a 429, an unparseable body) is
                // the far side having a bad minute, and destroying the DPAPI
                // session over it makes a transient outage cost the user a full
                // browser sign-in they can never get back by retrying. Same rule
                // as the degraded-read one every JSON store here follows: a
                // failure to READ an answer is never authority to DELETE.
                if (exchange.Rejected)
                {
                    _logger?.Warning(AppLogCategory.Application,
                        "TONE3000 rejected the refresh token — signing out.");
                    SignOut();
                }
                else
                {
                    _logger?.Warning(AppLogCategory.Application,
                        "TONE3000 token refresh could not be completed (the server did not answer with " +
                        "a token). The stored session is KEPT — the next call retries.");
                }

                return null;
            }

            var tokens = exchange.Tokens;
            var refreshed = session with
            {
                AccessToken = tokens.Value.AccessToken,
                RefreshToken = string.IsNullOrEmpty(tokens.Value.RefreshToken)
                    ? session.RefreshToken // some providers omit it on refresh
                    : tokens.Value.RefreshToken,
                AccessTokenExpiresUtc = _nowUtc() + TimeSpan.FromSeconds(tokens.Value.ExpiresInSeconds)
            };
            SetSession(refreshed, raiseEvent: false);
            return refreshed.AccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"TONE3000 token refresh failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // ─────────────────────────────── internals ───────────────────────────────

    private Tone3000SessionData? Session
    {
        get
        {
            lock (_sessionLock)
            {
                if (!_sessionLoaded)
                {
                    _session = _store.Load();
                    _sessionLoaded = true;
                }

                return _session;
            }
        }
    }

    private void SetSession(Tone3000SessionData session, bool raiseEvent = true)
    {
        lock (_sessionLock)
        {
            _store.Save(session);
            _session = session;
            _sessionLoaded = true;
        }

        if (raiseEvent) AuthStateChanged?.Invoke();
    }

    private string GetClientId()
    {
        try
        {
            return _config().Tone3000ClientId?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private readonly record struct TokenResponse(string AccessToken, string RefreshToken, int ExpiresInSeconds);

    /// <summary>
    ///     One token-endpoint exchange. <paramref name="Tokens" /> is null on any
    ///     failure; <paramref name="Rejected" /> separates the only failure that
    ///     means the CREDENTIAL is bad (OAuth answers a refused grant with
    ///     400/401 per RFC 6749 §5.2) from every failure that means the SERVER
    ///     did not manage to answer. Only the first justifies throwing the
    ///     stored session away.
    /// </summary>
    private readonly record struct ExchangeOutcome(TokenResponse? Tokens, bool Rejected);

    /// <summary>POSTs a form to the token endpoint. See <see cref="ExchangeOutcome"/>.</summary>
    private async Task<ExchangeOutcome> ExchangeAsync(
        Dictionary<string, string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Tone3000OAuth.TokenUrl);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Content = new FormUrlEncodedContent(fields);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new ExchangeOutcome(null,
                response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var access = OnlineJson.GetString(root, "access_token");
            if (access.Length == 0)
                // A 200 carrying an OAuth `error` is still the provider refusing
                // the grant; a 200 carrying neither is a body we do not
                // understand, which says nothing about the token.
                return new ExchangeOutcome(null, OnlineJson.GetString(root, "error").Length > 0);
            var refresh = OnlineJson.GetString(root, "refresh_token");
            var expires = root.TryGetProperty("expires_in", out var exp) &&
                          exp.ValueKind == JsonValueKind.Number && exp.TryGetInt32(out var s)
                ? s
                : 3600;
            return new ExchangeOutcome(new TokenResponse(access, refresh, Math.Max(60, expires)), false);
        }
        catch (JsonException)
        {
            return new ExchangeOutcome(null, false);
        }
    }

    private async Task<Tone3000User?> FetchUserAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Tone3000Client.ApiBase + "/user");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // Tolerate a wrapped { "user": { … } } body.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("user", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            {
                root = wrapped;
            }

            var id = OnlineJson.GetString(root, "id");
            var name = OnlineJson.GetString(root, "username");
            if (name.Length == 0) name = OnlineJson.GetString(root, "name");
            if (name.Length == 0) name = OnlineJson.GetString(root, "display_name");
            return new Tone3000User(id, name);
        }
        catch
        {
            return null; // identity is cosmetic — the sign-in still succeeded
        }
    }

    private static bool DefaultOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
