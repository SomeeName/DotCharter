namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
/// Pull-model PCM source backing a single decoded audio file.
/// Implementations are not thread-safe — only the pump thread reads from
/// them after handoff. Seeks come in via the audio command queue.
/// </summary>
public interface IAudioSource : IDisposable
{
    int SampleRate { get; }
    int Channels { get; }
    long TotalFrames { get; }

    /// <summary>Current read cursor in frames.</summary>
    long PositionFrames { get; }

    /// <summary>
    /// Reads up to <paramref name="frames"/> frames into <paramref name="output"/>
    /// (interleaved, channel-major). Returns the number of frames actually
    /// produced. A return value smaller than <paramref name="frames"/> means EOF.
    /// </summary>
    int Read(Span<float> output, int frames);

    void Seek(long frame);
}
