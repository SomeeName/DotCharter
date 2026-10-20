namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The slide target of a chord, read and written the way EOF and rscharter
/// do it: ONE number, the target of the LOWEST-FRETTED string in
///     scope, with every other string sliding by the same number of frets.
///     <list type="bullet">
///         <item>
///             EOF keeps one <c>slideend</c> per chord and exports each string
///             as <c>fret + (slideend - lowestfret)</c> (<c>rs.c</c>); its
///             "slide end fret" dialog writes that one number
///             (<c>menu/note.c</c>, <c>eof_pro_guitar_note_slide_end_fret</c>).
///             On import, chord notes that slide by DIFFERENT amounts keep their
///             slides per string instead of promoting one to the chord
///             (<c>rs_import.c</c>).
///         </item>
///         <item>
///             rscharter's <c>GuitarSoundSelectionEditor.setSlide</c> takes the
///             lowest fretted EDITED string's fret as the anchor and moves every
///             edited fretted string by the same difference, skipping open
///             strings; its readout is blank unless every edited fretted string
///             agrees.
///         </item>
///     </list>
///     <para>
///         The typed number is NOT written to every string in
///         scope as an ABSOLUTE fret and read back from the lowest string: a
///         5+8 chord whose 5 slides to 6 would read "6" for the whole chord, and
///         typing 7 would send the 8 DOWN to 7.
///     </para>
///     <para>
///         Pure, so the default gate pins it; the inspector passes it the
///         strings the string-edit mask targets (<c>MaskedChordNotes</c>).
///     </para>
/// </summary>
internal static class ChordSlideScope
{
    /// <summary>The highest fret a slide may end on — the same ceiling the
    /// field's <c>Maximum</c> and a typed fret use.</summary>
    internal const int MaxFret = FretDigitAccumulator.MaxTypedFret;

    /// <summary>
    ///     The strings a slide target MOVES: the fretted ones in scope. An open
    ///     string cannot slide, so it neither anchors nor moves — unless scope
    ///     holds nothing else, when the offset from fret 0 is the typed fret
    ///     itself and the old single-string behaviour is kept.
    /// </summary>
    internal static IReadOnlyList<ChordNote> Movers(IReadOnlyList<ChordNote> inScope)
    {
        var fretted = inScope.Where(n => n.Fret > 0).ToArray();
        return fretted.Length > 0 ? fretted : inScope;
    }

    /// <summary>What the slide field shows for these strings.</summary>
    internal static ChordSlideReadout Read(IReadOnlyList<ChordNote> inScope)
    {
        var movers = Movers(inScope);
        if (movers.Count == 0) return new ChordSlideReadout(null, false, movers);

        var offsets = movers.Select(Offset).Distinct().ToArray();
        if (offsets.Length > 1) return new ChordSlideReadout(null, true, movers);

        var target = offsets[0] is { } offset ? Anchor(movers) + offset : (int?)null;
        return new ChordSlideReadout(target, false, movers);
    }

    /// <summary>
    ///     The writes a typed target makes: <paramref name="typed" /> for the
    ///     lowest-fretted string in scope and the same offset for every other
    ///     mover. Null clears the slide on every string in scope. A target that
    ///     would put ANY mover off the fretboard is refused whole — nothing is
    ///     clamped, because a clamped string stops moving with the others.
    /// </summary>
    internal static ChordSlidePlan Plan(IReadOnlyList<ChordNote> inScope, int? typed)
    {
        if (typed is not { } target)
            return ChordSlidePlan.Apply(inScope.Select(n => new ChordSlideWrite(n.StringIndex, null)).ToArray());

        var movers = Movers(inScope);
        return movers.Count == 0 ? ChordSlidePlan.Apply([]) : PlanOffset(movers, target - Anchor(movers));
    }

    /// <summary>The same writes for a known OFFSET — "Apply techniques to all
    /// strings" copies the source string's slide this way, so a 5→6 copied
    /// onto an 8 makes it 8→9 rather than 8→6.</summary>
    internal static ChordSlidePlan PlanOffset(IReadOnlyList<ChordNote> movers, int offset)
    {
        var writes = new List<ChordSlideWrite>(movers.Count);
        foreach (var note in movers)
        {
            var end = note.Fret + offset;
            if (end is < 1 or > MaxFret)
                return ChordSlidePlan.Refuse(new ChordSlideOffBoard(note.StringIndex, note.Fret, end));
            writes.Add(new ChordSlideWrite(note.StringIndex, end));
        }

        return ChordSlidePlan.Apply(writes);
    }

    /// <summary>How far a string slides, or null when it holds.</summary>
    internal static int? Offset(ChordNote note) => note.SlideTo is { } end ? end - note.Fret : null;

    private static int Anchor(IReadOnlyList<ChordNote> movers) => movers.Min(n => n.Fret);
}

/// <summary>The slide field's state: the agreed target (null when nothing
/// slides or the strings differ), whether they differ, and the strings it was
/// read from — what the per-string summary lists.</summary>
internal sealed record ChordSlideReadout(int? Target, bool IsMixed, IReadOnlyList<ChordNote> Strings);

/// <summary>One string's new slide end.</summary>
internal readonly record struct ChordSlideWrite(int StringIndex, int? SlideTo);

/// <summary>The string a refused target would push off the fretboard.</summary>
internal readonly record struct ChordSlideOffBoard(int StringIndex, int Fret, int WouldEndOn);

/// <summary>Either the writes to make, or why none are made.</summary>
internal sealed record ChordSlidePlan(IReadOnlyList<ChordSlideWrite> Writes, ChordSlideOffBoard? Refusal)
{
    internal static ChordSlidePlan Apply(IReadOnlyList<ChordSlideWrite> writes) => new(writes, null);

    internal static ChordSlidePlan Refuse(ChordSlideOffBoard why) => new([], why);

    /// <summary>Writes the plan onto a chord's notes, matched by string.</summary>
    internal void ApplyTo(IEnumerable<ChordNote> notes)
    {
        foreach (var note in notes)
        {
            foreach (var write in Writes)
            {
                if (write.StringIndex == note.StringIndex)
                    note.SlideTo = write.SlideTo;
            }
        }
    }
}
