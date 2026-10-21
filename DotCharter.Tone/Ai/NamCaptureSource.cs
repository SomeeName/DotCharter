using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;

namespace DotCharter.Tone.Ai;

/// <summary>
///     Where the tone designer gets a neural capture that is NOT in the player's
///     library yet: a design names a search (<see cref="ToneDraft.CaptureSearch" />)
///     and this answers it with a capture on disk.
///
///     <para>
///         <b>The APP downloads, never the model.</b> A CLI provider runs in a
///         read-only sandbox and an API provider has no tools at all; either way
///         the model only ever writes a search string. The search, the pick and
///         the download go through the same TONE3000 client, the same library and
///         the same size cap as the capture browser's Download button, so an AI
///         capture is stored with its licence and attribution exactly like a
///         hand-picked one.
///     </para>
/// </summary>
public interface INamCaptureSource
{
    /// <summary>True when a search can run right now — for TONE3000, while the
    /// player is signed in. The prompt only offers <c>captureSearch</c> while this
    /// holds.</summary>
    bool IsAvailable { get; }

    /// <summary>Finds and downloads (or reuses) the best capture for
    /// <paramref name="query" />. Throw-free except for cancellation: every other
    /// failure is a <see cref="NamCaptureSourcing.Problem" />.</summary>
    Task<NamCaptureSourcing> FindAsync(string query, CancellationToken ct);
}

/// <summary>What a capture search produced.</summary>
/// <param name="Entry">The capture on disk, or null when nothing fit.</param>
/// <param name="Downloaded">True when this call downloaded it; false when the
/// library already had it.</param>
/// <param name="Problem">Why there is no capture, in words for the player's
/// warning list; null on success.</param>
public sealed record NamCaptureSourcing(NamCaptureEntry? Entry, bool Downloaded, string? Problem)
{
    /// <summary>A failed search.</summary>
    public static NamCaptureSourcing Failed(string problem) => new(null, false, problem);
}

/// <summary>
///     <see cref="INamCaptureSource" /> over TONE3000: search the AMP captures,
///     pick one deterministically, and download it into the capture library.
/// </summary>
public sealed class Tone3000CaptureSource : INamCaptureSource
{
    /// <summary>How many of the server's best matches are weighed against each
    /// other. The search is relevance-ordered; past the first handful a result is
    /// popular for a query it does not match.</summary>
    internal const int Considered = 5;

    /// <summary>The longest search sent. A model that writes a paragraph into the
    /// field is not asking a better question.</summary>
    internal const int MaxQueryLength = 80;

    private readonly ITone3000Service _service;
    private readonly ITone3000AuthService _auth;
    private readonly INamCaptureLibrary _library;
    private readonly IAppLogger? _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="service">The TONE3000 client.</param>
    /// <param name="auth">The sign-in state: a search runs only while signed in,
    /// and a 401 invalidates the session the way the browser's does.</param>
    /// <param name="library">Where the capture lands, and where an earlier download
    /// is found again.</param>
    /// <param name="logger">Optional app logger.</param>
    public Tone3000CaptureSource(
        ITone3000Service service, ITone3000AuthService auth, INamCaptureLibrary library, IAppLogger? logger = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => _auth.IsSignedIn;

    /// <inheritdoc />
    public async Task<NamCaptureSourcing> FindAsync(string query, CancellationToken ct)
    {
        var text = NormalizeQuery(query);
        if (text.Length == 0) return NamCaptureSourcing.Failed("The capture search was empty.");

        // The query is a model's text and goes to a third party: an amp name is not an address, and
        // one that is can only be carrying something out.
        if (LooksLikeAddress(query))
            return NamCaptureSourcing.Failed("The capture search looked like a web address, so it was not run.");
        if (!IsAvailable)
        {
            return NamCaptureSourcing.Failed(
                $"The design asked TONE3000 for \"{text}\", but TONE3000 is not signed in.");
        }

        var search = await _service.SearchAsync(
            new Tone3000SearchQuery(text, Tone3000GearType.Amp, PerPage: 10), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (Unauthorized(search.Error)) return SignedOut(text);
        if (!search.Ok || search.Value is null)
        {
            return NamCaptureSourcing.Failed(
                $"The TONE3000 search for \"{text}\" failed{Detail(search.Message)}.");
        }

        var tone = PickTone(search.Value.Tones);
        if (tone is null)
            return NamCaptureSourcing.Failed($"TONE3000 has no NAM amp capture for \"{text}\".");

        // Downloaded before — by an earlier design or from the browser: reuse it.
        // Downloading it again would only fail on the file name the first one took.
        var existing = _library.GetAll().FirstOrDefault(e => e.Manifest.ToneId == tone.Id && IsNeural(e));
        if (existing is not null)
        {
            _logger?.Info(AppLogCategory.Application,
                $"AI capture search \"{text}\" → tone {tone.Id} \"{tone.Title}\" (already in the library).");
            return new NamCaptureSourcing(existing, false, null);
        }

        var models = await _service.GetModelsAsync(tone.Id, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (Unauthorized(models.Error)) return SignedOut(text);
        if (!models.Ok || models.Value is not { Count: > 0 })
        {
            return NamCaptureSourcing.Failed(
                $"TONE3000 listed \"{tone.Title}\" but gave no file for it{Detail(models.Message)}.");
        }

        var model = Tone3000ModelPicker.Pick(models.Value);
        if (!Tone3000ModelPicker.IsNeuralCapture(model))
            return NamCaptureSourcing.Failed($"\"{tone.Title}\" on TONE3000 is not a NAM capture.");

        var download = await _library.DownloadAsync(tone, model, null, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (Unauthorized(download.Error)) return SignedOut(text);
        if (!download.Ok || download.Value is null)
        {
            return NamCaptureSourcing.Failed(
                $"Downloading \"{tone.Title}\" from TONE3000 failed{Detail(download.Message)}.");
        }

        _logger?.Info(AppLogCategory.Application,
            $"AI capture search \"{text}\" → tone {tone.Id} \"{tone.Title}\" by {tone.Author}, " +
            $"model {model.Id} ({model.SizeTier}), downloaded.");
        return new NamCaptureSourcing(download.Value, true, null);
    }

    /// <summary>
    ///     The capture a search settles on: of the first <see cref="Considered" />
    ///     results that are NAM captures, the most downloaded; the server's order
    ///     breaks a tie. Pure and deterministic — the same page always gives the
    ///     same capture — because a design that fetched a different amp on every
    ///     retry would be impossible to refine.
    /// </summary>
    internal static Tone3000Tone? PickTone(IReadOnlyList<Tone3000Tone>? tones)
    {
        if (tones is null) return null;
        Tone3000Tone? best = null;
        foreach (var tone in tones.Where(IsNamTone).Take(Considered))
            if (best is null || tone.Downloads > best.Downloads) best = tone;
        return best;
    }

    /// <summary>A tone whose formats include NAM, or that lists none (unknown on the
    /// wire, the same rule as a model's empty format).</summary>
    private static bool IsNamTone(Tone3000Tone tone)
        => tone.Formats.Count == 0 || tone.Formats.Any(f => string.Equals(f, "nam", StringComparison.OrdinalIgnoreCase));

    private static bool IsNeural(NamCaptureEntry entry)
    {
        var extension = Path.GetExtension(entry.Manifest.FileName);
        return string.Equals(extension, ".nam", StringComparison.OrdinalIgnoreCase)
               || string.Equals(extension, ".aidax", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the query holds a URL scheme, a <c>www.</c> host or an e-mail address.</summary>
    internal static bool LooksLikeAddress(string? query)
        => query is not null &&
           (query.Contains("://", StringComparison.Ordinal) ||
            query.Contains("www.", StringComparison.OrdinalIgnoreCase) ||
            query.Contains('@', StringComparison.Ordinal));

    /// <summary>One line, trimmed and capped, quotes stripped — the field is
    /// free text from a model.</summary>
    internal static string NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        var text = string.Join(' ', query.Replace('"', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= MaxQueryLength ? text : text[..MaxQueryLength].TrimEnd();
    }

    private static bool Unauthorized(OnlineError error) => error == OnlineError.NotSignedIn;

    private NamCaptureSourcing SignedOut(string text)
    {
        _auth.InvalidateSession("an AI capture search came back unauthorized");
        return NamCaptureSourcing.Failed(
            $"The design asked TONE3000 for \"{text}\", but the TONE3000 session has expired. Sign in again.");
    }

    private static string Detail(string? message)
        => string.IsNullOrWhiteSpace(message) ? "" : $": {message.Trim()}";
}
