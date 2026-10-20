namespace DotCharter.Services.Editing.Selection;

/// <summary>
///     What "the same fret-hand position" means to select-like: the anchor's own
///     fret and width, PLUS the sounds it actually governs, expressed relative
///     to it.
///     <para>
///         <b>The fret alone is emphatically NOT the key.</b> "Every anchor on
///         fret 7" is a meaningless selection — there are many different reasons
///         to park the hand at the 7th fret, and re-fretting all of them because
///         one was wrong is a chart-wide corruption dressed up as a convenience.
///         An anchor is only interchangeable with another when the hand is doing
///         the same JOB: sitting at the same position, spanning the same number
///         of frets, over the same run of notes.
///     </para>
///     <para>
///         So the signature carries the anchor's SPAN — every sound from this
///         anchor up to the next one, as (offset from the anchor's onset,
///         string, fret − anchor fret). Chord notes count individually; a chord
///         and the same pitches played as separate notes are the same hand
///         shape. Technique is absent by construction, exactly as in
///         <see cref="ChordShapeSignature" />: a palm mute does not move your
///         hand.
///     </para>
///     <para>
///         An anchor governing NO sounds still has a signature and matches only
///         other empty anchors of the same fret and width. Those are real (a
///         phrase-opening position over a rest), and lumping them in with a
///         busy anchor at the same fret would be exactly the over-selection this
///         type exists to prevent.
///     </para>
/// </summary>
public sealed class AnchorSpanSignature
{
    private readonly List<GovernedSound> _governed;

    private AnchorSpanSignature(int fret, int width, List<GovernedSound> governed)
    {
        Fret = fret;
        Width = width;
        _governed = governed;
    }

    /// <summary>The fret the hand sits on.</summary>
    public int Fret { get; }

    /// <summary>How many frets the hand spans.</summary>
    public int Width { get; }

    /// <summary>How many sounds this anchor governs — diagnostics only.</summary>
    public int GovernedCount => _governed.Count;

    /// <summary>
    ///     The anchor's position, its span, and everything played inside it.
    ///     The span ends at the next anchor in the level (by position); the last
    ///     anchor governs everything after it.
    /// </summary>
    public static AnchorSpanSignature Build(FretAnchor anchor, ChartLevel level)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(level);

        FractionalPosition? spanEnd = null;
        foreach (var other in level.Anchors)
        {
            if (other.Position <= anchor.Position) continue;
            if (spanEnd == null || other.Position < spanEnd.Value) spanEnd = other.Position;
        }

        var governed = new List<GovernedSound>();

        foreach (var note in level.Notes)
        {
            if (!note.Position.IsWithin(anchor.Position, spanEnd)) continue;
            governed.Add(new GovernedSound(note.Position - anchor.Position, note.StringIndex,
                note.Fret - anchor.Fret));
        }

        foreach (var chord in level.Chords)
        {
            if (!chord.Position.IsWithin(anchor.Position, spanEnd)) continue;
            foreach (var chordNote in chord.ChordNotes)
            {
                governed.Add(new GovernedSound(chord.Position - anchor.Position, chordNote.StringIndex,
                    chordNote.Fret - anchor.Fret));
            }
        }

        governed.Sort(static (a, b) =>
        {
            var byPosition = a.Offset.CompareTo(b.Offset);
            if (byPosition != 0) return byPosition;
            var byString = a.StringIndex.CompareTo(b.StringIndex);
            return byString != 0 ? byString : a.RelativeFret.CompareTo(b.RelativeFret);
        });

        return new AnchorSpanSignature(anchor.Fret, anchor.Width, governed);
    }

    /// <summary>
    ///     "The hand is doing the same job here": same fret, same width, and the
    ///     same run of sounds underneath. Deliberately strict — a false positive
    ///     re-frets a passage the user never looked at.
    /// </summary>
    public bool Matches(AnchorSpanSignature other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Fret != other.Fret || Width != other.Width) return false;
        if (_governed.Count != other._governed.Count) return false;

        for (var i = 0; i < _governed.Count; i++)
        {
            if (!_governed[i].Equals(other._governed[i]))
                return false;
        }

        return true;
    }

    /// <summary>One sound under an anchor, relative to it — so the same run at a
    /// different bar and a different neck position compares equal once the
    /// anchor's own fret is accounted for.</summary>
    private readonly record struct GovernedSound(FractionalPosition Offset, int StringIndex, int RelativeFret);
}
