using DotCharter.Analysis.Tone;
using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Services.Audio.Dsp.Tones;

/// <summary>Why a generation attempt ended the way it did.</summary>
public enum ToneGenerationStatus
{
    /// <summary>A tone was built.</summary>
    Generated,

    /// <summary>No stem and no song audio to analyse.</summary>
    NoAudio,

    /// <summary>The change is not in the arrangement, or there is no beat grid.</summary>
    NoSpan,

    /// <summary>The span is too short to say anything about.</summary>
    SpanTooShort,

    /// <summary>The guitar is not playing here.</summary>
    Silent,

    /// <summary>Loud, but broadband noise rather than an instrument.</summary>
    NoiseLike,

    /// <summary>The audio could not be decoded.</summary>
    DecodeFailed
}

/// <summary>The result of generating one tone, including why it did not happen.</summary>
public sealed record ToneGenerationResult(
    ToneGenerationStatus Status,
    ToneSpan Span,
    ToneSuggestion? Suggestion = null,
    string? ToneJson = null,
    ToneAudioProfile Profile = default,
    string StemName = "",
    bool FromIsolatedGuitar = false)
{
    public bool Succeeded => Status == ToneGenerationStatus.Generated && ToneJson is { Length: > 0 };
}

/// <summary>
///     Builds a tone from the song's own audio, for ONE placed tone change.
///     <para>
///         The owner's rule, verbatim: "the tone placement is not generated but
///         the tone that is generated is based on the audio sample between where
///         the tone starts and ends that is placed". So this never invents, moves
///         or removes a tone change. It reads one the charter placed, takes the
///         audio between it and the next, and answers with a tone.
///     </para>
///     <para>
///         It is the composition of five pure pieces and owns no logic of its own
///         beyond ordering them and decoding audio:
///         <see cref="ToneChangeSpans" /> (which audio),
///         <see cref="ToneAudioSource" /> (whose audio),
///         <c>ToneAudioAnalyzer</c> (what it measures),
///         <see cref="ToneEvidence" /> (whether that is worth using) and
///         <see cref="ToneFromAudioBuilder" /> (what gear it implies). Each of
///         those is tested without a file; this is the only part that touches one.
///     </para>
///     <para>
///         <b>What it cannot do.</b> The guitar on a record is already through an
///         amp, a room and a mix bus, and a separated stem carries artefacts of
///         the separation. Nothing here recovers the rig that made the sound. It
///         lands the charter in the right neighbourhood — clean versus crunch
///         versus high gain, with a tone stack leaning the way the recording
///         leans — so they start near instead of from a default. Every result
///         carries the rationale that produced it, because a charter given gear
///         they did not pick needs to see why.
///     </para>
/// </summary>
public sealed class ToneGenerationService
{
    private readonly IAppLogger? _logger;
    private readonly string? _ffmpegPath;
    private readonly Func<string, List<string>, PcmAudio> _load;

    // Decoded once per path for the life of this instance: decoding per placed tone
    // change would repeat the whole song for each one. A failed decode is cached too.
    private readonly Dictionary<string, (float[] Mono, int Rate)?> _decoded = new(StringComparer.OrdinalIgnoreCase);

    public ToneGenerationService(
        IAppLogger? logger = null, string? ffmpegPath = null, Func<string, List<string>, PcmAudio>? load = null)
    {
        _logger = logger;
        _ffmpegPath = ffmpegPath;
        _load = load ?? ((path, warnings) => PcmAudioLoader.Load(path, warnings, _ffmpegPath));
    }

    /// <summary>
    ///     Generates a tone for one placed change.
    ///     <para>
    ///         <paramref name="stemPaths" /> is stem name to absolute path;
    ///         <paramref name="songPath" /> the full mix. Passing stems matters: a
    ///         mix contains drums, and a cymbal is broadband noise that reads as
    ///         distortion (see <see cref="ToneAudioSource" />).
    ///     </para>
    /// </summary>
    public ToneGenerationResult Generate(
        ToneChange? change,
        IReadOnlyList<ToneChange>? changes,
        IList<BeatMarker>? beats,
        double songLengthMs,
        IReadOnlyDictionary<string, string>? stemPaths,
        string? songPath,
        string? key = null,
        string? name = null)
    {
        var span = ToneChangeSpans.SpanFor(change, changes, beats, songLengthMs);
        if (span is null) return new ToneGenerationResult(ToneGenerationStatus.NoSpan, default);
        if (!span.Value.IsUseful)
            return new ToneGenerationResult(ToneGenerationStatus.SpanTooShort, span.Value);

        var source = ToneAudioSource.Pick(stemPaths, songPath);
        if (source is null) return new ToneGenerationResult(ToneGenerationStatus.NoAudio, span.Value);

        if (Decode(source.Value.Path) is not var (mono, rate) || mono.Length == 0 || rate <= 0)
            return new ToneGenerationResult(ToneGenerationStatus.DecodeFailed, span.Value);

        var slice = ToneAudioAnalyzer.Slice(
            mono, rate, span.Value.StartMs / 1000.0, span.Value.EndMs / 1000.0);

        var profile = ToneAudioAnalyzer.Analyze(slice, rate);
        var stem = source.Value.StemName;
        var isolated = source.Value.IsIsolatedGuitar;

        if (profile.IsSilent)
        {
            return new ToneGenerationResult(
                ToneGenerationStatus.Silent, span.Value, Profile: profile,
                StemName: stem, FromIsolatedGuitar: isolated);
        }

        if (ToneEvidence.IsNoiseLike(profile))
        {
            return new ToneGenerationResult(
                ToneGenerationStatus.NoiseLike, span.Value, Profile: profile,
                StemName: stem, FromIsolatedGuitar: isolated);
        }

        var suggestion = ToneFromAudioBuilder.Build(profile, RsGearCatalog.Shared, key, name);
        if (suggestion is null)
        {
            return new ToneGenerationResult(
                ToneGenerationStatus.Silent, span.Value, Profile: profile,
                StemName: stem, FromIsolatedGuitar: isolated);
        }

        var json = RsToneJsonWriter.Write(suggestion.Tone);
        var where = isolated ? $"the {stem} stem" : "the full mix";
        _logger?.Info(AppLogCategory.Audio,
            $"Generated a tone from {where} over {span.Value.DurationMs / 1000.0:F1}s: {suggestion.Rationale}");

        return new ToneGenerationResult(
            ToneGenerationStatus.Generated, span.Value, suggestion, json, profile, stem, isolated);
    }

    private (float[] Mono, int Rate)? Decode(string path)
    {
        if (_decoded.TryGetValue(path, out var cached)) return cached;
        (float[] Mono, int Rate)? decoded;
        try
        {
            var warnings = new List<string>();
            var pcm = _load(path, warnings);
            foreach (var warning in warnings) _logger?.Warning(AppLogCategory.Audio, warning);
            decoded = (ToMono(pcm), pcm.SampleRate);
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio, $"Could not decode the audio at {path} to generate a tone.", ex);
            decoded = null;
        }

        _decoded[path] = decoded;
        return decoded;
    }

    /// <summary>
    ///     Interleaved 16-bit PCM to mono float in [-1,1].
    ///     <para>
    ///         Averaged, not left-channel-only: a guitar panned hard right would
    ///         otherwise measure as near-silence and be reported as "the guitar is
    ///         not playing here" for a span where it plainly is.
    ///     </para>
    /// </summary>
    private static float[] ToMono(PcmAudio pcm)
    {
        if (pcm.Channels <= 0 || pcm.Samples.Length == 0) return [];

        var frames = pcm.Samples.Length / pcm.Channels;
        var mono = new float[frames];

        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            var offset = frame * pcm.Channels;
            for (var channel = 0; channel < pcm.Channels; channel++)
                sum += pcm.Samples[offset + channel] / 32768f;

            mono[frame] = sum / pcm.Channels;
        }

        return mono;
    }
}
