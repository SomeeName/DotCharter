using System.Text.Json;
using DotCharter.Services.Export;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Services.Session;

public sealed class ProjectFolderTidyReferenceTests : IDisposable
{
    private readonly string _sandbox = Directory.CreateTempSubdirectory("tidy-references-").FullName;
    private string Root => Directory.CreateDirectory(Path.Combine(_sandbox, "chart")).FullName;
    private string ProjectPath => Path.Combine(Root, "project.dcproj");

    [Theory]
    [InlineData("lead.xml")]
    [InlineData("DTRS XML/sub/lead.xml")]
    public void Referenced_project_local_xml_keeps_its_relative_reference_when_the_builder_moves(string reference)
    {
        WriteProject();
        var source = Path.Combine(Root, reference);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "<song/>");
        Builder(reference);
        ProjectFolderTidy.Run(ProjectPath, new ChartProject(), new FakeAppLogger(), _ => { });
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(Root, "export", "project.rs2dlc")));
        Assert.Equal("<song/>", File.ReadAllText(Path.Combine(Root, "export", reference)));
    }

    [Theory]
    [InlineData("missing.xml")]
    [InlineData("../outside.xml")]
    [InlineData("//server/share/outside.xml")]
    [InlineData("source.txt")]
    public void Unresolved_or_unsafe_references_keep_the_builder_and_log_once(string reference)
    {
        WriteProject();
        var outside = Path.Combine(_sandbox, "outside.xml");
        File.WriteAllText(outside, "outside bytes");
        File.WriteAllText(Path.Combine(Root, "source.txt"), "user bytes");
        Builder(reference);
        var logger = new FakeAppLogger();
        ProjectFolderTidy.Run(ProjectPath, new ChartProject(), logger, _ => Assert.Fail("Do not recycle unresolved data"));
        Assert.True(File.Exists(Path.Combine(Root, "project.rs2dlc")));
        Assert.False(File.Exists(Path.Combine(Root, "export", "project.rs2dlc")));
        Assert.NotEmpty(logger.Snapshot());
        var next = new FakeAppLogger();
        ProjectFolderTidy.Run(ProjectPath, new ChartProject(), next, _ => Assert.Fail("Do not recycle unresolved data"));
        Assert.Empty(next.Snapshot());
        Assert.Equal("outside bytes", File.ReadAllText(outside));
        Assert.Equal("user bytes", File.ReadAllText(Path.Combine(Root, "source.txt")));
    }

    [Fact]
    public void Custom_key_projects_also_move_the_automatic_companions_metadata_named_package()
    {
        WriteProject();
        var project = new ChartProject { Chart = { Artist = "Artist", SongName = "Song", DlcKey = "CustomKey" } };
        var automatic = RocksmithDlcKey.Create("Artist", "Song") + "_p.psarc";
        var authored = "CustomKey_m.psarc";
        File.WriteAllText(Path.Combine(Root, automatic), "automatic package");
        File.WriteAllText(Path.Combine(Root, authored), "authored package");
        ProjectFolderTidy.Run(ProjectPath, project, new FakeAppLogger(), _ => Assert.Fail("Packages must only move"));
        Assert.Equal("automatic package", File.ReadAllText(Path.Combine(Root, "export", "Artist_Song_v1_p.psarc")));
        Assert.Equal("authored package", File.ReadAllText(Path.Combine(Root, "export", "Artist_Song_v1_m.psarc")));
    }

    [Fact]
    public void Invalid_authored_keys_never_authorize_generic_package_names()
    {
        WriteProject();
        File.WriteAllText(Path.Combine(Root, "_p.psarc"), "unrelated package");
        var project = new ChartProject { Chart = { DlcKey = "!!!" } };
        ProjectFolderTidy.Run(ProjectPath, project, new FakeAppLogger(), _ => Assert.Fail("Unknown packages stay"));
        Assert.Equal("unrelated package", File.ReadAllText(Path.Combine(Root, "_p.psarc")));
        Assert.False(File.Exists(Path.Combine(Root, "export", "_p.psarc")));
    }

    [Fact]
    public void Owned_companion_collisions_preserve_both_files_and_have_one_persistent_notice()
    {
        WriteProject();
        var source = Path.Combine(Root, "project.eof");
        CompanionOwnership.WriteIfUnmodified(source, path => File.WriteAllText(path, "owned EOF"), out _);
        var target = Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "export")).FullName, "project.eof");
        File.WriteAllText(target, "foreign EOF");
        var first = new FakeAppLogger();
        ProjectFolderTidy.Run(ProjectPath, new ChartProject(), first, _ => Assert.Fail("Both versions must remain"));
        Assert.Single(first.Snapshot());
        var second = new FakeAppLogger();
        ProjectFolderTidy.Run(ProjectPath, new ChartProject(), second, _ => Assert.Fail("Both versions must remain"));
        Assert.Empty(second.Snapshot());
        Assert.Equal("owned EOF", File.ReadAllText(source));
        Assert.Equal("foreign EOF", File.ReadAllText(target));
    }

    private void Builder(string reference) => CompanionOwnership.WriteIfUnmodified(Path.Combine(Root, "project.rs2dlc"),
        path => File.WriteAllText(path, JsonSerializer.Serialize(new { Xml = reference })), out _);

    private void WriteProject() => File.WriteAllText(ProjectPath, "project");

    public void Dispose() => Directory.Delete(_sandbox, true);
}
