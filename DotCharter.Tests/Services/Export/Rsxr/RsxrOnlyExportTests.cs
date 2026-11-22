using DotCharter.Services.Export;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Export.Validation;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrOnlyExportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-rsxr-only-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Extended_content_always_writes_a_tagged_plain_RS_Extended_Range_package(bool? plain)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var built = RsxrTestSupport.Chart(true, 5);
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);
        var result = await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(built.Project,
            new RocksmithBuildOptions
            {
                OutputPath = Path.Combine(_directory, "only_p.psarc"), AudioPath = audio,
                SongLengthMs = built.SongLengthMs, IncludeVocals = false, PlainPackage = plain,
                Identity = RsxrTestSupport.Identity(1)
            }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsPsxr);
        Assert.Equal("only_rsxr_p.psarc", Path.GetFileName(result.PsarcPath));
        Assert.Equal(RsxrTestSupport.PackageMagic(true), File.ReadAllBytes(result.PsarcPath)[..4]);
        Assert.NotEmpty(RsxrTestSupport.Entries(result.PsarcPath));
    }

    [Fact]
    public void Stock_writer_refuses_an_extended_model()
    {
        var model = new RocksmithSng { Extension = RsExtensionBlock.ForStrings(7) };
        Assert.Throws<InvalidDataException>(() => RocksmithSngWriter.WriteUnpacked(model));
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => RocksmithSngWriter.WritePlainPc(model, output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void An_unrepresentable_extended_tuning_warns_that_the_arrangement_is_skipped()
    {
        var project = RsxrTestSupport.Chart(false, 7).Project;
        project.Arrangements[0].TuningPitches[0] = new TuningOffset(25);
        Assert.Contains(new ChartExportValidator().Validate(project), finding =>
            finding.Severity == ExportFindingSeverity.Warning && finding.MessageKey == "Export.Validation.ExtendedRangeTuning");
    }

}
