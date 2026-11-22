using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

/// <summary>
/// Edits a vocal line boundary and its flags together, moving the existing syllables.
/// Undo restores the original phrase and lyric collections; redo reuses the split phrase.
/// A line cannot end mid-word, so a split after a word part also clears its word join
/// in the same step: a syllable carrying both would lose its line break in a Rocksmith file.
/// </summary>
public sealed class SetLyricPhraseEndAction : IChartAction, ILinkPolicyAction
{
    private readonly ObservableCollection<LyricPhrase> _phrases;
    private readonly LyricPhrase _phrase;
    private readonly LyricPhrase? _other;
    private readonly int _phraseIndex;
    private readonly int _lyricIndex;
    private readonly bool _split;
    private readonly FractionalPosition _oldStart;
    private readonly FractionalPosition _oldEnd;
    private readonly FractionalPosition _otherStart;
    private readonly FractionalPosition _otherEnd;
    private readonly LyricEvent[] _original;
    private readonly LyricEvent[] _following = [];
    private readonly (LyricEvent Lyric, bool IsPhraseEnd, bool IsWordPart)[] _flags;

    public SetLyricPhraseEndAction(ObservableCollection<LyricPhrase> phrases, LyricPhrase phrase,
        LyricEvent lyric, bool isPhraseEnd)
    {
        _phrases = phrases;
        _phrase = phrase;
        _phraseIndex = phrases.IndexOf(phrase);
        _lyricIndex = phrase.Lyrics.IndexOf(lyric);
        _split = isPhraseEnd;
        _oldStart = phrase.Position;
        _oldEnd = phrase.EndPosition;
        _original = phrase.Lyrics.ToArray();

        if (_phraseIndex >= 0 && _lyricIndex >= 0)
        {
            if (_split && _lyricIndex < _original.Length - 1)
            {
                _other = new LyricPhrase
                {
                    Position = _original[_lyricIndex + 1].Position,
                    EndPosition = _oldEnd
                };
                HasEffect = true;
            }
            else if (!_split && _lyricIndex == _original.Length - 1 && _phraseIndex + 1 < phrases.Count)
            {
                _other = phrases[_phraseIndex + 1];
                _following = _other.Lyrics.ToArray();
                HasEffect = true;
            }
        }

        _otherStart = _other?.Position ?? FractionalPosition.Zero;
        _otherEnd = _other?.EndPosition ?? FractionalPosition.Zero;
        _flags = _original.Concat(_following).Select(l => (Lyric: l, l.IsPhraseEnd, l.IsWordPart)).ToArray();
    }

    /// <summary>Lyrics are outside the linked measure content.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public bool HasEffect { get; }

    public void Execute()
    {
        if (!HasEffect || _other == null) return;

        if (_split)
        {
            ReplaceLyrics(_phrase, _original.Take(_lyricIndex + 1));
            ReplaceLyrics(_other, _original.Skip(_lyricIndex + 1));
            FractionalPosition? leading = _oldStart < _original.Min(l => l.Position) ? _oldStart : null;
            FractionalPosition? trailing = _oldEnd > _original.Max(l => l.EndPosition) ? _oldEnd : null;
            SetBounds(_phrase, leading, null);
            SetBounds(_other, null, trailing);
            _phrases.Insert(_phraseIndex + 1, _other);
            NormalizeEnds(_other);
        }
        else
        {
            ReplaceLyrics(_phrase, _original.Concat(_following));
            _other.Lyrics.Clear();
            SetBounds(_phrase, FractionalPosition.Min(_oldStart, _otherStart),
                FractionalPosition.Max(_oldEnd, _otherEnd));
            _phrases.RemoveAt(_phraseIndex + 1);
        }

        NormalizeEnds(_phrase);
    }

    public void Undo()
    {
        if (!HasEffect || _other == null) return;

        ReplaceLyrics(_phrase, _original);
        ReplaceLyrics(_other, _following);
        _phrase.Position = _oldStart;
        _phrase.EndPosition = _oldEnd;
        _other.Position = _otherStart;
        _other.EndPosition = _otherEnd;
        if (_split) _phrases.RemoveAt(_phraseIndex + 1);
        else _phrases.Insert(_phraseIndex + 1, _other);
        foreach (var (lyric, isPhraseEnd, isWordPart) in _flags)
        {
            lyric.IsPhraseEnd = isPhraseEnd;
            lyric.IsWordPart = isWordPart;
        }
    }

    private static void SetBounds(LyricPhrase phrase, FractionalPosition? paddingStart, FractionalPosition? paddingEnd)
    {
        var start = phrase.Lyrics.Min(l => l.Position);
        var end = phrase.Lyrics.Max(l => l.EndPosition);
        phrase.Position = paddingStart.HasValue ? FractionalPosition.Min(paddingStart.Value, start) : start;
        phrase.EndPosition = paddingEnd.HasValue ? FractionalPosition.Max(paddingEnd.Value, end) : end;
    }

    private static void ReplaceLyrics(LyricPhrase phrase, IEnumerable<LyricEvent> lyrics)
    {
        phrase.Lyrics.Clear();
        foreach (var lyric in lyrics) phrase.Lyrics.Add(lyric);
    }

    private static void NormalizeEnds(LyricPhrase phrase)
    {
        for (var i = 0; i < phrase.Lyrics.Count; i++)
            LyricLineRule.Apply(phrase.Lyrics[i], phrase.Lyrics[i].IsWordPart, i == phrase.Lyrics.Count - 1);
    }
}
