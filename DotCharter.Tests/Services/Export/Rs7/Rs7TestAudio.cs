using System.Buffers.Binary;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     Real audio for the rs7string test packages (#309). The game ends a song when its audio ends, so a 4 KB
///     placeholder cut the in-game test charts off after about 7 s. The generators write a quiet click track at
///     least as long as the chart and hand it to <see cref="RocksmithPackageBuilder" /> as a WAV, so the package
///     carries what the product's normal audio export path makes of it (decode, resample, Vorbis wem).
/// </summary>
internal static class Rs7TestAudio
{
    /// <summary>Audio kept after the chart's last beat so the song never ends under its last note.</summary>
    public const double OutroMs = 3000;

    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const short ClickAmplitude = 1500; // about -27 dBFS
    private const int ClickFrames = SampleRate / 100;

    /// <summary>Writes a 48 kHz stereo 16-bit WAV of exactly <paramref name="lengthMs" />: silence with a short quiet click on every beat.</summary>
    public static void WriteClickWav(string path, double lengthMs)
    {
        var frames = (int)Math.Ceiling(lengthMs / 1000.0 * SampleRate);
        var beatFrames = (int)Math.Round(Rs7TestSupport.BeatMs / 1000.0 * SampleRate);
        var dataBytes = frames * Channels * 2;
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * 2);
        writer.Write((short)(Channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var frame = 0; frame < frames; frame++)
        {
            var inClick = frame % beatFrames < ClickFrames;
            var sample = inClick ? (short)(ClickAmplitude * Math.Sin(2 * Math.PI * 1000 * frame / SampleRate)) : (short)0;
            for (var c = 0; c < Channels; c++) writer.Write(sample);
        }
    }

    /// <summary>
    ///     Builds a plain package of <paramref name="project" /> with a click-track WAV of <c>songLengthMs + OutroMs</c>
    ///     written to <paramref name="workDir" />. The encoded wem stays in that source folder's metadata cache (<see cref="WorkWem" />) for the caller to measure.
    /// </summary>
    public static Task<RocksmithBuildResult> BuildPackageAsync(
        ChartProject project, string psarcPath, string dlcKey, double songLengthMs, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var wav = Path.Combine(workDir, "audio.wav");
        WriteClickWav(wav, songLengthMs + OutroMs);
        return new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(project,
            new RocksmithBuildOptions
            {
                OutputPath = psarcPath,
                AudioPath = wav,
                DlcKey = dlcKey,
                IncludeVocals = false,
                SongLengthMs = songLengthMs,
                PreviewStartMs = 0,
                PlainPackage = true
            }, progress: null, ct);
    }

    /// <summary>The Vorbis wem <see cref="BuildPackageAsync" /> encoded in <paramref name="workDir" />: the bytes the package's audio entry holds.</summary>
    public static string WorkWem(string workDir) => ManagedAudioCache.WemPath(Path.Combine(workDir, "audio.wav"));

    /// <summary>The playing time of a Wwise Vorbis wem: its stored sample count over its sample rate.</summary>
    public static double WemDurationMs(string wemPath)
    {
        var bytes = File.ReadAllBytes(wemPath);
        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(position + 4));
            if (bytes.AsSpan(position, 4).SequenceEqual("fmt "u8))
            {
                var body = position + 8;
                var rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4));
                var samples = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 0x18));
                return samples * 1000.0 / rate;
            }

            position += 8 + size + (size & 1);
        }

        throw new InvalidDataException($"'{wemPath}' has no fmt chunk.");
    }
}
