using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Infrastructure.Psarc.Write;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrPackageExportTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-rsxr-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData(false, 6)]
    [InlineData(false, 7)]
    [InlineData(false, 8)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    public async Task Packages_preserve_stock_entries_and_describe_extended_charts(bool bass, int strings)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var built = RsxrTestSupport.Chart(bass, strings);
        await Compare(built.Project, built.SongLengthMs);
    }

    [Fact]
    public async Task A_stock_guitar_inside_an_extended_bass_package_keeps_every_entry()
    {
        var built = RsxrTestSupport.Chart(false, 6);
        var bass = RsxrTestSupport.Chart(true, 5);
        built.Project.Arrangements.Add(bass.Project.Arrangements[0]);
        await Compare(built.Project, Math.Max(built.SongLengthMs, bass.SongLengthMs));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rsxr_always_writes_plain_despite_the_package_option(bool? plain)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var built = RsxrTestSupport.Chart(true, 5);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, Audio(),
            Path.Combine(_directory, "plain_p.psarc"), plain: plain);
        var entries = RsxrTestSupport.Entries(result.PsarcPath);
        Assert.Single(result.Warnings, w => w.Contains("RS Extended Range format", StringComparison.Ordinal));
        foreach (var name in entries.Keys.Where(n => n.EndsWith(".sng", StringComparison.Ordinal)))
            Assert.Equal("SNXR", Encoding.ASCII.GetString(RsxrTestSupport.Inflate(entries[name]), 0, 4));
    }

    private async Task Compare(ChartProject project, double lengthMs)
    {
        var result = await RsxrTestSupport.Build(project, lengthMs, Audio(), Path.Combine(_directory, "new_p.psarc"));
        var hasExtended = project.Arrangements.Any(RsxrStringLayout.IsExtended);
        Assert.Equal(RsxrTestSupport.PackageMagic(hasExtended), File.ReadAllBytes(result.PsarcPath)[..4]);
        Assert.Equal(hasExtended, result.IsPsxr);
        var entries = RsxrTestSupport.Entries(result.PsarcPath);
        foreach (var (name, bytes) in entries)
        {
            if (name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".hsan", StringComparison.Ordinal))
                CompareManifest(bytes, project);
            if (!name.EndsWith(".sng", StringComparison.Ordinal)) continue;
            var partition = Path.GetFileNameWithoutExtension(name).Split('_')[^1];
            var arrangement = project.Arrangements.Single(a => (a.ArrangementType == ArrangementType.Bass ? "bass" : "lead") == partition);
            var body = RsxrTestSupport.Inflate(bytes);
            if (RsxrStringLayout.IsExtended(arrangement))
            {
                Assert.Equal("SNXR", Encoding.ASCII.GetString(body, 0, 4));
            }
            else
            {
                var stock = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, lengthMs, RsxrTestSupport.ConversionTime).Sng;
                Assert.Equal(RocksmithSngWriter.WriteUnpacked(stock), body);
            }
        }
    }

    private static void CompareManifest(byte[] bytes, ChartProject project)
    {
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("\\u", text, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(bytes);
        foreach (var entry in document.RootElement.GetProperty("Entries").EnumerateObject())
        {
            var attr = entry.Value.GetProperty("Attributes");
            var bass = attr.GetProperty("ArrangementName").GetString() == "Bass";
            var arrangement = project.Arrangements.Single(a => (a.ArrangementType == ArrangementType.Bass) == bass);
            if (!RsxrStringLayout.IsExtended(arrangement))
            {
                Assert.False(attr.TryGetProperty("ExtendedRange", out _));
                Assert.Equal(6, attr.GetProperty("Tuning").EnumerateObject().Count());
                continue;
            }
            var marker = attr.GetProperty("ExtendedRange");
            Assert.Equal("Version", Assert.Single(marker.EnumerateObject()).Name);
            Assert.Equal(1, marker.GetProperty("Version").GetInt32());
            var names = attr.EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(names.IndexOf("Tuning") - 1, names.IndexOf("ExtendedRange"));
            var tuning = attr.GetProperty("Tuning");
            Assert.Equal(Enumerable.Range(0, arrangement.StringCount).Select(i => $"string{i}"), tuning.EnumerateObject().Select(p => p.Name));
            Assert.Equal(RsxrStringLayout.TuningOffsets(arrangement), tuning.EnumerateObject().Select(p => p.Value.GetInt32()));
            Assert.DoesNotContain(tuning.EnumerateObject(), p => p.Name.StartsWith("Rs7String", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(false, 8)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    public void Extended_manifests_and_headers_have_the_positional_marker_and_tuning(bool bass, int strings)
    {
        var built = RsxrTestSupport.Chart(bass, strings);
        var domain = built.Project.Arrangements[0];
        var build = SngFromDomainMapper.BuildInstrumental(domain, built.Project.Beats, built.SongLengthMs, RsxrTestSupport.ConversionTime);
        var arrangement = new RsPackageArrangement
        {
            ArrangementName = bass ? "Bass" : "Lead", PartitionName = bass ? "bass" : "lead",
            PersistentId = Guid.Empty, Sng = build.Sng, NoteCounts = build.NoteCounts, Domain = domain
        };
        var meta = new RsPackageMetadata { DlcKey = "Positional", Title = "Positional", Artist = "Synthetic" };
        CompareManifest(Encoding.UTF8.GetBytes(RsManifestGenerator.CreateArrangementManifest(meta, arrangement)), built.Project);
        CompareManifest(Encoding.UTF8.GetBytes(RsManifestGenerator.CreateHeader(meta, [arrangement])), built.Project);
    }

    [Fact]
    public async Task Pc_and_mac_write_both_plain_extended_packages_without_keys()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var built = RsxrTestSupport.Chart(false, 7);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, Audio(),
            Path.Combine(_directory, "pair_p.psarc"), ExportPlatform.PcAndMac);
        Assert.Equal([Path.Combine(_directory, "pair_rsxr_p.psarc"), Path.Combine(_directory, "pair_rsxr_m.psarc")], result.PackagePaths);
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("Only the PC package was written:", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_directory, "pair_m.psarc")));
    }

    [Theory]
    [InlineData(false, 9, false)]
    [InlineData(true, 7, false)]
    [InlineData(false, 7, true)]
    public async Task A_refused_arrangement_is_skipped_next_to_a_good_one_and_is_the_failure_reason_when_alone(bool bass, int strings, bool tuning)
    {
        var built = RsxrTestSupport.Chart(bass, bass ? 6 : 7);
        var refused = built.Project.Arrangements[0];
        refused.Name = "Refused";
        refused.StringCount = strings;
        if (tuning) refused.TuningPitches[0] = new TuningOffset(25);
        var sentence = RsxrStringLayout.ExportRefusal(refused)!;
        var audio = Audio();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RsxrTestSupport.Build(built.Project, built.SongLengthMs,
            audio, Path.Combine(_directory, "refused_p.psarc")));
        Assert.Contains(sentence, error.Message, StringComparison.Ordinal);
        built.Project.Arrangements.Add(RsxrTestSupport.Chart(!bass, bass ? 6 : 4).Project.Arrangements[0]);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio,
            Path.Combine(_directory, "accepted_p.psarc"));
        Assert.Single(result.ArrangementNames);
        Assert.Contains(result.Warnings, w => w.StartsWith("Arrangement 'Refused'", StringComparison.Ordinal) && w.Contains(sentence, StringComparison.Ordinal));
    }

    [Fact]
    public void Psxr_magic_requires_a_plain_table_and_changes_only_the_magic()
    {
        using var old = new MemoryStream();
        using var native = new MemoryStream();
        PsarcWriter.Write(old, [new PsarcWriteEntry("entry", [1, 2, 3])], encryptToc: false);
        PsarcWriter.Write(native, [new PsarcWriteEntry("entry", [1, 2, 3])], encryptToc: false, extendedRange: true);
        Assert.Equal(RsxrTestSupport.PackageMagic(true), native.ToArray()[..4]);
        Assert.Equal(old.ToArray()[4..], native.ToArray()[4..]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(native.ToArray().AsSpan(28)));
        using var invalid = new MemoryStream();
        Assert.Throws<ArgumentException>(() => PsarcWriter.Write(invalid, [], extendedRange: true));
        Assert.Equal(0, invalid.Length);
    }

    private string Audio()
    {
        var path = Path.Combine(_directory, "audio.wem");
        if (File.Exists(path)) return path;
        var bytes = new byte[4000];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
