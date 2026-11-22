using DotCharter.Services.Infrastructure.Backup;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Configuration;

[Collection("CwdSerialized")]
public sealed class AutoVolumeTargetConfigTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-config-auto-volume-").FullName;

    public AutoVolumeTargetConfigTests() => WritablePaths.OverrideRootsForTesting(_directory, _directory);

    public void Dispose()
    {
        WritablePaths.OverrideRootsForTesting(null, null);
        BestEffortDelete.Directory(_directory);
    }

    [Theory]
    [InlineData("{}", -14)]
    [InlineData("""{"ExportAutoVolumeTargetLufs": -40}""", -20)]
    [InlineData("""{"ExportAutoVolumeTargetLufs": 0}""", -10)]
    public void StoredTargetIsClampedAndRoundTrips(string json, int expected)
    {
        File.WriteAllText(Path.Combine(_directory, "appconfig.json"), json);
        using (var service = new JsonConfigurationService(TimeSpan.Zero, null))
        {
            var config = service.LoadAppConfig();
            Assert.Equal(expected, config.ExportAutoVolumeTargetLufs);
            service.SaveAppConfig(config);
            service.Flush();
        }

        using var reader = new JsonConfigurationService(TimeSpan.Zero, null);
        Assert.Equal(expected, reader.LoadAppConfig().ExportAutoVolumeTargetLufs);
    }

    [Theory]
    [InlineData(-18, -18)]
    [InlineData(-40, -20)]
    [InlineData(0, -10)]
    public void ABackupRestoresTheTargetAndConfigLoadClampsIt(int stored, int expected)
    {
        var source = Directory.CreateDirectory(Path.Combine(_directory, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "appconfig.json"),
            System.Text.Json.JsonSerializer.Serialize(new { ExportAutoVolumeTargetLufs = stored }));
        var archive = Path.Combine(_directory, "settings.zip");
        new ConfigBackupService(source).Export(archive);

        var result = new ConfigBackupService(_directory).Import(archive);

        Assert.Empty(result.NotRestoredSettings);
        using var reader = new JsonConfigurationService(TimeSpan.Zero, null);
        Assert.Equal(expected, reader.LoadAppConfig().ExportAutoVolumeTargetLufs);
    }
}
