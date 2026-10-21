using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     Pure pieces of the TONE3000 OAuth 2.0 + PKCE flow: verifier/challenge
///     generation (RFC 7636 S256), the authorize-URL builder and the loopback
///     callback query parser. No I/O — everything here unit-tests directly;
///     <see cref="Tone3000AuthService"/> is the impure shell around it.
///
///     The loopback redirect binds port <see cref="CallbackPort"/> = 47821:
///     the dev/owner machines only allow inbound listeners in 47810–47829
///     (firewall rule, see the root AGENTS.md port allocation) and HTTP.SYS
///     lets a non-admin process listen on <c>http://localhost:…</c> without a
///     URL ACL. The owner must register <see cref="RedirectUri"/> for the
///     client id minted at tone3000.com/settings.
/// </summary>
public static class Tone3000OAuth
{
    /// <summary>Loopback callback port — inside the firewall-allowed 47810–47829
    /// range (47810/11/12/13/20 are taken).</summary>
    public const int CallbackPort = 47821;

    /// <summary>The redirect URI registered with the TONE3000 client id.</summary>
    public const string RedirectUri = "http://localhost:47821/tone3000/callback/";

    public const string AuthorizeUrl = Tone3000Client.ApiBase + "/oauth/authorize";
    public const string TokenUrl = Tone3000Client.ApiBase + "/oauth/token";

    /// <summary>A fresh high-entropy PKCE code verifier (43–128 unreserved
    /// chars per RFC 7636; this yields 64 base64url chars from 48 random bytes).</summary>
    public static string CreateCodeVerifier() =>
        Base64Url(RandomNumberGenerator.GetBytes(48));

    /// <summary>A fresh opaque state value for CSRF protection.</summary>
    public static string CreateState() =>
        Base64Url(RandomNumberGenerator.GetBytes(24));

    /// <summary>S256 code challenge for a verifier: BASE64URL(SHA256(ASCII(verifier))).</summary>
    public static string ComputeCodeChallenge(string codeVerifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    /// <summary>The browser URL that starts the flow.</summary>
    public static string BuildAuthorizeUrl(string clientId, string codeChallenge, string state) =>
        $"{AuthorizeUrl}" +
        $"?response_type=code" +
        $"&client_id={Uri.EscapeDataString(clientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
        $"&code_challenge_method=S256" +
        $"&state={Uri.EscapeDataString(state)}";

    /// <summary>Whether a loopback request's query string carries the state this
    /// sign-in generated. The listener's gate: a request without it is not ours and
    /// does not end the wait.</summary>
    public static bool CarriesState(string? query, string expectedState) =>
        string.Equals(HttpUtility.ParseQueryString(query ?? string.Empty).Get("state"),
            expectedState, StringComparison.Ordinal);

    /// <summary>
    ///     Parses the loopback redirect's query string ("?code=…&amp;state=…").
    ///     Returns the authorization code, or null with <paramref name="error"/>
    ///     set when the provider reported an error, the state does not match
    ///     (CSRF / stray request), or no code is present.
    /// </summary>
    public static string? TryParseCallback(string? query, string expectedState, out string error)
    {
        error = string.Empty;
        var parsed = HttpUtility.ParseQueryString(query ?? string.Empty);

        // STATE FIRST, before a single field of the response is believed. The
        // loopback listener answers whatever knocks on
        // http://localhost:47821/tone3000/callback/, and any page the user has
        // open can knock. Reading `error` ahead of the state check meant one
        // `?error=access_denied` from a stranger killed a sign-in that was going
        // fine — the exact request `state` exists to refuse.
        if (!string.Equals(parsed.Get("state"), expectedState, StringComparison.Ordinal))
        {
            error = "The sign-in response did not match this request (state mismatch).";
            return null;
        }

        var providerError = parsed.Get("error");
        if (!string.IsNullOrEmpty(providerError))
        {
            var description = parsed.Get("error_description");
            error = string.IsNullOrEmpty(description) ? providerError : $"{providerError}: {description}";
            return null;
        }

        var code = parsed.Get("code");
        if (string.IsNullOrEmpty(code))
        {
            error = "The sign-in response carried no authorization code.";
            return null;
        }

        return code;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
