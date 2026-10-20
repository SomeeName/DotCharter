using System.Buffers.Binary;
using DotCharter.Services.Import.Youtube;

namespace DotCharter.Services.Export.Audio;

/// <summary>Interleaved 16-bit PCM, the input <see cref="WemWriter" /> works from.</summary>
public sealed record PcmAudio(short[] Samples, int Channels, int SampleRate)
{
    public int FrameCount => Channels == 0 ? 0 : Samples.Length / Channels;
    public TimeSpan Duration => TimeSpan.FromSeconds(SampleRate == 0 ? 0 : FrameCount / (double)SampleRate);
}

/// <summary>
///     Decodes a song's source audio to the 48 kHz stereo 16-bit PCM Rocksmith
///     expects, so it can be re-encoded as a <c>.wem</c>.
/// </summary>
/// <remarks>
///     <b>The DECODER is the editor's</b>: a project's audio is played in the
///     editor by the native engine (JUCE's basic format set), so it is decoded
///     here by that same engine, and a chart exports against exactly the audio it
///     was charted against. Two decoders are two opinions on where a lossy file
///     starts: for MP3, ffmpeg drops the encoder's leading padding and the
///     engine's Windows Media reader keeps it, so decoding with ffmpeg exported
///     every MP3 project 1,729 samples (36 ms at 48 kHz) ahead of its chart.
///     <c>PcmAudioLoaderEditorParityTests</c> pins it.
///     <para>
///         ffmpeg still RESAMPLES a non-48 kHz decode (from the engine's own
///         float PCM, which is timing-neutral), and still DECODES the formats the
///         engine cannot read (.m4a/.aac/.wma — which the editor cannot play
///         either). Without ffmpeg the resample is linear and recorded as a
///         build warning, because it is audibly worse; without both, WAV is
///         decoded in-process and Ogg by the engine's own Vorbis reader.
///     </para>
/// </remarks>
public static class PcmAudioLoader
{
    public const int RocksmithSampleRate = 48000;
    public const int RocksmithChannels = 2;

    /// <summary>Upper bound on the ffmpeg decode. Bounded on purpose: this runs on
    /// the save/export worker and an unbounded wait there is unrecoverable.</summary>
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Loads <paramref name="path" /> as 48 kHz stereo 16-bit PCM. Appends a
    ///     human-readable note to <paramref name="warnings" /> for every lossy or
    ///     degraded decision taken along the way.
    /// </summary>
    public static PcmAudio Load(string path, IList<string> warnings, string? configuredFfmpegPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"Audio file not found: '{path}'.", path);

        // The editor's decoder first — see the class remarks.
        var viaNative = TryLoadWithNativeDecoder(path, warnings, configuredFfmpegPath);
        if (viaNative is not null) return viaNative;

        // A format the engine cannot read (and so the editor cannot play).
        var viaFfmpeg = TryLoadWithFfmpeg(path, configuredFfmpegPath);
        if (viaFfmpeg is not null) return viaFfmpeg;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        var decoded = extension switch
        {
            ".wav" => ReadWav(path),
            ".ogg" or ".oga" => ReadOgg(path),
            _ => throw new NotSupportedException(
                $"Cannot decode '{extension}' without ffmpeg. Install ffmpeg (Settings -> Components), " +
                "or supply the audio as .wav, .ogg or a pre-encoded .wem.")
        };

        warnings.Add(
            "ffmpeg was not found, so the audio was decoded in-process. Install ffmpeg " +
            "(Settings -> Components) for better resampling.");

        return Conform(decoded, warnings);
    }

    // ---- ffmpeg (preferred) -------------------------------------------------

    /// <summary>Same resolution order the rest of the app uses: configured -> exe
    /// dir -> exe dir/tools (where releases bundle it) -> PATH.</summary>
    private static string? ResolveFfmpeg(string? configuredFfmpegPath) => MediaToolResolver.Resolve(
        "ffmpeg",
        AppContext.BaseDirectory,
        configuredFfmpegPath,
        MediaToolResolver.CurrentPathDirs(),
        File.Exists,
        OperatingSystem.IsWindows());

    private static PcmAudio? TryLoadWithFfmpeg(string path, string? configuredFfmpegPath)
    {
        var ffmpeg = ResolveFfmpeg(configuredFfmpegPath);
        if (ffmpeg is null) return null;

        var temp = Path.Combine(Path.GetTempPath(), $"dotcharter_wem_{Guid.NewGuid():N}.wav");
        try
        {
            // Drained + bounded: an undrained redirected pipe fills at ~4 KB and
            // then deadlocks both processes (this decode runs on the save/export
            // worker, so a hang there is permanent).
            var result = MediaProcess.Run(
                ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-i", path,
                    .. FfmpegAudioOnly.Args,
                    "-ac", RocksmithChannels.ToString(),
                    "-ar", RocksmithSampleRate.ToString(),
                    "-c:a", "pcm_s16le",
                    "-f", "wav",
                    temp
                ],
                DecodeTimeout);

            if (!result.Succeeded || !File.Exists(temp)) return null;

            return ReadWav(temp);
        }
        catch (Exception)
        {
            // A missing/broken ffmpeg must not fail the export — fall through to
            // the in-process decoders and let those report a precise error.
            return null;
        }
        finally
        {
            BestEffortDelete.File(temp);
        }
    }

    // ---- native JUCE decoder (no external tool) -----------------------------

    /// <summary>Decodes via the native engine's format readers (flac, mp3,
    /// aiff, wav, ogg — whatever JUCE registers, i.e. what the editor plays).
    /// A 48 kHz stereo decode is used as-is; anything else is resampled by
    /// ffmpeg from the engine's own float PCM when ffmpeg is available, else
    /// linearly (with a warning). Null when the native DLL is unavailable or the
    /// format is unsupported, so callers fall through to the other decoders.</summary>
    internal static PcmAudio? TryLoadWithNativeDecoder(
        string path, IList<string> warnings, string? configuredFfmpegPath = null)
    {
        JuceSharp.AudioIo.AudioBuffer buf;
        try
        {
            buf = JuceSharp.AudioIo.JuceAudioDecoder.Decode(path);
        }
        catch (Exception)
        {
            // Missing native DLL or unreadable format — the other decoders (or
            // the NotSupportedException) take it from here.
            return null;
        }

        if (buf.Channels <= 0 || buf.SampleRate <= 0 || buf.FrameCount == 0) return null;

        if (buf.SampleRate != RocksmithSampleRate || buf.Channels != RocksmithChannels)
        {
            var resampled = TryConformWithFfmpeg(buf, configuredFfmpegPath);
            if (resampled is not null) return resampled;
        }

        var samples = new short[buf.Interleaved.Length];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = FloatToPcm16(buf.Interleaved[i]);

        return Conform(new PcmAudio(samples, buf.Channels, buf.SampleRate), warnings);
    }

    /// <summary>Resamples / remixes the ENGINE'S decode with ffmpeg, from a 32-bit
    /// float WAV so nothing is quantised twice. ffmpeg's resampler compensates
    /// its own filter delay, so the timing the editor plays survives. Null when
    /// ffmpeg is unavailable or fails.</summary>
    private static PcmAudio? TryConformWithFfmpeg(JuceSharp.AudioIo.AudioBuffer buf, string? configuredFfmpegPath)
    {
        var ffmpeg = ResolveFfmpeg(configuredFfmpegPath);
        if (ffmpeg is null) return null;

        var source = Path.Combine(Path.GetTempPath(), $"dotcharter_wem_src_{Guid.NewGuid():N}.wav");
        var target = Path.Combine(Path.GetTempPath(), $"dotcharter_wem_{Guid.NewGuid():N}.wav");
        try
        {
            WriteFloatWav(source, buf.Interleaved, buf.Channels, buf.SampleRate);
            var result = MediaProcess.Run(
                ffmpeg,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-i", source,
                    "-ac", RocksmithChannels.ToString(),
                    "-ar", RocksmithSampleRate.ToString(),
                    "-c:a", "pcm_s16le",
                    "-f", "wav",
                    target
                ],
                DecodeTimeout);

            return result.Succeeded && File.Exists(target) ? ReadWav(target) : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            BestEffortDelete.File(source);
            BestEffortDelete.File(target);
        }
    }

    /// <summary>Writes interleaved 32-bit float RIFF/WAVE (format tag 3).</summary>
    private static void WriteFloatWav(string path, float[] interleaved, int channels, int sampleRate)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);

        var dataBytes = interleaved.Length * sizeof(float);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)3); // IEEE float
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sizeof(float));
        writer.Write((ushort)(channels * sizeof(float)));
        writer.Write((ushort)32);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(interleaved.AsSpan()));
    }

    // ---- wav writing --------------------------------------------------------

    /// <summary>Writes 16-bit PCM RIFF/WAVE — the input format Wwise accepts.</summary>
    public static void WriteWav(string path, PcmAudio audio)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);

        var dataBytes = audio.Samples.Length * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1); // PCM
        writer.Write((ushort)audio.Channels);
        writer.Write(audio.SampleRate);
        writer.Write(audio.SampleRate * audio.Channels * sizeof(short));
        writer.Write((ushort)(audio.Channels * sizeof(short)));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (var sample in audio.Samples)
            writer.Write(sample);
    }

    // ---- in-process decoders ------------------------------------------------

    /// <summary>
    ///     Ogg Vorbis through the native engine's reader (JUCE's libvorbis), the one the
    ///     editor plays it with. Reached only after
    ///     <see cref="TryLoadWithNativeDecoder" /> returned null with no ffmpeg, so the
    ///     failure is reported here with the file named rather than swallowed.
    /// </summary>
    internal static PcmAudio ReadOgg(string path)
    {
        JuceSharp.AudioIo.AudioBuffer buf;
        try
        {
            buf = JuceSharp.AudioIo.JuceAudioDecoder.Decode(path);
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidDataException($"Cannot decode the Ogg file '{path}': {ex.Message}", ex);
        }

        var samples = new short[buf.Interleaved.Length];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = FloatToPcm16(buf.Interleaved[i]);

        return new PcmAudio(samples, buf.Channels, buf.SampleRate);
    }

    /// <summary>Reads a RIFF/WAVE file: 8/16/24/32-bit integer or 32-bit float.</summary>
    private static PcmAudio ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException($"'{path}' is not a RIFF/WAVE file.");
        }

        int channels = 0, sampleRate = 0, bitsPerSample = 0, formatTag = 0;
        ReadOnlySpan<byte> data = default;
        var haveFmt = false;

        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var body = offset + 8;
            if (size < 0 || body + size > bytes.Length) size = bytes.Length - body;

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14, 2));
                haveFmt = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                data = bytes.AsSpan(body, size);
            }

            offset = body + size + (size & 1); // chunks are word-aligned
        }

        if (!haveFmt || channels <= 0 || sampleRate <= 0)
            throw new InvalidDataException($"'{path}' has no usable fmt chunk.");
        if (data.IsEmpty)
            throw new InvalidDataException($"'{path}' has no data chunk.");

        const int formatFloat = 3;
        var samples = bitsPerSample switch
        {
            16 => Convert16(data),
            8 => Convert8(data),
            24 => Convert24(data),
            32 when formatTag == formatFloat => Convert32Float(data),
            32 => Convert32Int(data),
            _ => throw new NotSupportedException(
                $"Unsupported WAV bit depth {bitsPerSample} in '{Path.GetFileName(path)}'.")
        };

        return new PcmAudio(samples, channels, sampleRate);
    }

    private static short[] Convert16(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));
        return samples;
    }

    private static short[] Convert8(ReadOnlySpan<byte> data)
    {
        // 8-bit WAV is unsigned, centred on 128.
        var samples = new short[data.Length];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)((data[i] - 128) << 8);
        return samples;
    }

    private static short[] Convert24(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length / 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var b = data.Slice(i * 3, 3);
            var value = b[0] | (b[1] << 8) | ((sbyte)b[2] << 16);
            samples[i] = (short)(value >> 8);
        }

        return samples;
    }

    private static short[] Convert32Int(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length / 4];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i * 4, 4)) >> 16);
        return samples;
    }

    private static short[] Convert32Float(ReadOnlySpan<byte> data)
    {
        var samples = new short[data.Length / 4];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = FloatToPcm16(BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4, 4)));
        return samples;
    }

    private static short FloatToPcm16(float value) =>
        (short)Math.Clamp((int)Math.Round(value * 32767f), short.MinValue, short.MaxValue);

    // ---- conform to 48 kHz stereo ------------------------------------------

    private static PcmAudio Conform(PcmAudio audio, IList<string> warnings)
    {
        var result = audio;

        if (result.Channels != RocksmithChannels)
        {
            warnings.Add($"Audio had {result.Channels} channel(s); remixed to stereo.");
            result = Remix(result, RocksmithChannels);
        }

        if (result.SampleRate != RocksmithSampleRate)
        {
            warnings.Add(
                $"Audio was {result.SampleRate} Hz; linearly resampled to {RocksmithSampleRate} Hz. " +
                "Install ffmpeg for a better resampler.");
            result = Resample(result, RocksmithSampleRate);
        }

        return result;
    }

    private static PcmAudio Remix(PcmAudio audio, int targetChannels)
    {
        var frames = audio.FrameCount;
        var output = new short[frames * targetChannels];

        for (var f = 0; f < frames; f++)
        {
            // Downmix by averaging, upmix by copying the mono source to both sides.
            var sum = 0;
            for (var c = 0; c < audio.Channels; c++) sum += audio.Samples[(f * audio.Channels) + c];
            var mono = (short)(sum / audio.Channels);

            for (var c = 0; c < targetChannels; c++)
            {
                output[(f * targetChannels) + c] = audio.Channels == 1
                    ? audio.Samples[f]
                    : c < audio.Channels
                        ? audio.Samples[(f * audio.Channels) + c]
                        : mono;
            }
        }

        return audio with { Samples = output, Channels = targetChannels };
    }

    private static PcmAudio Resample(PcmAudio audio, int targetRate)
    {
        var channels = audio.Channels;
        var sourceFrames = audio.FrameCount;
        if (sourceFrames == 0) return audio with { SampleRate = targetRate };

        var ratio = targetRate / (double)audio.SampleRate;
        var targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * ratio));
        var output = new short[targetFrames * channels];

        for (var f = 0; f < targetFrames; f++)
        {
            var position = f / ratio;
            var index = (int)position;
            var fraction = position - index;
            var next = Math.Min(index + 1, sourceFrames - 1);

            for (var c = 0; c < channels; c++)
            {
                var a = audio.Samples[(Math.Min(index, sourceFrames - 1) * channels) + c];
                var b = audio.Samples[(next * channels) + c];
                output[(f * channels) + c] = (short)Math.Clamp(a + ((b - a) * fraction), short.MinValue, short.MaxValue);
            }
        }

        return audio with { Samples = output, SampleRate = targetRate };
    }
}
