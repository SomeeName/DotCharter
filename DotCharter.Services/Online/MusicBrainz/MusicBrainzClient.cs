using System.Net.Http.Headers;
using System.Text.Json;
using DotCharter.Tone.Online;

namespace DotCharter.Services.Online.MusicBrainz;

/// <summary>What a MusicBrainz recording search found for one song.</summary>
public sealed record MusicBrainzMatch(
    string RecordingId,
    string Title,
    string Artist,
    string? ReleaseId,
    string? ReleaseTitle,
    int Score);

/// <summary>
///     Anonymous MusicBrainz lookups for New project's Latin-name suggestion
///     (<see cref="FindLatinNamesAsync" />): the official Latin-script artist, title
///     and album of a song whose tags are in another script.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two things MusicBrainz requires and will block you for skipping:</b> a
///         DESCRIPTIVE User-Agent carrying contact information, and no more than one
///         request per second. Both are enforced here rather than hoped for — the
///         gate uses an injected clock so the rule is testable without sleeping.
///     </para>
///     <para>
///         Nothing here throws to its caller. Every failure is an
///         <see cref="OnlineResult{T}" /> so an import that cannot reach the network
///         still finishes.
///     </para>
/// </remarks>
public sealed class MusicBrainzClient : IDisposable
{
    private const string SearchBase = "https://musicbrainz.org/ws/2";

    /// <summary>MusicBrainz's published anonymous rate limit.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private readonly HttpClient _http;
    private readonly IAppLogger? _logger;
    private readonly Func<long> _nowMs;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _gate = new();
    private long _nextAllowedMs;

    /// <param name="userAgent">
    ///     Must be descriptive and carry contact info. The default names the app, its
    ///     version and its repository, which is what MusicBrainz asks for.
    /// </param>
    /// <param name="logger">Optional.</param>
    /// <param name="handler">Injected by tests; null uses a real socket handler.</param>
    /// <param name="nowMs">Injected clock for the rate gate.</param>
    /// <param name="delay">Injected sleep for the rate gate, so tests never wait.</param>
    public MusicBrainzClient(
        string? userAgent = null,
        IAppLogger? logger = null,
        HttpMessageHandler? handler = null,
        Func<long>? nowMs = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _logger = logger;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _delay = delay ?? ((span, token) => Task.Delay(span, token));

        _http = OnlineHttp.CreateClient(TimeSpan.FromSeconds(15), handler);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            userAgent ?? DefaultUserAgent());
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    ///     <c>Application name/version ( contact-url )</c>, the shape MusicBrainz documents.
    ///     The contact is the repository URL, never an email.
    /// </summary>
    /// <param name="version">Null reads the running application's version.</param>
    public static string DefaultUserAgent(Version? version = null)
    {
        var text = version == null ? DotCharter.Services.Infrastructure.AppVersionInfo.Current : Update.UpdateVersionText.Format(version);
        return $"DotCharter/{text} ( https://github.com/SomeeName/DotCharter )";
    }

    /// <summary>Best recording match for an artist + title, or a typed failure.</summary>
    private async Task<OnlineResult<MusicBrainzMatch>> FindRecordingAsync(
        string artist, string title, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return OnlineResult<MusicBrainzMatch>.Fail(
                OnlineError.InvalidRequest, "An artist and a title are both needed to search MusicBrainz.");
        }

        var query = $"artist:\"{Escape(artist)}\" AND recording:\"{Escape(title)}\"";
        var url = $"{SearchBase}/recording?query={Uri.EscapeDataString(query)}&fmt=json&limit=5";

        var body = await GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!body.Ok) return OnlineResult<MusicBrainzMatch>.Fail(body.Error, body.Message);

        try
        {
            var match = MusicBrainzJson.ParseBestRecording(body.Value!, artist, title);
            return match is null
                ? OnlineResult<MusicBrainzMatch>.Fail(OnlineError.None, "No MusicBrainz recording matched.")
                : OnlineResult<MusicBrainzMatch>.Success(match);
        }
        catch (JsonException ex)
        {
            return OnlineResult<MusicBrainzMatch>.Fail(OnlineError.Server, $"Unreadable MusicBrainz response: {ex.Message}");
        }
    }

    /// <summary>
    ///     The official LATIN-script artist, title and album for a song whose
    ///     tags are in another script — the "official English name" a charter would otherwise
    ///     look up by hand.
    ///     <para>
    ///     <b>The two halves are not equally trustworthy, and the UI must not
    ///     pretend otherwise.</b> The ARTIST is reliable: MusicBrainz's style
    ///     guide requires <c>sort-name</c> to be Latin, and English aliases are
    ///     well curated, so there is nearly always a defensible answer. The TITLE
    ///     is best-effort — an official English song title exists only when the
    ///     release actually carries one, and MusicBrainz stores the original
    ///     script as the primary. That asymmetry is why this returns a
    ///     SUGGESTION for a human to apply rather than overwriting anything.
    ///     </para>
    ///     <para>
    ///     At most two requests, so at most two rate-gate slots (~1 s apart) —
    ///     each endpoint is skipped when nothing it answers is in another script,
    ///     and the ALBUM costs nothing extra, because the release comes back on
    ///     the recording search the title already needs. Never throws: any
    ///     failure is an empty suggestion, because a metadata convenience must
    ///     not be able to fail an import.
    ///     </para>
    /// </summary>
    /// <param name="artist">The song's artist tag, in whatever script it is in.</param>
    /// <param name="title">The song's title tag. May be empty.</param>
    /// <param name="album">The album tag. May be empty.</param>
    /// <param name="cancellationToken">Cancellation for both requests.</param>
    /// <param name="onPartial">Invoked as soon as the ARTIST is known, before the
    /// recording request that resolves title and album. The two searches are
    /// sequential by necessity (MusicBrainz asks for one request per second) and
    /// its search server is genuinely slow — a real lookup measured 20.5 s end to
    /// end, by which time the charter has read the form and moved on. The artist
    /// is both the reliable half and the FIRST one available, so it is published
    /// on its own rather than held hostage to the second request.</param>
    public async Task<OnlineResult<LatinNameSuggestion>> FindLatinNamesAsync(
        string artist, string title, string album, CancellationToken cancellationToken,
        Action<LatinNameSuggestion>? onPartial = null)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return OnlineResult<LatinNameSuggestion>.Fail(
                OnlineError.InvalidRequest, "An artist is needed to search MusicBrainz.");
        }

        // Only ask for what is actually in another script. Each request costs a
        // rate-gate slot (~1 s), so an already-Latin artist skips the artist
        // endpoint entirely rather than fetching an answer that could only be
        // discarded.
        var latinArtist = "";
        if (ScriptDetection.NeedsRomanization(artist))
        {
            var artistUrl = $"{SearchBase}/artist?query={Uri.EscapeDataString(ArtistQuery(artist))}" +
                            "&fmt=json&limit=5";
            var artistBody = await GetAsync(artistUrl, cancellationToken).ConfigureAwait(false);
            if (!artistBody.Ok)
                return OnlineResult<LatinNameSuggestion>.Fail(artistBody.Error, artistBody.Message);

            try
            {
                latinArtist = MusicBrainzJson.ParseLatinArtistName(artistBody.Value!, artist) ?? "";
            }
            catch (JsonException ex)
            {
                return OnlineResult<LatinNameSuggestion>.Fail(
                    OnlineError.Server, $"Unreadable MusicBrainz response: {ex.Message}");
            }
        }

        var artistOnly = ScriptDetection.IsUsefulRomanization(artist, latinArtist) ? latinArtist : "";
        if (artistOnly.Length > 0 && onPartial != null)
        {
            try { onPartial(new LatinNameSuggestion(artistOnly, "", "")); }
            catch { /* a progress callback may never break the lookup */ }
        }

        // Title AND album both ride on the ordinary recording search — the release
        // it matched IS the album, so the second name costs no second request.
        // When MusicBrainz's best match is already Latin while our tag is not,
        // THAT is the suggestion; no second guess is made when it is not.
        string latinTitle = "", latinAlbum = "";
        var wantTitle = !string.IsNullOrWhiteSpace(title) && ScriptDetection.NeedsRomanization(title);
        var wantAlbum = !string.IsNullOrWhiteSpace(album) && ScriptDetection.NeedsRomanization(album);

        if (!string.IsNullOrWhiteSpace(title) && (wantTitle || wantAlbum))
        {
            var recording = await FindRecordingAsync(artist, title, cancellationToken).ConfigureAwait(false);
            if (recording.Ok)
            {
                if (wantTitle && ScriptDetection.IsUsefulRomanization(title, recording.Value!.Title))
                    latinTitle = ScriptDetection.Clean(recording.Value.Title);
                if (wantAlbum && ScriptDetection.IsUsefulRomanization(album, recording.Value!.ReleaseTitle))
                    latinAlbum = ScriptDetection.Clean(recording.Value.ReleaseTitle);
            }
        }

        var suggestion = new LatinNameSuggestion(artistOnly, latinTitle, latinAlbum);

        return suggestion.HasAnything
            ? OnlineResult<LatinNameSuggestion>.Success(suggestion)
            : OnlineResult<LatinNameSuggestion>.Fail(OnlineError.None, "MusicBrainz had no Latin name for this song.");
    }

    /// <summary>The Lucene term for an exact artist search, quoted so a
    /// multi-word name is one term.</summary>
    private static string ArtistQuery(string artist) => "artist:\"" + Escape(artist) + "\"";

    private async Task<OnlineResult<string>> GetAsync(string url, CancellationToken cancellationToken)
    {
        await WaitForRateGateAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return OnlineResult<string>.Success(text);

            // A 404 is "nothing found", which is ordinary.
            var error = (int)response.StatusCode switch
            {
                404 => OnlineError.None,
                _ => OnlineError.Server
            };
            return OnlineResult<string>.Fail(error, $"HTTP {(int)response.StatusCode} from {url}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var reason = OnlineHttp.Describe(ex);
            _logger?.Debug(AppLogCategory.External, $"MusicBrainz request failed: {reason}");
            return OnlineResult<string>.Fail(OnlineError.Network, reason);
        }
    }

    /// <summary>One request per second, measured on the injected clock.</summary>
    private async Task WaitForRateGateAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_gate)
        {
            var now = _nowMs();
            wait = _nextAllowedMs > now ? TimeSpan.FromMilliseconds(_nextAllowedMs - now) : TimeSpan.Zero;
            _nextAllowedMs = Math.Max(now, _nextAllowedMs) + (long)MinimumInterval.TotalMilliseconds;
        }

        if (wait > TimeSpan.Zero) await _delay(wait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lucene special characters would otherwise turn a title like
    /// <c>Where Is My Mind?</c> into a malformed query.</summary>
    private static string Escape(string value)
    {
        var escaped = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '"' or '+' or '-' or '!' or '(' or ')' or ':' or '^' or '[' or ']'
                or '{' or '}' or '~' or '*' or '?' or '|' or '&' or '/')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    public void Dispose() => _http.Dispose();
}
