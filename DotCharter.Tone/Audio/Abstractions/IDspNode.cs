namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
/// A single node in the DSP graph. Implementations must be allocation-free
/// in <see cref="Process"/> (called by the audio pump thread) and must not
/// touch UI state, dispatchers, or anything that may take a managed lock
/// held by other code paths.
/// </summary>
public interface IDspNode
{
    /// <summary>
    /// Fills <paramref name="output"/> with interleaved stereo float samples.
    /// Length is always a multiple of 2 (L, R, L, R, ...). Implementation
    /// must write exactly <c>output.Length</c> samples.
    /// </summary>
    void Process(Span<float> output);
}
