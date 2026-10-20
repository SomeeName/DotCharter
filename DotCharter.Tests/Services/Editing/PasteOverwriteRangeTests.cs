using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     What paste-special's Overwrite modes are allowed to delete.
///     <para>
///         <c>ComputePayloadRange</c> returns the span the payload OCCUPIES —
///         its end is the maximum <c>EndPosition</c>, i.e. one sustain past the
///         last onset. A hand shape is a span and is correctly cleared by
///         overlapping it. A note, chord, anchor, marker or tone change is a
///         POINT: the ones a paste replaces are the ones sitting at an onset the
///         paste writes to.
///     </para>
///     <para>
///         Testing the points against the span's end (<c>&lt;= rangeEnd</c>)
///         over-reached by exactly the last sustain, and the hand-shape test on
///         the very next line was already correctly exclusive — that asymmetry is
///         what says which half was intended. Symptom: copy a run whose last note
///         holds to beat 8, paste-special (Overwrite) it, and the neighbour that
///         STARTS at beat 8 — immediately after the pasted content, with nothing
///         pasted over it — is deleted too.
///     </para>
/// </summary>
public sealed class PasteOverwriteRangeTests
{
    [Fact]
    public void TheLastOnsetIsNotTheLastEnd()
    {
        var payload = new ClipboardPayload();
        payload.Notes.Add(Note(4));
        payload.Notes.Add(Note(6, sustain: 2)); // holds to 8

        var (_, end) = EditingClipboardCoordinator.ComputePayloadRange(payload);

        Assert.Equal(new FractionalPosition(8), end);
        Assert.Equal(new FractionalPosition(6), EditingClipboardCoordinator.ComputePayloadLastOnset(payload));
    }

    [Fact]
    public void OverwriteDoesNotDeleteTheNoteSittingAtTheEndOfTheLastSustain()
    {
        var track = NewTrack();
        var neighbour = Note(8);
        var inside = Note(6);
        track.Arrangement.Levels[0].Notes.Add(inside);
        track.Arrangement.Levels[0].Notes.Add(neighbour);

        var payload = new ClipboardPayload();
        payload.Notes.Add(Note(4));
        payload.Notes.Add(Note(6, sustain: 2)); // the payload's last ONSET is 6

        var targets = Collect(track, payload);

        Assert.Contains(inside, targets.notes);
        Assert.DoesNotContain(neighbour, targets.notes);
    }

    /// <summary>The half that must NOT change: with no sustain in the payload the
    /// span's end IS the last onset, and the sound there is still overwritten.</summary>
    [Fact]
    public void OverwriteStillDeletesTheSoundAtTheLastOnset()
    {
        var track = NewTrack();
        var atLastOnset = Note(6);
        track.Arrangement.Levels[0].Notes.Add(atLastOnset);

        var payload = new ClipboardPayload();
        payload.Notes.Add(Note(4));
        payload.Notes.Add(Note(6));

        Assert.Contains(atLastOnset, Collect(track, payload).notes);
    }

    [Fact]
    public void AnAnchorAtTheEndOfTheLastSustain_Survives()
    {
        var track = NewTrack();
        var neighbour = new FretAnchor { Position = new FractionalPosition(8), Fret = 5, Width = 4 };
        track.Anchors.Add(neighbour);

        var payload = new ClipboardPayload();
        payload.Notes.Add(Note(6, sustain: 2));
        payload.Anchors.Add(new FretAnchor { Position = new FractionalPosition(6), Fret = 3, Width = 4 });

        Assert.DoesNotContain(neighbour, Collect(track, payload).anchors);
    }

    /// <summary>A hand shape is a SPAN, so it is still cleared by overlapping the
    /// payload's occupied range — the one test that legitimately reads rangeEnd.</summary>
    [Fact]
    public void AHandShapeOverlappingTheSustainIsStillCleared()
    {
        var track = NewTrack();
        var bar = new HandShape
        {
            Position = new FractionalPosition(7),
            EndPosition = new FractionalPosition(12)
        };
        track.HandShapes.Add(bar);

        var payload = new ClipboardPayload();
        payload.Notes.Add(Note(6, sustain: 2)); // occupies 6..8, overlapping the bar
        payload.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(6),
            EndPosition = new FractionalPosition(8)
        });

        Assert.Contains(bar, Collect(track, payload).hs);
    }

    private static (List<GuitarNote> notes, List<Chord> chords, List<HandShape> hs, List<FretAnchor> anchors,
        List<EventPoint> events, List<ToneChange> tones) Collect(TrackEditorContext track, ClipboardPayload payload)
    {
        var (start, end) = EditingClipboardCoordinator.ComputePayloadRange(payload);
        return EditingClipboardCoordinator.CollectOverwriteTargets(
            track, payload, PasteSpecialMode.OverwriteAll, start, end);
    }

    private static TrackEditorContext NewTrack()
    {
        var arr = new Arrangement { Name = "Lead" };
        arr.Levels.Add(new ChartLevel());
        return new TrackEditorContext(arr, new SelectionService());
    }

    private static GuitarNote Note(int beat, int sustain = 0) => new()
    {
        Position = new FractionalPosition(beat),
        Sustain = new FractionalPosition(sustain)
    };
}
