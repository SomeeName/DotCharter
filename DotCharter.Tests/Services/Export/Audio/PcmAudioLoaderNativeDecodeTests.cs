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

    private static string RedistFfmpeg() => PcmDecoderFixture.Ffmpeg();

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
        // #608: both pipes drain concurrently under one cap (a deadlock detector, never a budget); a hung ffmpeg fails the test.
        var encode = new System.Diagnostics.ProcessStartInfo(ffmpeg!)
        {
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-y", "-i", wav, flac }
        };
        var run = BoundedProcess.Run(encode, TimeSpan.FromMinutes(2), "ffmpeg (flac fixture)");
        Assert.True(run.ExitCode == 0, $"ffmpeg exited {run.ExitCode} encoding flac: {run.StandardError}");
        Assert.True(File.Exists(flac) && new FileInfo(flac).Length > 0, "ffmpeg produced no flac file.");

        var warnings = new List<string>();
        var pcm = PcmAudioLoader.Load(flac, warnings, JuceSharp.AudioIo.JuceAudioDecoder.Decode, ffmpeg);

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

    /// <summary>
    ///     A mono file decodes at the same level whether or not an ffmpeg is found: ffmpeg's
    ///     mono-to-stereo upmix is 3 dB down, and which ffmpeg the loader resolves depends on
    ///     where the build output sits, so the recording-alignment stems came back at 1/sqrt(2)
    ///     in one checkout and at full level in another. The ffmpeg is handed in explicitly.
    /// </summary>
    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void MonoSource_DecodesAtFullLevel_WhenFfmpegIsAvailable(int sampleRate)
    {
        var wav = WriteConstantMonoWav(sampleRate, 1000);
        Assert.SkipWhen(PcmAudioLoader.TryLoadWithNativeDecoder(wav, []) is null, "juce_sharp.dll not available in this run.");
        var ffmpeg = RedistFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "repo redist ffmpeg not cached on this machine.");

        var pcm = PcmAudioLoader.Load(wav, [], JuceSharp.AudioIo.JuceAudioDecoder.Decode, ffmpeg);

        Assert.Equal(PcmAudioLoader.RocksmithChannels, pcm.Channels);
        Assert.Equal(PcmAudioLoader.RocksmithSampleRate, pcm.SampleRate);
        // The middle of the file: a resampler may ramp the first and last few samples.
        var middle = pcm.Samples.AsSpan(pcm.Samples.Length / 10, pcm.Samples.Length * 8 / 10).ToArray();
        Assert.All(middle, s => Assert.InRange(s, (short)990, (short)1010));
    }

    private string WriteConstantMonoWav(int sampleRate, short value)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"mono-{sampleRate}.wav");
        var n = sampleRate; // 1 second
        using var writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write));
        writer.Write("RIFF"u8);
        writer.Write(36 + (n * 2));
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(n * 2);
        for (var i = 0; i < n; i++) writer.Write(value);
        return path;
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
