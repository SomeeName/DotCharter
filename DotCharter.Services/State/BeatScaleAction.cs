using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

/// <summary>
/// One element whose position the tempo scale moves but which does not fit the
/// typed lists above: bend points, fret-hand mutes, showlights and vocal
/// phrase/lyric start+end positions. The setter is captured per element because
/// these live on five unrelated Core types with no shared "settable position"
/// contract, and a tempo scale is a one-shot tool action — never a hot path.
/// </summary>
public readonly record struct ScaledPosition(
    Action<FractionalPosition> Apply,
    FractionalPosition Old,
    FractionalPosition New);

/// <summary>
/// Atomic undoable action for double/half-time tempo scaling. Captures
/// snapshots of the beat list and every position-bearing element so that
/// redo/undo restore an exact pre/post state.
/// </summary>
public class BeatScaleAction : IChartAction
{
    private readonly List<ScaledPosition> _positions;

    private readonly IList<BeatMarker> _beats;
    private readonly List<BeatMarker> _oldBeats;
    private readonly List<BeatMarker> _newBeats;
    private readonly List<(GuitarNote n, FractionalPosition oPos, FractionalPosition oSus, FractionalPosition nPos, FractionalPosition nSus)> _notes;
    private readonly List<(Chord c, FractionalPosition oPos, List<FractionalPosition> oSus, FractionalPosition nPos, List<FractionalPosition> nSus)> _chords;
    private readonly List<(HandShape h, FractionalPosition oPos, FractionalPosition oEnd, FractionalPosition nPos, FractionalPosition nEnd)> _hands;
    private readonly List<(FretAnchor a, FractionalPosition oPos, FractionalPosition nPos)> _anchors;
    private readonly List<(EventPoint e, FractionalPosition oPos, FractionalPosition nPos)> _events;
    private readonly List<(ToneChange t, FractionalPosition oPos, FractionalPosition nPos)> _tones;

    public BeatScaleAction(
        IList<BeatMarker> beats,
        List<BeatMarker> oldBeats,
        List<BeatMarker> newBeats,
        List<(GuitarNote n, FractionalPosition oPos, FractionalPosition oSus, FractionalPosition nPos, FractionalPosition nSus)> notes,
        List<(Chord c, FractionalPosition oPos, List<FractionalPosition> oSus, FractionalPosition nPos, List<FractionalPosition> nSus)> chords,
        List<(HandShape h, FractionalPosition oPos, FractionalPosition oEnd, FractionalPosition nPos, FractionalPosition nEnd)> hands,
        List<(FretAnchor a, FractionalPosition oPos, FractionalPosition nPos)> anchors,
        List<(EventPoint e, FractionalPosition oPos, FractionalPosition nPos)> events,
        List<(ToneChange t, FractionalPosition oPos, FractionalPosition nPos)> tones,
        List<ScaledPosition> positions)
    {
        _beats = beats;
        _oldBeats = oldBeats;
        _newBeats = newBeats;
        _notes = notes;
        _chords = chords;
        _hands = hands;
        _anchors = anchors;
        _events = events;
        _tones = tones;
        _positions = positions;
    }

    public bool HasEffect => _newBeats.Count != _oldBeats.Count
                             || _notes.Count > 0 || _chords.Count > 0 || _hands.Count > 0
                             || _anchors.Count > 0 || _events.Count > 0 || _tones.Count > 0
                             || _positions.Count > 0;

    public void Execute()
    {
        ReplaceBeats(_newBeats);
        foreach (var (n, _, _, nPos, nSus) in _notes) { n.Position = nPos; n.Sustain = nSus; }
        foreach (var (c, _, _, nPos, nSus) in _chords)
        {
            c.Position = nPos;
            for (var i = 0; i < c.ChordNotes.Count && i < nSus.Count; i++) c.ChordNotes[i].Sustain = nSus[i];
        }
        foreach (var (h, _, _, nPos, nEnd) in _hands) { h.Position = nPos; h.EndPosition = nEnd; }
        foreach (var (a, _, nPos) in _anchors) a.Position = nPos;
        foreach (var (e, _, nPos) in _events) e.Position = nPos;
        foreach (var (t, _, nPos) in _tones) t.Position = nPos;
        foreach (var p in _positions) p.Apply(p.New);
    }

    public void Undo()
    {
        ReplaceBeats(_oldBeats);
        foreach (var (n, oPos, oSus, _, _) in _notes) { n.Position = oPos; n.Sustain = oSus; }
        foreach (var (c, oPos, oSus, _, _) in _chords)
        {
            c.Position = oPos;
            for (var i = 0; i < c.ChordNotes.Count && i < oSus.Count; i++) c.ChordNotes[i].Sustain = oSus[i];
        }
        foreach (var (h, oPos, oEnd, _, _) in _hands) { h.Position = oPos; h.EndPosition = oEnd; }
        foreach (var (a, oPos, _) in _anchors) a.Position = oPos;
        foreach (var (e, oPos, _) in _events) e.Position = oPos;
        foreach (var (t, oPos, _) in _tones) t.Position = oPos;
        foreach (var p in _positions) p.Apply(p.Old);
    }

    private void ReplaceBeats(List<BeatMarker> states)
    {
        if (_beats is ObservableCollection<BeatMarker> obs)
        {
            obs.Clear();
            foreach (var b in states) obs.Add(b.Clone());
        }
        else
        {
            _beats.Clear();
            foreach (var b in states) _beats.Add(b.Clone());
        }
    }
}
