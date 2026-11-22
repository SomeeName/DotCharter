using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     #822: the song key the charter LOCKED as an override (<c>ChartData.KeyLocked</c>)
///     survives save and reopen, C major included, and a project from before the field
///     opens unlocked without the writer adding markup to an unlocked chart.
/// </summary>
public sealed class RscpSongKeyLockRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_keylock_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.dcproj");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Theory]
    [InlineData(-5, false)]
    [InlineData(0, false)] // a locked C major is a real key, not "never set"
    [InlineData(0, true)]
    public void ALockedKey_RoundTrips(int accidentals, bool minor)
    {
        var project = NewProject();
        project.Chart.KeyAccidentals = accidentals;
        project.Chart.KeyIsMinor = minor;
        project.Chart.KeyLocked = true;

        new RscpChartSerializer().Save(ProjectPath, project);
        var loaded = new RscpChartSerializer().Load(ProjectPath).Chart;

        Assert.True(loaded.KeyLocked);
        Assert.Equal(accidentals, loaded.KeyAccidentals);
        Assert.Equal(minor, loaded.KeyIsMinor);
    }

    [Fact]
    public void AnUnlockedChart_WritesNoLockElement_AndAnOlderFileOpensUnlocked()
    {
        var project = NewProject();
        project.Chart.KeyAccidentals = 2;

        new RscpChartSerializer().Save(ProjectPath, project);
        var root = XDocument.Load(ProjectPath).Root!;

        Assert.Null(root.Element("dcKeyLocked"));
        var loaded = new RscpChartSerializer().Load(ProjectPath).Chart;
        Assert.False(loaded.KeyLocked);
        Assert.Equal(2, loaded.KeyAccidentals);
    }

    private static ChartProject NewProject()
    {
        var project = new ChartProject();
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arrangement);
        return project;
    }
}
