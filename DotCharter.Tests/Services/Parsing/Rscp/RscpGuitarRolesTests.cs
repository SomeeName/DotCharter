using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     <c>ChartProject.GuitarRolesSplit</c> (#260) survives a save: written as the
///     additive <c>&lt;stems guitarRoles="true"&gt;</c>, ABSENT when off — absent and
///     false mean the same thing, so a project that never used it stays
///     byte-identical (docs/RSCP-FORMAT-GUARANTEES.md).
/// </summary>
public sealed class RscpGuitarRolesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_guitarroles_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.rscp");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void TheFlag_RoundTrips()
    {
        var project = NewProject();
        project.GuitarRolesSplit = true;

        new RscpChartSerializer().Save(ProjectPath, project);
        var loaded = new RscpChartSerializer().Load(ProjectPath);

        Assert.Equal("true", XDocument.Load(ProjectPath).Root!.Element("stems")!.Attribute("guitarRoles")?.Value);
        Assert.True(loaded.GuitarRolesSplit);
    }

    [Fact]
    public void Off_WritesNothing_AndReadsBackOff()
    {
        new RscpChartSerializer().Save(ProjectPath, NewProject());
        var loaded = new RscpChartSerializer().Load(ProjectPath);

        Assert.Null(XDocument.Load(ProjectPath).Root!.Element("stems")!.Attribute("guitarRoles"));
        Assert.False(loaded.GuitarRolesSplit);
    }

    private static ChartProject NewProject()
    {
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        project.Stems.Add(new Stem { Name = "guitar", Path = "stems/guitar.wav" });
        return project;
    }
}
