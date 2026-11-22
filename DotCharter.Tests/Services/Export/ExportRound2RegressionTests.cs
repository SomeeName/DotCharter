using System.Xml.Linq;
using DotCharter.Services.Export.DynamicDifficulty;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tests.Services.Export.DynamicDifficulty;

namespace DotCharter.Tests.Services.Export;

public sealed class ExportRound2RegressionTests
{
    [Fact]
    public void The_final_explicit_phrase_keeps_mains_maximum_difficulty_two()
    {
        const int mainMaximum = 2;
        var project = FinalPhraseChart();
        var sng = Build(project);
        var xml = new RsXmlSongWriter().Write(project);

        Assert.Equal(mainMaximum, sng.Phrases[1].MaxDifficulty);
        Assert.Equal(mainMaximum, (int)xml.Descendants("phrase").ElementAt(1).Attribute("maxDifficulty")!);
    }

    [Fact]
    public void Case_distinct_authored_phrases_keep_mains_solo_flags_one_and_zero()
    {
        var project = CaseDistinctChart();
        var sng = Build(project);
        var xml = new RsXmlSongWriter().Write(project);

        Assert.Equal([1, 0], sng.Phrases.Select(p => (int)p.Solo));
        Assert.Equal([1, 0], xml.Descendants("phrase").Select(p => (int?)p.Attribute("solo") ?? 0));
    }

    [Theory]
    [InlineData(600000)]
    [InlineData(3000)]
    public void Mains_ordered_END_and_section_intervals_survive_a_shorter_resolved_end(int audioLengthMs)
    {
        const float mainEndStartSeconds = 7.5f;
        var sng = Build(ExplicitEndChart(), audioLengthMs);

        Assert.Equal(Math.Min(mainEndStartSeconds, sng.MetaData.SongLength), sng.PhraseIterations[^1].StartTime);
        AssertIntervals(sng);
    }

    public static TheoryData<string> InvariantCases =>
        [.. DdGoldenParityTests.Names.Select(name => "dd/" + name), .. ReviewCharts().Select(chart => chart.Name)];

    [Theory]
    [MemberData(nameof(InvariantCases))]
    public void Existing_fixtures_and_review_charts_obey_phrase_and_interval_invariants(string fixture)
    {
        var project = fixture.StartsWith("dd/", StringComparison.Ordinal)
            ? new RsXmlSongReader().ReadDocument(new XDocument(DdGoldenParityTests.LoadFixture(fixture[3..] + ".xml")), fixture)
            : ReviewCharts().Single(chart => chart.Name == fixture).Project;
        var source = project.Arrangements[0];
        var raw = SngFromDomainMapper.BuildInstrumental(source, project.Beats, 0).Sng;
        AssertIntervals(raw);
        AssertPhraseDifficulties(raw);
        var authored = source.PhraseInfos.ToDictionary(pair => pair.Key,
            pair => pair.Value.Solo, StringComparer.Ordinal);
        var generated = DlcBuilderBuildSteps.Apply(project, source, null, DlcBuilderSteps.Default) ?? source;
        var sng = SngFromDomainMapper.BuildInstrumental(generated, project.Beats, 0).Sng;
        AssertIntervals(sng);
        AssertPhraseDifficulties(sng);
        for (var id = 0; id < sng.Phrases.Count; id++)
        {
            var name = sng.Phrases[id].Name;
            if (authored.TryGetValue(name, out var solo))
                Assert.Equal(solo, sng.Phrases[id].Solo != 0);
        }
    }

    internal static IEnumerable<(string Name, ChartProject Project)> ReviewCharts() =>
    [
        ("review/final-phrase", FinalPhraseChart()),
        ("review/explicit-end", ExplicitEndChart()),
        ("review/case-metadata", CaseDistinctChart()),
        ("review/synthetic-p0", ExportRound3PhraseTests.SyntheticP0Chart()),
        ("review/first-explicit-p0", ExportRound3PhraseTests.FirstExplicitP0Chart()),
        ("review/final-explicit-owner", ExportRound3PhraseTests.FinalBoundaryChart("explicit")),
        ("review/final-marker-owner", ExportRound3PhraseTests.FinalBoundaryChart("markers")),
        ("review/final-synthetic-owner", ExportRound3PhraseTests.FinalBoundaryChart("synthetic")),
        ("review/muted-grips", DdMutedHandShapeTests.MutedChart()),
        ("review/authored-arpeggio", DdMutedHandShapeTests.AuthoredArpeggioChart()),
        ("review/shared-own-arpeggio", DdMutedHandShapeTests.SharedOwnArpeggioChart()),
        ("review/last-millisecond", ExportPhraseBoundaryTests.Chart("last-millisecond", false)),
        ("review/every-boundary", ExportPhraseBoundaryTests.Chart("every-boundary", false)),
        ("review/sub-millisecond", ExportPhraseBoundaryTests.Chart("sub-millisecond", false))
    ];

    private static void AssertIntervals(RocksmithSng sng)
    {
        foreach (var iteration in sng.PhraseIterations)
        {
            Assert.InRange(iteration.StartTime, 0, sng.MetaData.SongLength);
            Assert.InRange(iteration.EndTime, iteration.StartTime, sng.MetaData.SongLength);
        }

        foreach (var section in sng.Sections)
        {
            Assert.InRange(section.StartTime, 0, sng.MetaData.SongLength);
            Assert.InRange(section.EndTime, section.StartTime, sng.MetaData.SongLength);
        }
    }

    private static void AssertPhraseDifficulties(RocksmithSng sng)
    {
        foreach (var level in sng.Levels)
        {
            foreach (var note in level.Notes)
            {
                Assert.InRange(note.PhraseIterationId, 0, sng.PhraseIterations.Count - 1);
                var owner = sng.PhraseIterations[note.PhraseIterationId].PhraseId;
                Assert.True(sng.Phrases[owner].MaxDifficulty >= level.Difficulty);
            }
        }

        for (var id = 0; id < sng.Phrases.Count; id++)
        {
            var maximum = sng.Levels.Where(level => level.Notes.Any(note =>
                    sng.PhraseIterations[note.PhraseIterationId].PhraseId == id))
                .Select(level => level.Difficulty).DefaultIfEmpty(0).Max();
            Assert.Equal(maximum, sng.Phrases[id].MaxDifficulty);
        }
    }

    private static RocksmithSng Build(ChartProject project, int audioLengthMs = 600000) =>
        SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, audioLengthMs).Sng;

    private static ChartProject Project(string first, string last)
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.Phrases.AddRange([first, last]);
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 1));
        project.Arrangements.Add(arrangement);
        return project;
    }

    private static ChartProject FinalPhraseChart()
    {
        var project = Project("A", "B");
        for (var difficulty = 0; difficulty <= 2; difficulty++)
        {
            var level = new ChartLevel { DifficultyIndex = difficulty };
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(10), StringIndex = 0, Fret = 3 });
            project.Arrangements[0].Levels.Add(level);
        }

        return project;
    }

    private static ChartProject ExplicitEndChart()
    {
        var project = Project("A", "END");
        var arrangement = project.Arrangements[0];
        arrangement.PhraseIterations[1] = new PhraseIteration(new FractionalPosition(15), 1);
        arrangement.EventPoints.Add(new EventPoint { Position = FractionalPosition.Zero, Section = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(15), Section = "noguitar" });
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
        arrangement.Levels.Add(level);
        return project;
    }

    private static ChartProject CaseDistinctChart()
    {
        var project = Project("Verse", "verse");
        var arrangement = project.Arrangements[0];
        arrangement.PhraseInfos["Verse"] = new PhraseInfo(0, true);
        arrangement.PhraseInfos["verse"] = new PhraseInfo(0, false);
        var level = new ChartLevel();
        foreach (var beat in new[] { 2, 10 })
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 0, Fret = 3 });
        arrangement.Levels.Add(level);
        return project;
    }
}
