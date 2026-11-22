namespace DotCharter.UI.ViewModels.Editor;

/// <summary>Resolves a queued line against current ownership without changing the chart or session.</summary>
internal readonly record struct LyricTapTarget(LyricPhrase? Phrase, LyricEvent? Trim)
{
    internal static LyricTapTarget Resolve(IEnumerable<LyricPhrase> phrases, IEnumerable<LyricEvent> newestFirst,
        FractionalPosition start)
    {
        var current = phrases.ToArray();
        var owner = newestFirst.Select(anchor => current.FirstOrDefault(p => p.Lyrics.Contains(anchor)))
            .FirstOrDefault(p => p != null);
        var previous = owner?.Lyrics.Where(l => l.Position < start).MaxBy(l => l.Position);
        return new LyricTapTarget(owner, previous != null && previous.EndPosition > start ? previous : null);
    }
}
