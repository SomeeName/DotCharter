using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>
///     Regression for the owner-reported companion-psarc failure (2026-07-18):
///     a flac-audio project failed the psarc build with "Cannot decode '.flac'
///     without ffmpeg" although the stem path decodes the SAME file natively.
///     <see cref="PcmAudioLoader.TryLoadWithNativeDecoder" /> now covers the
///     no-ffmpeg case. The flac fixture is encoded on the fly with the repo's
///     cached ffmpeg (tools/redist/win-x64) — skipped where neither that tool
///     nor the native DLL is available.
/// </summary>
public sealed class PcmAudioLoaderNativeDecodeTests : IDisposable
{
    private const int Sr = 44100;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    private static string? RedistFfmpeg()
    {
        // Repo-root cache the release packer fills; not on PATH by design.
        var repo = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && repo != null; i++, repo = Path.GetDirectoryName(repo))
        {
            var candidate = Path.Combine(repo, "tools", "redist", "win-x64", "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private string WriteToneWav()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "tone.wav");
        var n = Sr; // 1 second mono
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
    public void NativeDecoder_DecodesFlac_AndConformsTo48kStereo()
    {
        var wav = WriteToneWav();

        // Native-DLL availability probe: JUCE always reads WAV when present.
        var probe = PcmAudioLoader.TryLoadWithNativeDecoder(wav, []);
        Assert.SkipWhen(probe is null, "juce_sharp.dll not available in this run.");

        var ffmpeg = RedistFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "repo redist ffmpeg not cached on this machine.");

        var flac = Path.Combine(_dir, "tone.flac");
        var encode = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg!)
        {
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-y", "-i", wav, flac },
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        encode.WaitForExit(60_000);
        Assert.SkipWhen(!File.Exists(flac) || new FileInfo(flac).Length == 0, "flac encode failed on this machine.");

        var warnings = new List<string>();
        var pcm = PcmAudioLoader.TryLoadWithNativeDecoder(flac, warnings);

        Assert.NotNull(pcm);
        Assert.Equal(PcmAudioLoader.RocksmithChannels, pcm!.Channels);
        Assert.Equal(PcmAudioLoader.RocksmithSampleRate, pcm.SampleRate);
        // 1 s of audio resampled 44.1k -> 48k, allow codec padding tolerance.
        Assert.InRange(pcm.FrameCount, PcmAudioLoader.RocksmithSampleRate - 4800,
            PcmAudioLoader.RocksmithSampleRate + 4800);
        Assert.Contains(pcm.Samples, s => Math.Abs((int)s) > 4000); // non-silent
        // The native decode is the PRIMARY path now (the editor's decoder — see
        // PcmAudioLoaderEditorParityTests), so it no longer warns about itself;
        // only a linear resample, when no ffmpeg can be found, still does.
    }

    [Fact]
    public void NativeDecoder_ReturnsNull_OnUnreadableFile()
    {
        Directory.CreateDirectory(_dir);
        var garbage = Path.Combine(_dir, "garbage.flac");
        File.WriteAllBytes(garbage, [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03]);

        Assert.Null(PcmAudioLoader.TryLoadWithNativeDecoder(garbage, []));
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
