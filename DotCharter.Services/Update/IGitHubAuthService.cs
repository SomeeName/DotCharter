namespace DotCharter.Services.Update;

/// <summary>A pending GitHub Device Flow authorization.</summary>
public sealed record GitHubDeviceCode(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int IntervalSeconds,
    DateTimeOffset ExpiresAt);

/// <summary>
/// GitHub sign-in for private-repo update checks. Two paths:
/// the OAuth Device Flow (needs an OAuth app client id from AppConfig)
/// and a pasted personal access token. Either way the resulting token is
/// stored DPAPI-encrypted via <see cref="GitHubTokenStore"/>.
/// </summary>
public interface IGitHubAuthService
{
    bool IsSignedIn { get; }

    /// <summary>Raised on sign-in / sign-out.</summary>
    event Action? AuthStateChanged;

    /// <summary>The stored token, or null when signed out. Never log it.</summary>
    string? GetToken();

    /// <summary>Requests a device + user code pair from GitHub
    /// (POST github.com/login/device/code, scope "repo").</summary>
    Task<GitHubDeviceCode> StartDeviceFlowAsync(string clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Polls github.com/login/oauth/access_token until the user authorizes
    /// (honoring interval + slow_down), then stores and returns the token.
    /// Throws <see cref="TimeoutException"/> on expiry and
    /// <see cref="InvalidOperationException"/> on denial / protocol errors.
    /// </summary>
    Task<string> WaitForDeviceAuthorizationAsync(
        string clientId, GitHubDeviceCode deviceCode, CancellationToken cancellationToken = default);

    /// <summary>Stores a pasted personal access token.</summary>
    void SignInWithPersonalAccessToken(string token);

    void SignOut();

    /// <summary>GET api.github.com/user → login name, or null when signed
    /// out / token invalid / offline.</summary>
    Task<string?> GetSignedInLoginAsync(CancellationToken cancellationToken = default);
}
