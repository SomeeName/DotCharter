using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Utilities;

public static class TimelineMath
{
    /// <summary>Beat length used when the grid cannot supply one — 120 BPM.</summary>
    private const double DefaultBeatLengthMs = 500.0;

    /// <summary>
    ///     How far outside the beat grid <see cref="PositionFromMs" /> will extrapolate,
    ///     in 1/192-beat ticks (±1,000,000 beats). Bounds the carry into
    ///     <see cref="FractionalPosition.BeatId" /> so a nonsense timestamp cannot
    ///     overflow it.
    /// </summary>
    private const double MaxExtrapolationTicks = 192.0 * 1_000_000.0;

    /// <summary>
    ///     A single-beat grid has no interval to interpolate over, so the marker's own
    ///     tempo is the only information available. Every importer populates
    ///     <see cref="BeatMarker.Bpm" />; assuming 120 misplaced every sound on a
    ///     one-beat grid at any other tempo.
    /// </summary>
    private static double BeatLengthMs(BeatMarker beat) => beat.Bpm > 0 ? 60000.0 / beat.Bpm : DefaultBeatLengthMs;

    public static double CalculateTimeMs(FractionalPosition pos, IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return 0;
        if (beats.Count == 1)
            return beats[0].Time + (pos.BeatId + pos.Fraction.DoubleValue) * BeatLengthMs(beats[0]);

        if (pos.BeatId >= 0 && pos.BeatId + 1 < beats.Count)
        {
            var beat = beats[pos.BeatId];
            var nextBeat = beats[pos.BeatId + 1];
            return beat.Time + pos.Fraction.DoubleValue * (nextBeat.Time - beat.Time);
        }

        var anchorIndex = pos.BeatId < 0 ? 0 : beats.Count - 2;
        var anchorBeat = beats[anchorIndex];
        var nextAnchor = beats[anchorIndex + 1];
        var beatLength = nextAnchor.Time - anchorBeat.Time;
        var totalBeatsFromAnchor = pos.BeatId - anchorIndex + pos.Fraction.DoubleValue;

        return anchorBeat.Time + totalBeatsFromAnchor * beatLength;
    }

    /// <summary>
    ///     The inverse of <see cref="CalculateTimeMs" />, including OUTSIDE the beat
    ///     grid: a time before the first beat extrapolates backwards off the first
    ///     interval and a time past the last beat extrapolates off the last one, both
    ///     exactly as <see cref="CalculateTimeMs" /> does. Snapped to 1/192 of a beat.
    /// </summary>
    public static FractionalPosition PositionFromMs(double ms, IList<BeatMarker> beats)
    {
        if (!TryBeatOffsetFromMs(ms, beats, out var beatIdx, out var offsetBeats))
            return FractionalPosition.Zero;

        const int den = 192; // High precision snap for legacy conversions

        var ticks = Math.Round(offsetBeats * den);
        ticks = Math.Clamp(ticks, -MaxExtrapolationTicks, MaxExtrapolationTicks);

        // A tick count outside [0, den) is not an error here — it is how
        // extrapolation is expressed. The FractionalPosition ctor carries the
        // whole (and negative) beats into BeatId and leaves Fraction in [0, 1).
        return new FractionalPosition(beatIdx, new Fraction((int)ticks, den));
    }

    /// <summary>
    ///     The grids <see cref="PositionFromImportedMs" /> tries, coarsest first. Each
    ///     divides the next, so any sum or difference of two imported positions
    ///     stays on the finest one and <see cref="Fraction" /> arithmetic cannot grow
    ///     past it.
    /// </summary>
    private static readonly int[] ImportDenominators = [192, 1920, 19200];

    /// <summary>The ±1,000,000-beat extrapolation bound of <see cref="MaxExtrapolationTicks" />, in beats.</summary>
    private const double MaxExtrapolationBeats = 1_000_000.0;

    /// <summary>
    ///     A time in ms as a file stores it: rounded to the NEAREST whole ms, a half
    ///     to EVEN (62.5 → 62), which is what the SNG exporter has always written:
    ///     half-ms times are common (an eighth of a 120 BPM beat is 62.5 ms), and
    ///     changing the rule would move shipped SNG bytes for nothing. Every ms-keyed writer (RS-XML song, vocals and
    ///     showlights, the SNG phrase/beat fields) goes through this one helper, and
    ///     <see cref="PositionFromImportedMs" /> uses it to decide when a grid point
    ///     is close enough — so the reader and the writers cannot disagree.
    /// A plain <c>(int)</c> cast truncates: 166.67 ms was written as 166.
    /// </summary>
    public static int ToWholeMs(double ms) => (int)Math.Round(ms, MidpointRounding.ToEven);

    /// <summary>
    ///     <see cref="PositionFromMs" /> for a time read from a FILE that stores
    ///     milliseconds (RS-XML, SNG, EOF): the position that writes back as the same
    ///     whole millisecond.
    ///     <para>
    ///         It keeps the 1/192-beat grid whenever that grid point rounds back to
    ///         the source's millisecond — which is every note a charter placed on a
    ///         1/2…1/64 or triplet grid, including DotCharter's own exports. Only a
    ///         note the source placed OFF that grid gets a finer denominator (1/1920,
    ///         then 1/19200). <see cref="PositionFromMs" /> snapped those to 1/192 and
    /// moved them by up to 1/384 beat: 2.6 ms at 60 BPM. 1/19200 is
    ///         exact to the millisecond for any beat shorter than 19.2 s (above
    ///         3.2 BPM).
    ///     </para>
    ///     <para>
    ///         Editor gestures and retiming keep <see cref="PositionFromMs" />: a drag
    ///         has no authored millisecond to preserve, and the 1/192 grid is theirs.
    ///     </para>
    /// </summary>
    public static FractionalPosition PositionFromImportedMs(double ms, IList<BeatMarker> beats)
    {
        if (!TryBeatOffsetFromMs(ms, beats, out var beatIdx, out var offsetBeats))
            return FractionalPosition.Zero;

        offsetBeats = Math.Clamp(offsetBeats, -MaxExtrapolationBeats, MaxExtrapolationBeats);
        var wholeBeats = Math.Floor(offsetBeats);
        var withinBeat = offsetBeats - wholeBeats;
        var sourceMs = ToWholeMs(ms);

        var position = FractionalPosition.Zero;
        foreach (var den in ImportDenominators)
        {
            // The ctor carries a rounded-up `den/den` into BeatId.
            position = new FractionalPosition(beatIdx + (int)wholeBeats,
                new Fraction((long)Math.Round(withinBeat * den), den));
            if (ToWholeMs(CalculateTimeMs(position, beats)) == sourceMs) break;
        }

        return position;
    }

    /// <summary>
    ///     The UN-ROUNDED half of <see cref="PositionFromMs" />: which beat anchors
    ///     <paramref name="ms" />, and how many beats past that anchor it lies —
    ///     extrapolating on both sides exactly as <see cref="CalculateTimeMs" />
    ///     does, and never clamping.
    ///     <para>
    ///     Exposed because a caller that snaps to its OWN grid resolution needs the
    ///     raw offset twice: once to place the element and once to price how far it
    ///     had to move. Rolling that arithmetic by hand is what produced the
    ///     collapse this method exists to prevent — a hand-written version clamped
    ///     the fraction into <c>[0, 1]</c>, so every time past the last beat mapped
    ///     to the SAME position and, being exactly on grid afterwards, scored the
    ///     highest possible confidence.
    ///     </para>
    ///     False (and nothing written) when there is no grid or the time is not
    ///     finite.
    /// </summary>
    public static bool TryBeatOffsetFromMs(double ms, IList<BeatMarker> beats,
        out int beatIndex, out double offsetBeats)
    {
        beatIndex = 0;
        offsetBeats = 0.0;
        if (beats.Count == 0 || !double.IsFinite(ms)) return false;

        const double boundaryEpsilonMs = 0.0001;
        double beatStart;
        double beatLength;

        if (beats.Count == 1)
        {
            beatIndex = 0;
            beatStart = beats[0].Time;
            beatLength = BeatLengthMs(beats[0]);
        }
        else if (ms + boundaryEpsilonMs < beats[0].Time)
        {
            // Before the grid. Clamping the fraction to [0, 0.999] mapped every
            // pre-roll time onto beat 0, so a lead-in sound and the first beat
            // shared one position and collided.
            beatIndex = 0;
            beatStart = beats[0].Time;
            beatLength = beats[1].Time - beats[0].Time;
        }
        else
        {
            beatIndex = Math.Max(0, BinarySearchBeats(beats, ms + boundaryEpsilonMs));
            beatStart = beats[beatIndex].Time;
            beatLength = beatIndex + 1 < beats.Count
                ? beats[beatIndex + 1].Time - beatStart
                // Past the grid, extrapolate off the LAST interval — the old
                // fallback used the FIRST one, which disagreed with
                // CalculateTimeMs on any chart with a tempo change.
                : beats[^1].Time - beats[^2].Time;
        }

        if (!(beatLength > 0)) beatLength = DefaultBeatLengthMs;

        offsetBeats = (ms - beatStart) / beatLength;
        return true;
    }

    public static int BinarySearchBeats(IList<BeatMarker> beats, double time)
    {
        if (beats.Count == 0) return -1;
        int l = 0, r = beats.Count - 1;
        while (l <= r)
        {
            var m = l + (r - l) / 2;
            if (beats[m].Time <= time) l = m + 1;
            else r = m - 1;
        }

        return Math.Max(0, l - 1);
    }

    public static int BinarySearchTimed<T>(IList<T> list, double targetTimeMs, IList<BeatMarker> beats)
        where T : ITimedElement
    {
        if (list.Count == 0) return -1;
        int l = 0, r = list.Count - 1;
        while (l <= r)
        {
            var m = l + (r - l) / 2;
            if (list[m].GetTimeMs(beats) <= targetTimeMs) l = m + 1;
            else r = m - 1;
        }

        return Math.Max(0, l - 1);
    }

    /// <summary>
    ///     How far back <see cref="ExtendStartToSounding{T}" /> looks for an element
    ///     that started before the window and still rings into it. A held note longer
    ///     than this loses its tail at the window edge; no chart has one.
    /// </summary>
    public const double MaxSoundingLookBackMs = 30_000;

    /// <summary>
    ///     Steps a <see cref="GetVisibleRange{T}" /> <paramref name="start" /> back to
    ///     the earliest element still SOUNDING at <paramref name="viewStartMs" />.
    ///     <para>
    ///         <see cref="GetVisibleRange{T}" /> returns one element before the window,
    ///         so a held note followed by two later sounds dropped out of the slice while
    ///         it still rang: a pedal bass under a melody vanished at the 3D strike line
    /// (buffer 0, 1 s look-behind) and lost its 2D tail past 5 s. The scan
    ///         stops at elements that start more than <see cref="MaxSoundingLookBackMs" />
    ///         before the window, so it walks only what a real sustain could cover.
    ///         Elements in between that have stopped ringing are included too; every renderer
    ///         culls per element.
    ///     </para>
    /// </summary>
    public static int ExtendStartToSounding<T>(IList<T> list, int start, double viewStartMs,
        IList<BeatMarker> beats, Func<T, FractionalPosition> endPosition) where T : ITimedElement
    {
        var earliest = start;
        var floorMs = viewStartMs - MaxSoundingLookBackMs;
        for (var i = Math.Min(start, list.Count) - 1; i >= 0; i--)
        {
            var item = list[i];
            if (item.GetTimeMs(beats) < floorMs) break;
            if (endPosition(item).GetPositionMs(beats) > viewStartMs) earliest = i;
        }

        return earliest;
    }

    /// <summary>
    ///     GC-free inclusive index range covering <c>[viewStartMs - bufferMs,
    ///     viewEndMs + bufferMs]</c>. <paramref name="end" /> is <c>-1</c> for an empty
    ///     list.
    ///     <para>
    ///         <b>Precondition:</b> <paramref name="list" /> must be ascending by
    ///         <see cref="ITimedElement.GetTimeMs" />. It is not checked — the searches are
    ///         on the per-frame path and an O(n) verification would defeat them. An
    ///         out-of-order element is simply not found, so it silently disappears from
    ///         both rendering and hit-testing. Anything that reorders a chart collection
    ///         is responsible for re-sorting it.
    ///     </para>
    ///     <para>
    ///         <b>Contract:</b> callers get exactly ONE element before the window plus
    ///         whatever <paramref name="bufferMs" /> covers. An element whose sustain
    ///         reaches into the window from further back than that is NOT included —
    ///         size <paramref name="bufferMs" /> for the longest sustain you must draw.
    ///     </para>
    /// </summary>
    public static void GetVisibleRange<T>(IList<T> list, double viewStartMs, double viewEndMs,
        IList<BeatMarker> beats, double bufferMs, out int start, out int end) where T : ITimedElement
    {
        start = 0;
        end = list.Count - 1;
        if (list.Count == 0)
        {
            end = -1;
            return;
        }

        var targetStart = viewStartMs - bufferMs;
        var targetEnd = viewEndMs + bufferMs;

        // BinarySearchTimed already returns Math.Max(0, l - 1) on a non-empty list,
        // so start is in range and list[start] is at or before targetStart — the
        // step-back and negative guards would never fire, so there are none.
        start = BinarySearchTimed(list, targetStart, beats);

        end = BinarySearchTimed(list, targetEnd, beats);
        if (end < list.Count - 1 && list[end].GetTimeMs(beats) < targetEnd) end++;
    }

    /// <summary>
    ///     The FIRST element strictly after <paramref name="timeMs" />, or
    ///     <c>null</c>. The seek/select-next verbs must not spell this as
    ///     <c>list.Where(x =&gt; time(x) &gt; t).OrderBy(x =&gt; x.Position).FirstOrDefault()</c>
    ///     — an O(n log n) sort of an ALREADY-SORTED list plus an n-element buffer,
    ///     per arrow-key press, on the UI thread.
    ///     <para>
    ///         <b>Precondition</b> (the same one <see cref="GetVisibleRange{T}" />
    ///         carries and every chart collection already satisfies): ascending by
    ///         <see cref="ITimedElement.GetTimeMs" />. Under it the passing elements
    ///         are a SUFFIX, so its first member is exactly what the stable
    ///         <c>OrderBy</c> returned — including the tie-break among sounds that
    ///         share a position, which is the first of them in list order.
    ///     </para>
    /// </summary>
    public static T? FirstAfter<T>(IList<T> list, double timeMs, IList<BeatMarker> beats)
        where T : class, ITimedElement
    {
        var index = FirstIndexAfter(list, timeMs, beats);
        return index < 0 ? null : list[index];
    }

    /// <summary>
    ///     Index of <see cref="FirstAfter{T}" />'s element, or <c>-1</c>.
    /// </summary>
    public static int FirstIndexAfter<T>(IList<T> list, double timeMs, IList<BeatMarker> beats)
        where T : ITimedElement
    {
        int lo = 0, hi = list.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (list[mid].GetTimeMs(beats) > timeMs)
            {
                found = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return found;
    }

    /// <summary>
    ///     The LAST element strictly before <paramref name="timeMs" />, or
    ///     <c>null</c> — the mirror of <see cref="FirstAfter{T}" /> and the same
    ///     precondition.
    ///     <para>
    ///         The tie-break is why this is not just "the last passing index".
    ///         <c>OrderByDescending(x =&gt; x.Position)</c> is STABLE, so among the
    ///         sounds sharing the greatest passing position it yielded the FIRST in
    ///         list order — the low string of a chord's worth of notes, not the
    ///         high one. So the walk back over the equal-time run is behaviour, not
    ///         tidiness; it is bounded by the string count in practice.
    ///     </para>
    /// </summary>
    public static T? LastBefore<T>(IList<T> list, double timeMs, IList<BeatMarker> beats)
        where T : class, ITimedElement
    {
        var index = LastIndexBefore(list, timeMs, beats);
        return index < 0 ? null : list[index];
    }

    /// <summary>
    ///     Index of <see cref="LastBefore{T}" />'s element, or <c>-1</c>.
    /// </summary>
    public static int LastIndexBefore<T>(IList<T> list, double timeMs, IList<BeatMarker> beats)
        where T : ITimedElement
    {
        int lo = 0, hi = list.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (list[mid].GetTimeMs(beats) < timeMs)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found < 0) return -1;
        var tieTime = list[found].GetTimeMs(beats);
        while (found > 0 && list[found - 1].GetTimeMs(beats) >= tieTime) found--;
        return found;
    }

    /// <summary>
    ///     The element CLOSEST in time to <paramref name="timeMs" />, or
    ///     <c>null</c> for an empty list; <paramref name="distanceMs" /> is how
    ///     far it is (<see cref="double.MaxValue" /> when nothing is returned).
    ///     <para>
    ///         Same precondition as <see cref="FirstAfter{T}" /> — ascending by
    ///         <see cref="ITimedElement.GetTimeMs" />, which is what makes
    ///         |t(x) − target| unimodal over the list and therefore the minimum a
    ///         boundary: the LAST element at or before the target, or the FIRST
    ///         one after it. The sheet strip's click-to-select spelled this as two
    ///         full passes with a <c>GetPositionMs</c> per element, over lists the
    ///         spacing enforcer keeps sorted after every committed edit.
    ///     </para>
    ///     <para>
    ///         Ties resolve to the EARLIER element and, among elements sharing a
    ///         time, to the first in LIST order — exactly what the linear scan's
    ///         strict <c>&lt;</c> comparison produced, which for a chord's worth of
    ///         notes is its low string rather than its high one. Both halves of
    ///         that are why this is not a bare binary search: the walk back over
    ///         the equal-time run is behaviour.
    ///     </para>
    /// </summary>
    public static T? NearestTo<T>(IList<T> list, double timeMs, IList<BeatMarker> beats, out double distanceMs)
        where T : class, ITimedElement
    {
        var index = NearestIndexTo(list, timeMs, beats, out distanceMs);
        return index < 0 ? null : list[index];
    }

    /// <summary>Index of <see cref="NearestTo{T}" />'s element, or <c>-1</c>.</summary>
    public static int NearestIndexTo<T>(IList<T> list, double timeMs, IList<BeatMarker> beats,
        out double distanceMs)
        where T : ITimedElement
    {
        distanceMs = double.MaxValue;
        if (list.Count == 0) return -1;

        // The last element whose time is <= the target (clamped to 0 when every
        // element is after it — that clamp is why the candidate below is still
        // checked rather than assumed to be worse).
        var best = BinarySearchTimed(list, timeMs, beats);
        var bestTime = list[best].GetTimeMs(beats);
        while (best > 0 && list[best - 1].GetTimeMs(beats) >= bestTime) best--;
        distanceMs = Math.Abs(bestTime - timeMs);

        var after = FirstIndexAfter(list, timeMs, beats);
        if (after < 0) return best;

        var afterDistance = Math.Abs(list[after].GetTimeMs(beats) - timeMs);
        // Strictly closer, never equal: `after` is always the LATER index, and a
        // tie has to keep the earlier one.
        if (afterDistance >= distanceMs) return best;

        distanceMs = afterDistance;
        return after;
    }
}
