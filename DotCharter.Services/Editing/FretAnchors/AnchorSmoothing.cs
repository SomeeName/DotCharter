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

    /// <summary>
    ///     True when <paramref name="index" /> is a one-fret grip (every fretted
    ///     sound on the same fret, two or more strings: the drop-tuned power chord)
    ///     and it lies inside the box at <paramref name="fret" /> spanning <paramref name="width" />.
    /// </summary>
    bool OneFretGripWithin(int index, int fret, int width);

    /// <summary>True when <paramref name="other" /> frets the same strings as
    ///     <paramref name="index" /> in the same shape, moved along the neck when
    ///     it is a grip of two or more strings (a single note must match its fret).</summary>
    bool SameShapeAt(int index, int other);

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

    // How far above a lower anchor a folded higher one may sit: the ring finger.
    private const int RingReach = 2;

    // Most struck positions a folded higher anchor may hold: a passing reach of a
    // beat or so. A chromatic walk down (4 for two beats, 3, 2) is three real
    // positions, and the owner charts each one.
    private const int FoldSpan = 2;

    /// <summary>
    ///     A -> B -> A, where everything played under B is already playable from A:
    ///     the hand never left A, so B and the return to A are both dropped. The
    /// issue's third pattern — a double stop `xx(12)(12)xx` between two
    ///     `xx(10)(10)xx` played by the ring finger from FHP 10, not by moving the
    ///     index up and back on every strum.
    ///     <para>
    ///         On a seven-string guitar a one-fret grip under B also counts as playable
    ///         from A, even with an index barre: the owner kept one anchor over a
    ///         B5 / C5 riff on the two low strings. Neutral on the six-string corpus,
    ///         where the same lever cost 0.4 pp guitar FHP on the holdout split.
    ///     </para>
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
            if (!SpanPlayableFrom(struck, keys[k + 1], keys[k + 2], a.AnchorFret, a.Width, BounceSpan, oneFretGrips: true)) continue;

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

    /// <summary>
    ///     A HIGHER anchor A followed straight by a LOWER anchor B, where everything
    ///     played under A already fits B's box: the hand moves to B once, where A
    ///     started, instead of reaching up to A and coming back down. A seven-string
    ///     riff with a fret-9 power chord on beat 4 and fret 7 on the next downbeat
    ///     (9 sits inside 7's box, 7-10) keeps one anchor at 7 from beat 4.
    ///     When the anchor before A already is B, the moved B is dropped too.
    ///     <para>
    ///         Only when A holds at most two struck positions (a passing reach, not a
    ///         position of its own), sits within the ring finger's reach of B (two frets) and a
    ///         shape played under A recurs under B, as itself or slid down (the
    ///         riff rocks 7-9-7, or moves one power-chord shape 9 then 7): then the
    ///         hand never left B. A drop of three frets, or onto a different shape,
    ///         keeps both anchors, as the owner charted it. Seven-string guitar only:
    ///         ungated, the fold cost 0.03 pp guitar FHP on the holdout split.
    ///     </para>
    /// </summary>
    /// <returns>The positions whose anchor moved, old to new.</returns>
    public static List<(FractionalPosition From, FractionalPosition To)> FoldHigherIntoLower(
        IDictionary<FractionalPosition, AnchorHint> hints,
        ISet<FractionalPosition> pinned,
        IStruckSounds struck)
    {
        var moved = new List<(FractionalPosition, FractionalPosition)>();
        var keys = hints.Keys.OrderBy(k => k).ToList();
        for (var k = 0; k + 1 < keys.Count; k++)
        {
            var aKey = keys[k];
            var bKey = keys[k + 1];
            var a = hints[aKey];
            var b = hints[bKey];
            if (b.AnchorFret >= a.AnchorFret || pinned.Contains(aKey) || pinned.Contains(bKey)) continue;
            if (!SpanPlayableFrom(struck, aKey, bKey, b.AnchorFret, b.Width, FoldSpan, oneFretGrips: true)) continue;
            if (a.AnchorFret - b.AnchorFret > RingReach) continue;
            if (!ShapeRecurs(struck, aKey, bKey, k + 2 < keys.Count ? keys[k + 2] : null)) continue;

            hints.Remove(bKey);
            keys.RemoveAt(k + 1);
            if (k > 0 && hints[keys[k - 1]] == b)
            {
                hints.Remove(aKey);
                keys.RemoveAt(k);
            }
            else
            {
                hints[aKey] = b;
            }

            moved.Add((bKey, aKey));
            k = Math.Max(-1, k - 2);
        }

        return moved;
    }

    // True when a shape struck in [from, mid) is struck again in [mid, to), as
    // itself or slid, within BounceSpan positions of mid.
    private static bool ShapeRecurs(
        IStruckSounds struck, FractionalPosition from, FractionalPosition mid, FractionalPosition? to)
    {
        var first = struck.LowerBound(from);
        var split = struck.LowerBound(mid);
        for (var j = split; j < struck.Count && j - split < BounceSpan && (to is null || struck.PositionAt(j) < to.Value); j++)
        {
            for (var i = first; i < split; i++)
            {
                if (struck.SameShapeAt(i, j)) return true;
            }
        }

        return false;
    }

    private static bool SpanPlayableFrom(
        IStruckSounds struck,
        FractionalPosition from,
        FractionalPosition? to,
        int fret,
        int width,
        int maxSpan,
        bool oneFretGrips = false)
    {
        var count = 0;
        for (var i = struck.LowerBound(from); i < struck.Count && (to is null || struck.PositionAt(i) < to.Value); i++)
        {
            if (++count > maxSpan) return false;
            if (!struck.PlayableFrom(i, fret, width) && !(oneFretGrips && struck.OneFretGripWithin(i, fret, width))) return false;
        }

        return count > 0;
    }
}
