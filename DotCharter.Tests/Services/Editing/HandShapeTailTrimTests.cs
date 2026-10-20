using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     Generating a hand shape takes the TAILS off the sounds it covers — except
///     the ones whose tail carries the technique.
///     <para>
///         The tail is how a charter SETS the bar's length: stretch the last note
///         until the bar comes out right, generate, then go back and shrink every
///         tail again. The owner ran that loop a dozen times in one session
///         ("I had to scroll the fret tails to make the handshape correct length
///         but when I created the handshape the tails had to be selected again
///         and scrolled back"). By the time the bar exists the tails have done
///         their job, so the second half of the loop is dead work.
///     </para>
///     <para>
///         A bend, slide, vibrato, tremolo or link-next is drawn ALONG the tail —
///         shorten it and the technique becomes unreadable — so those sounds keep
///         their length. That is the owner's own rule: "if there is 4 different
///         notes in there and have tail and 2 of them have bend slide vibrato or
///         anything that is indicated on the tail rather than the note head those
///         should be kept at their length for readability."
///     </para>
/// </summary>
public sealed class HandShapeTailTrimTests
{
    [Fact]
    public void APlainSustain_IsDrawnOnTheHead()
    {
        var note = new GuitarNote { Sustain = new FractionalPosition(2) };
        Assert.False(TailTechniques.AreDrawnOnTheTail(note));
    }

    [Theory]
    [InlineData("bend")]
    [InlineData("slide")]
    [InlineData("unpitched")]
    [InlineData("vibrato")]
    [InlineData("tremolo")]
    [InlineData("linknext")]
    public void TheTailTechniques_AreRecognised(string technique)
    {
        var note = new GuitarNote { Sustain = new FractionalPosition(2) };
        Apply(note, technique);
        Assert.True(TailTechniques.AreDrawnOnTheTail(note));
    }

    /// <summary>
    ///     The head techniques, which say as much with no tail — these must NOT
    ///     protect a sustain or the trim would do nothing on a real chart.
    /// </summary>
    [Fact]
    public void HeadTechniques_DoNotProtectATail()
    {
        var note = new GuitarNote
        {
            Sustain = new FractionalPosition(2),
            Mute = MuteType.Palm,
            Hopo = HopoType.HammerOn,
            Harmonic = HarmonicType.Normal,
            Accent = true
        };

        Assert.False(TailTechniques.AreDrawnOnTheTail(note));
    }

    [Fact]
    public void AChordKeepsItsLength_WhenANYOfItsNotesNeedsTheTail()
    {
        var chord = new Chord { Position = new FractionalPosition(0) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3, Sustain = new FractionalPosition(2) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(2) });
        Assert.False(TailTechniques.AreDrawnOnTheTail(chord));

        chord.ChordNotes[1].Vibrato = true;
        Assert.True(TailTechniques.AreDrawnOnTheTail(chord));
    }

    /// <summary>
    ///     The reported shape, end to end: four notes under one bar, two of them
    ///     bent or slid. The two plain ones lose their tails; the two that carry
    ///     the technique keep theirs.
    /// </summary>
    [Fact]
    public void GeneratingABar_TrimsThePlainTailsAndKeepsTheExpressiveOnes()
    {
        var h = new Harness();
        var plainOne = h.AddNote(0, 0, 3, sustain: 2);
        var bent = h.AddNote(1, 1, 5, sustain: 2);
        bent.BendValues.Add(new BendValue { Position = new FractionalPosition(1), Step = 1f });
        var slid = h.AddNote(2, 2, 7, sustain: 2);
        slid.SlideTo = 9;
        var plainTwo = h.AddNote(3, 3, 5, sustain: 2);

        h.SelectAllNotes();
        h.Generator.Generate(h.Track);

        Assert.Equal(FractionalPosition.Zero, plainOne.Sustain);
        Assert.Equal(FractionalPosition.Zero, plainTwo.Sustain);
        // NOT an exact length: ChartSpacingEnforcer runs on every committed edit
        // and only ever SHORTENS, so a tail is capped by the next sound. The
        // claim is that these two still HAVE one.
        Assert.NotEqual(FractionalPosition.Zero, bent.Sustain);
        Assert.NotEqual(FractionalPosition.Zero, slid.Sustain);
    }

    /// <summary>
    ///     The bar keeps the length the tails were used to dial in — that is the
    ///     whole point of trimming them afterwards rather than before.
    /// </summary>
    [Fact]
    public void TheBarKeepsTheLengthTheTailsGaveIt()
    {
        var h = new Harness();
        h.AddNote(0, 0, 3, sustain: 1);
        h.AddNote(2, 1, 5, sustain: 3); // stretched to set the bar's end at 5

        h.SelectAllNotes();
        h.Generator.Generate(h.Track);

        var bar = Assert.Single(h.Level.HandShapes);
        Assert.Equal(new FractionalPosition(0), bar.Position);
        Assert.Equal(new FractionalPosition(5), bar.EndPosition);
    }

    [Fact]
    public void OneUndoTakesTheBarAndTheTrimsBackTogether()
    {
        var h = new Harness();
        var note = h.AddNote(0, 0, 3, sustain: 2);
        h.AddNote(2, 1, 5, sustain: 2);

        h.SelectAllNotes();
        h.Generator.Generate(h.Track);
        Assert.Equal(FractionalPosition.Zero, note.Sustain);

        h.History.Undo();

        Assert.Empty(h.Level.HandShapes);
        Assert.Equal(new FractionalPosition(2), note.Sustain);
    }

    /// <summary>A sound OUTSIDE the new bar is none of the verb's business.</summary>
    [Fact]
    public void ASoundOutsideTheBar_KeepsItsTail()
    {
        var h = new Harness();
        h.AddNote(0, 0, 3, sustain: 1);
        var selected = h.AddNote(2, 1, 5, sustain: 1);
        var faraway = h.AddNote(20, 2, 7, sustain: 2);

        h.Selection.SelectedNotes.Add(h.Level.Notes[0]);
        h.Selection.SelectedNotes.Add(selected);
        h.Generator.Generate(h.Track);

        Assert.Equal(new FractionalPosition(2), faraway.Sustain);
    }

    private static void Apply(GuitarNote note, string technique)
    {
        switch (technique)
        {
            case "bend":
                note.BendValues.Add(new BendValue { Position = note.Position, Step = 1f });
                break;
            case "slide":
                note.SlideTo = 9;
                break;
            case "unpitched":
                note.UnpitchedSlide = true;
                break;
            case "vibrato":
                note.Vibrato = true;
                break;
            case "tremolo":
                note.Tremolo = true;
                break;
            case "linknext":
                note.LinkNext = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(technique), technique, "unknown technique");
        }
    }

    private sealed class Harness
    {
        private readonly ChartProject _project = new();

        public Harness()
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            var service = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            for (var i = 0; i < 64; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });

            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(Arrangement);
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            Generator = new HandShapeFromSelectionGenerator(
                new FingeringGenerationService(), service, Selection, logger);
            Track = new TrackEditorContext(Arrangement, Selection)
            {
                Project = _project,
                Level = Arrangement.Levels[0]
            };
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public HandShapeFromSelectionGenerator Generator { get; }
        public Arrangement Arrangement { get; }
        public TrackEditorContext Track { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        public GuitarNote AddNote(int beat, int stringIndex, int fret, int sustain)
        {
            var note = new GuitarNote
            {
                Position = new FractionalPosition(beat),
                StringIndex = stringIndex,
                Fret = fret,
                Sustain = new FractionalPosition(sustain)
            };
            Level.Notes.Add(note);
            return note;
        }

        public void SelectAllNotes()
        {
            foreach (var note in Level.Notes) Selection.SelectedNotes.Add(note);
        }
    }
}
