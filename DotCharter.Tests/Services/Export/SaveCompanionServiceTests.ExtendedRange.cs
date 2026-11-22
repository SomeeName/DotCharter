using System.Text.Json;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tests.Services.Export.Rsxr;

namespace DotCharter.Tests.Services.Export;

public sealed partial class SaveCompanionServiceTests
{
    [Fact]
    public async Task Refused_tuning_is_logged_and_the_stock_companion_is_published()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        File.WriteAllBytes(Path.Combine(_dir, "song.wem"), new byte[4000]);
        var project = RsxrTestSupport.Chart(false, 6).Project;
        project.AudioFileName = "song.wem";
        var refused = RsxrTestSupport.Chart(false, 7).Project.Arrangements[0];
        refused.Name = "Refused";
        refused.TuningPitches[0] = new TuningOffset(25);
        project.Arrangements.Add(refused);
        var sentence = RsxrStringLayout.ExportRefusal(refused)!;
        var logger = new FakeAppLogger();
        var service = Create(new AppConfig
        {
            SaveCompanionPsarc = true,
            ExportGenerateDynamicDifficulty = false, ExportApplyDlcBuilderFixes = false
        }, logger);

        var path = await service.BuildPsarcAsync(Path.Combine(_dir, "song.dcproj"), project, null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(path);
        Assert.Equal(DotCharter.Services.Export.Rocksmith.RocksmithReleaseName.For(project, project.Chart.Version) + "_p.psarc", Path.GetFileName(path));
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(path)[..4]);
        var entries = RsxrTestSupport.Entries(path);
        var manifest = Assert.Single(entries, e => e.Key.EndsWith(".json", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(manifest.Value);
        Assert.Single(json.RootElement.GetProperty("Entries").EnumerateObject());
        Assert.Single(entries, e => e.Key.EndsWith(".sng", StringComparison.Ordinal));
        Assert.True(logger.HasEntryContaining("WARN", $"Arrangement '{refused.Name}' was skipped: {sentence}"));
    }

    [Fact]
    public async Task Extended_companion_package_uses_RS_Extended_Range()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        WriteWav(Path.Combine(_dir, "song.wav"));
        var service = Create(new AppConfig
        {
            SaveCompanionPsarc = true, SaveCompanionPsarcCodec = "Vorbis",
            ExportGenerateDynamicDifficulty = false, ExportApplyDlcBuilderFixes = false
        });
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        arrangement.StringCount = 7;
        arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels[0].Notes.Add(new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(1000, project.Beats), StringIndex = 0, Fret = 3
        });

        var path = await service.BuildPsarcAsync(Path.Combine(_dir, "song.dcproj"), project, null,
            TestContext.Current.CancellationToken);

        Assert.NotNull(path);
        Assert.EndsWith("_p.psarc", path);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(Rsxr.RsxrTestSupport.PackageMagic(true), bytes[..4]);
    }
}
