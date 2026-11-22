using DotCharter.Core.Linking;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.HandShapes;

internal static class LetRingHandShapes
{
    public static (int Created, int Existing) Apply(TrackEditorContext track, AppConfig config, IHistoryService history)
    {
        if (track.Level is not { } level) return default;
        var selection = track.Selection.SelectedNotes.Cast<ITimedElement>().Concat(track.Selection.SelectedChords).ToHashSet();
        if (selection.Count == 0) return default;

        var strikes = HeldNoteGrips.Strikes(level).ToArray();
        var ringing = strikes.Where(s => s.CanRing && s.IsSelected(selection)).ToArray();
        if (ringing.Length == 0) return default;

        var start = ringing.Min(s => s.Position);
        var end = ringing.Max(s => s.End);
        var existing = level.HandShapes.Where(h => h.Position < end && h.EndPosition > start && h.Position < h.EndPosition)
            .OrderBy(h => h.Position).ToArray();
        var onsets = strikes.Where(s => s.Position.IsWithin(start, end)).GroupBy(s => s.Position).OrderBy(g => g.Key);
        var grips = new HeldGripBatch(track.Arrangement);
        var pass = new ShapePass(grips, track.Beats, ChartSpacingEnforcer.SpacingGapOf(config));
        Build(pass, onsets, selection, existing, start, end);

        if (pass.Shapes.Count == 0) return (0, existing.Length);
        var add = new AddHandShapesAction(level.HandShapes, pass.Shapes);
        var committed = grips.Commit(history, add, "History_HandShapesFromLetRing");
        return (committed ? pass.Shapes.Count : 0, existing.Length);
    }

    private static void Build(ShapePass pass, IEnumerable<IGrouping<FractionalPosition, HeldNoteGrips.Strike>> onsets,
        IReadOnlySet<ITimedElement> selection, HandShape[] existing, FractionalPosition start, FractionalPosition end)
    {
        var next = 0;
        var coveredUntil = start;
        foreach (var onset in onsets)
        {
            while (next < existing.Length && existing[next].Position <= onset.Key)
            {
                var bar = existing[next++];
                pass.EndRunAt(bar.Position);
                coveredUntil = FractionalPosition.Max(coveredUntil, bar.EndPosition);
            }

            if (onset.Key >= coveredUntil) pass.Add(onset, selection);
        }

        pass.EndRunAt(next < existing.Length ? existing[next].Position : end);
    }

    private sealed class ShapePass(HeldGripBatch grips, IList<BeatMarker> beats, SpacingGap spacing)
    {
        private readonly Dictionary<int, int> _played = [];
        private FractionalPosition? _start;
        private FractionalPosition? _end;
        private FractionalPosition _last;
        private int _openingStrings;

        public List<HandShape> Shapes { get; } = [];

        public void Add(IGrouping<FractionalPosition, HeldNoteGrips.Strike> onset, IReadOnlySet<ITimedElement> selection)
        {
            var position = onset.Key;
            var selected = onset.Where(s => s.IsSelected(selection)).ToArray();
            var ringing = selected.Where(s => s.CanRing).ToArray();
            if (ringing.Length == 0 || HasConflictingFrets(ringing))
            {
                EndRunAt(position);
                return;
            }

            if (ringing.Length != selected.Length || onset.Any(s => s.IsMuteMarker) ||
                (_end is { } runEnd && position - runEnd > spacing.At(_last, beats)))
            {
                EndRunAt(position);
            }
            if (ringing.Any(ChangesGrip)) CloseAt(position);

            if (_start == null)
            {
                _start = position;
                _openingStrings = HeldNoteGrips.StruckStrings(onset);
            }
            var end = ringing.Max(s => s.End);
            _end = FractionalPosition.Max(_end ?? end, end);
            _last = position;
            foreach (var note in ringing)
            {
                grips.Held[note.StringIndex] = note.Fret;
                _played[note.StringIndex] = note.Fret;
            }
        }

        public void EndRunAt(FractionalPosition position)
        {
            if (_end is { } end) CloseAt(FractionalPosition.Min(end, position));
            grips.Held.Clear();
            _end = null;
        }

        private bool ChangesGrip(HeldNoteGrips.Strike note) =>
            grips.Held.TryGetValue(note.StringIndex, out var held) && held != note.Fret;

        private static bool HasConflictingFrets(IEnumerable<HeldNoteGrips.Strike> ringing) =>
            ringing.GroupBy(s => s.StringIndex).Any(g => g.Any(s => s.Fret != g.First().Fret));

        private void CloseAt(FractionalPosition end)
        {
            if (_start is not { } start) return;
            var shape = new HandShape { Position = start, EndPosition = end, Name = string.Empty };
            if (end > start && grips.Bind(shape, _played, _openingStrings)) Shapes.Add(shape);
            _start = null;
            _played.Clear();
        }
    }
}
