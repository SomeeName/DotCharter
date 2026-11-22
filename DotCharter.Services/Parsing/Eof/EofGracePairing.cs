namespace DotCharter.Services.Parsing.Eof;

/// <summary>Resolve wire identities before folding moves any target's attack.</summary>
internal static class EofGracePairing
{
    public static Dictionary<int, int> Targets(IReadOnlyList<EofRawProGuitarNote> notes,
        IList<BeatMarker> beats, Func<int, bool> hasFingering)
    {
        var pairs = new Dictionary<int, int>();
        for (var i = 0; i < notes.Count; i++)
        {
            var target = EofGraceTiming.TargetIndex(notes, i, beats, hasFingering(i));
            if (target >= 0) pairs[i] = target;
        }
        // Two ornaments cannot claim the same target. Neither identity is safe.
        var ambiguous = pairs.Values.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var pair in pairs.Where(p => ambiguous.Contains(p.Value)).ToList()) pairs.Remove(pair.Key);

        // A played neighbour still vetoes folding. Only other complete marker
        // identities may interleave, including their targets. Prune until every
        // skipped sound belongs to an identity that itself survives validation.
        var removed = true;
        while (removed)
        {
            removed = false;
            var identities = pairs.Keys.Concat(pairs.Values).ToHashSet();
            foreach (var pair in pairs.ToList())
            {
                if (!HasUnpairedNeighbour(notes, pair.Key, pair.Value, identities)) continue;
                pairs.Remove(pair.Key);
                removed = true;
            }
        }
        return pairs;
    }

    private static bool HasUnpairedNeighbour(IReadOnlyList<EofRawProGuitarNote> notes, int ornamentIndex,
        int targetIndex, HashSet<int> identities)
    {
        var ornament = notes[ornamentIndex];
        for (var i = ornamentIndex + 1; i < targetIndex; i++)
        {
            var note = notes[i];
            if (note.Difficulty == ornament.Difficulty &&
                ((note.NoteBitmask & ~note.GhostBitmask) & ornament.NoteBitmask) != 0 && !identities.Contains(i))
            {
                return true;
            }
        }
        return false;
    }
}
