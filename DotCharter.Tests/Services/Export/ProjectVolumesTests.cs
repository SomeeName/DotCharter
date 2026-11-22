using DotCharter.Services.Export.Audio;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     A song's own export volume: measured from the audio that ships and kept with a signature
///     that includes the target. Automatic values refresh when the audio or target changes.
/// </summary>
public sealed class ProjectVolumesTests : IDisposable
{
    [Fact]
    public void Export_volume_overrides_are_one_dirty_undo_step_including_the_manual_flags()
    {
        var chart = new ChartData { AudioVolume = -10, PreviewVolume = -9, AudioVolumeSignature = "audio", PreviewVolumeSignature = "preview" };
        var history = new HistoryService(new FakeAppLogger());
        using var dirty = new ProjectDirtyTracker(history);

        RocksmithExportSupport.CommitVolumes(history, chart, -3, -2);

        Assert.True(dirty.IsDirty);
        Assert.Single(history.GetUndoSteps());
        Assert.True(chart.AudioVolumeManual);
        Assert.True(chart.PreviewVolumeManual);
        history.Undo();
        Assert.False(dirty.IsDirty);
        Assert.Equal(-10, chart.AudioVolume);
        Assert.Equal(-9, chart.PreviewVolume);
        Assert.False(chart.AudioVolumeManual);
        Assert.False(chart.PreviewVolumeManual);
        Assert.Equal("audio", chart.AudioVolumeSignature);
        Assert.Equal("preview", chart.PreviewVolumeSignature);
        history.Redo();
        Assert.Equal(-3, chart.AudioVolume);
        Assert.Equal(-2, chart.PreviewVolume);
        Assert.True(chart.AudioVolumeManual);
        Assert.True(chart.PreviewVolumeManual);
    }

    [Fact]
    public async Task Export_measurement_uses_a_detached_chart_and_commits_one_undoable_result()
    {
        var chart = new ChartData();
        var path = WriteSignal("detached.wav", 0.3, seconds: 5);
        var history = new HistoryService(new FakeAppLogger());
        using var dirty = new ProjectDirtyTracker(history);

        var measurement = await RocksmithExportSupport.MeasureProjectVolumesAsync(chart, path, "", new FakeAppLogger());

        Assert.Null(chart.AudioVolume);
        Assert.Null(chart.PreviewVolume);
        Assert.False(dirty.IsDirty);
        RocksmithExportSupport.CommitMeasuredVolumes(history, chart, measurement);
        Assert.True(dirty.IsDirty);
        Assert.Single(history.GetUndoSteps());
        Assert.NotNull(chart.AudioVolume);
        Assert.NotNull(chart.PreviewVolume);
        Assert.NotEmpty(chart.AudioVolumeSignature);
        Assert.NotEmpty(chart.PreviewVolumeSignature);
        history.Undo();
        Assert.False(dirty.IsDirty);
        Assert.Null(chart.AudioVolume);
        Assert.Null(chart.PreviewVolume);
        Assert.Empty(chart.AudioVolumeSignature);
        Assert.Empty(chart.PreviewVolumeSignature);
        history.Redo();
        Assert.NotNull(chart.AudioVolume);
    }

    [Fact]
    public async Task An_in_flight_measurement_does_not_overwrite_a_newer_volume_edit()
    {
        var chart = new ChartData();
        var measurement = RocksmithExportSupport.MeasureProjectVolumesAsync(chart, WriteSignal("race.wav", 0.3, seconds: 1), "", new FakeAppLogger());
        var history = new HistoryService(new FakeAppLogger());
        RocksmithExportSupport.CommitVolumes(history, chart, -3, -2);

        RocksmithExportSupport.CommitMeasuredVolumes(history, chart, await measurement);

        Assert.Single(history.GetUndoSteps());
        Assert.Equal(-3, chart.AudioVolume);
        Assert.Equal(-2, chart.PreviewVolume);
        Assert.True(chart.AudioVolumeManual);
        Assert.True(chart.PreviewVolumeManual);
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-volumes-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>The synthetic stereo signal RocksmithAutoVolumeTests pins to DLC Builder's output, as 16-bit PCM.</summary>
    private string WriteSignal(string name, double amplitude, double seconds = 30)
    {
        const int rate = 48000;
        var frames = (int)(seconds * rate);
        var random = new Random(1234);
        var samples = new short[frames * 2];
        for (var channel = 0; channel < 2; channel++)
        {
            for (var i = 0; i < frames; i++)
            {
                var t = (double)i / rate;
                var envelope = 0.5 + (0.5 * Math.Sin(2.0 * Math.PI * 0.7 * t));
                var value = (amplitude * envelope * Math.Sin(2.0 * Math.PI * (220.0 * (channel + 1)) * t)) +
                            (0.05 * (random.NextDouble() - 0.5));
                samples[(i * 2) + channel] = (short)Math.Round(Math.Clamp(value, -1, 1) * 32767);
            }
        }

        var path = Path.Combine(_dir, name);
        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, 2, rate));
        return path;
    }

    [Fact]
    public void AWavFile_TargetsTwoLufsHigherThanDlcBuilderByDefault()
    {
        // RocksmithAutoVolumeTests: amplitude 0.3 over 5 s is loudness -14.5, volume -1.5 in DLC Builder.
        var path = WriteSignal("song.wav", 0.3, seconds: 5);

        Assert.Equal(0.5, RocksmithAutoVolume.Calculate(path));
    }

    [Fact]
    public void TheVolumeIsMeasuredFromTheSongsOwnAudio_NotFromAGlobalDefault()
    {
        var loud = new ChartData();
        var quiet = new ChartData();

        ProjectVolumes.Measure(loud, WriteSignal("loud.wav", 0.5), null, null);
        ProjectVolumes.Measure(quiet, WriteSignal("quiet.wav", 0.02), null, null);

        Assert.NotNull(loud.AudioVolume);
        Assert.NotNull(quiet.AudioVolume);
        Assert.True(quiet.AudioVolume > loud.AudioVolume + 10, $"quiet {quiet.AudioVolume} dB vs loud {loud.AudioVolume} dB");
        Assert.NotEqual(ProjectVolumes.DefaultAudioVolume, loud.AudioVolume);
        Assert.NotNull(loud.PreviewVolume);
        Assert.NotEqual(loud.AudioVolumeSignature, quiet.AudioVolumeSignature);
    }

    [Fact]
    public void AMeasuredSong_IsNotMeasuredAgain_UntilItsAudioChanges()
    {
        var chart = new ChartData();
        var path = WriteSignal("song.wav", 0.3);
        Assert.True(ProjectVolumes.Measure(chart, path, null, null));
        var first = chart.AudioVolume;

        Assert.False(ProjectVolumes.Measure(chart, path, null, null));

        WriteSignal("song.wav", 0.05);
        Assert.True(ProjectVolumes.Measure(chart, path, null, null));
        Assert.NotEqual(first, chart.AudioVolume);
    }

    [Fact]
    public void ATypedVolume_IsNeverMeasuredOver()
    {
        var chart = new ChartData { AudioVolume = -3.5, AudioVolumeManual = true, PreviewVolume = -2.0, PreviewVolumeManual = true };

        var changed = ProjectVolumes.Measure(chart, WriteSignal("song.wav", 0.3), null, null);

        Assert.False(changed);
        Assert.Equal(-3.5, chart.AudioVolume);
        Assert.Equal(-2.0, chart.PreviewVolume);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANewTargetRefreshesOnlyTheAutomaticVolume(bool manualAudio)
    {
        var path = WriteSignal("target.wav", 0.3, seconds: 5);
        var chart = new ChartData();
        Assert.True(ProjectVolumes.Measure(chart, path, null, null, targetLufs: -20));
        if (manualAudio)
        {
            chart.AudioVolume = -3.5;
            chart.AudioVolumeManual = true;
        }
        else
        {
            chart.PreviewVolume = -2.0;
            chart.PreviewVolumeManual = true;
        }
        var before = ProjectVolumeFields.From(chart);

        Assert.True(ProjectVolumes.Measure(chart, path, null, null, targetLufs: -10));
        Assert.False(ProjectVolumes.Measure(chart, path, null, null, targetLufs: -10));
        Assert.Equal(manualAudio ? before.Audio : 4.5, chart.AudioVolume);
        Assert.Equal(manualAudio ? before.Preview + 10 : before.Preview, chart.PreviewVolume);
        Assert.Equal(manualAudio ? before.AudioSignature : before.PreviewSignature,
            manualAudio ? chart.AudioVolumeSignature : chart.PreviewVolumeSignature);
    }

    [Fact]
    public void ACommittedAutoChoiceIsRefreshableEvenWhenItReplacesAManualGain()
    {
        var path = WriteSignal("auto-choice.wav", 0.3, seconds: 5);
        var chart = new ChartData { AudioVolume = 0.5, AudioVolumeManual = true, PreviewVolume = -2, PreviewVolumeManual = true };
        var history = new HistoryService(new FakeAppLogger());

        RocksmithExportSupport.CommitVolumes(history, chart, 0.5, -2, autoAudio: true);
        Assert.False(chart.AudioVolumeManual);
        Assert.True(ProjectVolumes.Measure(chart, path, null, null, targetLufs: -10));
        Assert.Equal(4.5, chart.AudioVolume);
        Assert.Equal(-2, chart.PreviewVolume);
        Assert.True(chart.PreviewVolumeManual);
    }

    [Fact]
    public void ThePreviewIsTheClipThatShips_OrThePreviewFileWhenThereIsOne()
    {
        var chart = new ChartData();
        var audio = WriteSignal("song.wav", 0.3);
        var previewFile = WriteSignal("preview.wav", 0.03, seconds: 20);

        ProjectVolumes.Measure(chart, audio, null, 1000);
        var clip = chart.PreviewVolume;
        Assert.Contains("@1000", chart.PreviewVolumeSignature, StringComparison.Ordinal);

        ProjectVolumes.Measure(chart, audio, previewFile, null);

        Assert.Equal(RocksmithAutoVolume.Calculate(previewFile), chart.PreviewVolume);
        Assert.NotEqual(clip, chart.PreviewVolume);
    }

    [Fact]
    public void AnUnreadableFile_LeavesTheVolumesUnsetAndSaysWhy()
    {
        var chart = new ChartData();
        var bad = Path.Combine(_dir, "broken.wav");
        File.WriteAllBytes(bad, [1, 2, 3, 4]);
        var warnings = new List<string>();

        var changed = ProjectVolumes.Measure(chart, bad, null, null, warnings.Add);

        Assert.False(changed);
        Assert.Null(chart.AudioVolume);
        Assert.Single(warnings);
        Assert.Equal(ProjectVolumes.DefaultAudioVolume, ProjectVolumes.AudioOrDefault(chart));
    }

    [Fact]
    public void Old_automatic_signatures_are_remeasured_but_manual_overrides_survive()
    {
        var path = WriteSignal("old.wav", 0.3, seconds: 2);
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        var legacy = $"{stream.Length}:{Convert.ToHexStringLower(hash)[..16]}";
        var chart = new ChartData
        {
            AudioVolume = 99, PreviewVolume = 98,
            AudioVolumeSignature = legacy, PreviewVolumeSignature = legacy
        };

        Assert.True(ProjectVolumes.Measure(chart, path, path, null));
        Assert.Equal(RocksmithAutoVolume.Calculate(path), chart.AudioVolume);
        Assert.Equal(chart.AudioVolume, chart.PreviewVolume);
        Assert.NotEqual(legacy, chart.AudioVolumeSignature);
        Assert.NotEqual(legacy, chart.PreviewVolumeSignature);
        Assert.False(ProjectVolumes.Measure(chart, path, path, null));

        chart.AudioVolume = 99;
        chart.PreviewVolume = 98;
        chart.AudioVolumeManual = true;
        chart.PreviewVolumeManual = true;
        chart.AudioVolumeSignature = legacy;
        chart.PreviewVolumeSignature = legacy;
        Assert.False(ProjectVolumes.Measure(chart, path, path, null));
        Assert.Equal(99, chart.AudioVolume);
        Assert.Equal(98, chart.PreviewVolume);
        Assert.Equal(legacy, chart.AudioVolumeSignature);
        Assert.Equal(legacy, chart.PreviewVolumeSignature);
    }

    [Fact]
    public void An_old_automatic_clip_signature_is_remeasured()
    {
        var path = WriteSignal("old-clip.wav", 0.3, seconds: 2);
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        var legacy = $"{stream.Length}:{Convert.ToHexStringLower(hash)[..16]}@0";
        var chart = new ChartData
        {
            AudioVolume = 99, AudioVolumeManual = true, PreviewVolume = 98,
            PreviewVolumeSignature = legacy
        };

        Assert.True(ProjectVolumes.Measure(chart, path, null, 0));
        Assert.Equal(99, chart.AudioVolume);
        Assert.NotEqual(98, chart.PreviewVolume);
        Assert.NotEqual(legacy, chart.PreviewVolumeSignature);
        Assert.False(ProjectVolumes.Measure(chart, path, null, 0));
    }

    [Fact]
    public async Task Refreshing_an_existing_automatic_measurement_does_not_add_undo_history()
    {
        var path = WriteSignal("legacy-export.wav", 0.3, seconds: 2);
        var chart = new ChartData
        {
            AudioVolume = 31, AudioVolumeSignature = "legacy-audio",
            PreviewVolume = -2, PreviewVolumeSignature = "manual-preview", PreviewVolumeManual = true
        };
        var history = new HistoryService(new FakeAppLogger());
        using var dirty = new ProjectDirtyTracker(history);
        var measurement = RocksmithExportSupport.MeasureProjectVolumesAsync(chart, path, "", new FakeAppLogger());
        await BoundedWait.ForAsync(measurement, "the export volume refresh", TestContext.Current.CancellationToken);

        RocksmithExportSupport.CommitMeasuredVolumes(history, chart, await measurement);

        Assert.Equal(RocksmithAutoVolume.Calculate(path), chart.AudioVolume);
        Assert.Equal(ProjectVolumes.VolumeSignature(ProjectVolumes.Signature(path), RocksmithAutoVolume.DefaultTargetLufs), chart.AudioVolumeSignature);
        Assert.Equal(-2, chart.PreviewVolume);
        Assert.Equal("manual-preview", chart.PreviewVolumeSignature);
        Assert.True(chart.PreviewVolumeManual);
        Assert.Empty(history.GetUndoSteps());
        Assert.False(dirty.IsDirty);
    }

    [Fact]
    public void TheSignature_ChangesWithTheContent()
    {
        var path = WriteSignal("song.wav", 0.3, seconds: 1);
        var before = ProjectVolumes.Signature(path);

        WriteSignal("song.wav", 0.31, seconds: 1);

        Assert.NotEqual(before, ProjectVolumes.Signature(path));
        Assert.Equal(ProjectVolumes.Signature(path), ProjectVolumes.Signature(path));
    }
}
