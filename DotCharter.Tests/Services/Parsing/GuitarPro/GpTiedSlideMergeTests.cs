using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     <see cref="GpPostProcessor.MergeTiedNotes" /> and slides. A tie says "the
///     same struck sound, held longer", so the two halves collapse into one —
///     but the pass used to refuse outright whenever EITHER half carried a
///     slide of any kind.
///     <para>
///     Provenance: owner report 2026-08-21 against a real GP8 file, measures
///     100–101. A whole-note {9,10} dyad is tied across the barline and slid
///     off; Guitar Pro copies the slide-out onto BOTH halves, so the two chords
///     were byte-identical and the blanket refusal still split them — two
///     chords, two hand shapes, and a visible break at the barline where the
///     player holds one sound.
///     </para>
/// </summary>
public sealed class GpTiedSlideMergeTests
{
    private static readonly FractionalPosition Whole = new(4);
    private static readonly FractionalPosition Quarter = new(1);

    private static GuitarNote Note(int beat, FractionalPosition sustain, bool linkNext,
        int? slideTo = null, bool unpitched = false) => new()
        {
            Position = new FractionalPosition(beat), StringIndex = 2, Fret = 9,
            Sustain = sustain, LinkNext = linkNext, SlideTo = slideTo, UnpitchedSlide = unpitched
        };

    private static Chord Dyad(int beat, FractionalPosition sustain, bool linkNext,
        int? slideTo = null, bool unpitched = false)
    {
        var chord = new Chord { Position = new FractionalPosition(beat) };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 2, Fret = 10, Sustain = sustain, LinkNext = linkNext,
            SlideTo = slideTo is { } s ? s + 1 : null, UnpitchedSlide = unpitched
        });
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 3, Fret = 9, Sustain = sustain, LinkNext = linkNext,
            SlideTo = slideTo, UnpitchedSlide = unpitched
        });
        return chord;
    }

    [Fact]
    public void TiedChords_WithIdenticalSlideOut_MergeIntoOne()
    {
        // The reported bar pair: identical whole-note dyads, both sides carrying
        // the same unpitched slide-off, tied across the barline.
        var level = new ChartLevel();
        level.Chords.Add(Dyad(4, Whole, linkNext: true, slideTo: 4, unpitched: true));
        level.Chords.Add(Dyad(8, Whole, linkNext: false, slideTo: 4, unpitched: true));

        GpPostProcessor.MergeTiedNotes(level, 6);

        var chord = Assert.Single(level.Chords);
        Assert.Equal(new FractionalPosition(4), chord.Position);
        Assert.All(chord.ChordNotes, cn => Assert.Equal(new FractionalPosition(8), cn.Sustain));
        Assert.All(chord.ChordNotes, cn => Assert.True(cn.UnpitchedSlide));
    }

    [Fact]
    public void TiedNotes_WithIdenticalSlideOut_MergeIntoOne()
    {
        var level = new ChartLevel();
        level.Notes.Add(Note(4, Whole, linkNext: true, slideTo: 4, unpitched: true));
        level.Notes.Add(Note(8, Whole, linkNext: false, slideTo: 4, unpitched: true));

        GpPostProcessor.MergeTiedNotes(level, 6);

        var note = Assert.Single(level.Notes);
        Assert.Equal(new FractionalPosition(8), note.Sustain);
        Assert.True(note.UnpitchedSlide);
        Assert.Equal(4, note.SlideTo);
    }

    [Fact]
    public void TiedNote_WhoseTailSlidesOff_MergesAndAdoptsTheSlide()
    {
        // The slide-off belongs to the END of the held sound: GP writes it on the
        // tie's second half only. Merging must carry it onto the merged sound
        // rather than leaving a second strike behind to hold it.
        var level = new ChartLevel();
        level.Notes.Add(Note(4, Quarter, linkNext: true));
        level.Notes.Add(Note(5, Quarter, linkNext: false, slideTo: 4, unpitched: true));

        GpPostProcessor.MergeTiedNotes(level, 6);

        var note = Assert.Single(level.Notes);
        Assert.Equal(new FractionalPosition(2), note.Sustain);
        Assert.True(note.UnpitchedSlide);
        Assert.Equal(4, note.SlideTo);
    }

    [Fact]
    public void TiedNote_WithPitchedSlideOnTheFirstHalf_StillDoesNotMerge()
    {
        // A pitched slide travels to a landing that the second half IS. Folding
        // the two would delete the landing and leave a slide to nowhere, so this
        // stays refused — the guard the relaxed rule must not cost us.
        var level = new ChartLevel();
        level.Notes.Add(Note(4, Quarter, linkNext: true, slideTo: 12));
        level.Notes.Add(Note(5, Quarter, linkNext: false));

        GpPostProcessor.MergeTiedNotes(level, 6);

        Assert.Equal(2, level.Notes.Count);
    }

    [Fact]
    public void TiedNotes_WithDifferentSlideTargets_DoNotMerge()
    {
        var level = new ChartLevel();
        level.Notes.Add(Note(4, Quarter, linkNext: true, slideTo: 4, unpitched: true));
        level.Notes.Add(Note(5, Quarter, linkNext: false, slideTo: 7, unpitched: true));

        GpPostProcessor.MergeTiedNotes(level, 6);

        Assert.Equal(2, level.Notes.Count);
    }

    [Fact]
    public void PlainTiedNotes_StillMerge()
    {
        // The pre-existing case, pinned so the relaxed rule cannot lose it.
        var level = new ChartLevel();
        level.Notes.Add(Note(4, Quarter, linkNext: true));
        level.Notes.Add(Note(5, Quarter, linkNext: false));

        GpPostProcessor.MergeTiedNotes(level, 6);

        var note = Assert.Single(level.Notes);
        Assert.Equal(new FractionalPosition(2), note.Sustain);
        Assert.Null(note.SlideTo);
    }
}
