using System.Xml.Linq;
using DotCharter.Services.Export;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tests.Services.Export.Rs7;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.DynamicDifficulty;

/// <summary>
///     The export runs DLC Builder's generator on a COPY of every single-level arrangement
///     (<see cref="DlcBuilderBuildSteps" />): the SNG gets the generated ladder, the chart
///     keeps its one level, a chart that already has levels builds the same bytes as before,
///     and a 7-string guitar's RS7X block matches the generated templates and chord notes.
/// </summary>
public sealed class DynamicDifficultyExportTests : IDisposable
{
    private static readonly DateTime ConversionTime = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Unspecified);
    private static readonly DlcBuilderSteps DdOnly = new(false, true);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-dd-export-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    [Theory]
    [MemberData(nameof(DdGoldenParityTests.Fixtures), MemberType = typeof(DdGoldenParityTests))]
    public void A_single_level_chart_ships_the_generated_ladder_and_keeps_its_own_level(string fixture)
    {
        var project = new RsXmlSongReader().ReadDocument(new XDocument(DdGoldenParityTests.LoadFixture(fixture + ".xml")), fixture);
        var chart = project.Arrangements[0];
        var before = Written(project, chart);

        var generated = DlcBuilderBuildSteps.Apply(project, chart, null, DdOnly);

        Assert.NotNull(generated);
        Assert.NotSame(chart, generated);
        Assert.Single(chart.Levels);
        Assert.Equal(before, Written(project, chart));

        // What the generator makes of the XML the export writes...
        var expected = DdXml.Read(new RsXmlSongWriter().Write(SingleProject(project, chart)).Root!);
        DdGenerator.GenerateForArrangement(DdGeneratorConfig.Default, expected);
        // ...is what the SNG carries, level by level and phrase by phrase.
        var sng = SngFromDomainMapper.BuildInstrumental(generated, project.Beats, 0, ConversionTime).Sng;
        Assert.True(sng.Levels.Count > 1);
        Assert.Contains(sng.Phrases, p => p.MaxDifficulty > 0);
        Assert.Equal(expected.Levels.Count, sng.Levels.Count);
        Assert.Equal(expected.Phrases.Select(p => (p.Name, p.MaxDifficulty)), sng.Phrases.Select(p => (p.Name, p.MaxDifficulty)));
        Assert.Equal(expected.PhraseIterations.Select(pi => pi.PhraseId), sng.PhraseIterations.Select(pi => pi.PhraseId));
        Assert.Equal(expected.Levels.Select(l => l.Notes.Count + l.Chords.Count), sng.Levels.Select(l => l.Notes.Count));
    }

    /// <summary>DLC Builder's own order on the export path: its fixes, THEN the levels, in
    /// both level-count modes, read back into the copy the SNG is built from.</summary>
    [Theory]
    [InlineData("lead-a", DdLevelCountMode.Simple)]
    [InlineData("rhythm-a", DdLevelCountMode.MLModel)]
    public void The_default_steps_fix_then_generate_and_the_SNG_carries_both(string fixture, DdLevelCountMode mode)
    {
        var project = new RsXmlSongReader().ReadDocument(new XDocument(DdGoldenParityTests.LoadFixture(fixture + ".xml")), fixture);
        var chart = project.Arrangements[0];
        var steps = DlcBuilderSteps.Default with { LevelCountMode = mode };

        var built = DlcBuilderBuildSteps.Apply(project, chart, null, steps);

        var expected = DdXml.Read(new RsXmlSongWriter().Write(SingleProject(project, chart)).Root!);
        DdImprover.ApplyAll(expected);
        DdGenerator.GenerateForArrangement(DdGeneratorConfig.For(mode), expected);
        Assert.NotNull(built);
        Assert.Single(chart.Levels);
        var sng = SngFromDomainMapper.BuildInstrumental(built, project.Beats, 0, ConversionTime).Sng;
        Assert.Equal(expected.Levels.Count, sng.Levels.Count);
        Assert.Equal(expected.Phrases.Select(p => p.MaxDifficulty), sng.Phrases.Select(p => p.MaxDifficulty));
        Assert.Equal(expected.Levels.Select(l => l.Notes.Count + l.Chords.Count), sng.Levels.Select(l => l.Notes.Count));
        // The fixes' crowd events reach the copy's event points.
        Assert.Contains(built.EventPoints, p => p.Events.Split(',').Contains("E3"));
    }

    [Fact]
    public void Fixes_alone_still_change_a_chart_that_has_levels_and_steps_off_change_nothing()
    {
        var project = RocksmithPackageBuilderTests.CreateProject();
        var chart = project.Arrangements[0];
        chart.Levels.Add(chart.Levels[0]);

        Assert.Null(DlcBuilderBuildSteps.Apply(project, chart, null, new DlcBuilderSteps(false, false)));
        var fixedOnly = DlcBuilderBuildSteps.Apply(project, chart, null, new DlcBuilderSteps(true, true));
        Assert.NotNull(fixedOnly);
        Assert.Equal(2, fixedOnly.Levels.Count);
    }

    [Fact]
    public void A_chart_that_already_has_levels_and_a_vocal_track_are_left_alone()
    {
        var project = RocksmithPackageBuilderTests.CreateProject();
        var chart = project.Arrangements[0];
        Assert.True(DlcBuilderBuildSteps.GeneratesLevels(chart));

        chart.Levels.Add(new ChartLevel { DifficultyIndex = 1 });
        Assert.False(DlcBuilderBuildSteps.GeneratesLevels(chart));
        Assert.Null(DlcBuilderBuildSteps.Apply(project, chart, null, DdOnly));
        Assert.False(DlcBuilderBuildSteps.GeneratesLevels(new Arrangement { ArrangementType = ArrangementType.Vocal, Levels = [new ChartLevel()] }));
    }

    [Fact]
    public async Task The_package_builder_generates_levels_only_for_a_single_level_chart_and_only_when_asked()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();

        // One level: the option changes the package, and the log names the arrangement.
        var (off, offLog) = await BuildAsync(RocksmithPackageBuilderTests.CreateProject(), generate: false, "single_off");
        var (on, onLog) = await BuildAsync(RocksmithPackageBuilderTests.CreateProject(), generate: true, "single_on");
        Assert.DoesNotContain(offLog.Entries, e => e.Message.Contains("generated dynamic difficulty", StringComparison.Ordinal));
        Assert.Contains(onLog.Entries, e => e.Message.Contains("DLC Builder steps on arrangement 'Lead': fixes off, generated dynamic difficulty", StringComparison.Ordinal));
        Assert.False(off.AsSpan().SequenceEqual(on));

        // Two levels: byte for byte what the build wrote before.
        var (multiOff, _) = await BuildAsync(TwoLevelProject(), generate: false, "multi_off");
        var (multiOn, multiLog) = await BuildAsync(TwoLevelProject(), generate: true, "multi_on");
        Assert.True(multiOff.AsSpan().SequenceEqual(multiOn));
        Assert.DoesNotContain(multiLog.Entries, e => e.Message.Contains("generated dynamic difficulty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_build_leaves_the_project_it_is_given_with_its_one_level()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var project = RocksmithPackageBuilderTests.CreateProject();
        var chart = project.Arrangements[0];
        var notes = chart.Levels[0].Notes.Count;

        await BuildAsync(project, generate: true, "untouched");

        Assert.Same(chart, project.Arrangements[0]);
        Assert.Single(chart.Levels);
        Assert.Equal(notes, chart.Levels[0].Notes.Count);
    }

    [Fact]
    public void A_seven_string_chart_gets_levels_and_an_RS7X_block_that_matches_them()
    {
        var project = Rs7PackageExportTests.CreateProject(7);
        var chart = project.Arrangements[0];
        PhraseSectionGenerator.GenerateIfAbsent(chart, project.Beats, 0);

        var generated = DlcBuilderBuildSteps.Apply(project, chart, null, DdOnly);

        Assert.NotNull(generated);
        Assert.Equal(7, generated.StringCount);
        Assert.True(generated.Levels.Count > 1);
        var lowB = Rs7StringLayout.ToDomain(6, 7);
        // The 7th string is played in the easy levels too, and the narrower templates keep it.
        Assert.Contains(generated.Levels[0].Notes, n => n.StringIndex == lowB);
        Assert.Contains(generated.ChordTemplates, t => t.Frets.ContainsKey(lowB) && t.Frets.Count < 4);

        var sng = SngFromDomainMapper.BuildInstrumental(generated, project.Beats, 0, ConversionTime).Sng;
        Assert.True(sng.Levels.Count > 1);
        var block = Assert.IsType<RsExtensionBlock>(sng.Extension);
        Assert.Equal(sng.Chords.Count, block.Templates!.Count);
        Assert.Equal(sng.ChordNotes.Count, block.ChordNotes!.Count);
        // The narrower templates keep the 7th string (notes are removed from the top), and
        // the block carries its column for each of them.
        Assert.True(block.Templates.Count(row => row.Any(c => c.Fret >= 0)) > 1);
    }

    private async Task<(byte[] Bytes, FakeAppLogger Log)> BuildAsync(ChartProject project, bool generate, string run)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_tempDir, run)).FullName;
        var wem = Path.Combine(directory, "audio.wem");
        var data = new byte[80_000];
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(wem, data, TestContext.Current.CancellationToken);
        var log = new FakeAppLogger();
        var result = await new RocksmithPackageBuilder(log).BuildPsarcAsync(project, new RocksmithBuildOptions
        {
            OutputPath = Path.Combine(directory, "dd_p.psarc"),
            AudioPath = wem,
            DlcKey = "DdExport",
            SongLengthMs = 32_000,
            IncludeVocals = false,
            DlcBuilderSteps = generate ? DdOnly : null,
            Identity = Identity()
        }, cancellationToken: TestContext.Current.CancellationToken);
        return (await File.ReadAllBytesAsync(result.PsarcPath, TestContext.Current.CancellationToken), log);
    }

    private static ChartProject TwoLevelProject()
    {
        var project = RocksmithPackageBuilderTests.CreateProject();
        var chart = project.Arrangements[0];
        var easy = new ChartLevel { DifficultyIndex = 0 };
        easy.Notes.Add(chart.Levels[0].Notes[0].Clone());
        easy.Anchors.Add(chart.Levels[0].Anchors[0].Clone());
        chart.Levels[0].DifficultyIndex = 1;
        chart.Levels.Insert(0, easy);
        return project;
    }

    private static RocksmithBuildIdentity Identity() => new()
    {
        ConversionTime = ConversionTime,
        Arrangements = [new RsArrangementIdentity(new Guid(0x5EED0000, 0x11, 0x22, [9, 8, 7, 6, 5, 4, 3, 0]), 2_000_000)],
        GraphItems = [.. Enumerable.Range(1, 10).Select(i => new RsGraphItemIdentity(
            new Guid(i, 0x33, 0x44, [1, 2, 3, 4, 5, 6, 7, (byte)i]), new Guid(2000 + i, 0, 0, new byte[8])))],
        MainBank = new SoundBankIds(0x1111_2222, 0x0333_4444, 0x0555_6666, 0x7777_8888, 0x0999_AAAA),
        PreviewBank = new SoundBankIds(0x2111_2222, 0x0433_4444, 0x0655_6666, 0x8777_8888, 0x0999_AAAA),
        SngIv = [.. Enumerable.Range(0, 16).Select(i => (byte)(0xC0 + i))]
    };

    private static ChartProject SingleProject(ChartProject project, Arrangement arrangement) => new()
    {
        Chart = project.Chart,
        Beats = project.Beats,
        Arrangements = [arrangement],
    };

    /// <summary>The arrangement as the RS XML writer spells it, without the clock.</summary>
    private static string Written(ChartProject project, Arrangement arrangement)
    {
        var song = new RsXmlSongWriter().Write(SingleProject(project, arrangement)).Root!;
        song.Element("lastConversionDateTime")?.Remove();
        return song.ToString();
    }
}
