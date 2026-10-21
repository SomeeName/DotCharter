using System.Xml.Linq;
using DotCharter.Services.Export.DlcBuilder;
using DotCharter.Services.Infrastructure.Documents;
using DotCharter.Services.Parsing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tests.Services.Parsing.MoverPhrases;
using DotCharter.Tone.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     "RS XML phrases between beats as moveR" reaches both XML outputs: the saved
///     <c>.xml</c> and the DTRS XML bundle DLC Builder builds from. Off by default.
/// </summary>
public sealed class RsXmlMoverPhrasesOptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", "mover-option-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAppLogger _logger = new();

    public RsXmlMoverPhrasesOptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void IsOffByDefault() => Assert.False(new AppConfig().RsXmlMoverPhrases);

    [Theory]
    [InlineData(true, "2.000", "2.250")]
    [InlineData(false, "2.250", "2.000")]
    public void SaveAsXml_FollowsTheOption(bool mover, string expectedTime, string notTime)
    {
        var path = Path.Combine(_dir, "song.xml");

        Service(new AppConfig { RsXmlMoverPhrases = mover }).SaveChart(path, MoverPhraseRoundTripTests.Project());

        var times = IterationTimes(XDocument.Load(path));
        Assert.Contains(expectedTime, times);
        Assert.DoesNotContain(notTime, times);
    }

    [Theory]
    [InlineData(true, "2.000", "2.250")]
    [InlineData(false, "2.250", "2.000")]
    public void TheDtrsXmlBundle_FollowsTheOption(bool mover, string expectedTime, string notTime)
    {
        var config = new AppConfig
        {
            RsXmlMoverPhrases = mover, ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false
        };

        Service(config).SaveDlcBuilderXmlBundle(Path.Combine(_dir, "project.rscp"), MoverPhraseRoundTripTests.Project());

        var file = Directory.GetFiles(Path.Combine(_dir, DlcBuilderBundle.DirectoryName), "*_Lead_*.xml").Single();
        var times = IterationTimes(XDocument.Load(file));
        Assert.Contains(expectedTime, times);
        Assert.DoesNotContain(notTime, times);
    }

    /// <summary>Every iteration time: the chorus is at 2250 ms, or as moveR2 on beat 4 (2000 ms).</summary>
    private static List<string> IterationTimes(XDocument doc) =>
        [.. doc.Root!.Element("phraseIterations")!.Elements().Select(pi => pi.Attribute("time")!.Value)];

    private ProjectDocumentService Service(AppConfig config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppLogger>(_logger);
        services.AddKeyedTransient<IChartSerializer, RscpChartSerializer>("rscp");
        services.AddKeyedTransient<IChartSerializer, XmlChartSerializer>("xml");
        services.AddSingleton<IConfigurationService>(new FakeConfigurationService { AppConfig = config });
        return new ProjectDocumentService(services.BuildServiceProvider(), _logger);
    }
}
