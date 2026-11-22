using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #309: the rs7string test packages carry real audio at least as long as the chart (the game ends the song
///     when its audio ends), and the export validator warns when a project's audio is shorter than its chart.
/// </summary>
public sealed class Rs7AudioLengthTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rs7audio-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void The_click_wav_is_as_long_as_asked_and_not_all_silence()
    {
        var path = Path.Combine(_dir, "clicks.wav");

        Rs7TestAudio.WriteClickWav(path, 12_500);

        var warnings = new List<string>();
        var pcm = PcmAudioLoader.Load(path, warnings);
        Assert.InRange(pcm.Duration.TotalMilliseconds, 12_500, 12_520);
        Assert.Contains(pcm.Samples, s => s != 0);
        Assert.Contains(pcm.Samples, s => s == 0);
    }

    [Fact]
    public async Task A_package_built_the_way_the_generators_do_has_audio_at_least_as_long_as_the_chart()
    {
        var project = Rs7PackageExportTests.CreateProject(8);
        var chartMs = project.Beats[^1].Time + Rs7TestSupport.BeatMs;
        var work = Path.Combine(_dir, "work");

        var result = await Rs7TestAudio.BuildPackageAsync(project, Path.Combine(_dir, "rs7audio_p.psarc"), "rs7audio",
            chartMs, work, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.PsarcPath));
        var audioMs = Rs7TestAudio.WemDurationMs(Rs7TestAudio.WorkWem(work));
        Assert.True(audioMs >= chartMs, $"audio {audioMs:0} ms is shorter than the chart {chartMs:0} ms");
        // The old 4 KB placeholder could not hold even a second of audio.
        Assert.True(new FileInfo(Rs7TestAudio.WorkWem(work)).Length > 4_000);
    }

    [Fact]
    public void Audio_shorter_than_the_chart_is_a_warning_naming_both_lengths()
    {
        var project = Rs7PackageExportTests.CreateProject(7);
        var chartEndMs = project.Arrangements[0].Levels.SelectMany(l => l.Notes).Max(n => n.EndPosition.GetPositionMs(project.Beats));

        var findings = new ChartExportValidator().Validate(project, audioLengthMs: 5_000)
            .Where(f => f.MessageKey == ChartExportValidator.KeyAudioShorterThanChart).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(ExportFindingSeverity.Warning, finding.Severity);
        Assert.StartsWith("0:05 / ", finding.Detail, StringComparison.Ordinal);
        Assert.True(chartEndMs > 5_000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(600_000.0)]
    public void Audio_that_is_unknown_or_long_enough_gets_no_warning(double? audioLengthMs)
    {
        var project = Rs7PackageExportTests.CreateProject(7);

        var findings = new ChartExportValidator().Validate(project, audioLengthMs);

        Assert.DoesNotContain(findings, f => f.MessageKey == ChartExportValidator.KeyAudioShorterThanChart);
    }
}
