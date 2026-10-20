using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     v0.29 section-status tracker: the `ss` rscp attribute must be
///     backward-tolerant BOTH ways — a pre-v0.29 file (no attribute) reads
///     None, an unknown/garbage value reads None (never a load failure), and
///     an untracked chart writes NO attribute so existing files stay
///     byte-identical (nd/gf precedent). Old apps ignore unknown attributes
///     by construction; the extra-attribute test pins that shape.
/// </summary>
public sealed class RscpSectionStatusTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_ss_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.rscp");

    private static ChartProject ProjectWithSection(SectionStatus status)
    {
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.EventPoints.Add(new EventPoint
        {
            Section = "verse", // Position defaults to zero — irrelevant here
            Status = status
        });
        project.Arrangements.Add(arrangement);
        return project;
    }

    [Fact]
    public void Status_RoundTripsThroughSaveAndLoad()
    {
        new RscpChartSerializer().Save(ProjectPath, ProjectWithSection(SectionStatus.Review));

        var loaded = new RscpChartSerializer().Load(ProjectPath);

        var ep = loaded.Arrangements[0].EventPoints.Single(e => e.Section == "verse");
        Assert.Equal(SectionStatus.Review, ep.Status);
    }

    [Fact]
    public void None_WritesNoAttribute_UntrackedChartsStayByteIdentical()
    {
        new RscpChartSerializer().Save(ProjectPath, ProjectWithSection(SectionStatus.None));

        var epNode = XDocument.Load(ProjectPath).Descendants("eventPoint").Single();
        Assert.Null(epNode.Attribute("ss"));
    }

    [Fact]
    public void TheAttribute_IsTheLowercaseName_NotTheOrdinal()
    {
        // Names survive enum growth; ordinals would silently re-map statuses.
        new RscpChartSerializer().Save(ProjectPath, ProjectWithSection(SectionStatus.Done));

        var epNode = XDocument.Load(ProjectPath).Descendants("eventPoint").Single();
        Assert.Equal("done", epNode.Attribute("ss")!.Value);
    }

    [Theory]
    [InlineData(null, SectionStatus.None)]       // pre-v0.29 file
    [InlineData("", SectionStatus.None)]
    [InlineData("todo", SectionStatus.Todo)]
    [InlineData("REVIEW", SectionStatus.Review)] // case-insensitive
    [InlineData("shipped", SectionStatus.None)]  // a status this build doesn't know
    [InlineData("7", SectionStatus.None)]        // numeric strings must not map to undefined values
    public void Parse_IsTolerant(string? value, SectionStatus expected)
        => Assert.Equal(expected, RscpParsingUtilities.ParseSectionStatus(value));

    [Fact]
    public void AFileWithUnknownExtraAttributes_StillLoads()
    {
        // The shape that makes old-app-reads-new-file safe: attribute-based
        // parsing ignores extras. Pin it so a rewrite to strict parsing can't
        // sneak in.
        new RscpChartSerializer().Save(ProjectPath, ProjectWithSection(SectionStatus.Todo));
        var doc = XDocument.Load(ProjectPath);
        doc.Descendants("eventPoint").Single().Add(new XAttribute("futureAttr", "whatever"));
        doc.Save(ProjectPath);

        var loaded = new RscpChartSerializer().Load(ProjectPath);

        Assert.Equal(SectionStatus.Todo,
            loaded.Arrangements[0].EventPoints.Single(e => e.Section == "verse").Status);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
