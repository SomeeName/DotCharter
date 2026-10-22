using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Library;

/// <summary>
///     The app-config half of the library identity migration: an old build's
///     <c>psarc:</c> song keys in <c>appconfig.json</c> are read as <c>lib:</c> and the
///     next save writes them. It is its own class because it moves the process working
///     directory and the <c>WritablePaths</c> roots to read a real config file, so it
///     sits in the serialized <c>CwdSerialized</c> collection; the other identity cases
///     pass explicit folders to their stores and run beside the rest of the gate.
/// </summary>
[Collection("CwdSerialized")]
public sealed class LibrarySongIdentityAppConfigMigrationTests : IDisposable
{
    private const string OwnChart = @"C:\Charts\My Song\project.rscp";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dc-lib-identity-cfg-" + Guid.NewGuid().ToString("N"));

    public LibrarySongIdentityAppConfigMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void AppConfig_OldSongKeys_AreReadAsLib_AndTheNextSaveWritesThem()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_root);
        WritablePaths.OverrideRootsForTesting(_root, _root);
        try
        {
            var configPath = Path.Combine(_root, "appconfig.json");
            File.WriteAllText(configPath, """
                {
                  "UseInGameTones": true,
                  "SongTonePreferences": {
                    "psarc:CallMe": { "Mode": "mytone", "PresetId": "user-1" },
                    "psarc:Heart": { "Mode": "ingame" },
                    "lib:Heart": { "Mode": "mytone", "PresetId": "user-2" },
                    "C:\\Charts\\My Song\\project.rscp": { "Mode": "ingame" }
                  },
                  "DropTunerSongs": [ "psarc:CallMe", "lib:callme", "C:\\Charts\\My Song\\project.rscp" ]
                }
                """);

            using var service = new JsonConfigurationService(TimeSpan.Zero, null);
            var config = service.LoadAppConfig();

            Assert.Equal(["C:\\Charts\\My Song\\project.rscp", "lib:CallMe", "lib:Heart"],
                config.SongTonePreferences.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("user-1", config.SongTonePreferences["lib:CallMe"].PresetId);
            Assert.Equal("user-2", config.SongTonePreferences["lib:Heart"].PresetId);
            Assert.Equal(["lib:CallMe", OwnChart], config.DropTunerSongs);

            service.SaveAppConfig(config);
            service.Flush();
            Assert.DoesNotContain("psarc:", File.ReadAllText(configPath), StringComparison.Ordinal);

            // Second run over the saved file: unchanged.
            var again = config.SongTonePreferences.Count;
            JsonConfigurationService.MigrateLibrarySongKeys(config);
            Assert.Equal(again, config.SongTonePreferences.Count);
            Assert.Equal(["lib:CallMe", OwnChart], config.DropTunerSongs);
        }
        finally
        {
            WritablePaths.OverrideRootsForTesting(null, null);
            Directory.SetCurrentDirectory(originalCwd);
        }
    }
}
