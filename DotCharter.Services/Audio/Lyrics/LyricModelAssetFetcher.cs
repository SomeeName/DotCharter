using System.Net;
using System.Text.Json;

namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     Resolves a lyric-model archive to a GitHub release asset and streams it.
///     <para>
///         <b>Searched across releases, not pinned to one.</b> The models are
///         versioned in their own asset NAME (<c>lyric-align-en-v1.zip</c>) and
///         change far more rarely than the app, so pinning the lookup to "the
///         release that happens to carry them" would break the download on the
///         next app release that does not re-upload 640 MB of unchanged weights.
///         Finding the newest release that has the named asset keeps re-uploading
///         optional.
///     </para>
///     <para>
///         <b>Which means the scan has to REACH that release, and for a release
///         it does not.</b> A search of one page of 30 would miss it, because
///         <c>lyric-align-v1.zip</c> sits on <c>v0.41.0</c> — over a hundred
///         releases back — since it was published. The lookup would return
///         nothing, the installer would turn that into "not installed", and lyric
///         auto-sync could not fetch its own model at all. So it PAGES; see
///         <c>MaxReleasePages</c>. Anything that narrows the window again
///         re-breaks the feature silently, because the failure is a plain
///         "the model is not available".
///     </para>
///     <para>
///         Uses the api.github.com asset endpoint with
///         <c>Accept: application/octet-stream</c>, which is the form that works
///         for a private repo when a token is supplied — the same rule
///         <see cref="Update.GitHubUpdateService" /> follows.
///     </para>
/// </summary>
public sealed class LyricModelAssetFetcher : IDisposable
{
    private const string ApiVersion = "2022-11-28";

    /// <summary>
    ///     Releases per page. 100 is the API's maximum.
    /// </summary>
    private const int ReleasesPerPage = 100;

    /// <summary>
    ///     How many pages to walk before giving up.
    ///     <para>
    ///         <b>This must NOT be a single page of 30, which quietly breaks the
    ///         whole download.</b> The models are published once, on their own
    ///         asset names, precisely so that later releases need not re-upload
    ///         400 MB of unchanged weights — but the scan then has to REACH the
    ///         release that carries them. <c>lyric-align-v1.zip</c> has sat on
    ///         <c>v0.41.0</c> since it was published, more than a
    ///         hundred releases back, so a 30-release window finds nothing, the
    ///         installer turns the "no such asset" into <c>false</c>, and the user
    ///         is told the model "is not available" with no hint that the app has
    ///         simply not looked far enough. Measured: the asset is on
    ///         page 2 of a 100-per-page walk.
    ///     </para>
    ///     <para>
    ///         Five pages is 500 releases. The walk STOPS at the first empty page,
    ///         so a young repository still costs exactly one request, and a hit
    ///         stops immediately — the common case (the asset is old) is the only
    ///         one that pays.
    ///     </para>
    /// </summary>
    private const int MaxReleasePages = 5;

    private readonly HttpClient _http;
    private readonly Func<string?>? _tokenProvider;
    private readonly IAppLogger? _logger;
    private readonly string _owner;
    private readonly string _repo;

    public LyricModelAssetFetcher(
        Func<string?>? tokenProvider = null,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        string owner = Update.GitHubUpdateService.DefaultOwner,
        string repo = Update.GitHubUpdateService.DefaultRepo)
    {
        _tokenProvider = tokenProvider;
        _logger = logger;
        _owner = owner;
        _repo = repo;

        _http = handler != null
            ? new HttpClient(handler)
            // A process-lifetime connection pool never re-resolves DNS — the
            // classic singleton-HttpClient bug, and the same one
            // OnlineHttp.SharedHandler bounds for the Online/ and Update/
            // clients. SocketsHttpHandler rather than HttpClientHandler because
            // that setting only exists here.
            : new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            });

        // Header phase only — the model body streams with ResponseHeadersRead, so
        // a 340 MB download is not bound by this.
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    ///     The delegate <see cref="LyricModelInstaller" /> takes. Throws when the
    ///     asset cannot be found or fetched; the installer turns that into
    ///     "not installed" rather than letting it reach the caller.
    /// </summary>
    public async Task<Stream> FetchAsync(string assetName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);

        var url = await ResolveAssetUrlAsync(assetName, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException(
                      $"No release of {_owner}/{_repo} carries an asset named '{assetName}'.");

        using var request = Request(url, "application/octet-stream");
        var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // NOT `using var response`: on the success path the response OWNS the
        // stream we hand back, and disposing it here would close the download
        // out from under the installer. But every path that does NOT hand the
        // stream back has to release it — a 404, a rate-limit 403, a cancelled
        // read — or the connection stays checked out of the pool until a
        // finalizer happens to run. ResponseHeadersRead makes that a LIVE
        // socket, not a buffered body.
        try
        {
            response.EnsureSuccessStatusCode();

            _logger?.Info(AppLogCategory.Audio,
                $"Downloading lyric model asset '{assetName}' " +
                $"({response.Content.Headers.ContentLength / (1024 * 1024) ?? 0} MB).");

            // The response owns the stream; disposing it with the stream is the
            // caller's job (the installer wraps it in an await using).
            return await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<string?> ResolveAssetUrlAsync(string assetName, CancellationToken ct)
    {
        for (var page = 1; page <= MaxReleasePages; page++)
        {
            using var request = Request(
                $"https://api.github.com/repos/{_owner}/{_repo}/releases" +
                $"?per_page={ReleasesPerPage}&page={page}",
                "application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.Warning(AppLogCategory.Audio,
                    $"Could not list releases of {_owner}/{_repo} ({(int)response.StatusCode}).");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var url = FindAssetUrl(json, assetName);
            if (url != null) return url;

            // An empty page is the end of the list — stop rather than burning the
            // remaining requests (and the rate limit) on nothing.
            if (IsEmptyPage(json)) break;
        }

        _logger?.Warning(AppLogCategory.Audio,
            $"No release of {_owner}/{_repo} in the newest " +
            $"{MaxReleasePages * ReleasesPerPage} carries an asset named '{assetName}'.");
        return null;
    }

    /// <summary>
    ///     Whether a listing page holds no releases at all. Unparseable counts as
    ///     empty: the alternative is walking every remaining page over a response
    ///     that will never contain an asset.
    /// </summary>
    internal static bool IsEmptyPage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind != JsonValueKind.Array ||
                   document.RootElement.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Pure so the release-scan rule is testable without a network.</summary>
    internal static string? FindAssetUrl(string json, string assetName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            // Newest first, which is the order the API returns.
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (!release.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("name", out var name) ||
                        name.ValueKind != JsonValueKind.String ||
                        !string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (asset.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                        return url.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable listing reads as "no asset", never a throw into a download.
        }

        return null;
    }

    private HttpRequestMessage Request(string url, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        request.Headers.TryAddWithoutValidation("User-Agent",
            $"DotCharter (+https://github.com/{_owner}/{_repo})");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);

        var token = _tokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        return request;
    }

    public void Dispose() => _http.Dispose();
}
