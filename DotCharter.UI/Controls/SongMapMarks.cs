using Avalonia;

namespace DotCharter.UI.Controls;

/// <summary>One mark on the song map: where it is drawn and the sound it stands for.</summary>
public readonly record struct SongMapMark(Rect Bounds, object Sound);

/// <summary>
///     Where the song map (<see cref="MinimapOverviewControl" />) draws a chord,
/// a hand shape or an anchor as a USE MARK — the ONE path for every set it marks: the
///     chord panel's bulk-edit preview (<c>ChartEditorViewModel.UseHighlight</c>,
///     in the EditTarget colour) and the selected chords and hand shapes (white),
/// which is what the Chord shapes list's "select uses" produces. A
///     chord is a tick rising from the bottom edge, a hand shape a bar along it
///     spanning its length, so the two read apart at any zoom. Pure: the control
///     draws exactly what <see cref="Collect" /> answers.
/// </summary>
public static class SongMapMarks
{
    /// <summary>Height of a chord's tick, from the bottom edge.</summary>
    public const double TickHeight = 8;

    public const double TickWidth = 2;

    /// <summary>Height of a hand shape's bar along the bottom edge.</summary>
    public const double BarHeight = 3;

    /// <summary>Narrowest bar: a short hand shape on a long song is still a mark.</summary>
    public const double MinBarWidth = 2;

    /// <summary>Fills <paramref name="into" /> (cleared first) with the mark of each
    /// of <paramref name="chords" /> and <paramref name="handShapes" />, in that
    /// order. Indexed loops and a caller-owned list: no allocation per frame.</summary>
    public static void Collect(IReadOnlyList<Chord> chords, IReadOnlyList<HandShape> handShapes,
        IList<BeatMarker> beats, double scale, double height, List<SongMapMark> into)
    {
        into.Clear();
        for (var i = 0; i < chords.Count; i++)
        {
            var chord = chords[i];
            var x = chord.Position.GetPositionMs(beats) * scale;
            into.Add(new SongMapMark(new Rect(x - TickWidth / 2, height - TickHeight, TickWidth, TickHeight), chord));
        }

        for (var i = 0; i < handShapes.Count; i++)
        {
            var handShape = handShapes[i];
            var x = handShape.Position.GetPositionMs(beats) * scale;
            var w = Math.Max(MinBarWidth, handShape.EndPosition.GetPositionMs(beats) * scale - x);
            into.Add(new SongMapMark(new Rect(x, height - BarHeight, w, BarHeight), handShape));
        }
    }

    /// <summary>Appends to <paramref name="into" /> the mark of each of
    /// <paramref name="anchors" />: a tick hanging from the TOP edge, so a hand position
    /// reads apart from the chords rising from the bottom.
    /// Indexed; no allocation per frame.</summary>
    public static void AppendAnchors(IReadOnlyList<FretAnchor> anchors, IList<BeatMarker> beats, double scale,
        List<SongMapMark> into)
    {
        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            var x = anchor.Position.GetPositionMs(beats) * scale;
            into.Add(new SongMapMark(new Rect(x - TickWidth / 2, 0, TickWidth, TickHeight), anchor));
        }
    }

    /// <summary>Appends to <paramref name="into" /> the mark of each section or phrase marker in
    /// <paramref name="events" />: a tall tick hanging from the TOP edge (twice an anchor's), so a
    /// structure change reads apart from a hand position (the AI chat's sections &amp; phrases
    /// review). Called only while such a review is open.</summary>
    public static void AppendEvents(IEnumerable<EventPoint> events, IList<BeatMarker> beats, double scale,
        List<SongMapMark> into)
    {
        foreach (var e in events)
        {
            var x = e.Position.GetPositionMs(beats) * scale;
            into.Add(new SongMapMark(new Rect(x - TickWidth / 2, 0, TickWidth, TickHeight * 2), e));
        }
    }

    /// <summary>Fills <paramref name="into" /> (cleared first) with the BAND of each suggested hand
    /// shape in <paramref name="bars" /> — what <c>HandShapeSuggestionLatch</c> answers for the
    /// level on screen: its span, from <paramref name="top" /> (under the section band) down to the
    /// bottom edge, so an open suggestion reads as a faint region of the song rather than as one more
    /// tick. Indexed; no allocation per frame.</summary>
    public static void CollectSuggestions(IList<HandShape> bars, IList<BeatMarker> beats, double scale,
        double top, double height, List<SongMapMark> into)
    {
        into.Clear();
        var bandHeight = Math.Max(0, height - top);
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var x = bar.Position.GetPositionMs(beats) * scale;
            var w = Math.Max(MinBarWidth, bar.EndPosition.GetPositionMs(beats) * scale - x);
            into.Add(new SongMapMark(new Rect(x, top, w, bandHeight), bar));
        }
    }

    /// <summary>How close (in map pixels) a click must land to a mark to seek to
    /// the mark itself rather than to the clicked point.</summary>
    public const double SnapPx = 4;
}
