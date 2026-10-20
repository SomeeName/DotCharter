using System.Net;
using DotCharter.Tone.Online;

namespace DotCharter.Services.Update;

/// <summary>
///     The one way this app builds a request to GitHub's release API, and the client that
///     sends it. Shared by the updater (<see cref="GitHubUpdateService" />) and the private
///     model download (<see cref="GitHubReleaseAssetDownloader" />), so the rule that keeps the
///     signed-in user's token on GitHub is written once.
///     <para>
///         <b>An https GitHub release host or nothing.</b> Every url either side requests is a
///         constant built from the owner and repo, or a string read out of a release document,
///         and the second kind is a claim the response makes. <c>SocketsHttpHandler</c> strips
///         <c>Authorization</c> across a cross-host REDIRECT, but a url that simply IS somewhere
///         else was never redirected, so the refusal has to happen here
///         (<c>UpdateCredentialScopeTests</c>).
///     </para>
/// </summary>
internal static class GitHubApiRequest
{
    /// <summary>The REST API version every request pins.</summary>
    internal const string ApiVersion = "2022-11-28";

    /// <summary>A GET for <paramref name="url" /> with the API headers and, when there is one,
    ///     the token as a bearer header.</summary>
    /// <param name="url">An https GitHub release host url (<see cref="GitHubReleaseRules.IsAllowedReleaseHost" />).</param>
    /// <param name="accept">The Accept header: the JSON media type for a release document,
    ///     <c>application/octet-stream</c> for an asset's bytes.</param>
    /// <param name="userAgent">GitHub refuses a request without one.</param>
    /// <param name="token">The signed-in user's token, or null. Never logged.</param>
    /// <returns>The request; the caller disposes it.</returns>
    /// <exception cref="InvalidDataException">The url is not an https GitHub release host.</exception>
    internal static HttpRequestMessage Create(string url, string accept, string userAgent, string? token)
    {
        if (!GitHubReleaseRules.IsAllowedReleaseHost(url))
        {
            throw new InvalidDataException(
                $"Refusing to request '{url}': that is not an https GitHub release host.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        return request;
    }

    /// <summary>
    ///     The client both sides send with: through <see cref="OnlineHttp.CreateClient" />
    ///     (which caps a buffered response), INFINITE on the client so every call carries its
    ///     own deadline, and on a private handler that follows redirects — GitHub answers an
    ///     asset read with a 302 to <c>objects.githubusercontent.com</c>.
    /// </summary>
    /// <param name="handler">A test handler, or null for the real one.</param>
    /// <returns>The client; the owner disposes it.</returns>
    internal static HttpClient CreateClient(HttpMessageHandler? handler) =>
        OnlineHttp.CreateClient(
            Timeout.InfiniteTimeSpan,
            handler ?? new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });
}
