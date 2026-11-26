using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     #925: a fully dead strum between two strums of a DIFFERENT grip (a tab writes
///     the dead strum one fret higher than the chord it sits between) must not open a
///     hand shape of its own, or its frame covers the chord that is actually held.
///     It joins the bar of the chord before it and keeps its own frets, mute and
///     template, which is what EOF's Rocksmith export does with the same file
///     (rs.c: a fully string-muted chord with no fingering after a chord is high
///     density and stays in the previous chord's hand shape). Charter's importer
///     keeps the frets and the full mute too.
/// </summary>
public sealed class GpFullMuteHandShapeTests
{
    private static readonly (int String, int Fret)[] Played = [(3, 10), (4, 12), (5, 13)];
    private static readonly (int String, int Fret)[] Dead = [(3, 11), (4, 13), (5, 14)];

    [Fact]
    public void FullMuteBetweenTwoStrumsOfAnotherGrip_JoinsTheirHandShape_AndKeepsItsShape()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Strum(0, MuteType.None, Played));
        level.Chords.Add(Strum(1, MuteType.Full, Dead));
        level.Chords.Add(Strum(2, MuteType.None, Played));

        GpPostProcessor.Run(level, arr);

        var bar = Assert.Single(level.HandShapes);
        var played = arr.ChordTemplates.Single(t => t.Id == bar.TemplateId);
        Assert.Equal(10, played.Frets[3]);
        Assert.Equal(new FractionalPosition(0), bar.Position);
        Assert.True(bar.EndPosition.CompareTo(new FractionalPosition(2)) > 0);

        var mute = level.Chords.Single(c => c.IsFullyMuted());
        Assert.Equal(new[] { 11, 13, 14 }, mute.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret).ToArray());
        var muteGrip = arr.ChordTemplates.Single(t => t.Id == mute.TemplateId);
        Assert.Equal(11, muteGrip.Frets[3]);
        Assert.Equal(13, muteGrip.Frets[4]);
        Assert.Equal(14, muteGrip.Frets[5]);
        // The later strum repeats the grip inside the bar, so it plays the grip's unnamed twin.
        var strums = level.Chords.Where(c => !c.IsFullyMuted()).ToList();
        Assert.Equal(bar.TemplateId, strums[0].TemplateId);
        var twin = arr.ChordTemplates.Single(t => t.Id == strums[1].TemplateId);
        Assert.True(twin.HasSameShapeAs(played));
        Assert.Equal(string.Empty, twin.Name);
    }

    [Fact]
    public void FullMuteTheFileFingered_KeepsItsOwnHandShape()
    {
        // EOF's condition: only a mute with NO fingering of its own joins.
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Strum(0, MuteType.None, Played));
        level.Chords.Add(Strum(1, MuteType.Full, Dead));
        var fingers = new GpAuthoredFingering();
        fingers.Set(3, new FractionalPosition(1), 1);

        ChordHandShapeBuilder.BuildAndTrim(level, arr, fingers);

        Assert.Equal(2, level.HandShapes.Count);
    }

    [Fact]
    public void FullMuteOpeningARun_StillGetsItsOwnHandShape()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Strum(0, MuteType.Full, Dead));
        level.Chords.Add(Strum(1, MuteType.None, Played));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        Assert.Equal(2, level.HandShapes.Count);
    }

    [Fact]
    public void DeadStrumsBeforeAPlayedChordOfTheSameFrets_NeverOpenItsHandShape()
    {
        // A tab writes two dead strums at the frets of the chord that follows. A bar
        // opened on the dead strums would make the played chord a repeat inside it,
        // which Rocksmith draws with no frame: the played chord starts its own bar.
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Strum(0, MuteType.Full, Dead));
        level.Chords.Add(Strum(1, MuteType.Full, Dead));
        level.Chords.Add(Strum(2, MuteType.None, Dead));
        level.Chords.Add(Strum(3, MuteType.Full, Dead));

        ChordHandShapeBuilder.BuildAndTrim(level, arr);

        var bars = level.HandShapes.OrderBy(h => h.Position).ToList();
        Assert.Equal(2, bars.Count);
        Assert.Equal(new FractionalPosition(0), bars[0].Position);
        Assert.Equal(new FractionalPosition(2), bars[0].EndPosition);
        Assert.Equal(new FractionalPosition(2), bars[1].Position);
        Assert.True(bars[1].EndPosition.CompareTo(new FractionalPosition(3)) > 0);
    }

    [Fact]
    public void EditorGenerateHandShapes_PointsAJoinedMuteAtItsOwnGrip()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var level = arr.Levels[0];
        level.Chords.Add(Strum(0, MuteType.None, Played));
        level.Chords.Add(Strum(1, MuteType.Full, Dead));
        foreach (var c in level.Chords) c.TemplateId = -1;

        var plan = ArrangementHandShapeGenerator.Plan(arr);
        var bar = Assert.Single(plan.Levels.SelectMany(l => l.HandShapes));
        Assert.Equal(2, bar.Chords.Count);
        Assert.Equal(11, bar.GripOf(level.Chords[1]).Frets[3]);
        Assert.Equal(10, bar.GripOf(level.Chords[0]).Frets[3]);
    }

    private static Chord Strum(int beat, MuteType mute, (int String, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), NotationDuration = new Fraction(1, 1) };
        foreach (var (s, f) in notes)
        {
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = s,
                Fret = f,
                Mute = mute,
                Sustain = new FractionalPosition(1)
            });
        }

        return chord;
    }
}
