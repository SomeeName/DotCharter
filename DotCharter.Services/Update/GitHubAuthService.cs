using System.Net;
using System.Text.Json;
using DotCharter.Tone.Online;
using DotCharter.Tone.Infrastructure.Json;

namespace DotCharter.Services.Update;

/// <summary>
/// <see cref="IGitHubAuthService"/> implementation. Device Flow per
/// https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps#device-flow
/// The token value itself is deliberately kept out of every log line. The client id is the
/// caller's: the selected <see cref="IUpdateFeed.SignInClientId"/>, never one of the product's.
/// </summary>
public sealed class GitHubAuthService : IGitHubAuthService, IDisposable
{
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";
    private const string UserUrl = "https://api.github.com/user";
    private const string Scope = "repo";

    private readonly GitHubTokenStore _store;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _tokenLock = new();
    private string? _cachedToken;
    private bool _tokenLoaded;

    /// <param name="store">Persistent store for the OAuth access token.</param>
    /// <param name="logger">Optional diagnostics logger; null disables logging.</param>
    /// <param name="handler">Optional message handler for the internal
    /// <see cref="HttpClient"/> (tests inject a fake); null uses a default handler.</param>
    /// <param name="delay">Test hook replacing Task.Delay for the poll loop.</param>
    public GitHubAuthService(
        GitHubTokenStore store,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _store = store;
        _logger = logger;
        _delay = delay ?? Task.Delay;
        // Through OnlineHttp.CreateClient for the response-size cap: every read
        // here is an unbounded ReadAsStringAsync of a body GitHub chooses, and the
        // device-flow poll makes that read repeatedly. Unlike the update service
        // this client never streams — every call buffers — so HttpClient.Timeout
        // really does bound the whole exchange and stays a plain 30 s.
        _http = OnlineHttp.CreateClient(
            TimeSpan.FromSeconds(30),
            handler ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
    }

    public event Action? AuthStateChanged;

    public bool IsSignedIn => !string.IsNullOrEmpty(GetToken());

    public string? GetToken()
    {
        lock (_tokenLock)
        {
            if (!_tokenLoaded)
            {
                _cachedToken = _store.Load();
                _tokenLoaded = true;
            }

            return _cachedToken;
        }
    }

    public async Task<GitHubDeviceCode> StartDeviceFlowAsync(
        string clientId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("An OAuth app client id is required for the Device Flow.", nameof(clientId));

        var json = await PostFormAsync(DeviceCodeUrl, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = Scope
        }, cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException(
                $"GitHub rejected the device-code request: {error.GetString()}.");
        }

        var deviceCode = JsonFields.StringOrEmpty(root, "device_code");
        var userCode = JsonFields.StringOrEmpty(root, "user_code");
        var verificationUri = JsonFields.StringOrEmpty(root, "verification_uri");
        if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode))
            throw new InvalidOperationException("GitHub returned an unexpected device-code response.");

        var interval = JsonFields.Int32OrNumericText(root, "interval") ?? 5;
        var expiresIn = JsonFields.Int32OrNumericText(root, "expires_in") ?? 900;
        _logger?.Info(AppLogCategory.Application,
            $"GitHub device flow started (user code {userCode}, interval {interval}s).");
        return new GitHubDeviceCode(
            deviceCode,
            userCode,
            string.IsNullOrEmpty(verificationUri) ? "https://github.com/login/device" : verificationUri,
            interval,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    /// <summary>
    ///     How many CONSECUTIVE transient failures the poll rides out before it
    ///     gives up. The user is sitting on github.com with a code in front of
    ///     them; one 502 from GitHub's edge must not end the whole flow with
    ///     "Response status code does not indicate success" and no way back except
    ///     starting over. Consecutive, so a single bad poll in a long wait costs
    ///     nothing, and bounded, so a hard outage still terminates — as does the
    ///     device code's own expiry, re-checked every iteration.
    /// </summary>
    private const int MaxConsecutiveTransientPolls = 5;

    public async Task<string> WaitForDeviceAuthorizationAsync(
        string clientId, GitHubDeviceCode deviceCode, CancellationToken cancellationToken = default)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, deviceCode.IntervalSeconds));
        var transientFailures = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deviceCode.ExpiresAt)
                throw new TimeoutException("The GitHub device code expired before the sign-in was confirmed.");

            await _delay(interval, cancellationToken).ConfigureAwait(false);

            string json;
            try
            {
                json = await PostFormAsync(AccessTokenUrl, new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = deviceCode.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientPollFailure(ex, cancellationToken))
            {
                transientFailures++;
                if (transientFailures > MaxConsecutiveTransientPolls)
                {
                    throw new InvalidOperationException(
                        $"GitHub's device-flow endpoint failed {MaxConsecutiveTransientPolls} times in a row " +
                        "— giving up on this sign-in.", ex);
                }

                // Exponential over the poll interval, capped at a minute so a
                // long outage does not stretch one wait past the code's expiry
                // check without ever reaching it.
                var backoff = TimeSpan.FromSeconds(Math.Min(
                    60.0, interval.TotalSeconds * Math.Pow(2, transientFailures - 1)));
                _logger?.Warning(AppLogCategory.Application,
                    $"GitHub device-flow poll failed transiently ({transientFailures}/" +
                    $"{MaxConsecutiveTransientPolls}); retrying in {backoff.TotalSeconds:0}s.", ex);
                await _delay(backoff, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Only a real ANSWER resets the streak — a 5xx run must stay a run.
            transientFailures = 0;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var token = JsonFields.StringOrEmpty(root, "access_token");
            if (!string.IsNullOrEmpty(token))
            {
                StoreToken(token);
                _logger?.Info(AppLogCategory.Application, "GitHub device flow completed — signed in.");
                return token;
            }

            switch (JsonFields.StringOrEmpty(root, "error"))
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    // GitHub sends the new minimum interval alongside slow_down;
                    // fall back to +5 s when it doesn't.
                    var next = JsonFields.Int32OrNumericText(root, "interval") ?? (int)interval.TotalSeconds + 5;
                    interval = TimeSpan.FromSeconds(Math.Max((int)interval.TotalSeconds + 5, next));
                    continue;
                case "expired_token":
                    throw new TimeoutException("The GitHub device code expired before the sign-in was confirmed.");
                case "access_denied":
                    throw new InvalidOperationException("The sign-in request was denied on github.com.");
                case var other:
                    throw new InvalidOperationException(
                        $"GitHub device flow failed: {(string.IsNullOrEmpty(other) ? "unexpected response" : other)}.");
            }
        }
    }

    /// <summary>
    ///     Whether a poll failure says something about the SERVER rather than
    ///     about this sign-in. A 5xx and a bare connection failure (an
    ///     <see cref="HttpRequestException" /> with no status) are the network
    ///     having a moment; an <see cref="HttpClient" /> timeout arrives as a
    ///     <see cref="TaskCanceledException" /> with the caller's token NOT
    ///     cancelled. Every 4xx is GitHub answering about the flow and is
    ///     terminal, as is the caller's own cancellation.
    /// </summary>
    private static bool IsTransientPollFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;

        return ex switch
        {
            HttpRequestException http => http.StatusCode is null or >= HttpStatusCode.InternalServerError,
            TaskCanceledException => true,
            TimeoutException => true,
            _ => false
        };
    }

    public void SignInWithPersonalAccessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token must not be empty.", nameof(token));
        StoreToken(token.Trim());
        _logger?.Info(AppLogCategory.Application, "GitHub sign-in via personal access token stored.");
    }

    public void SignOut()
    {
        lock (_tokenLock)
        {
            _store.Delete();
            _cachedToken = null;
            _tokenLoaded = true;
        }

        _logger?.Info(AppLogCategory.Application, "GitHub sign-out — stored token removed.");
        AuthStateChanged?.Invoke();
    }

    public async Task<string?> GetSignedInLoginAsync(CancellationToken cancellationToken = default)
    {
        var token = GetToken();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserUrl);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", "DotCharter");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return JsonFields.StringOrEmpty(doc.RootElement, "login") is { Length: > 0 } login ? login : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application, "Could not resolve the signed-in GitHub login.", ex);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    private void StoreToken(string token)
    {
        lock (_tokenLock)
        {
            _store.Save(token);
            _cachedToken = token;
            _tokenLoaded = true;
        }

        AuthStateChanged?.Invoke();
    }

    private async Task<string> PostFormAsync(
        string url, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "DotCharter");
        request.Content = new FormUrlEncodedContent(fields);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
