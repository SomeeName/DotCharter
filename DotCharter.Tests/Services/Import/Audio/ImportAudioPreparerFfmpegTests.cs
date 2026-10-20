using System.Diagnostics;
using System.IO.Compression;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Import.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Import.Audio;

/// <summary>
///     End-to-end bake with the repo-cached ffmpeg (tools/redist/win-x64, the copy
///     the release packer fills — not on PATH by design). Proves the produced
///     song.ogg is decodable and is longer than the source by ~lead+trail seconds.
///     Skips cleanly when ffmpeg is not cached on this machine.
/// </summary>
public sealed class ImportAudioPreparerFfmpegTests : IDisposable
{
    private const int Sr = 44100;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // BaseDirectory ends in a separator (one step to strip), and an agent worktree sits three levels
    // under the checkout that holds the git-ignored tools/redist.
    private static string? RedistFfmpeg()
    {
        var repo = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && repo != null; i++, repo = Path.GetDirectoryName(repo))
        {
            var candidate = Path.Combine(repo, "tools", "redist", "win-x64", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private string WriteToneWav(string name, int seconds)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        var n = Sr * seconds; // mono
        using var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write));
        var dataBytes = n * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Sr);
        writer.Write(Sr * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < n; i++)
            writer.Write((short)(Math.Sin(2 * Math.PI * 220 * i / Sr) * 12000));
        return path;
    }

    [Fact]
    public void Prepare_WithRealFfmpeg_ProducesDecodableOggLongerByLeadPlusTrail()
    {
        var ffmpeg = RedistFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "repo redist ffmpeg not cached on this machine.");

        const double lead = 2.0;
        const double trail = 1.0;
        var source = WriteToneWav("Band - Song.wav", seconds: 1);

        var result = ImportAudioPreparer.Prepare(
            source, _dir, leadingSeconds: lead, trailingSeconds: trail,
            configuredFfmpegPath: ffmpeg);

        // Baked: song.ogg working audio + preserved byte-exact original.
        Assert.True(result.SilenceBaked, "silence should have been baked with a real ffmpeg present.");
        Assert.Equal("song.ogg", result.WorkingAudioFileName);
        Assert.Equal("original.wav", result.OriginalFileName);
        var oggPath = Path.Combine(_dir, "song.ogg");
        Assert.True(File.Exists(oggPath));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(_dir, "original.wav")));

        // The ogg decodes, and is longer than the source by ~lead+trail (±0.5 s).
        var warnings = new List<string>();
        var sourcePcm = PcmAudioLoader.Load(source, warnings, ffmpeg);
        var oggPcm = PcmAudioLoader.Load(oggPath, warnings, ffmpeg);

        var delta = oggPcm.Duration.TotalSeconds - sourcePcm.Duration.TotalSeconds;
        Assert.InRange(delta, lead + trail - 0.5, lead + trail + 0.5);
    }

    /// <summary>
    ///     #461: a FLAC with cover art carries the picture as a video stream. Mapped into the ogg it
    ///     needs Theora: the shipped minimal ffmpeg has none and fails, a full one writes a video
    ///     stream into the song. Either way the ogg must come out audio-only.
    /// </summary>
    [Fact]
    public void Prepare_WithRealFfmpeg_BakesAFlacWithCoverArtIntoAnAudioOnlyOgg()
    {
        var ffmpeg = RedistFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "repo redist ffmpeg not cached on this machine.");

        var wav = WriteToneWav("tone.wav", seconds: 1);
        var png = Path.Combine(_dir, "cover.png");
        File.WriteAllBytes(png, OnePixelPng());
        var source = Path.Combine(_dir, "Band - Song.flac");
        Assert.True(RunFfmpeg(ffmpeg!,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", wav, "-i", png, "-map", "0:a", "-map", "1:v",
             "-c:a", "flac", "-c:v", "png", "-disposition:v", "attached_pic", source]),
            "could not build the cover-art FLAC fixture.");

        var bakeDir = Path.Combine(_dir, "project");
        Directory.CreateDirectory(bakeDir);
        var result = ImportAudioPreparer.Prepare(
            source, bakeDir, leadingSeconds: 1.0, trailingSeconds: 0.5, configuredFfmpegPath: ffmpeg);

        Assert.True(result.SilenceBaked, "the bake failed on a FLAC with cover art.");
        var ogg = File.ReadAllBytes(Path.Combine(bakeDir, "song.ogg"));
        Assert.True(ogg.AsSpan().IndexOf("theora"u8) < 0, "the cover art went into the ogg as a Theora stream.");

        // The manual Add-silence pads stems with the splicer's own vector: audio-only too.
        var stemOut = Path.Combine(_dir, "stem.wav");
        Assert.True(RunFfmpeg(ffmpeg!, AudioSilenceSplicer.BuildStemEncodeArgs(source, stemOut, "adelay=1000:all=1")));
    }

    private static bool RunFfmpeg(string ffmpeg, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        _ = process.StandardError.ReadToEnd();
        return process.WaitForExit(60_000) && process.ExitCode == 0;
    }

    /// <summary>A valid 1x1 RGB PNG, built here so the fixture needs no lavfi in the ffmpeg build.</summary>
    private static byte[] OnePixelPng()
    {
        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
                z.Write([0, 0xFF, 0, 0]);
            WriteChunk(png, "IDAT", raw.ToArray());
        }

        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        png.Write(BigEndian((uint)data.Length));
        png.Write(typeBytes);
        png.Write(data);
        png.Write(BigEndian(Crc32([.. typeBytes, .. data])));
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }
}
