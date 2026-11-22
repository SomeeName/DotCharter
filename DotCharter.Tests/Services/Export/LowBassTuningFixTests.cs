using System.Text.Json;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tests.Services.Export.Rsxr;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     #906: a stock bass below C1 ships an octave up at A220 (manifest <c>Tuning</c> +12 from the SNG,
///     <c>CentOffset</c> −1200); the project never changes, the fix off writes the same bytes as before,
///     and a guitar or an RS Extended Range bass is never touched.
/// </summary>
public sealed class LowBassTuningFixTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dotcharter-lowbass-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_directory);

    [Theory]
    [InlineData(new[] { -5, -5, -5, -5 }, true)]   // B standard
    [InlineData(new[] { -5, -3, -3, -3 }, true)]   // C# drop B
    [InlineData(new[] { -6, -4, -4, -4 }, true)]   // C drop A#
    [InlineData(new[] { -4, -4, -4, -4 }, false)]  // C standard: low C1 registers
    [InlineData(new[] { -4, -2, -2, -2 }, false)]  // D drop C
    [InlineData(new[] { -2, -2, -2, -2 }, false)]  // D standard
    [InlineData(new[] { 0, 0, 0, 0 }, false)]      // E standard
    public void A_four_string_bass_needs_the_fix_exactly_below_c1(int[] offsets, bool needs) =>
        Assert.Equal(needs, LowBassTuningFix.Needs(Bass(offsets)));

    [Fact]
    public void A_guitar_an_extended_bass_and_an_already_fixed_bass_never_need_it()
    {
        var guitar = Bass([-5, -5, -5, -5, -5, -5]);
        guitar.ArrangementType = ArrangementType.Lead;
        guitar.StringCount = 6;
        var five = Bass([-5, 0, 0, 0, 0]); // B E A D G
        five.StringCount = 5;
        var fixedBass = Bass([7, 7, 7, 7]);
        fixedBass.CentOffset = -1200;

        Assert.False(LowBassTuningFix.Needs(guitar));
        Assert.Equal(23, LowBassTuningFix.LowestPitch(five));
        Assert.False(LowBassTuningFix.Needs(five));
        Assert.False(LowBassTuningFix.Needs(fixedBass));
    }

    [Fact]
    public void The_copy_is_an_octave_up_at_a220_sounds_the_same_and_leaves_the_source()
    {
        var source = Bass([-5, -5, -5, -5]);
        source.CentOffset = -20;

        var copy = LowBassTuningFix.Apply(source);

        Assert.Equal([7, 7, 7, 7], copy.TuningPitches.Select(t => t.Step));
        Assert.Equal(-1220, copy.CentOffset);
        Assert.Equal([-5, -5, -5, -5], source.TuningPitches.Select(t => t.Step));
        Assert.Equal(-20, source.CentOffset);
        for (var i = 0; i < 4; i++)
            Assert.Equal(SoundingPitch.OpenSoundingMidi(source, i), SoundingPitch.OpenSoundingMidi(copy, i));
    }

    [Theory]
    [InlineData(new[] { -5, -5, -5, -5 })]
    [InlineData(new[] { -6, -4, -4, -4 })]
    public async Task A_low_bass_package_carries_the_fix_and_the_project_is_unchanged(int[] offsets)
    {
        var (project, length) = BassProject(offsets);
        var before = Snapshot(project.Arrangements[0]);

        var result = await Build(project, length, "fixed", fix: true, clone: false);

        var bass = BassManifest(result.PsarcPath);
        Assert.Equal(-1200, bass.CentOffset);
        Assert.Equal(offsets.Select(o => o + 12), bass.Tuning.Take(4));
        Assert.Equal(before, Snapshot(project.Arrangements[0]));
    }

    [Fact]
    public async Task With_the_fix_off_the_package_is_the_one_written_before()
    {
        var (project, length) = BassProject([-5, -5, -5, -5]);

        var off = await Build(project, length, "off", fix: false);
        var legacy = await Build(project, length, "legacy", fix: null);
        var on = await Build(project, length, "on", fix: true);

        Assert.Equal(File.ReadAllBytes(legacy.PsarcPath), File.ReadAllBytes(off.PsarcPath));
        Assert.NotEqual(File.ReadAllBytes(off.PsarcPath), File.ReadAllBytes(on.PsarcPath));
        var bass = BassManifest(off.PsarcPath);
        Assert.Equal(0, bass.CentOffset);
        Assert.Equal([-5, -5, -5, -5], bass.Tuning.Take(4));
    }

    [Fact]
    public async Task An_e_standard_bass_and_a_guitar_are_unchanged_by_the_fix()
    {
        var (project, length) = BassProject([0, 0, 0, 0]);
        var guitar = RsxrTestSupport.Chart(false, 6).Project.Arrangements[0];
        guitar.Name = "Lead";
        foreach (var t in guitar.TuningPitches) t.Step = -5;
        project.Arrangements.Add(guitar);

        var off = await Build(project, length, "eoff", fix: false, arrangements: 2);
        var on = await Build(project, length, "eon", fix: true, arrangements: 2);

        Assert.Equal(File.ReadAllBytes(off.PsarcPath), File.ReadAllBytes(on.PsarcPath));
    }

    [Fact]
    public async Task Encrypted_pc_and_mac_packages_carry_the_fix()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var (project, length) = BassProject([-5, -5, -5, -5]);

        var off = await Build(project, length, "encoff", fix: false, platform: ExportPlatform.PcAndMac, plain: false);
        var on = await Build(project, length, "encon", fix: true, platform: ExportPlatform.PcAndMac, plain: false);

        Assert.Equal(2, on.PackagePaths.Count);
        for (var i = 0; i < 2; i++)
            Assert.NotEqual(File.ReadAllBytes(off.PackagePaths[i]), File.ReadAllBytes(on.PackagePaths[i]));
    }

    [Fact]
    public async Task The_standard_package_beside_an_extended_one_carries_the_fix_and_the_five_string_bass_does_not()
    {
        var (project, length) = BassProject([-5, -5, -5, -5]);
        var seven = RsxrTestSupport.Chart(false, 7);
        seven.Project.Arrangements[0].Name = "Lead7";
        project.Arrangements.Add(seven.Project.Arrangements[0]);
        var five = RsxrTestSupport.Chart(true, 5).Project.Arrangements[0];
        five.Name = "Bass5";
        project.Arrangements.Add(five);
        length = Math.Max(length, seven.SongLengthMs);

        var on = await Build(project, length, "dualon", fix: true, arrangements: 3, standard: true);
        var off = await Build(project, length, "dualoff", fix: false, arrangements: 3, standard: true);

        var standard = BassManifest(Assert.Single(on.StandardPackagePaths));
        Assert.Equal(-1200, standard.CentOffset);
        Assert.Equal([7, 7, 7, 7], standard.Tuning.Take(4));
        // The extended package: its stock bass is fixed, its SNXR five-string bass is byte-equal to the fix off.
        var onEntries = RsxrTestSupport.Entries(on.PsarcPath);
        var offEntries = RsxrTestSupport.Entries(off.PsarcPath);
        var fiveEntries = onEntries.Keys.Where(k => k.Contains("_bass2", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, fiveEntries.Count);
        Assert.All(fiveEntries, k => Assert.Equal(offEntries[k], onEntries[k]));
        var stockSng = offEntries.Keys.Single(k => k.EndsWith("_bass.sng", StringComparison.Ordinal));
        Assert.NotEqual(offEntries[stockSng], onEntries[stockSng]);
    }

    private static Arrangement Bass(int[] offsets)
    {
        var bass = new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass", StringCount = offsets.Length };
        foreach (var o in offsets) bass.TuningPitches.Add(new TuningOffset(o));
        return bass;
    }

    private static (ChartProject Project, double Length) BassProject(int[] offsets)
    {
        var built = RsxrTestSupport.Chart(true, 4);
        var bass = built.Project.Arrangements[0];
        bass.Name = "Bass";
        bass.TuningPitches.Clear();
        foreach (var o in offsets) bass.TuningPitches.Add(new TuningOffset(o));
        return (built.Project, built.SongLengthMs);
    }

    private static string Snapshot(Arrangement a) =>
        $"{a.CentOffset}|{string.Join(',', a.TuningPitches.Select(t => t.Step))}|{a.StringCount}";

    private static (double CentOffset, int[] Tuning) BassManifest(string package)
    {
        var entries = RsxrTestSupport.Entries(package);
        foreach (var (name, bytes) in entries.Where(e => e.Key.EndsWith(".json", StringComparison.Ordinal)))
        {
            var attributes = JsonDocument.Parse(bytes).RootElement.GetProperty("Entries").EnumerateObject().Single().Value
                .GetProperty("Attributes");
            if (attributes.GetProperty("ArrangementName").GetString() != "Bass" || attributes.TryGetProperty("ExtendedRange", out _)) continue;
            var tuning = attributes.GetProperty("Tuning");
            return (attributes.GetProperty("CentOffset").GetDouble(),
                [.. Enumerable.Range(0, 6).Select(i => tuning.GetProperty($"string{i}").GetInt32())]);
        }
        throw new InvalidOperationException($"no stock bass manifest in {Path.GetFileName(package)}");
    }

    private Task<RocksmithBuildResult> Build(ChartProject project, double length, string name, bool? fix,
        ExportPlatform platform = ExportPlatform.Pc, bool? plain = true, int arrangements = 1, bool standard = false,
        bool clone = true)
    {
        var options = new RocksmithBuildOptions
        {
            OutputPath = Path.Combine(_directory, name + "_p.psarc"), AudioPath = Audio(), PreviewAudioPath = Audio(),
            DlcKey = "RsxrLowBass", SongLengthMs = length, IncludeVocals = false, Identity = RsxrTestSupport.Identity(arrangements),
            PlainPackage = plain, Platform = platform, WriteStandardPackage = standard
        };
        if (fix is { } on) options = options with { FixLowBassTuning = on };
        return new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(clone ? ChartProjectCloner.Clone(project) : project, options,
            cancellationToken: TestContext.Current.CancellationToken);
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
