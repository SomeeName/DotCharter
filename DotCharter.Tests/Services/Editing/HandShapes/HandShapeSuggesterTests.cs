using DotCharter.Services.Editing.HandShapes;
using static DotCharter.Tests.Services.Editing.HandShapes.ChordHandShapePlannerTests;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     The hand-shape suggester (#491) on synthetic runs: one grip → one proposal, a position shift
///     or a second chord shape → two, a scale or an already covered run → none. The corpus side
///     (precision and recall against the owner's finished charts) is
///     <see cref="HandShapeSuggesterCorpusFloorTests" />.
/// </summary>
public sealed class HandShapeSuggesterTests
{
    /// <summary>An open C (x32010) picked up and back down, half a beat apart.</summary>
    private static readonly (int String, int Fret)[] COpenArpeggio =
        [(1, 3), (2, 2), (3, 0), (4, 1), (5, 0), (4, 1), (3, 0), (2, 2)];

    private static readonly Dictionary<int, int> COpen = new() { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 };

    private static GuitarNote Note(double beat, int stringIndex, int fret) =>
        new() { Position = Pos(beat), StringIndex = stringIndex, Fret = fret };

    private static void Pick(ChartLevel level, double startBeat, params (int String, int Fret)[] notes)
    {
        for (var i = 0; i < notes.Length; i++)
            level.Notes.Add(Note(startBeat + i * 0.5, notes[i].String, notes[i].Fret));
    }

    private static ChordTemplate Template(int id, string name, Dictionary<int, int> frets) =>
        new() { Id = id, Name = name, Frets = new Dictionary<int, int>(frets) };

    [Fact]
    public void AnArpeggiatedChord_IsOneProposal_OverEveryNote()
    {
        var arr = Arr();
        Pick(arr.Levels[0], 0, COpenArpeggio);

        var s = Assert.Single(HandShapeSuggester.Suggest(arr, arr.Levels[0]));

        Assert.Equal(Pos(0), s.Position);
        Assert.Equal(Pos(4.5), s.EndPosition); // a beat past the tail-less last note, nothing after it
        Assert.Equal(COpen, s.Frets);
        Assert.Equal(8, s.NoteCount);
        Assert.Null(s.Existing);
        Assert.All(arr.Levels[0].Notes, n => Assert.True(s.Covers(n.Position)));
    }

    [Fact]
    public void ADeclaredTemplate_IsReused_ExactFirst_ElseTheShapeContainingTheGrip()
    {
        var arr = Arr();
        var full = Template(0, "C/G", new Dictionary<int, int> { [0] = 3, [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 });
        arr.ChordTemplates.Add(full);
        Pick(arr.Levels[0], 0, COpenArpeggio);

        Assert.Same(full, Assert.Single(HandShapeSuggester.Suggest(arr, arr.Levels[0])).Existing);

        var exact = Template(1, "C", COpen);
        arr.ChordTemplates.Add(exact);
        Assert.Same(exact, Assert.Single(HandShapeSuggester.Suggest(arr, arr.Levels[0])).Existing);
    }

    [Fact]
    public void APositionShift_SplitsTheRun()
    {
        // An Am arpeggio at the 5th fret, then the same figure at the 8th.
        var arr = Arr();
        Pick(arr.Levels[0], 0, (1, 7), (2, 7), (3, 5), (4, 5));
        Pick(arr.Levels[0], 2, (1, 10), (2, 10), (3, 8), (4, 8));

        var found = HandShapeSuggester.Suggest(arr, arr.Levels[0]);

        Assert.Equal(2, found.Count);
        Assert.Equal((Pos(0), Pos(2)), (found[0].Position, found[0].EndPosition));
        Assert.Equal(Pos(2), found[1].Position);
        Assert.Equal(8, found[1].Frets[3]);
    }

    [Fact]
    public void AScaleRun_IsNotAGrip()
    {
        // C major in second position: every string plays two or three frets.
        var arr = Arr();
        Pick(arr.Levels[0], 0, (1, 3), (1, 5), (2, 2), (2, 3), (2, 5), (3, 2), (3, 4), (3, 5), (4, 3), (4, 5));

        Assert.Empty(HandShapeSuggester.Suggest(arr, arr.Levels[0]));
    }

    [Fact]
    public void ARunABarAlreadyCovers_IsNotProposedAgain()
    {
        var arr = Arr();
        Pick(arr.Levels[0], 0, COpenArpeggio);
        arr.Levels[0].HandShapes.Add(new HandShape { Position = Pos(0), EndPosition = Pos(4.5) });

        Assert.Empty(HandShapeSuggester.Suggest(arr, arr.Levels[0]));
    }

    [Fact]
    public void AThreeStringGrip_NeedsADeclaredShapeContainingIt()
    {
        // Three-string figures are mostly riffs; one the chart plays as a chord elsewhere is a grip.
        var arr = Arr();
        Pick(arr.Levels[0], 0, (1, 3), (2, 2), (3, 0), (2, 2), (1, 3), (2, 2));

        Assert.Empty(HandShapeSuggester.Suggest(arr, arr.Levels[0]));

        var c = Template(0, "C", COpen);
        arr.ChordTemplates.Add(c);
        Assert.Same(c, Assert.Single(HandShapeSuggester.Suggest(arr, arr.Levels[0])).Existing);
    }

    [Fact]
    public void ARootAlternatingWithAChord_IsOneGrip()
    {
        // The Martian Rhythm figure: a bass note, then the chord over it, four times.
        var arr = Arr();
        for (var i = 0; i < 4; i++)
        {
            arr.Levels[0].Notes.Add(Note(i, 0, 5));
            arr.Levels[0].Chords.Add(Chord(i + 0.5, 0, (1, 7), (2, 7), (3, 6)));
        }

        var s = Assert.Single(HandShapeSuggester.Suggest(arr, arr.Levels[0]));

        Assert.Equal((8, 4), (s.SoundCount, s.NoteCount));
        Assert.Equal(new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7, [3] = 6 }, s.Frets);
    }

    [Fact]
    public void ASecondChordShape_OpensASecondBar()
    {
        var arr = Arr();
        var level = arr.Levels[0];
        (int, int)[] a = [(1, 7), (2, 7), (3, 6)];
        (int, int)[] b = [(2, 7), (3, 6), (4, 5)];
        level.Notes.Add(Note(0, 0, 5));
        level.Chords.Add(Chord(0.5, 0, a));
        level.Notes.Add(Note(1, 0, 5));
        level.Chords.Add(Chord(1.5, 0, a));
        level.Chords.Add(Chord(2, 0, b));
        level.Notes.Add(Note(2.5, 0, 5));
        level.Chords.Add(Chord(3, 0, b));
        level.Notes.Add(Note(3.5, 0, 5));

        var found = HandShapeSuggester.Suggest(arr, level);

        Assert.Equal(2, found.Count);
        Assert.Equal((Pos(0), Pos(2)), (found[0].Position, found[0].EndPosition));
        Assert.Equal(Pos(2), found[1].Position);
    }

    [Fact]
    public void AnAnchorInsideTheRun_SplitsIt()
    {
        // An anchor inside a bar is I20; the run breaks where the hand moves.
        var arr = Arr();
        Pick(arr.Levels[0], 0, COpenArpeggio);
        arr.Levels[0].Anchors.Add(new FretAnchor { Position = Pos(2), Fret = 1 });

        var found = HandShapeSuggester.Suggest(arr, arr.Levels[0]);

        Assert.Equal(2, found.Count);
        Assert.Equal(Pos(2), found[0].EndPosition);
        Assert.Equal(Pos(2), found[1].Position);
    }

    [Fact]
    public void ALongRest_SplitsTheRun()
    {
        var arr = Arr();
        Pick(arr.Levels[0], 0, (1, 3), (2, 2), (3, 0), (4, 1));
        Pick(arr.Levels[0], 6, (4, 1), (3, 0), (2, 2), (1, 3));

        Assert.Equal(2, HandShapeSuggester.Suggest(arr, arr.Levels[0]).Count);
    }

    [Fact]
    public void AFretHandMute_NeitherBreaksTheRunNorJoinsTheGrip()
    {
        var arr = Arr();
        Pick(arr.Levels[0], 0, COpenArpeggio);
        arr.Levels[0].Notes.Add(new GuitarNote { Position = Pos(1.25), StringIndex = 0, Fret = 7, Mute = MuteType.Full });

        var s = Assert.Single(HandShapeSuggester.Suggest(arr, arr.Levels[0]));

        Assert.False(s.Frets.ContainsKey(0));
    }

    [Fact]
    public void ABassArrangement_GetsNoSuggestions()
    {
        // Bass charters never bar single notes (0 of 1,101 proposed notes in the corpus).
        var arr = Arr();
        arr.ArrangementType = ArrangementType.Bass;
        Pick(arr.Levels[0], 0, COpenArpeggio);

        Assert.Empty(HandShapeSuggester.Suggest(arr, arr.Levels[0]));
    }

    [Fact]
    public void ARunOfChordsAlone_IsThePlannersJob()
    {
        var arr = Arr();
        for (var i = 0; i < 4; i++) arr.Levels[0].Chords.Add(Chord(i, 0, (1, 3), (2, 2), (3, 0), (4, 1)));

        Assert.Empty(HandShapeSuggester.Suggest(arr, arr.Levels[0]));
    }

    [Fact]
    public void AGripTheFingeringHeuristicCannotHold_IsNotAGrip()
    {
        // Frets 1 and 5 on neighbouring strings: a five-fret stretch.
        Assert.False(HandShapeSuggester.CanHold(new Dictionary<int, int> { [0] = 1, [1] = 5, [2] = 3 }, 0, 6, 3));
        Assert.True(HandShapeSuggester.CanHold(new Dictionary<int, int> { [0] = 5, [1] = 7, [2] = 7, [3] = 6 }, 0, 6, 3));
        // The capo's fret is open: it neither widens the spread nor needs a finger.
        Assert.True(HandShapeSuggester.CanHold(new Dictionary<int, int> { [0] = 2, [1] = 6, [2] = 5 }, 2, 6, 3));
    }
}
