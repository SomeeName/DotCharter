using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Pure hit-testing for the top event ribbon. The exact-time tolerance
/// follows the same convention used by every event row: a small "back"
/// slack (10 px ÷ pixelsPerMs) plus a larger "forward" tolerance
/// (25 px ÷ pixelsPerMs) so events fire when clicked anywhere on or
/// just to the right of their drawn glyph.
/// </summary>
public static class EventInteractionHitTester
{
    private const double BackPixels = 10.0;
    private const double ForwardPixels = 25.0;
    private const double LyricSlackPixels = 8.0;

    public static EventPoint? HitTestEvent(IReadOnlyList<EventPoint> eventPoints, IList<BeatMarker> beats,
        EventRowType rowType, double exactTimeMs, double pixelsPerMs)
    {
        if (rowType is not EventRowType.Section and not EventRowType.Phrase) return null;
        if (pixelsPerMs <= 0) return null;

        var msTolerance = ForwardPixels / pixelsPerMs;
        var msBack = BackPixels / pixelsPerMs;

        for (var i = eventPoints.Count - 1; i >= 0; i--)
        {
            var ep = eventPoints[i];
            if (rowType == EventRowType.Section && string.IsNullOrEmpty(ep.Section)) continue;
            if (rowType == EventRowType.Phrase && string.IsNullOrEmpty(ep.Phrase)) continue;
            var delta = exactTimeMs - ep.Position.GetPositionMs(beats);
            if (delta >= -msBack && delta <= msTolerance) return ep;
        }
        return null;
    }

    public static ToneChange? HitTestTone(IReadOnlyList<ToneChange> tones, IList<BeatMarker> beats,
        EventRowType rowType, double exactTimeMs, double pixelsPerMs)
    {
        if (rowType != EventRowType.Tone) return null;
        if (pixelsPerMs <= 0) return null;

        var msTolerance = ForwardPixels / pixelsPerMs;
        var msBack = BackPixels / pixelsPerMs;

        for (var i = tones.Count - 1; i >= 0; i--)
        {
            var t = tones[i];
            var delta = exactTimeMs - t.Position.GetPositionMs(beats);
            if (delta >= -msBack && delta <= msTolerance) return t;
        }
        return null;
    }

    /// <summary>
    /// Lyric pills span [Position, EndPosition]. We walk newest-first so
    /// when two pills overlap we hit the one drawn on top. Returns the
    /// owning phrase too because lyric removal needs both.
    /// <para>
    /// The span is widened to the pill's DRAWN minimum
    /// (<see cref="LyricPillMetrics.MinPillWidthPx" />) before the slack is
    /// applied. Without that the click span was the syllable's duration alone,
    /// which at any normal editing zoom is a fraction of the chip the layer
    /// actually paints — a 60 ms syllable at 0.2 px/ms draws an 18 px pill over
    /// a 12 ms (2.4 px) target.
    /// </para>
    /// </summary>
    public static (LyricEvent? Lyric, LyricPhrase? Phrase) HitTestLyric(
        IReadOnlyList<LyricPhrase>? phrases, IList<BeatMarker> beats, double exactTimeMs, double pixelsPerMs)
    {
        if (phrases == null || beats.Count == 0 || pixelsPerMs <= 0) return (null, null);

        var slackMs = LyricSlackPixels / pixelsPerMs;
        var minPillMs = LyricPillMetrics.MinPillWidthPx / pixelsPerMs;
        var nextPhraseFirstMs = double.PositiveInfinity; // of the phrase after i, walking backwards
        for (var i = phrases.Count - 1; i >= 0; i--)
        {
            var phrase = phrases[i];
            for (var j = phrase.Lyrics.Count - 1; j >= 0; j--)
            {
                var ly = phrase.Lyrics[j];
                var startMs = ly.Position.GetPositionMs(beats);
                var endMs = ly.EndPosition.GetPositionMs(beats);
                if (endMs <= startMs) endMs = startMs + LyricPillMetrics.DefaultDurationMs;
                if (endMs - startMs < minPillMs) endMs = startMs + minPillMs;
                endMs = Math.Min(endMs, DrawnEndLimitMs(phrase, j, nextPhraseFirstMs, beats, pixelsPerMs));
                endMs = Math.Max(endMs, startMs + LyricPillLayout.MinDrawnWidthPx / pixelsPerMs);
                if (exactTimeMs >= startMs - slackMs && exactTimeMs <= endMs + slackMs)
                    return (ly, phrase);
            }

            if (phrase.Lyrics.Count > 0) nextPhraseFirstMs = phrase.Lyrics[0].Position.GetPositionMs(beats);
        }
        return (null, null);
    }

    /// <summary>
    ///     Where <c>EventLayer.DrawLyrics</c> stops syllable <paramref name="j" />'s pill: the next
    ///     syllable of the phrase, else the next phrase's region edge
    ///     (<see cref="LyricPillLayout.LastPillLimit" />), less <see cref="LyricPillLayout.GapPx" />
    ///     (<see cref="LyricPillLayout.Layout" />). Screen space is ms × px/ms, so the limits are
    ///     converted there and back rather than restated in ms.
    /// </summary>
    private static double DrawnEndLimitMs(LyricPhrase phrase, int j, double nextPhraseFirstMs,
        IList<BeatMarker> beats, double pixelsPerMs)
    {
        double limitPx;
        if (j + 1 < phrase.Lyrics.Count)
            limitPx = phrase.Lyrics[j + 1].Position.GetPositionMs(beats) * pixelsPerMs;
        else if (!double.IsPositiveInfinity(nextPhraseFirstMs))
            limitPx = LyricPillLayout.LastPillLimit((float)(nextPhraseFirstMs * pixelsPerMs));
        else
            return double.PositiveInfinity;

        return (limitPx - LyricPillLayout.GapPx) / pixelsPerMs;
    }
}
