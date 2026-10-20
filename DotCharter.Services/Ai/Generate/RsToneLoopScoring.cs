using System.Globalization;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Services.Ai.Generate;

/// <summary>
///     What still differs between a render and the song, in the named quantities of <see cref="ToneWindowFeatures" />:
///     the words the revision round sends back to the model ("too bright", "too compressed", "missing modulation").
///     Pure; each line names both numbers, so a reader can disagree with it.
/// </summary>
public static class RsToneLoopFeedback
{
    /// <summary>A tilt difference past this (dB/octave) is "too bright" or "too dark".</summary>
    public const double TiltStep = 1.5;

    /// <summary>A crest difference past this (dB) is "too compressed" or "not driven enough".</summary>
    public const double CrestStep = 3.0;

    /// <summary>Below this depth there is no modulation to speak of (the prompt's own "none detected").</summary>
    public const double ModulationFloor = 0.08;

    /// <summary>The differences, biggest axes first in a fixed order: brightness, dynamics, modulation, space.</summary>
    public static IReadOnlyList<string> Differences(ToneWindowFeatures song, ToneWindowFeatures render)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(render);
        var c = CultureInfo.InvariantCulture;
        var lines = new List<string>(4);
        var tilt = render.TiltDbPerOctave - song.TiltDbPerOctave;
        if (Math.Abs(tilt) > TiltStep)
        {
            var (word, fix) = tilt > 0 ? ("bright", "less treble or presence, a darker cabinet or mic") : ("dark", "more treble or presence, a brighter cabinet or mic");
            lines.Add(string.Create(c, $"too {word}: spectral tilt {render.TiltDbPerOctave:0.0} dB/octave against the song's {song.TiltDbPerOctave:0.0} ({fix})"));
        }

        var crest = render.CrestDb - song.CrestDb;
        if (Math.Abs(crest) > CrestStep)
        {
            var (word, fix) = crest < 0 ? ("too compressed or distorted", "less gain or compression") : ("not compressed or driven enough", "more gain or compression");
            lines.Add(string.Create(c, $"{word}: crest {render.CrestDb:0.0} dB against the song's {song.CrestDb:0.0} ({fix})"));
        }

        var songMoves = song.ModulationDepth >= ModulationFloor && !double.IsNaN(song.ModulationHz);
        var renderMoves = render.ModulationDepth >= ModulationFloor && !double.IsNaN(render.ModulationHz);
        if (songMoves && render.ModulationDepth < song.ModulationDepth / 2)
        {
            lines.Add(string.Create(c,
                $"missing modulation: the song's level moves {song.ModulationDepth:0.00} at {song.ModulationHz:0.0} Hz, the render's {render.ModulationDepth:0.00} (a chorus, phaser, flanger or tremolo)"));
        }
        else if (renderMoves && render.ModulationDepth > Math.Max(song.ModulationDepth * 2, ModulationFloor * 2))
        {
            lines.Add(string.Create(c,
                $"too much modulation: the render's level moves {render.ModulationDepth:0.00} at {render.ModulationHz:0.0} Hz, the song's {song.ModulationDepth:0.00}"));
        }

        if (!double.IsNaN(song.DecaySeconds))
        {
            var renderDecay = double.IsNaN(render.DecaySeconds) ? 0 : render.DecaySeconds;
            if (renderDecay < song.DecaySeconds / 2)
            {
                lines.Add(string.Create(c, $"too dry: the song's notes decay over {song.DecaySeconds:0.00} s, the render's over {renderDecay:0.00} s (more reverb or delay)"));
            }
            else if (renderDecay > song.DecaySeconds * 2)
            {
                lines.Add(string.Create(c, $"too wet: the render's notes decay over {renderDecay:0.00} s, the song's over {song.DecaySeconds:0.00} s (less reverb or delay)"));
            }
        }

        return lines;
    }
}

/// <summary>
///     The shipping <see cref="IToneLoopScorer" />: <see cref="MatchScale" /> over the match lane's metric (the target
///     trimmed to its playing first, as the generator's W5b confidence does), plus <see cref="RsToneLoopFeedback" /> over
///     the named features. The target's and the anchor's features are kept, since every render of a run is scored
///     against the same two.
/// </summary>
public sealed class MatchScaleToneLoopScorer(IToneSimilarity similarity) : IToneLoopScorer
{
    private readonly ToneWindowFeatureExtractor _named = new();
    private readonly object _sync = new();
    private (MonoAudio Audio, MonoAudio Measured, ToneFeatures Features, ToneWindowFeatures? Named, bool TooShort)? _target;
    private (MonoAudio Audio, ToneFeatures Features)? _reference;

    public ToneLoopScore Score(MonoAudio target, MonoAudio candidate, MonoAudio reference)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var extractor = similarity.Extractor;
        var song = Target(target);
        var anchor = Reference(reference);
        var score = MatchScale.Score(song.Features, extractor.Extract(candidate), anchor, similarity, double.NaN, song.TooShort);
        var differences = song.Named is { } named && Named(candidate) is { } rendered ? RsToneLoopFeedback.Differences(named, rendered) : [];
        return new ToneLoopScore(score.Percent, score.UnratedReason, score.Distance, differences);
    }

    private (MonoAudio Audio, MonoAudio Measured, ToneFeatures Features, ToneWindowFeatures? Named, bool TooShort) Target(MonoAudio target)
    {
        lock (_sync)
        {
            if (_target is { } kept && ReferenceEquals(kept.Audio, target)) return kept;
        }

        var trimmed = MatchTargetAudio.Trim(target);
        var measured = trimmed.Content > TimeSpan.Zero ? trimmed.Audio : target;
        var entry = (target, measured, similarity.Extractor.Extract(measured), Named(measured), !MatchTargetAudio.IsMeasurable(trimmed));
        lock (_sync) _target = entry;
        return entry;
    }

    private ToneFeatures Reference(MonoAudio reference)
    {
        lock (_sync)
        {
            if (_reference is { } kept && ReferenceEquals(kept.Audio, reference)) return kept.Features;
        }

        var features = similarity.Extractor.Extract(reference);
        lock (_sync) _reference = (reference, features);
        return features;
    }

    /// <summary>The named features, or null for audio too short to measure (the score still stands without words).</summary>
    private ToneWindowFeatures? Named(MonoAudio audio)
    {
        try
        {
            return _named.Extract(audio);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
