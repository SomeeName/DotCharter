namespace DotCharter.Services.Audio.Lyrics;

/// <summary>
///     The LAST step of <see cref="LyricAutoSyncService.BuildPhrases" /> that moves a syllable
///     time, so nothing after it can collapse one again: the fold onto the 1/192-beat grid can
///     put two close onsets on one tick, and <see cref="Editing.Lyrics.LyricPhraseSpacing.CloseGaps" />
///     shortens an end to its own start when the next onset is closer than the spacing gap.
///     <see cref="VocalTiming.EnforceMinimum" /> (the export's own rule and minimum) decides the
///     times in milliseconds; only the syllables it changed are written back, each start on the
///     first tick at or after its time and above the previous start, each end on the first tick
///     that keeps the minimum, never past the next start. Every other syllable keeps its exact
///     position, and a phrase's bounds follow its first and last syllable.
/// </summary>
internal static class LyricMinimumTiming
{
    /// <summary>One 1/192 beat: the grid auto-sync places syllables on.</summary>
    private static readonly FractionalPosition GridTick = new(new Fraction(1, 192));

    public static void Apply(List<LyricPhrase> phrases, IList<BeatMarker> beats)
    {
        var lyrics = phrases.SelectMany(p => p.Lyrics).ToList();
        var count = lyrics.Count;
        if (count == 0) return;

        var starts = new double[count];
        var lengths = new double[count];
        for (var i = 0; i < count; i++)
        {
            starts[i] = lyrics[i].Position.GetPositionMs(beats);
            lengths[i] = lyrics[i].EndPosition.GetPositionMs(beats) - starts[i];
        }

        var oldStarts = (double[])starts.Clone();
        var oldLengths = (double[])lengths.Clone();
        VocalTiming.EnforceMinimum(starts, lengths);

        var moved = new bool[count];
        var resized = new bool[count];
        for (var i = 0; i < count; i++)
        {
            moved[i] = starts[i] != oldStarts[i];
            resized[i] = moved[i] || lengths[i] != oldLengths[i];
        }

        if (!resized.Any(r => r)) return;

        var bounds = phrases.Select(PhraseBounds).ToList();
        PlaceStarts(lyrics, starts, moved, beats);
        PlaceEnds(lyrics, lengths, moved, resized, beats);
        for (var p = 0; p < phrases.Count; p++) FollowLyrics(phrases[p], bounds[p]);
    }

    private static void PlaceStarts(List<LyricEvent> lyrics, double[] starts, bool[] moved, IList<BeatMarker> beats)
    {
        // The first syllable never moves: only a later member of a shared start does.
        for (var i = 1; i < lyrics.Count; i++)
        {
            if (!moved[i]) continue;
            var start = AtOrAfter(starts[i], beats);
            if (i + 1 < lyrics.Count && !moved[i + 1] && start >= lyrics[i + 1].Position)
                start = TimelineMath.PositionFromMs(starts[i], beats);
            var previous = lyrics[i - 1].Position;
            lyrics[i].Position = start > previous ? start : previous + GridTick;
        }
    }

    private static void PlaceEnds(
        List<LyricEvent> lyrics, double[] lengths, bool[] moved, bool[] resized, IList<BeatMarker> beats)
    {
        for (var i = 0; i < lyrics.Count; i++)
        {
            var lyric = lyrics[i];
            var hasNext = i + 1 < lyrics.Count;
            var nextMoved = hasNext && moved[i + 1];
            if (!resized[i] && !nextMoved) continue;

            var end = resized[i]
                ? AtOrAfter(lyric.Position.GetPositionMs(beats) + lengths[i], beats)
                : lyric.EndPosition;
            if (hasNext && end > lyrics[i + 1].Position) end = lyrics[i + 1].Position;
            lyric.EndPosition = end > lyric.Position ? end : lyric.Position + GridTick;
        }
    }

    private static (FractionalPosition Start, FractionalPosition End, bool StartsAtFirst, bool EndsAtLast) PhraseBounds(
        LyricPhrase phrase) =>
        phrase.Lyrics.Count == 0
            ? (phrase.Position, phrase.EndPosition, false, false)
            : (phrase.Position, phrase.EndPosition,
                phrase.Position == phrase.Lyrics[0].Position, phrase.EndPosition == phrase.Lyrics[^1].EndPosition);

    private static void FollowLyrics(
        LyricPhrase phrase, (FractionalPosition Start, FractionalPosition End, bool StartsAtFirst, bool EndsAtLast) bounds)
    {
        if (bounds.StartsAtFirst) phrase.Position = phrase.Lyrics[0].Position;
        if (bounds.EndsAtLast) phrase.EndPosition = phrase.Lyrics[^1].EndPosition;
    }

    /// <summary>The first grid tick at or after <paramref name="ms" />.</summary>
    private static FractionalPosition AtOrAfter(double ms, IList<BeatMarker> beats)
    {
        var position = TimelineMath.PositionFromMs(ms, beats);
        return position.GetPositionMs(beats) < ms - 1e-6 ? position + GridTick : position;
    }
}
