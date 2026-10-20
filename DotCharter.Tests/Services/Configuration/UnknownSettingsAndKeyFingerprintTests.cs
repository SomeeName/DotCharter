using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Configuration;

/// <summary>
///     #806: a save keeps the members this build does not know (an older build
///     deleting them wiped the Rocksmith keys), and a record of the keys' SHA-256
///     lets a later build warn when they vanish. Every key here is synthetic.
/// </summary>
[Collection("CwdSerialized")]
public sealed class UnknownSettingsAndKeyFingerprintTests : IDisposable
{
    private static readonly string TocHex = Convert.ToHexString(TestRocksmithKeys.SyntheticToc);
    private static readonly string PcHex = Convert.ToHexString(TestRocksmithKeys.SyntheticSngPc);

    private readonly string _originalCwd;
    private readonly string _tempDir;
    private readonly List<JsonConfigurationService> _services = [];

    public UnknownSettingsAndKeyFingerprintTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterTests_" + Guid.NewGuid().ToString("N"));
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
    private static string RecordPath => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "rocksmith-keys.fingerprint");

    private JsonConfigurationService NewService()
    {
        var service = new JsonConfigurationService(TimeSpan.Zero, null);
        _services.Add(service);
        return service;
    }

    private static void WriteConfig(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, json);
    }

    private static JsonElement SavedRoot()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        return json.RootElement.Clone();
    }

    private static void LoadSave(JsonConfigurationService service)
    {
        service.Update(_ => { });
        service.Flush();
    }

    [Fact]
    public void An_unknown_top_level_and_an_unknown_nested_property_survive_a_load_and_save()
    {
        WriteConfig("""
                    { "LanguageCulture": "de-DE",
                      "FutureTopLevel": { "Deep": [1, 2, 3], "Name": "kept" },
                      "RocksmithKeys": { "PsarcToc": "", "SngPc": "", "SngMac": "", "FutureNested": 42 } }
                    """);
        var service = NewService();
        LoadSave(service);

        var root = SavedRoot();
        Assert.Equal("de-DE", root.GetProperty("LanguageCulture").GetString());
        var future = root.GetProperty("FutureTopLevel");
        Assert.Equal("kept", future.GetProperty("Name").GetString());
        Assert.Equal([1, 2, 3], future.GetProperty("Deep").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Equal(42, root.GetProperty("RocksmithKeys").GetProperty("FutureNested").GetInt32());
    }

    [Fact]
    public void Export_presets_stored_by_an_older_build_load_cleanly_and_are_left_alone()
    {
        WriteConfig("""
                    { "ExportTestFolder": "D:\\test",
                      "ExportPresets": [ { "Id": "a", "Name": "Mac", "Platform": 1, "DefaultToneKey": "dc_house" } ],
                      "DefaultExportPresetId": "a" }
                    """);
        var service = NewService();

        Assert.Equal("D:\\test", service.LoadAppConfig().ExportTestFolder);
        LoadSave(service);

        var root = SavedRoot();
        Assert.Equal("a", root.GetProperty("DefaultExportPresetId").GetString());
        Assert.Equal("dc_house", root.GetProperty("ExportPresets")[0].GetProperty("DefaultToneKey").GetString());
    }

    [Fact]
    public void A_config_without_unknown_members_gains_none()
    {
        var service = NewService();
        LoadSave(service);

        Assert.False(SavedRoot().TryGetProperty("UnknownSettings", out _));
    }

    [Fact]
    public void The_warning_fires_when_the_fingerprint_is_present_but_the_keys_are_empty()
    {
        WriteConfig($$"""{ "RocksmithKeys": { "PsarcToc": "{{TocHex}}", "SngPc": "", "SngMac": "" } }""");
        NewService().LoadAppConfig();
        Assert.True(File.Exists(RecordPath));

        // An older build saved without the section; the new build then re-adds it empty.
        WriteConfig("""{ "LanguageCulture": "system", "RocksmithKeys": { "PsarcToc": "", "SngPc": "", "SngMac": "" } }""");
        var service = NewService();
        service.LoadAppConfig();
        var logger = new FakeAppLogger();
        service.Logger = logger;

        var warning = Assert.Single(logger.Entries, e => e.Level == "WARN");
        Assert.Contains("Rocksmith keys were removed", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_first_run_without_keys_stays_quiet_and_writes_no_record()
    {
        var service = NewService();
        service.LoadAppConfig();
        var logger = new FakeAppLogger();
        service.Logger = logger;

        Assert.DoesNotContain(logger.Entries, e => e.Level == "WARN");
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void The_warning_stops_when_the_keys_are_back()
    {
        WriteConfig($$"""{ "RocksmithKeys": { "PsarcToc": "{{TocHex}}" } }""");
        NewService().LoadAppConfig();
        WriteConfig("""{ "RocksmithKeys": { "PsarcToc": "" } }""");
        WriteConfig($$"""{ "RocksmithKeys": { "PsarcToc": "{{TocHex}}" } }""");

        var service = NewService();
        service.LoadAppConfig();
        var logger = new FakeAppLogger();
        service.Logger = logger;

        Assert.DoesNotContain(logger.Entries, e => e.Level == "WARN");
    }

    [Fact]
    public void The_fingerprint_never_contains_the_key_bytes()
    {
        WriteConfig($$"""{ "RocksmithKeys": { "PsarcToc": "{{TocHex}}", "SngPc": "{{PcHex}}", "SngMac": "" } }""");
        var service = NewService();
        service.LoadAppConfig();
        var logger = new FakeAppLogger();
        service.Logger = logger;

        var record = File.ReadAllBytes(RecordPath);
        Assert.Equal(64, File.ReadAllText(RecordPath).Trim().Length); // a SHA-256 in hex
        foreach (var key in new[] { TestRocksmithKeys.SyntheticToc, TestRocksmithKeys.SyntheticSngPc })
        {
            Assert.DoesNotContain(TocHexOrPc(key), File.ReadAllText(RecordPath), StringComparison.OrdinalIgnoreCase);
            Assert.False(record.AsSpan().IndexOf(key) >= 0);
        }

        Assert.All(logger.Entries, e =>
        {
            Assert.DoesNotContain(TocHex, e.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(PcHex, e.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string TocHexOrPc(byte[] key) => Convert.ToHexString(key);
}
