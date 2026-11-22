using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>Builds one undoable placeholder insertion, including the phrase span and end flags.</summary>
internal static class VocalInsertion
{
    internal static IChartAction Create(VocalTrack track, FractionalPosition start, FractionalPosition end)
    {
        var phrase = track.Phrases.FirstOrDefault(p => p.Lyrics.Any(l => l.Position == start))
            ?? track.Phrases.FirstOrDefault(p => p.Position <= start && p.EndPosition >= start);
        var lyric = new LyricEvent
        {
            Text = "+",
            IsWordPart = false,
            IsPhraseEnd = true,
            Position = start,
            EndPosition = end
        };

        if (phrase == null)
        {
            var newPhrase = new LyricPhrase { Position = start, EndPosition = end };
            newPhrase.Lyrics.Add(lyric);
            return new AddElementsAction<LyricPhrase>(track.Phrases, new[] { newPhrase }, p => p.Position);
        }

        // The syllable, boundary flag and span belong to the same history node.
        var actions = new List<IChartAction>
        {
            new AddElementsAction<LyricEvent>(phrase.Lyrics, new[] { lyric }, l => l.Position),
            new NormalizeLyricEndsAction(phrase)
        };
        if (phrase.EndPosition < end)
            actions.Add(new ResizeLyricPhraseAction(phrase, phrase.EndPosition, end));

        return new CompositeAction(actions);
    }
}
