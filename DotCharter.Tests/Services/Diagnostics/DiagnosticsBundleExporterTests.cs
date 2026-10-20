using System.IO.Compression;
using System.Text;
using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Diagnostics;

/// <summary>
///     Coverage for the diagnostics-bundle assembly: secret redaction (token
///     shapes + sensitive key/value lines), the "credential stores never
///     travel" inclusion rule, unsafe-entry-name refusal, and a full gather →
///     zip round-trip against temp config/log roots.
/// </summary>
public sealed class DiagnosticsBundleExporterTests : IDisposable
{
    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "dc-diag-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_workDir);

    // ---- redaction ---------------------------------------------------------
    //
    // The redactor itself is SensitiveTextRedactorTests; these pin that the
    // BUNDLE actually runs it, over every part, with the OS account name.

    /// <summary>
    ///     The bundle is the artifact the app invites the user to attach to a bug
    ///     report. Until 2026-09-04 it went through a second, weaker redactor with
    ///     no user-name or home-path pass at all, so it shipped
    ///     <c>C:\Users\&lt;name&gt;</c> in system-info, every recent project path
    ///     out of appconfig.json, and up to 10 MB of log tail carrying LAN peer
    ///     endpoints. One synthetic bundle, all four leak shapes.
    /// </summary>
    [Fact]
    public void TheBundleNeverCarriesTheOsUserNameOrAHomePath()
    {
        var exporter = new DiagnosticsBundleExporter(userName: "alice");
        using var ms = new MemoryStream();

        exporter.WriteBundle(ms, new DiagnosticsBundleRequest([
            new DiagnosticsBundleEntry
            {
                EntryName = "system-info.txt",
                InlineText = """
                             config C:\Users\alice\AppData\Roaming\DotCharter
                             home /home/alice/.config/DotCharter
                             """
            },
            new DiagnosticsBundleEntry
            {
                EntryName = "appconfig.json",
                InlineText = """
                             {
                               "recentProjects": [ "C:\\Users\\alice\\Projects\\x.rscp" ],
                               "lanProfileName": "alice",
                               "Tone3000ClientId": "FAKE-tone3000-client-0000"
                             }
                             """
            },
            new DiagnosticsBundleEntry
            {
                EntryName = "logs/dotcharter.log",
                InlineText = """
                             2026-09-04 12:00:00.000 +02:00 [INFO] [Tone3000] refreshToken=FAKE-refresh-0000
                             2026-09-04 12:00:01.000 +02:00 [INFO] Served LAN song 'x' to 192.168.1.44:47815
                             2026-09-04 12:00:02.000 +02:00 [INFO] opened C:\Users\alice\Projects\x.rscp
                             """
            }
        ]));

        var systemInfo = ReadEntry(ms, "system-info.txt");
        var appConfig = ReadEntry(ms, "appconfig.json");
        var log = ReadEntry(ms, "logs/dotcharter.log");
        var all = systemInfo + appConfig + log;

        // 1. the OS account name — in a Windows path, a Unix home, a project path
        //    AND outside any path at all (the LAN profile name), which only the
        //    explicit user-name pass can reach.
        Assert.DoesNotContain("alice", all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"Users\" + SensitiveTextRedactor.UserPlaceholder, systemInfo);
        Assert.Contains("/home/" + SensitiveTextRedactor.UserPlaceholder, systemInfo);
        // 2. a TONE3000 token inside a Serilog-timestamped line (the anchored-regex hole)
        Assert.DoesNotContain("FAKE-refresh-0000", log);
        // 3. a client id inside a longer key, in appconfig.json
        Assert.DoesNotContain("FAKE-tone3000-client-0000", appConfig);

        // The bundle stays USEFUL: keys and timestamps survive, so the log still
        // reads as a log and the config still says which integrations are set up.
        Assert.Contains("Tone3000ClientId", appConfig);
        Assert.Contains("2026-09-04 12:00:01.000 +02:00", log);
    }

    [Fact]
    public void WriteBundle_RunsTheOneRedactor_OverInlineText()
    {
        var exporter = new DiagnosticsBundleExporter();
        using var ms = new MemoryStream();

        var result = exporter.WriteBundle(ms, new DiagnosticsBundleRequest([
            new DiagnosticsBundleEntry
            {
                EntryName = "system-info.txt",
                InlineText = "token=" + FakeSecrets.GitHubPat
            }
        ]));

        Assert.Equal(1, result.EntryCount);
        Assert.DoesNotContain(FakeSecrets.GitHubPat, ReadEntry(ms, "system-info.txt"));
    }

    // ---- inclusion / assembly ---------------------------------------------

    [Fact]
    public void WriteBundle_HonorsRedactFalse()
    {
        var exporter = new DiagnosticsBundleExporter();
        using var ms = new MemoryStream();

        exporter.WriteBundle(ms, new DiagnosticsBundleRequest([
            new DiagnosticsBundleEntry { EntryName = "raw.txt", InlineText = "password = keepme", Redact = false }
        ]));

        Assert.Contains("keepme", ReadEntry(ms, "raw.txt"));
    }

    [Theory]
    [InlineData("github-token.dat")]
    [InlineData("online-session.bin")]
    [InlineData("online-server-session.bin")]
    public void WriteBundle_RefusesCredentialFiles(string credentialName)
    {
        var credPath = Path.Combine(_workDir, credentialName);
        Directory.CreateDirectory(_workDir);
        File.WriteAllText(credPath, "super-secret-bytes");

        var exporter = new DiagnosticsBundleExporter();
        using var ms = new MemoryStream();

        var result = exporter.WriteBundle(ms, new DiagnosticsBundleRequest([
            new DiagnosticsBundleEntry { EntryName = "creds/" + credentialName, SourceFilePath = credPath }
        ]));

        Assert.Equal(0, result.EntryCount);
        Assert.Contains("creds/" + credentialName, result.SkippedEntries);
        Assert.Null(TryReadEntry(ms, "creds/" + credentialName));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("C:/rooted.txt")]
    [InlineData("a/../../b.txt")]
    public void WriteBundle_RefusesUnsafeEntryNames(string entryName)
    {
        var exporter = new DiagnosticsBundleExporter();
        using var ms = new MemoryStream();

        var result = exporter.WriteBundle(ms, new DiagnosticsBundleRequest([
            new DiagnosticsBundleEntry { EntryName = entryName, InlineText = "x" }
        ]));

        Assert.Equal(0, result.EntryCount);
        Assert.Contains(entryName, result.SkippedEntries);
    }

    [Fact]
    public void WriteBundle_SkipsMissingSourceFile()
    {
        var exporter = new DiagnosticsBundleExporter();
        using var ms = new MemoryStream();

        var result = exporter.WriteBundle(ms, new DiagnosticsBundleRequest([
            new DiagnosticsBundleEntry { EntryName = "gone.txt", SourceFilePath = Path.Combine(_workDir, "nope.txt") }
        ]));

        Assert.Equal(0, result.EntryCount);
        Assert.Contains("gone.txt", result.SkippedEntries);
    }

    [Fact]
    public void WriteBundle_WritesManifest()
    {
        var exporter = new DiagnosticsBundleExporter();
        using var ms = new MemoryStream();

        exporter.WriteBundle(ms, new DiagnosticsBundleRequest([
            new DiagnosticsBundleEntry { EntryName = "a.txt", InlineText = "hello" }
        ]));

        Assert.NotNull(TryReadEntry(ms, DiagnosticsBundleExporter.ManifestEntryName));
    }

    [Fact]
    public void Export_GathersConfigAndLogs_RedactedAndCredentialFree()
    {
        var configRoot = Path.Combine(_workDir, "config");
        var logRoot = Path.Combine(_workDir, "logs");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(logRoot);

        File.WriteAllText(Path.Combine(configRoot, "appconfig.json"),
            "{\n  \"onlineApiUrl\": \"https://example\",\n  \"secretToken\": \"" + FakeSecrets.GitHubPat + "\"\n}");
        // Credential store sitting in the config root must never be gathered
        // (Export only picks appconfig.json + logs, but assert the belt-and-braces).
        File.WriteAllText(Path.Combine(configRoot, "github-token.dat"), "tok");
        File.WriteAllText(Path.Combine(logRoot, "dotcharter.log"),
            "2026-07-10 INFO started\nusing Bearer abcdef1234567890XYZ header");

        var archivePath = Path.Combine(_workDir, "bundle.zip");
        var exporter = new DiagnosticsBundleExporter(configRoot, logRoot);

        var result = exporter.Export(archivePath, "OS: Windows\napiKey: shhh");

        Assert.True(File.Exists(archivePath));
        Assert.True(result.EntryCount >= 3); // system-info + appconfig + log

        using var zip = ZipFile.OpenRead(archivePath);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains(DiagnosticsBundleExporter.SystemInfoEntryName, names);
        Assert.Contains("appconfig.json", names);
        Assert.Contains("logs/dotcharter.log", names);
        Assert.DoesNotContain(names, n => n.Contains("github-token", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(FakeSecrets.GitHubPat, ReadZipEntry(zip, "appconfig.json"));
        Assert.DoesNotContain("abcdef1234567890XYZ", ReadZipEntry(zip, "logs/dotcharter.log"));
        Assert.DoesNotContain("shhh", ReadZipEntry(zip, DiagnosticsBundleExporter.SystemInfoEntryName));
    }

    /// <summary>
    ///     The bundle carries <c>appconfig.json</c> VERBATIM, so the redactor has
    ///     to cover every credential-ish field <c>AppConfig</c> really declares —
    ///     not a plausible sample. These three are the real ones, and none of them
    ///     normalized to any listed fragment: only "clientsecret" was listed,
    ///     never "clientid", so all three shipped in the clear. (A fourth,
    ///     <c>OnlineAnonKey</c>, went with the online backend in v0.60; the
    ///     redactor still lists "anonkey" for config files older builds wrote.)
    ///     <para>
    ///         Written against the SERIALIZED shape of a real
    ///         <c>AppConfig</c> so a renamed property cannot make this vacuous.
    ///     </para>
    /// </summary>
    [Fact]
    public void Export_RedactsEveryCredentialIshAppConfigField()
    {
        var configRoot = Path.Combine(_workDir, "config-real");
        var logRoot = Path.Combine(_workDir, "logs-real");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(logRoot);

        var config = new AppConfig
        {
            Tone3000ClientId = "FAKE-tone3000-client-0000"
        };
        var json = System.Text.Json.JsonSerializer.Serialize(config,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(configRoot, "appconfig.json"), json);
        File.WriteAllText(Path.Combine(logRoot, "dotcharter.log"), "2026-08-07 INFO started\n");

        var archivePath = Path.Combine(_workDir, "bundle-real.zip");
        new DiagnosticsBundleExporter(configRoot, logRoot).Export(archivePath, "OS: Windows");

        using var zip = ZipFile.OpenRead(archivePath);
        var bundled = ReadZipEntry(zip, "appconfig.json");

        Assert.DoesNotContain("FAKE-tone3000-client-0000", bundled);

        // The KEYS stay so the bundle still says WHICH integrations are set up —
        // an empty file would satisfy every assertion above.
        Assert.Contains("Tone3000ClientId", bundled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SensitiveTextRedactor.Redacted, bundled);
    }

    // ---- helpers -----------------------------------------------------------

    private static string ReadEntry(MemoryStream ms, string entryName) => TryReadEntry(ms, entryName) ?? throw new Xunit.Sdk.XunitException($"entry '{entryName}' missing");

    private static string? TryReadEntry(MemoryStream ms, string entryName)
    {
        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName);
        if (entry == null) return null;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ReadZipEntry(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName)!;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
