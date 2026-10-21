namespace DotCharter.Services.Parsing.MoverPhrases;

/// <summary>
///     Where a moveR&lt;N&gt; phrase lands, and the N that lands one on a given note.
///     <para>
///         The count is "at or after": a note ON the beat is the 1st. Both tools agree —
///         DLC Builder's <c>Utils.getTimes</c> keeps <c>x.Time &gt;= startTime</c>, and EOF's
///         <c>eof_get_pos_num_notes_after_timestamp</c> skips only <c>pos &lt; pos</c>. Notes
///         at one time (a split chord) count once.
///     </para>
/// </summary>
public static class MoverNoteCount
{
    /// <summary>
    ///     DLC Builder's <c>Utils.findTimeOfNthNoteFrom</c>, step for step: the first
    ///     <paramref name="n" /> distinct note times and the first <paramref name="n" />
    ///     distinct chord times at or after <paramref name="start" />, merged, sorted, the
    ///     n-th. Null where DLC Builder throws (fewer than n, or n below 1).
    /// </summary>
    public static int? FindTimeOfNthNoteFrom(IEnumerable<int> noteTimes, IEnumerable<int> chordTimes, int start,
        int n)
    {
        if (n < 1) return null;

        var merged = FirstTimes(noteTimes, start, n).Concat(FirstTimes(chordTimes, start, n)).Order().ToList();
        return merged.Count >= n ? merged[n - 1] : null;
    }

    /// <summary>
    ///     The smallest N for which <see cref="FindTimeOfNthNoteFrom" /> from
    ///     <paramref name="start" /> returns <paramref name="target" />; null when no N does
    ///     (no note or chord starts at <paramref name="target" />).
    /// </summary>
    public static int? CountTo(IEnumerable<int> noteTimes, IEnumerable<int> chordTimes, int start, int target)
    {
        if (target < start) return null;

        var notes = noteTimes as IReadOnlyCollection<int> ?? noteTimes.ToList();
        var chords = chordTimes as IReadOnlyCollection<int> ?? chordTimes.ToList();
        var candidates = notes.Concat(chords).Count(t => t >= start && t <= target);
        for (var n = 1; n <= candidates; n++)
        {
            var at = FindTimeOfNthNoteFrom(notes, chords, start, n);
            if (at == target) return n;
            if (at is null || at > target) return null;
        }

        return null;
    }

    private static IEnumerable<int> FirstTimes(IEnumerable<int> times, int start, int n) =>
        times.Where(t => t >= start).Distinct().Take(n);
}
