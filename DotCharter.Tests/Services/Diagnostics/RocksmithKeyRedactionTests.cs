using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Diagnostics;
using DotCharter.Services.Infrastructure.CrashReporting;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Diagnostics;

/// <summary>
///     #774: the Rocksmith keys sit in appconfig.json, and appconfig.json travels in
///     the diagnostics bundle. Their names are not secret-shaped and a pasted value
///     may hold spaces, so the ONE redactor knows them by name. Synthetic keys only.
/// </summary>
public sealed class RocksmithKeyRedactionTests : IDisposable
{
    private static readonly string TocHex = Convert.ToHexString(TestRocksmithKeys.SyntheticToc);
    private static readonly string PcHex = Convert.ToHexString(TestRocksmithKeys.SyntheticSngPc);

    /// <summary>The PC key as a user may paste it: lower-case pairs split by spaces,
    /// which neither the long-hex fallback nor the key=value pass removes whole.</summary>
    private static readonly string PcSpaced = string.Join(' ', PcHex.ToLowerInvariant().Chunk(2).Select(p => new string(p)));

    private readonly string _workDir = Directory.CreateTempSubdirectory("dc-rskey-redact-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_workDir);

    private static string ConfigJson() =>
        JsonSerializer.Serialize(new AppConfig { RocksmithKeys = new RocksmithKeysConfig { PsarcToc = TocHex, SngPc = PcSpaced } },
            new JsonSerializerOptions { WriteIndented = true });

    private static void AssertNoKeyText(string text)
    {
        Assert.DoesNotContain(TocHex, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PcHex, text, StringComparison.OrdinalIgnoreCase);
        // Not even a fragment of the spaced paste survives.
        Assert.DoesNotContain(PcSpaced[..8], text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PcSpaced[^8..], text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_redactor_blanks_each_key_value_and_keeps_an_empty_one_visible()
    {
        var redacted = SensitiveTextRedactor.Redact(ConfigJson());

        AssertNoKeyText(redacted);
        Assert.Contains($"\"PsarcToc\": \"{SensitiveTextRedactor.Redacted}\"", redacted, StringComparison.Ordinal);
        Assert.Contains($"\"SngPc\": \"{SensitiveTextRedactor.Redacted}\"", redacted, StringComparison.Ordinal);
        Assert.Contains("\"SngMac\": \"\"", redacted, StringComparison.Ordinal); // "no key configured" stays readable
        Assert.Equal(redacted, SensitiveTextRedactor.Redact(redacted)); // idempotent
    }

    [Fact]
    public void Old_key_file_lines_and_log_lines_are_blanked_too()
    {
        var text = $"psarc-toc = {TocHex}\nsng-pc={PcSpaced}\n" +
                   $"2026-10-13 12:00:00.000 +02:00 [INFO] SngPc: {PcSpaced}\n";

        var redacted = SensitiveTextRedactor.Redact(text);

        AssertNoKeyText(redacted);
        Assert.Contains("psarc-toc = " + SensitiveTextRedactor.Redacted, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void The_diagnostics_bundle_carries_appconfig_without_the_keys()
    {
        var configRoot = Directory.CreateDirectory(Path.Combine(_workDir, "config")).FullName;
        var logRoot = Directory.CreateDirectory(Path.Combine(_workDir, "logs")).FullName;
        File.WriteAllText(Path.Combine(configRoot, "appconfig.json"), ConfigJson());
        File.WriteAllText(Path.Combine(logRoot, "dotcharter.log"), $"2026-10-13 INFO SngPc={PcSpaced}\n");

        var archivePath = Path.Combine(_workDir, "bundle.zip");
        new DiagnosticsBundleExporter(configRoot, logRoot).Export(archivePath, "OS: Windows");

        using var zip = ZipFile.OpenRead(archivePath);
        var config = Read(zip, "appconfig.json");
        AssertNoKeyText(config);
        Assert.Contains("\"RocksmithKeys\"", config, StringComparison.Ordinal); // the section is there, blanked
        foreach (var entry in zip.Entries) AssertNoKeyText(Read(zip, entry.FullName));
    }

    [Fact]
    public void The_crash_report_blanks_a_key_that_reached_the_log_tail()
    {
        var report = CrashReportBuilder.Build(new CrashReportInput
        {
            Source = "AppDomain",
            ExceptionType = "System.Exception",
            ExceptionMessage = "boom",
            RecentLogLines = [$"[INFO] PsarcToc: {TocHex}", $"[INFO] \"SngPc\": \"{PcSpaced}\""]
        });

        AssertNoKeyText(report);
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
