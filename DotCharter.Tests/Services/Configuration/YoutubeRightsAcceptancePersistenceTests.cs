using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Configuration;

/// <summary>#821: the YouTube rights acceptance survives a restart, and saving it keeps the members this build does not know (#806).</summary>
[Collection("CwdSerialized")]
public sealed class YoutubeRightsAcceptancePersistenceTests : IDisposable
{
    private readonly string _originalCwd = Directory.GetCurrentDirectory();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterTests_" + Guid.NewGuid().ToString("N"));
    private readonly List<JsonConfigurationService> _services = [];

    public YoutubeRightsAcceptancePersistenceTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
        WritablePaths.OverrideRootsForTesting(_tempDir, _tempDir);
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        WritablePaths.OverrideRootsForTesting(null, null);
        Directory.SetCurrentDirectory(_originalCwd);
        BestEffortDelete.Directory(_tempDir);
    }

    private static string ConfigPath => WritablePaths.ResolveConfigFile("appconfig.json");

    private JsonConfigurationService NewService()
    {
        var service = new JsonConfigurationService(TimeSpan.Zero, null);
        _services.Add(service);
        return service;
    }

    [Fact]
    public void The_acceptance_is_off_by_default_and_survives_a_restart_beside_unknown_members()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, """{ "FutureTopLevel": { "Name": "kept" } }""");

        var first = NewService();
        Assert.False(first.LoadAppConfig().YoutubeRightsAccepted);
        first.Update(config => config.YoutubeRightsAccepted = true);
        first.Flush();

        var restarted = NewService();
        Assert.True(restarted.LoadAppConfig().YoutubeRightsAccepted);
        using var json = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        Assert.Equal("kept", json.RootElement.GetProperty("FutureTopLevel").GetProperty("Name").GetString());
    }
}
