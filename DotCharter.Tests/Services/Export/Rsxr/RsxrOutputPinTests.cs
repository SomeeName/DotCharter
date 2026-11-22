using System.Security.Cryptography;
using System.Text.Json;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tests.Services.Export.Rs7;
using DotCharter.Tests.Services.Export.Rs7.Suite;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rsxr;

/// <summary>Pins complete catalog packages and their chart, manifest, header and arrangement XML bytes.</summary>
public sealed class RsxrOutputPinTests : IDisposable
{
    private const string FixturePath = "DotCharter.Tests/Services/Export/Rsxr/rsxr-output-pins.json";
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-rsxr-pin-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Fact]
    public async Task Every_catalog_package_and_arrangement_matches_its_frozen_output()
    {
        var actual = new Dictionary<string, OutputPin>(StringComparer.Ordinal);
        foreach (var spec in Rs7TestSuiteCatalog.All)
            actual.Add(spec.Name, await Capture(spec));

        var fixture = Path.Combine(TestPaths.RepoRoot(), FixturePath);
        if (Environment.GetEnvironmentVariable("DOTCHARTER_REGEN_RSXR_PINS") == "1")
            File.WriteAllText(fixture, JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n");

        var expected = JsonSerializer.Deserialize<Dictionary<string, OutputPin>>(File.ReadAllText(fixture))!;
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (name, pin) in expected)
            Compare(name, pin, actual[name]);
    }

    private async Task<OutputPin> Capture(ChartSpec spec)
    {
        var built = Rs7TestSuiteBuilder.Build(spec);
        var folder = Directory.CreateDirectory(Path.Combine(_directory, spec.Name)).FullName;
        var audio = Path.Combine(folder, "audio.wav");
        Rs7TestAudio.WriteClickWav(audio, built.SongLengthMs + Rs7TestAudio.OutroMs);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio,
            Path.Combine(folder, "Rsxr_p.psarc"), dlcKey: Rs7TestSuiteExpected.DlcKey(spec));
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("Arrangement '", StringComparison.Ordinal) &&
            w.Contains("' was skipped: ", StringComparison.Ordinal));
        Assert.Equal(spec.Parts.Length, result.ArrangementNames.Count);
        var written = ExtendedRangePackageName.Apply(Path.Combine(folder, spec.Name + "_p.psarc"), result.IsPsxr);
        File.Copy(result.PsarcPath, written);
        var bytes = File.ReadAllBytes(written);
        var entries = RsxrTestSupport.Entries(written);
        var bodies = entries.Where(e => e.Key.EndsWith(".sng", StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => Hash(RsxrTestSupport.Inflate(e.Value)));
        var manifests = entries.Where(e => e.Key.EndsWith(".json", StringComparison.Ordinal) || e.Key.EndsWith(".hsan", StringComparison.Ordinal))
            .ToDictionary(e => e.Key, e => Hash(e.Value));
        Assert.Equal(spec.Parts.Length, bodies.Count);
        Assert.Equal(spec.Parts.Length + 1, manifests.Count);
        var xml = built.Project.Arrangements.ToDictionary(a => a.Name, a => XmlHash(built, a));
        return new OutputPin(Path.GetFileName(written), Convert.ToHexStringLower(bytes.AsSpan(0, 4)), Hash(bytes), bodies, manifests, xml);
    }

    private static string XmlHash(BuiltChart built, Arrangement arrangement)
    {
        var project = ChartProjectCloner.Clone(built.Project);
        var selected = project.Arrangements.Single(a => a.Name == arrangement.Name);
        project.Arrangements.Clear();
        project.Arrangements.Add(selected);
        var document = new RsXmlSongWriter().Write(project, built.SongLengthMs);
        // The XML writer stamps the wall clock; every other emitted byte is pinned.
        document.Root!.Element("lastConversionDateTime")!.Value = "11-01-26 12:00";
        using var output = new MemoryStream();
        document.Save(output);
        return Hash(output.ToArray());
    }

    private static void Compare(string name, OutputPin expected, OutputPin actual)
    {
        Assert.Equal(expected.FileName, actual.FileName);
        Assert.Equal(expected.MagicHex, actual.MagicHex);
        CompareHashes(name + ": chart body", expected.ChartBodies, actual.ChartBodies);
        CompareHashes(name + ": manifest/header", expected.Manifests, actual.Manifests);
        CompareHashes(name + ": XML", expected.ArrangementXml, actual.ArrangementXml);
        Assert.True(expected.PackageHash == actual.PackageHash,
            $"{name}: package SHA-256 {actual.PackageHash} != pinned {expected.PackageHash}");
    }

    private static void CompareHashes(string label, Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var (entry, hash) in expected)
            Assert.True(hash == actual[entry], $"{label} {entry}: SHA-256 {actual[entry]} != pinned {hash}");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record OutputPin(string FileName, string MagicHex, string PackageHash,
        Dictionary<string, string> ChartBodies, Dictionary<string, string> Manifests, Dictionary<string, string> ArrangementXml);
}
