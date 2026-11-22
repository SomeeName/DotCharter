using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export.Audio;

/// <summary>The volume meter must hear exactly the stereo PCM handed to the WEM encoder.</summary>
public sealed class RocksmithAutoVolumePackagedPcmTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-packaged-volume-").FullName;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Native_volume_measures_the_packaged_channels(int channels)
    {
        var path = WriteSignal(channels);
        var pcm = PcmDecoderFixture.Load(path, native: true, ffmpeg: false);

        Assert.Equal(Volume(pcm), RocksmithAutoVolume.Calculate(path));
    }

    [Fact]
    public void Explicit_ffmpeg_mono_volume_measures_the_packaged_channels()
    {
        var wav = WriteSignal(1);
        var ffmpeg = PcmDecoderFixture.Ffmpeg("aac");
        var path = Path.ChangeExtension(wav, ".m4a");
        var encode = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-hide_banner", "-loglevel", "error", "-y", "-i", wav, "-c:a", "aac", path }
        };
        var run = BoundedProcess.Run(encode, TimeSpan.FromMinutes(2), "ffmpeg (mono volume fixture)");
        Assert.True(run.ExitCode == 0, run.StandardError);
        var pcm = PcmAudioLoader.Load(path, [], _ => null, ffmpeg);

        Assert.Equal(Volume(pcm), RocksmithAutoVolume.Calculate(path, _ => null, ffmpeg));
    }

    private string WriteSignal(int channels)
    {
        const int rate = 48000;
        var samples = new short[rate * 3 * channels];
        for (var frame = 0; frame < samples.Length / channels; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
                samples[frame * channels + channel] = (short)(6000 * Math.Sin(2 * Math.PI * 440 * frame / rate));
        }
        var path = Path.Combine(_dir, $"signal-{channels}.wav");
        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, channels, rate));
        return path;
    }

    private static double Volume(PcmAudio pcm) => RocksmithAutoVolume.FromSamples(
        pcm.Samples.Select(sample => sample / 32768f).ToArray(), pcm.Channels, pcm.SampleRate);

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
