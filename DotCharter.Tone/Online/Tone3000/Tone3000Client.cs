using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     HTTP client for the TONE3000 community NAM-capture API
///     (<c>https://www.tone3000.com/api/v1</c>). Every method catches everything
///     and maps to a typed <see cref="OnlineResult{T}"/>; the message handler is
///     injectable so the whole surface unit-tests without sockets. A
///     client-side <see cref="Tone3000RateLimiter"/> keeps request bursts under
///     the server's 100 req/min limit.
///
///     The live TONE3000 API rejects anonymous requests (401
///     "missing/invalid authorization header"), so EVERY request — search, tone,
///     models and the model-file download — carries the signed-in user's Bearer
///     token when a session exists, supplied by the injected
///     <c>accessTokenProvider</c> (wired to <c>ITone3000AuthService</c> in DI).
///     When there is no session the request is sent with NO Authorization header
///     (never a malformed/empty one); a 401 back maps to typed
///     <see cref="OnlineError.NotSignedIn"/>, and the download short-circuits to
///     the same before touching the network.
///
///     …and an ANONYMOUS search that comes back 200-with-zero-results maps to
///     <see cref="OnlineError.NotSignedIn"/> too, rather than to a successful
///     empty page. Against the live API that case cannot happen (it is 401), so
///     the only things that produce it are a proxy, a captive portal or a schema
///     change — none of which mean "no captures matched", which is what the UI
///     would otherwise print to a user whose actual problem is being signed out.
///     A SIGNED-IN empty page is still reported as the genuine empty result.
/// </summary>
public sealed class Tone3000Client : ITone3000Service, IDisposable
{
    /// <summary>API root. Const so every endpoint builds from one place.</summary>
    public const string ApiBase = "https://www.tone3000.com/api/v1";

    /// <summary>
    ///     Hosts we will present the user's TONE3000 bearer token to. Download
    ///     URLs arrive in an API RESPONSE, so without this the service could
    ///     name any host and be handed a working credential for the user's
    ///     account. Exact match or a subdomain of these, https only.
    /// </summary>
    private static readonly string[] CredentialHosts = ["tone3000.com"];

    /// <summary>Whether <paramref name="uri" /> may receive the bearer token.
    /// Internal for test; subdomain match is suffix-with-dot so
    /// "nottone3000.com" cannot pass.</summary>
    internal static bool IsTrustedCredentialHost(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        foreach (var host in CredentialHosts)
        {
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Budget for a metadata call (search / tone / models).</summary>
    public static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Budget for a model-file download. Deliberately far larger than
    ///     <see cref="DefaultApiTimeout" />: a capture may be up to
    ///     <see cref="NamCaptureLibrary.MaxDownloadBytes" /> (128 MB), which no
    ///     ordinary connection moves in thirty seconds.
    /// </summary>
    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly Func<CancellationToken, Task<string?>>? _accessTokenProvider;
    private readonly HttpClient _http;
    private readonly IAppLogger? _logger;
    private readonly Tone3000RateLimiter _limiter;
    private readonly Func<long> _nowMs;
    private readonly TimeSpan _apiTimeout;
    private readonly TimeSpan _downloadTimeout;

    /// <param name="accessTokenProvider">Supplies the signed-in user's Bearer
    /// token, or null when signed out. Wired to <c>ITone3000AuthService</c>.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="handler">Injected by tests; null shares the pooled handler.</param>
    /// <param name="limiter">Client-side request budget; null uses the default.</param>
    /// <param name="nowMs">Injected clock for the rate limiter.</param>
    /// <param name="apiTimeout">Per-request budget for the metadata calls.</param>
    /// <param name="downloadTimeout">Per-request budget for a model download.</param>
    public Tone3000Client(
        Func<CancellationToken, Task<string?>>? accessTokenProvider = null,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        Tone3000RateLimiter? limiter = null,
        Func<long>? nowMs = null,
        TimeSpan? apiTimeout = null,
        TimeSpan? downloadTimeout = null)
    {
        _accessTokenProvider = accessTokenProvider;
        _logger = logger;
        _limiter = limiter ?? new Tone3000RateLimiter();
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _apiTimeout = apiTimeout ?? DefaultApiTimeout;
        _downloadTimeout = downloadTimeout ?? DefaultDownloadTimeout;
        // INFINITE on the client, a linked CTS per call. HttpClient.Timeout is a
        // deadline for the WHOLE exchange including reading the response stream,
        // so one shared 30 s value silently gave a 128 MB capture download thirty
        // seconds to finish and reported the miss as an opaque "Network" error.
        _http = OnlineHttp.CreateClient(Timeout.InfiniteTimeSpan, handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DotCharter/1.0 (+https://github.com/SomeeName/DotCharter)");
    }

    public void Dispose() => _http.Dispose();

    public async Task<OnlineResult<Tone3000SearchPage>> SearchAsync(
        Tone3000SearchQuery query, CancellationToken ct = default)
    {
        if (query == null)
            return OnlineResult<Tone3000SearchPage>.Fail(OnlineError.InvalidRequest, "A query is required.");

        // The parameter names are NOT free-form — see Tone3000Wire for the
        // documented contract and why the previous spellings silently searched
        // for nothing.
        var queryString = Tone3000Wire.BuildSearchQueryString(query);
        var url = $"{ApiBase}/tones/search?{queryString}";

        string? token;
        try
        {
            token = await AcquireTokenAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OnlineResult<Tone3000SearchPage>.Fail(OnlineError.Network, "Cancelled.");
        }

        var result = await GetJsonAsync(url, "tone search", token,
            body => Tone3000Json.ParseSearchPage(body, Math.Max(1, query.Page)), ct).ConfigureAwait(false);

        // An ANONYMOUS search that came back empty is not "no captures matched".
        // The live API is 401 for everyone without a bearer, so a 200 with no
        // results to a request we sent with no token is a proxy, a captive
        // portal or a schema change — and reporting it as success would put
        // "no captures matched" in front of a user whose real problem is that
        // they are signed out. A SIGNED-IN empty page still means what it says.
        if (result.Ok && string.IsNullOrEmpty(token) && result.Value!.Tones.Count == 0)
        {
            _logger?.Warning(AppLogCategory.Application,
                $"TONE3000 search ?{queryString} returned no results to an anonymous request — " +
                "the API requires a sign-in, so this is reported as signed-out, not as 'no matches'.");
            return OnlineResult<Tone3000SearchPage>.Fail(OnlineError.NotSignedIn,
                "Searching TONE3000 requires a sign-in.");
        }

        // A search that ran and matched nothing must be distinguishable in the
        // log from a search that never left the building — the previous client
        // logged NOTHING at all for search at any level, so an owner staring at
        // an empty result list had no way to tell which had happened.
        if (result.Ok)
        {
            _logger?.Info(AppLogCategory.Application,
                $"TONE3000 search ?{queryString} → {result.Value!.Tones.Count} tone(s) " +
                $"on page {result.Value.Page} of {result.Value.TotalCount} total.");
        }

        return result;
    }

    public async Task<OnlineResult<Tone3000Tone>> GetToneAsync(long toneId, CancellationToken ct = default)
    {
        if (toneId <= 0)
            return OnlineResult<Tone3000Tone>.Fail(OnlineError.InvalidRequest, "A tone id is required.");

        var result = await GetAuthedJsonAsync<Tone3000Tone?>($"{ApiBase}/tones/{toneId}", "tone fetch",
            Tone3000Json.ParseToneBody, ct).ConfigureAwait(false);
        if (!result.Ok)
            return OnlineResult<Tone3000Tone>.Fail(result.Error, result.Message);
        return result.Value is { } tone
            ? OnlineResult<Tone3000Tone>.Success(tone)
            : OnlineResult<Tone3000Tone>.Fail(OnlineError.Server, "Unrecognized tone response.");
    }

    public async Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(
        long toneId, CancellationToken ct = default)
    {
        if (toneId <= 0)
        {
            return OnlineResult<IReadOnlyList<Tone3000Model>>.Fail(
                OnlineError.InvalidRequest, "A tone id is required.");
        }

        return await GetAuthedJsonAsync($"{ApiBase}/models?tone_id={toneId}", "model list",
            body => Tone3000Json.ParseModels(body, toneId), ct).ConfigureAwait(false);
    }

    public async Task<OnlineResult<long>> DownloadModelAsync(
        Tone3000Model model,
        Stream destination,
        long maxBytes,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.ModelUrl))
            return OnlineResult<long>.Fail(OnlineError.InvalidRequest, "The model has no download URL.");
        if (destination == null || !destination.CanWrite)
            return OnlineResult<long>.Fail(OnlineError.InvalidRequest, "A writable destination is required.");
        // HTTPS only. This URL comes from the API RESPONSE, not from us, and the
        // request below carries the user's bearer token — over http that token
        // is on the wire in cleartext for anyone on the path.
        if (!Uri.TryCreate(model.ModelUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return OnlineResult<long>.Fail(OnlineError.InvalidRequest,
                "The model download URL is not a valid https URL.");
        }

        string? token;
        try
        {
            token = await AcquireTokenAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OnlineResult<long>.Fail(OnlineError.Network, "Cancelled.");
        }

        if (string.IsNullOrEmpty(token))
        {
            return OnlineResult<long>.Fail(OnlineError.NotSignedIn,
                "Downloading captures requires a TONE3000 sign-in.");
        }

        // The download gets its OWN budget, not the metadata one — see the ctor.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_downloadTimeout);
        var dct = budget.Token;

        try
        {
            await ThrottleAsync(dct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            // Only send the user's credential to TONE3000 itself. The download
            // URL is chosen by the API response, so attaching the bearer
            // unconditionally meant any host that response named — or any host
            // an attacker could get it to name — received a working token for
            // the user's account. A model file served elsewhere (a CDN, object
            // storage) carries its own signed URL and needs no header from us,
            // so withholding it costs nothing.
            if (IsTrustedCredentialHost(uri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            else
            {
                _logger?.Info(AppLogCategory.Application,
                    $"Downloading a TONE3000 model from '{uri.Host}' WITHOUT credentials " +
                    "(not a tone3000.com host).");
            }

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, dct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OnlineResult<long>.Fail(MapError(response.StatusCode),
                    $"Download failed ({(int)response.StatusCode}).");
            }

            var total = response.Content.Headers.ContentLength ?? 0;
            if (total > maxBytes)
            {
                return OnlineResult<long>.Fail(OnlineError.InvalidRequest,
                    $"The capture is larger than the {maxBytes:N0}-byte download cap.");
            }

            await using var src = await response.Content.ReadAsStreamAsync(dct).ConfigureAwait(false);
            var buffer = new byte[1 << 16];
            long written = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, dct).ConfigureAwait(false)) > 0)
            {
                written += n;
                if (written > maxBytes)
                {
                    return OnlineResult<long>.Fail(OnlineError.InvalidRequest,
                        $"The capture exceeded the {maxBytes:N0}-byte download cap.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, n), dct).ConfigureAwait(false);
                if (total > 0) progress?.Report(Math.Clamp(written / (double)total, 0.0, 1.0));
            }

            progress?.Report(1.0);
            _logger?.Info(AppLogCategory.Application,
                $"TONE3000 model {model.Id} downloaded ({written:N0} bytes).");
            return OnlineResult<long>.Success(written);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OnlineResult<long>.Fail(OnlineError.Network, "Cancelled.");
        }
        catch (OperationCanceledException)
        {
            // Our own budget, not the caller's — say which, because "cancelled"
            // in front of a user who cancelled nothing is a lie.
            return TimeoutFail<long>("model download", _downloadTimeout);
        }
        catch (Exception ex)
        {
            return NetworkFail<long>("model download", ex);
        }
    }

    // ─────────────────────────────── internals ───────────────────────────────

    /// <summary>Acquires the token, then <see cref="GetJsonAsync{T}" />. For the
    /// calls that do not need to know whether they went out anonymously.</summary>
    private async Task<OnlineResult<T>> GetAuthedJsonAsync<T>(
        string url, string operation, Func<string, T> parse, CancellationToken ct)
    {
        string? token;
        try
        {
            token = await AcquireTokenAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OnlineResult<T>.Fail(OnlineError.Network, "Cancelled.");
        }

        return await GetJsonAsync(url, operation, token, parse, ct).ConfigureAwait(false);
    }

    private async Task<OnlineResult<T>> GetJsonAsync<T>(
        string url, string operation, string? token, Func<string, T> parse, CancellationToken ct)
    {
        // Metadata budget, linked to the caller's token — the client itself has
        // no timeout so a download is not held to this one.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_apiTimeout);
        var act = budget.Token;

        try
        {
            await ThrottleAsync(act).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            // The live API rejects anonymous requests (401); attach the user's
            // Bearer token when a session exists, and send NO Authorization
            // header (never an empty/malformed one) when it doesn't.
            if (!string.IsNullOrEmpty(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, act).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(act).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The server's own error text is UNTRUSTED content that we then
                // write to the log verbatim, because it is the one line the
                // owner can act on. A server that quotes the credential we just
                // sent it — deliberately or by echoing the request — must not
                // be able to put that credential in the log file or on screen.
                var serverMessage = Scrub(OnlineJson.ExtractErrorMessage(body), token);
                // WARNING, not Debug: a rejected request is the one thing the
                // owner needs in the log to act on, and Debug is off by default.
                // The URL carries no credential (the token is a header) — its
                // query is the user's own filters, which is exactly what makes
                // a 400 diagnosable.
                _logger?.Warning(AppLogCategory.Application,
                    $"TONE3000 {operation} rejected: HTTP {(int)response.StatusCode} {response.StatusCode}" +
                    (serverMessage.Length > 0 ? $" — {serverMessage}" : string.Empty) +
                    $" [{url}]");
                return OnlineResult<T>.Fail(MapError(response.StatusCode), serverMessage);
            }

            return OnlineResult<T>.Success(parse(body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OnlineResult<T>.Fail(OnlineError.Network, "Cancelled.");
        }
        catch (OperationCanceledException)
        {
            return TimeoutFail<T>(operation, _apiTimeout);
        }
        catch (JsonException ex)
        {
            return OnlineResult<T>.Fail(OnlineError.Server, $"Unrecognized response: {ex.Message}");
        }
        catch (Exception ex)
        {
            return NetworkFail<T>(operation, ex);
        }
    }

    /// <summary>The user's current Bearer access token, or null when signed
    /// out / no provider is wired. Swallows provider faults (⇒ anonymous
    /// request), but lets a real cancellation propagate to the caller.</summary>
    private async Task<string?> AcquireTokenAsync(CancellationToken ct)
    {
        if (_accessTokenProvider == null) return null;
        try
        {
            return await _accessTokenProvider(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        var wait = _limiter.MsUntilNextPermit(_nowMs());
        if (wait > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
        _limiter.Record(_nowMs());
    }

    /// <summary>Removes the bearer token we sent from a server-supplied string.
    /// Exact-match only: the token is an opaque value with no shape a pattern
    /// could recognize, so the only reliable scrub is the literal we know.</summary>
    internal static string Scrub(string message, string? token) =>
        string.IsNullOrEmpty(token) || message.Length == 0
            ? message
            : message.Replace(token, "***", StringComparison.Ordinal);

    private static OnlineError MapError(HttpStatusCode status) => status switch
    {
        // 401 = not authenticated (missing/invalid/expired token) — the caller
        // must (re-)sign in. 403 = authenticated but not allowed — a distinct,
        // rarer state kept as the generic auth error.
        HttpStatusCode.Unauthorized => OnlineError.NotSignedIn,
        HttpStatusCode.Forbidden => OnlineError.Unauthorized,
        HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict
            or HttpStatusCode.UnprocessableEntity => OnlineError.InvalidRequest,
        _ => OnlineError.Server
    };

    private OnlineResult<T> NetworkFail<T>(string operation, Exception ex)
    {
        // WARNING, not Debug. Cancellation is caught by the caller BEFORE this
        // runs, so anything reaching here is a real failure the user just saw
        // as an empty panel — and Debug is off in a default install, which is
        // why the owner's log had nothing whatsoever about search.
        var reason = OnlineHttp.Describe(ex);
        _logger?.Warning(AppLogCategory.Application,
            $"TONE3000 {operation} failed: {ex.GetType().Name}: {reason}");
        return OnlineResult<T>.Fail(OnlineError.Network, reason);
    }

    private OnlineResult<T> TimeoutFail<T>(string operation, TimeSpan budget)
    {
        var reason = $"The TONE3000 {operation} timed out after {budget.TotalSeconds:N0}s.";
        _logger?.Warning(AppLogCategory.Application, reason);
        return OnlineResult<T>.Fail(OnlineError.Network, reason);
    }
}
