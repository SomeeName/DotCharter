namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     Client surface for the TONE3000 (tone3000.com) community NAM-capture
///     API: free-text search, tone/model metadata and the authenticated model
///     download. Same resilience contract as the rest of <c>Online/</c>: every
///     method catches everything and returns a typed
///     <see cref="OnlineResult{T}"/> — nothing ever throws to the UI.
///
///     The live API rejects anonymous requests: search, tone/model metadata
///     AND the download all carry the signed-in user's Bearer access token, and
///     a 401 (or a download with no session) fails typed
///     <see cref="OnlineError.NotSignedIn"/> so the UI can prompt a sign-in.
/// </summary>
public interface ITone3000Service
{
    /// <summary>Free-text capture search with optional gear-type / format /
    /// size filters and sort order.</summary>
    Task<OnlineResult<Tone3000SearchPage>> SearchAsync(
        Tone3000SearchQuery query, CancellationToken ct = default);

    /// <summary>One tone's full metadata.</summary>
    Task<OnlineResult<Tone3000Tone>> GetToneAsync(long toneId, CancellationToken ct = default);

    /// <summary>The downloadable model files belonging to a tone
    /// (<c>GET /api/v1/models?tone_id=…</c>).</summary>
    Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(
        long toneId, CancellationToken ct = default);

    /// <summary>
    ///     Streams a model file into <paramref name="destination"/> using the
    ///     user's access token. Enforces <paramref name="maxBytes"/> as a hard
    ///     cap (typed <see cref="OnlineError.InvalidRequest"/> when exceeded —
    ///     the caller must discard the partial output). Returns the bytes
    ///     written. <paramref name="progress"/> reports a 0..1 fraction when
    ///     the server declares a length, else stays at 0.
    /// </summary>
    Task<OnlineResult<long>> DownloadModelAsync(
        Tone3000Model model,
        Stream destination,
        long maxBytes,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
