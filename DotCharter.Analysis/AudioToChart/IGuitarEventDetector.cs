namespace DotCharter.Analysis.AudioToChart;

/// <summary>One fundamental candidate carried by a <see cref="GuitarAudioEvent"/>.
/// <paramref name="Salience"/> is normalised so the strongest candidate of an
/// event is 1.</summary>
public readonly record struct GuitarPitchCandidate(int Midi, float FrequencyHz, float Salience);

/// <summary>
///     One detected guitar event (picked attack or legato transition) in a
///     mono audio stream. <paramref name="Strength"/> is a 0..1 level cue
///     derived from the attack loudness (used as a confidence factor).
/// </summary>
public sealed record GuitarAudioEvent(
    double TimeSeconds,
    bool IsLegato,
    float Strength,
    IReadOnlyList<GuitarPitchCandidate> Pitches);

/// <summary>
///     The onset + pitch core shared between realtime detection (JuceSharp.Dsp,
///     scheduled by the live-play audio thread) and the offline analysis in
///     this project (scheduled by <see cref="AudioToChartAnalyzer"/> over
///     streamed chunks). Implementations must be deterministic: pushing the
///     same total sample sequence — regardless of chunk boundaries — must
///     produce the same events.
/// </summary>
public interface IGuitarEventDetector
{
    int SampleRate { get; }

    /// <summary>Push a block of mono samples; returns the events finalized by
    /// this block, in stream order.</summary>
    IReadOnlyList<GuitarAudioEvent> Push(ReadOnlySpan<float> samples);

    void Reset();
}
