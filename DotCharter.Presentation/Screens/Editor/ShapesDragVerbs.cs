namespace DotCharter.Presentation.Screens.Editor;

/// <summary>What a card dragged onto another needs from the panel that owns both.</summary>
internal interface IShapesDragHost
{
    /// <summary>The card picked as the shape to merge away: a drag's card, or the one Merge… picked.</summary>
    ShapePanelRowViewModel? MergeSource { get; }

    /// <summary>Shows the shape to merge away (null: none) and lights the card under the drag when it can take it; any bar waiting for a yes is dropped.</summary>
    void Show(ShapePanelRowViewModel? source, ShapePanelRowViewModel? over);

    /// <summary>Puts the merge in the confirmation bar, or says why the two shapes cannot merge.</summary>
    void MergeOrRefuse(ShapePanelRowViewModel source, ShapePanelRowViewModel target);
}

/// <summary>Drag one card onto another to merge it: begin, over, drop and end become the keyboard path's pick-then-merge, in the same bar. A release over nothing, over the card itself or Esc changes nothing.</summary>
internal sealed class ShapesDragVerbs(IShapesDragHost host)
{
    /// <summary>True while a card is being dragged.</summary>
    internal bool IsDragging { get; private set; }

    internal void Begin(ShapePanelRowViewModel row)
    {
        IsDragging = true;
        host.Show(row, null);
    }

    internal void Over(ShapePanelRowViewModel row, bool entered)
    {
        if (IsDragging) host.Show(host.MergeSource, entered ? row : null);
    }

    internal void Drop(ShapePanelRowViewModel target)
    {
        if (!IsDragging) return;
        var source = host.MergeSource;
        End();
        if (source is not null && !ReferenceEquals(source, target)) host.MergeOrRefuse(source, target);
    }

    internal void End()
    {
        if (!IsDragging) return;
        IsDragging = false;
        host.Show(null, null);
    }
}
