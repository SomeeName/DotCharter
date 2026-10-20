using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Which techniques are drawn on a sound's TAIL rather than on its head.
///     <para>
///         The distinction is a readability one and it decides whether a sustain
///         is load-bearing. A bend, a slide, vibrato or tremolo is rendered ALONG
///         the tail — shorten it and the technique becomes invisible or
///         unreadable. A mute, a HOPO, a harmonic or an accent is drawn on the
///         note head and says exactly as much with no tail at all.
///     </para>
///     <para>
///         <c>LinkNext</c> counts because a linked sound rings INTO the
///         next one: its tail is the link, and the spacing enforcer already
///         treats it that way.
///     </para>
/// </summary>
public static class TailTechniques
{
    /// <summary>True when this sound's TAIL carries the technique — so its
    /// length is meaning, not spacing.</summary>
    public static bool AreDrawnOnTheTail(GuitarNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.BendValues.Count > 0 ||
               note.SlideTo.HasValue ||
               note.UnpitchedSlide ||
               note.Vibrato ||
               note.Tremolo ||
               note.LinkNext;
    }

    /// <inheritdoc cref="AreDrawnOnTheTail(GuitarNote)" />
    public static bool AreDrawnOnTheTail(ChordNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.BendValues.Count > 0 ||
               note.SlideTo.HasValue ||
               note.UnpitchedSlide ||
               note.Vibrato ||
               note.Tremolo ||
               note.LinkNext;
    }

    /// <summary>A chord keeps its length when ANY of its notes needs a tail —
    /// the chord's sustain is one value for all of them.</summary>
    public static bool AreDrawnOnTheTail(Chord chord)
    {
        ArgumentNullException.ThrowIfNull(chord);
        foreach (var note in chord.ChordNotes)
        {
            if (AreDrawnOnTheTail(note))
                return true;
        }

        return false;
    }
}
