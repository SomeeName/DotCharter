using DotCharter.Core.Diff;

namespace DotCharter.Tests.Core;

/// <summary>
///     <c>ChartDiffResult.IsEmpty</c> is the documented LAN "has update"
///     signal, so a field the diff cannot see is a field that lets a peer be
///     told a changed chart is unchanged. Several real, exportable authoring
///     decisions were simply never compared.
/// </summary>
public sealed class ChartDiffCoverageTests
{
    private static (Arrangement Old, Arrangement New) Pair(Action<GuitarNote> mutate)
    {
        static Arrangement Build()
        {
            var arrangement = new Arrangement { Name = "Lead" };
            var level = new ChartLevel();
            level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = 5 });
            arrangement.Levels.Add(level);
            return arrangement;
        }

        var oldArr = Build();
        var newArr = Build();
        mutate(newArr.Levels[0].Notes[0]);
        return (oldArr, newArr);
    }

    private static (Arrangement Old, Arrangement New) ChordPair(Action<Chord> mutate)
    {
        static Arrangement Build()
        {
            var arrangement = new Arrangement { Name = "Lead" };
            var level = new ChartLevel();
            var chord = new Chord { TemplateId = 0 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
            level.Chords.Add(chord);
            arrangement.Levels.Add(level);
            return arrangement;
        }

        var oldArr = Build();
        var newArr = Build();
        mutate(newArr.Levels[0].Chords[0]);
        return (oldArr, newArr);
    }

    [Fact]
    public void AnIdenticalChartStillDiffsAsEmpty()
    {
        var (a, b) = Pair(_ => { });

        Assert.True(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void PassOtherNotesOnANoteIsSeen()
    {
        var (a, b) = Pair(n => n.PassOtherNotes = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void LinkNextOnANoteIsSeen()
    {
        var (a, b) = Pair(n => n.LinkNext = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void SplitIntoNotesOnAChordIsSeen()
    {
        var (a, b) = ChordPair(c => c.SplitIntoNotes = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void ForceNoNotesOnAChordIsSeen()
    {
        var (a, b) = ChordPair(c => c.ForceNoNotes = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void PerStringAccentIsSeen()
    {
        // Only the CHORD-level accent was compared, so accenting one string of
        // a strum reported no change at all.
        var (a, b) = ChordPair(c => c.ChordNotes[1].Accent = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }

    [Fact]
    public void PerStringIgnoreIsSeen()
    {
        var (a, b) = ChordPair(c => c.ChordNotes[0].Ignore = true);

        Assert.False(ChartDiffEngine.Diff(a, b).IsEmpty);
    }
}
