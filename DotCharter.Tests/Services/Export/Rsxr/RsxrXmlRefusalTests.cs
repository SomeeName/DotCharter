using System.Xml.Linq;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrXmlRefusalTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-xml-refusal-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Six_string_XML_projection_bypasses_extended_tuning_refusal(bool projection, bool bundle)
    {
        var built = RsxrTestSupport.Chart(false, 7);
        var arrangement = built.Project.Arrangements[0];
        arrangement.TuningPitches[0] = new TuningOffset(25);
        var sentence = RsxrStringLayout.ExportRefusal(arrangement)!;
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig
            {
                RsXmlSixStringProjection = projection,
                ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false
            }
        };
        using var provider = new ServiceCollection().AddSingleton<IConfigurationService>(config)
            .AddKeyedTransient<IChartSerializer, XmlChartSerializer>("xml").BuildServiceProvider();
        var logger = new FakeAppLogger();
        var documents = new ProjectDocumentService(provider, logger);
        var path = Path.Combine(_directory, "song.xml");
        if (!projection && !bundle)
        {
            Assert.Equal(sentence, Assert.Throws<InvalidDataException>(() => documents.SaveChart(path, built.Project)).Message);
            Assert.False(File.Exists(path));
            return;
        }
        if (bundle)
        {
            var projectPath = Path.Combine(_directory, "song.dcproj");
            documents.SaveDlcBuilderXmlBundle(projectPath, built.Project);
            var paths = Directory.GetFiles(DlcBuilderBundle.DirectoryFor(projectPath), "*.xml");
            if (!projection)
            {
                Assert.Empty(paths);
                Assert.True(logger.HasEntryContaining("WARN", sentence));
                return;
            }
            path = Assert.Single(paths);
        }
        else
        {
            documents.SaveChart(path, built.Project);
        }

        var tuning = XDocument.Load(path).Root!.Element("tuning")!;
        Assert.Null(tuning.Attribute("extendedRange"));
        Assert.Null(tuning.Attribute("string6"));
        var restored = Assert.Single(documents.LoadChart(path).Arrangements);
        Assert.Equal(6, restored.StringCount);
        Assert.NotEmpty(restored.Levels.SelectMany(l => l.Notes));
        Assert.All(restored.Levels.SelectMany(l => l.Notes), n => Assert.InRange(n.StringIndex, 0, 5));
        Assert.DoesNotContain(logger.Snapshot(), e => e.Level == "WARN" && e.Message.Contains(sentence, StringComparison.Ordinal));
    }

    [Fact]
    public void Refused_arrangements_are_absent_from_the_DLC_Builder_project()
    {
        var project = RsxrTestSupport.Chart(false, 8).Project;
        project.Arrangements[0].StringCount = 9;
        project.Arrangements.Add(RsxrTestSupport.Chart(false, 6).Project.Arrangements[0]);
        var dto = Rs2DlcProjectBuilder.Build(project, new Rs2DlcProjectBuilder.Rs2DlcBuildOptions("", "", null, "", SixStringProjection: false));
        Assert.Single(dto.Arrangements);
    }

    [Theory]
    [InlineData(false, 9, false)]
    [InlineData(true, 7, false)]
    [InlineData(false, 7, true)]
    [InlineData(false, 8, true)]
    [InlineData(true, 5, true)]
    [InlineData(true, 6, true)]
    public void Refused_XML_is_left_out_and_its_sentence_reaches_the_warning_channel(bool bass, int strings, bool tuning)
    {
        var built = RsxrTestSupport.Chart(bass, bass ? 6 : 7);
        var arrangement = built.Project.Arrangements[0];
        arrangement.StringCount = strings;
        if (tuning) arrangement.TuningPitches[0] = new TuningOffset(25);
        var sentence = RsxrStringLayout.ExportRefusal(arrangement)!;
        var warnings = new List<string>();
        var serializer = new XmlChartSerializer { Warning = warnings.Add };
        var path = Path.Combine(_directory, "refused.xml");
        serializer.Save(path, built.Project, built.SongLengthMs);
        Assert.Contains(sentence, warnings);
        Assert.False(File.Exists(path));
        built.Project.Arrangements.Add(RsxrTestSupport.Chart(false, 6).Project.Arrangements[0]);
        var dto = Rs2DlcProjectBuilder.Build(built.Project, new Rs2DlcProjectBuilder.Rs2DlcBuildOptions("", "", null, "", SixStringProjection: false));
        Assert.Single(dto.Arrangements);
    }
}
