using System.Net;
using System.Net.Http.Headers;
using JuceSharp.StemSeparation.Runtime;

namespace DotCharter.Services.Update;

/// <summary>One asset of one release of this app's own repository: the tag and the file name.</summary>
/// <param name="Tag">The release tag, e.g. <c>models-bs-roformer-sw-v1</c>.</param>
/// <param name="AssetName">The asset's file name on that release.</param>
public sealed record GitHubReleaseAssetRef(string Tag, string AssetName);

/// <summary>A download from the private repository was asked for with nobody signed in to GitHub.</summary>
public sealed class GitHubSignInRequiredException : InvalidOperationException
{
    /// <param name="message">What needed the sign-in.</param>
    public GitHubSignInRequiredException(string message) : base(message)
    {
    }
}

/// <summary>Downloads a pinned file that is published as a release asset of this app's own
/// repository — with the user's GitHub sign-in when the repository needs one.</summary>
public interface IGitHubReleaseAssetDownloader
{
    /// <summary>Whether the repository is private, so nothing downloads without a sign-in. The
    /// app's own downloader reads the public repo and answers false.</summary>
    bool RequiresSignIn { get; }

    /// <summary>Whether a GitHub token is stored. Without one the private repository's assets
    /// answer 404, so a caller shows the download as unavailable rather than offering it.</summary>
    bool IsSignedIn { get; }

    /// <summary>Raised after a sign-in or a sign-out.</summary>
    event Action? SignInChanged;

    /// <summary>
    /// Ensures <paramref name="destinationPath" /> holds the asset and that its SHA-256 and size
    /// are the pinned ones. An installed file that verifies is kept; one that does not is
    /// replaced. Nothing reaches the destination before it verifies.
    /// </summary>
    /// <param name="asset">Which release and which file.</param>
    /// <param name="destinationPath">Where the verified file goes.</param>
    /// <param name="sha256">The pinned lower-case hex SHA-256.</param>
    /// <param name="exactBytes">The pinned size.</param>
    /// <param name="progress">Bytes so far.</param>
    /// <param name="ct">Cancellation; keeps the partial file so the next call resumes.</param>
    /// <returns><paramref name="destinationPath" />.</returns>
    /// <exception cref="GitHubSignInRequiredException">The repository <see cref="RequiresSignIn" />
    /// and nobody is signed in.</exception>
    /// <exception cref="InvalidDataException">The release does not carry those bytes.</exception>
    Task<string> DownloadVerifiedAsync(
        GitHubReleaseAssetRef asset,
        string destinationPath,
        string sha256,
        long exactBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);
}

/// <summary>
/// <see cref="IGitHubReleaseAssetDownloader" /> on the updater's own mechanism: the signed-in
/// user's token (<see cref="IGitHubAuthService" />), the api.github.com asset endpoint with
/// <c>Accept: application/octet-stream</c> (the form that works for a PRIVATE repository; the
/// browser url does not), redirects followed to <c>objects.githubusercontent.com</c>, and every
/// request through <see cref="GitHubApiRequest" />, which refuses a host that is not GitHub's.
/// <para>
/// <b>Pinned, not trusted.</b> The release document says where the bytes are; the CALLER says
/// what they must be. The asset's declared size must equal the pin before a byte is fetched,
/// the body is never written past it, and the complete file's SHA-256 is checked BEFORE the
/// atomic rename (JuceSharp's <see cref="VerifiedDownload.VerifyFileAsync" />, the check every
/// other model download uses). A file that fails is deleted; a transfer that stops is kept as
/// <c>&lt;file&gt;.github.part</c> and resumed with a <c>Range</c> request.
/// </para>
/// <para>
/// <b>The token is never logged or stored again.</b> It is read from the auth service per
/// request and lives only in the request header.
/// </para>
/// </summary>
public sealed class GitHubReleaseAssetDownloader : IGitHubReleaseAssetDownloader, IDisposable
{
    /// <summary>The suffix of the resumable partial file beside the destination.</summary>
    public const string PartialSuffix = ".github.part";

    /// <summary>Deadline for the release lookup and for the headers of the asset request.</summary>
    public static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the body may go without a single byte before the transfer counts as
    /// stalled. A 358 MB model on a slow line is legitimately slow; one that stops is not.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<string?> _token;
    private readonly IAppLogger? _logger;
    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _userAgent;
    private readonly TimeSpan _apiTimeout;
    private readonly TimeSpan _stallTimeout;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <param name="tokenProvider">The signed-in user's token, or null. Read per request.</param>
    /// <param name="logger">Optional log; never sees the token.</param>
    /// <param name="handler">A test handler, or null for the real one.</param>
    /// <param name="owner">Repository owner.</param>
    /// <param name="repo">Repository name.</param>
    /// <param name="appVersion">For the User-Agent.</param>
    /// <param name="apiTimeout">Overrides <see cref="DefaultApiTimeout" />.</param>
    /// <param name="stallTimeout">Overrides <see cref="DefaultStallTimeout" />.</param>
    /// <param name="requiresSignIn">The repository is private: refuse to ask it without a token.</param>
    public GitHubReleaseAssetDownloader(
        Func<string?> tokenProvider,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        string owner = GitHubUpdateService.DefaultOwner,
        string repo = GitHubUpdateService.DefaultRepo,
        Version? appVersion = null,
        TimeSpan? apiTimeout = null,
        TimeSpan? stallTimeout = null,
        bool requiresSignIn = true)
    {
        RequiresSignIn = requiresSignIn;
        _token = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger;
        _owner = owner;
        _repo = repo;
        _userAgent = $"DotCharter/{(appVersion == null ? Infrastructure.AppVersionInfo.Current : UpdateVersionText.Format(appVersion))} (+https://github.com/{owner}/{repo})";
        _apiTimeout = apiTimeout ?? DefaultApiTimeout;
        _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _http = GitHubApiRequest.CreateClient(handler);
    }

    /// <summary>The app's downloader: the public repo, read anonymously, with the token and the
    /// sign-in events of <paramref name="auth" /> when someone is signed in.</summary>
    /// <param name="auth">The app's GitHub sign-in.</param>
    /// <param name="logger">Optional log.</param>
    /// <param name="appVersion">For the User-Agent.</param>
    /// <returns>A downloader that raises <see cref="SignInChanged" /> with the auth service.</returns>
    public static GitHubReleaseAssetDownloader For(IGitHubAuthService auth, IAppLogger? logger, Version? appVersion)
    {
        ArgumentNullException.ThrowIfNull(auth);
        var downloader = new GitHubReleaseAssetDownloader(auth.GetToken, logger, appVersion: appVersion,
            requiresSignIn: false);
        auth.AuthStateChanged += downloader.OnSignInChanged;
        return downloader;
    }

    /// <inheritdoc />
    public bool RequiresSignIn { get; }

    /// <inheritdoc />
    public bool IsSignedIn => !string.IsNullOrWhiteSpace(_token());

    /// <inheritdoc />
    public event Action? SignInChanged;

    /// <summary>The partial file a transfer to <paramref name="destinationPath" /> resumes from.</summary>
    /// <param name="destinationPath">The verified file's path.</param>
    /// <returns>The partial file's path.</returns>
    public static string PartialPathFor(string destinationPath) => destinationPath + PartialSuffix;

    /// <inheritdoc />
    public async Task<string> DownloadVerifiedAsync(
        GitHubReleaseAssetRef asset,
        string destinationPath,
        string sha256,
        long exactBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exactBytes);

        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await VerifiedDownload.VerifyFileAsync(destinationPath, sha256, exactBytes, ct).ConfigureAwait(false))
                return destinationPath;
            if (File.Exists(destinationPath))
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"'{Path.GetFileName(destinationPath)}' is on disk but does not match its pinned SHA-256; downloading it again.");
                File.Delete(destinationPath);
            }

            if (RequiresSignIn && string.IsNullOrWhiteSpace(_token()))
            {
                throw new GitHubSignInRequiredException(
                    $"'{asset.AssetName}' is published on {_owner}/{_repo}, which needs a GitHub sign-in " +
                    "(Settings, Updates).");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            var url = await ResolveAssetUrlAsync(asset, exactBytes, ct).ConfigureAwait(false);
            var partial = PartialPathFor(destinationPath);
            await FetchAsync(url, partial, exactBytes, progress, ct).ConfigureAwait(false);

            if (!await VerifiedDownload.VerifyFileAsync(partial, sha256, exactBytes, ct).ConfigureAwait(false))
            {
                File.Delete(partial);
                throw new InvalidDataException(
                    $"'{asset.AssetName}' from {asset.Tag} does not match its pinned SHA-256; it was discarded.");
            }

            File.Move(partial, destinationPath, overwrite: true);
            _logger?.Info(AppLogCategory.Application,
                $"Release asset '{asset.AssetName}' ({asset.Tag}) downloaded and verified ({exactBytes:N0} bytes).");
            return destinationPath;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>The asset's API url, read from its release BY TAG. The declared size must be
    /// the pinned one: a re-uploaded file is a different file, refused before it is fetched.</summary>
    private async Task<string> ResolveAssetUrlAsync(GitHubReleaseAssetRef asset, long exactBytes, CancellationToken ct)
    {
        using var budget = Budget(_apiTimeout, ct);
        using var request = GitHubApiRequest.Create(
            $"https://api.github.com/repos/{_owner}/{_repo}/releases/tags/{Uri.EscapeDataString(asset.Tag)}",
            "application/vnd.github+json", _userAgent, _token());
        using var response = await _http.SendAsync(request, budget.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // GitHub answers 404, not 403, when a token cannot SEE a private repository.
            throw new HttpRequestException(
                $"GitHub returned {(int)response.StatusCode} for release {asset.Tag} of {_owner}/{_repo}" +
                (response.StatusCode == HttpStatusCode.NotFound
                    ? " (the signed-in token cannot read this repository, or the release is gone)."
                    : "."),
                null, response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
        var release = GitHubReleaseRules.ParseLatestRelease(json)
                      ?? throw new InvalidDataException($"Unexpected release JSON for {asset.Tag}.");
        var found = release.Assets.FirstOrDefault(a =>
                        string.Equals(a.Name, asset.AssetName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Release {asset.Tag} carries no asset named '{asset.AssetName}'.");
        if (found.Size != exactBytes)
        {
            throw new InvalidDataException(
                $"Release {asset.Tag} declares '{asset.AssetName}' as {found.Size:N0} bytes; the pinned file is " +
                $"{exactBytes:N0}. Refusing a different file.");
        }

        return string.IsNullOrWhiteSpace(found.ApiUrl) ? found.BrowserUrl : found.ApiUrl;
    }

    /// <summary>Streams the asset into <paramref name="partial" />, resuming what is there.</summary>
    private async Task FetchAsync(
        string url, string partial, long exactBytes, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > exactBytes)
        {
            File.Delete(partial);
            have = 0;
        }

        if (have == exactBytes) return;

        using var budget = Budget(_apiTimeout, ct);
        using var request = GitHubApiRequest.Create(url, "application/octet-stream", _userAgent, _token());
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);

        HttpResponseMessage sent;
        try
        {
            sent = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"GitHub did not answer within {_apiTimeout.TotalSeconds:0.#} s.", ex);
        }

        using var response = sent;
        if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is not a prefix of this asset any more; start over next time.
            File.Delete(partial);
            throw new IOException($"The partial download of '{Path.GetFileName(partial)}' no longer matches; it was discarded.");
        }

        response.EnsureSuccessStatusCode();

        // A 206 continues at `have`; anything else is the whole body, so the partial file is
        // started over rather than appended to.
        var resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent &&
                      response.Content.Headers.ContentRange?.From == have;
        if (!resumed) have = 0;

        var remaining = exactBytes - have;
        if (response.Content.Headers.ContentLength is { } declared && declared > remaining)
        {
            throw new InvalidDataException(
                $"GitHub declares {declared:N0} bytes for a download with {remaining:N0} left; refusing a different file.");
        }

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            await using var target = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            var written = have;
            progress?.Report(new DownloadProgress(written, exactBytes, (double)written / exactBytes, resumed));
            while (true)
            {
                budget.CancelAfter(_stallTimeout);
                var read = await source.ReadAsync(buffer, budget.Token).ConfigureAwait(false);
                if (read <= 0) break;
                if (written + read > exactBytes)
                    throw new InvalidDataException($"GitHub sent more than the pinned {exactBytes:N0} bytes.");

                await target.WriteAsync(buffer.AsMemory(0, read), budget.Token).ConfigureAwait(false);
                written += read;
                progress?.Report(new DownloadProgress(written, exactBytes, (double)written / exactBytes, resumed));
            }

            if (written != exactBytes)
                throw new IOException($"The download stopped at {written:N0} of {exactBytes:N0} bytes; the next attempt resumes.");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The download stalled: no data for {_stallTimeout.TotalSeconds:0.#} s; the next attempt resumes.", ex);
        }
        catch (InvalidDataException)
        {
            BestEffortDelete.File(partial);
            throw;
        }
    }

    private static CancellationTokenSource Budget(TimeSpan timeout, CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        return cts;
    }

    private void OnSignInChanged() => SignInChanged?.Invoke();

    /// <inheritdoc />
    public void Dispose()
    {
        _http.Dispose();
        _oneAtATime.Dispose();
    }
}
