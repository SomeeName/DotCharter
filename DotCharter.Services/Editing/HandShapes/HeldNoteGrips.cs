using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.HandShapes;

internal static class HeldNoteGrips
{
    internal static (int Changed, int Selected, int Conflicts) Apply(
        TrackEditorContext track, AppConfig config, IHistoryService history)
    {
        if (track.Level is not { } level) return default;

        var selection = track.Selection.SelectedHandShapes.ToHashSet();
        var shapes = level.HandShapes.Where(selection.Contains).OrderBy(h => h.Position).ToArray();
        if (shapes.Length == 0) return default;

        var pass = new FillPass(track.Arrangement, level);
        var targets = new List<HandShape>();
        var states = new List<HandShape>();
        var spacing = ChartSpacingEnforcer.SpacingGapOf(config);
        HandShape? previous = null;
        foreach (var shape in shapes)
        {
            if (previous != null)
            {
                var gap = shape.Position - previous.EndPosition;
                if (gap < FractionalPosition.Zero || gap > spacing.At(previous.Position, track.Beats))
                    pass.Reset();
            }
            previous = shape;

            if (pass.Fill(shape) is not { } state) continue;
            targets.Add(shape);
            states.Add(state);
        }

        var changed = targets.Count > 0 && pass.Commit(history, targets, states) ? targets.Count : 0;
        return (changed, shapes.Length, pass.Conflicts);
    }

    private sealed class FillPass(Arrangement arrangement, ChartLevel level)
    {
        private readonly IGrouping<FractionalPosition, Strike>[] _onsets = Strikes(level)
            .GroupBy(n => n.Position).OrderBy(g => g.Key).ToArray();
        private readonly HeldGripBatch _grips = new(arrangement);
        private int _start;

        public int Conflicts { get; private set; }

        public void Reset() => _grips.Held.Clear();

        public HandShape? Fill(HandShape shape)
        {
            var played = new Dictionary<int, int>();
            var conflict = false;
            while (_start < _onsets.Length && _onsets[_start].Key < shape.Position)
                _start++;

            var openingStrings = 0;
            for (var i = _start; i < _onsets.Length && shape.Covers(_onsets[i].Key); i++)
            {
                var onset = _onsets[i];
                if (openingStrings == 0) openingStrings = StruckStrings(onset);
                conflict |= ReadOnset(onset, played);
            }

            if (conflict)
            {
                Conflicts++;
                return null;
            }

            var state = shape.Clone();
            return _grips.Bind(state, played, openingStrings) ? state : null;
        }

        private bool ReadOnset(IEnumerable<Strike> onset, Dictionary<int, int> played)
        {
            var openOrMuted = onset.All(n => n.Fret <= arrangement.Capo);
            if (openOrMuted || onset.Any(n => n.IsMuteMarker)) Reset();

            var conflict = false;
            foreach (var note in onset)
            {
                if (note.Fret < 0) continue;
                if (played.TryGetValue(note.StringIndex, out var fret) && fret != note.Fret) conflict = true;
                played[note.StringIndex] = note.Fret;
                if (!openOrMuted) _grips.Held[note.StringIndex] = note.Fret;
            }

            return conflict;
        }

        public bool Commit(IHistoryService history, List<HandShape> targets, List<HandShape> states)
        {
            var rebind = new MutateHandShapesAction(level.HandShapes, targets, targets.Select(h => h.Clone()), states);
            return _grips.Commit(history, rebind, "History_GripsFromHeldNotes");
        }
    }

    internal static IEnumerable<Strike> Strikes(ChartLevel level)
    {
        foreach (var note in level.Notes)
            yield return new Strike(note.Position, note.StringIndex, PlayedFret(note.Fret, note.Mute), note.Sustain, note);

        foreach (var chord in level.Chords)
        {
            foreach (var note in chord.ChordNotes)
                yield return new Strike(chord.Position, note.StringIndex, PlayedFret(note.Fret, note.Mute), note.Sustain, chord);
        }

        foreach (var mute in level.FretHandMutes)
            yield return new Strike(mute.Position, -1, -1, FractionalPosition.Zero, null, IsMuteMarker: true);
    }

    private static int PlayedFret(int fret, MuteType mute) => mute == MuteType.Full ? -1 : fret;

    internal static int StruckStrings(IEnumerable<Strike> onset)
    {
        var mask = 0;
        foreach (var strike in onset)
        {
            if (strike.Fret >= 0) mask |= ChordTemplate.GhostBit(strike.StringIndex);
        }

        return mask;
    }

    internal readonly record struct Strike(FractionalPosition Position, int StringIndex, int Fret,
        FractionalPosition Sustain, ITimedElement? Source, bool IsMuteMarker = false)
    {
        public bool CanRing => Fret >= 0 && Sustain > FractionalPosition.Zero;
        public FractionalPosition End => Position + Sustain;

        public bool IsSelected(IReadOnlySet<ITimedElement> selection) => Source != null && selection.Contains(Source);
    }
}
