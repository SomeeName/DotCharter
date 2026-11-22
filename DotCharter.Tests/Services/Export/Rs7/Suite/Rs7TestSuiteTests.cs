using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Export.Validation;

namespace DotCharter.Tests.Services.Export.Rs7.Suite;

/// <summary>
///     #307: the generated in-game test suite for rs7string. <see cref="Write_the_suite" /> is opt-in
///     (env <c>DOTCHARTER_RS7_SUITE_OUT</c>) and writes plain packages, an expected-results file per chart and a
///     README; the other facts keep the specs honest in the default gate.
/// </summary>
public sealed class Rs7TestSuiteTests
{
    public static TheoryData<string> Names => [.. Rs7TestSuiteCatalog.All.Select(s => s.Name)];

    private static ChartSpec Spec(string name) => Rs7TestSuiteCatalog.All.Single(s => s.Name == name);

    [Fact]
    public async Task Write_the_suite()
    {
        var outputDir = Environment.GetEnvironmentVariable("DOTCHARTER_RS7_SUITE_OUT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(outputDir), "Opt-in: set DOTCHARTER_RS7_SUITE_OUT.");
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(outputDir);
        var refused = new Dictionary<string, string>();
        foreach (var spec in Rs7TestSuiteCatalog.All)
        {
            var built = Rs7TestSuiteBuilder.Build(spec);
            var errors = new ChartExportValidator().Validate(built.Project)
                .Where(f => f.Severity == ExportFindingSeverity.Error).Select(f => $"{f.ArrangementName}: {f.MessageKey} {f.Detail}").ToList();
            try
            {
                await ExportAsync(spec, built, outputDir, ct);
                await File.WriteAllTextAsync(Path.Combine(outputDir, spec.Name + ".expected.md"), Rs7TestSuiteExpected.Markdown(spec, built), ct);
                if (errors.Count > 0) refused[spec.Name] = "validator error (package still built): " + string.Join(" | ", errors);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                refused[spec.Name] = ex.Message + (errors.Count > 0 ? " | " + string.Join(" | ", errors) : "");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(outputDir, "README.md"), Rs7TestSuiteExpected.Readme(Rs7TestSuiteCatalog.All, refused), ct);
        await File.WriteAllLinesAsync(Path.Combine(outputDir, "refused.txt"), refused.Select(r => $"{r.Key}: {r.Value}"), ct);
    }

    private static async Task ExportAsync(ChartSpec spec, BuiltChart built, string outputDir, CancellationToken ct)
    {
        var workDir = Directory.CreateTempSubdirectory("rs7suite-audio-").FullName;
        try
        {
            await Rs7TestAudio.BuildPackageAsync(built.Project, Path.Combine(outputDir, spec.Name + "_p.psarc"),
                Rs7TestSuiteExpected.DlcKey(spec), built.SongLengthMs, workDir, ct);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Names_are_unique_lowercase_ascii_and_the_key_fits()
    {
        var names = Rs7TestSuiteCatalog.All.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.Matches("^[a-z0-9_]+$", n));
        Assert.All(Rs7TestSuiteCatalog.All, s => Assert.InRange(Rs7TestSuiteExpected.DlcKey(s).Length, 1, 30));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_chart_builds_and_is_30_to_60_seconds_long(string name)
    {
        var built = Rs7TestSuiteBuilder.Build(Spec(name));
        Assert.InRange(built.SongLengthMs / 1000, 30, 60);
        Assert.Equal(Spec(name).Parts.Length, built.Project.Arrangements.Count);
        Assert.All(built.Project.Arrangements, a => Assert.True(a.Levels[^1].Notes.Count + a.Levels[^1].Chords.Count > 0));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void No_string_is_tuned_or_played_below_G0(string name)
    {
        foreach (var p in Spec(name).Parts)
        {
            Assert.All(Enumerable.Range(0, p.Inst.Strings), s => Assert.True(Rs7SuiteSpec.Midi(p.Inst, p.Tuning, s, 0) >= Rs7SuiteSpec.LowestMidi, $"{name} string {s}"));
            Assert.All(p.Events.SelectMany(e => e.Cols), c => Assert.InRange(Rs7SuiteSpec.Midi(p.Inst, p.Tuning, c.String, c.Fret), Rs7SuiteSpec.LowestMidi, 127));
            Assert.All(p.Events.SelectMany(e => e.Cols), c => Assert.InRange(c.Fret, 0, 24));
            Assert.All(p.Events.SelectMany(e => e.Cols), c => Assert.Contains(c.String, p.Inst.Order));
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void The_sng_carries_the_specs_tuning_and_sounds(string name)
    {
        var spec = Spec(name);
        var built = Rs7TestSuiteBuilder.Build(spec);
        for (var i = 0; i < spec.Parts.Length; i++)
        {
            var p = spec.Parts[i];
            var sng = SngFromDomainMapper.BuildInstrumental(built.Project.Arrangements[i], built.Project.Beats, built.SongLengthMs,
                new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Unspecified)).Sng;
            Assert.Equal(p.Tuning, sng.MetaData.Tuning.Take(p.Tuning.Length).Select(t => (int)t));
            Assert.Equal(p.Inst.Extended, sng.Extension is not null);

            // #309: every chord stays one chord, so every column is a chord column: the stock six in the
            // chord template, a guitar's extra strings in the RS7X block's CHDT row.
            var expected = p.Events.SelectMany(e => e.Cols.Select(c => ((int)Math.Round(e.Beat * Rs7SuiteSpec.BeatMs), c.String, c.Fret)))
                .Order().ToList();
            var level = sng.Levels.OrderBy(l => l.Difficulty).Last();
            var actual = level.Notes.SelectMany(n => (n.Mask & RsNoteMask.Chord) == 0
                    ? [((int)Math.Round(n.Time * 1000), n.StringIndex, n.Fret)]
                    : ChordColumns(sng, n.ChordId).Select(x => ((int)Math.Round(n.Time * 1000), x.String, x.Fret)))
                .Order().ToList();
            Assert.Equal(expected, actual);
        }
    }

    private static IEnumerable<(int String, int Fret)> ChordColumns(RocksmithSng sng, int chordId)
    {
        foreach (var (f, st) in sng.Chords[chordId].Frets.Select((f, st) => (f, st)).Where(x => x.f >= 0))
            yield return (st, f);
        var block = sng.Extension;
        if (block?.Templates is not { } rows) yield break;
        for (var i = 0; i < block.Strings.Count; i++)
        {
            if (rows[chordId][i].Fret >= 0) yield return (block.Strings[i], rows[chordId][i].Fret);
        }
    }

    [Fact]
    public void The_suite_covers_every_instrument_tuning_and_string()
    {
        var parts = Rs7TestSuiteCatalog.All.SelectMany(s => s.Parts).ToList();
        foreach (var inst in new[] { Inst.G6, Inst.G7, Inst.G8, Inst.B4, Inst.B5, Inst.B6 })
            Assert.Contains(parts, p => p.Inst == inst);

        foreach (var inst in new[] { Inst.B5, Inst.B6, Inst.G7, Inst.G8 })
        {
            var own = Rs7TestSuiteCatalog.All.Where(s => s.Parts.Length == 1 && s.Parts[0].Inst == inst).Select(s => s.Parts[0]).ToList();
            var names = own.Select(Rs7TuningNames.Expected).ToList();
            Assert.Contains(own, p => p.Tuning.All(t => t == 0));
            Assert.Contains(names, n => n.Contains("Drop", StringComparison.Ordinal));
            Assert.Contains(own, p => p.Tuning.All(t => t == -2));
            Assert.Contains(names, n => n.StartsWith("unknown", StringComparison.Ordinal));
            foreach (var s in inst.Order)
            {
                var frets = own.SelectMany(p => p.Events).SelectMany(e => e.Cols).Where(c => c.String == s).Select(c => c.Fret).ToHashSet();
                Assert.True(frets.Contains(0) && frets.Any(f => f >= 12), $"{inst.Key} string {s} lacks open or 12-24");
            }

            var techs = own.SelectMany(p => p.Events).SelectMany(e => e.Cols).Aggregate(Tech.None, (a, c) => a | c.Tech);
            Assert.Equal(Rs7SuiteContent.Techniques(inst).Aggregate(Tech.None, (a, t) => a | t), techs);
            Assert.Contains(own, p => p.Events.Any(e => e.Chord is not null && e.Cols.Length == 2));
            Assert.Contains(own, p => p.Events.Any(e => e.Cols.Length == inst.Strings));
            Assert.Contains(own, p => p.Events.Any(e => e.Cols.Any(c => c.Finger > 0)));
            Assert.Contains(own, p => p.Levels > 1 && p.Sections!.Length >= 4);
        }

        Assert.Contains(Rs7TestSuiteCatalog.All, s => s.Parts.Length == 5);
        Assert.Contains(Rs7TestSuiteCatalog.All, s => s.Parts.Length is >= 6 and <= 9);
        Assert.Contains(Rs7TestSuiteCatalog.All, s => s.Parts.Length > 8 && s.Parts.Any(p => p.Inst.Bass) && s.Parts.Any(p => !p.Inst.Bass));
        foreach (var pair in new[] { (Inst.G7, Inst.B5), (Inst.G8, Inst.B6), (Inst.G6, Inst.B4) })
            Assert.Contains(Rs7TestSuiteCatalog.All, s => s.Parts.Length == 2 && s.Parts[0].Inst == pair.Item1 && s.Parts[1].Inst == pair.Item2);

        Assert.Contains(parts, p => Rs7SuiteSpec.Open(p.Inst, p.Inst.Order[0]) + p.Tuning[p.Inst.Order[0]] == Rs7SuiteSpec.LowestMidi);
        Assert.Contains(parts, p => p.Inst == Inst.G8 && Rs7SuiteSpec.Midi(p.Inst, p.Tuning, 7, 0) == 23);
    }

    [Fact]
    public void No_chord_is_marked_as_split_in_the_expected_file()
    {
        var spec = Spec("b5_chords");
        var md = Rs7TestSuiteExpected.Markdown(spec, Rs7TestSuiteBuilder.Build(spec));
        Assert.DoesNotContain("split from", md, StringComparison.Ordinal);
        Assert.Contains("| Open |", md, StringComparison.Ordinal);
        Assert.Contains("B Standard 5Str", md, StringComparison.Ordinal);
        Assert.Equal("B Standard 7Str", Rs7TuningNames.Expected(Spec("g7_b_standard").Parts[0]));
        Assert.Equal("F# Standard 8Str", Rs7TuningNames.Expected(Spec("g8_fsharp_standard").Parts[0]));
        Assert.Equal("Drop G 5Str", Rs7TuningNames.Expected(Spec("b5_drop_g_lowest").Parts[0]));
    }
}
