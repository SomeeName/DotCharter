using DotCharter.Services.Export;
using DotCharter.Services.Session;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Session;

public sealed class ProjectFolderTidyNamingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tidy-package-name-").FullName;

    [Theory]
    [InlineData("_p.psarc", "Artist_Title_v2_1_p.psarc")]
    [InlineData("_rsxr_p.psarc", "Artist_Title_v2_1_rsxr_p.psarc")]
    [InlineData("_m.psarc", "Artist_Title_v2_1_m.psarc")]
    public void Legacy_key_package_moves_to_new_release_name_and_is_owned(string oldSuffix, string expected)
    {
        var project = new ChartProject { Chart = { Artist = "Artist", SongName = "Title", Version = "2.1" } };
        var projectPath = Path.Combine(_root, "project.dcproj");
        File.WriteAllText(projectPath, "project");
        var old = Path.Combine(_root, RocksmithDlcKey.Create("Artist", "Title") + oldSuffix);
        File.WriteAllText(old, "legacy package");
        var foreign = Path.Combine(_root, "OtherKey_p.psarc");
        File.WriteAllText(foreign, "foreign package");

        ProjectFolderTidy.Run(projectPath, project, new FakeAppLogger(), _ => Assert.Fail("Migration moves packages; it never recycles them"));

        var target = Path.Combine(_root, "export", expected);
        Assert.True(File.Exists(target));
        Assert.Equal("legacy package", File.ReadAllText(target));
        Assert.False(File.Exists(old));
        Assert.True(CompanionOwnership.IsUnmodified(target));
        Assert.Equal("foreign package", File.ReadAllText(foreign));
    }

    [Fact]
    public void A_new_name_collision_preserves_both_packages_and_does_not_adopt_the_foreign_target()
    {
        var project = new ChartProject { Chart = { Artist = "Artist", SongName = "Title" } };
        var projectPath = Path.Combine(_root, "project.dcproj");
        File.WriteAllText(projectPath, "project");
        var old = Path.Combine(_root, RocksmithDlcKey.Create("Artist", "Title") + "_p.psarc");
        File.WriteAllText(old, "legacy package");
        var target = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "export")).FullName, "Artist_Title_v1_p.psarc");
        File.WriteAllText(target, "foreign target");

        ProjectFolderTidy.Run(projectPath, project, new FakeAppLogger(), _ => Assert.Fail("Collisions preserve both packages"));

        Assert.Equal("legacy package", File.ReadAllText(old));
        Assert.Equal("foreign target", File.ReadAllText(target));
        Assert.False(CompanionOwnership.IsUnmodified(target));
    }

    public void Dispose() => BestEffortDelete.Directory(_root);
}
