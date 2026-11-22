using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Services.State.Checkpoints;
using DotCharter.Tests.Services.Export.Rsxr;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Session;

public sealed partial class ProjectSessionSaveOutcomeTests
{
    [Theory]
    [InlineData(9, 0)]
    [InlineData(7, 25)]
    public async Task Refused_main_XML_fails_with_its_sentence_and_preserves_the_document(int strings, int offset)
    {
        var project = RsxrTestSupport.Chart(false, 7).Project;
        var path = Path.Combine(_tempDir, "song.xml");
        new XmlChartSerializer().Save(path, project);
        var original = File.ReadAllBytes(path);
        var arrangement = project.Arrangements[0];
        arrangement.StringCount = strings;
        arrangement.TuningPitches[0] = new TuningOffset(offset);
        var sentence = RsxrStringLayout.ExportRefusal(arrangement)!;
        using var provider = new ServiceCollection()
            .AddKeyedTransient<IChartSerializer, XmlChartSerializer>("xml").BuildServiceProvider();
        var documents = new ProjectDocumentService(provider, new FakeAppLogger());
        var h = new Harness(project, path, new NoOpSaveCompanionService(), documents, null,
            new CheckpointService(Path.Combine(_tempDir, "checkpoints")));
        using var session = h.Session;

        Assert.False(await session.Saver.SaveProjectCoreAsync(path));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.saving", SearchOption.AllDirectories));
        Assert.Contains(sentence, h.State.MainDisplayMessage, StringComparison.Ordinal);
        var failure = Assert.Single(h.Logger.Snapshot(), e => e.Level == "ERROR" && e.Exception != null);
        Assert.Equal(sentence, Assert.IsType<InvalidDataException>(failure.Exception).Message);
    }
}
