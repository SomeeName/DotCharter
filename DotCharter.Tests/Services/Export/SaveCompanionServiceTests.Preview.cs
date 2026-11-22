using DotCharter.Services.Export.Audio;

namespace DotCharter.Tests.Services.Export;

/// <summary>A companion that cuts its own preview must measure that clip rather than a dialog's other source.</summary>
public sealed partial class SaveCompanionServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_companion_uses_its_main_clip_gain_after_an_explicit_preview_was_measured(bool manual)
    {
        var audio = WriteTone("main.wav", 0.6);
        var preview = WriteTone("quiet-preview.wav", 0.01);
        var project = CreateProject();
        Assert.True(ProjectVolumes.Measure(project.Chart, audio, preview, null));
        if (manual)
        {
            project.Chart.PreviewVolume = -13.3;
            project.Chart.PreviewVolumeManual = true;
        }
        var expected = new ChartData();
        Assert.True(ProjectVolumes.Measure(expected, audio, null, null));

        var (_, gain) = await BoundedWait.ForAsync(Create(new AppConfig()).VolumesForAsync(project, audio),
            "the companion main-clip measurement", TestContext.Current.CancellationToken);

        Assert.Equal(manual ? -13.3 : expected.PreviewVolume, gain);
        if (!manual) Assert.Equal(expected.PreviewVolumeSignature, project.Chart.PreviewVolumeSignature);
    }
}
