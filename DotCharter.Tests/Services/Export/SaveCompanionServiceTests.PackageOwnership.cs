using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

public sealed partial class SaveCompanionServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Any_unmodified_owned_sibling_is_removed_across_service_instances(int ownership)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig
            {
                SaveCompanionPsarc = true, ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false
            }
        };
        var logger = new FakeAppLogger();
        var service = Create(config.AppConfig, logger);
        var project = CreateProject();
        project.Arrangements[0].StringCount = 7;
        project.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 });
        var projectPath = Path.Combine(_dir, "song.dcproj");
        var plain = Path.Combine(_dir, "export", RocksmithReleaseName.For(project, project.Chart.Version) + "_p.psarc");
        var tagged = ExtendedRangePackageName.Apply(plain, true);
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        if (ownership == 0)
        {
            File.WriteAllText(tagged, "copied package");
        }
        else
        {
            var publisher = ownership == 1 ? service : Create(config.AppConfig, new FakeAppLogger());
            Assert.Equal(tagged, await publisher.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken));
        }
        var before = File.ReadAllBytes(tagged);
        project.Arrangements[0].StringCount = 6;

        Assert.Equal(plain, await service.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken));

        Assert.True(File.Exists(plain));
        if (ownership > 0)
        {
            Assert.False(File.Exists(tagged));
        }
        else
        {
            Assert.Equal(before, File.ReadAllBytes(tagged));
            Assert.Single(logger.Snapshot(), entry => entry.Level == "WARN" && entry.Message.Contains(tagged, StringComparison.Ordinal)
                && entry.Message.Contains("was left in place; it holds an older package of this song", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task An_unavailable_recycle_backend_retains_published_ownership_for_the_next_save()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var available = false;
        var service = Create(new AppConfig
        {
            SaveCompanionPsarc = true, ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false
        }, recycle: path =>
        {
            if (available) RecycleCompanion(path);
        });
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        arrangement.StringCount = 7;
        arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 });
        var projectPath = Path.Combine(_dir, "song.dcproj");
        var plain = Path.Combine(_dir, "export", RocksmithReleaseName.For(project, project.Chart.Version) + "_p.psarc");
        var tagged = ExtendedRangePackageName.Apply(plain, true);
        Assert.Equal(tagged, await service.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken));
        var before = File.ReadAllBytes(tagged);
        arrangement.StringCount = 6;

        Assert.Equal(plain, await service.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken));

        Assert.Equal(before, File.ReadAllBytes(tagged));
        Assert.True(CompanionOwnership.IsUnmodified(tagged));
        available = true;
        Assert.Equal(plain, await service.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(tagged));
        var recycled = Assert.Single(Directory.GetFiles(Path.Combine(_dir, ".dotcharter", "test-bin")));
        Assert.Equal(before, File.ReadAllBytes(recycled));
    }
}
