using System.Text.Json;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Configuration;

/// <summary>
///     #774: the Rocksmith keys live in appconfig.json's <c>RocksmithKeys</c> — written
///     EMPTY by default so the user sees where to paste, read by the product through
///     the configuration service, validated per value (a malformed one is ignored and
///     named, never quoted, in the log), and moved ONCE out of the #756 builds'
///     <c>rocksmith-keys.txt</c>. Every key here is synthetic.
/// </summary>
[Collection("CwdSerialized")]
public sealed partial class AppConfigRocksmithKeysTests : IDisposable
{
    private static readonly string TocHex = Convert.ToHexString(TestRocksmithKeys.SyntheticToc);
    private static readonly string PcHex = Convert.ToHexString(TestRocksmithKeys.SyntheticSngPc);
    private static readonly string MacHex = Convert.ToHexString(TestRocksmithKeys.SyntheticSngMac);

    private readonly string _originalCwd;
    private readonly string _tempDir;
    private readonly List<JsonConfigurationService> _services = [];

    public AppConfigRocksmithKeysTests()
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
    private static string LegacyPath => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "rocksmith-keys.txt");

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

    private static string KeysJson(string toc, string pc, string mac) =>
        $$"""{ "LanguageCulture": "system", "RocksmithKeys": { "PsarcToc": "{{toc}}", "SngPc": "{{pc}}", "SngMac": "{{mac}}" } }""";

    private static JsonElement SavedKeys()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        return json.RootElement.GetProperty("RocksmithKeys").Clone();
    }
}
