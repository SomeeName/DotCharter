using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

internal enum GripShapeFields
{
    All,
    NameAndFingers,
    Fingers
}

internal readonly record struct GripShapeTarget(ChartLevel Level, HandShape Shape, GripShapeFields Fields = GripShapeFields.All);

internal sealed record GripInspectorTargets(
    IReadOnlyList<(ChartLevel Level, Chord Chord)> Chords,
    IReadOnlyList<GripShapeTarget> Shapes)
{
    internal IReadOnlySet<Chord> ChordSet { get; } = new HashSet<Chord>(Chords.Select(t => t.Chord), ReferenceEqualityComparer.Instance);

    internal static HandShape? CoveringShape(ChartLevel level, HandShape? shape, Chord? chord) =>
        chord == null || shape?.Covers(chord.Position) == true
            ? shape : level.HandShapes.FirstOrDefault(h => h.Covers(chord.Position));

    internal static GripInspectorTargets Resolve(
        Arrangement arrangement, ChartLevel level, HandShape? shape, Chord? chord,
        IReadOnlyCollection<HandShape> selectedShapes, IReadOnlyCollection<Chord> selectedChords,
        ChordApplyRung scope)
    {
        if (scope == ChordApplyRung.ThisChord)
        {
            var covering = CoveringShape(level, shape, chord);
            return new(chord == null ? [] : [(level, chord)], covering == null ? [] : [new(level, covering)]);
        }

        if (scope == ChordApplyRung.Selected)
        {
            var chords = level.Chords.Where(selectedChords.Contains).ToArray();
            var shapes = level.HandShapes.Where(h => selectedShapes.Contains(h) || chords.Any(c => h.Covers(c.Position))).ToArray();
            return new(chords.Length == 0 && chord != null ? [(level, chord)] : chords.Select(c => (level, c)).ToArray(),
                shapes.Length == 0 && shape != null ? [new(level, shape)] : shapes.Select(h => new GripShapeTarget(level, h)).ToArray());
        }

        var grip = arrangement.FindChordTemplate(shape?.TemplateId ?? chord?.TemplateId ?? -1);
        var chordId = chord?.TemplateId ?? grip?.Id ?? -1;
        var shapeFields = ShapeFields(arrangement, grip, scope);
        var levels = arrangement.Levels.Append(level).Distinct();
        return new(
            levels.SelectMany(l => l.Chords.Where(c => ReferenceEquals(c, chord) || chordId >= 0 && c.TemplateId == chordId)
                .Select(c => (l, c))).ToArray(),
            levels.SelectMany(l => l.HandShapes.Where(h => ReferenceEquals(h, shape) || shapeFields.ContainsKey(h.TemplateId))
                .Select(h => new GripShapeTarget(l, h, shapeFields.GetValueOrDefault(h.TemplateId)))).ToArray());
    }

    private static Dictionary<int, GripShapeFields> ShapeFields(Arrangement arrangement, ChordTemplate? grip, ChordApplyRung scope)
    {
        var fields = new Dictionary<int, GripShapeFields>();
        if (grip == null) return fields;
        fields.Add(grip.Id, GripShapeFields.All);
        if (scope != ChordApplyRung.SameGrip) return fields;

        var matches = ChordGripMatches.Find(arrangement, grip);
        foreach (var same in matches.For(ChordTemplateEditKind.Name))
            fields.TryAdd(same.Id, GripShapeFields.NameAndFingers);
        foreach (var moved in matches.For(ChordTemplateEditKind.Fingering))
            fields.TryAdd(moved.Id, GripShapeFields.Fingers);
        return fields;
    }
}
