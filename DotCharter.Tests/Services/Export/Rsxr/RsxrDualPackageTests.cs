using System.Text;
using System.Text.Json;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rsxr;

/// <summary>
///     #898: a mixed export writes the RS Extended Range package as before and a standard package with the stock
///     arrangements alone, byte-equal to a stock-only export. The RS Extended Range package also has a plain Mac form.
/// </summary>
public sealed class RsxrDualPackageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-rsxr-dual-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Fact]
    public async Task A_mixed_export_also_writes_the_standard_package_equal_to_a_stock_only_export()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var (project, length) = Mixed();
        var stockName = project.Arrangements[0].Name;

        var dual = await Build(project, length, "dual", ExportPlatform.PcAndMac, standard: true);
        var stock = await Build(project, length, "stock", ExportPlatform.PcAndMac, standard: false, filter: [stockName]);

        Assert.Equal([Path("dual_rsxr_p.psarc"), Path("dual_rsxr_m.psarc"), Path("dual_p.psarc"), Path("dual_m.psarc")],
            dual.PackagePaths);
        Assert.Equal([Path("dual_p.psarc"), Path("dual_m.psarc")], dual.StandardPackagePaths);
        Assert.Equal([Path("stock_p.psarc"), Path("stock_m.psarc")], stock.PackagePaths);
        Assert.False(stock.IsPsxr);
        Assert.Equal(File.ReadAllBytes(Path("stock_p.psarc")), File.ReadAllBytes(Path("dual_p.psarc")));
        Assert.Equal(File.ReadAllBytes(Path("stock_m.psarc")), File.ReadAllBytes(Path("dual_m.psarc")));
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(Path("dual_p.psarc"))[..4]);
    }

    [Fact]
    public async Task The_extended_package_is_unchanged_and_shares_the_stock_arrangement_ids()
    {
        var (project, length) = Mixed();
        var dual = await Build(project, length, "dual", ExportPlatform.Pc, standard: true, plain: true);
        var single = await Build(project, length, "single", ExportPlatform.Pc, standard: false, plain: true);

        Assert.Single(single.PackagePaths);
        Assert.Equal(File.ReadAllBytes(single.PsarcPath), File.ReadAllBytes(dual.PsarcPath));
        var extended = RsxrTestSupport.Entries(dual.PsarcPath);
        var standard = RsxrTestSupport.Entries(dual.StandardPackagePaths[0]);
        Assert.Equal(2, extended.Keys.Count(n => n.EndsWith(".sng", StringComparison.Ordinal)));
        Assert.Single(standard.Keys, n => n.EndsWith(".sng", StringComparison.Ordinal));
        var stockManifest = standard.Keys.Single(n => n.EndsWith(".json", StringComparison.Ordinal));
        Assert.Equal(PersistentId(standard[stockManifest]), PersistentId(extended[stockManifest]));
        Assert.Equal(DlcKey(standard[stockManifest]), DlcKey(extended[stockManifest]));
    }

    [Fact]
    public async Task A_stock_main_beside_an_extended_main_on_its_path_ships_as_alternate_only_in_the_extended_package()
    {
        var lead6 = RsxrTestSupport.Chart(false, 6);
        var lead7 = RsxrTestSupport.Chart(false, 7).Project.Arrangements[0];
        var bass4 = RsxrTestSupport.Chart(true, 4).Project.Arrangements[0];
        lead7.Name = "Lead7";
        bass4.Name = "Bass4";
        var project = lead6.Project;
        project.Arrangements.Add(lead7);
        project.Arrangements.Add(bass4);
        Assert.All(project.Arrangements, a => Assert.Equal(ArrangementSubtype.Main, a.Subtype));

        var dual = await Build(project, lead6.SongLengthMs, "mains", ExportPlatform.Pc, standard: true, plain: true, arrangements: 3);

        var extended = Manifests(dual.PsarcPath);
        Assert.Equal(3, extended.Count);
        var lead = Assert.Single(extended, m => m.Name == "Lead" && m.Representative);
        Assert.True(lead.Extended);
        Assert.Contains(extended, m => m is { Name: "Lead", Extended: false, Representative: false, Represent: false, Bonus: false });
        // Bass has no extended duplicate: it stays Main.
        Assert.Contains(extended, m => m is { Name: "Bass", Representative: true, Represent: true });

        var standard = Manifests(Assert.Single(dual.StandardPackagePaths));
        Assert.Equal(2, standard.Count);
        Assert.All(standard, m => Assert.True(m is { Extended: false, Representative: true, Represent: true, Bonus: false }));
        Assert.All(project.Arrangements, a => Assert.Equal(ArrangementSubtype.Main, a.Subtype));
    }

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 5)]
    public async Task A_single_kind_of_content_writes_one_package_as_today(bool bass, int strings)
    {
        var built = RsxrTestSupport.Chart(bass, strings);
        var result = await Build(built.Project, built.SongLengthMs, "one", ExportPlatform.Pc, standard: true, plain: true);
        Assert.Single(result.PackagePaths);
        Assert.Empty(result.StandardPackagePaths);
    }

    [Fact]
    public async Task The_option_off_writes_only_the_extended_package()
    {
        var (project, length) = Mixed();
        var result = await Build(project, length, "off", ExportPlatform.Pc, standard: false, plain: true);
        Assert.Equal([Path("off_rsxr_p.psarc")], result.PackagePaths);
        Assert.False(File.Exists(Path("off_p.psarc")));
    }

    [Fact]
    public async Task The_mac_extended_package_has_the_mac_layout_and_the_pc_charts()
    {
        var built = RsxrTestSupport.Chart(true, 5);
        var result = await Build(built.Project, built.SongLengthMs, "pair", ExportPlatform.PcAndMac, standard: true, plain: false);
        Assert.Equal([Path("pair_rsxr_p.psarc"), Path("pair_rsxr_m.psarc")], result.PackagePaths);

        var pcBytes = File.ReadAllBytes(result.PackagePaths[0]);
        var macBytes = File.ReadAllBytes(result.PackagePaths[1]);
        Assert.Equal(RsxrTestSupport.PackageMagic(true), macBytes[..4]);
        var pc = RsxrTestSupport.Entries(result.PackagePaths[0]);
        var mac = RsxrTestSupport.Entries(result.PackagePaths[1]);
        Assert.Equal(pc.Keys.Select(MacName).Order(StringComparer.Ordinal), mac.Keys.Order(StringComparer.Ordinal));
        foreach (var sng in pc.Keys.Where(n => n.EndsWith(".sng", StringComparison.Ordinal)))
        {
            Assert.Equal(pc[sng], mac[MacName(sng)]);
            Assert.Equal("SNXR", Encoding.ASCII.GetString(RsxrTestSupport.Inflate(mac[MacName(sng)]), 0, 4));
        }
        foreach (var manifest in pc.Keys.Where(n => n.EndsWith(".json", StringComparison.Ordinal) || n.EndsWith(".hsan", StringComparison.Ordinal)))
            Assert.Equal(pc[manifest], mac[manifest]);
        Assert.Contains(mac.Keys, n => n.StartsWith("audio/mac/", StringComparison.Ordinal));
        Assert.NotEqual(pcBytes, macBytes);
    }

    [Fact]
    public async Task A_mac_only_extended_export_writes_the_tagged_mac_package()
    {
        var built = RsxrTestSupport.Chart(false, 7);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, Audio(), Path("only_m.psarc"), ExportPlatform.Mac);
        Assert.Equal([Path("only_rsxr_m.psarc")], result.PackagePaths);
        Assert.Equal(RsxrTestSupport.PackageMagic(true), File.ReadAllBytes(result.PsarcPath)[..4]);
        Assert.Contains(RsxrTestSupport.Entries(result.PsarcPath).Keys, n => n.StartsWith("songs/bin/macos/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("song_m.psarc", true, "song_rsxr_m.psarc")]
    [InlineData("song_rsxr_m.psarc", false, "song_m.psarc")]
    [InlineData("song_rsxr_m.psarc", true, "song_rsxr_m.psarc")]
    [InlineData("song_p.psarc", true, "song_p.psarc")]
    public void The_mac_name_carries_the_tag_exactly_when_asked(string input, bool tagged, string expected) =>
        Assert.Equal(expected, ExtendedRangePackageName.ApplyMac(input, tagged));

    private static string MacName(string entry) => entry
        .Replace("songs/bin/generic/", "songs/bin/macos/", StringComparison.Ordinal)
        .Replace("audio/windows/", "audio/mac/", StringComparison.Ordinal);

    // "Representative" lives only in the .hsan header, keyed by persistent id; the flags live in each manifest.
    private static List<(string Name, bool Extended, bool Representative, bool Represent, bool Bonus)> Manifests(string package)
    {
        var entries = RsxrTestSupport.Entries(package);
        var header = JsonDocument.Parse(entries.Single(e => e.Key.EndsWith(".hsan", StringComparison.Ordinal)).Value)
            .RootElement.GetProperty("Entries");
        return [.. entries.Where(e => e.Key.EndsWith(".json", StringComparison.Ordinal)).Select(e => Attributes(e.Value))
            .Where(a => a.GetProperty("ArrangementName").GetString() != "Vocals")
            .Select(a => (a.GetProperty("ArrangementName").GetString()!, a.TryGetProperty("ExtendedRange", out _),
                header.GetProperty(a.GetProperty("PersistentID").GetString()!).GetProperty("Attributes")
                    .GetProperty("Representative").GetInt32() == 1,
                a.GetProperty("ArrangementProperties").GetProperty("represent").GetInt32() == 1,
                a.GetProperty("ArrangementProperties").GetProperty("bonusArr").GetInt32() == 1))];
    }

    private static string PersistentId(byte[] manifest) => Attributes(manifest).GetProperty("PersistentID").GetString()!;

    private static string DlcKey(byte[] manifest) => Attributes(manifest).GetProperty("DLCKey").GetString()!;

    private static JsonElement Attributes(byte[] manifest)
    {
        var entries = JsonDocument.Parse(manifest).RootElement.GetProperty("Entries");
        return entries.EnumerateObject().Single().Value.GetProperty("Attributes");
    }

    private static (ChartProject Project, double Length) Mixed()
    {
        var guitar = RsxrTestSupport.Chart(false, 6);
        var bass = RsxrTestSupport.Chart(true, 5);
        var arrangement = bass.Project.Arrangements[0];
        arrangement.Name = "Bass5";
        guitar.Project.Arrangements.Add(arrangement);
        return (guitar.Project, Math.Max(guitar.SongLengthMs, bass.SongLengthMs));
    }

    private Task<RocksmithBuildResult> Build(ChartProject project, double length, string name, ExportPlatform platform,
        bool standard, bool? plain = null, IReadOnlyCollection<string>? filter = null, int arrangements = 2) =>
        new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(ChartProjectCloner.Clone(project),
            new RocksmithBuildOptions
            {
                OutputPath = Path(name + "_p.psarc"), AudioPath = Audio(), PreviewAudioPath = Audio(), DlcKey = "RsxrDual",
                SongLengthMs = length, IncludeVocals = false, Identity = RsxrTestSupport.Identity(arrangements),
                PlainPackage = plain, Platform = platform, WriteStandardPackage = standard, ArrangementFilter = filter
            }, cancellationToken: TestContext.Current.CancellationToken);

    private string Path(string file) => System.IO.Path.Combine(_directory, file);

    private string Audio()
    {
        var path = Path("audio.wem");
        if (File.Exists(path)) return path;
        var bytes = new byte[4000];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
