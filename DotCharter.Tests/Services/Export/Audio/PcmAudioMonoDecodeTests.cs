using DotCharter.Services.Export.Audio;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>Mono amplitude is independent of native availability and explicit ffmpeg selection.</summary>
public sealed class PcmAudioMonoDecodeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-mono-decode-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Theory]
    [InlineData(48000, true, false)]
    [InlineData(44100, true, false)]
    [InlineData(48000, true, true)]
    [InlineData(44100, true, true)]
    [InlineData(48000, false, true)]
    [InlineData(44100, false, true)]
    [InlineData(48000, false, false)]
    [InlineData(44100, false, false)]
    public void MonoIsCopiedAtFullLevel(int sampleRate, bool native, bool ffmpeg)
    {
        var path = Path.Combine(_dir, "constant.wav");
        PcmAudioLoader.WriteWav(path, new PcmAudio(Enumerable.Repeat((short)1000, sampleRate).ToArray(), 1, sampleRate));

        var pcm = PcmDecoderFixture.Load(path, native, ffmpeg);

        Assert.Equal(2, pcm.Channels);
        Assert.Equal(48000, pcm.SampleRate);
        Assert.Equal(48000, pcm.FrameCount);
        Assert.All(pcm.Samples.Skip(9600).Take(76800), sample => Assert.InRange(sample, (short)990, (short)1010));
    }

    [Theory]
    [InlineData("m4a", "aac")]
    [InlineData("aac", "aac")]
    [InlineData("opus", "libopus")]
    public void FfmpegOnlyMonoFormatsKeepTheirLevel(string extension, string codec)
    {
        var samples = Enumerable.Range(0, 48000)
            .Select(i => (short)(12000 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray();
        var source = Path.Combine(_dir, "tone.wav");
        PcmAudioLoader.WriteWav(source, new PcmAudio(samples, 1, 48000));
        var encoded = Path.Combine(_dir, "tone." + extension);
        var ffmpeg = PcmDecoderFixture.Ffmpeg(codec);
        var encode = MediaProcess.Run(ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-c:a", codec, "-b:a", "192k", encoded],
            TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.True(encode.Succeeded, encode.StandardError);

        // Force the fallback even if this machine's native decoder supports this format.
        var pcm = PcmAudioLoader.Load(encoded, [], _ => null, ffmpeg);

        Assert.Equal(2, pcm.Channels);
        Assert.Equal(48000, pcm.SampleRate);
        var middle = pcm.Samples.Skip(19200).Take(57600).ToArray();
        var rms = Math.Sqrt(middle.Average(sample => (double)sample * sample));
        Assert.InRange(rms, 8000, 9000);
        for (var i = 0; i < middle.Length; i += 2) Assert.Equal(middle[i], middle[i + 1]);
    }
}
