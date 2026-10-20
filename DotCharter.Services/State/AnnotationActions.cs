using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

/// <summary>
/// Adds a charting annotation to the project's time-ordered list. Annotations
/// are PERSISTED chart content (the cloner and <c>ChartProjectRestorer</c> both
/// carry them), so the Notes tab must not mutate the collection directly — an
/// add or a one-click delete would be invisible to Ctrl+Z and never mark the
/// session dirty. The ordered insert lives in <see cref="Execute" /> so a redo
/// re-derives the slot against whatever the list holds by then.
/// </summary>
public sealed class AddAnnotationAction : IChartAction
{
    private readonly ObservableCollection<ChartAnnotation> _annotations;
    private readonly ChartAnnotation _annotation;

    public AddAnnotationAction(ObservableCollection<ChartAnnotation> annotations, ChartAnnotation annotation)
    {
        _annotations = annotations;
        _annotation = annotation;
    }

    public void Execute()
    {
        var insertAt = 0;
        while (insertAt < _annotations.Count && _annotations[insertAt].TimeMs <= _annotation.TimeMs)
            insertAt++;
        _annotations.Insert(insertAt, _annotation);
    }

    public void Undo() => _annotations.Remove(_annotation);
}

/// <summary>
/// Removes a charting annotation; undo re-inserts it at its original index.
/// Same shape as <see cref="AddAnnotationAction" /> — see its remarks for why
/// these exist.
/// </summary>
public sealed class RemoveAnnotationAction : IChartAction
{
    private readonly ObservableCollection<ChartAnnotation> _annotations;
    private readonly ChartAnnotation _annotation;
    private readonly bool _hasEffect;
    private int _index = -1;

    public RemoveAnnotationAction(ObservableCollection<ChartAnnotation> annotations, ChartAnnotation annotation)
    {
        _annotations = annotations;
        _annotation = annotation;
        // Captured at construction, NOT computed live: HistoryService.Do checks
        // HasEffect, Executes, and then PushAction checks it AGAIN — a live
        // Contains() is false by then and the executed removal is never pushed,
        // i.e. exactly the un-undoable delete this action exists to fix.
        _hasEffect = annotations.Contains(annotation);
    }

    public bool HasEffect => _hasEffect;

    public void Execute()
    {
        _index = _annotations.IndexOf(_annotation);
        if (_index >= 0) _annotations.RemoveAt(_index);
    }

    public void Undo()
    {
        if (_index < 0) return;
        _annotations.Insert(Math.Min(_index, _annotations.Count), _annotation);
    }
}
