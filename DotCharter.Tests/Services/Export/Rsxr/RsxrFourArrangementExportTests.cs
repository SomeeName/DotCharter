using System.Text.Json;
using DotCharter.Services.Export;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rsxr;

/// <summary>
///     #1043: an extended 7-string Lead (Main), a stock Lead (Alternate), a Rhythm and a Bass. Both packages of the
///     mixed export must carry every arrangement they own with a unique, consistent path setup: one entry name means
///     one arrangement across the pair (the standard package used to reuse <c>key_lead</c> for the Alternate), and
///     every path has exactly one Main in each package.
/// </summary>
public sealed class RsxrFourArrangementExportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-rsxr-four-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Fact]
    public async Task Every_arrangement_keeps_a_unique_path_setup_in_both_packages()
    {
        var result = await BuildFourArrangementMix();

        var extended = Manifests(result.PsarcPath);
        var standard = Manifests(Assert.Single(result.StandardPackagePaths));

        Assert.Equal([1, 1, 2, 4], extended.Values.Select(m => m.Route).Order());
        Assert.Equal([1, 2, 4], standard.Values.Select(m => m.Route).Order());
        foreach (var package in new[] { extended, standard })
        {
            Assert.Equal(package.Count, package.Values.Select(m => m.PersistentId).Distinct().Count());
            Assert.Equal(package.Count, package.Values.Select(m => (m.Route, m.Partition)).Distinct().Count());
            Assert.All(package.Values, m => Assert.Equal(m.Representative, m.Represent));
            Assert.All(package.Values.GroupBy(m => m.Route), path => Assert.Single(path, m => m.Representative));
        }
        Assert.Single(extended.Values, m => m is { Route: 1, Extended: true, Representative: true });

        // An entry name both packages carry is the same arrangement in both.
        foreach (var (name, stock) in standard)
        {
            Assert.True(extended.TryGetValue(name, out var twin), name);
            Assert.Equal(twin.PersistentId, stock.PersistentId);
            Assert.Equal(twin.Partition, stock.Partition);
            Assert.False(stock.Extended);
        }
    }

    private async Task<RocksmithBuildResult> BuildFourArrangementMix()
    {
        var lead7 = RsxrTestSupport.Chart(false, 7);
        var project = lead7.Project;
        project.Arrangements[0].Name = "Lead";
        var alternate = RsxrTestSupport.Chart(false, 6).Project.Arrangements[0];
        alternate.Name = "Alt Lead";
        alternate.Subtype = ArrangementSubtype.Alternate;
        var rhythm = RsxrTestSupport.Chart(false, 6).Project.Arrangements[0];
        rhythm.Name = "Rhythm";
        rhythm.ArrangementType = ArrangementType.Rhythm;
        var bass = RsxrTestSupport.Chart(true, 4).Project.Arrangements[0];
        bass.Name = "Bass";
        foreach (var arrangement in new[] { alternate, rhythm, bass }) project.Arrangements.Add(arrangement);

        var audio = System.IO.Path.Combine(_directory, "audio.wem");
        var bytes = new byte[4000];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(audio, bytes);
        // No pinned identity: the ids are drawn as a shipped export draws them.
        return await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(ChartProjectCloner.Clone(project),
            new RocksmithBuildOptions
            {
                OutputPath = System.IO.Path.Combine(_directory, "four_p.psarc"), AudioPath = audio, PreviewAudioPath = audio,
                DlcKey = "FourArr", SongLengthMs = lead7.SongLengthMs, IncludeVocals = false, PlainPackage = true,
                Platform = ExportPlatform.Pc, WriteStandardPackage = true
            }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed record Manifest(string PersistentId, int Route, int Partition, bool Extended, bool Representative, bool Represent);

    // Keyed by the manifest's entry name; "Representative" lives only in the .hsan header, keyed by persistent id.
    private static Dictionary<string, Manifest> Manifests(string package)
    {
        var entries = RsxrTestSupport.Entries(package);
        var header = JsonDocument.Parse(entries.Single(e => e.Key.EndsWith(".hsan", StringComparison.Ordinal)).Value)
            .RootElement.GetProperty("Entries");
        var manifests = new Dictionary<string, Manifest>(StringComparer.Ordinal);
        foreach (var (name, data) in entries.Where(e => e.Key.EndsWith(".json", StringComparison.Ordinal)))
        {
            var attributes = JsonDocument.Parse(data).RootElement.GetProperty("Entries").EnumerateObject().Single().Value
                .GetProperty("Attributes");
            var id = attributes.GetProperty("PersistentID").GetString()!;
            var properties = attributes.GetProperty("ArrangementProperties");
            manifests[name] = new Manifest(id, properties.GetProperty("routeMask").GetInt32(),
                attributes.GetProperty("SongPartition").GetInt32(), attributes.TryGetProperty("ExtendedRange", out _),
                header.GetProperty(id).GetProperty("Attributes").GetProperty("Representative").GetInt32() == 1,
                properties.GetProperty("represent").GetInt32() == 1);
        }
        Assert.Equal(manifests.Count, header.EnumerateObject().Count());
        return manifests;
    }
}
