using System.Text;
using JuceSharp;

namespace DotCharter.Services.Export.Audio;

/// <summary>
///     Writes a Rocksmith 2014-compatible Wwise <c>.wem</c> from 16-bit PCM,
///     with no Wwise installation and no external tool.
/// </summary>
/// <remarks>
///     <see cref="WemCodec.Vorbis" /> is written by JuceSharp's Wwise-free encoder
/// (<see cref="JuceWemEncoder" />, ABI v44) exactly the way Wwise 2021 writes
///     it for Rocksmith; it needs 48 kHz stereo. <see cref="WemCodec.Pcm" /> is
///     written here, in a container the same shape as <c>TestData/EOF/*/guitar.wem</c>
///     minus the Vorbis-only <c>AkVorbisInfo</c> tail. <see cref="WemCodec.ImaAdpcm" />
/// is REFUSED: Rocksmith 2014 does not play it, the song never starts.
///     <see cref="WwiseImaAdpcm" /> stays only to DECODE wems built before that.
///     <para>
///         <b>The u32 at absolute offset 40 is the channel configuration</b> and it
///         is load-bearing. Rocksmith's 2013-era Wwise runtime reads it as a legacy
///         <c>WAVEFORMATEXTENSIBLE</c> channel MASK (3 = front-left|front-right).
///         Wwise 2018+ writes a packed <c>AkChannelConfig</c> there instead
///         (0x3102 for stereo), which the old runtime reads as a nonsense speaker
///         layout — which is exactly why Rocksmith2014.NET post-patches a 3 over
///         whatever the modern encoder emitted. We write the 3 directly.
///     </para>
/// </remarks>
public static class WemWriter
{
    private const ushort ChannelConfigStereo = 3; // FRONT_LEFT | FRONT_RIGHT
    private const ushort ChannelConfigMono = 4;   // FRONT_CENTER

    /// <summary>fmt chunk payload size: WAVEFORMATEX (18) + 6 bytes of Wwise extra.</summary>
    private const uint FmtChunkSize = 0x18;

    /// <summary>
    ///     Writes a complete <c>.wem</c> into <paramref name="output" /> and returns
    ///     the number of bytes written. <paramref name="interleaved" /> must be
    ///     interleaved 16-bit PCM with <paramref name="channels" /> channels.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="codec" /> is <see cref="WemCodec.ImaAdpcm" /> (the game does
    ///     not play it), or Vorbis was asked for anything but 48 kHz stereo.
    /// </exception>
    public static long Write(
        Stream output,
        WemCodec codec,
        ReadOnlySpan<short> interleaved,
        int channels,
        int sampleRate)
    {
        if (codec == WemCodec.ImaAdpcm)
        {
            throw new ArgumentException(
                "Rocksmith 2014 does not play IMA-ADPCM .wem audio: the song never starts (#717). Write Vorbis.",
                nameof(codec));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        if (interleaved.Length % channels != 0)
            throw new ArgumentException("PCM length is not a whole number of frames.", nameof(interleaved));

        if (codec == WemCodec.Vorbis)
        {
            var samples = new float[interleaved.Length];
            for (var i = 0; i < samples.Length; i++) samples[i] = interleaved[i] / 32768f;
            var wem = JuceWemEncoder.EncodeVorbis(samples, channels, sampleRate);
            output.Write(wem);
            return wem.Length;
        }

        var payload = new byte[interleaved.Length * sizeof(short)];
        for (var i = 0; i < interleaved.Length; i++)
        {
            payload[i * 2] = (byte)(interleaved[i] & 0xFF);
            payload[(i * 2) + 1] = (byte)((interleaved[i] >> 8) & 0xFF);
        }

        var blockAlign = (ushort)(channels * sizeof(short));
        const ushort bitsPerSample = 16;
        var avgBytesPerSec = (uint)(sampleRate * blockAlign);

        var start = output.Position;
        var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        // RIFF header. Size is everything after the 8-byte RIFF preamble:
        // "WAVE"(4) + fmt header(8) + fmt payload(0x18) + data header(8) + payload.
        var riffSize = (uint)(4 + 8 + FmtChunkSize + 8 + payload.Length);
        writer.Write("RIFF"u8);
        writer.Write(riffSize);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(FmtChunkSize);
        writer.Write(codec.FormatTag());          // 0x14
        writer.Write((ushort)channels);           // 0x16
        writer.Write((uint)sampleRate);           // 0x18
        writer.Write(avgBytesPerSec);             // 0x1C
        writer.Write(blockAlign);                 // 0x20
        writer.Write(bitsPerSample);              // 0x22
        writer.Write((ushort)6);                  // 0x24 cbSize — the 6 bytes below
        writer.Write((ushort)0);                  // 0x26 reserved
        writer.Write(ChannelConfig(channels));    // 0x28 == offset 40. See remarks.

        writer.Write("data"u8);
        writer.Write((uint)payload.Length);
        writer.Write(payload);

        writer.Flush();
        return output.Position - start;
    }

    /// <summary>Convenience overload writing straight to a file path.</summary>
    public static long WriteFile(
        string path,
        WemCodec codec,
        ReadOnlySpan<short> interleaved,
        int channels,
        int sampleRate)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        return Write(file, codec, interleaved, channels, sampleRate);
    }

    private static uint ChannelConfig(int channels) => channels switch
    {
        1 => ChannelConfigMono,
        2 => ChannelConfigStereo,
        // Rocksmith audio is always stereo; anything else is a caller bug, but a
        // zero mask is the honest "unspecified" value rather than a wrong layout.
        _ => 0u
    };
}
