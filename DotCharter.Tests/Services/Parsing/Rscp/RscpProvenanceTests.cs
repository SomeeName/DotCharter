using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tests.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     #891: the play-only flag in a <c>.rscp</c> (#822) under its neutral spelling. New files write
///     <c>playOnly</c>; a file an earlier build wrote with <c>officialRocksmith</c> must still load
///     play-only, or an old project would turn into one that exports freely.
/// </summary>
public sealed class RscpProvenanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_provenance_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SavePlayOnly(string name)
    {
        var project = EofExportTestCharts.BuildRepresentativeProject();
        project.Provenance = ChartProvenance.PlayOnly;
        project.Arrangements[1].Provenance = ChartProvenance.PlayOnly;
        var path = Path.Combine(_dir, name);
        new RscpChartSerializer().Save(path, project);
        return path;
    }

    [Fact]
    public void Play_only_is_written_under_the_neutral_spelling_and_round_trips()
    {
        var path = SavePlayOnly("new.rscp");
        var text = File.ReadAllText(path);

        Assert.Contains(">" + RscpProvenance.PlayOnly + "<", text);
        Assert.DoesNotContain(RscpProvenance.LegacyPlayOnly, text);
        var loaded = new RscpChartSerializer().Load(path);
        Assert.Equal(ChartProvenance.PlayOnly, loaded.Provenance);
        Assert.Equal(ChartProvenance.PlayOnly, loaded.Arrangements[1].Provenance);
        Assert.Equal(ChartProvenance.Unrestricted, loaded.Arrangements[0].Provenance);
    }

    [Fact]
    public void A_file_written_with_the_old_spelling_still_loads_play_only()
    {
        var path = SavePlayOnly("old.rscp");
        var text = File.ReadAllText(path).Replace(">" + RscpProvenance.PlayOnly + "<", ">" + RscpProvenance.LegacyPlayOnly + "<");
        Assert.Contains(RscpProvenance.LegacyPlayOnly, text);
        File.WriteAllText(path, text);

        var loaded = new RscpChartSerializer().Load(path);

        Assert.Equal(ChartProvenance.PlayOnly, loaded.Provenance);
        Assert.Equal(ChartProvenance.PlayOnly, loaded.Arrangements[1].Provenance);
    }

    [Fact]
    public void The_old_spelling_parses_as_play_only_and_a_missing_value_as_unrestricted()
    {
        Assert.Equal(ChartProvenance.PlayOnly, RscpProvenance.Parse(RscpProvenance.LegacyPlayOnly));
        Assert.Equal(ChartProvenance.PlayOnly, RscpProvenance.Parse("someFutureLicence"));
        Assert.Equal(ChartProvenance.Unrestricted, RscpProvenance.Parse(null));
        Assert.Equal(ChartProvenance.Unrestricted, RscpProvenance.Parse(" "));
        Assert.Equal(RscpProvenance.PlayOnly, RscpProvenance.Format(ChartProvenance.PlayOnly));
    }
}
