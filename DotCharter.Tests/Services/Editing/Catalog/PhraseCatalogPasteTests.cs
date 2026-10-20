using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Catalog;

/// <summary>
///     Pasting a catalog phrase goes through the SAME history-backed commit as
///     every other paste — <c>IHighwayEditCommand.Execute(IHistoryService)</c> —
///     so it is one undo step and never an edit path that bypasses undo.
/// </summary>
public sealed class PhraseCatalogPasteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-paste-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        BestEffortDelete.Directory(_dir);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void PastingAPhraseIsExactlyOneUndoableStep()
    {
        var h = new Harness(_dir);
        var entry = h.Capture("Riff",
            Note(10, 5, 3), Note(11, 4, 5), Note(12, 4, 7));

        var before = h.History.CurrentNodeId;
        var outcome = h.Clipboard.PastePhrase(h.Track(h.Arrangement), entry, 8000, 4);

        Assert.Equal(PhrasePasteResult.Pasted, outcome.Result);
        Assert.Equal(3, h.Level.Notes.Count);
        Assert.NotEqual(before, h.History.CurrentNodeId);
        Assert.True(h.History.CanUndo);

        h.History.Undo();

        Assert.Empty(h.Level.Notes);
        Assert.Equal(before, h.History.CurrentNodeId);
    }

    [Fact]
    public void APastedPhraseLandsAtTheGridSnappedPlayheadAndKeepsItsRhythm()
    {
        // Captured at beats 10 / 11.5; pasted at 8000 ms == beat 16 on the
        // harness's 120 BPM grid. The internal spacing is what has to survive.
        var h = new Harness(_dir);
        var entry = h.Capture("Riff",
            new GuitarNote { Position = new FractionalPosition(10), StringIndex = 5, Fret = 3 },
            new GuitarNote
            {
                Position = new FractionalPosition(11, new Fraction(1, 2)), StringIndex = 4, Fret = 5
            });

        h.Clipboard.PastePhrase(h.Track(h.Arrangement), entry, 8000, 4);

        var placed = h.Level.Notes.OrderBy(n => n.Position).ToList();
        Assert.Equal(new FractionalPosition(16), placed[0].Position);
        Assert.Equal(new FractionalPosition(17, new Fraction(1, 2)), placed[1].Position);
    }

    [Fact]
    public void APastedChordResolvesItsShapeInTheDestinationInsteadOfCarryingAnId()
    {
        // Same failure the clipboard's incomingTemplates fixed: template id 7 in
        // the source is somebody else's shape in the destination.
        var h = new Harness(_dir);
        var source = h.AddArrangement("Lead");
        var dmin = new ChordTemplate { Id = 7, Name = "Dmin" };
        dmin.Frets[0] = 10;
        dmin.Frets[1] = 12;
        source.ChordTemplates.Add(dmin);
        var chord = new Chord { Position = new FractionalPosition(4), TemplateId = 7 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 10 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 12 });
        source.Levels[0].Chords.Add(chord);

        h.Selection.SelectedChords.Add(chord);
        var entry = h.Clipboard.CaptureToCatalog(h.Catalog, h.Track(source), EditorFocusArea.NoteHighway, "Dmin riff");
        h.Selection.ClearAll();
        Assert.NotNull(entry);

        var stranger = new ChordTemplate { Id = 7, Name = "E5" };
        stranger.Frets[0] = 0;
        stranger.Frets[1] = 2;
        h.Arrangement.ChordTemplates.Add(stranger);

        h.Clipboard.PastePhrase(h.Track(h.Arrangement), entry!, 4000, 4);

        var pasted = Assert.Single(h.Level.Chords);
        Assert.NotEqual(7, pasted.TemplateId);
        var resolved = h.Arrangement.ChordTemplates.Single(t => t.Id == pasted.TemplateId);
        Assert.Equal(ChordTemplateResolver.FretsOf(pasted), resolved.Frets);
        Assert.Equal("Dmin", resolved.Name);
        // The stranger's shape is untouched.
        Assert.Equal(2, stranger.Frets.Count);
        Assert.Equal("E5", stranger.Name);
    }

    [Fact]
    public void ABlockedPhraseChangesNothingAndCreatesNoHistoryEntry()
    {
        var h = new Harness(_dir);
        var entry = h.Capture("Low riff", Note(10, 5, 2), Note(11, 5, 4));

        // Capo 3 makes fret 2 malformed — ChartExportValidator.KeyNoteBelowCapo.
        h.Arrangement.Capo = 3;
        var before = h.History.CurrentNodeId;

        var outcome = h.Clipboard.PastePhrase(h.Track(h.Arrangement), entry, 4000, 4);

        Assert.Equal(PhrasePasteResult.Blocked, outcome.Result);
        Assert.Empty(h.Level.Notes);
        Assert.Equal(before, h.History.CurrentNodeId);
        Assert.False(h.History.CanUndo);
        Assert.Contains(outcome.Check!.Issues,
            i => i.LocalizationKey == PhrasePasteCompatibility.KeyFretBelowCapo);
    }

    [Fact]
    public void APhraseThatWillNotSoundTheSamePastesAndSaysSo()
    {
        var h = new Harness(_dir);
        var entry = h.Capture("Open riff", Note(10, 5, 0), Note(11, 5, 5));

        h.Arrangement.Capo = 2;

        var outcome = h.Clipboard.PastePhrase(h.Track(h.Arrangement), entry, 4000, 4);

        Assert.Equal(PhrasePasteResult.PastedWithWarning, outcome.Result);
        Assert.Equal(2, h.Level.Notes.Count);
        Assert.Contains(outcome.Check!.Issues,
            i => i.LocalizationKey == PhrasePasteCompatibility.KeyCapoDiffers);
    }

    [Fact]
    public void APhraseSurvivesTheWholeRoundTripThroughTheStore()
    {
        // Capture with one service, paste with a second that only ever saw the
        // files — the end-to-end proof that nothing important lives in memory.
        var first = new Harness(_dir);
        first.Capture("Chorus", Note(10, 5, 3), Note(11, 4, 5));

        var second = new Harness(_dir);
        var reloaded = second.Catalog.FindByName("chorus");
        Assert.NotNull(reloaded);

        second.Clipboard.PastePhrase(second.Track(second.Arrangement), reloaded!, 4000, 4);

        Assert.Equal(2, second.Level.Notes.Count);
        Assert.Equal([3, 5], second.Level.Notes.OrderBy(n => n.Position).Select(n => n.Fret));
    }

    private static GuitarNote Note(int beat, int stringIndex, int fret) =>
        new() { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };

    private sealed class Harness
    {
        private readonly ChartProject _project = new();

        public Harness(string catalogDir)
        {
            var logger = new FakeAppLogger();
            History = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var scope = new EditingScope(State);
            var service = new HighwayEditingService(State, Selection, History, new StrictCollisionService(),
                config, scope, new EditTargetService(Selection, scope, config), logger);

            for (var i = 0; i < 64; i++)
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });

            Arrangement = AddArrangement("Rhythm");
            State.ActiveProject = _project;
            State.ActiveArrangement = Arrangement;
            State.ActiveLevel = Arrangement.Levels[0];

            Catalog = new PhraseCatalogService(catalogDir, new RscpChartSerializer(), logger);
            Clipboard = new EditingClipboardCoordinator(new ClipboardService(logger), config, service,
                Selection, new TempoService(logger), logger);
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public EditingClipboardCoordinator Clipboard { get; }
        public PhraseCatalogService Catalog { get; }
        public Arrangement Arrangement { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        public Arrangement AddArrangement(string name)
        {
            var arrangement = new Arrangement { Name = name, ArrangementType = ArrangementType.Rhythm };
            foreach (var step in TuningUtilities.GetOffsetsForTuningType("E_STANDARD", 6))
                arrangement.TuningPitches.Add(new TuningOffset(step));
            arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(arrangement);
            return arrangement;
        }

        public TrackEditorContext Track(Arrangement arrangement)
        {
            return new TrackEditorContext(arrangement, Selection)
            {
                Project = _project, Level = arrangement.Levels[0]
            };
        }

        /// <summary>Selects the given notes in a scratch arrangement and stores
        /// them, so the fixture never depends on the destination's own content.</summary>
        public PhraseCatalogEntry Capture(string name, params GuitarNote[] notes)
        {
            var scratch = AddArrangement("Source");
            foreach (var note in notes)
            {
                scratch.Levels[0].Notes.Add(note);
                Selection.SelectedNotes.Add(note);
            }

            var entry = Clipboard.CaptureToCatalog(Catalog, Track(scratch), EditorFocusArea.NoteHighway, name);
            Selection.ClearAll();
            Assert.NotNull(entry);
            return entry!;
        }
    }
}
