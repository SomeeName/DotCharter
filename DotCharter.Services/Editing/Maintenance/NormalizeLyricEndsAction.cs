using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>Normalizes an inserted line from its actual collection order; undo restores its original flags.</summary>
internal sealed class NormalizeLyricEndsAction : IChartAction, ILinkPolicyAction
{
    private readonly LyricPhrase _phrase;
    private readonly (LyricEvent Lyric, bool IsPhraseEnd, bool IsWordPart)[] _ends;

    public NormalizeLyricEndsAction(LyricPhrase phrase)
    {
        _phrase = phrase;
        _ends = phrase.Lyrics.Select(l => (Lyric: l, l.IsPhraseEnd, l.IsWordPart)).ToArray();
    }

    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public void Execute()
    {
        for (var i = 0; i < _phrase.Lyrics.Count; i++)
            LyricLineRule.Apply(_phrase.Lyrics[i], _phrase.Lyrics[i].IsWordPart, i == _phrase.Lyrics.Count - 1);
    }

    public void Undo()
    {
        foreach (var (lyric, isPhraseEnd, isWordPart) in _ends)
        {
            lyric.IsPhraseEnd = isPhraseEnd;
            lyric.IsWordPart = isWordPart;
        }
    }
}
