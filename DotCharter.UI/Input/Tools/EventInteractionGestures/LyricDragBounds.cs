namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>
/// Pure clamp math for the live lyric-drag preview and the wheel resize —
/// the vocal twin of <see cref="BeatDragBounds"/>.
///
/// A lyric may never be dragged past its neighbouring syllable: the vocal
/// track's phrases (and each phrase's lyrics) are kept in ascending order
/// (<c>TimelineMath.GetVisibleRange</c> requires it), and no spacing pass
/// covers vocals on save, so the GESTURE has to be correct on its own —
/// unlike notes, where <c>ChartSpacingEnforcer</c> re-checks every commit.
/// The minimum gap mirrors note spacing (<c>ChartSpacingEnforcer.GetMinDistance</c>),
/// resolved to milliseconds by the caller at the neighbour it bounds against.
/// </summary>
public static class LyricDragBounds
{
    /// <summary>
    /// The syllables directly before and after <paramref name="target"/> in
    /// track order. A neighbour may live in the ADJACENT PHRASE — the last
    /// syllable of a phrase bounds against the next phrase's first — so this
    /// walks the flattened phrase list, not just the target's own phrase.
    /// Returns <c>(null, null)</c> when the target is not in the track.
    /// </summary>
    public static (LyricEvent? Prev, LyricEvent? Next) FindNeighbors(
        IList<LyricPhrase> phrases, LyricEvent target)
    {
        if (phrases == null) throw new ArgumentNullException(nameof(phrases));

        for (var p = 0; p < phrases.Count; p++)
        {
            var lyrics = phrases[p].Lyrics;
            for (var i = 0; i < lyrics.Count; i++)
            {
                if (!ReferenceEquals(lyrics[i], target)) continue;

                var prev = i > 0 ? lyrics[i - 1] : LastLyricBefore(phrases, p);
                var next = i + 1 < lyrics.Count ? lyrics[i + 1] : FirstLyricAfter(phrases, p);
                return (prev, next);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// The inclusive [lower, upper] window the dragged lyric's START may move
    /// to: at least <paramref name="gapAfterPrevMs"/> after the previous
    /// syllable's start, at most <paramref name="gapBeforeNextMs"/> before the
    /// next one's. ±∞ on a side with no neighbour. The window can come back
    /// INVERTED (upper &lt; lower) when the neighbours already sit closer than
    /// two gaps — the caller must then leave the lyric where it was rather
    /// than mint a new violation.
    /// </summary>
    public static (double Lower, double Upper) ComputeStartBounds(
        double? prevStartMs, double gapAfterPrevMs, double? nextStartMs, double gapBeforeNextMs)
    {
        if (gapAfterPrevMs < 0) throw new ArgumentOutOfRangeException(nameof(gapAfterPrevMs));
        if (gapBeforeNextMs < 0) throw new ArgumentOutOfRangeException(nameof(gapBeforeNextMs));

        var lower = prevStartMs is { } prev ? prev + gapAfterPrevMs : double.NegativeInfinity;
        var upper = nextStartMs is { } next ? next - gapBeforeNextMs : double.PositiveInfinity;
        return (lower, upper);
    }

    /// <summary>Clamps <paramref name="value"/> into the [lower, upper] window.</summary>
    public static double Clamp(double value, double lower, double upper)
    {
        if (value < lower) return lower;
        if (value > upper) return upper;
        return value;
    }

    /// <summary>
    /// The note-style minimum gap in milliseconds, evaluated at
    /// <paramref name="position"/> (the spacing rules are position-dependent in
    /// measure-fraction mode). Zero without a beat grid.
    /// </summary>
    public static double MinGapMsAt(FractionalPosition position, IList<BeatMarker> beats, AppConfig config)
    {
        if (beats.Count == 0) return 0.0;
        var gap = ChartSpacingEnforcer.GetMinDistance(position, beats, config);
        return (position + gap).GetPositionMs(beats) - position.GetPositionMs(beats);
    }

    private static LyricEvent? LastLyricBefore(IList<LyricPhrase> phrases, int phraseIndex)
    {
        for (var p = phraseIndex - 1; p >= 0; p--)
        {
            if (phrases[p].Lyrics.Count > 0)
                return phrases[p].Lyrics[^1];
        }

        return null;
    }

    private static LyricEvent? FirstLyricAfter(IList<LyricPhrase> phrases, int phraseIndex)
    {
        for (var p = phraseIndex + 1; p < phrases.Count; p++)
        {
            if (phrases[p].Lyrics.Count > 0)
                return phrases[p].Lyrics[0];
        }

        return null;
    }
}
