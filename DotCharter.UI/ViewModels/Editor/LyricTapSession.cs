using System.Collections.ObjectModel;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>What a <see cref="LyricTapSession.TapNext" /> call did.</summary>
internal enum LyricTapOutcome
{
    /// <summary>The next syllable was committed to the chart as one undo node.</summary>
    Tapped,

    /// <summary>There is no active vocal track to tap into.</summary>
    NoVocalTrack,

    /// <summary>The chart has no beat grid to anchor the syllable on.</summary>
    NoBeats
}

/// <summary>Which history step moved the queue.</summary>
internal enum LyricTapChange
{
    Undone,
    Redone
}

/// <summary>
///     The tap-sync queue behind the lyrics inspector: the syllables still to be tapped, the
///     phrase each one lands in, and the undo/redo bookkeeping of a tap. The view model keeps
///     only the UI state (armed flag, counters, preview, status text) and reads it from here.
/// </summary>
internal sealed class LyricTapSession
{
    private const double DefaultLastSyllableMs = 250.0;
    private const int PreviewSyllables = 8;

    // Flat queue across phrases; each item carries its phrase index so we can
    // group LyricEvents back into LyricPhrase blocks at commit time.
    private readonly List<QueueItem> _queue = [];

    // Each queued line keeps its tap history so deleted anchors can fall back to survivors.
    private readonly Dictionary<int, LyricEvent[]> _anchorsByIndex = [];

    // A tapped syllable ends its line until the next syllable of its line lands after it, so its
    // queued word part is kept here and set only then (LyricLineRule: a line end is no word part).
    private readonly Dictionary<LyricEvent, bool> _queuedWordPart = [];

    /// <summary>Raised after an undo or redo of a tap moved the queue.</summary>
    public event Action<LyricTapChange>? Changed;

    public int Count => _queue.Count;

    /// <summary>The next few syllables, space separated.</summary>
    public string Preview => string.Join(" ", _queue.Take(PreviewSyllables).Select(q => q.Text));

    /// <summary>
    ///     Parses the raw block (syllables split by <c>-</c>, words by space, phrases by newline)
    ///     into the queue. A line ending in a word part joins the next line, as the word-part flag
    ///     does in the editor. An empty result leaves the phrase map alone, like a fresh arm would.
    /// </summary>
    /// <returns>The number of queued syllables.</returns>
    public int Load(string rawLyrics)
    {
        _queue.Clear();
        var lines = rawLyrics.Replace("\r\n", "\n").Split('\n');
        var phraseIdx = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (EnqueueLine(line, phraseIdx) && !_queue[^1].IsWordPart) phraseIdx++;
        }

        if (_queue.Count == 0) return 0;
        _anchorsByIndex.Clear();
        _queuedWordPart.Clear();
        return _queue.Count;
    }

    /// <summary>Drops the queue and the phrase mapping.</summary>
    public void Clear()
    {
        _queue.Clear();
        _anchorsByIndex.Clear();
        _queuedWordPart.Clear();
    }

    /// <summary>
    ///     Pops the next syllable and commits it to <paramref name="track" /> at
    ///     <paramref name="tapMs" /> through <paramref name="history" />.
    /// </summary>
    public LyricTapOutcome TapNext(VocalTrack? track, ObservableCollection<BeatMarker> beats, double tapMs,
        IHistoryService history, out string tappedText)
    {
        tappedText = "";
        if (track == null) return LyricTapOutcome.NoVocalTrack;
        if (beats.Count == 0) return LyricTapOutcome.NoBeats;

        var item = _queue[0];
        var action = BuildAction(track, beats, tapMs, item);
        history.Do(action);
        tappedText = item.Text;
        return LyricTapOutcome.Tapped;
    }

    // Called by LyricTapAction.Undo: pushes the syllable back to the front of the queue
    // and restores the anchor that preceded this tap.
    internal void RestoreForUndo(QueueItem item, LyricEvent[] previousAnchors)
    {
        _queue.Insert(0, item);
        if (previousAnchors.Length == 0) _anchorsByIndex.Remove(item.PhraseIndex);
        else _anchorsByIndex[item.PhraseIndex] = previousAnchors;
        Changed?.Invoke(LyricTapChange.Undone);
    }

    // Called by LyricTapAction.Execute: pops once on the initial commit or redo
    // and establishes the phrase map. TapNext must leave the queue intact.
    internal void ReapplyForRedo(QueueItem item, LyricEvent lyric, LyricEvent[] previousAnchors)
    {
        if (_queue.Count > 0 && _queue[0].PhraseIndex == item.PhraseIndex && _queue[0].Text == item.Text)
            _queue.RemoveAt(0);
        _anchorsByIndex[item.PhraseIndex] = [lyric, .. previousAnchors];
        _queuedWordPart[lyric] = item.IsWordPart;
        Changed?.Invoke(LyricTapChange.Redone);
    }

    private bool EnqueueLine(string line, int phraseIdx)
    {
        var phraseHadSyllable = false;
        foreach (var word in line.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = word.Split('-');
            for (var i = 0; i < parts.Length; i++)
            {
                var text = parts[i];
                if (text.Length == 0) continue;
                var isWordPart = i < parts.Length - 1;
                // Store the syllable text WITHOUT the trailing dash —
                // the dash is just an input-syntax separator, the
                // continuation is encoded by IsWordPart and rendered
                // as a connector line between adjacent pills.
                _queue.Add(new QueueItem(phraseIdx, text, isWordPart));
                phraseHadSyllable = true;
            }
        }

        return phraseHadSyllable;
    }

    private LyricTapAction BuildAction(VocalTrack track, ObservableCollection<BeatMarker> beats, double tapMs,
        QueueItem item)
    {
        var startPos = TimelineMath.PositionFromMs(tapMs, beats);
        var endMs = tapMs + DefaultLastSyllableMs;
        var endPos = TimelineMath.PositionFromMs(endMs, beats);

        // Decide which phrase the new syllable lands in WITHOUT mutating
        // anything yet — the LyricTapAction does the actual chart mutation
        // in its Execute() so initial-commit and redo are identical.
        _anchorsByIndex.TryGetValue(item.PhraseIndex, out var previousAnchors);
        previousAnchors ??= [];
        var target = LyricTapTarget.Resolve(track.Phrases, previousAnchors, startPos);
        var existingPhrase = target.Phrase;
        var phraseStillInTrack = existingPhrase != null;
        var targetPhrase = phraseStillInTrack ? existingPhrase! : new LyricPhrase
        {
            Position = startPos,
            EndPosition = endPos
        };
        var newLyric = new LyricEvent { Text = item.Text, Position = startPos, EndPosition = endPos };
        LyricLineRule.Apply(newLyric, item.IsWordPart, true);
        // The syllable before it in the same queued line stops ending the line and gets its word part.
        var previous = targetPhrase.Lyrics.LastOrDefault();
        bool? previousWordPart = previous != null && previousAnchors.Length > 0 &&
                                 ReferenceEquals(previousAnchors[0], previous) &&
                                 _queuedWordPart.TryGetValue(previous, out var queued)
            ? queued
            : null;

        var phraseStartBefore = targetPhrase.Position;
        var phraseEndBefore = targetPhrase.EndPosition;
        FractionalPosition? phraseNewStart = phraseStartBefore.GetPositionMs(beats) > tapMs ? startPos : null;
        FractionalPosition? phraseNewEnd = phraseEndBefore.GetPositionMs(beats) < endMs ? endPos : null;

        return new LyricTapAction(this, track.Phrases, targetPhrase, newLyric, item,
            addPhraseToTrack: !phraseStillInTrack,
            previousAnchors: previousAnchors,
            trimLyric: target.Trim,
            phraseStartBefore: phraseStartBefore,
            phraseEndBefore: phraseEndBefore,
            phraseNewStart: phraseNewStart,
            phraseNewEnd: phraseNewEnd,
            prevWordPart: previousWordPart);
    }

    internal readonly record struct QueueItem(int PhraseIndex, string Text, bool IsWordPart);
}

/// <summary>
/// Wraps a single syllable-tap commit so undo/redo also moves the
/// LyricTapSession queue cursor back/forward AND fully reverts the
/// chart mutation (LyricEvent insert + previous-syllable tail trim +
/// new LyricPhrase add). Without this, the user hits Ctrl+Z to recover
/// from a mis-tap and either the syllable stays in the chart (because
/// in-place LyricPhrase mutation isn't covered by SetVocalPhrasesAction)
/// or the inspector queue advances past the recovered syllable.
/// </summary>
internal sealed class LyricTapAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly LyricTapSession _session;
    private readonly ObservableCollection<LyricPhrase> _trackPhrases;
    private readonly LyricPhrase _targetPhrase;
    private readonly LyricEvent _newLyric;
    private readonly LyricTapSession.QueueItem _item;
    private readonly bool _addPhraseToTrack;
    private readonly LyricEvent[] _previousAnchors;
    private readonly LyricEvent? _prevLyric;
    private readonly bool _prevIsPhraseEnd;
    private readonly bool _prevIsWordPart;
    private readonly bool? _prevWordPart;
    private readonly FractionalPosition? _prevEndBefore;
    private readonly LyricEvent? _trimLyric;
    private readonly FractionalPosition _phraseStartBefore;
    private readonly FractionalPosition _phraseEndBefore;
    private readonly FractionalPosition? _phraseNewStart;
    private readonly FractionalPosition? _phraseNewEnd;

    public LyricTapAction(LyricTapSession session,
        ObservableCollection<LyricPhrase> trackPhrases,
        LyricPhrase targetPhrase, LyricEvent newLyric, LyricTapSession.QueueItem item,
        bool addPhraseToTrack, LyricEvent[] previousAnchors, LyricEvent? trimLyric,
        FractionalPosition phraseStartBefore, FractionalPosition phraseEndBefore,
        FractionalPosition? phraseNewStart, FractionalPosition? phraseNewEnd, bool? prevWordPart)
    {
        _session = session;
        _trackPhrases = trackPhrases;
        _targetPhrase = targetPhrase;
        _newLyric = newLyric;
        _item = item;
        _addPhraseToTrack = addPhraseToTrack;
        _previousAnchors = previousAnchors;
        _prevLyric = targetPhrase.Lyrics.LastOrDefault();
        _prevIsPhraseEnd = _prevLyric?.IsPhraseEnd ?? false;
        _prevIsWordPart = _prevLyric?.IsWordPart ?? false;
        _prevWordPart = prevWordPart;
        _trimLyric = trimLyric;
        _prevEndBefore = trimLyric?.EndPosition;
        _phraseStartBefore = phraseStartBefore;
        _phraseEndBefore = phraseEndBefore;
        _phraseNewStart = phraseNewStart;
        _phraseNewEnd = phraseNewEnd;
    }

    public bool HasEffect => true;

    public void Execute()
    {
        if (_prevLyric != null)
            LyricLineRule.Apply(_prevLyric, _prevWordPart ?? _prevLyric.IsWordPart, false);
        _trimLyric?.EndPosition = _newLyric.Position;
        if (_phraseNewStart.HasValue) _targetPhrase.Position = _phraseNewStart.Value;
        if (_phraseNewEnd.HasValue) _targetPhrase.EndPosition = _phraseNewEnd.Value;
        if (!_targetPhrase.Lyrics.Contains(_newLyric))
            _targetPhrase.Lyrics.Add(_newLyric);
        if (_addPhraseToTrack && !_trackPhrases.Contains(_targetPhrase))
        {
            // Insert in time-sorted order so the rendered ribbon stays tidy.
            var idx = 0;
            while (idx < _trackPhrases.Count &&
                   _trackPhrases[idx].Position.CompareTo(_targetPhrase.Position) <= 0)
            {
                idx++;
            }

            _trackPhrases.Insert(idx, _targetPhrase);
        }
        _session.ReapplyForRedo(_item, _newLyric, _previousAnchors);
    }

    public void Undo()
    {
        _targetPhrase.Lyrics.Remove(_newLyric);
        _prevLyric?.IsPhraseEnd = _prevIsPhraseEnd;
        _prevLyric?.IsWordPart = _prevIsWordPart;
        if (_addPhraseToTrack)
            _trackPhrases.Remove(_targetPhrase);
        if (_phraseNewStart.HasValue) _targetPhrase.Position = _phraseStartBefore;
        if (_phraseNewEnd.HasValue) _targetPhrase.EndPosition = _phraseEndBefore;
        if (_trimLyric != null && _prevEndBefore.HasValue)
            _trimLyric.EndPosition = _prevEndBefore.Value;
        _session.RestoreForUndo(_item, _previousAnchors);
    }
}
