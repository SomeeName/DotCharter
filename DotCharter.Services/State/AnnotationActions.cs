using System.Collections.ObjectModel;

namespace DotCharter.Services.State;

/// <summary>Checks off or reopens an annotation as persisted chart content.</summary>
public sealed class SetAnnotationResolvedAction(ChartAnnotation annotation, bool resolved) : IChartAction, ILinkPolicyAction
{
    private readonly bool _before = annotation.Resolved;

    public LinkPolicy LinkPolicy => LinkPolicy.Retain;
    public bool HasEffect => _before != resolved;
    public void Execute() => annotation.Resolved = resolved;
    public void Undo() => annotation.Resolved = _before;
}

/// <summary>Rewords an annotation (the Annotations tab's details card).</summary>
public sealed class SetAnnotationTextAction(ChartAnnotation annotation, string text) : IChartAction, ILinkPolicyAction
{
    private readonly string _before = annotation.Text;

    public LinkPolicy LinkPolicy => LinkPolicy.Retain;
    public bool HasEffect => !string.Equals(_before, text, StringComparison.Ordinal);
    public void Execute() => annotation.Text = text;
    public void Undo() => annotation.Text = _before;
}

/// <summary>Retypes an annotation: a note, a reminder or a mark.</summary>
public sealed class SetAnnotationKindAction(ChartAnnotation annotation, ChartAnnotationKind kind) : IChartAction, ILinkPolicyAction
{
    private readonly ChartAnnotationKind _before = annotation.Kind;

    public LinkPolicy LinkPolicy => LinkPolicy.Retain;
    public bool HasEffect => _before != kind;
    public void Execute() => annotation.Kind = kind;
    public void Undo() => annotation.Kind = _before;
}

/// <summary>
/// Adds a charting annotation to the project's time-ordered list. Annotations
/// are PERSISTED chart content (the cloner and <c>ChartProjectRestorer</c> both
/// carry them), so the Notes tab must not mutate the collection directly — an
/// add or a one-click delete would be invisible to Ctrl+Z and never mark the
/// session dirty. The ordered insert lives in <see cref="Execute" /> so a redo
/// re-derives the slot against whatever the list holds by then.
/// </summary>
public sealed class AddAnnotationAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

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
public sealed class RemoveAnnotationAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

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
