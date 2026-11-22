using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tests.Services.Export.Rs7;
using DotCharter.Tests.Services.Export.Rs7.Suite;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrComparisonPackagesTests
{
    [Fact]
    public async Task Write_the_comparison_packages()
    {
        var output = Environment.GetEnvironmentVariable("DOTCHARTER_RSXR_COMPARE_OUT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(output), "Opt-in: set DOTCHARTER_RSXR_COMPARE_OUT.");
        var work = Directory.CreateTempSubdirectory("dotcharter-rsxr-audio-").FullName;
        try
        {
            foreach (var spec in Rs7TestSuiteCatalog.All)
            {
                var built = Rs7TestSuiteBuilder.Build(spec);
                var chartWork = Path.Combine(work, spec.Name);
                Directory.CreateDirectory(chartWork);
                var audio = Path.Combine(chartWork, "audio.wav");
                Rs7TestAudio.WriteClickWav(audio, built.SongLengthMs + Rs7TestAudio.OutroMs);
                var folder = Path.Combine(output, "rsxr");
                Directory.CreateDirectory(folder);
                var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio,
                    Path.Combine(chartWork, "Rsxr_p.psarc"), dlcKey: Rs7TestSuiteExpected.DlcKey(spec));
                Assert.DoesNotContain(result.Warnings, w => w.StartsWith("Arrangement '", StringComparison.Ordinal) &&
                    w.Contains("' was skipped: ", StringComparison.Ordinal));
                Assert.Equal(spec.Parts.Length, result.ArrangementNames.Count);
                File.Copy(result.PsarcPath, ExtendedRangePackageName.Apply(Path.Combine(folder, spec.Name + "_p.psarc"), result.IsPsxr), overwrite: true);
            }
        }
        finally
        {
            BestEffortDelete.Directory(work);
        }
    }
}
