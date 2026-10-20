namespace DotCharter.Services.Editing.Selection;

/// <summary>
///     Existing shapes that CONTAIN a smaller one — the two-string grip a
///     single-note bar is really part of.
///     <para>
///         <b>The gesture this exists for.</b> Generating a hand shape over one
///         note builds a one-string shape, and the charter then opens it and adds
///         the rest of the grip by hand. The next occurrence generates the same
///         one-string seed, which does not match the finished shape, so it
///         mints another — and the whole grip is rebuilt. Measured on one of the owner's
///         sessions: the SAME grip (fret 12 on string 3 plus fret 15 on
///         string 4, fingered 1 and 4) was built <b>seven times</b> at seven
///         positions, producing templates 95, 96, 98, 100, 101, 102 and 103.
///     </para>
///     <para>
///         <b>Why this is an OFFER and never automatic.</b> Binding a bar to a
///         containing shape makes it declare a string the sounding note does not
///         play. That is legitimate and common — a let-ring or arpeggio bar states
///         the whole grip while one note rings — but it is a real authoring
///         decision, and two separate templates for the same frets is sometimes
///         exactly what a charter wants. So this only ever ANSWERS the question
///         "which shapes contain mine"; choosing one stays with the user.
///     </para>
/// </summary>
public static class ChordShapeSuperset
{
    /// <summary>How many suggestions are worth showing before the row is noise.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>
    ///     True when <paramref name="candidate" /> plays everything
    ///     <paramref name="seed" /> does, at the same frets, plus at least one more
    ///     string.
    ///     <para>
    ///         <b>Frets compare EXACTLY, with no capo folding.</b> Elsewhere a fret
    ///         at or below the capo is treated as open, because there the question
    ///         is what SOUNDS. Here the question is which grip the charter is
    ///         looking at, and the fretboard diagram shows absolute frets — so
    ///         offering a shape whose diagram does not match the note under it
    ///         would be a confusing suggestion however identical the pitches.
    ///     </para>
    ///     <para>
    ///         A candidate with the SAME strings is not a superset: that is either
    ///         the same shape (already handled by the find-or-create key) or a
    ///         different one, and neither is something to offer here.
    ///     </para>
    /// </summary>
    public static bool Contains(ChordTemplate? seed, ChordTemplate? candidate)
    {
        if (seed == null || candidate == null) return false;
        if (ReferenceEquals(seed, candidate)) return false;
        if (seed.Frets.Count == 0) return false;
        if (candidate.Frets.Count <= seed.Frets.Count) return false;

        // An arpeggio is a different KIND of shape, not a bigger one — the flag
        // drives ChordMask.Arpeggio, the .eof section type and the purple
        // bracket instead of a hand-shape bar. TWO flags spell it and each
        // import path sets only one, so ask IsArpeggio, never one flag.
        if (seed.IsArpeggio != candidate.IsArpeggio) return false;

        foreach (var (stringIndex, fret) in seed.Frets)
        {
            if (!candidate.Frets.TryGetValue(stringIndex, out var candidateFret)) return false;
            if (candidateFret != fret) return false;
        }

        return true;
    }

    /// <summary>
    ///     Shapes in <paramref name="arrangement" /> that contain
    ///     <paramref name="seed" />, closest fit first.
    ///     <para>
    ///         Ordered by how many strings they ADD, so the two-string grip a
    ///         one-note bar belongs to is offered ahead of a six-string chord that
    ///         merely happens to include the same fret. Ties break on template id
    ///         so the row is stable between selections rather than reshuffling as
    ///         the dictionary enumerates.
    ///     </para>
    /// </summary>
    public static List<ChordTemplate> FindContaining(
        Arrangement arrangement, ChordTemplate seed, int max = MaxSuggestions)
    {
        var found = new List<ChordTemplate>();
        foreach (var candidate in arrangement.ChordTemplates)
        {
            if (Contains(seed, candidate))
                found.Add(candidate);
        }

        found.Sort((a, b) =>
        {
            var byExtra = (a.Frets.Count - seed.Frets.Count).CompareTo(b.Frets.Count - seed.Frets.Count);
            return byExtra != 0 ? byExtra : a.Id.CompareTo(b.Id);
        });

        if (found.Count > max) found.RemoveRange(max, found.Count - max);
        return found;
    }
}
