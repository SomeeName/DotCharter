using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Package.DcSong;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Package.DcSong;

/// <summary>Synthetic audio, synthetic images and hand-built packages for the
/// <c>.dcsong</c> tests. No corpus audio: every sample is generated here.</summary>
internal static class DcSongTestKit
{
    /// <summary>A 1x1 PNG and a minimal JPEG header: enough for the media-type sniff.</summary>
    public static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89
    ];

    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];

    /// <summary>True when the encoder the writer uses can find ffmpeg here.</summary>
    public static bool FfmpegAvailable => new FfmpegStemEncoder().IsExternalEncoderAvailable;

    /// <summary>
    ///     Song-like 16-bit PCM: three detuned partials of a moving chord plus a
    ///     little seeded noise, so FLAC cannot shrink it to nothing and a bit-exact
    ///     check has real entropy to compare.
    /// </summary>
    public static short[] SongSamples(int sampleRate, int channels, double seconds, int seed = 7)
    {
        var frames = (int)(sampleRate * seconds);
        var samples = new short[frames * channels];
        var random = new Random(seed);
        double[] roots = [110.0, 146.83, 164.81, 98.0];
        for (var f = 0; f < frames; f++)
        {
            var t = f / (double)sampleRate;
            var root = roots[(int)(t / 2) % roots.Length];
            var value = 0.0;
            foreach (var ratio in new[] { 1.0, 1.5, 2.0 })
                value += Math.Sin(2 * Math.PI * root * ratio * t) / ratio;
            for (var c = 0; c < channels; c++)
            {
                var noise = (random.NextDouble() - 0.5) * 0.08;
                samples[(f * channels) + c] = (short)Math.Clamp((value * 0.25 + noise) * 32767 * (c == 0 ? 1.0 : 0.9), short.MinValue, short.MaxValue);
            }
        }

        return samples;
    }

    public static string WriteWav(string path, int sampleRate, int channels, double seconds, int seed = 7)
    {
        WriteWav(path, SongSamples(sampleRate, channels, seconds, seed), sampleRate, channels);
        return path;
    }

    public static void WriteWav(string path, short[] samples, int sampleRate, int channels)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file);
        var dataBytes = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
    }

    /// <summary>A 16-bit PCM WAV, chunk by chunk (ffmpeg adds a LIST chunk).</summary>
    public static (short[] Samples, int SampleRate, int Channels) ReadWav16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var span = bytes.AsSpan();
        int channels = 0, rate = 0, bits = 0;
        var pos = 12;
        while (pos + 8 <= span.Length)
        {
            var id = Encoding.ASCII.GetString(bytes, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(span[(pos + 4)..]);
            var body = pos + 8;
            if (id == "fmt ")
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(span[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(span[(body + 4)..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(span[(body + 14)..]);
            }
            else if (id == "data")
            {
                Assert.Equal(16, bits);
                var length = Math.Min(size, span.Length - body);
                var samples = new short[length / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(span[(body + (i * 2))..]);
                return (samples, rate, channels);
            }

            pos = body + size + (size & 1);
        }

        throw new InvalidDataException($"'{path}' has no data chunk.");
    }

    public static void CopyToFile(Stream source, string path)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        source.CopyTo(file);
    }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A small, valid chart's <c>.rscp</c> bytes.</summary>
    public static byte[] ChartBytes(string dir, ChartProject? project = null)
    {
        var path = Path.Combine(dir, $"chart-{Guid.NewGuid():N}.rscp");
        new RscpChartSerializer().Save(path, project ?? new ChartProject { AudioFileName = "audio/song.opus" });
        return File.ReadAllBytes(path);
    }

    /// <summary>One member of a hand-built package.</summary>
    public sealed record Member(string Path, byte[] Bytes, bool Deflate = false);

    /// <summary>
    ///     Writes a zip of <paramref name="members" /> plus a manifest built from
    ///     <paramref name="files" /> (or, when null, from the members themselves:
    ///     the first <c>chart.rscp</c> and <c>audio/song.opus</c> get their roles).
    ///     <paramref name="manifestJson" /> overrides the manifest bytes entirely.
    /// </summary>
    public static void BuildPackage(
        string path, IReadOnlyList<Member> members, IReadOnlyList<DcSongFileEntry>? files = null,
        byte[]? manifestJson = null, string formatVersion = "1.0")
    {
        files ??= members.Select(m => new DcSongFileEntry(m.Path, RoleOf(m.Path), m.Bytes.Length, Sha(m.Bytes))).ToList();
        var manifest = manifestJson ?? ManifestBytes(files, formatVersion);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open())
            entry.Write(manifest);
        foreach (var member in members)
        {
            using var entry = zip.CreateEntry(member.Path, member.Deflate ? CompressionLevel.Optimal : CompressionLevel.NoCompression).Open();
            entry.Write(member.Bytes);
        }
    }

    public static byte[] ManifestBytes(IReadOnlyList<DcSongFileEntry> files, string formatVersion = "1.0")
    {
        var manifest = new DcSongManifest(1, 0, "test",
            new DcSongInfo("DCTestSong", "", "Title", "Artist", "", "", "", 1000, false, []),
            new DcSongAudioInfo(DcSongAudioCodec.Opus, false, 192, 0, 0),
            files);
        var json = Encoding.UTF8.GetString(DcSongManifestJson.Write(manifest));
        return Encoding.UTF8.GetBytes(json.Replace("\"formatVersion\": \"1.0\"", $"\"formatVersion\": \"{formatVersion}\"", StringComparison.Ordinal));
    }

    /// <summary>The members of the smallest valid package: a chart and an "Opus" song.</summary>
    public static List<Member> MinimalMembers(string dir, byte[]? audio = null) =>
    [
        new("chart.rscp", ChartBytes(dir), Deflate: true),
        new("audio/song.opus", audio ?? RandomBytes(4096, 1))
    ];

    public static byte[] RandomBytes(int count, int seed)
    {
        var bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>The song and drum-stem members of <see cref="BuildProjectPackage" />.</summary>
    public static readonly byte[] PackagedSong = RandomBytes(6000, 3);

    public static readonly byte[] PackagedDrums = RandomBytes(3000, 4);

    /// <summary>A package as the writer lays it out: the chart's references point into
    /// the package (DcSongChart.ForPackage), the manifest lists every member.</summary>
    public static string BuildProjectPackage(string dir, bool lossless, bool cover = false, bool stems = false)
    {
        var codec = lossless ? DcSongAudioCodec.Flac : DcSongAudioCodec.Opus;
        var audioPath = DcSongManifestJson.AudioPath(codec);
        var project = new ChartProject();
        project.Chart.SongName = "The Song";
        project.Chart.Artist = "The Artist";
        project.Arrangements.Add(new Arrangement { Name = "Lead" });
        project.Arrangements.Add(new Arrangement { Name = "Bass" });
        project.Stems.Add(new Stem { Name = "drums", Volume = 0.25, Muted = true });
        var packaged = DcSongChart.ForPackage(project, audioPath, cover ? "images/cover.png" : null,
            stems ? [("drums", DcSongManifestJson.StemPath("drums"))] : []);

        var members = new List<Member> { new("chart.rscp", ChartBytes(dir, packaged), Deflate: true), new(audioPath, PackagedSong) };
        var files = new List<DcSongFileEntry>
        {
            new("chart.rscp", DcSongFileRole.Chart, members[0].Bytes.Length, Sha(members[0].Bytes)),
            new(audioPath, DcSongFileRole.Audio, PackagedSong.Length, Sha(PackagedSong))
        };
        if (cover)
        {
            members.Add(new Member("images/cover.png", Png));
            files.Add(new DcSongFileEntry("images/cover.png", DcSongFileRole.Cover, Png.Length, Sha(Png), MediaType: DcSongImages.Png));
        }

        if (stems)
        {
            var path = DcSongManifestJson.StemPath("drums");
            members.Add(new Member(path, PackagedDrums));
            files.Add(new DcSongFileEntry(path, DcSongFileRole.Stem, PackagedDrums.Length, Sha(PackagedDrums), StemName: "drums"));
        }

        var manifest = new DcSongManifest(1, 0, "test",
            new DcSongInfo("DCTheSong", "", "The Song", "The Artist", "", "", "", 1000, false, []),
            new DcSongAudioInfo(codec, lossless, lossless ? null : 192, 0, 0),
            files);
        var output = Path.Combine(dir, $"song-{Guid.NewGuid():N}.dcsong");
        BuildPackage(output, members, files, DcSongManifestJson.Write(manifest));
        return output;
    }

    /// <summary>"Decodes" by writing a marker plus the encoded bytes, so a test can tell
    /// which member reached which file.</summary>
    public sealed class FakeDecoder(bool available, bool fail = false) : IStemEncoder
    {
        public static byte[] Decoded(byte[] encoded) => [.. "RIFF"u8, .. encoded];

        public StemCodec PreferredCodec => available ? StemCodec.Opus : StemCodec.Wav;

        public bool IsExternalEncoderAvailable => available;

        public Task<string> EncodeAsync(string wavPath, string destPathWithoutExtension, StemCodec codec, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> DecodeToWavAsync(string encodedPath, StemCodec codec, string destWavPath, CancellationToken ct = default)
        {
            Assert.Equal(StemCodec.Opus, codec);
            if (fail) throw new StemDecodeUnavailableException("ffmpeg failed.");
            File.WriteAllBytes(destWavPath, Decoded(File.ReadAllBytes(encodedPath)));
            return Task.FromResult(destWavPath);
        }
    }

    /// <summary>Overwrites the uncompressed-size field of <paramref name="name" />'s
    /// CENTRAL record — a sender lying about how big a member is.</summary>
    public static void PatchCentralUncompressedSize(string path, string name, uint size)
    {
        var bytes = File.ReadAllBytes(path);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        for (var i = 0; i + 46 <= bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 28));
            if (!bytes.AsSpan(i + 46, nameLength).SequenceEqual(nameBytes)) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), size);
            File.WriteAllBytes(path, bytes);
            return;
        }

        throw new InvalidOperationException($"no central record for '{name}'.");
    }

    /// <summary>Flips one byte in the middle of the first occurrence of <paramref name="payload" />.</summary>
    public static void FlipByteInside(string path, byte[] payload)
    {
        var bytes = File.ReadAllBytes(path);
        var at = bytes.AsSpan().IndexOf(payload.AsSpan(0, 64));
        Assert.True(at >= 0, "payload not found in the package");
        bytes[at + (payload.Length / 2)] ^= 0x5A;
        File.WriteAllBytes(path, bytes);
    }

    private static DcSongFileRole RoleOf(string path) => path switch
    {
        "chart.rscp" => DcSongFileRole.Chart,
        "audio/song.opus" or "audio/song.flac" => DcSongFileRole.Audio,
        "audio/preview.opus" => DcSongFileRole.Preview,
        _ => DcSongFileRole.Other
    };
}
