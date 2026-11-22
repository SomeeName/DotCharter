using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

public sealed partial class SaveCompanionServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unsuccessful_build_keeps_both_owned_names_and_foreign_files(bool cancelled)
    {
        var project = CreateProject();
        project.Arrangements[0].StringCount = 7;
        project.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 });
        project.AudioFileName = "broken.wav";
        File.WriteAllText(Path.Combine(_dir, project.AudioFileName), "invalid audio");
        var plain = Path.Combine(_dir, "export", RocksmithReleaseName.For(project, project.Chart.Version) + "_p.psarc");
        var tagged = ExtendedRangePackageName.Apply(plain, true);
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        var foreign = Path.Combine(_dir, "foreign_p.psarc");
        foreach (var path in new[] { plain, tagged, foreign }) File.WriteAllText(path, Path.GetFileName(path));
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        var service = Create(new AppConfig { SaveCompanionPsarc = true });
        var failure = await Record.ExceptionAsync(() => service.BuildPsarcAsync(Path.Combine(_dir, "song.dcproj"), project, null, cancellation.Token));
        Assert.NotNull(failure);
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        foreach (var path in new[] { plain, tagged, foreign }) Assert.Equal(Path.GetFileName(path), File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.building"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_string_count_removes_a_published_sibling_and_preserves_an_unowned_one(bool owned)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var config = new FakeConfigurationService
        {
            AppConfig = new AppConfig
            {
                SaveCompanionPsarc = true,
                ExportApplyDlcBuilderFixes = false, ExportGenerateDynamicDifficulty = false
            }
        };
        var logger = new FakeAppLogger();
        var service = Create(config.AppConfig, logger);
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        arrangement.StringCount = 7;
        arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 1, Fret = 3 });
        var projectPath = Path.Combine(_dir, "song.dcproj");
        var plain = Path.Combine(_dir, "export", RocksmithReleaseName.For(project, project.Chart.Version) + "_p.psarc");
        var tagged = ExtendedRangePackageName.Apply(plain, true);
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);
        var foreign = Path.Combine(_dir, "foreign_rsxr_p.psarc");
        File.WriteAllText(foreign, "foreign");
        if (owned)
        {
            arrangement.StringCount = 6;
            Assert.Equal(plain, await service.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken));
            arrangement.StringCount = 7;
        }
        else
        {
            File.WriteAllText(plain, "copied package");
        }

        Assert.Equal(tagged, await service.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(tagged));
        if (owned)
        {
            Assert.False(File.Exists(plain));
        }
        else
        {
            Assert.Equal("copied package", File.ReadAllText(plain));
            Assert.Single(logger.Snapshot(), entry => entry.Level == "WARN" && entry.Message.Contains(plain, StringComparison.Ordinal)
                && entry.Message.Contains("was left in place; it holds an older package of this song", StringComparison.Ordinal));
        }

        arrangement.StringCount = 6;
        var next = await service.BuildPsarcAsync(projectPath, project, null, TestContext.Current.CancellationToken);
        if (!owned)
        {
            Assert.Null(next);
            Assert.Equal("copied package", File.ReadAllText(plain));
            Assert.True(File.Exists(tagged));
            return;
        }
        Assert.Equal(plain, next);
        Assert.True(File.Exists(plain));
        Assert.False(File.Exists(tagged));
        Assert.Equal("foreign", File.ReadAllText(foreign));
    }
}
