using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Session;

/// <summary>Derived volume migration must reach the actual project write and its companions once.</summary>
public sealed partial class ProjectSessionSaveOutcomeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Legacy_load_save_reload_persists_automatic_volumes_but_preserves_manual_overrides(bool saveAs, bool manual)
    {
        var companions = new SaveCompanionService(new FakeConfigurationService
        {
            AppConfig = new AppConfig
            {
                SaveCompanionEof = false, SaveCompanionEofAudio = false,
                SaveCompanionPsarc = false, SaveCompanionRs2Dlc = true
            }
        }, new FakeAppLogger());
        var meter = new MeasuringCompanions(companions);
        var h = CreateHarness(out var origin, meter);
        var source = Path.ChangeExtension(origin, ".dcproj");
        var audio = WriteMonoVolumeSource(Path.GetDirectoryName(source)!);
        var legacy = new ChartProject { AudioFileName = Path.GetFileName(audio) };
        legacy.Chart.SongName = "Migration";
        legacy.Chart.Artist = "Tests";
        legacy.Arrangements.Add(new Arrangement { Name = "Lead Guitar" });
        legacy.Chart.AudioVolume = 31;
        legacy.Chart.PreviewVolume = 32;
        legacy.Chart.AudioVolumeSignature = "legacy-audio";
        legacy.Chart.PreviewVolumeSignature = "legacy-preview";
        legacy.Chart.AudioVolumeManual = manual;
        legacy.Chart.PreviewVolumeManual = manual;
        new RscpChartSerializer().Save(source, legacy);

        await BoundedWait.ForAsync(h.Session.LoadFile(source), "loading a legacy volume project", TestContext.Current.CancellationToken);
        Assert.False(h.Session.IsProjectDirty);
        Assert.Empty(h.History.GetUndoSteps());
        var target = saveAs ? Path.Combine(Path.GetDirectoryName(source)!, "moved", "saved.dcproj") : source;
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(target)!, "export", "DTRS XML"));
        var saving = saveAs ? h.Session.Saver.SaveToPickedPathAsync(target) : h.Session.Saver.SaveProjectCoreAsync(target);
        Assert.True(await BoundedWait.ForAsync(saving, "saving migrated volumes", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the migrated companions", TestContext.Current.CancellationToken);

        Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(target), "persisting adopted volumes", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the persisted-volume companions", TestContext.Current.CancellationToken);
        var reloaded = new RscpChartSerializer().Load(target);
        var expected = new ChartData();
        ProjectVolumes.Measure(expected, audio, null, null);
        Assert.Equal(manual ? legacy.Chart.AudioVolume : expected.AudioVolume, reloaded.Chart.AudioVolume);
        Assert.Equal(manual ? legacy.Chart.PreviewVolume : expected.PreviewVolume, reloaded.Chart.PreviewVolume);
        Assert.Equal(manual ? legacy.Chart.AudioVolumeSignature : expected.AudioVolumeSignature, reloaded.Chart.AudioVolumeSignature);
        Assert.Equal(manual ? legacy.Chart.PreviewVolumeSignature : expected.PreviewVolumeSignature, reloaded.Chart.PreviewVolumeSignature);
        Assert.Equal(ProjectVolumeFields.From(reloaded.Chart), ProjectVolumeFields.From(h.State.ActiveProject!.Chart));
        var companion = Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(target)!, "export"), "*.rs2dlc"));
        using var dto = System.Text.Json.JsonDocument.Parse(File.ReadAllText(companion));
        Assert.Equal(reloaded.Chart.AudioVolume, dto.RootElement.GetProperty("AudioFile").GetProperty("Volume").GetDouble());
        Assert.Equal(reloaded.Chart.PreviewVolume, dto.RootElement.GetProperty("AudioPreviewFile").GetProperty("Volume").GetDouble());
        Assert.False(ProjectVolumes.Measure(reloaded.Chart, audio, null, null));
        Assert.False(h.Session.IsProjectDirty);
        Assert.Empty(h.History.GetUndoSteps());

        var bytes = File.ReadAllBytes(target);
        await BoundedWait.ForAsync(h.Session.LoadFile(target), "reloading migrated volumes", TestContext.Current.CancellationToken);
        Assert.True(await BoundedWait.ForAsync(h.Session.Saver.SaveProjectCoreAsync(target), "saving current volumes", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(h.Session.Saver.FlushCompanionsAsync(), "the unchanged companions", TestContext.Current.CancellationToken);
        Assert.Equal(bytes, File.ReadAllBytes(target));
        Assert.False(h.Session.IsProjectDirty);
        Assert.Empty(h.History.GetUndoSteps());
        Assert.Equal(manual ? 0 : 1, meter.Decodes);
    }

    private static string WriteMonoVolumeSource(string directory)
    {
        const int rate = 48000;
        var samples = Enumerable.Range(0, rate * 3)
            .Select(i => (short)(6000 * Math.Sin(2 * Math.PI * 440 * i / rate))).ToArray();
        var path = Path.Combine(directory, "mono.wav");
        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, 1, rate));
        return path;
    }
}
