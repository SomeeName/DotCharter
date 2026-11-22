using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Configuration;

[Collection("CwdSerialized")]
public sealed class AppConfigExtendedRangeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-config-format-").FullName;

    public AppConfigExtendedRangeTests() => WritablePaths.OverrideRootsForTesting(_directory, _directory);

    public void Dispose()
    {
        WritablePaths.OverrideRootsForTesting(null, null);
        BestEffortDelete.Directory(_directory);
    }

    [Theory]
    [InlineData("\"Rs7x\"")]
    [InlineData("\"Rsxr\"")]
    [InlineData("\"unknown\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{\"Value\":\"Rsxr\"}")]
    public void Stored_format_keys_are_ignored_and_real_settings_survive_load_and_save(string value)
    {
        var path = Path.Combine(_directory, "appconfig.json");
        File.WriteAllText(path, "{\"ExtendedRangeFormat\":" + value +
            ",\"SaveCompanionPsarc\":true,\"LanguageCulture\":\"de-DE\",\"LastExportAppId\":\"123456\"," +
            "\"PasteReplacesExisting\":false,\"ExportGenerateDynamicDifficulty\":false," +
            "\"FutureSetting\":{\"Count\":17,\"Label\":\"kept\"}}");
        var logger = new FakeAppLogger();
        using var service = new JsonConfigurationService(TimeSpan.Zero, null) { Logger = logger };

        var config = service.LoadAppConfig();

        AssertSettings(config);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == "ERROR");
        service.SaveAppConfig(config);
        service.Flush();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var saved = document.RootElement;
        Assert.False(saved.TryGetProperty("ExtendedRangeFormat", out _));
        Assert.True(saved.GetProperty("SaveCompanionPsarc").GetBoolean());
        Assert.Equal("de-DE", saved.GetProperty("LanguageCulture").GetString());
        Assert.Equal("123456", saved.GetProperty("LastExportAppId").GetString());
        Assert.False(saved.GetProperty("PasteReplacesExisting").GetBoolean());
        Assert.False(saved.GetProperty("ExportGenerateDynamicDifficulty").GetBoolean());
        Assert.Equal(17, saved.GetProperty("FutureSetting").GetProperty("Count").GetInt32());
        Assert.Equal("kept", saved.GetProperty("FutureSetting").GetProperty("Label").GetString());
        using var reader = new JsonConfigurationService(TimeSpan.Zero, null);
        AssertSettings(reader.LoadAppConfig());
    }

    private static void AssertSettings(AppConfig config)
    {
        Assert.True(config.SaveCompanionPsarc);
        Assert.Equal("de-DE", config.LanguageCulture);
        Assert.Equal("123456", config.LastExportAppId);
        Assert.False(config.PasteReplacesExisting);
        Assert.False(config.ExportGenerateDynamicDifficulty);
    }
}
