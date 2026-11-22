namespace DotCharter.Services.Editing.Lyrics;

/// <summary>The note-style spacing shared by vocal import, sync and timing edits.</summary>
public static class LyricPhraseSpacing
{
    public static FractionalPosition EndLimit(FractionalPosition start, FractionalPosition next,
        IList<BeatMarker> beats, AppConfig config) =>
        next - ChartSpacingEnforcer.GetMinDistance(start, beats, config);

    public static FractionalPosition MinimumLength(int gridDivisor) => new(new Fraction(1, Math.Max(gridDivisor, 16)));

    /// <summary>
    /// Finds the ends to repair without mutating the track. If starts are already closer than the
    /// configured gap, shortening to zero is the closest possible result with fixed starts.
    /// </summary>
    public static IEnumerable<(LyricEvent Lyric, FractionalPosition End)> InPhraseEnds(
        IEnumerable<LyricPhrase> phrases, IList<BeatMarker> beats, AppConfig config, bool? spanWordGaps = null)
    {
        var spanWords = spanWordGaps ?? config.LyricSpanWordGap;
        foreach (var phrase in phrases)
        {
            for (var i = 0; i + 1 < phrase.Lyrics.Count; i++)
            {
                var lyric = phrase.Lyrics[i];
                if (lyric.IsPhraseEnd) continue;
                var limit = EndLimit(lyric.Position, phrase.Lyrics[i + 1].Position, beats, config);
                var end = spanWords || lyric.IsWordPart ? limit : FractionalPosition.Min(lyric.EndPosition, limit);
                yield return (lyric, FractionalPosition.Max(lyric.Position, end));
            }
        }
    }

    /// <summary>Repairs ends on newly created or imported phrases before they enter history.</summary>
    public static void CloseGaps(IEnumerable<LyricPhrase> phrases, IList<BeatMarker> beats, AppConfig config,
        bool? spanWordGaps = null)
    {
        foreach (var (lyric, end) in InPhraseEnds(phrases, beats, config, spanWordGaps))
            lyric.EndPosition = end;
    }
}
