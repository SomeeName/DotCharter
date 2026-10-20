using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

/// <summary>DLC Builder's test build (<c>TestPackageBuilder.fs</c>): what it writes and removes, with and without the game.</summary>
public sealed class RocksmithTestExportTests
{
    private const string Folder = @"C:\Rocksmith\dlc\test";

    private static string[] Files(params string[] names) => [.. names.Select(n => Path.Combine(Folder, n))];

    [Fact]
    public void WithoutTheGame_ThePlainNameIsReused_AndEarlierTestBuildsAreDeleted()
    {
        var existing = Files("dotcsong_p.psarc", "dotcsong_v2_p.psarc", "dotcsong_m.psarc", "other_p.psarc");

        var plan = RocksmithTestExport.Plan("DotcSong", existing, gameRunning: false);

        Assert.Equal("DotcSong", plan.DlcKey);
        Assert.Equal("dotcsong", plan.FileStem);
        Assert.Equal("", plan.VersionTag);
        Assert.Equal("", plan.TitleSuffix);
        Assert.Equal(existing[..3], plan.Superseded);
    }

    [Fact]
    public void WithTheGame_ANewVersionIsWritten_AndNothingIsDeleted()
    {
        var plan = RocksmithTestExport.Plan("DotcSong", Files("dotcsong_p.psarc", "dotcsong_v2_p.psarc", "dotcsong_v7_p.psarc"),
            gameRunning: true);

        Assert.Equal("DotcSongv8", plan.DlcKey);
        Assert.Equal("dotcsong_v8", plan.FileStem);
        Assert.Equal("v8", plan.VersionTag);
        Assert.Equal(" v8", plan.TitleSuffix);
        Assert.Empty(plan.Superseded);
    }

    [Theory]
    [InlineData(0, "v1")]
    // no _vN yet: one past the number of earlier builds, like DLC Builder
    [InlineData(1, "v2")]
    [InlineData(2, "v3")]
    public void WithTheGame_UnnumberedBuilds_CountTowardsTheNextVersion(int earlier, string expected)
    {
        var names = new[] { "dotcsong_p.psarc", "dotcsong_m.psarc" }.Take(earlier).ToArray();

        Assert.Equal(expected, RocksmithTestExport.Plan("DotcSong", Files(names), gameRunning: true).VersionTag);
    }

    [Fact]
    public void OnlyFilesNamedAfterThePackageCount()
    {
        var plan = RocksmithTestExport.Plan("DotcSong", Files("OTHER_v9_p.psarc", "readme.txt"), gameRunning: true);

        Assert.Equal("v1", plan.VersionTag);
    }

    [Fact]
    public void ATestBuildIsVersionTest() => Assert.Equal("test", RocksmithTestExport.Version);
}
