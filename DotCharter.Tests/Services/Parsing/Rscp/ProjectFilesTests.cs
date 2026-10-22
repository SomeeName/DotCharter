using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     The one spelling of the two project extensions: <c>.dcproj</c> is DotCharter's project,
///     <c>.rscp</c> is Charter's (and older DotCharter builds'). A <c>.rscp</c> beside its
///     same-named <c>.dcproj</c> is that project's Charter companion and never stands in for it.
/// </summary>
public sealed class ProjectFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_projectfiles_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void TheProjectExtension_IsDcproj_AndANewProjectIsNamedForIt()
    {
        Assert.Equal(".dcproj", ProjectFiles.Extension);
        Assert.Equal("project.dcproj", ProjectFiles.DefaultFileName);
        Assert.Equal(Path.Combine(_dir, "project.dcproj"), ProjectFiles.DefaultPath(_dir));
        Assert.Equal(["*.dcproj", "*.rscp"], ProjectFiles.OpenablePatterns);
    }

    [Theory]
    [InlineData("a.dcproj", true, true, false)]
    [InlineData("A.DCPROJ", true, true, false)]
    [InlineData("a.rscp", false, true, true)]
    [InlineData("a.rscp.bak", false, false, false)]
    [InlineData("a.xml", false, false, false)]
    [InlineData("", false, false, false)]
    public void Classification(string path, bool project, bool openable, bool charter)
    {
        Assert.Equal(project, ProjectFiles.IsProject(path));
        Assert.Equal(openable, ProjectFiles.IsOpenable(path));
        Assert.Equal(charter, CharterRscp.IsCharterProject(path));
    }

    [Fact]
    public void ARscpBesideItsDcproj_OpensTheDcproj()
    {
        var rscp = Path.Combine(_dir, "song.rscp");
        var dcproj = Path.Combine(_dir, "song.dcproj");

        Assert.Equal(rscp, ProjectFiles.PreferProject(rscp, _ => false));
        Assert.Equal(dcproj, ProjectFiles.PreferProject(rscp, p => p == dcproj));
        Assert.Equal(dcproj, ProjectFiles.PreferProject(dcproj, _ => true));
    }

    [Fact]
    public void AFolderScan_ListsEveryDcproj_AndOnlyTheRscpFilesWithNoDcprojBeside()
    {
        var found = new[]
        {
            Path.Combine(_dir, "A", "project.dcproj"),
            Path.Combine(_dir, "A", "project.rscp"),
            Path.Combine(_dir, "B", "charter.rscp"),
            Path.Combine(_dir, "C", "other.rscp"),
            Path.Combine(_dir, "C", "project.dcproj")
        };

        Assert.Equal(
            [found[0], found[2], found[3], found[4]],
            ProjectFiles.WithoutShadowedCompanions(found));
    }

    [Fact]
    public void AFileWithADcWriterVersion_IsAnOlderDotCharterProject_AndOneWithoutIsCharters()
    {
        var ours = Path.Combine(_dir, "ours.rscp");
        new RscpChartSerializer().Save(ours, new ChartProject());
        var charters = Path.Combine(_dir, "charter.rscp");
        File.WriteAllText(charters, "<?xml version='1.0' encoding='UTF-8'?>\n<project><chartFormatVersion>3</chartFormatVersion><title>t</title></project>");
        var nested = Path.Combine(_dir, "nested.rscp");
        File.WriteAllText(nested, "<project><arrangements><dcWriterVersion>1.0</dcWriterVersion></arrangements></project>");
        var broken = Path.Combine(_dir, "broken.rscp");
        File.WriteAllText(broken, "<project><unclosed>");

        Assert.True(CharterRscp.IsWrittenByDotCharter(ours));
        Assert.False(CharterRscp.IsWrittenByDotCharter(charters));
        Assert.False(CharterRscp.IsWrittenByDotCharter(nested));
        Assert.False(CharterRscp.IsWrittenByDotCharter(broken));
        Assert.False(CharterRscp.IsWrittenByDotCharter(Path.Combine(_dir, "missing.rscp")));
    }

    [Fact]
    public void TheLegacyBackupName_NeverReusesAnExistingBackup()
    {
        var rscp = Path.Combine(_dir, "project.rscp");
        var taken = new HashSet<string> { rscp + ".bak", rscp + ".2.bak" };

        Assert.Equal(rscp + ".bak", CharterRscp.LegacyBackupPath(rscp, _ => false));
        Assert.Equal(rscp + ".3.bak", CharterRscp.LegacyBackupPath(rscp, taken.Contains));
        Assert.False(ProjectFiles.IsOpenable(rscp + ".3.bak"));
    }

    [Fact]
    public void TheCompanion_IsTheProjectNameWithRscp()
    {
        Assert.Equal(Path.Combine(_dir, "project.rscp"), CharterRscp.CompanionPath(Path.Combine(_dir, "project.dcproj")));
        Assert.Equal(Path.Combine(_dir, "Song.rscp"), CharterRscp.CompanionPath(Path.Combine(_dir, "Song.dcproj")));
    }
}
