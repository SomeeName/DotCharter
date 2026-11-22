using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Highway;

/// <summary>
///     The inspector's Selection "Move" by frets: a sound moves WHOLE or stays, and its slide
///     targets move with it.
///     <list type="bullet">
///         <item>A note moves its fret and its slide end by the same number of frets.</item>
///         <item>
///             A chord moves every FRETTED string and that string's slide end; an open string (and its
///             slide) stays where it is, so a moved shape keeps its open strings ringing.
///         </item>
///         <item>
///             A sound any of whose moved frets or slide ends would leave the neck stays exactly where it
///             is, so the caller can name it. The keyboard's <c>MoveFretBy</c> clamps per string
///             instead, which squashes a shape and leaves a slide end behind.
///         </item>
///     </list>
///     The legal frets are <see cref="SoundingPitch.SnapToNeck" />'s: <c>{0} ∪ [capo, 24]</c>, with a
///     fretted chord string and a slide end never landing on 0.
/// </summary>
public static class WholeFretMove
{
    /// <summary>True when <paramref name="note" /> can move <paramref name="delta" /> frets whole.</summary>
    public static bool Fits(GuitarNote note, int delta, int capo) =>
        Legal(note.Fret + delta, capo, open: true) && (note.SlideTo is not { } end || Legal(end + delta, capo, open: false));

    /// <summary>True when every fretted string of <paramref name="chord" /> and its slide end can move.</summary>
    public static bool Fits(Chord chord, int delta, int capo) =>
        chord.ChordNotes.Where(n => n.Fret > 0)
            .All(n => Legal(n.Fret + delta, capo, open: false) && (n.SlideTo is not { } end || Legal(end + delta, capo, open: false)));

    /// <summary>Moves the note and its slide end; a move that does not fit changes nothing.</summary>
    public static void Apply(GuitarNote note, int delta, int capo)
    {
        if (delta == 0 || !Fits(note, delta, capo)) return;
        note.Fret += delta;
        if (note.SlideTo is { } end) note.SlideTo = end + delta;
    }

    /// <summary>Moves every fretted string and its slide end; a move that does not fit changes nothing.</summary>
    public static void Apply(Chord chord, int delta, int capo)
    {
        if (delta == 0 || !Fits(chord, delta, capo)) return;
        foreach (var note in chord.ChordNotes.Where(n => n.Fret > 0))
        {
            note.Fret += delta;
            if (note.SlideTo is { } end) note.SlideTo = end + delta;
        }
    }

    private static bool Legal(int fret, int capo, bool open) =>
        fret <= FretLimits.MaxFret && (open && fret == 0 || fret >= Math.Max(1, capo));
}
