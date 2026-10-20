namespace DotCharter.Services.Editing.DraftMerge;

/// <summary>
///     What merging a chart-from-audio draft into ONE phrase would do to the sounds already
/// there, counted per string at each position: a draft note that restates an existing
///     one is <see cref="Unchanged" />, one that sits on the same position as an existing sound but
///     on another fret or string is <see cref="Changed" />, one with nothing under it is
///     <see cref="Added" />, and an existing sound the draft has nothing for is <see cref="Removed" />.
///     <para>
///         Pure and order-free: the counts depend on the sets, not on the collections' order. An
///         existing chord counts one sound per chord note; a chord with no chord notes is one sound
///         the draft can never restate.
///     </para>
/// </summary>
public readonly record struct DraftPhraseDiff(int Added, int Changed, int Removed, int Unchanged)
{
    /// <summary>The merge would leave the phrase exactly as it is.</summary>
    public bool IsEmpty => Added + Changed + Removed == 0;

    /// <summary>Compares the <paramref name="draft" /> notes with the <paramref name="existingNotes" /> and
    /// <paramref name="existingChords" /> they would replace.</summary>
    public static DraftPhraseDiff Compute(IEnumerable<GuitarNote> existingNotes, IEnumerable<Chord> existingChords,
        IEnumerable<GuitarNote> draft)
    {
        ArgumentNullException.ThrowIfNull(existingNotes);
        ArgumentNullException.ThrowIfNull(existingChords);
        ArgumentNullException.ThrowIfNull(draft);

        var existing = existingNotes.Select(n => new Sound(n.Position, n.StringIndex, n.Fret))
            .Concat(existingChords.SelectMany(Sounds))
            .ToLookup(s => s.Position);
        var proposed = draft.Select(n => new Sound(n.Position, n.StringIndex, n.Fret)).ToLookup(s => s.Position);

        int added = 0, changed = 0, removed = 0, unchanged = 0;
        foreach (var position in existing.Select(g => g.Key).Union(proposed.Select(g => g.Key)))
        {
            var before = existing[position].ToList();
            var after = proposed[position].ToList();

            // The same string and fret: restated.
            unchanged += Pair(before, after, (a, b) => a.String == b.String && a.Fret == b.Fret);
            // The same string, another fret: re-fretted.
            changed += Pair(before, after, (a, b) => a.String == b.String);
            // Anything left at this position on both sides: moved to another string.
            var moved = Math.Min(before.Count, after.Count);
            changed += moved;
            added += after.Count - moved;
            removed += before.Count - moved;
        }

        return new DraftPhraseDiff(added, changed, removed, unchanged);
    }

    /// <summary>Takes every pair <paramref name="match" /> accepts out of both lists; returns how many.</summary>
    private static int Pair(List<Sound> before, List<Sound> after, Func<Sound, Sound, bool> match)
    {
        var pairs = 0;
        for (var i = before.Count - 1; i >= 0; i--)
        {
            var j = after.FindIndex(b => match(before[i], b));
            if (j < 0) continue;
            before.RemoveAt(i);
            after.RemoveAt(j);
            pairs++;
        }

        return pairs;
    }

    private static IEnumerable<Sound> Sounds(Chord chord) => chord.ChordNotes.Count == 0
        ? [new Sound(chord.Position, -1, -1)]
        : chord.ChordNotes.Select(n => new Sound(chord.Position, n.StringIndex, n.Fret));

    private readonly record struct Sound(FractionalPosition Position, int String, int Fret);
}
