using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

/// <summary>
/// Repositions a single <see cref="LyricEvent"/> in place. Used by the
/// event-ribbon drag tool when the user grabs a lyric pill and slides it
/// horizontally; undo restores both the start and end positions so the
/// original sustain length comes back exactly.
/// </summary>
public class MoveLyricAction : IChartAction
{
    private readonly LyricEvent _lyric;
    private readonly FractionalPosition _oldPos;
    private readonly FractionalPosition _oldEnd;
    private readonly FractionalPosition _newPos;
    private readonly FractionalPosition _newEnd;

    public MoveLyricAction(LyricEvent lyric, FractionalPosition oldPos, FractionalPosition oldEnd,
        FractionalPosition newPos, FractionalPosition newEnd)
    {
        _lyric = lyric;
        _oldPos = oldPos;
        _oldEnd = oldEnd;
        _newPos = newPos;
        _newEnd = newEnd;
    }

    public bool HasEffect => !_oldPos.Equals(_newPos) || !_oldEnd.Equals(_newEnd);

    public void Execute()
    {
        _lyric.Position = _newPos;
        _lyric.EndPosition = _newEnd;
    }

    public void Undo()
    {
        _lyric.Position = _oldPos;
        _lyric.EndPosition = _oldEnd;
    }
}

/// <summary>
/// Adjusts only the end position (sustain) of a lyric. Used by the
/// scroll-wheel handler so the user can stretch a syllable by hovering
/// over it and rolling the wheel.
/// <para>
/// <b>Mergeable.</b> A wheel-driven action that is not <see cref="IMergeableAction"/> at all
/// would push one history node
/// per wheel TICK, which is exactly the pattern the merge machinery exists to
/// kill (a real session's 111-node hand-shape resize). A syllable is
/// stretched by rolling the wheel over it, so the tick count is the user's wrist,
/// not their intent.
/// </para>
/// </summary>
public class ResizeLyricAction : IChartAction, IMergeableAction
{
    private readonly LyricEvent _lyric;
    private readonly FractionalPosition _oldEnd;
    private FractionalPosition _newEnd;

    /// <summary>
    ///     An empty <c>mergeVerb</c> (the default) means "never merge" — the same
    ///     opt-in-per-call-site rule every other mergeable action follows, so a
    ///     caller that pushes a single deliberate resize still gets its own node.
    /// </summary>
    public ResizeLyricAction(LyricEvent lyric, FractionalPosition oldEnd, FractionalPosition newEnd,
        string mergeVerb = "")
    {
        _lyric = lyric;
        _oldEnd = oldEnd;
        _newEnd = newEnd;
        MergeVerb = mergeVerb;
    }

    public string MergeVerb { get; }

    public bool HasEffect => !_oldEnd.Equals(_newEnd);
    public void Execute() => _lyric.EndPosition = _newEnd;
    public void Undo() => _lyric.EndPosition = _oldEnd;

    /// <summary>Same gesture, same syllable — compared by REFERENCE, because two
    /// equal-looking lyrics at one position can be different objects (rule (c)).</summary>
    public bool CanAbsorb(IChartAction next) =>
        next is ResizeLyricAction other && ReferenceEquals(_lyric, other._lyric);

    /// <summary>Our <c>_oldEnd</c> stays — it is the run's true starting point —
    /// and only the destination advances.</summary>
    public void Absorb(IChartAction next) => _newEnd = ((ResizeLyricAction)next)._newEnd;
}

/// <summary>
/// Adjusts only the end position of a <see cref="LyricPhrase"/>.
/// <para>
/// A phrase end is chart state like any other, so the gesture that stretches one
/// has to carry it in ITS undo node — <b>a gesture is one node</b>.
/// <c>ChartMaintenanceService.InsertVocalAtPlayhead</c> put the placeholder lyric
/// in through an <see cref="AddElementsAction{T}"/> and then assigned
/// <c>EndPosition</c> as a bare statement beside it, so Ctrl+Z took the
/// placeholder back out and left the phrase permanently longer. Nothing on screen
/// shows a phrase's end moving by a fraction of a beat, so repeating the
/// insert/undo crept the phrase forward invisibly, one gesture at a time.
/// </para>
/// <para>
/// Deliberately NOT <see cref="IMergeableAction"/>: unlike
/// <see cref="ResizeLyricAction"/> this is not a wheel-driven run, and it is
/// meant to be composed into the node of whatever gesture forced the stretch
/// rather than to own one.
/// </para>
/// </summary>
public class ResizeLyricPhraseAction : IChartAction
{
    private readonly LyricPhrase _phrase;
    private readonly FractionalPosition _oldEnd;
    private readonly FractionalPosition _newEnd;

    public ResizeLyricPhraseAction(LyricPhrase phrase, FractionalPosition oldEnd, FractionalPosition newEnd)
    {
        _phrase = phrase;
        _oldEnd = oldEnd;
        _newEnd = newEnd;
    }

    public bool HasEffect => !_oldEnd.Equals(_newEnd);
    public void Execute() => _phrase.EndPosition = _newEnd;
    public void Undo() => _phrase.EndPosition = _oldEnd;
}

/// <summary>
/// Removes a single lyric from its phrase. If the phrase is left empty
/// it is also removed from the track so the inspector ribbon doesn't
/// keep an empty gap. Undo re-inserts the lyric at its original index
/// and restores the phrase reference if it had been dropped.
/// </summary>
public class RemoveLyricAction : IChartAction
{
    private readonly ObservableCollection<LyricPhrase> _trackPhrases;
    private readonly LyricPhrase _phrase;
    private readonly LyricEvent _lyric;
    private int _lyricIndex;
    private int _phraseIndex = -1;
    private bool _droppedPhrase;

    public RemoveLyricAction(ObservableCollection<LyricPhrase> trackPhrases, LyricPhrase phrase, LyricEvent lyric)
    {
        _trackPhrases = trackPhrases;
        _phrase = phrase;
        _lyric = lyric;
    }

    public bool HasEffect => true;

    public void Execute()
    {
        _lyricIndex = _phrase.Lyrics.IndexOf(_lyric);
        if (_lyricIndex < 0) return;
        _phrase.Lyrics.RemoveAt(_lyricIndex);
        if (_phrase.Lyrics.Count == 0)
        {
            _phraseIndex = _trackPhrases.IndexOf(_phrase);
            if (_phraseIndex >= 0)
            {
                _trackPhrases.RemoveAt(_phraseIndex);
                _droppedPhrase = true;
            }
        }
    }

    public void Undo()
    {
        if (_droppedPhrase && _phraseIndex >= 0)
        {
            var insertAt = Math.Min(_phraseIndex, _trackPhrases.Count);
            _trackPhrases.Insert(insertAt, _phrase);
            _droppedPhrase = false;
        }
        if (_lyricIndex >= 0)
        {
            var insertAt = Math.Min(_lyricIndex, _phrase.Lyrics.Count);
            _phrase.Lyrics.Insert(insertAt, _lyric);
        }
    }
}

/// <summary>
/// Sets the pitch (MIDI note, or the <c>255</c> "no pitch" sentinel) of a
/// single lyric in place. Backs the vocal pitch-line editor: adjusting the
/// note per syllable over time. Undo restores the previous pitch exactly.
/// </summary>
public class SetLyricNoteAction : IChartAction
{
    private readonly LyricEvent _lyric;
    private readonly int _oldNote;
    private readonly int _newNote;

    public SetLyricNoteAction(LyricEvent lyric, int oldNote, int newNote)
    {
        _lyric = lyric;
        _oldNote = oldNote;
        _newNote = newNote;
    }

    public bool HasEffect => _oldNote != _newNote;
    public void Execute() => _lyric.Note = _newNote;
    public void Undo() => _lyric.Note = _oldNote;
}

/// <summary>
/// Renames a single lyric's text in place. Used by the inspector's
/// "Selected lyric" rename field so typos can be corrected without
/// having to delete and re-tap the syllable.
/// </summary>
public class RenameLyricAction : IChartAction
{
    private readonly LyricEvent _lyric;
    private readonly string _oldText;
    private readonly string _newText;
    private readonly bool _oldIsWordPart;
    private readonly bool _newIsWordPart;
    private readonly bool _oldIsPhraseEnd;
    private readonly bool _newIsPhraseEnd;

    public RenameLyricAction(LyricEvent lyric, string oldText, string newText,
        bool oldIsWordPart, bool newIsWordPart,
        bool oldIsPhraseEnd = false, bool newIsPhraseEnd = false)
    {
        _lyric = lyric;
        _oldText = oldText;
        _newText = newText;
        _oldIsWordPart = oldIsWordPart;
        _newIsWordPart = newIsWordPart;
        _oldIsPhraseEnd = oldIsPhraseEnd;
        _newIsPhraseEnd = newIsPhraseEnd;
    }

    public bool HasEffect => _oldText != _newText
                             || _oldIsWordPart != _newIsWordPart
                             || _oldIsPhraseEnd != _newIsPhraseEnd;

    public void Execute()
    {
        _lyric.Text = _newText;
        _lyric.IsWordPart = _newIsWordPart;
        _lyric.IsPhraseEnd = _newIsPhraseEnd;
    }

    public void Undo()
    {
        _lyric.Text = _oldText;
        _lyric.IsWordPart = _oldIsWordPart;
        _lyric.IsPhraseEnd = _oldIsPhraseEnd;
    }
}
