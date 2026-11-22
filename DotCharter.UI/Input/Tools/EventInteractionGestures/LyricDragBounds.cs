namespace DotCharter.UI.Input.Tools.EventInteractionGestures;

/// <summary>Finds the chronological neighbours a lyric move or resize must respect.</summary>
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
        IList<LyricPhrase> phrases, LyricEvent target) => FindNeighbors(phrases, target, out _, out _);

    internal static (LyricEvent? Prev, LyricEvent? Next) FindNeighbors(
        IList<LyricPhrase> phrases, LyricEvent target, out bool previousInPhrase, out bool nextInPhrase)
    {
        previousInPhrase = false;
        nextInPhrase = false;
        if (phrases == null) throw new ArgumentNullException(nameof(phrases));

        for (var p = 0; p < phrases.Count; p++)
        {
            var lyrics = phrases[p].Lyrics;
            for (var i = 0; i < lyrics.Count; i++)
            {
                if (!ReferenceEquals(lyrics[i], target)) continue;

                previousInPhrase = i > 0 && !lyrics[i - 1].IsPhraseEnd;
                nextInPhrase = i + 1 < lyrics.Count && !target.IsPhraseEnd;
                var prev = i > 0 ? lyrics[i - 1] : LastLyricBefore(phrases, p);
                var next = i + 1 < lyrics.Count ? lyrics[i + 1] : FirstLyricAfter(phrases, p);
                return (prev, next);
            }
        }

        return (null, null);
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
