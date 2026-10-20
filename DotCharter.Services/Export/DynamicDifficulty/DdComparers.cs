namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>
///     Ported from Rocksmith2014.NET (src/Rocksmith2014.DD/Comparers.fs; MIT, Copyright (c)
///     2020 Tapio Malmberg — see THIRD-PARTY-NOTICES.md § 2.1): how alike two phrases'
///     notes and chords are. The F# immutable lists are arrays read from an offset; the
///     recursion is unrolled into loops, step for step.
/// </summary>
internal static class DdComparers
{
    public static bool SameNote(DdNote n1, DdNote n2) =>
        n1.Fret == n2.Fret
        && n1.String == n2.String
        && n1.Mask == n2.Mask
        && n1.SlideTo == n2.SlideTo
        && n1.SlideUnpitchTo == n2.SlideUnpitchTo
        && n1.Vibrato == n2.Vibrato
        && n1.Tap == n2.Tap
        && n1.MaxBend == n2.MaxBend;

    public static bool SameChord(DdChord c1, DdChord c2) =>
        c1.ChordId == c2.ChordId && c1.Mask == c2.Mask && SameChordNotes(c1, c2);

    private static bool SameChordNotes(DdChord c1, DdChord c2)
    {
        if (c1.ChordNotes == null || c2.ChordNotes == null) return c1.ChordNotes == null && c2.ChordNotes == null;
        if (c1.ChordNotes.Count != c2.ChordNotes.Count) return false;
        for (var i = 0; i < c1.ChordNotes.Count; i++)
        {
            if (!SameNote(c1.ChordNotes[i], c2.ChordNotes[i])) return false;
        }

        return true;
    }

    /// <summary>The number of same items in two lists when their order matters
    /// (<c>getSameItemCount</c>).</summary>
    public static int GetSameItemCount<T>(Func<T, T, bool> equal, IReadOnlyList<T> input1, IReadOnlyList<T> input2)
    {
        var count = 0;
        var at1 = 0;
        var at2 = 0;
        var len1 = input1.Count;
        var len2 = input2.Count;
        while (at1 < input1.Count && at2 < input2.Count)
        {
            if (equal(input1[at1], input2[at2]))
            {
                // First items match, continue count
                count++;
                len1--;
                len2--;
                at1++;
                at2++;
            }
            else if (len1 > len2)
            {
                // Skip items until the list lengths are the same
                len1--;
                at1++;
            }
            else if (len1 < len2)
            {
                len2--;
                at2++;
            }
            else
            {
                // Same lengths, find the next matching items, taking into account skipped items
                var next = FindNextMatch(equal, input1, input2, at1, at2);
                if (next == null) return count;
                count++;
                (at1, at2) = next.Value;
                len1 = input1.Count - at1;
                len2 = input2.Count - at2;
            }
        }

        return count;
    }

    /// <summary><c>findNextMatch</c> called as <c>findNextMatch equal [head1] [head2] tail1 tail2</c>:
    /// returns the new starting offsets. A hit among the skipped items skips that many
    /// items of the ORIGINAL tail (the index counts from the most recently skipped), as
    /// the F# does.</summary>
    private static (int At1, int At2)? FindNextMatch<T>(Func<T, T, bool> equal, IReadOnlyList<T> list1,
        IReadOnlyList<T> list2, int head1, int head2)
    {
        var tail1Start = head1 + 1;
        var tail2Start = head2 + 1;
        // Skipped items, most recent last (the F# list keeps the most recent first).
        var skipped1 = new List<T> { list1[head1] };
        var skipped2 = new List<T> { list2[head2] };
        var r1 = tail1Start;
        var r2 = tail2Start;
        while (r1 < list1.Count && r2 < list2.Count)
        {
            var h1 = list1[r1];
            var h2 = list2[r2];
            if (equal(h1, h2)) return (r1 + 1, r2 + 1);

            var i = IndexFromMostRecent(skipped1, x => equal(h2, x));
            if (i >= 0) return (tail1Start + i, r2 + 1);

            var j = IndexFromMostRecent(skipped2, x => equal(h1, x));
            if (j >= 0) return (r1 + 1, tail2Start + j);

            skipped1.Add(h1);
            skipped2.Add(h2);
            r1++;
            r2++;
        }

        return null;
    }

    private static int IndexFromMostRecent<T>(List<T> stack, Func<T, bool> predicate)
    {
        for (var k = stack.Count - 1; k >= 0; k--)
        {
            if (predicate(stack[k])) return stack.Count - 1 - k;
        }

        return -1;
    }

    /// <summary>The similarity of two lists in percent (<c>getSimilarityPercent</c>).</summary>
    public static double GetSimilarityPercent<T>(Func<T, T, bool> equal, IReadOnlyList<T> l1, IReadOnlyList<T> l2)
    {
        if (l1.Count == 0 && l2.Count == 0) return 100.0;
        if (l1.Count == 0 || l2.Count == 0) return 0.0;
        double sameCount = GetSameItemCount(equal, l1, l2);
        double maxCount = Math.Max(l1.Count, l2.Count);
        return 100.0 * sameCount / maxCount;
    }

    /// <summary>The most two lists can be alike, from their lengths alone
    /// (<c>getMaxSimilarityFastest</c>).</summary>
    public static double GetMaxSimilarityFastest(int count1, int count2)
    {
        if (count1 == 0 && count2 == 0) return 100.0;
        if (count1 == 0 || count2 == 0) return 0.0;
        return 100.0 * Math.Min(count1, count2) / Math.Max(count1, count2);
    }

    /// <summary>The most two lists can be alike, from how many items share a projection
    /// (<c>getMaxSimilarityFast</c>). Divides by the FIRST list's length, as the F#
    /// does (<c>max l1.Length l1.Length</c>).</summary>
    public static double GetMaxSimilarityFast<T>(Func<T, int> projection, IReadOnlyList<T> l1, IReadOnlyList<T> l2)
    {
        if (l1.Count == 0 && l2.Count == 0) return 100.0;
        if (l1.Count == 0 || l2.Count == 0) return 0.0;
        var map1 = l1.CountBy(projection).ToDictionary(kv => kv.Key, kv => kv.Value);
        var map2 = l2.CountBy(projection).ToDictionary(kv => kv.Key, kv => kv.Value);
        var sameCount = 0;
        foreach (var (key, count1) in map1)
        {
            if (map2.TryGetValue(key, out var count2)) sameCount += Math.Min(count1, count2);
        }

        return 100.0 * sameCount / l1.Count;
    }

    public static int ChordProjection(DdChord chord) => chord.ChordId;

    public static int NoteProjection(DdNote note) => (short)((short)note.String | (short)(note.Fret << 8));
}
