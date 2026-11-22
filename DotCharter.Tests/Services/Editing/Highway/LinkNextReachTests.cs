using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Services.Editing.Collisions;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     Turning Link next ON must make the link real: the linking sound rings to the
///     next sound on its string. A flag whose sustain stops short is dropped by
///     <c>LinkNextRepair</c> on the next save, and the frame snapshot (which matches
///     the landing by time) draws the follower as a fresh note head (owner report).
/// </summary>
public sealed class LinkNextReachTests
{
    private static readonly FractionalPosition HalfBeat = new(new Fraction(1, 2));

    [Fact]
    public void TogglingLinkNextOn_RingsIntoTheNextNoteOnTheString_AndTheFollowerIsALanding()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var a = h.AddNote(2, 2, 15);
        a.Sustain = HalfBeat;
        var other = h.AddNote(3, 4, 12);
        var b = h.AddNote(4, 2, 15);
        h.Selection.SelectedNotes.Add(a);

        h.Service.ToggleSelectedLinkNext();

        Assert.True(a.LinkNext);
        Assert.Equal(b.Position, a.Position + a.Sustain);
        Assert.False(other.LinkNext);

        ChartSpacingEnforcer.EnforceArrangement(h.Arrangement, h.State.ActiveProject!.Beats, new AppConfig());
        Assert.True(a.LinkNext);

        var snapshot = Project(h.State.ActiveProject.Beats, [.. h.State.PlacedNotes.OrderBy(n => n.Position)]);
        var follower = Enumerable.Range(0, snapshot.NoteCount)
            .Single(i => snapshot.Notes[i].StringIndex == 2 && snapshot.Notes[i].Time > 1500);
        Assert.Equal(NoteFlags.LinkPrevious, snapshot.Notes[follower].Flags & NoteFlags.LinkPrevious);
    }

    [Fact]
    public void TogglingLinkNextOn_AndUndo_RestoresTheAuthoredSustain()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var a = h.AddNote(2, 2, 15);
        a.Sustain = HalfBeat;
        h.AddNote(4, 2, 15);
        h.Selection.SelectedNotes.Add(a);

        h.Service.ToggleSelectedLinkNext();
        h.History.Undo();

        Assert.False(a.LinkNext);
        Assert.Equal(HalfBeat, a.Sustain);
    }

    private static FrameSnapshot Project(IList<BeatMarker> beats, IList<GuitarNote> notes)
    {
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = notes,
            ActiveChords = Array.Empty<Chord>(),
            ActiveAnchors = Array.Empty<FretAnchor>(),
            ActiveHandShapes = Array.Empty<HandShape>()
        };

        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 10_000, 0, snapshot, ctx, []);
        return snapshot;
    }
}
