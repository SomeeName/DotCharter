using System.Numerics;

namespace DotCharter.Core.Domain.Vocals;

/// <summary>
///     The one minimum-length and distinct-start rule for vocals, shared by every Rocksmith
///     vocals XML writer (<see cref="Normalize" />, on the written whole milliseconds) and by
///     lyric auto-sync when it places syllables (<see cref="EnforceMinimum" />): no entry has
///     length 0 and the starts rise strictly.
///     <para>
///         <b>Why: Editor on Fire's lyric import.</b> It reads whole milliseconds, and when a
///         piece has duration 0 and the next piece starts at the same millisecond it MERGES the
///         two into one piece (an overlap is first shortened to the next start, so an overlap
///         onto the same start ends there too). That merge also runs across a line end: the last
///         piece of the previous line absorbs the first piece of the new line and unlinks it from
///         its own line, and the import then fails with "Invalid lyric file". A start earlier
///         than the previous one aborts the import outright, and a line whose only piece has
///         length 0 fails its line-length check. The aligner can report exactly such a pair
///         where it cannot clearly hear a short syllable.
///     </para>
///     <para>
///         <b>Only the offending entries change.</b> A run of entries that start at the same
///         time is spread out after its first, at most <see cref="MinLengthMs" /> apart and
///         never past the next later start; a moved entry keeps its original end where that is
///         still after its new start and never runs into the next start. An entry of length 0
///         gets <see cref="MinLengthMs" />, or the gap to the next start if that is shorter.
///         Every other entry is left exactly as it was. A start EARLIER than the one before it
///         is left alone: in a chart that is a line out of time order, which the export check
///         already blocks (<c>KeyLyricsOutOfOrder</c>), and moving it would only hide where it
///         is sung.
///     </para>
/// </summary>
public static class VocalTiming
{
    /// <summary>
    ///     The length a zero-length entry gets, and the most a coincident start moves. A
    ///     hand-repaired file Editor on Fire accepted used 24-30 ms.
    /// </summary>
    public const int MinLengthMs = 25;

    /// <summary>
    ///     The export rule: rewrites <paramref name="timesMs" /> and <paramref name="lengthsMs" />
    ///     (in written order, the same count) in place so that no length is 0 and no two starts
    ///     are equal. A short but non-zero length is written as it is.
    /// </summary>
    public static void Normalize(int[] timesMs, int[] lengthsMs) => Apply(timesMs, lengthsMs, raiseShort: false);

    /// <summary>
    ///     The placement rule for syllables a program times itself (lyric auto-sync): the same
    ///     spreading as <see cref="Normalize" />, and every entry at least
    ///     <see cref="MinLengthMs" /> long, or as long as the room to the next start when that
    ///     is less. An end is only ever lengthened, never shortened.
    /// </summary>
    public static void EnforceMinimum(double[] startsMs, double[] lengthsMs) =>
        Apply(startsMs, lengthsMs, raiseShort: true);

    private static void Apply<T>(T[] times, T[] lengths, bool raiseShort) where T : INumber<T>
    {
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(lengths);
        if (times.Length != lengths.Length)
            throw new ArgumentException("Every vocal needs one time and one length.", nameof(lengths));

        var count = times.Length;
        var min = T.CreateChecked(MinLengthMs);
        var originalEnds = new T[count];
        for (var i = 0; i < count; i++) originalEnds[i] = times[i] + T.Max(T.Zero, lengths[i]);

        var moved = SpreadCoincidentStarts(times, min);

        for (var i = 0; i < count; i++)
        {
            var start = times[i];
            var hasNext = i + 1 < count;
            var gap = hasNext ? times[i + 1] - start : min;
            var end = moved[i] ? T.Max(originalEnds[i], start) : start + T.Max(T.Zero, lengths[i]);
            if (moved[i] && hasNext && gap > T.Zero) end = T.Min(end, times[i + 1]);
            var tooShort = raiseShort ? end - start < min : end <= start;
            if (tooShort) end = T.Max(end, start + (gap > T.Zero ? T.Min(min, gap) : min));
            lengths[i] = end - start;
        }
    }

    private static bool[] SpreadCoincidentStarts<T>(T[] times, T min) where T : INumber<T>
    {
        var count = times.Length;
        var moved = new bool[count];
        var first = 0;
        while (first < count)
        {
            var last = first;
            while (last + 1 < count && times[last + 1] == times[first]) last++;
            if (last > first)
            {
                var members = T.CreateChecked(last - first + 1);
                var room = last + 1 < count ? times[last + 1] - times[first] : min * members;
                var step = T.Clamp(room / members, T.One, min);
                for (var k = first + 1; k <= last; k++)
                {
                    times[k] = times[first] + (T.CreateChecked(k - first) * step);
                    moved[k] = true;
                }
            }

            first = last + 1;
        }

        // Only reachable when more entries share a start than there are milliseconds before
        // the next one: push the rest along one millisecond at a time. An earlier start the
        // spreading did not cause stays where it is.
        for (var i = 1; i < count; i++)
        {
            if (times[i] > times[i - 1] || (times[i] < times[i - 1] && !moved[i - 1])) continue;
            times[i] = times[i - 1] + T.One;
            moved[i] = true;
        }

        return moved;
    }
}
