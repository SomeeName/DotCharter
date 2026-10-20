using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
/// #326: <c>TimelineMath.GetVisibleRange</c> hands back ONE element before the
/// window, so a held note followed by two later sounds dropped out of the
/// projected slice while it still rang: a pedal bass under a melody vanished at
/// the 3D strike line (1 s look-behind, buffer 0) and lost its 2D tail past the
/// 5 s buffer. <c>ExtendStartToSounding</c> steps the start back to it.
/// </summary>
public sealed class HeldNoteLookBackTests
{
    /// <summary>120 BPM: one beat every 500 ms.</summary>
    private static List<BeatMarker> Beats(int count = 200) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static GuitarNote Note(int beat, int sustainBeats, int stringIndex) => new()
    {
        Position = new FractionalPosition(beat),
        Sustain = new FractionalPosition(sustainBeats),
        StringIndex = stringIndex,
        Fret = 5
    };

    [Fact]
    public void The_3D_window_keeps_a_held_note_that_two_later_notes_follow()
    {
        // The issue's case: 0 ms held 4 s, then 1,000 and 2,000 ms on other
        // strings; now = 3,000 ms, so the 3D window starts at 2,000 ms.
        var beats = Beats();
        List<GuitarNote> notes = [Note(0, 8, 0), Note(2, 0, 1), Note(4, 0, 2)];
        TimelineMath.GetVisibleRange(notes, 2_000, 10_000, beats, 0, out var start, out _);
        Assert.Equal(2, start); // the bug: the held note is outside the slice

        Assert.Equal(0, TimelineMath.ExtendStartToSounding(notes, start, 2_000, beats, static n => n.EndPosition));
    }

    [Fact]
    public void A_note_that_has_stopped_ringing_does_not_move_the_start()
    {
        var beats = Beats();
        List<GuitarNote> notes = [Note(0, 2, 0), Note(2, 0, 1), Note(4, 0, 2)];
        TimelineMath.GetVisibleRange(notes, 2_000, 10_000, beats, 0, out var start, out _);
        Assert.Equal(start, TimelineMath.ExtendStartToSounding(notes, start, 2_000, beats, static n => n.EndPosition));
    }

    [Fact]
    public void The_scan_stops_at_the_look_back_bound()
    {
        // Held 50 s from 0 ms; the window starts at 40 s, more than
        // MaxSoundingLookBackMs after the note began, so it is not searched for.
        var beats = Beats();
        List<GuitarNote> notes = [Note(0, 100, 0), Note(70, 0, 1), Note(76, 0, 2), Note(78, 0, 3)];
        TimelineMath.GetVisibleRange(notes, 40_000, 45_000, beats, 0, out var start, out _);
        Assert.Equal(start, TimelineMath.ExtendStartToSounding(notes, start, 40_000, beats, static n => n.EndPosition));
    }

    [Fact]
    public void The_2D_snapshot_keeps_a_sustain_longer_than_its_buffer()
    {
        // Held 7 s from 0 ms with two notes after it; the view starts at 6 s, so
        // the 5 s buffer reaches back to 1 s and the one-before rule stops there.
        var beats = Beats();
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = [Note(0, 14, 0), Note(1, 0, 1), Note(2, 0, 2)],
            ActiveChords = [],
            ActiveAnchors = [],
            ActiveHandShapes = [],
            EventPoints = [],
            Tones = [],
            LyricPhrases = [],
            Showlights = []
        };

        var snapshot = new FrameSnapshot { StringCount = 6 };
        FrameSnapshot.BuildSnapshot(6_000, 8_000, 6_000, snapshot, ctx, []);

        var times = snapshot.Notes.Take(snapshot.NoteCount).Select(n => n.Time).ToList();
        Assert.Contains(0.0, times);
    }
}
