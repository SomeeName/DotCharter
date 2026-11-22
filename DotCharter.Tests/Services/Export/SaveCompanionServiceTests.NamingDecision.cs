using DotCharter.Services.Export;

namespace DotCharter.Tests.Services.Export;

public sealed partial class SaveCompanionServiceTests
{
    [Theory]
    [InlineData(false, "Artist_Title_v2_1_p.psarc")]
    [InlineData(true, "Artist_Title_v2_1_rsxr_p.psarc")]
    public async Task Naming_decision_companion_uses_the_shared_release_filename(bool extended, string expected)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var project = NamingProject(extended);
        project.Chart.Version = "2.1";
        var path = await Create(NamingConfig()).BuildPsarcAsync(Path.Combine(_dir, "project.dcproj"), project, null,
            TestContext.Current.CancellationToken);
        Assert.Equal(expected, Path.GetFileName(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Naming_decision_switching_range_replaces_the_owned_variant_across_service_instances(bool initiallyExtended)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var project = NamingProject(initiallyExtended);
        var projectPath = Path.Combine(_dir, "project.dcproj");
        var before = await Create(NamingConfig()).BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        project.Arrangements[0].StringCount = initiallyExtended ? 6 : 7;
        var after = await Create(NamingConfig()).BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
        Assert.False(File.Exists(before));
        Assert.True(File.Exists(after));
    }

    [Fact]
    public async Task Naming_decision_version_bump_replaces_owned_v1_and_keeps_foreign_or_edited_packages()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var project = NamingProject(false);
        var projectPath = Path.Combine(_dir, "project.dcproj");
        var service = Create(NamingConfig());
        var first = await service.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        var owned = Path.Combine(_dir, "export", "Artist_Title_v1_p.psarc");
        // Represents an owned v1 from a prior session even against the former DLC-key naming implementation.
        if (!File.Exists(owned)) CompanionOwnership.WriteIfUnmodified(owned, path => File.WriteAllText(path, "owned v1"), out _);
        var edited = Path.Combine(_dir, "export", "Artist_Title_v0_9_rsxr_p.psarc");
        CompanionOwnership.WriteIfUnmodified(edited, path => File.WriteAllText(path, "owned before edit"), out _);
        File.WriteAllText(edited, "user edit");
        var foreign = Path.Combine(_dir, "export", "Artist_Title_v0_8_p.psarc");
        File.WriteAllText(foreign, "foreign package");
        project.Chart.Version = "2";

        var second = await Create(NamingConfig()).BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(owned));
        Assert.Equal("Artist_Title_v2_p.psarc", Path.GetFileName(second));
        Assert.Equal("user edit", File.ReadAllText(edited));
        Assert.Equal("foreign package", File.ReadAllText(foreign));
    }

    [Fact]
    public async Task Naming_decision_never_replaces_an_unowned_package_at_the_derived_output()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var project = NamingProject(false);
        var target = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "export")).FullName,
            ExportPathDefaults.PackageFileName(project));
        File.WriteAllText(target, "foreign target");

        await Create(NamingConfig()).BuildPsarcAsync(Path.Combine(_dir, "project.dcproj"), project, null,
            TestContext.Current.CancellationToken);

        Assert.Equal("foreign target", File.ReadAllText(target));
        Assert.False(CompanionOwnership.IsUnmodified(target));
    }

    private static ChartProject NamingProject(bool extended)
    {
        var project = CreateProject();
        project.Chart.Artist = "Artist";
        project.Chart.SongName = "Title";
        project.Arrangements[0].StringCount = extended ? 7 : 6;
        project.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 1, Fret = 3 });
        return project;
    }

    [Fact]
    public async Task Naming_decision_the_next_save_replaces_a_migrated_and_stamped_legacy_package()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var project = NamingProject(false);
        var projectPath = Path.Combine(_dir, "project.dcproj");
        File.WriteAllText(projectPath, "project");
        var old = Path.Combine(_dir, RocksmithDlcKey.Create("Artist", "Title") + "_p.psarc");
        File.WriteAllText(old, "legacy package");
        DotCharter.Services.Session.ProjectFolderTidy.Run(projectPath, project, new FakeAppLogger(),
            _ => Assert.Fail("Package migration must move, never recycle"));
        var target = Path.Combine(_dir, "export", "Artist_Title_v1_p.psarc");
        Assert.True(CompanionOwnership.IsUnmodified(target));

        Assert.Equal(target, await Create(NamingConfig()).BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(old));
        Assert.NotEqual("legacy package"u8.ToArray(), File.ReadAllBytes(target));
        Assert.True(CompanionOwnership.IsUnmodified(target));
    }

    private static AppConfig NamingConfig() => new()
    {
        SaveCompanionPsarc = true, ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false
    };
}
