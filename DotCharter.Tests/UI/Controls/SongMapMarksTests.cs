using DotCharter.UI.Controls;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The song map's use marks (#485): <see cref="SongMapMarks.Collect" /> — the
///     one path the map draws both the bulk-edit preview and the selected chords
///     and hand shapes through — answers one mark per sound, where the sound is;
///     <see cref="ChordUseHighlight" /> splits a preview by level and steps
///     between its marks; and a click within a few pixels of a mark seeks to the
///     mark itself (<see cref="MinimapDragController.SnapToMark" />).
///     <see cref="DotCharter.Tests.Perf.MinimapUseMarksUiPerfSmokeTests" /> proves
///     the control really draws these sets.
/// </summary>
public sealed class SongMapMarksTests
{
    private const double BeatMs = 500;
    private readonly Arrangement _arr = new() { Name = "Lead", ArrangementType = ArrangementType.Lead };
    private readonly List<BeatMarker> _beats =
        [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * BeatMs, Bpm = 120 })];

    public SongMapMarksTests()
    {
        _arr.Levels.Add(new ChartLevel());
        _arr.Levels.Add(new ChartLevel());
    }

    [Fact]
    public void Collect_IsOneMarkPerSound_AtItsTime()
    {
        var chords = new List<Chord> { Chord(0, 4), Chord(0, 10) };
        var bars = new List<HandShape> { Bar(0, 20, 24) };
        var marks = new List<SongMapMark>();
        const double scale = 0.01; // px per ms
        const double height = 40;

        SongMapMarks.Collect(chords, bars, _beats, scale, height, marks);

        Assert.Equal([chords[0], chords[1], bars[0]], marks.Select(m => m.Sound));
        Assert.Equal(4 * BeatMs * scale, marks[0].Bounds.Center.X, 6);
        Assert.Equal(10 * BeatMs * scale, marks[1].Bounds.Center.X, 6);
        Assert.Equal(height, marks[0].Bounds.Bottom, 6);
        // A hand shape spans its length; a chord is a tick.
        Assert.Equal(20 * BeatMs * scale, marks[2].Bounds.Left, 6);
        Assert.Equal(24 * BeatMs * scale, marks[2].Bounds.Right, 6);
        Assert.Equal(height, marks[2].Bounds.Bottom, 6);
        Assert.True(marks[0].Bounds.Height > marks[2].Bounds.Height, "a chord's tick stands taller than a bar");
    }

    [Fact]
    public void Collect_ReusesTheCallersList()
    {
        var marks = new List<SongMapMark>();
        SongMapMarks.Collect([Chord(0, 1), Chord(0, 2)], [], _beats, 1, 10, marks);
        SongMapMarks.Collect([Chord(0, 3)], [], _beats, 1, 10, marks);

        Assert.Single(marks);
    }

    [Fact]
    public void TheHighlight_SplitsByLevel_AndCountsTheRest()
    {
        var here = Chord(0, 4);
        var bar = Bar(0, 0, 2);
        var there = Chord(1, 4);
        var loose = new Chord { Position = new FractionalPosition(1) }; // on no level

        var highlight = ChordUseHighlight.From([here, there, loose], [bar], _arr);

        Assert.Equal(3, highlight.Count);
        Assert.Equal([here], highlight.On(_arr.Levels[0].Chords).Chords);
        Assert.Equal([bar], highlight.On(_arr.Levels[0].Chords).HandShapes);
        Assert.Equal(1, highlight.ElsewhereThan(_arr.Levels[0].Chords));
        Assert.Same(ChordUseHighlight.None, ChordUseHighlight.From([loose], [], _arr));
    }

    [Fact]
    public void Step_WalksTheMarksInTimeOrder_AndWrapsRound()
    {
        var level = _arr.Levels[0].Chords;
        var highlight = ChordUseHighlight.From([Chord(0, 10), Chord(0, 2)], [Bar(0, 6, 8)], _arr);

        Assert.Equal(2 * BeatMs, highlight.Step(level, _beats, 0, 1));
        Assert.Equal(6 * BeatMs, highlight.Step(level, _beats, 2 * BeatMs, 1)); // ON a mark: the next one
        Assert.Equal(2 * BeatMs, highlight.Step(level, _beats, 10 * BeatMs, 1)); // past the last: wraps
        Assert.Equal(6 * BeatMs, highlight.Step(level, _beats, 10 * BeatMs, -1));
        Assert.Equal(10 * BeatMs, highlight.Step(level, _beats, 2 * BeatMs, -1)); // before the first: wraps
        Assert.Null(ChordUseHighlight.None.Step(level, _beats, 0, 1));
    }

    [Fact]
    public void AClickNearAMark_SeeksToTheMark_AndFartherPassesThrough()
    {
        var level = _arr.Levels[0].Chords;
        var highlight = ChordUseHighlight.From([Chord(0, 10)], [], _arr);
        const double width = 1000;
        const double totalMs = 32000; // 32 ms per map pixel
        var markMs = 10 * BeatMs;
        var px = totalMs / width;

        Assert.Equal(markMs, MinimapDragController.SnapToMark(markMs + 3 * px, width, totalMs, highlight, level, _beats));
        Assert.Equal(markMs, MinimapDragController.SnapToMark(markMs - 3 * px, width, totalMs, highlight, level, _beats));
        Assert.Equal(markMs + 6 * px,
            MinimapDragController.SnapToMark(markMs + 6 * px, width, totalMs, highlight, level, _beats));
        Assert.Equal(markMs + 3 * px,
            MinimapDragController.SnapToMark(markMs + 3 * px, width, totalMs, ChordUseHighlight.None, level, _beats));
    }

    [Fact]
    public void AClick_OnlySnapsToMarksOfTheLevelOnScreen()
    {
        var highlight = ChordUseHighlight.From([Chord(1, 10)], [], _arr);

        Assert.Equal(5010, MinimapDragController.SnapToMark(5010, 1000, 32000, highlight, _arr.Levels[0].Chords, _beats));
    }

    private Chord Chord(int level, int beat)
    {
        var chord = new Chord { Position = new FractionalPosition(beat) };
        _arr.Levels[level].Chords.Add(chord);
        return chord;
    }

    private HandShape Bar(int level, int start, int end)
    {
        var bar = new HandShape { Position = new FractionalPosition(start), EndPosition = new FractionalPosition(end) };
        _arr.Levels[level].HandShapes.Add(bar);
        return bar;
    }
}
