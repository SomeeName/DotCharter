using System.IO.Compression;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Plugins;

namespace DotCharter.Services.Update;

/// <summary>
/// <see cref="IAppUpdateService"/> backed by the GitHub releases API.
/// Anonymous for public repos; when a token provider is supplied (GitHub
/// sign-in, see <see cref="GitHubAuthService"/>) every request carries an
/// <c>Authorization: Bearer</c> header so private-repo releases work too.
/// Which repo is the <see cref="IUpdateFeed" /> the app picked; a feed that
/// <see cref="IUpdateFeed.RequiresSignIn" /> is never asked without a token.
/// </summary>
public sealed class GitHubUpdateService : IAppUpdateService, IDisposable
{
    public const string DefaultOwner = "SomeeName";
    public const string DefaultRepo = "DotCharter";

    /// <summary>
    ///     The largest release asset this updater will ever write to disk. A
    ///     release is ~414 MB, so this is roughly ten times anything legitimate —
    ///     it exists to stop a hostile or broken host (or a redirect target, since
    ///     the handler follows redirects) filling the user's volume, not to
    ///     second-guess a real release.
    /// </summary>
    public const long MaxAssetBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    ///     Largest <c>SHA256SUMS.txt</c> this will read into a string. A release
    ///     digests a handful of assets, so a real one is a few hundred bytes; the
    ///     ceiling exists because the file comes from a host we do not control and
    ///     is read whole.
    /// </summary>
    public const int MaxChecksumManifestBytes = 256 * 1024;

    /// <summary>
    ///     Largest <c>payload-manifest.json</c> this will read into memory. It is
    ///     NOT <see cref="MaxChecksumManifestBytes" />: a real one is ~66 KB today
    ///     because it carries a line per payload file, and that count grows with
    ///     the product — a ceiling sized to today's file quietly disables partial
    ///     updates the release it is exceeded, which costs bandwidth silently
    ///     rather than failing loudly. Generous, but finite, because the body is
    ///     buffered whole off a host we do not control.
    /// </summary>
    public const int MaxPayloadManifestBytes = 4 * 1024 * 1024;

    /// <summary>
    ///     Deadline for one metadata exchange — the release JSON, the payload
    ///     manifest — covering headers AND body. <see cref="HttpClient.Timeout" />
    ///     cannot be that deadline for this service: the downloads stream with
    ///     <c>ResponseHeadersRead</c>, where it bounds the header exchange and
    ///     nothing after it, so one shared value is either far too short for a
    ///     414 MB asset or no bound at all on a metadata call. Same split, same
    ///     reason, as <c>Online/Tone3000/Tone3000Client</c>.
    /// </summary>
    public static readonly TimeSpan DefaultApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Deadline for the whole full-asset download. Deliberately generous — a
    ///     414 MB release on a domestic line is legitimately slow — but finite: a
    ///     host that sends headers and then dribbles bytes forever otherwise
    ///     stalls the update with nothing to cancel it.
    /// </summary>
    public static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Deadline for ONE range fetch. Short, because a range is at most 16 MiB
    ///     and because a range fetch that gives up costs only the optimisation —
    ///     every refusal falls back to the full download.
    /// </summary>
    public static readonly TimeSpan DefaultRangeTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly Func<string?>? _tokenProvider;
    private readonly IAppLogger? _logger;
    private readonly string _owner;
    private readonly string _repo;
    private readonly bool _requiresSignIn;
    private readonly string _installDir;
    private readonly string _downloadDir;
    private readonly TimeSpan _apiTimeout;
    private readonly TimeSpan _downloadTimeout;
    private readonly TimeSpan _rangeTimeout;
    private readonly UpdateInstaller _installer;
    private readonly IReadOnlyList<string> _pluginFiles;
    private readonly byte[] _releaseSigningKey;

    /// <summary>
    ///     Every zip this service produced and verified, by full path, with the
    ///     SHA-256 it had at that moment. <see cref="ApplyDownloadedUpdate" /> applies
    ///     nothing else, and re-hashes against this before staging: the zip
    ///     sits in a user-writable folder between download and apply.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _verifiedDownloads = new(StringComparer.OrdinalIgnoreCase);

    public GitHubUpdateService(
        Version currentVersion,
        Func<string?>? tokenProvider = null,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        string owner = DefaultOwner,
        string repo = DefaultRepo,
        string? installDir = null,
        string? downloadDir = null,
        TimeSpan? apiTimeout = null,
        TimeSpan? downloadTimeout = null,
        TimeSpan? rangeTimeout = null,
        bool requiresSignIn = false,
        string? currentVersionText = null,
        IReadOnlyList<string>? pluginFiles = null,
        byte[]? releaseSigningKey = null)
    {
        CurrentVersion = currentVersion;
        CurrentVersionText = GitHubReleaseRules.TryParseVersionFromTag(currentVersionText ?? GitHubReleaseRules.FormatVersion(currentVersion))
            ?? throw new ArgumentException("Expected a SemVer version.", nameof(currentVersionText));
        _tokenProvider = tokenProvider;
        _logger = logger;
        _owner = owner;
        _repo = repo;
        _requiresSignIn = requiresSignIn;
        Feed = new UpdateFeed(owner, repo, requiresSignIn);
        // The install's TOP, not app\ — the folder the payload goes over.
        _installDir = installDir ?? InstallLocation.InstallRoot;
        _downloadDir = downloadDir ?? Path.Combine(WritablePaths.ConfigRoot, "updates");
        _apiTimeout = apiTimeout ?? DefaultApiTimeout;
        _downloadTimeout = downloadTimeout ?? DefaultDownloadTimeout;
        _rangeTimeout = rangeTimeout ?? DefaultRangeTimeout;
        // The install half — apply, pending, launch — lives in UpdateInstaller; this
        // class keeps the release check and the downloads and forwards the rest.
        // The plugins this app declares; an install carrying one never takes a payload
        // without it (PluginUpdateGuard).
        _pluginFiles = pluginFiles ?? PluginLoader.DeclaredFiles(Assembly.GetEntryAssembly());
        _installer = new UpdateInstaller(_installDir, logger, _pluginFiles, _downloadDir);
        // The key every release's SHA256SUMS.txt must be signed with; tests pass their own.
        _releaseSigningKey = releaseSigningKey ?? ReleaseSignature.PinnedPublicKey();

        // Through OnlineHttp.CreateClient, which applies MaxResponseContentBufferSize:
        // the release JSON and the payload manifest are read with ReadAsStringAsync,
        // which is unbounded on its own, and both come from a host we do not control.
        // The cap does NOT touch the downloads — those stream with
        // ResponseHeadersRead and carry their own explicit byte bounds.
        // The handler stays ours rather than the shared pooled one: this client
        // must follow redirects (GitHub 302s asset reads to
        // objects.githubusercontent.com) and there is exactly one of these per
        // process, so a private pool costs nothing.
        //
        // INFINITE on the client, a linked CTS per call. The old 30 s here bounded
        // the HEADER exchange and nothing else — its own comment said so and
        // offered no replacement — so a host that answered and then dribbled bytes
        // stalled an update indefinitely. Every call below now carries a budget
        // sized for what it is actually doing. GitHubApiRequest.CreateClient is the shared
        // shape (the private model download sends through the same one).
        _http = GitHubApiRequest.CreateClient(handler);
    }

    /// <summary>
    ///     A deadline for ONE exchange, headers AND body, linked to the caller's
    ///     token so a user cancel still wins. Dispose it with the exchange.
    /// </summary>
    private static CancellationTokenSource Budget(TimeSpan timeout, CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(timeout);
        return cts;
    }

    public Version CurrentVersion { get; }

    public string CurrentVersionText { get; }

    /// <summary>The releases this service reads.</summary>
    public IUpdateFeed Feed { get; private init; }

    /// <summary>
    ///     The update service over <paramref name="feed" />: the app's composition root builds it
    ///     from <see cref="UpdateFeed.Select" />, so a loaded plugin's feed wins over the public one.
    /// </summary>
    public static GitHubUpdateService For(
        IUpdateFeed feed, Version currentVersion, Func<string?>? tokenProvider, IAppLogger? logger,
        HttpMessageHandler? handler = null, string? currentVersionText = null) =>
        new(currentVersion, tokenProvider, logger, handler, owner: feed.Owner, repo: feed.Repo,
            requiresSignIn: feed.RequiresSignIn, currentVersionText: currentVersionText)
        {
            Feed = feed
        };

    /// <inheritdoc cref="UpdateInstaller.ExtractionBudgetOverrideForTests" />
    internal long? ExtractionBudgetOverrideForTests
    {
        get => _installer.ExtractionBudgetOverrideForTests;
        set => _installer.ExtractionBudgetOverrideForTests = value;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        // A private feed asked anonymously answers 404, which reads as "no releases". Never
        // ask it without a token; say what the user has to do instead.
        if (_requiresSignIn && string.IsNullOrWhiteSpace(_tokenProvider?.Invoke()))
        {
            _logger?.Info(AppLogCategory.Application,
                $"Update check: {_owner}/{_repo} needs a GitHub sign-in and none is stored; not asked.");
            return new UpdateCheckResult(UpdateCheckStatus.SignInRequired,
                Error: "sign in to GitHub to get updates for this build");
        }

        try
        {
            using var budget = Budget(_apiTimeout, cancellationToken);
            // Beta tags are published as normal releases marked Latest, so this endpoint includes them.
            using var request = CreateRequest(
                $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest",
                "application/vnd.github+json");
            using var response = await _http.SendAsync(request, budget.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // GitHub answers 404 (not 403) when a token can't SEE a
                // private repo — so "signed in but 404" almost always means
                // an under-scoped token, not a bad login. Say so.
                var signedIn = !string.IsNullOrWhiteSpace(_tokenProvider?.Invoke());
                var hint = signedIn
                    ? "the signed-in token cannot access this repository — fine-grained tokens need this " +
                      "repo selected with 'Contents: Read'; classic tokens need the 'repo' scope"
                    : "no releases, private repo without sign-in, or rate limit";
                return new UpdateCheckResult(UpdateCheckStatus.Failed,
                    Error: $"GitHub returned {(int)response.StatusCode} for {_owner}/{_repo} ({hint}).");
            }
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            var release = GitHubReleaseRules.ParseLatestRelease(json);
            if (release == null)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "Unexpected release JSON from GitHub.");

            var latest = GitHubReleaseRules.TryParseVersionFromTag(release.TagName);
            if (latest == null || !Version.TryParse(latest.Split('-')[0], out var numericVersion))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed,
                    Error: $"Release tag '{release.TagName}' is not a vX.Y.Z version.");
            }

            if (GitHubReleaseRules.CompareVersions(latest, CurrentVersionText) <= 0)
            {
                _logger?.Info(AppLogCategory.Application,
                    $"Update check: up to date (latest {release.TagName}, current {CurrentVersionText}).");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
            }

            var asset = GitHubReleaseRules.SelectWinX64Asset(release.Assets);
            if (asset == null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed,
                    Error: $"Release {release.TagName} has no win-x64 zip asset.");
            }

            // The manifest turns that one zip into something range-addressable:
            // with it, the download is only the files this install is missing.
            // Without it the full zip is always correct, which is what makes
            // attempting the partial path safe.
            var manifest = GitHubReleaseRules.SelectManifestAsset(release.Assets);
            _logger?.Info(AppLogCategory.Application,
                $"Update check: {release.TagName} available (current {CurrentVersionText}), asset '{asset.Name}' " +
                $"({asset.Size:N0} bytes){(manifest == null ? " — no payload manifest, full download only" : "")}.");
            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                new UpdateInfo(numericVersion, release.TagName, release.Body, release.HtmlUrl,
                    asset.Name, asset.ApiUrl, asset.BrowserUrl, asset.Size, manifest?.ApiUrl));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The budget, not the user. Say which — "a task was canceled" reads as
            // a bug in DotCharter rather than as a host that stopped answering.
            var message = $"GitHub did not answer within {_apiTimeout.TotalSeconds:0.#} s.";
            _logger?.Warning(AppLogCategory.Application, message);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: message);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Application, "Update check failed.", ex);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }
    }

    public async Task<string> DownloadUpdateAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_downloadDir);

        // WHERE the bytes come from, before anything asks for them. The asset url
        // is read out of the release document, so it is data: a doctored release
        // could name any host at all, and the allow-list must gate not only whether
        // the TOKEN is attached, but also whether the app downloads from there.
        RefuseAForeignHost(ResolveAssetUrl(update), update);
        if (!string.IsNullOrWhiteSpace(update.ManifestAssetApiUrl))
            RefuseAForeignHost(update.ManifestAssetApiUrl, update);

        // …and WHAT they must hash to, fetched once for both download paths and
        // signature-checked against the pinned key. A release without a valid
        // signature THROWS here rather than proceeding unverified.
        var digests = await ResolveReleaseDigestsAsync(update, cancellationToken).ConfigureAwait(false);

        // Only the bytes this install is missing, out of the one full zip. Any
        // refusal at all — no manifest, a server that will not serve ranges, a zip
        // we do not understand, a hash that does not match — returns null and the
        // untouched full download below runs instead. That fallback is what makes
        // the whole scheme safe to attempt.
        var partial = await TryDownloadPartialAsync(update, digests, progress, cancellationToken)
            .ConfigureAwait(false);
        if (partial != null) return partial;

        return await DownloadFullAsync(update, digests, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The url a download actually goes to: the api asset endpoint when
    /// the release names one (it works for private repos), else the browser url.</summary>
    private static string ResolveAssetUrl(UpdateInfo update)
        => string.IsNullOrWhiteSpace(update.AssetApiUrl) ? update.AssetBrowserUrl : update.AssetApiUrl;

    /// <summary>Refuses a download url that is not an https GitHub release host.</summary>
    private static void RefuseAForeignHost(string url, UpdateInfo update)
    {
        if (GitHubReleaseRules.IsAllowedReleaseHost(url)) return;

        throw new InvalidDataException(
            $"Refusing to download {update.TagName} from '{url}': that is not an https GitHub release host. " +
            "The url comes out of the release document, so it is a claim the response makes, not something " +
            "this app knows.");
    }

    /// <summary>
    ///     The release's published digests, fetched ONCE for both download paths,
    ///     and only after the publisher signature over them verifies against the
    ///     pinned key (<see cref="ReleaseSignature" />). Every failure THROWS:
    ///     an unreachable manifest, a missing or wrong signature and a manifest
    ///     signed for another tag are all states someone who controls the release
    ///     page can arrange, and the bytes these digests vouch for are copied over
    ///     the install.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveReleaseDigestsAsync(
        UpdateInfo update, CancellationToken cancellationToken)
    {
        var manifest = await TryFetchReleaseAssetBytesAsync(update.TagName, ReleaseChecksums.AssetName,
            MaxChecksumManifestBytes, cancellationToken).ConfigureAwait(false);
        if (manifest == null || manifest.Length == 0)
        {
            throw new InvalidDataException(
                $"{update.TagName}: the signed SHA-256 digests every release publishes could not be read, so " +
                "nothing can say these bytes are the build they claim to be. This is usually a network or " +
                "rate-limit problem — try again; the download is refused rather than applied unverified.");
        }

        var signature = await TryFetchReleaseAssetBytesAsync(update.TagName, ReleaseSignature.AssetName,
            ReleaseSignature.MaxSignatureBytes, cancellationToken).ConfigureAwait(false);
        var problem = ReleaseSignature.Check(manifest, signature, update.TagName, _releaseSigningKey);
        if (problem != null)
        {
            _logger?.Warning(AppLogCategory.Application, $"Refused {update.TagName}: {problem}.");
            throw new InvalidDataException(
                $"Refusing {update.TagName}: {problem}. The update is not applied.");
        }

        var digests = ReleaseChecksums.Parse(DecodeUtf8(manifest));
        if (digests.Count == 0)
        {
            throw new InvalidDataException(
                $"{update.TagName}: the signed {ReleaseChecksums.AssetName} lists no digests.");
        }

        _logger?.Info(AppLogCategory.Application,
            $"{update.TagName}: {ReleaseChecksums.AssetName} carries a valid release signature.");
        return digests;
    }

    private async Task<string> DownloadFullAsync(
        UpdateInfo update,
        IReadOnlyDictionary<string, string> digests,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(_downloadDir, Path.GetFileName(update.AssetName));

        // The api.github.com asset url + octet-stream accept works for both
        // public and private repos (browser_download_url does not for private).
        var url = ResolveAssetUrl(update);

        // Headers AND body get a deadline. With ResponseHeadersRead the header
        // exchange is the only thing HttpClient.Timeout could ever have bounded,
        // so the stream below ran under the caller's token alone — and the UI
        // passes one with no deadline. A host that answers and then dribbles bytes
        // must cost one timeout, not forever.
        using var budget = Budget(_apiTimeout, cancellationToken);
        var ct = budget.Token;

        using var request = CreateRequest(url, "application/octet-stream");
        HttpResponseMessage sent;
        try
        {
            sent = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The update host did not answer within {_apiTimeout.TotalSeconds:0.#} s.", ex);
        }

        using var response = sent;
        response.EnsureSuccessStatusCode();

        // Headers are in: swap the short header deadline for the transfer one. A
        // 414 MB asset is legitimately slow; a 414 MB asset that never arrives is
        // not, and both need a different number.
        budget.CancelAfter(_downloadTimeout);

        // The declared length is the only thing between the user's disk and a body
        // whose size the far side chooses, so it is checked BEFORE a byte lands and
        // the copy below refuses to write past it. The download target is
        // server-chosen (the release JSON names the asset url) and the handler
        // follows redirects, so any host in that chain could otherwise stream until
        // the volume filled — AppPayload.CheckArchiveBounds only runs once the
        // whole thing has already landed.
        var declared = response.Content.Headers.ContentLength ?? update.AssetSizeBytes;
        if (declared > MaxAssetBytes)
        {
            throw new InvalidDataException(
                $"The update asset declares {declared:N0} bytes, past the {MaxAssetBytes:N0}-byte ceiling.");
        }

        // A host that declares nothing still gets a ceiling; it just cannot also
        // be held to an exact length afterwards.
        var total = declared > 0 ? declared : 0;
        var ceiling = total > 0 ? total : MaxAssetBytes;
        try
        {
            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while (written < ceiling &&
                       (read = await source.ReadAsync(
                           buffer.AsMemory(0, (int)Math.Min(buffer.Length, ceiling - written)),
                           ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;
                    if (total > 0)
                        progress?.Report(Math.Clamp((double)written / total, 0.0, 1.0));
                }
            }

            if (total > 0 && written != total)
                throw new IOException($"Truncated update download: {written:N0}/{total:N0} bytes.");
            if (total <= 0 && written >= ceiling)
            {
                throw new InvalidDataException(
                    $"The update asset declared no length and streamed past the {MaxAssetBytes:N0}-byte ceiling.");
            }

            // Verify the archive is a readable, plausibly-sized zip before we
            // ever offer to apply it — from the end-of-central-directory record,
            // so nothing materialises an entry per member to answer it.
            var bounds = AppPayload.CheckArchiveBounds(targetPath);
            if (bounds != null)
                throw new InvalidDataException($"Downloaded update archive: {bounds}.");

            // …and "it is a readable zip of a plausible shape" is not "it is the
            // asset we published". The partial path hashes EVERY file it fetches
            // against payload-manifest.json; the full path had no content check at
            // all, on bytes that arrive over a chain of redirects from hosts we do
            // not control and are then robocopied over the install. The digests
            // are published with every release — this is the reader.
            await VerifyAgainstReleaseChecksumsAsync(targetPath, update, digests, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget, not the user — say which, and leave nothing half-written.
            BestEffortDelete.File(targetPath);
            throw new TimeoutException(
                $"The update download stalled: no progress within {_downloadTimeout.TotalMinutes:0.#} minutes.", ex);
        }
        catch
        {
            BestEffortDelete.File(targetPath);
            throw;
        }

        progress?.Report(1.0);
        _logger?.Info(AppLogCategory.Application,
            $"Update {update.TagName} downloaded to '{targetPath}'.");
        return targetPath;
    }

    /// <summary>
    ///     Checks a downloaded full asset against the release's signed
    ///     <c>SHA256SUMS.txt</c> and remembers the digest for the re-hash at apply.
    ///     Throws <see cref="InvalidDataException" /> when the digests disagree or
    ///     the manifest does not list the asset — renaming the asset out of the
    ///     manifest must not be a way around the check. The caller deletes the file.
    /// </summary>
    private async Task VerifyAgainstReleaseChecksumsAsync(
        string archivePath,
        UpdateInfo update,
        IReadOnlyDictionary<string, string> digests,
        CancellationToken cancellationToken)
    {
        var assetName = Path.GetFileName(update.AssetName);
        if (!digests.TryGetValue(assetName, out var expected))
        {
            throw new InvalidDataException(
                $"{update.TagName}: {ReleaseChecksums.AssetName} does not list '{assetName}', so nothing " +
                "says these bytes are the build they claim to be. A release that publishes digests must " +
                "digest the asset it ships.");
        }

        var actual = await ReleaseChecksums.ComputeSha256Async(archivePath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"'{assetName}' does not match the SHA-256 published for {update.TagName} " +
                $"(expected {expected}, got {actual}). This is not the build it claims to be.");
        }

        RememberVerified(archivePath, actual);
        _logger?.Info(AppLogCategory.Application,
            $"'{assetName}' matches the SHA-256 published for {update.TagName}.");
    }

    /// <summary>Records a zip this service verified (or assembled from verified
    /// parts), so <see cref="ApplyDownloadedUpdate" /> can re-hash against it.</summary>
    private void RememberVerified(string zipPath, string sha256)
        => _verifiedDownloads[Path.GetFullPath(zipPath)] = sha256;

    /// <summary>Every member of a partial zip was hashed against the signed manifest
    /// as it landed; the zip itself is ours, so its digest is taken now, for the
    /// re-hash at apply.</summary>
    private async Task RememberAssembledAsync(string zipPath, CancellationToken cancellationToken)
        => RememberVerified(zipPath, await ReleaseChecksums.ComputeSha256Async(zipPath, cancellationToken)
            .ConfigureAwait(false));

    /// <summary>Test seam: marks a hand-built zip as if this service had downloaded
    /// and verified it, so the apply path's OTHER refusals stay reachable.</summary>
    internal void TrustDownloadForTests(string zipPath)
        => RememberVerified(zipPath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))).ToLowerInvariant());

    /// <summary>
    ///     Builds the update payload out of only the bytes this install does not
    ///     already have, and hands back a small zip the UNCHANGED apply path
    ///     accepts. Null means "use the full download" — for any reason at all.
    ///     <para>
    ///         The result is a zip rather than a directory on purpose: everything
    ///         downstream (<see cref="ApplyDownloadedUpdate" />'s bounds check,
    ///         <c>BoundedZipExtractor</c>, <see cref="AppPayload.ValidateDirectory" />,
    ///         <see cref="PayloadDeletions" />, <see cref="UpdaterScriptBuilder" />)
    ///         then runs byte-identically to the full-zip case. A partial update is
    ///         a smaller DOWNLOAD, never a second way of writing to disk.
    ///     </para>
    /// </summary>
    private async Task<string?> TryDownloadPartialAsync(
        UpdateInfo update,
        IReadOnlyDictionary<string, string> digests,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(update.ManifestAssetApiUrl)) return null;

        var stagingDir = Path.Combine(_downloadDir, PartialStagingDirName);
        var zipPath = Path.Combine(_downloadDir, "DotCharter-v" + update.TagName.TrimStart('v', 'V') + "-partial-win-x64.zip");

        try
        {
            var installLayout = AppPayload.ClassifyDirectory(_installDir);
            if (installLayout == InstallLayout.Unknown)
            {
                _logger?.Info(AppLogCategory.Application,
                    "Partial update skipped: this install's layout is unrecognised.");
                return null;
            }

            // Bounded, and read as BYTES rather than text: the digest published
            // for this asset is over the file's bytes, so decoding first and
            // re-encoding to compare would be comparing something else.
            var manifestBytes = await GetBytesAsync(
                update.ManifestAssetApiUrl, MaxPayloadManifestBytes, cancellationToken).ConfigureAwait(false);
            if (manifestBytes == null)
            {
                _logger?.Info(AppLogCategory.Application,
                    "Partial update skipped: the release's payload manifest could not be read.");
                return null;
            }

            // The manifest AUTHORISES every byte this path writes to disk — it
            // says which files are missing and what each one must hash to — and
            // it is checked too, not the one document nothing checks, while the
            // full-asset path cross-checks its zip against the same file.
            // Falling back to the full download is not a downgrade: that path
            // digests what it fetched and REFUSES a mismatch.
            if (!PayloadManifestMatchesPublishedDigest(manifestBytes, update, digests))
                return null;

            // Never fetch the copies only a pre-0.63 updater needs.
            var published = PayloadManifest.FromJson(DecodeUtf8(manifestBytes));
            var release = published == null ? null : LegacyUpdaterShim.StripFrom(published);
            if (release == null || release.Files.Count == 0)
            {
                _logger?.Info(AppLogCategory.Application,
                    "Partial update skipped: the release's payload manifest did not parse.");
                return null;
            }

            // An install with plugins never downloads a payload without them: the published
            // manifest is the whole release, so it says before any byte is fetched.
            var pluginRefusal = PluginUpdateGuard.Check(
                PluginUpdateGuard.MissingFromManifest(release, PluginUpdateGuard.InstalledPlugins(_installDir, _pluginFiles)),
                update.TagName);
            if (pluginRefusal != null)
                throw new PluginUpdateRefusedException($"Refusing to download {update.TagName}: {pluginRefusal}.");

            if (release.Layout != installLayout)
            {
                _logger?.Info(AppLogCategory.Application,
                    $"Partial update skipped: the release is {AppPayload.Describe(release.Layout)} " +
                    $"and this install is {AppPayload.Describe(installLayout)}.");
                return null;
            }

            if (AppPayload.CheckShape(AppPayload.IsSplitDirectory(_installDir), release.IsSplit) is { } shape)
            {
                _logger?.Info(AppLogCategory.Application, $"Partial update skipped: {shape}.");
                return null;
            }

            BestEffortDelete.Directory(stagingDir);
            BestEffortDelete.File(zipPath);

            var installed = InstalledPayloadReader.FromDirectory(release, _installDir);
            var downloader = new PartialUpdateDownloader(
                new HttpAssetRangeFetcher(_http, CreateRequest, _rangeTimeout), _logger);

            var url = string.IsNullOrWhiteSpace(update.AssetApiUrl) ? update.AssetBrowserUrl : update.AssetApiUrl;
            var result = await downloader.FetchAsync(
                    url, update.AssetSizeBytes, release, installed, stagingDir,
                    progress: progress, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                _logger?.Info(AppLogCategory.Application,
                    $"Partial update not used ({result.Outcome}: {result.Reason}) — downloading the full asset.");
                BestEffortDelete.Directory(stagingDir);
                return null;
            }

            // The payload describes itself, so this install is the FROM side of the
            // next update without re-hashing 414 MB.
            var manifestPath = Path.Combine(stagingDir, release.InstalledPath);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllText(manifestPath, release.ToJson());
            if (result.Deletions.Count > 0)
            {
                File.WriteAllText(Path.Combine(stagingDir, PayloadDeletions.FileName),
                    PayloadDeletions.Write(result.Deletions));
            }

            // The same validator the apply path runs, one step early: a payload it
            // would refuse must become a full download here, not a failed update
            // after the user pressed install.
            var problem = AppPayload.ValidateDirectory(stagingDir, installLayout);
            if (problem != null)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Partial update rejected before staging: {problem} — downloading the full asset.");
                BestEffortDelete.Directory(stagingDir);
                return null;
            }

            ZipFile.CreateFromDirectory(stagingDir, zipPath, CompressionLevel.NoCompression, includeBaseDirectory: false);
            BestEffortDelete.Directory(stagingDir);

            var bounds = AppPayload.CheckArchiveBounds(zipPath);
            if (bounds != null)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Partial update payload: {bounds} — downloading the full asset.");
                BestEffortDelete.File(zipPath);
                return null;
            }

            await RememberAssembledAsync(zipPath, cancellationToken).ConfigureAwait(false);
            progress?.Report(1.0);
            _logger?.Info(AppLogCategory.Application,
                $"Update {update.TagName} assembled from {result.BytesFetched:N0} downloaded bytes " +
                $"({result.FilesWritten} file(s), {result.Deletions.Count} deletion(s)) instead of " +
                $"{update.AssetSizeBytes:N0} — '{zipPath}'.");
            return zipPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BestEffortDelete.Directory(stagingDir);
            BestEffortDelete.File(zipPath);
            throw;
        }
        catch (PluginUpdateRefusedException ex)
        {
            _logger?.Warning(AppLogCategory.Application, ex.Message);
            BestEffortDelete.Directory(stagingDir);
            BestEffortDelete.File(zipPath);
            throw;
        }
        catch (Exception ex)
        {
            // A partial download is an optimisation. Nothing it can do may turn
            // into a failed update, so every fault here degrades to the full zip.
            _logger?.Warning(AppLogCategory.Application,
                "Partial update failed — downloading the full asset.", ex);
            BestEffortDelete.Directory(stagingDir);
            BestEffortDelete.File(zipPath);
            return null;
        }
    }

    /// <summary>
    ///     One bounded metadata GET returning the RAW bytes, for a body whose
    ///     published digest is over the file rather than over its text.
    ///     Null means "could not be read" for every reason — a status, a declared
    ///     length past the ceiling, or a body that streamed past it anyway.
    /// </summary>
    private async Task<byte[]?> GetBytesAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        using var budget = Budget(_apiTimeout, cancellationToken);
        using var request = CreateRequest(url, "application/octet-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        if (response.Content.Headers.ContentLength > maxBytes) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var buffered = new MemoryStream();
        var buffer = new byte[8192];
        while (buffered.Length <= maxBytes)
        {
            var count = (int)Math.Min(buffer.Length, maxBytes - buffered.Length + 1);
            var read = await stream.ReadAsync(buffer.AsMemory(0, count), budget.Token).ConfigureAwait(false);
            if (read == 0) return buffered.ToArray();
            buffered.Write(buffer, 0, read);
        }

        return null;
    }

    /// <summary>UTF-8, with a byte-order mark tolerated — <c>JsonSerializer</c>
    /// over a string does not accept one.</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    /// <summary>
    ///     Cross-checks the release's <c>payload-manifest.json</c> against the
    ///     digest published for it in the signed <c>SHA256SUMS.txt</c> — the same
    ///     document the full-asset path checks its zip against
    ///     (<see cref="VerifyAgainstReleaseChecksumsAsync" />).
    ///     <para>
    ///         False means the signed digests do not name this file, or it is not
    ///         the file we published. The caller then takes
    ///         the FULL download, which digests its own bytes and refuses a mismatch
    ///         outright; "fall back" is never "trust it".
    ///     </para>
    /// </summary>
    private bool PayloadManifestMatchesPublishedDigest(
        byte[] manifestBytes, UpdateInfo update, IReadOnlyDictionary<string, string> digests)
    {
        if (!digests.TryGetValue(PayloadManifest.FileName, out var expected))
        {
            _logger?.Warning(AppLogCategory.Application,
                $"{update.TagName}: {ReleaseChecksums.AssetName} does not list " +
                $"'{PayloadManifest.FileName}'. Taking the full download instead — that one IS digest-verified, " +
                "and a partial download assembles a zip no published digest can describe.");
            return false;
        }

        var actual = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Warning(AppLogCategory.Application,
                $"{update.TagName}: '{PayloadManifest.FileName}' does not match the SHA-256 published " +
                $"for it (expected {expected}, got {actual}). Taking the full download instead — " +
                "the document that decides which bytes land on disk is not the one we published.");
            return false;
        }

        _logger?.Info(AppLogCategory.Application,
            $"'{PayloadManifest.FileName}' matches the SHA-256 published for {update.TagName}.");
        return true;
    }

    /// <summary>
    ///     Downloads the release's <c>SHA256SUMS.txt</c> for one version, or null
    ///     when it cannot be reached.
    ///     <para>
    ///         <b>The asset must be read, not just published.</b> The release script
    ///         emits it;
    ///         <see cref="VerifyAgainstReleaseChecksumsAsync" /> is what consumes
    ///         it, and a download that matches no published digest is refused.
    ///     </para>
    ///     <para>
    ///         This text is NOT signature-checked: it is informational. The download
    ///         path verifies the signature itself (<c>ResolveReleaseDigestsAsync</c>)
    ///         and refuses an update without one. Every fault here is logged and
    ///         returns null rather than throwing.
    ///     </para>
    /// </summary>
    public Task<string?> TryFetchReleaseChecksumsAsync(
        Version version, CancellationToken cancellationToken = default)
        => TryFetchReleaseAssetTextAsync(
            "v" + GitHubReleaseRules.FormatVersion(version), ReleaseChecksums.AssetName, MaxChecksumManifestBytes, cancellationToken);

    /// <summary>
    ///     Reads one small TEXT asset off the release carrying the given version's
    ///     tag.
    ///     <para>
    ///         By TAG, not <c>/releases/latest</c>: the caller is usually asking
    ///         about a version a LAN peer is offering, which can be newer than the
    ///         newest release this install has heard of.
    ///     </para>
    /// </summary>
    private async Task<string?> TryFetchReleaseAssetTextAsync(
        string tag, string assetName, int maxBytes, CancellationToken cancellationToken)
        => await TryFetchReleaseAssetBytesAsync(tag, assetName, maxBytes, cancellationToken).ConfigureAwait(false)
            is { } bytes
            ? DecodeUtf8(bytes)
            : null;

    /// <summary>One small asset off the release carrying <paramref name="tag" />,
    /// as its exact BYTES — a signature is over the file, not over its text.</summary>
    private async Task<byte[]?> TryFetchReleaseAssetBytesAsync(
        string tag, string assetName, int maxBytes, CancellationToken cancellationToken)
    {
        try
        {
            string json;
            using (var budget = Budget(_apiTimeout, cancellationToken))
            using (var request = CreateRequest(
                       $"https://api.github.com/repos/{_owner}/{_repo}/releases/tags/{tag}",
                       "application/vnd.github+json"))
            using (var response = await _http.SendAsync(request, budget.Token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Info(AppLogCategory.Application,
                        $"{assetName} for {tag}: GitHub returned {(int)response.StatusCode}.");
                    return null;
                }

                json = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            }

            var release = GitHubReleaseRules.ParseLatestRelease(json);
            var asset = release?.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null)
            {
                _logger?.Info(AppLogCategory.Application, $"Release {tag} publishes no {assetName}.");
                return null;
            }

            // Both of these are read WHOLE into a string, off a host we do not
            // control, so a declared length past the ceiling is refused before the
            // body is ever requested.
            if (asset.Size > maxBytes)
            {
                _logger?.Warning(AppLogCategory.Application,
                    $"Refusing {assetName} for {tag}: it declares {asset.Size:N0} bytes, " +
                    $"past the {maxBytes:N0}-byte ceiling.");
                return null;
            }

            var url = string.IsNullOrWhiteSpace(asset.ApiUrl) ? asset.BrowserUrl : asset.ApiUrl;
            return await GetBytesAsync(url, maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException
                                       or JsonException or InvalidOperationException or UriFormatException)
        {
            // Offline, a stalled host, a mangled release document. Every one of
            // them means "unverified", never "refuse the update".
            _logger?.Info(AppLogCategory.Application,
                $"{assetName} for {tag} unreachable ({ex.GetType().Name}); a package for that " +
                "version will require fingerprint confirmation.");
            return null;
        }
    }

    public UpdateApplyBlockReason GetApplyBlockReason() => _installer.GetApplyBlockReason();

    /// <summary>
    ///     Applies a zip THIS service downloaded and verified, after re-hashing it
    ///     against the digest it had then. Anything else — a path from
    ///     elsewhere, or a zip changed since — is refused before a byte is staged.
    /// </summary>
    public void ApplyDownloadedUpdate(string zipPath)
    {
        if (!_verifiedDownloads.TryGetValue(Path.GetFullPath(zipPath), out var expected))
        {
            throw new InvalidDataException(
                $"Refusing to apply '{Path.GetFileName(zipPath)}': it is not an update this session " +
                "downloaded and verified against the signed release digests.");
        }

        _installer.ApplyDownloadedUpdate(zipPath, expected);
    }

    public PendingUpdateApply? GetPendingUpdateApply() => _installer.GetPendingUpdateApply();

    public void ResumePendingUpdateApply() => _installer.ResumePendingUpdateApply();

    public void DiscardPendingUpdateApply() => _installer.DiscardPendingUpdateApply();

    public void Dispose() => _http.Dispose();

    /// <summary>Where a partial payload is assembled before it is zipped.</summary>
    private const string PartialStagingDirName = "partial-payload";

    // The choke point: an https GitHub release host or nothing, and the token goes there
    // or nowhere. The rule lives in GitHubApiRequest, shared with the model download.
    private HttpRequestMessage CreateRequest(string url, string accept) =>
        GitHubApiRequest.Create(url, accept,
            $"DotCharter/{CurrentVersionText} (+https://github.com/{_owner}/{_repo})", _tokenProvider?.Invoke());
}
