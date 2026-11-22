using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

public sealed class EofInterleavedGraceTests
{
    [Fact]
    public void The_600_and_650_ms_graces_keep_both_identities_and_the_second_end()
    {
        var project = EofGraceTechniqueRoundTripTests.Project(100, 1200, "plain");
        var level = project.Arrangements[0].Levels[0];
        var first = level.Notes[0];
        first.StringIndex = 1;
        first.Sustain = EofGraceNeighbourCases.At(project, 650) - first.Position;
        var second = new GuitarNote
        {
            StringIndex = 1, Fret = 9, GraceFret = 7, Position = EofGraceNeighbourCases.At(project, 650),
            Sustain = EofGraceNeighbourCases.At(project, 1850) - EofGraceNeighbourCases.At(project, 650)
        };
        level.Notes.Add(second);
        var actual = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes).Levels[0].Notes;
        Assert.Equal(2, actual.Count);
        Assert.Equal(new int?[] { 5, 7 }, actual.Select(n => n.GraceFret));
        Assert.InRange(Math.Abs(EofGraceNeighbourCases.Ms(project, actual[1].Position) - 650), 0, 1);
        Assert.InRange(Math.Abs(EofGraceNeighbourCases.Ms(project, actual[1].EndPosition) - 1850), 0, 1);
    }

    [Fact]
    public void The_seeded_inputs_cover_the_extended_geometry()
    {
        var projects = Enumerable.Range(0, 300).Select(EofGraceNeighbourCases.Create).ToList();
        Assert.Equal(new[] { 0, 2 }, projects.Select(p => p.Arrangements[0].Capo).Distinct().Order());
        Assert.Equal(new[] { 7, 8 }, projects.Select(p => p.Arrangements[0].StringCount).Distinct().Order());
        Assert.All(projects, p =>
        {
            Assert.Equal(2, p.Arrangements[0].Levels.Count);
            Assert.Contains(EofGraceExtendedCases.Rows(p.Arrangements[0]), r => r.Chord);
            Assert.Contains(EofGraceExtendedCases.Rows(p.Arrangements[0]), r => r.Difficulty == 1);
            var first = p.Arrangements[0].Levels[0].Notes.First(n => Math.Abs(n.Position.GetPositionMs(p.Beats) - 11980) < 1);
            var end = 11980 + EofGraceTiming.LengthAt(first.Position, p.Beats);
            Assert.Contains(p.Beats, b => b.Time > 11980 && b.Time < end);
        });
        var clusters = projects.SelectMany(p => p.Arrangements[0].Levels.Select(level =>
            (Project: p, Notes: level.Notes.Where(n => n.GraceFret != null &&
                EofGraceNeighbourCases.Ms(p, n.Position) >= 24000).OrderBy(n => n.Position).ToList())))
            .Where(c => c.Notes.Count >= 2).ToList();
        Assert.Equal(new[] { 2, 3 }, clusters.Select(c => c.Notes.Count).Distinct().Order());
        Assert.Equal(new[] { 10, 25, 50, 75 }, clusters.Select(c => (int)Math.Round(
            EofGraceNeighbourCases.Ms(c.Project, c.Notes[1].Position) -
            EofGraceNeighbourCases.Ms(c.Project, c.Notes[0].Position))).Distinct().Order());
        Assert.All(clusters, c =>
        {
            var gap = EofGraceNeighbourCases.Ms(c.Project, c.Notes[1].Position) -
                      EofGraceNeighbourCases.Ms(c.Project, c.Notes[0].Position);
            Assert.True(gap < EofGraceTiming.LengthAt(c.Notes[0].Position, c.Project.Beats));
        });
        Assert.Contains(clusters, c => c.Notes[0].Sustain < c.Notes[1].Position - c.Notes[0].Position);
        Assert.Contains(clusters, c => c.Notes[0].Sustain > c.Notes[1].Position - c.Notes[0].Position);
    }

    [Fact]
    public void A_marked_single_string_ornament_can_target_an_unnamed_chord_tone()
    {
        var project = EofGraceExtendedCases.Extend(new ChartProject
        {
            Arrangements = { new Arrangement { Name = "Lead", Levels = { new ChartLevel() } } }
        }, 0);
        var built = EofNoteBuilder.Build(project.Arrangements[0], project.Beats);
        var ornament = Assert.Single(built, n => n.Raw.PositionMs == 18000);
        Assert.Equal(EofGraceTiming.SynthesizedNoteName, ornament.Raw.Name);
        var back = EofGraceNeighbourRoundTripTests.Import(EofExporter.Export(project).Bytes);
        var target = Assert.Single(back.Levels[0].Chords, c => Math.Abs(c.Position.GetPositionMs(project.Beats) - 18000) <= 1);
        Assert.Equal(5, Assert.Single(target.ChordNotes, n => n.StringIndex == 6).GraceFret);
        Assert.Null(Assert.Single(target.ChordNotes, n => n.StringIndex == 5).GraceFret);
        Assert.InRange(Math.Abs(target.Position.GetPositionMs(project.Beats) - 18000), 0, 1);
    }
}
