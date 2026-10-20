using System.Buffers.Binary;
using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     End-to-end: a Rocksmith package built with NO Wwise installed. Asserts the
///     thing that actually breaks a CDLC — that the sound bank declares the same
///     codec that is really inside the .wem. If those disagree, the game selects
///     the wrong decoder and the song is silently silent.
/// </summary>
public partial class WwiseFreeExportTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter_wwisefree_").FullName;

    public void Dispose()
    {
        BestEffortDelete.Directory(_tempDir);
        GC.SuppressFinalize(this);
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>
    ///     Digs the codec plugin id out of a .bnk: HIRC chunk -> object count ->
    ///     first object (Sound, type 2) -> [u32 soundId][u32 pluginId].
    /// </summary>
    private static uint ReadSoundObjectPluginId(byte[] bank)
    {
        var offset = 0;
        while (offset + 8 <= bank.Length)
        {
            var id = bank.AsSpan(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bank.AsSpan(offset + 4, 4));
            var body = offset + 8;

            if (id.SequenceEqual("HIRC"u8))
            {
                var p = body + 4;              // skip numObjects
                var type = bank[p];
                Assert.Equal(2, type);         // 2 = Sound
                p += 1 + 4;                    // skip type byte + object length
                p += 4;                        // skip soundId
                return BinaryPrimitives.ReadUInt32LittleEndian(bank.AsSpan(p, 4));
            }

            offset = body + size;
        }

        throw new InvalidOperationException("no HIRC chunk in bank");
    }

    /// <summary>A minimal RIFF/WAVE container carrying an arbitrary <c>fmt </c>
    /// format tag — enough for <see cref="WemCodecInfo.Detect" /> to read a codec
    /// out of it, without needing an encoder for that codec.</summary>
    private static void WriteWemWithFormatTag(string path, ushort formatTag)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);

        const int payload = 256;
        writer.Write("RIFF"u8);
        writer.Write(4 + 8 + 0x18 + 8 + payload);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(0x18);
        writer.Write(formatTag);        // 0x14
        writer.Write((ushort)2);        // channels
        writer.Write(48000);            // sample rate
        writer.Write(48000 * 4);        // avg bytes/sec
        writer.Write((ushort)4);        // block align
        writer.Write((ushort)16);       // bits
        writer.Write((ushort)6);        // cbSize
        writer.Write((ushort)0);        // reserved
        writer.Write(3u);               // channel config
        writer.Write("data"u8);
        writer.Write(payload);
        writer.Write(new byte[payload]);
    }

    /// <summary>A short 48 kHz stereo 16-bit WAV, so no resampling is needed and the
    /// test does not depend on ffmpeg being installed.</summary>
    private static string WriteWav(string path, int seconds)
    {
        const int rate = 48000;
        const int channels = 2;
        var frames = rate * seconds;

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);

        var dataBytes = frames * channels * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * sizeof(short));
        writer.Write((ushort)(channels * sizeof(short)));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (var f = 0; f < frames; f++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 220 * f / rate) * 12000);
            for (var c = 0; c < channels; c++) writer.Write(v);
        }

        return path;
    }

    private static ChartProject CreateProject()
    {
        var project = new ChartProject
        {
            Bpm = 120,
            Chart =
            {
                SongName = "Wwise Free",
                Artist = "DotCharter",
                AlbumName = "Test Album",
                AlbumYear = "2026"
            }
        };

        for (var i = 0; i < 64; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        return project;
    }
}

internal static class WemCodecTestExtensions
{
    /// <summary>Mirrors WemCodecInfo.PluginId, which is internal to the extension class.</summary>
    public static uint PluginIdForTest(this WemCodec codec) => codec switch
    {
        WemCodec.Pcm => 0x00010001u,
        WemCodec.ImaAdpcm => 0x00020001u,
        WemCodec.Vorbis => 0x00040001u,
        _ => throw new ArgumentOutOfRangeException(nameof(codec))
    };
}
