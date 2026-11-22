namespace DotCharter.Core.Domain.Vocals;

/// <summary>Selects the next doubtful syllable in chart order, wrapping at either end.</summary>
public static class LyricReviewNavigation
{
    public static LyricEvent? Find(IReadOnlyList<LyricEvent> lyrics, LyricEvent? selected, int direction)
    {
        var current = -1;
        for (var i = 0; i < lyrics.Count; i++)
            if (ReferenceEquals(lyrics[i], selected)) current = i;
        if (current < 0) current = direction < 0 ? 0 : -1;
        var step = direction < 0 ? -1 : 1;
        for (var i = 1; i <= lyrics.Count; i++)
        {
            var index = (current + step * i + lyrics.Count) % lyrics.Count;
            if (lyrics[index].ReviewReasons != LyricReviewReason.None) return lyrics[index];
        }

        return null;
    }
}
