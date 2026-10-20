using DotCharter.Localization;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Match;

/// <summary>
///     Hands the match lane the guitar out of a record: the cached stem for a song,
///     decoded to mono float, cut to the span asked for.
///     <para>
///         <b>Stems are NEVER regenerated.</b> The cache is checked first, every time,
///         through <see cref="StemCacheCoordinator.TryLoadCachedAsync" /> — the only
///         front door — and a separation that does run is saved back before the audio
///         is used. Separating a four-minute song is minutes of GPU or tens of minutes
///         of CPU; doing it twice for the same song is the difference between an eval
///         that takes an evening and one that takes a weekend, and between a match
///         the user waits three seconds for and one they abandon.
///     </para>
///     <para>
///         <b>Guitar, then "other".</b> htdemucs' six-source model emits a
///         <c>guitar</c> source whose own author calls it "okay quality"; when the
///         cached archive has no <c>guitar</c> (a four-source model), <c>other</c> is
///         the fallback and <see cref="ToneAudioSource.GuitarishStems" /> is the one
///         table that says so. <c>drums</c>, <c>bass</c> and <c>vocals</c> are never
///         acceptable and the pick refuses rather than guesses.
///     </para>
///     <para>
///         <b>…and since the sections lane, any of the four <see cref="ToneStems" />.</b>
///         <see cref="IsolateAsync" /> answers for <c>guitar</c>, <c>bass</c>,
///         <c>piano</c> and <c>other</c> out of the SAME cached separation — a
///         four-section song costs one separation, not four — and
///         <see cref="AcceptableSourcesFor" /> is the one table that says which
///         separated sources are an acceptable answer for which stem. Its fallbacks
///         are asymmetric on purpose; read it before adding a stem.
///     </para>
///     <para>
///         The returned audio is the stem's own rate and is NOT level-matched — the
///         extractor and <see cref="Similarity.LevelMatch" /> own that, and a caller
///         writing a WAV for a listening check wants what the separator produced.
///     </para>
/// </summary>
public sealed class StemIsolator : IStemIsolator
{
    /// <summary>The folder under the stem cache root where each song's stems are expanded,
    /// one sub-folder per <see cref="StemCacheKey.DirectoryToken" />.</summary>
    internal const string PlaybackFolderName = "match-eval";

    /// <summary>The sub-folder of a playback directory the separator writes its own
    /// LOSSLESS output into on a cache miss (the cache then keeps an encoded copy).</summary>
    internal const string RawFolderName = "raw";

    private readonly IStemSeparationService _separation;
    private readonly StemCacheStore _store;
    private readonly ILocalizationService _localization;
    private readonly StemCacheCoordinator _coordinator;
    private readonly IAppLogger? _logger;
    private readonly IAudioClipLoader _clips;

    /// <summary>
    ///     Builds an isolator over the app's separation backend and stem cache.
    /// </summary>
    /// <param name="separation">The backend used ONLY on a cache miss.</param>
    /// <param name="store">The durable <c>.dcstems</c> cache. Pass a store with a
    /// <c>rootOverride</c> in a test so a run cannot touch the real cache.</param>
    /// <param name="localization">Resolves the status and progress text the user reads.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="clips">Decoder for the cached stem. Defaults to the shared
    /// <see cref="AudioClipLoader" />; injected so a test can hand over a fake without
    /// a WAV on disk.</param>
    public StemIsolator(IStemSeparationService separation, StemCacheStore store, ILocalizationService localization, IAppLogger? logger = null,
        IAudioClipLoader? clips = null)
    {
        _separation = separation ?? throw new ArgumentNullException(nameof(separation));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _localization = localization;
        _coordinator = new StemCacheCoordinator(store, logger);
        _logger = logger;
        _clips = clips ?? new AudioClipLoader();
    }

    /// <summary>
    ///     False makes this isolator CACHE-ONLY: a song with no cached stems throws
    ///     <see cref="StemsNotCachedException" /> instead of starting a separation. The AI chat's
    /// Tones task reads stems this way — a chat message never starts minutes of GPU.
    /// </summary>
    public bool SeparateOnMiss { get; init; } = true;

    /// <summary>A cache-only twin of this isolator (<see cref="SeparateOnMiss" /> false) over the
    /// SAME store, so the one-separation-per-song rule still has one owner.</summary>
    public StemIsolator CacheOnly() =>
        new(_separation, _store, _localization, _logger, _clips) { SeparateOnMiss = false };

    /// <summary>
    ///     True when a separation COULD run. A false here is not fatal: a song whose
    ///     stems are already cached isolates fine without any backend at all, which is
    ///     exactly the state a machine is in after one eval run.
    /// </summary>
    public Task<bool> CanSeparateAsync(CancellationToken ct = default) => _separation.IsAvailableAsync(ct);

    /// <inheritdoc />
    public async Task<MonoAudio> IsolateGuitarAsync(
        string songAudioPath, TimeSpan from, TimeSpan to, IProgress<string>? status, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songAudioPath);
        if (to <= from) throw new ArgumentException("The span is empty.", nameof(to));
        if (!File.Exists(songAudioPath))
            throw new FileNotFoundException("Song audio not found.", songAudioPath);

        var stemPath = await ResolveGuitarStemAsync(songAudioPath, status, ct).ConfigureAwait(false);
        status?.Report(_localization.Format("Progress_DecodingStem", "guitar"));

        return _clips.Load(stemPath, from, to);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ADDITIVE (sections lane). All four <see cref="ToneStems" />,
    ///     out of the SAME cached separation — the six-stem model already wrote every
    ///     one of them, so a bass section costs no separation a guitar section has not
    ///     already paid for.
    /// </remarks>
    public async Task<MonoAudio> IsolateAsync(
        string songAudioPath, string stem, TimeSpan from, TimeSpan to, IProgress<string>? status,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(songAudioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);
        if (to <= from) throw new ArgumentException("The span is empty.", nameof(to));
        if (!File.Exists(songAudioPath))
            throw new FileNotFoundException("Song audio not found.", songAudioPath);

        var stemPath = await ResolveStemAsync(songAudioPath, stem, status, ct).ConfigureAwait(false);
        status?.Report(_localization.Format("Progress_DecodingStem", stem));

        return _clips.Load(stemPath, from, to);
    }

    /// <summary>
    ///     The cached guitar stem's WAV path, separating first only if the cache
    ///     misses. Public because an eval wants to know whether a run was a hit before
    ///     it commits an evening to it.
    /// </summary>
    public Task<string> ResolveGuitarStemAsync(
        string songAudioPath, IProgress<string>? status, CancellationToken ct) =>
        ResolveStemAsync(songAudioPath, ToneStems.Guitar, status, ct);

    /// <summary>
    ///     The cached WAV path for any <see cref="ToneStems" /> source, separating
    ///     first only if the cache misses. One separation serves every stem: the
    ///     coordinator hands back the whole archive and only the PICK differs.
    /// </summary>
    public async Task<string> ResolveStemAsync(
        string songAudioPath, string stem, IProgress<string>? status, CancellationToken ct)
    {
        // Reject an unknown stem BEFORE a separation, not after: finding out that
        // "guitars" is not a stem name at the end of a twenty-minute separation is
        // the worst possible moment to find out.
        var wanted = AcceptableSourcesFor(stem);

        var identity = StemCacheIdentity.ForAudioFile(songAudioPath);
        var signature = StemAudioSignature.ComputeFile(songAudioPath);
        var key = StemCacheKey.TryDerive(identity, signature)
                  ?? throw new InvalidOperationException(
                      "No cache key could be derived for " + songAudioPath +
                      " — the audio could not be signed, so a separation could not be cached and will " +
                      "not be run.");

        var playbackDirectory = Path.Combine(_store.Root, PlaybackFolderName, key.DirectoryToken);
        Directory.CreateDirectory(playbackDirectory);

        status?.Report(_localization["Progress_CheckingStemCache"]);
        var stems = await _coordinator
            .TryLoadCachedAsync(identity, songAudioPath, playbackDirectory, ct)
            .ConfigureAwait(false);

        if ((stems is null || stems.Count == 0) && !SeparateOnMiss)
            throw new StemsNotCachedException(_localization["Stems_NotCachedNoSeparation"]);

        if (stems is null || stems.Count == 0)
        {
            stems = await SeparateAndCacheAsync(songAudioPath, identity, signature, key, playbackDirectory, status, ct)
                .ConfigureAwait(false);
        }
        else
        {
            status?.Report(_localization["Progress_StemCacheHit"]);
        }

        return Pick(stems, wanted, stem, songAudioPath);
    }

    /// <summary>
    ///     Which separated sources are an acceptable answer for a stem, best first.
    ///     <para>
    ///         <b>The fallbacks are asymmetric on purpose.</b> A four-source model
    ///         emits no <c>guitar</c> and no <c>piano</c>, and both of those live
    ///         inside <c>other</c> — so <c>other</c> is a documented, degraded answer
    ///         for them (this is the rule
    ///         <see cref="ToneAudioSource.GuitarishStems" /> has always stated for the
    ///         guitar). <c>bass</c> is a target of EVERY model, so it has no fallback
    ///         at all: substituting <c>other</c> there would hand a bass section the
    ///         one stem that is defined as everything except the bass.
    ///     </para>
    /// </summary>
    /// <exception cref="NotSupportedException"><paramref name="stem" /> is not one of
    /// <see cref="ToneStems" />.</exception>
    public static IReadOnlyList<string> AcceptableSourcesFor(string stem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stem);
        return stem.Trim().ToLowerInvariant() switch
        {
            ToneStems.Guitar => ToneAudioSource.GuitarishStems,
            ToneStems.Bass => [ToneStems.Bass],
            ToneStems.Piano => [ToneStems.Piano, ToneStems.Other],
            ToneStems.Other => [ToneStems.Other],
            _ => throw new NotSupportedException(
                $"'{stem}' is not a stem this app separates. Known stems: " +
                string.Join(", ", ToneStems.All) + ".")
        };
    }

    private async Task<IReadOnlyList<StemTrack>> SeparateAndCacheAsync(
        string songAudioPath, StemCacheIdentity identity, StemAudioSignature signature, StemCacheKey key,
        string playbackDirectory, IProgress<string>? status, CancellationToken ct)
    {
        if (!await _separation.IsAvailableAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "No stems are cached for " + Path.GetFileName(songAudioPath) +
                " and no separation backend is available (no model is installed). " +
                "Install a stem model or pre-populate the cache.");
        }

        var backend = await _separation.DescribeBackendAsync(ct).ConfigureAwait(false);
        status?.Report(_localization.Format("Progress_SeparatingWith", backend));

        var workDirectory = Path.Combine(playbackDirectory, RawFolderName);
        Directory.CreateDirectory(workDirectory);

        var progress = new Progress<double>(p => status?.Report(_localization.Format("Progress_SeparatingPercent", Math.Round(p * 100))));
        var result = await _separation.SeparateAsync(songAudioPath, workDirectory, progress, ct).ConfigureAwait(false);
        if (!result.Success || result.Stems.Count == 0)
        {
            throw new InvalidOperationException(
                "Stem separation failed for " + Path.GetFileName(songAudioPath) + ": " +
                (result.ErrorMessage ?? "no stems were produced"));
        }

        // Save BEFORE returning: a run that crashes after using the stems but before
        // caching them has bought nothing, and this is the expensive half.
        status?.Report(_localization["Progress_CachingStems"]);
        try
        {
            await _store.SaveAsync(key, identity, signature, result.Stems, backend, AppVersion(), ct,
                    StemAudioSignature.ComputeFullFileHash(songAudioPath))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written is a slow eval, not a failed one.
            _logger?.Warning(AppLogCategory.Audio, "Could not cache stems for " + songAudioPath + ": " + ex.Message);
        }

        return result.Stems;
    }

    private static string Pick(
        IReadOnlyList<StemTrack> stems, IReadOnlyList<string> acceptable, string requested, string songAudioPath)
    {
        foreach (var wanted in acceptable)
        {
            foreach (var stem in stems)
            {
                if (string.Equals(stem.Name, wanted, StringComparison.OrdinalIgnoreCase) && File.Exists(stem.Path))
                    return stem.Path;
            }
        }

        throw new InvalidOperationException(
            "The stems for " + Path.GetFileName(songAudioPath) + " contain no source acceptable for '" +
            requested + "' (wanted one of: " + string.Join(", ", acceptable) + "; have: " +
            string.Join(", ", stems.Select(s => s.Name)) + "). A stem that is not the instrument asked " +
            "for is never an acceptable substitute — it produces a plausible distance and a wrong rig.");
    }

    private static string AppVersion() =>
        DotCharter.Services.Infrastructure.AppVersionInfo.Current;
}

/// <summary>
///     A cache-only <see cref="StemIsolator" /> (<see cref="StemIsolator.SeparateOnMiss" /> false)
///     found no separated stems for the song. The message is the user's: it says how to get them.
/// </summary>
public sealed class StemsNotCachedException(string message) : InvalidOperationException(message);
