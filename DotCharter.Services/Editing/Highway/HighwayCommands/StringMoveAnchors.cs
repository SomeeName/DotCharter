using System.Collections.ObjectModel;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>Plans the anchors that follow actual same-pitch string moves, before history writes either.</summary>
internal static class StringMoveAnchors
{
    private readonly record struct SoundMove(FractionalPosition Position, bool Moved, int? Delta);

    private sealed record AnchorState(FretAnchor? Original, FretAnchor After, bool Changed);

    public static IChartAction? BuildAction(SelectedSoundMutationTargets targets,
        IReadOnlyDictionary<GuitarNote, GuitarNote> notes, IReadOnlyDictionary<Chord, Chord> chords,
        IReadOnlyList<ChordTemplate> createdTemplates, string mergeVerb)
    {
        var arrangement = targets.Arrangement;
        var level = arrangement?.Levels.FirstOrDefault(l => ReferenceEquals(l.Notes, targets.Notes) &&
            ReferenceEquals(l.Chords, targets.Chords));
        if (arrangement == null || level == null || level.Anchors.Count == 0) return null;

        var sounds = SoundMoves(level, notes, chords);
        if (!sounds.Any(s => s.Moved)) return null;

        // Stable ties agree with the validation context: the first anchor at a position is in force.
        var anchors = level.Anchors.OrderBy(a => a.Position).GroupBy(a => a.Position).Select(g => g.First()).ToList();
        var states = level.Anchors.Select(a => new AnchorState(a, a.Clone(), false)).ToList();
        var projected = Project(level, notes, chords, targets.SyncedHandShapes != null);
        var templates = arrangement.ChordTemplates.Concat(createdTemplates).ToList();
        var generator = new FretAnchorGenerationService();
        var soundIndex = 0;

        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            FractionalPosition? end = i + 1 < anchors.Count ? anchors[i + 1].Position : null;
            while (soundIndex < sounds.Count && sounds[soundIndex].Position < anchor.Position) soundIndex++;
            var first = soundIndex;
            while (soundIndex < sounds.Count && (!end.HasValue || sounds[soundIndex].Position < end.Value)) soundIndex++;
            var span = sounds.GetRange(first, soundIndex - first);
            if (!span.Any(s => s.Moved)) continue;

            if (span[0].Delta is { } delta && span.All(s => s.Moved && s.Delta == delta))
            {
                var after = anchor.Clone();
                var minFret = FretLimits.MinAnchorFret(arrangement.Capo);
                after.Fret = Math.Clamp(anchor.Fret + delta, minFret, Math.Max(minFret, FretLimits.MaxAnchorFretFor(anchor.Width)));
                var index = states.FindIndex(s => ReferenceEquals(s.Original, anchor));
                states[index] = new AnchorState(anchor, after, true);
                continue;
            }

            // The generator's end is inclusive. Slice sounds first so the next span cannot enter this one.
            var generated = generator.GenerateAnchors(Span(projected, anchor.Position, end), anchor.Position, end,
                    templates, arrangement.Capo, ArrangementInstrument.IsBassPart(arrangement), tuning: arrangement)
                .Where(a => a.Position.IsWithin(anchor.Position, end)).ToList();
            if (generated.Count == 0) continue;
            states.RemoveAll(s => ReferenceEquals(s.Original, anchor));
            states.AddRange(generated.Select(a => new AnchorState(null, a, true)));
        }

        return Changes(arrangement, level, states, targets.OnPostExecute, mergeVerb, targets.AnchorSelection);
    }

    private static List<SoundMove> SoundMoves(ChartLevel level,
        IReadOnlyDictionary<GuitarNote, GuitarNote> notes, IReadOnlyDictionary<Chord, Chord> chords)
    {
        var sounds = new List<SoundMove>();
        foreach (var note in level.Notes)
        {
            var moved = notes.TryGetValue(note, out var after) && note.StringIndex != after.StringIndex;
            sounds.Add(new SoundMove(note.Position, moved, moved ? after!.Fret - note.Fret : null));
        }

        foreach (var chord in level.Chords)
        {
            var moved = chords.TryGetValue(chord, out var after) && chord.ChordNotes.Where((n, i) =>
                n.StringIndex != after.ChordNotes[i].StringIndex).Any();
            int? delta = null;
            if (moved)
            {
                var deltas = chord.ChordNotes.Select((n, i) => after!.ChordNotes[i].Fret - n.Fret).Distinct().ToList();
                if (deltas.Count == 1) delta = deltas[0];
            }

            sounds.Add(new SoundMove(chord.Position, moved, delta));
        }

        return sounds.OrderBy(s => s.Position).ToList();
    }

    private static ChartLevel Project(ChartLevel level, IReadOnlyDictionary<GuitarNote, GuitarNote> notes,
        IReadOnlyDictionary<Chord, Chord> chords, bool syncHandShapes)
    {
        var result = new ChartLevel
        {
            Notes = new(level.Notes.Select(n => notes.GetValueOrDefault(n, n))),
            Chords = new(level.Chords.Select(c => chords.GetValueOrDefault(c, c)))
        };
        var retargets = syncHandShapes
            ? ChordHandShapeSync.Follow(level.HandShapes, level.Chords,
                chords.Select(kv => new ChordHandShapeSync.ChordMove(kv.Key, kv.Key.TemplateId, kv.Value.TemplateId)).ToList())
            : [];
        var templates = retargets.ToDictionary(r => r.Shape, r => r.TemplateId);
        foreach (var shape in level.HandShapes)
        {
            var after = shape.Clone();
            if (templates.TryGetValue(shape, out var template)) after.TemplateId = template;
            result.HandShapes.Add(after);
        }

        return result;
    }

    private static ChartLevel Span(ChartLevel projected, FractionalPosition start, FractionalPosition? end)
    {
        var span = new ChartLevel
        {
            Notes = new(projected.Notes.Where(n => n.Position.IsWithin(start, end))),
            Chords = new(projected.Chords.Where(c => c.Position.IsWithin(start, end)))
        };
        foreach (var shape in projected.HandShapes.Where(s => s.EndPosition > start && (!end.HasValue || s.Position < end.Value)))
        {
            var clipped = shape.Clone();
            if (clipped.Position < start) clipped.Position = start;
            if (end.HasValue && clipped.EndPosition > end.Value) clipped.EndPosition = end.Value;
            span.HandShapes.Add(clipped);
        }

        return span;
    }

    private static IReadOnlySet<FractionalPosition> ProtectedPhrasePositions(Arrangement arrangement, ChartLevel level,
        IReadOnlyList<AnchorState> states)
    {
        var phrases = PhraseMarkers.Playable(arrangement);
        var positions = phrases.Select(p => p.Position).ToHashSet();
        for (var i = 0; i < phrases.Count; i++)
        {
            var start = phrases[i].Position;
            FractionalPosition? end = i + 1 < phrases.Count ? phrases[i + 1].Position : null;
            if (PhraseStartAnchors.FirstSound(level, start, end) is not { } first) continue;
            foreach (var state in states)
            {
                if (state.After.Position >= start && state.After.Position <= first) positions.Add(state.After.Position);
            }
        }

        return positions;
    }

    private static IChartAction? Changes(Arrangement arrangement, ChartLevel level, List<AnchorState> states,
        Action? postExecute, string mergeVerb, ObservableCollection<FretAnchor>? selectedAnchors)
    {
        var protectedPositions = ProtectedPhrasePositions(arrangement, level, states);
        var kept = new List<AnchorState>();
        foreach (var state in states.OrderBy(s => s.After.Position))
        {
            var previous = kept.LastOrDefault();
            if (previous != null && (previous.Changed || state.Changed) && previous.After.Fret == state.After.Fret &&
                previous.After.Width == state.After.Width && !protectedPositions.Contains(state.After.Position))
            {
                continue;
            }
            kept.Add(state);
        }

        var retained = kept.Where(s => s.Original != null).Select(s => s.Original!).ToHashSet();
        var removed = level.Anchors.Where(a => !retained.Contains(a)).ToList();
        var changed = kept.Where(s => s.Original != null && s.Changed).ToList();
        var added = kept.Where(s => s.Original == null).Select(s => s.After).ToList();
        var actions = new List<IChartAction>();
        if (changed.Count > 0)
        {
            actions.Add(new MutateAnchorsAction(level.Anchors, changed.Select(s => s.Original!),
                changed.Select(s => s.Original!.Clone()), changed.Select(s => s.After), postExecute, mergeVerb));
        }

        if (removed.Count > 0) actions.Add(new RemoveAnchorsAction(level.Anchors, removed, selectedAnchors));
        if (added.Count > 0) actions.Add(new AddAnchorsAction(level.Anchors, added));
        return actions.Count == 0 ? null : new CompositeAction(actions);
    }
}
