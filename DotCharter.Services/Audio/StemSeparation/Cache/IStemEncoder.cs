namespace DotCharter.Services.Audio.StemSeparation.Cache;

/// <summary>
/// Encodes separated stem WAVs into the cache's storage codec and decodes them
/// back to WAV for JUCE playback. Abstracted so the cache store can be unit
/// tested without an ffmpeg binary present.
/// </summary>
public interface IStemEncoder
{
    /// <summary>The best codec this encoder can currently produce. When no
    /// external encoder (ffmpeg) is available this degrades to
    /// <see cref="StemCodec.Wav"/>.</summary>
    StemCodec PreferredCodec { get; }

    /// <summary>True when a real external encoder is present (ffmpeg found).</summary>
    bool IsExternalEncoderAvailable { get; }

    /// <summary>
    /// Encodes <paramref name="wavPath"/> into <paramref name="codec"/> and
    /// returns the path to the encoded file (written next to
    /// <paramref name="destPathWithoutExtension"/> with the codec extension).
    /// Falls back to WAV (a copy) when the requested codec cannot be produced;
    /// the caller inspects the returned path's extension / uses
    /// <see cref="PreferredCodec"/> to record the codec actually used.
    /// </summary>
    Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec codec, CancellationToken ct = default);

    /// <summary>
    /// Decodes an encoded stem file (opus/flac/wav) into
    /// <paramref name="destWavPath"/> for playback and returns that path.
    /// Environmental failures (decoder missing, decode process failed) throw
    /// <see cref="StemDecodeUnavailableException"/> — the cache layer keeps the
    /// archive for those, deleting only on genuine corruption.
    /// </summary>
    Task<string> DecodeToWavAsync(string encodedPath, StemCodec codec, string destWavPath, CancellationToken ct = default);
}
