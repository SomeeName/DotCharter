using DotCharter.Services.Editing.Targets;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>Charts or reverses a pick slide using the import's placement and template rules.</summary>
internal sealed class PickSlideCommand(
    IEditingScope scope,
    EditTarget target,
    ISelectionService selection,
    StringEditMask mask,
    FractionalPosition? penPosition,
    int previewFret,
    AppConfig config,
    Action onPostExecute) : IHighwayEditCommand
{
    private static readonly FractionalPosition OneBeat = new(1);

    public bool Execute(IHistoryService history)
    {
        if (scope.Arrangement is not { } arrangement || scope.Level is not { } level) return false;
        var notes = target.Notes.Where(n => mask.Includes(n.StringIndex)).ToList();
        var chords = target.Chords.Where(c => c.ChordNotes.Any(n => mask.Includes(n.StringIndex))).ToList();
        var placed = Placement(arrangement);
        if (placed != null) chords.Add(placed);

        if (notes.Count == 0 && chords.Count == 0) return false;
        var flip = placed == null && notes.All(GpPickScrape.IsScrape) && chords.All(GpPickScrape.IsScrapeChord);
        var actions = new List<IChartAction>();
        var created = new List<ChordTemplate>();
        var cutPositions = notes.Where(n => !GpPickScrape.IsScrape(n)).Select(n => n.Position)
            .Concat(chords.Where(c => !GpPickScrape.IsScrapeChord(c)).Select(c => c.Position)).ToHashSet();
        foreach (var note in notes)
        {
            if (!flip && GpPickScrape.IsScrape(note)) continue;
            var next = note.Clone();
            var towardNut = !flip || !GpPickScrape.IsTowardNut(note);
            if (next.Sustain <= FractionalPosition.Zero) next.Sustain = DefaultSustain(note.Position, new HashSet<int> { note.StringIndex });
            Chart(next, note.Position, note.NotationDuration, towardNut, arrangement, level);
            next.IgnoreDerived = false;
            actions.Add(new MutateNotesAction(scope.Notes, [note], [note.Clone()], [next], onPostExecute));
        }

        foreach (var chord in chords)
        {
            if (!flip && GpPickScrape.IsScrapeChord(chord)) continue;
            var next = chord.Clone();
            var sustain = chord.ChordNotes.Any(n => n.Sustain <= FractionalPosition.Zero)
                ? DefaultSustain(chord.Position, chord.ChordNotes.Select(n => n.StringIndex).ToHashSet())
                : FractionalPosition.Zero;
            foreach (var sound in next.ChordNotes)
            {
                var towardNut = !flip || !GpPickScrape.IsTowardNut(sound);
                if (sound.Sustain <= FractionalPosition.Zero) sound.Sustain = sustain;
                Chart(sound, chord.Position, chord.NotationDuration, towardNut, arrangement, level);
            }

            next.SplitIntoNotes = true;
            next.Ignore = true;
            next.IgnoreDerived = false;
            next.TemplateId = GpPickScrape.FingerlessTemplate(arrangement, next, created).Id;
            if (ReferenceEquals(chord, placed))
            {
                placed.CopyFrom(next);
                actions.Add(new AddChordsAction(scope.Chords, [placed], onPostExecute));
            }
            else
            {
                actions.Add(new MutateChordsAction(scope.Chords, [chord], [chord.Clone()], [next], onPostExecute));
            }
        }

        if (actions.Count == 0) return false;
        if (created.Count > 0) actions.Insert(0, new AddChordTemplatesAction(arrangement.ChordTemplates, created));
        var removedShapes = AddHandShapeCuts(actions, level, cutPositions);
        actions.Add(new PickSlideSelectionAction(selection, target, removedShapes, placed));
        return history.Do(new CompositeAction(actions) { LabelKey = "Keybind_PickSlide" });
    }

    private List<HandShape> AddHandShapeCuts(List<IChartAction> actions, ChartLevel level,
        IReadOnlySet<FractionalPosition> cutPositions)
    {
        var remainingSounds = level.Notes.Where(n => !GpPickScrape.IsScrape(n)).Select(n => n.Position)
            .Concat(level.Chords.Where(c => !GpPickScrape.IsScrapeChord(c)).Select(c => c.Position))
            .Where(p => !cutPositions.Contains(p)).ToHashSet();
        var removedShapes = level.HandShapes.Where(h => cutPositions.Any(h.Covers)).ToList();
        var keptShapes = HandShapeSplitter.ExcludeSounds(removedShapes, remainingSounds, cutPositions);
        if (removedShapes.Count > 0) actions.Add(new RemoveHandShapesAction(level.HandShapes, removedShapes, onPostExecute));
        if (keptShapes.Count > 0) actions.Add(new AddHandShapesAction(level.HandShapes, keptShapes, onPostExecute));
        return removedShapes;
    }

    private Chord? Placement(Arrangement arrangement)
    {
        if (!target.IsNone || penPosition is not { } position || arrangement.StringCount < 2) return null;
        var chord = new Chord { Position = position };
        for (var i = 0; i < 2; i++)
            chord.ChordNotes.Add(new ChordNote { StringIndex = i, Fret = previewFret });
        return chord;
    }

    private static void Chart(IGuitarSound sound, FractionalPosition position, Fraction? notationDuration,
        bool towardNut, Arrangement arrangement, ChartLevel level)
    {
        var duration = notationDuration ?? sound.Sustain.Fraction + new Fraction(sound.Sustain.BeatId, 1);
        GpPickScrape.Apply(sound, towardNut ? GpPickScrape.DownFlag : GpPickScrape.UpFlag, arrangement.Capo);
        GpPickScrape.PlaceOnAnchor(arrangement, level, sound, position, duration, GpPickScrape.LastFret(null));
    }

    private FractionalPosition DefaultSustain(FractionalPosition position, IReadOnlySet<int> strings)
    {
        var end = position + OneBeat;
        var nextFound = false;
        foreach (var note in scope.Notes)
        {
            if (note.Position > position && note.Position <= end && strings.Contains(note.StringIndex))
            {
                end = note.Position;
                nextFound = true;
            }
        }

        foreach (var chord in scope.Chords)
        {
            if (chord.Position > position && chord.Position <= end && chord.ChordNotes.Any(n => strings.Contains(n.StringIndex)))
            {
                end = chord.Position;
                nextFound = true;
            }
        }

        // The committed edit also applies the import-compatible level spacing pass.
        var sustain = end - position;
        if (nextFound) sustain -= ChartSpacingEnforcer.GetMinDistance(position, scope.Beats, config);
        return FractionalPosition.Max(FractionalPosition.Zero, sustain);
    }
}
