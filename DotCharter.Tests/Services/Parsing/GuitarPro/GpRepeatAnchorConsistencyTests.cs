using DotCharter.Core.Linking;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Linking;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>Repeated measures import with the same FHP anchors, so they link.</summary>
public sealed class GpRepeatAnchorConsistencyTests
{
    [Fact]
    public void APhraseRepeatedAfterDifferentMaterialImportsWithTheSameAnchorsAndLinks()
    {
        // m0 holds the hand at fret 8; m1 and m2 play open, 1, 2, 1. The walk alone leaves m1's
        // open downbeat under fret 8 and moves to fret 1 on beat 2, while m2 inherits fret 1
        // from m1: same notes, different hand at the downbeat.
        var high = new FxMeasure().AddVoice(Enumerable.Repeat(FxBeat.Note(0, 4, 8), 4).ToArray());
        var phrase = new FxMeasure().AddVoice(FxBeat.Note(0, 4, 0), FxBeat.Note(0, 4, 1), FxBeat.Note(0, 4, 2),
            FxBeat.Note(0, 4, 1));
        var (project, measures) = Import([high, phrase, phrase, high]);
        var arrangement = Assert.Single(project.Arrangements);
        var level = Assert.Single(arrangement.Levels);

        Assert.Equal(Pattern(level, measures[1]), Pattern(level, measures[2]));
        Assert.Equal(1, InEffect(level, measures[1].Start)!.Fret);
        var exact = MeasureContentIndex.ForAutoLinkVerification(arrangement, project.Beats);
        Assert.Equal(exact.KeyFor(1), exact.KeyFor(2));

        var rule = MeasureContentOptions.Detection;
        LinkSuggestionFinder.LinkAll(project, LinkSuggestionFinder.Find(project, [arrangement], null, rule), rule).Action?.Execute();
        var group = Assert.Single(project.LinkGroups);
        Assert.Equal([measures[1].Start, measures[2].Start], group.Placements.Select(p => p.Position).Order());
    }

    [Fact]
    public void TheMeasureAfterARewrittenCopyKeepsItsPosition()
    {
        var level = new ChartLevel();
        var at = (int beat) => new FractionalPosition(beat);
        // m0 (beats 0-3) and m1 (4-7) play the same; m0 holds fret 2, m1 moves 3 then 5.
        // m2 (8-11) plays something else under the 5 that m1 left in effect.
        foreach (var start in new[] { 0, 4 })
        {
            level.Notes.Add(new GuitarNote { Position = at(start), StringIndex = 0, Fret = 3 });
            level.Notes.Add(new GuitarNote { Position = at(start + 2), StringIndex = 0, Fret = 5 });
        }

        level.Notes.Add(new GuitarNote { Position = at(8), StringIndex = 0, Fret = 5 });
        FretAnchor[] anchors = [new(at(0), 2), new(at(4), 3), new(at(6), 5), new(at(9), 9)];
        var barlines = new[] { at(0), at(4), at(8), at(12) };

        var result = RepeatedMeasureAnchors.Harmonize(level, anchors, barlines, [], 0);

        // One member each: the pattern with the fewest moves (m0's) wins, and m2 gets its 5 restated.
        Assert.Equal(["0 0/1:2", "8 0/1:5", "9 0/1:9"], result.Select(a => $"{a.Position}:{a.Fret}"));
    }

    [Fact]
    public void ACopyIsLeftAloneWhenAHeldNoteWouldLeaveTheHand()
    {
        var level = new ChartLevel();
        var at = (int beat) => new FractionalPosition(beat);
        foreach (var start in new[] { 4, 8, 12 })
        {
            level.Notes.Add(new GuitarNote { Position = at(start + 1), StringIndex = 0, Fret = 3 });
            level.Notes.Add(new GuitarNote { Position = at(start + 2), StringIndex = 0, Fret = 5 });
        }

        // m0 rings fret 12 into m1. m1 moves to fret 3 on beat 1; m2 and m3 inherit it at their
        // downbeats, so their pattern wins, but at m1's downbeat it would cut the held 12.
        level.Notes.Add(new GuitarNote { Position = at(3), StringIndex = 1, Fret = 12, Sustain = at(2) });
        FretAnchor[] anchors = [new(at(0), 11), new(at(5), 3), new(at(16), 7)];
        var barlines = new[] { at(0), at(4), at(8), at(12), at(16) };

        var result = RepeatedMeasureAnchors.Harmonize(level, anchors, barlines, [], 0);

        Assert.Equal(["0 0/1:11", "5 0/1:3", "16 0/1:7"], result.Select(a => $"{a.Position}:{a.Fret}"));
    }

    private static string Pattern(ChartLevel level, MeasureSpan measure) =>
        $"{InEffect(level, measure.Start)?.Fret}|" + string.Join(",", level.Anchors
            .Where(a => a.Position > measure.Start && a.Position < measure.End)
            .Select(a => $"{a.Position - measure.Start}:{a.Fret}/{a.Width}"));

    private static FretAnchor? InEffect(ChartLevel level, FractionalPosition at) =>
        level.Anchors.Where(a => a.Position <= at).MaxBy(a => a.Position);

    private static (ChartProject Project, IReadOnlyList<MeasureSpan> Measures) Import(FxMeasure[] measures)
    {
        var path = LegacyGpFixtureBuilder.WriteFile("5.10", measures);
        try
        {
            var (project, _) = GpAutoLinkQualityTests.Load(path);
            return (project, MeasureGrid.Build(project.Beats));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
