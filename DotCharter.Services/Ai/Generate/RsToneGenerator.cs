using DotCharter.Localization;
using System.Diagnostics;
using System.Globalization;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     One Rocksmith tone per placed tone change: plan the window, isolate the
///     arrangement's own stem for it, measure it, map it to gear, validate it against
///     the game's catalogue, and score it.
///     <para>
///         <b>Sequential, like the section matcher and for the same reason</b>: the
///         confidence render goes through one device-less engine whose own lock
///         serialises concurrent renders, so running windows in parallel buys nothing
///         and costs the ability to say which one is running.
///     </para>
///     <para>
///         <b>A set never fails as a whole.</b> A window whose stem is missing, whose
///         audio is too short, or whose reply names gear the game does not have comes
///         back as a <see cref="GeneratedTone" /> with a null tone and a REASON, and
///         the run carries on. Cancellation is the one thing that propagates.
///     </para>
///     <para>
///         <b>A missing stem is an explicit failure, never the full mix.</b> A bass
///         tone measured off the whole record is a tone built from the drums, and it
///         would look exactly as confident as a real one.
///     </para>
/// </summary>
public sealed class RsToneGenerator : IRsToneGenerator
{
    private readonly IStemIsolator _isolator;
    private readonly IRsToneMapper _mapper;
    private readonly ILocalizationService _localization;
    private readonly IToneEvidenceSource _evidence;
    private readonly IRigRenderer? _renderer;
    private readonly IToneSimilarity? _similarity;
    private readonly RsGearCatalog _catalog;

    /// <summary>Builds the generator.</summary>
    /// <param name="isolator">Cache-first stem isolation, per arrangement's own stem.</param>
    /// <param name="mapper">The model boundary.</param>
    /// <param name="localization">Resolves the status and progress text the user reads.</param>
    /// <param name="evidence">The DSP measurement. Null for the shipping
    /// <see cref="MeasuredToneEvidenceSource" />.</param>
    /// <param name="renderer">Renders the generated tone for the confidence score.
    /// Null ⇒ every tone is unrated, which is honest and costs nothing.</param>
    /// <param name="similarity">The metric behind that score.</param>
    /// <param name="catalog">The gear catalogue; null for the shared one.</param>
    public RsToneGenerator(
        IStemIsolator isolator,
        IRsToneMapper mapper,
        ILocalizationService localization,
        IToneEvidenceSource? evidence = null,
        IRigRenderer? renderer = null,
        IToneSimilarity? similarity = null,
        RsGearCatalog? catalog = null)
    {
        _isolator = isolator ?? throw new ArgumentNullException(nameof(isolator));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _localization = localization;
        _evidence = evidence ?? new MeasuredToneEvidenceSource();
        _renderer = renderer;
        _similarity = similarity;
        _catalog = catalog ?? RsGearCatalog.Shared;
    }

    /// <summary>Why a run reports no confidence for a tone.</summary>
    public const string UnratedNoRenderer = "no-renderer";

    /// <summary>Why a tone mapped from the context alone (no cached stem) has no confidence:
    /// there is no audio to score it against.</summary>
    public const string UnratedNoStem = "no-stem";

    /// <summary>The provenance's stem when none was measured (a cache-only isolator found none).</summary>
    public const string NoStem = "none";

    /// <inheritdoc />
    public async Task<ToneSetGenerationResult> GenerateAsync(
        ToneGenerateRequest request, IProgress<string>? status, CancellationToken ct,
        IProgress<double>? fraction = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();
        var notes = new List<string> { "mapper: " + _mapper.Describe() };
        var results = new List<GeneratedTone>(request.Windows.Count);
        var keys = new List<string>(request.ExistingKeys);

        for (var i = 0; i < request.Windows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            fraction?.Report((double)i / request.Windows.Count);
            var window = request.Windows[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"Tone {i + 1} of {request.Windows.Count}");
            var step = _localization.Format("Progress_ToneOf", i + 1, request.Windows.Count);

            try
            {
                status?.Report(_localization.Format("Progress_StepIsolating", step,
                    ToneStemNames.Display(request.Instrument, _localization)));
                MonoAudio? audio;
                try
                {
                    audio = await _isolator
                        .IsolateAsync(request.SongAudioPath, request.Instrument,
                            TimeSpan.FromMilliseconds(window.FromMs), TimeSpan.FromMilliseconds(window.ToMs),
                            status, ct)
                        .ConfigureAwait(false);
                }
                catch (StemsNotCachedException ex)
                {
                    // A CACHE-ONLY isolator (the AI chat's Tones task) found no separated
                    // stems and was told never to separate. That is "no measurement", not the
                    // full mix: the window is mapped from the charter's words alone, and says so.
                    audio = null;
                    notes.Add($"{prefix}: {ex.Message} — mapped from the context alone.");
                    status?.Report(_localization.Format("Progress_StepNoStem", step));
                }

                ToneWindowEvidence? evidence = null;
                if (audio is not null)
                {
                    status?.Report(_localization.Format("Progress_StepMeasuring", step));
                    evidence = await _evidence.DescribeAsync(audio, ct).ConfigureAwait(false);
                }

                // Neither source ⇒ nothing to map FROM. Refusing is the honest answer:
                // a tone invented from a tone key is not a tone generated from a song.
                if (evidence?.Features is null && string.IsNullOrWhiteSpace(request.Context) &&
                    string.IsNullOrWhiteSpace(request.Instruction))
                {
                    throw new InvalidOperationException(
                        "there is nothing to generate from — the window could not be measured and no " +
                        "context was supplied");
                }

                var key = RsToneAuthoring.MakeUniqueKey(KeyFor(request, window, i), keys);
                keys.Add(key);

                status?.Report(_localization.Format("Progress_StepAsking", step, _mapper.Describe()));
                var mapping = await _mapper.MapAsync(
                    new RsToneMapRequest(
                        evidence?.Features, request.Context, key, NameFor(window, i),
                        request.Instrument, request.SongTitle, request.Artist, request.Instruction,
                        request.ArrangementName),
                    ct).ConfigureAwait(false);

                var (percent, unrated) = audio is null
                    ? (null, UnratedNoStem)
                    : await ScoreAsync(mapping.Tone, audio, request.ReferenceDi, ct).ConfigureAwait(false);

                results.Add(new GeneratedTone(window, mapping.Tone, new ToneGenerationProvenance(
                    window, audio is null ? NoStem : request.Instrument, mapping.Sources, mapping.ModelId,
                    mapping.Rationale, percent, unrated, evidence?.Features), null));

                notes.Add(
                    $"{prefix} ({key}): {window.Describe()}; stem {request.Instrument}; " +
                    $"sources {string.Join("+", mapping.Sources)}; {Chain(mapping.Tone)}; " +
                    (percent is { } p
                        ? p.ToString(CultureInfo.InvariantCulture) + " % confidence"
                        : $"unrated ({unrated})"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new GeneratedTone(window, null, null, ex.Message));
                notes.Add($"{prefix}: failed — {ex.Message}");
                status?.Report(_localization.Format("Progress_StepFailed", step, ex.Message));
            }
        }

        fraction?.Report(1.0);
        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"{results.Count(r => r.Tone is not null)}/{results.Count} tones generated in " +
            $"{sw.Elapsed.TotalSeconds:0.0} s"));

        return new ToneSetGenerationResult(results, sw.Elapsed, notes);
    }

    /// <summary>
    ///     The W5b confidence for a generated tone: convert it back to the app's own
    ///     rig (<see cref="RsToneConverter" />, the RS→DC direction that already
    ///     exists), render that over the reference DI, and score it against the
    ///     window's own audio through <see cref="MatchScale" />.
    ///     <para>
    ///         <b>It is the same scale the matcher reports and it means the same
    ///         thing</b>: 0 % is an information-free amp and cab, 100 % is identical.
    ///         A generated tone that scores at or under the reference rig has been
    ///         told so rather than shipped with a number that flatters it.
    ///     </para>
    ///     <para>
    ///         <b>The conversion is a real approximation and the score inherits it.</b>
    ///         What is rendered is DotCharter's rig for that Rocksmith tone, not
    ///         Rocksmith's own DSP — so this is "how close is the tone we can PLAY",
    ///         which is the number the player hears anyway.
    ///     </para>
    /// </summary>
    private async Task<(int? Percent, string? Unrated)> ScoreAsync(
        RsTone tone, MonoAudio target, MonoAudio? di, CancellationToken ct)
    {
        if (_renderer is null || _similarity is null || di is null) return (null, UnratedNoRenderer);

        var trimmed = MatchTargetAudio.Trim(target);
        var measured = trimmed.Content > TimeSpan.Zero ? trimmed.Audio : target;
        var extractor = _similarity.Extractor;

        var rig = new RsToneConverter().ConvertFilled(tone, out _);
        var wet = await _renderer.RenderAsync(di, rig, ct).ConfigureAwait(false);
        var reference = await _renderer.RenderAsync(di, MatchScale.ReferenceRig(), ct).ConfigureAwait(false);

        var score = MatchScale.Score(
            extractor.Extract(measured), extractor.Extract(wet), extractor.Extract(reference),
            _similarity, double.NaN, !MatchTargetAudio.IsMeasurable(trimmed));

        return (score.Percent, score.UnratedReason);
    }

    private static string KeyFor(ToneGenerateRequest request, ToneWindow window, int index)
        => RsToneAuthoring.SanitizeKey(
            string.IsNullOrWhiteSpace(window.ToneName)
                ? $"{request.ArrangementName}_{index + 1}"
                : window.ToneName);

    private static string NameFor(ToneWindow window, int index)
        => string.IsNullOrWhiteSpace(window.ToneName)
            ? string.Create(CultureInfo.InvariantCulture, $"Tone {index + 1}")
            : window.ToneName;

    private static string Chain(RsTone tone)
    {
        var parts = new List<string>(8);
        foreach (var pedal in tone.PrePedals) parts.Add(pedal.GearKey);
        parts.Add(tone.Amp.GearKey);
        parts.Add(tone.Cabinet.GearKey);
        foreach (var rack in tone.Racks) parts.Add(rack.GearKey);
        foreach (var pedal in tone.PostPedals) parts.Add(pedal.GearKey);
        return string.Join(" → ", parts);
    }
}

/// <summary>
///     The shipping evidence source: <see cref="ToneWindowFeatureExtractor" />, one
///     window at a time. Answers null for a window too short to measure, so the
///     generator can fall back to context alone rather than failing outright.
/// </summary>
public sealed class MeasuredToneEvidenceSource : IToneEvidenceSource
{
    private readonly ToneWindowFeatureExtractor _extractor = new();

    /// <inheritdoc />
    public string Id => ToneGenerationSources.Audio;

    /// <inheritdoc />
    public Task<ToneWindowEvidence?> DescribeAsync(MonoAudio window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);
        ct.ThrowIfCancellationRequested();
        try
        {
            var features = _extractor.Extract(window);
            return Task.FromResult<ToneWindowEvidence?>(
                new ToneWindowEvidence(Id, features, string.Empty));
        }
        catch (ArgumentException)
        {
            // Too short to measure. Not an error for the SET — the caller decides
            // whether context alone is enough.
            return Task.FromResult<ToneWindowEvidence?>(null);
        }
    }
}
