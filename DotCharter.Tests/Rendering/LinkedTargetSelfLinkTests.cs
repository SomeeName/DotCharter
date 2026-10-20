using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A link-next chain lands at the END of the linking sound's sustain, so a
///     sound with NO sustain has no landing and cannot be its own link target.
///     <para>
///         <c>FrameSnapshot.BuildLinkedTargets</c> registered
///         <c>FractionalPosition.Max(Position, EndPosition)</c> — and
///         <c>EndPosition == Position + Sustain</c>, so a zero-sustain sound
///         registered its OWN (time, string). The projection then read that back
///         and flagged the sound <see cref="NoteFlags.LinkPrevious" />, which the
///         renderers treat as "this is a landing, its head is already drawn by
///         the note before it":
///     </para>
///     <list type="bullet">
///         <item><c>NoteHeadRenderer</c> skips a LinkPrevious note outright, and
///         a zero sustain draws no tail either — so the note vanished.</item>
///         <item><c>ChordRenderer</c> ORs LinkPrevious across the chord's notes
///         and suppresses the whole box AND every head — so ONE zero-sustain
///         string in a chord that links next took the entire chord with it. That
///         is the reachable case: <c>Chord.HasLinkNext()</c> is true for the
///         whole chord as soon as any one string links, and the registration
///         then ran for every string including the unsustained ones.</item>
///     </list>
/// </summary>
public sealed class LinkedTargetSelfLinkTests
{
    private static readonly FractionalPosition QuarterBeat = new(new Fraction(1, 4));

    [Fact]
    public void A_zero_sustain_linkNext_note_is_not_its_own_link_target()
    {
        var note = new GuitarNote
        {
            Position = At(4), StringIndex = 0, Fret = 5,
            Sustain = FractionalPosition.Zero, LinkNext = true
        };

        var snapshot = Project(notes: [note]);

        Assert.Equal(1, snapshot.NoteCount);
        Assert.Equal(NoteFlags.None, snapshot.Notes[0].Flags & NoteFlags.LinkPrevious);
        Assert.Equal(NoteFlags.LinkNext, snapshot.Notes[0].Flags & NoteFlags.LinkNext);
    }

    [Fact]
    public void A_chord_that_links_next_still_draws_its_unsustained_strings()
    {
        // A 6-string guitar chord: string 0 rings out and links into the next
        // sound, strings 1 and 2 are plain stabs. Before the fix, registering
        // the link target for EVERY string (Chord.HasLinkNext is chord-wide)
        // made strings 1 and 2 their own targets at the chord's own time, and
        // ChordRenderer's chord-wide `isLinkPrevious` then dropped the box,
        // the fill and all three heads.
        var chord = new Chord { Position = At(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5, Sustain = QuarterBeat, LinkNext = true });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, Sustain = FractionalPosition.Zero });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 6, Sustain = FractionalPosition.Zero });

        var snapshot = Project(chords: [chord]);

        Assert.Equal(1, snapshot.ChordCount);
        Assert.Equal(3, snapshot.ChordNoteCount);
        for (var i = 0; i < snapshot.ChordNoteCount; i++)
            Assert.Equal(NoteFlags.None, snapshot.ChordNotes[i].Flags & NoteFlags.LinkPrevious);
    }

    [Fact]
    public void A_real_link_landing_is_still_flagged()
    {
        // The guard must not cost the feature it guards: a sustained linkNext
        // note followed by a note at the end of that sustain, same string, IS a
        // landing and must keep drawing no second head.
        var linking = new GuitarNote
        {
            Position = At(4), StringIndex = 0, Fret = 5, Sustain = QuarterBeat, LinkNext = true
        };
        var landing = new GuitarNote { Position = At(4) + QuarterBeat, StringIndex = 0, Fret = 7 };

        var snapshot = Project(notes: [linking, landing]);

        Assert.Equal(2, snapshot.NoteCount);
        Assert.Equal(NoteFlags.None, snapshot.Notes[0].Flags & NoteFlags.LinkPrevious);
        Assert.Equal(NoteFlags.LinkPrevious, snapshot.Notes[1].Flags & NoteFlags.LinkPrevious);
    }

    [Fact]
    public void A_landing_on_a_DIFFERENT_string_is_untouched()
    {
        // Sanity on the (time, string) key: the chain is per string.
        var linking = new GuitarNote
        {
            Position = At(4), StringIndex = 0, Fret = 5, Sustain = QuarterBeat, LinkNext = true
        };
        var other = new GuitarNote { Position = At(4) + QuarterBeat, StringIndex = 3, Fret = 7 };

        var snapshot = Project(notes: [linking, other]);

        Assert.Equal(NoteFlags.None, snapshot.Notes[1].Flags & NoteFlags.LinkPrevious);
    }

    // ---- fixtures -----------------------------------------------------------

    private static FractionalPosition At(int beat) => new(beat, Fraction.Zero);

    private static FrameSnapshot Project(
        IList<GuitarNote>? notes = null, IList<Chord>? chords = null)
    {
        var beats = StressChartFactory.BeatGrid(lengthMinutes: 0.2);
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = beats,
            ActiveNotes = notes ?? Array.Empty<GuitarNote>(),
            ActiveChords = chords ?? Array.Empty<Chord>(),
            ActiveAnchors = Array.Empty<FretAnchor>(),
            ActiveHandShapes = Array.Empty<HandShape>()
        };

        var snapshot = new FrameSnapshot();
        FrameSnapshot.BuildSnapshot(0, 10_000, 0, snapshot, ctx, []);
        return snapshot;
    }
}
