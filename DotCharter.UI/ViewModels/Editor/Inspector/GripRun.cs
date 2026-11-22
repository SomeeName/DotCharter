namespace DotCharter.UI.ViewModels.Editor.Inspector;

internal sealed class GripRun
{
    private readonly HandShape[] _shapes;
    private readonly Arrangement _arrangement;
    private readonly int _nextIndex;
    private readonly int _previousIndex;

    internal GripRun(IEnumerable<HandShape> shapes, HandShape? current, Arrangement arrangement,
        FractionalPosition? chordPosition = null)
    {
        _shapes = shapes.OrderBy(shape => shape.Position).ToArray();
        _arrangement = arrangement;
        Position = Array.IndexOf(_shapes, current) + 1;
        _nextIndex = Position == 0 && chordPosition is { } position
            ? Array.FindIndex(_shapes, shape => shape.Position > position) : Position;
        if (_nextIndex < 0) _nextIndex = Count;
        _previousIndex = Position == 0 && chordPosition is { } onset
            ? Array.FindLastIndex(_shapes, shape => shape.Position < onset) : Position - 2;
    }

    internal int Position { get; }
    internal int Count => _shapes.Length;
    internal HandShape? Previous => _previousIndex >= 0 ? _shapes[_previousIndex] : null;
    internal HandShape? Next => _nextIndex < Count ? _shapes[_nextIndex] : null;
    internal HandShape? NextMissing => _shapes.Skip(_nextIndex).FirstOrDefault(shape => IsMissing(TemplateOf(shape)));

    /// <summary>Names a shape of the run for a Save and next button: its grip name, real frets and completeness.</summary>
    internal GripRunItem Describe(HandShape shape, string unnamed)
    {
        var template = TemplateOf(shape);
        var name = string.IsNullOrWhiteSpace(template?.Name) ? unnamed : template.Name;
        var frets = GripEdit.FretText(template ?? new ChordTemplate(), _arrangement.StringCount);
        var number = Array.IndexOf(_shapes, shape) + 1;
        return new(shape, number, name, frets, number == Position, IsMissing(template));
    }

    private ChordTemplate? TemplateOf(HandShape shape) => _arrangement.FindChordTemplate(shape.TemplateId);

    private bool IsMissing(ChordTemplate? template) =>
        template != null && GripEdit.MissingFingering(template, _arrangement.Capo);
}

public sealed record GripRunItem(HandShape Shape, int Number, string Name, string Frets, bool IsCurrent, bool IsMissing)
{
    public string Display => $"{Number} {Name} · {Frets}";
}
