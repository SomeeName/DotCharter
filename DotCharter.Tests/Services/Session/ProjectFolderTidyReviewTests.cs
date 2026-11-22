using System.Security.Cryptography;
using System.Text.Json;
using DotCharter.Services.Export;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

public sealed class ProjectFolderTidyReviewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tidy-review-").FullName;

    [Fact]
    public void Review1_original_rscp_backups_move_to_unique_visible_backup_names_without_recycling()
    {
        Write("project.dcproj", "project");
        Directory.CreateDirectory(Path.Combine(_root, "backups"));
        Write("backups/project.rscp.bak", "existing original");
        Write("backups/project.rscp.2.bak", "another existing original");
        Write("project.rscp.bak", "first original");
        Write("project.rscp.7.bak", "second original");
        var logger = new FakeAppLogger();
        Assert.True(ProjectFolderTidy.Run(Path.Combine(_root, "project.dcproj"), new ChartProject(), logger,
            _ => Assert.Fail("An original backup must never be recycled")));
        Assert.False(File.Exists(Path.Combine(_root, "project.rscp.bak")));
        Assert.False(File.Exists(Path.Combine(_root, "project.rscp.7.bak")));
        Assert.Equal(new[] { "another existing original", "existing original", "first original", "second original" },
            Directory.GetFiles(Path.Combine(_root, "backups")).Select(File.ReadAllText).Order().ToArray());
        Assert.Equal(new[] { "project.rscp.2.bak", "project.rscp.3.bak", "project.rscp.4.bak", "project.rscp.bak" },
            Directory.GetFiles(Path.Combine(_root, "backups")).Select(Path.GetFileName).Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review4_real_legacy_builder_reference_moves_unstamped_xml_and_all_matching_packages(bool authoredKey)
    {
        Write("project.dcproj", "project");
        Write("song.flac", "working audio");
        Write("song.wem", "unproven audio");
        var project = new ChartProject { AudioFileName = "song.flac", Chart = { Artist = "Artist", SongName = "Song" } };
        if (authoredKey) project.Chart.DlcKey = "SyntheticKey";
        var key = authoredKey ? project.Chart.DlcKey : RocksmithDlcKey.Create("Artist", "Song");
        Directory.CreateDirectory(Path.Combine(_root, "DTRS XML"));
        Write("DTRS XML/1_Lead_Main_RS2.xml", "<song><title>Song</title></song>");
        Write("DTRS XML/2_Vocals.xml", "<vocals/>");
        Write("DTRS XML/foreign.xml", "foreign XML");
        const string builder = """{"Arrangements":[{"Xml":"DTRS XML/1_Lead_Main_RS2.xml"},{"Xml":"DTRS XML/2_Vocals.xml"}]}""";
        Write("project.rs2dlc", builder);
        StampLegacy("project.rs2dlc");
        foreach (var suffix in new[] { "_p.psarc", "_m.psarc", "_rsxr_p.psarc" }) Write(key + suffix, "package " + suffix);
        Write("OtherKey_p.psarc", "foreign package");
        var logger = new FakeAppLogger();
        ProjectFolderTidy.Run(Path.Combine(_root, "project.dcproj"), project, logger, FakeRecycle);
        Assert.Equal(builder, File.ReadAllText(Path.Combine(_root, "export", "project.rs2dlc")));
        Assert.Equal("<song><title>Song</title></song>", File.ReadAllText(Path.Combine(_root, "export", "DTRS XML", "1_Lead_Main_RS2.xml")));
        Assert.Equal("<vocals/>", File.ReadAllText(Path.Combine(_root, "export", "DTRS XML", "2_Vocals.xml")));
        foreach (var suffix in new[] { "_p.psarc", "_m.psarc", "_rsxr_p.psarc" })
        {
            var target = Path.Combine(_root, "export", "Artist_Song_v1" + suffix);
            Assert.Equal("package " + suffix, File.ReadAllText(target));
            Assert.True(CompanionOwnership.IsUnmodified(target));
        }
        Assert.Equal("foreign XML", File.ReadAllText(Path.Combine(_root, "DTRS XML", "foreign.xml")));
        Assert.Equal("foreign package", File.ReadAllText(Path.Combine(_root, "OtherKey_p.psarc")));
        Assert.Equal("unproven audio", File.ReadAllText(Path.Combine(_root, "song.wem")));
        var nextLogger = new FakeAppLogger();
        ProjectFolderTidy.Run(Path.Combine(_root, "project.dcproj"), project, nextLogger, _ => Assert.Fail("Nothing else to recycle"));
        Assert.Empty(nextLogger.Snapshot());
        Assert.Contains(Directory.GetFiles(Path.Combine(_root, ".dotcharter")), file => Path.GetFileName(file).Contains("tidy", StringComparison.Ordinal));
    }

    [Fact]
    public void Review4_unproven_candidates_are_logged_once_across_separate_opens()
    {
        Write("project.dcproj", "project");
        Write("project.eof", "foreign EOF");
        Write("project.rs2dlc", "foreign builder");
        Write("song.flac", "source");
        Write("song.wem", "foreign audio");
        var project = new ChartProject { AudioFileName = "song.flac" };
        var first = new FakeAppLogger();
        ProjectFolderTidy.Run(Path.Combine(_root, "project.dcproj"), project, first, _ => Assert.Fail("Foreign data must remain"));
        Assert.NotEmpty(first.Snapshot());
        var second = new FakeAppLogger();
        ProjectFolderTidy.Run(Path.Combine(_root, "project.dcproj"), project, second, _ => Assert.Fail("Foreign data must remain"));
        Assert.Empty(second.Snapshot());
        Assert.Equal("foreign EOF", File.ReadAllText(Path.Combine(_root, "project.eof")));
    }

    [Fact]
    public void Review4_existing_export_package_never_causes_recycling_or_overwriting_the_old_package()
    {
        Write("project.dcproj", "project");
        var project = new ChartProject { Chart = { Artist = "Artist", SongName = "Song" } };
        var name = RocksmithDlcKey.Create("Artist", "Song") + "_p.psarc";
        Write(name, "old package");
        CompanionOwnership.AdoptMovedFile(Path.Combine(_root, name));
        Directory.CreateDirectory(Path.Combine(_root, "export"));
        Write("export/Artist_Song_v1_p.psarc", "published package");
        CompanionOwnership.AdoptMovedFile(Path.Combine(_root, "export", "Artist_Song_v1_p.psarc"));
        ProjectFolderTidy.Run(Path.Combine(_root, "project.dcproj"), project, new FakeAppLogger(),
            _ => Assert.Fail("Package collisions must never recycle either file"));
        Assert.Equal("old package", File.ReadAllText(Path.Combine(_root, name)));
        Assert.Equal("published package", File.ReadAllText(Path.Combine(_root, "export", "Artist_Song_v1_p.psarc")));
    }

    private void StampLegacy(params string[] names) => Write(".dotcharter-companions.json", JsonSerializer.Serialize(new
    {
        Version = 1,
        Files = names.ToDictionary(name => name, name => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, name)))))
    }));

    private void FakeRecycle(string path)
    {
        Assert.Equal(".dotcharter-companions.json", Path.GetFileName(path));
        File.Move(path, Path.Combine(_root, ".dotcharter", "legacy-stamp.recycled"));
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_root, name), text);

    public void Dispose() => Directory.Delete(_root, true);
}
