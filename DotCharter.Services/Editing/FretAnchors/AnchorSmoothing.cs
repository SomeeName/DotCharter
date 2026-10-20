namespace DotCharter.Services.Editing.FretAnchors;

/// <summary>One anchor the walk decided on: its fret and its width.</summary>
internal readonly record struct AnchorHint(int AnchorFret, int Width);

/// <summary>
///     The struck positions of one level, in order, as the anchor walk indexed
///     them — what <see cref="AnchorSmoothing" /> needs to judge whether a span of
///     sounds can be played from another anchor.
/// </summary>
internal interface IStruckSounds
{
    int Count { get; }

    FractionalPosition PositionAt(int index);

    /// <summary>Lowest fretted fret played at <paramref name="index" />.</summary>
    int MinFretAt(int index);

    /// <summary>Highest fretted fret played at <paramref name="index" />.</summary>
    int MaxFretAt(int index);

    /// <summary>The fret an AUTHORED grip pins the index finger to here, or null.</summary>
    int? HardRootAt(int index);

    /// <summary>True when every sound at <paramref name="index" /> can be played
    /// with the hand at <paramref name="fret" /> spanning <paramref name="width" />.</summary>
    bool PlayableFrom(int index, int fret, int width);

    /// <summary>First index whose position is at or after <paramref name="position" />.</summary>
    int LowerBound(FractionalPosition position);
}

/// <summary>
/// The two post-passes that run over the walk's anchors. Both only ever
///     REMOVE an anchor change or MOVE one earlier; neither invents a position the
///     walk did not reach, and neither touches a slide landing (the slide moves the
///     hand there, which is a fact, not a choice).
///     <para>
///         Measured on the owner's finished-chart corpus (tools/chart-eval, 86 songs,
///         train split, suggested fingers) one lever at a time — see the anchors
///         guide for the whole table.
///     </para>
/// </summary>
internal static class AnchorSmoothing
{
    /// <summary>
    ///     Longest middle span (struck positions) a bounce may cover and still
    ///     collapse. Flat from 4 to 12 on guitar; 8 is the bass peak on BOTH splits
    ///     (6: 82.8 / 80.3, 8: 83.2 / 81.0, 10: 83.0 / 80.8 % train / holdout).
    /// </summary>
    internal const int BounceSpan = 8;

    /// <summary>
    ///     How many struck positions an up-shift may be pulled back (bass). 2, 4 and
    ///     8 all score within 0.1 pp: almost every run it finds is one or two notes.
    /// </summary>
    internal const int PullBackRun = 2;

    // The four-fret box a pulled-back anchor must cover (EOF's flat tolerance,
    // the walk's BaseAnchorWidth).
    private const int HandWidth = 4;

    /// <summary>
    ///     A -> B -> A, where everything played under B is already playable from A:
    ///     the hand never left A, so B and the return to A are both dropped. The
    /// issue's third pattern — a double stop `xx(12)(12)xx` between two
    ///     `xx(10)(10)xx` played by the ring finger from FHP 10, not by moving the
    ///     index up and back on every strum.
    /// </summary>
    public static void CollapseBounces(
        IDictionary<FractionalPosition, AnchorHint> hints,
        ISet<FractionalPosition> pinned,
        IStruckSounds struck)
    {
        var keys = hints.Keys.OrderBy(k => k).ToList();
        for (var k = 0; k + 2 < keys.Count; k++)
        {
            var a = hints[keys[k]];
            var b = hints[keys[k + 1]];
            var c = hints[keys[k + 2]];
            if (c.AnchorFret != a.AnchorFret || b.AnchorFret == a.AnchorFret) continue;
            if (pinned.Contains(keys[k + 1]) || pinned.Contains(keys[k + 2])) continue;
            if (!SpanPlayableFrom(struck, keys[k + 1], keys[k + 2], a.AnchorFret, a.Width, BounceSpan)) continue;

            hints.Remove(keys[k + 1]);
            if (c.Width == a.Width)
            {
                hints.Remove(keys[k + 2]);
                keys.RemoveAt(k + 2);
            }

            keys.RemoveAt(k + 1);
            // A removal can line up a new A -> B -> A one step back.
            k = Math.Max(-1, k - 2);
        }
    }

    /// <summary>
    ///     An UP-shift the walk was forced into at B is set EARLIER, at the start of
    ///     the run of sounds just before B that already play above the old anchor and
    ///     fit, together with B's own span, under one hand — a bassist playing
    ///     3 | 5 5 6 7 sets the hand at 5 for the whole climb instead of letting
    ///     fret 3 reach for 5 and 6 and only moving when 7 forces it. The new anchor
    ///     is the lowest fret of that run and B's span. BASS ONLY: on guitar it
    ///     measured -0.5 / -0.4 pp (train / holdout).
    ///     <para>
    ///         The run stops at a position in <paramref name="stops" /> — a bass
    /// octave figure's octave note: pulling the anchor back onto it
    ///         would leave the root under one hand position and its octave under
    ///         another, which is exactly what the figure's root anchor prevents.
    ///     </para>
    /// </summary>
    /// <returns>The positions whose anchor moved, old → new.</returns>
    public static List<(FractionalPosition From, FractionalPosition To)> PullBackUpshifts(
        IDictionary<FractionalPosition, AnchorHint> hints,
        ISet<FractionalPosition> pinned,
        IStruckSounds struck,
        ISet<FractionalPosition>? stops = null)
    {
        var moved = new List<(FractionalPosition, FractionalPosition)>();
        var keys = hints.Keys.OrderBy(k => k).ToList();
        for (var k = 1; k < keys.Count; k++)
        {
            var a = hints[keys[k - 1]];
            var bKey = keys[k];
            var b = hints[bKey];
            if (b.AnchorFret <= a.AnchorFret || pinned.Contains(bKey)) continue;
            FractionalPosition? bEnd = k + 1 < keys.Count ? keys[k + 1] : null;

            var j = struck.LowerBound(bKey);
            var spanLo = int.MaxValue;
            var spanHi = int.MinValue;
            for (var i = j; i < struck.Count && (bEnd is null || struck.PositionAt(i) < bEnd.Value); i++)
            {
                spanLo = Math.Min(spanLo, struck.MinFretAt(i));
                spanHi = Math.Max(spanHi, struck.MaxFretAt(i));
            }

            if (spanLo == int.MaxValue) continue;

            var start = j;
            var low = Math.Min(b.AnchorFret, spanLo);
            for (var i = j - 1; i >= 0 && j - i <= PullBackRun; i--)
            {
                // Never across A's own opening: that sound is why A is there.
                if (struck.PositionAt(i) <= keys[k - 1]) break;
                if (stops != null && stops.Contains(struck.PositionAt(i))) break;
                var minFret = struck.MinFretAt(i);
                if (minFret <= a.AnchorFret) break;
                var newLow = Math.Min(low, minFret);
                var high = Math.Max(spanHi, struck.MaxFretAt(i));
                if (high - newLow + 1 > HandWidth) break;
                if (struck.HardRootAt(i) is { } hard && hard != newLow) break;
                start = i;
                low = newLow;
                spanHi = high;
            }

            if (start == j) continue;
            if (!SpanPlayableFrom(struck, bKey, bEnd, low, b.Width, int.MaxValue)) continue;

            var newKey = struck.PositionAt(start);
            hints.Remove(bKey);
            hints[newKey] = new AnchorHint(low, b.Width);
            keys[k] = newKey;
            moved.Add((bKey, newKey));
        }

        return moved;
    }

    private static bool SpanPlayableFrom(
        IStruckSounds struck,
        FractionalPosition from,
        FractionalPosition? to,
        int fret,
        int width,
        int maxSpan)
    {
        var count = 0;
        for (var i = struck.LowerBound(from); i < struck.Count && (to is null || struck.PositionAt(i) < to.Value); i++)
        {
            if (++count > maxSpan) return false;
            if (!struck.PlayableFrom(i, fret, width)) return false;
        }

        return count > 0;
    }
}
