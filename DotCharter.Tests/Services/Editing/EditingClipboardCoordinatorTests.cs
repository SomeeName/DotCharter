using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.Services.Editing;

public class EditingClipboardCoordinatorTests
{
    private sealed class CapturingClipboard : IClipboardService
    {
        public List<FretAnchor> Anchors { get; } = [];
        public List<HandShape> HandShapes { get; } = [];
        public List<ChordTemplate> Templates { get; } = [];
        public bool HasData => false;

        public void Copy(IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords,
            IEnumerable<HandShape> handShapes, IEnumerable<EventPoint> eventPoints,
            IEnumerable<ToneChange> toneChanges, IEnumerable<FretAnchor> anchors,
            IEnumerable<ChordTemplate>? chordTemplates = null, PhraseSourceContext? source = null)
        {
            Anchors.Clear();
            Anchors.AddRange(anchors);
            HandShapes.Clear();
            HandShapes.AddRange(handShapes);
            Templates.Clear();
            if (chordTemplates != null) Templates.AddRange(chordTemplates);
        }

        public ClipboardPayload? GetPastedData(FractionalPosition targetPos) => null;
    }

    private static (EditingClipboardCoordinator coordinator, TrackEditorContext track,
        SelectionService selection, CapturingClipboard clipboard) MakeCopyFixture()
    {
        var arr = new Arrangement { Name = "Lead" };
        arr.Levels.Add(new ChartLevel());
        var selection = new SelectionService();
        var track = new TrackEditorContext(arr, selection);
        var clipboard = new CapturingClipboard();
        var coordinator = new EditingClipboardCoordinator(
            clipboard, null!, null!, selection, null!, new FakeAppLogger());
        return (coordinator, track, selection, clipboard);
    }

    [Fact]
    public void Capture_IncludesGoverningAnchorClampedToSelectionStart()
    {
        var (coordinator, track, selection, _) = MakeCopyFixture();

        // The FHP at position 4 governs the notes at 10..12; a position-in-
        // range filter would drop it because it starts before the selection.
        track.Anchors.Add(new FretAnchor { Position = new FractionalPosition(4), Fret = 5, Width = 4 });
        var note1 = new GuitarNote { Position = new FractionalPosition(10) };
        var note2 = new GuitarNote { Position = new FractionalPosition(12) };
        track.Arrangement.Levels[0].Notes.Add(note1);
        track.Arrangement.Levels[0].Notes.Add(note2);
        selection.SelectedNotes.Add(note1);
        selection.SelectedNotes.Add(note2);

        var content = coordinator.CollectHighwayContent(track, EditorFocusArea.NoteHighway);

        var anchor = Assert.Single(content.Anchors);
        Assert.Equal(5, anchor.Fret);
        // Clamped to the selection start so the paste stays anchored to the
        // first pasted note instead of shifting back to the anchor's origin.
        Assert.Equal(new FractionalPosition(10), anchor.Position);
    }

    [Fact]
    public void Capture_KeepsAnchorsInsideTheSelectionSpan()
    {
        var (coordinator, track, selection, _) = MakeCopyFixture();

        track.Anchors.Add(new FretAnchor { Position = new FractionalPosition(2), Fret = 3 });
        track.Anchors.Add(new FretAnchor { Position = new FractionalPosition(11), Fret = 7 });
        var note1 = new GuitarNote { Position = new FractionalPosition(10) };
        var note2 = new GuitarNote { Position = new FractionalPosition(14) };
        track.Arrangement.Levels[0].Notes.Add(note1);
        track.Arrangement.Levels[0].Notes.Add(note2);
        selection.SelectedNotes.Add(note1);
        selection.SelectedNotes.Add(note2);

        var content = coordinator.CollectHighwayContent(track, EditorFocusArea.NoteHighway);

        Assert.Equal(2, content.Anchors.Count);
        Assert.Contains(content.Anchors,
            a => a.Fret == 3 && a.Position == new FractionalPosition(10)); // governing, clamped
        Assert.Contains(content.Anchors,
            a => a.Fret == 7 && a.Position == new FractionalPosition(11)); // in-span, untouched
    }

    /// <summary>
    ///     Capturing a chord as a phrase takes its hand shape (a plain copy does not, #468).
    ///     <para>
    ///         A strum has ZERO sustain by convention — the bar carries the
    ///         duration — so one selected chord gives <c>minPos == maxPos</c> and
    ///         the span filter's <c>hs.Position &lt; maxPos</c> rejected a bar
    ///         starting exactly on it. That is the commonest copy in the app, and
    ///         the governing ANCHOR came along the whole time under its own rule,
    ///         which made the gap read as a mystery rather than a filter.
    ///     </para>
    /// </summary>
    [Fact]
    public void Capture_TakesTheHandShapeOverAZeroSustainChord()
    {
        var (coordinator, track, selection, _) = MakeCopyFixture();

        var chord = new Chord { Position = new FractionalPosition(80), TemplateId = 3 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 14 });
        track.Arrangement.Levels[0].Chords.Add(chord);

        var bar = new HandShape
        {
            Position = new FractionalPosition(80),
            EndPosition = new FractionalPosition(81),
            TemplateId = 3
        };
        track.Arrangement.Levels[0].HandShapes.Add(bar);

        selection.SelectedChords.Add(chord);
        var content = coordinator.CollectHighwayContent(track, EditorFocusArea.NoteHighway);

        Assert.Same(bar, Assert.Single(content.HandShapes));
    }

    /// <summary>
    ///     The end of a bar is EXCLUSIVE, so the NEXT bar — a different hand
    ///     position that merely abuts this one — must not be dragged in.
    /// </summary>
    [Fact]
    public void Capture_DoesNotTakeTheAbuttingNextHandShape()
    {
        var (coordinator, track, selection, _) = MakeCopyFixture();

        var chord = new Chord { Position = new FractionalPosition(80), TemplateId = 3 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 14 });
        track.Arrangement.Levels[0].Chords.Add(chord);

        var mine = new HandShape
        {
            Position = new FractionalPosition(80), EndPosition = new FractionalPosition(81), TemplateId = 3
        };
        var next = new HandShape
        {
            Position = new FractionalPosition(81), EndPosition = new FractionalPosition(82), TemplateId = 9
        };
        track.Arrangement.Levels[0].HandShapes.Add(mine);
        track.Arrangement.Levels[0].HandShapes.Add(next);

        selection.SelectedChords.Add(chord);
        var content = coordinator.CollectHighwayContent(track, EditorFocusArea.NoteHighway);

        Assert.Same(mine, Assert.Single(content.HandShapes));
    }

    /// <summary>The pre-existing span rule still stands: a bar overlapping a
    /// multi-sound selection comes along even if it covers no onset.</summary>
    [Fact]
    public void Capture_StillTakesABarOverlappingTheSelectedSpan()
    {
        var (coordinator, track, selection, _) = MakeCopyFixture();

        var first = new GuitarNote { Position = new FractionalPosition(10) };
        var last = new GuitarNote { Position = new FractionalPosition(20) };
        track.Arrangement.Levels[0].Notes.Add(first);
        track.Arrangement.Levels[0].Notes.Add(last);

        // Starts and ends strictly between the two onsets.
        var bar = new HandShape
        {
            Position = new FractionalPosition(13), EndPosition = new FractionalPosition(17), TemplateId = 3
        };
        track.Arrangement.Levels[0].HandShapes.Add(bar);

        selection.SelectedNotes.Add(first);
        selection.SelectedNotes.Add(last);
        var content = coordinator.CollectHighwayContent(track, EditorFocusArea.NoteHighway);

        Assert.Same(bar, Assert.Single(content.HandShapes));
    }

    [Fact]
    public void ComputePayloadRange_EmptyPayload_ReturnsZeroSpan()
    {
        var payload = new ClipboardPayload();

        var (start, end) = EditingClipboardCoordinator.ComputePayloadRange(payload);

        Assert.Equal(FractionalPosition.Zero, start);
        Assert.Equal(FractionalPosition.Zero, end);
    }

    [Fact]
    public void ComputePayloadRange_SpansAcrossAllItemKinds()
    {
        var payload = new ClipboardPayload
        {
            Notes =
            {
                new GuitarNote { Position = new FractionalPosition(10), Sustain = new FractionalPosition(5) }
            },
            Chords =
            {
                new Chord { Position = new FractionalPosition(2) }
            },
            HandShapes =
            {
                new HandShape { Position = new FractionalPosition(20), EndPosition = new FractionalPosition(30) }
            },
            Anchors = { new FretAnchor { Position = new FractionalPosition(1) } },
            EventPoints = { new EventPoint { Position = new FractionalPosition(40) } },
            ToneChanges = { new ToneChange { Position = new FractionalPosition(35), ToneName = "lead" } }
        };

        var (start, end) = EditingClipboardCoordinator.ComputePayloadRange(payload);

        Assert.Equal(new FractionalPosition(1), start);
        Assert.Equal(new FractionalPosition(40), end);
    }

    [Fact]
    public void ComputePayloadRange_SingleAnchor_StartEqualsEnd()
    {
        var payload = new ClipboardPayload
        {
            Anchors = { new FretAnchor { Position = new FractionalPosition(7) } }
        };

        var (start, end) = EditingClipboardCoordinator.ComputePayloadRange(payload);

        Assert.Equal(new FractionalPosition(7), start);
        Assert.Equal(new FractionalPosition(7), end);
    }

    [Fact]
    public void ComputePayloadRange_PicksMaxEndAcrossNotes()
    {
        var payload = new ClipboardPayload
        {
            Notes =
            {
                new GuitarNote { Position = new FractionalPosition(5), Sustain = new FractionalPosition(2) },
                new GuitarNote { Position = new FractionalPosition(10), Sustain = new FractionalPosition(20) },
                new GuitarNote { Position = new FractionalPosition(15), Sustain = new FractionalPosition(1) }
            }
        };

        var (start, end) = EditingClipboardCoordinator.ComputePayloadRange(payload);

        Assert.Equal(new FractionalPosition(5), start);
        Assert.Equal(new FractionalPosition(30), end);
    }

    [Fact]
    public void ComputePayloadRange_ToneOnlyPayload_UsesTonePosition()
    {
        var payload = new ClipboardPayload
        {
            ToneChanges = { new ToneChange { Position = new FractionalPosition(12), ToneName = "clean" } }
        };

        var (start, end) = EditingClipboardCoordinator.ComputePayloadRange(payload);

        Assert.Equal(new FractionalPosition(12), start);
        Assert.Equal(new FractionalPosition(12), end);
    }
}
