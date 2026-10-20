using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     A <see cref="ChordTemplate" /> is SHARED — one instance backs every chord
///     and hand shape with that shape. Three editing paths changed a chord's
///     NOTES without repointing <c>TemplateId</c>, so the chord and its template
///     ended up describing different shapes: the SNG/RS-XML export wrote the
///     chord under a chordId whose template disagreed, the 3D hand-shape bar drew
///     the old shape, every OTHER chord on that template was misrepresented, and
///     <c>RefretPlanner.IsChordPinned</c> pinned the chord forever as template
///     drift. Nothing in the app repaired it.
/// </summary>
public sealed class ChordTemplateIntegrityTests
{
    [Fact]
    public void RightClickAddChordString_RepointsTheChordInsteadOfDivergingFromItsSharedTemplate()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));
        var sibling = h.AddChord(6, 0, (0, 3), (1, 5));

        h.Service.HandleRightClickChord(chord, 2);

        Assert.Equal(3, chord.ChordNotes.Count);
        // The chord moved to a template describing what it now plays…
        var resolved = h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(chord), resolved.Frets);
        // …and the shared one is untouched, so the sibling still means what it did.
        Assert.Equal(2, shared.Frets.Count);
        Assert.Equal(0, sibling.TemplateId);
    }

    [Fact]
    public void RightClickRemoveChordString_AlsoRepoints()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (0, 3), (1, 5), (2, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5), (2, 5));

        h.Service.HandleRightClickChord(chord, 2);

        Assert.Equal(2, chord.ChordNotes.Count);
        var resolved = h.Arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(chord), resolved.Frets);
        Assert.Equal(3, shared.Frets.Count);
    }

    [Fact]
    public void RightClickChordEdit_ReusesAnExistingTemplateWithTheSameShape()
    {
        // Resolve-OR-create: minting a duplicate for a shape the arrangement
        // already has is the other half of the invariant (templates are never
        // garbage collected, so duplicates accumulate for the life of the file).
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var target = h.AddTemplate(1, (0, 3), (1, 5), (2, 0));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));

        h.Service.HandleRightClickChord(chord, 2);

        Assert.Equal(target.Id, chord.TemplateId);
        Assert.Equal(2, h.Arrangement.ChordTemplates.Count);
    }

    [Fact]
    public void DroppingANoteOntoAChord_RepointsTheMergedChord()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));

        var dropped = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 2, Fret = 7 };
        h.Service.CommitHandShapeDragAndDrop(
            [], [dropped],
            [], [],
            [], [],
            [], []);

        var merged = Assert.Single(h.Level.Chords);
        Assert.Equal(3, merged.ChordNotes.Count);
        var resolved = h.Arrangement.ChordTemplates.Single(t => t.Id == merged.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(merged), resolved.Frets);
        Assert.Equal(2, shared.Frets.Count);
        Assert.NotEqual(shared.Id, merged.TemplateId);
        _ = chord;
    }

    [Fact]
    public void PastingAChordIntoAnotherArrangement_ResolvesTheTemplateInTheTarget()
    {
        // Copy a Dmin barre from Lead (template id 7) and paste it into Rhythm,
        // where id 7 is an open E5. The payload used to carry the raw id, so the
        // pasted chord rendered and exported as E5 with Dmin frets.
        var h = new Harness();
        var lead = h.Arrangement;
        h.AddTemplate(7, (0, 10), (1, 12), (2, 12));
        lead.ChordTemplates[^1].Name = "Dmin";
        var chord = h.AddChord(2, 7, (0, 10), (1, 12), (2, 12));

        var rhythm = h.AddArrangement("Rhythm");
        var strangersShape = new ChordTemplate { Id = 7, Name = "E5" };
        strangersShape.Frets[0] = 0;
        strangersShape.Frets[1] = 2;
        rhythm.ChordTemplates.Add(strangersShape);

        var leadTrack = h.Track(lead);
        h.Selection.SelectedChords.Add(chord);
        h.Clipboard.Copy(leadTrack, EditorFocusArea.NoteHighway);
        h.Selection.ClearAll();

        // Paste targets Rhythm.
        h.State.ActiveArrangement = rhythm;
        h.State.ActiveLevel = rhythm.Levels[0];
        h.Clipboard.Paste(h.Track(rhythm), 1000, 4);

        var pasted = Assert.Single(rhythm.Levels[0].Chords);
        Assert.NotEqual(7, pasted.TemplateId);
        var resolved = rhythm.ChordTemplates.Single(t => t.Id == pasted.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(pasted), resolved.Frets);
        Assert.Equal("Dmin", resolved.Name); // the source's deliberate name survives
        // The stranger's template is untouched.
        Assert.Equal(2, strangersShape.Frets.Count);
        Assert.Equal("E5", strangersShape.Name);
    }

    [Fact]
    public void PastingWithinTheSameArrangement_KeepsTheTemplateAndMintsNothing()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        var chord = h.AddChord(2, 0, (0, 3), (1, 5));

        var track = h.Track(h.Arrangement);
        h.Selection.SelectedChords.Add(chord);
        h.Clipboard.Copy(track, EditorFocusArea.NoteHighway);
        h.Selection.ClearAll();
        h.Clipboard.Paste(track, 4000, 4);

        Assert.Single(h.Arrangement.ChordTemplates);
        Assert.All(h.Level.Chords, c => Assert.Equal(0, c.TemplateId));
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
            Service = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            for (var i = 0; i < 32; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            Arrangement = AddArrangement("Lead");
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            Clipboard = new EditingClipboardCoordinator(new ClipboardService(logger), config, Service,
                Selection, new TempoService(logger), logger);
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public HighwayEditingService Service { get; }
        public EditingClipboardCoordinator Clipboard { get; }
        public Arrangement Arrangement { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        public Arrangement AddArrangement(string name)
        {
            var arrangement = new Arrangement { Name = name, ArrangementType = ArrangementType.Lead };
            arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(arrangement);
            return arrangement;
        }

        public TrackEditorContext Track(Arrangement arrangement)
        {
            var track = new TrackEditorContext(arrangement, Selection)
            {
                Project = _project, Level = arrangement.Levels[0]
            };
            return track;
        }

        public ChordTemplate AddTemplate(int id, params (int stringIndex, int fret)[] frets)
        {
            var template = new ChordTemplate { Id = id };
            foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
            Arrangement.ChordTemplates.Add(template);
            return template;
        }

        public Chord AddChord(int beat, int templateId, params (int stringIndex, int fret)[] frets)
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
            foreach (var (stringIndex, fret) in frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            Level.Chords.Add(chord);
            return chord;
        }
    }
}
