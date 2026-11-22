using System.Text;
using System.Buffers.Binary;
using DotCharter.Services.Export;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrPackageNameTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-names-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData("song_p.psarc", true, "song_rsxr_p.psarc")]
    [InlineData("song_rsxr_p.psarc", true, "song_rsxr_p.psarc")]
    [InlineData("song_rsxr_rsxr_p.psarc", true, "song_rsxr_p.psarc")]
    [InlineData("song_rsxr_rsxr_p.psarc", false, "song_p.psarc")]
    [InlineData("song_RSXR_P.PSARC", false, "song_P.PSARC")]
    [InlineData("song_P.PSARC", true, "song_rsxr_P.PSARC")]
    [InlineData("song_p.psarc", false, "song_p.psarc")]
    [InlineData("song_rsxr_m.psarc", false, "song_rsxr_m.psarc")]
    [InlineData("song_p.psarc.building", true, "song_p.psarc.building")]
    [InlineData("song.psarc", false, "song.psarc")]
    [InlineData("song", true, "song")]
    [InlineData("", true, "")]
    public void Apply_normalizes_only_the_pc_suffix_and_is_idempotent(string name, bool tagged, string expected)
    {
        var path = Path.Combine(_directory, name);
        var result = ExtendedRangePackageName.Apply(path, tagged);
        Assert.Equal(Path.Combine(_directory, expected), result);
        Assert.Equal(result, ExtendedRangePackageName.Apply(result, tagged));
    }

    [Fact]
    public void The_predicate_uses_only_accepted_extended_arrangements_in_the_selected_format()
    {
        var stock = RsxrTestSupport.Chart(false, 6).Project.Arrangements[0];
        var extended = RsxrTestSupport.Chart(true, 5).Project.Arrangements[0];
        Assert.True(ExtendedRangePackageName.IsPsxr([stock, extended]));
        Assert.False(ExtendedRangePackageName.IsPsxr([stock]));
        extended.TuningPitches[0] = new TuningOffset(25);
        Assert.False(ExtendedRangePackageName.IsPsxr([stock, extended]));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public async Task Builder_tags_extended_content_and_preserves_stock_paths(
        bool extended, bool mixed, bool taggedInput)
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var built = RsxrTestSupport.Chart(false, extended ? 7 : 6);
        if (mixed) built.Project.Arrangements.Add(RsxrTestSupport.Chart(true, 4).Project.Arrangements[0]);
        var path = Path.Combine(_directory, taggedInput ? "song_rsxr_p.psarc" : "song_p.psarc");
        var psxr = extended;
        var expected = ExtendedRangePackageName.Apply(path, psxr);
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);

        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio, path);

        Assert.Equal(expected, result.PsarcPath);
        Assert.Equal(psxr, result.IsPsxr);
        Assert.Equal(expected, Assert.Single(result.PackagePaths));
        Assert.True(File.Exists(expected));
        Assert.Equal(RsxrTestSupport.PackageMagic(psxr), File.ReadAllBytes(expected)[..4]);
        if (path != expected) Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Test_export_finds_both_names_and_versions(bool running)
    {
        var old = Path.Combine(_directory, "songkey_p.psarc");
        var tagged = Path.Combine(_directory, "songkey_v4_rsxr_p.psarc");
        var foreign = Path.Combine(_directory, "songkey_other_rsxr_p.psarc");
        var plan = RocksmithTestExport.Plan("SongKey", [old, tagged, foreign,
            Path.Combine(_directory, "songkey_rsxr_m.psarc"), Path.Combine(_directory, "songkey_rsxr.psarc")], running);
        Assert.Equal(running ? "songkey_v5" : "songkey", plan.FileStem);
        Assert.Equal(running ? [] : new[] { old, tagged }, plan.Superseded);
    }

    [Fact]
    public void Platform_suffix_drops_the_pc_tag_on_mac_and_never_duplicates_it()
    {
        var pc = Path.Combine(_directory, "song_rsxr_p.psarc");
        var mac = Path.Combine(_directory, "song_m.psarc");
        Assert.Equal(pc, ExportPathDefaults.WithPlatformSuffix(pc, false));
        Assert.Equal(mac, ExportPathDefaults.WithPlatformSuffix(pc, true));
        Assert.Equal(Path.Combine(_directory, "song_p.psarc"), ExportPathDefaults.WithPlatformSuffix(mac, false));
        Assert.Equal(pc, ExportPathDefaults.WithPlatformSuffix(Path.Combine(_directory, "song_rsxr_rsxr_p.psarc"), false));
        Assert.Equal(mac, ExportPathDefaults.WithPlatformSuffix(Path.Combine(_directory, "song_rsxr_m.psarc"), true));
    }

    [Theory]
    [InlineData("song_rsxr_m.psarc")]
    [InlineData("song_rsxr_p.psarc")]
    public async Task Mac_only_stock_content_writes_the_requested_path(string name)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var built = RsxrTestSupport.Chart(false, 6);
        var requested = Path.Combine(_directory, name);
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);

        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio, requested,
            ExportPlatform.Mac, plain: false);

        Assert.Equal(requested, result.PsarcPath);
        Assert.Equal(requested, Assert.Single(result.PackagePaths));
        Assert.Equal(requested, Assert.Single(Directory.GetFiles(_directory, "*.psarc")));
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(requested)[..4]);
        Assert.False(result.IsPsxr);
    }

    [Fact]
    public async Task Stock_content_removes_the_pc_tag_for_both_platforms()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var built = RsxrTestSupport.Chart(false, 6);
        var requested = Path.Combine(_directory, "song_rsxr_p.psarc");
        var pc = Path.Combine(_directory, "song_p.psarc");
        var mac = Path.Combine(_directory, "song_m.psarc");
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio, requested,
            ExportPlatform.PcAndMac, plain: false);
        Assert.Equal(pc, result.PsarcPath);
        Assert.Equal(new[] { pc, mac }, result.PackagePaths);
        Assert.Equal(new[] { mac, pc }, Directory.GetFiles(_directory, "*.psarc").Order().ToArray());
        Assert.All(result.PackagePaths, path => Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(path)[..4]));
        Assert.False(result.IsPsxr);
    }

    [Fact]
    public async Task A_skipped_extended_arrangement_removes_the_tag_and_reports_the_written_stock_path()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var built = RsxrTestSupport.Chart(false, 6);
        var skipped = RsxrTestSupport.Chart(false, 7).Project.Arrangements[0];
        skipped.Name = "Too many levels";
        while (skipped.Levels.Count <= DotCharter.Services.Export.Sng.SngFormatLimits.MaxLevels)
            skipped.Levels.Add(new ChartLevel());
        built.Project.Arrangements.Add(skipped);
        Assert.True(ExtendedRangePackageName.IsPsxr(built.Project.Arrangements));
        var requested = Path.Combine(_directory, "skipped_rsxr_p.psarc");
        var expected = Path.Combine(_directory, "skipped_p.psarc");
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);

        var result = await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(
            ChartProjectCloner.Clone(built.Project), new RocksmithBuildOptions
            {
                OutputPath = requested, AudioPath = audio, PreviewAudioPath = audio,
                SongLengthMs = built.SongLengthMs, IncludeVocals = false
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsPsxr);
        Assert.Single(result.ArrangementNames);
        Assert.Contains(result.Warnings, warning => warning.Contains(skipped.Name, StringComparison.Ordinal));
        Assert.Equal(expected, result.PsarcPath);
        Assert.Equal(expected, Assert.Single(result.PackagePaths));
        Assert.Equal(RsxrTestSupport.PackageMagic(false), File.ReadAllBytes(expected)[..4]);
        Assert.False(File.Exists(requested));
    }

    [Theory]
    [InlineData(false, 6, false, false)]
    [InlineData(false, 7, false, false)]
    [InlineData(false, 8, false, false)]
    [InlineData(true, 4, false, false)]
    [InlineData(true, 5, false, false)]
    [InlineData(true, 6, false, false)]
    [InlineData(true, 5, true, false)]
    [InlineData(true, 5, true, true)]
    public async Task Dialog_prediction_agrees_with_written_content_result_magic_and_name(
        bool bass, int strings, bool mixed, bool refused)
    {
        var built = RsxrTestSupport.Chart(bass, strings);
        if (refused) built.Project.Arrangements[0].TuningPitches[0] = new TuningOffset(25);
        if (mixed) built.Project.Arrangements.Add(RsxrTestSupport.Chart(!bass, bass ? 6 : 4).Project.Arrangements[0]);
        var expected = !refused && (bass ? strings > 4 : strings > 6);
        var prediction = ExtendedRangePackageName.IsPsxr(built.Project.Arrangements);
        var path = Path.Combine(_directory, "agreement_p.psarc");
        var expectedPath = Path.Combine(_directory, expected ? "agreement_rsxr_p.psarc" : "agreement_p.psarc");
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);

        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio, path);

        Assert.Equal(expected, prediction);
        Assert.Equal(prediction, result.IsPsxr);
        Assert.Equal(expectedPath, result.PsarcPath);
        Assert.Equal(expectedPath, Assert.Single(result.PackagePaths));
        Assert.Equal(RsxrTestSupport.PackageMagic(expected), File.ReadAllBytes(result.PsarcPath)[..4]);
        if (expected) Assert.False(File.Exists(path));
        if (refused) Assert.Single(result.ArrangementNames);
    }

    [Fact]
    public async Task Native_chart_has_the_plain_compressed_wrapper_with_unused_slots_zeroed()
    {
        var built = RsxrTestSupport.Chart(true, 5);
        var audio = Path.Combine(_directory, "audio.wem");
        File.WriteAllBytes(audio, new byte[4000]);
        var result = await RsxrTestSupport.Build(built.Project, built.SongLengthMs, audio,
            Path.Combine(_directory, "wrapper_p.psarc"));
        var packed = Assert.Single(RsxrTestSupport.Entries(result.PsarcPath), e => e.Key.EndsWith(".sng", StringComparison.Ordinal)).Value;
        Assert.Equal(0x4Au, BinaryPrimitives.ReadUInt32LittleEndian(packed));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(4)));
        Assert.Equal(new byte[16], packed[8..24]);
        Assert.Equal(new byte[56], packed[^56..]);
        var body = RsxrTestSupport.Inflate(packed);
        Assert.Equal(body.Length, BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(24)));
        Assert.Equal("SNXR", Encoding.ASCII.GetString(body, 0, 4));
    }
}
