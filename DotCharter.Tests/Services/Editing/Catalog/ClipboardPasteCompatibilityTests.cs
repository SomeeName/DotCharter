using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing.Catalog;

/// <summary>
///     #327: plain Paste and Paste Special wrote the clipboard into the active track
///     with no fit check, while the phrase catalog's paste of the same payload was
///     checked by <see cref="PhrasePasteCompatibility" />. Guitar content landed on
///     strings 5–6 of a 4-string bass; open strings copied at capo 0 silently became
///     "the capo" at capo 3. Both clipboard paths now run the same check.
/// </summary>
public sealed class ClipboardPasteCompatibilityTests
{
    [Fact]
    public void SixStringContentPastedIntoAFourStringBass_IsRefusedAndCommitsNothing()
    {
        var h = new Harness();
        var guitar = h.AddArrangement("Lead", strings: 6, capo: 0);
        var bass = h.AddArrangement("Bass", strings: 4, capo: 0);
        h.CopyNotes(guitar, Note(2, 0, 5), Note(3, 5, 7));

        var before = h.History.CurrentNodeId;
        var outcome = h.Clipboard.Paste(h.Track(bass), 8000, 4);

        Assert.NotNull(outcome);
        Assert.Equal(PhrasePasteResult.Blocked, outcome!.Result);
        Assert.Contains(outcome.Check!.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyStringOutOfRange);
        Assert.Empty(bass.Levels[0].Notes);
        Assert.Equal(before, h.History.CurrentNodeId);
    }

    [Fact]
    public void PasteSpecial_RunsTheSameCheck()
    {
        var h = new Harness();
        var guitar = h.AddArrangement("Lead", strings: 6, capo: 0);
        var bass = h.AddArrangement("Bass", strings: 4, capo: 0);
        h.CopyNotes(guitar, Note(2, 5, 5));

        var outcome = h.Clipboard.PasteSpecial(h.Track(bass), 8000, 4, PasteSpecialMode.Preserve);

        Assert.Equal(PhrasePasteResult.Blocked, outcome!.Result);
        Assert.Empty(bass.Levels[0].Notes);
    }

    [Fact]
    public void AnOpenStringCopiedAtCapoZero_PastesAtCapoThreeWithAWarning()
    {
        var h = new Harness();
        var source = h.AddArrangement("Lead", strings: 6, capo: 0);
        var capoed = h.AddArrangement("Rhythm", strings: 6, capo: 3);
        h.CopyNotes(source, Note(2, 1, 0), Note(3, 2, 5));

        var outcome = h.Clipboard.Paste(h.Track(capoed), 8000, 4);

        Assert.Equal(PhrasePasteResult.PastedWithWarning, outcome!.Result);
        Assert.Contains(outcome.Check!.Issues, i => i.LocalizationKey == PhrasePasteCompatibility.KeyCapoDiffers);
        Assert.Equal(2, capoed.Levels[0].Notes.Count);
    }

    [Fact]
    public void ContentThatFits_PastesCleanly()
    {
        var h = new Harness();
        var a = h.AddArrangement("Lead", strings: 6, capo: 0);
        var b = h.AddArrangement("Rhythm", strings: 6, capo: 0);
        h.CopyNotes(a, Note(2, 1, 3), Note(3, 2, 5));

        var outcome = h.Clipboard.Paste(h.Track(b), 8000, 4);

        Assert.Equal(PhrasePasteResult.Pasted, outcome!.Result);
        Assert.Equal(2, b.Levels[0].Notes.Count);
    }

    private static GuitarNote Note(int beat, int stringIndex, int fret) =>
        new() { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };

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
                _project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
            State.ActiveProject = _project;

            Clipboard = new EditingClipboardCoordinator(new ClipboardService(logger), config, service,
                Selection, new TempoService(logger), logger);
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public HistoryService History { get; }
        public EditingClipboardCoordinator Clipboard { get; }

        public Arrangement AddArrangement(string name, int strings, int capo)
        {
            var arrangement = new Arrangement { Name = name, StringCount = strings, Capo = capo };
            for (var s = 0; s < strings; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
            arrangement.Levels.Add(new ChartLevel());
            _project.Arrangements.Add(arrangement);
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = arrangement.Levels[0];
            return arrangement;
        }

        public TrackEditorContext Track(Arrangement arrangement)
        {
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = arrangement.Levels[0];
            return new TrackEditorContext(arrangement, Selection) { Project = _project, Level = arrangement.Levels[0] };
        }

        public void CopyNotes(Arrangement source, params GuitarNote[] notes)
        {
            foreach (var note in notes)
            {
                source.Levels[0].Notes.Add(note);
                Selection.SelectedNotes.Add(note);
            }

            Clipboard.Copy(Track(source), EditorFocusArea.NoteHighway);
            Selection.ClearAll();
        }
    }
}
