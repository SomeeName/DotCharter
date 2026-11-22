using System.Xml.Linq;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tests.Services.Export.Rs7;
using DotCharter.Tests.Services.Export.Rsxr;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Export;

public sealed class RsxrSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("extended-range-save-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void Main_document_refusal_does_not_make_companion_XML_throw()
    {
        var project = Project();
        project.Arrangements[0].TuningPitches[0] = new TuningOffset(25);
        var sentence = RsxrStringLayout.ExportRefusal(project.Arrangements[0])!;
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig { ExportGenerateDynamicDifficulty = false, ExportApplyDlcBuilderFixes = false }
        };
        using var provider = new ServiceCollection().AddSingleton<IConfigurationService>(config)
            .AddKeyedSingleton<IChartSerializer, XmlChartSerializer>("xml").BuildServiceProvider();
        var logger = new FakeAppLogger();
        var documents = new ProjectDocumentService(provider, logger);
        var main = Path.Combine(_dir, "main.xml");
        Assert.Equal(sentence, Assert.Throws<InvalidDataException>(() => documents.SaveChart(main, project)).Message);

        var warnings = new List<string>();
        var serializer = (XmlChartSerializer)provider.GetRequiredKeyedService<IChartSerializer>("xml");
        serializer.Warning = warnings.Add;
        var companion = Path.Combine(_dir, "companion.xml");
        serializer.Save(companion, project);
        Assert.False(File.Exists(companion));
        Assert.Contains(sentence, warnings);
        project.Arrangements.Add(RsxrTestSupport.Chart(false, 6).Project.Arrangements[0]);
        var path = Path.Combine(_dir, "song.dcproj");
        documents.SaveDlcBuilderXmlBundle(path, project);
        var xml = Assert.Single(Directory.GetFiles(DlcBuilderBundle.DirectoryFor(path), "*.xml"));
        Assert.Null(XDocument.Load(xml).Root!.Element("tuning")!.Attribute("extendedRange"));
        var dto = Rs2DlcProjectBuilder.Build(project,
            new Rs2DlcProjectBuilder.Rs2DlcBuildOptions("", "", null, "", SixStringProjection: false));
        Assert.Single(dto.Arrangements);
        Assert.True(logger.HasEntryContaining("WARN", sentence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saved_xml_and_bundle_use_positional_strings(bool bundle)
    {
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig
            {
                ExportGenerateDynamicDifficulty = false, ExportApplyDlcBuilderFixes = false
            }
        };
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationService>(config);
        services.AddKeyedTransient<IChartSerializer, XmlChartSerializer>("xml");
        using var provider = services.BuildServiceProvider();
        var service = new ProjectDocumentService(provider, new FakeAppLogger());
        var path = Path.Combine(_dir, "song.xml");
        if (bundle)
        {
            service.SaveDlcBuilderXmlBundle(Path.Combine(_dir, "song.dcproj"), Project());
            path = Directory.GetFiles(DlcBuilderBundle.DirectoryFor(Path.Combine(_dir, "song.dcproj")), "*.xml").Single();
        }
        else
        {
            service.SaveChart(path, Project());
        }

        AssertXml(path);
    }

    [Fact]
    public void Serializer_writes_positional_strings()
    {
        var path = Path.Combine(_dir, "song.xml");
        new XmlChartSerializer().Save(path, Project());
        AssertXml(path);
    }

    private static void AssertXml(string path)
    {
        var song = XDocument.Load(path).Root!;
        Assert.Equal("1", (string?)song.Element("tuning")!.Attribute("extendedRange"));
        Assert.Equal(0, (int)song.Descendants("note").Single().Attribute("string")!);
    }

    private static ChartProject Project()
    {
        var project = new ChartProject { Beats = [.. Rs7TestSupport.Beats(40)] };
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 7 };
        for (var i = 0; i < 7; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        arrangement.Levels[0].Notes.Add(new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(1500, project.Beats), StringIndex = 0, Fret = 3
        });
        project.Arrangements.Add(arrangement);
        return project;
    }
}
