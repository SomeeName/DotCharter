using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.DlcBuilder;

namespace DotCharter.Tests.Services.Export;

/// <summary>The volumes a save companion writes are the SONG's own, not the last measured song's (#293).</summary>
public sealed partial class SaveCompanionServiceTests
{
    private string WriteTone(string name, double amplitude)
    {
        const int rate = 48000;
        var samples = new short[rate * 2 * 4];
        for (var i = 0; i < samples.Length / 2; i++)
        {
            var value = (short)(amplitude * 32767 * Math.Sin(2.0 * Math.PI * 330.0 * i / rate));
            samples[i * 2] = value;
            samples[(i * 2) + 1] = value;
        }

        var path = Path.Combine(_dir, name);
        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, 2, rate));
        return path;
    }

    [Fact]
    public async Task Two_songs_get_their_own_volumes_from_their_own_audio()
    {
        var service = Create(new AppConfig());
        var loud = CreateProject();
        var quiet = CreateProject();

        var (loudAudio, loudPreview) = await service.VolumesForAsync(loud, WriteTone("loud.wav", 0.6));
        var (quietAudio, quietPreview) = await service.VolumesForAsync(quiet, WriteTone("quiet.wav", 0.02));

        Assert.NotEqual(ProjectVolumes.DefaultAudioVolume, loudAudio);
        Assert.True(quietAudio > loudAudio + 10, $"quiet song {quietAudio} dB, loud song {loudAudio} dB");
        Assert.True(quietPreview > loudPreview + 10);
        Assert.Equal(loudAudio, loud.Chart.AudioVolume);
    }

    [Fact]
    public async Task A_song_without_readable_audio_gets_the_defaults()
    {
        var service = Create(new AppConfig());

        var (audio, preview) = await service.VolumesForAsync(CreateProject(), Path.Combine(_dir, "missing.wav"));

        Assert.Equal(ProjectVolumes.DefaultAudioVolume, audio);
        Assert.Equal(ProjectVolumes.DefaultPreviewVolume, preview);
    }

    [Fact]
    public void The_dlc_builder_project_carries_the_songs_volumes_not_a_fixed_one()
    {
        var project = CreateProject();
        project.Chart.AudioVolume = -3.7;
        project.Chart.PreviewVolume = -1.2;

        var dto = Rs2DlcProjectBuilder.Build(project, new Rs2DlcProjectBuilder.Rs2DlcBuildOptions("song.wav", "cover.jpg", 4000, "Alex"));

        Assert.Equal(-3.7, dto.AudioFile.Volume);
        Assert.Equal(-1.2, dto.AudioPreviewFile.Volume);
    }
}
