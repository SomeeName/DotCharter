using DotCharter.Services.Editing.HandShapes;
using static DotCharter.Tests.Services.Editing.HandShapes.ChordHandShapePlannerTests;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     A repeated-strum run continues through GRIP TONES, single notes the grip
///     itself plays (<see cref="ChordHandShapePlanOptions.ContinueThroughGripTones" />),
///     so strum, arpeggio of the grip, strum is one bar, as the charters draw it. Any
///     other note, a sustain technique and a rest still end the run, and
///     <see cref="ChordHandShapePlanner.Plan" /> counts why each run ended.
/// </summary>
public sealed class ChordHandShapeGripToneTests
{
    // An open-position C: x32010, strings 1..5 (string 3 open).
    private static readonly (int, int)[] C = [(1, 3), (2, 2), (3, 0), (4, 1), (5, 0)];

    private static readonly ChordHandShapePlanOptions On = new(ContinueThroughGripTones: true);

    private static GuitarNote Note(double beat, int stringIndex, int fret, double beats = 0.5) => new()
    {
        Position = Pos(beat),
        StringIndex = stringIndex,
        Fret = fret,
        NotationDuration = new Fraction((int)Math.Round(beats * 4), 4)
    };

    private static Chord Strum(double beat, double beats = 1)
    {
        var chord = ChordHandShapePlannerTests.Chord(beat, beats, C);
        chord.NotationDuration = new Fraction((int)Math.Round(beats * 4), 4);
        return chord;
    }

    private static List<PlannedHandShape> Plan(ChartLevel level, ChordHandShapePlanOptions options,
        Dictionary<HandShapeRunBreak, int>? breaks = null) =>
        ChordHandShapePlanner.Plan(level, new FretGrips(), [], options, breaks);

    /// <summary>strum (1 beat), C's own notes for a beat, strum.</summary>
    private static ChartLevel StrumArpeggioStrum(params GuitarNote[] between)
    {
        var level = new ChartLevel();
        level.Chords.Add(Strum(0));
        foreach (var note in between) level.Notes.Add(note);
        level.Chords.Add(Strum(2));
        return level;
    }

    [Fact]
    public void FrettedAndOpenGripTones_KeepOneBar()
    {
        // The B string's 1st fret and the open G string are the grip's own; the open
        // high e is an open string the grip does not fret.
        var level = StrumArpeggioStrum(Note(1, 4, 1), Note(1.5, 3, 0));

        var bar = Assert.Single(Plan(level, On));

        Assert.Equal(2, bar.Chords.Count);
        Assert.Equal(Pos(0), bar.Position);
        Assert.Equal(Pos(3), bar.EndPosition);
    }

    [Fact]
    public void WithoutTheOption_TheSameNotesBreakTheRun()
    {
        var level = StrumArpeggioStrum(Note(1, 4, 1), Note(1.5, 3, 0));
        var breaks = new Dictionary<HandShapeRunBreak, int>();

        Assert.Equal(2, Plan(level, default, breaks).Count);
        Assert.Equal(1, breaks[HandShapeRunBreak.ForeignSound]);
    }

    [Fact]
    public void ANoteTheGripDoesNotHold_BreaksTheRun()
    {
        // B string fret 3 is not the grip's fret 1.
        var level = StrumArpeggioStrum(Note(1, 4, 1), Note(1.5, 4, 3));
        var breaks = new Dictionary<HandShapeRunBreak, int>();

        Assert.Equal(2, Plan(level, On, breaks).Count);
        Assert.Equal(1, breaks[HandShapeRunBreak.ForeignSound]);
    }

    [Fact]
    public void AnOpenStringTheGripFrets_IsNotAGripTone()
    {
        // The A string is fretted at 3 under the grip: playing it open lifts a finger.
        var level = StrumArpeggioStrum(Note(1, 1, 0));

        Assert.Equal(2, Plan(level, On).Count);
    }

    [Fact]
    public void AGripToneWithASustainTechnique_BreaksTheRun()
    {
        var vibrato = Note(1, 4, 1);
        vibrato.Vibrato = true;
        var level = StrumArpeggioStrum(vibrato);

        Assert.Equal(2, Plan(level, On).Count);
    }

    [Fact]
    public void ARest_IsMeasuredFromTheLastGripTone()
    {
        var split = On with { SplitRunsOnRests = true };

        // The grip tones fill the beat between the strums: no rest, one bar.
        Assert.Single(Plan(StrumArpeggioStrum(Note(1, 4, 1), Note(1.5, 3, 0)), split));

        // A half-beat rest after the last grip tone still splits the run.
        var breaks = new Dictionary<HandShapeRunBreak, int>();
        Assert.Equal(2, Plan(StrumArpeggioStrum(Note(1, 4, 1)), split, breaks).Count);
        Assert.Equal(1, breaks[HandShapeRunBreak.Rest]);
    }

    [Fact]
    public void BreakTelemetry_CountsOneReasonPerBoundary()
    {
        var level = new ChartLevel();
        level.Chords.Add(Strum(0));
        level.Chords.Add(ChordHandShapePlannerTests.Chord(1, 1, (0, 3), (1, 5))); // another grip
        level.Chords.Add(ChordHandShapePlannerTests.Chord(6, 1, (0, 3), (1, 5))); // 4 beats later
        var breaks = new Dictionary<HandShapeRunBreak, int>();

        Assert.Equal(3, Plan(level, On, breaks).Count);
        Assert.Equal(1, breaks[HandShapeRunBreak.OtherGrip]);
        Assert.Equal(1, breaks[HandShapeRunBreak.Gap]);
        Assert.Equal(2, breaks.Values.Sum());
    }

    /// <summary>The import's grip rule: a template per distinct set of frets.</summary>
    private sealed class FretGrips : IHandShapeGripSource
    {
        public ChordTemplate GripFor(Chord chord, IList<ChordTemplate> minted)
        {
            foreach (var t in minted)
            {
                if (Holds(t, chord)) return t;
            }

            var template = new ChordTemplate { Id = minted.Count };
            foreach (var cn in chord.ChordNotes) template.Frets[cn.StringIndex] = cn.Fret;
            minted.Add(template);
            return template;
        }

        public bool Holds(ChordTemplate grip, Chord chord) =>
            grip.Frets.Count == chord.ChordNotes.Count &&
            chord.ChordNotes.All(cn => grip.Frets.TryGetValue(cn.StringIndex, out var f) && f == cn.Fret);
    }
}
