using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     The FHP generator's own output must not trip the FHP findings (D50-D56, <see cref="FretHandPositionRule" />):
///     over every window of the chart-style fixture (<see cref="ChartStyleFloorTests" />, windows of the owner's
///     finished charts) the anchors it places sit on notes (or hand-shape starts), never move the hand on a
///     hammer-on and stay on the comfortable frets; and a bend that would fall to the pinky moves its anchor up a
///     fret when the whole span still fits (<c>BendFingerShift</c>), else stays a real D56.
/// </summary>
public sealed class FhpGeneratorFindingsTests(ITestOutputHelper output)
{
    private static List<BeatMarker> Beats(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static List<ArrangementIssue> Findings(Arrangement arr, int beats)
    {
        var issues = new List<ArrangementIssue>();
        new FretHandPositionRule().Validate(ArrangementValidationContext.Build(arr, Beats(beats), null), issues);
        return issues;
    }

    [Fact]
    public void GeneratedAnchorsOnTheFixtureWindows_TripNoFhpFinding()
    {
        var windows = ChartStyleFloorTests.RunAll();
        var all = new List<ArrangementIssue>();
        foreach (var w in windows)
        {
            w.Arrangement.Levels.Clear();
            foreach (var a in w.Anchors) w.Level.Anchors.Add(a);
            w.Arrangement.Levels.Add(w.Level);
            all.AddRange(Findings(w.Arrangement, 128));
        }

        output.WriteLine($"{windows.Count} windows, {windows.Sum(w => w.Anchors.Count)} anchors, {all.Count} findings");
        foreach (var group in all.GroupBy(i => i.Code)) output.WriteLine($"{group.Key}: {group.Count()}");
        Assert.True(windows.Sum(w => w.Anchors.Count) > 100, "the fixture produced almost no anchors to judge");
        Assert.Empty(all);
    }

    // One lick on the G string, one note per beat; a bent note bends a whole step.
    private static (Arrangement Arr, ChartLevel Level) Lick(params (int Fret, int? SlideTo, bool Bend)[] notes)
    {
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) arr.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel();
        arr.Levels.Add(level);
        for (var i = 0; i < notes.Length; i++)
        {
            var (fret, slideTo, bend) = notes[i];
            var note = new GuitarNote { Position = new FractionalPosition(i), StringIndex = 3, Fret = fret };
            if (slideTo is { } to)
            {
                note.SlideTo = to;
                note.Sustain = new FractionalPosition(1);
            }

            if (bend) note.BendValues.Add(new BendValue(new FractionalPosition(i, new Fraction(1, 2)), 1));
            level.Notes.Add(note);
        }

        foreach (var a in new FretAnchorGenerationService().GenerateAnchors(level, chordTemplates: [], capo: 0, tuning: arr))
            level.Anchors.Add(a);
        return (arr, level);
    }

    [Fact]
    public void ABendThatFallsToThePinky_MovesTheAnchorUpAFret_WhenTheWholeSpanStillFits()
    {
        // Index on 2, a slide 3 -> 5 lands with the middle finger (FHP 4), then 5 and a bend on 7: from FHP 4 the
        // bend is the pinky's, from FHP 5 the ring finger's, and 5 / 7 (and the landing on 5) all fit 5-8.
        var (arr, level) = Lick((2, null, false), (3, 5, false), (5, null, false), (7, null, true));

        output.WriteLine(string.Join(", ", level.Anchors.Select(a => $"{a.Position}:{a.Fret}")));
        Assert.Equal(5, level.Anchors.Last().Fret);
        Assert.DoesNotContain(Findings(arr, 16), i => i.Type == ArrangementIssueType.BendNeedsPinky);
    }

    [Fact]
    public void ABendThatNeedsThePinky_KeepsItsAnchor_WhenAnotherNoteSitsOnTheIndexFret()
    {
        // 5, 7, 8, 5, 7, then a bend on 8: FHP 6 would put the bend on the ring finger but leave the 5s off the
        // hand, so the anchor stays on 5 and D56 is a real finding for the charter.
        var (arr, level) = Lick((5, null, false), (7, null, false), (8, null, false), (5, null, false), (7, null, false),
            (8, null, true));

        output.WriteLine(string.Join(", ", level.Anchors.Select(a => $"{a.Position}:{a.Fret}")));
        Assert.All(level.Anchors, a => Assert.Equal(5, a.Fret));
        Assert.Contains(Findings(arr, 16), i => i.Type == ArrangementIssueType.BendNeedsPinky);
    }

    [Fact]
    public void ABendAtTheTopOfTheNeck_KeepsItsAnchor_WhenTheMovedBoxWouldLeaveTheNeck()
    {
        // 21 and a bend on 24: FHP 21 is the highest four-fret box, so the bend stays on the pinky.
        var (_, level) = Lick((21, null, false), (24, null, true));

        Assert.All(level.Anchors, a => Assert.Equal(21, a.Fret));
    }
}
