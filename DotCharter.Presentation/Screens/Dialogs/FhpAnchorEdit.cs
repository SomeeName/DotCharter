using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Services.Editing;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The two undoable fret-hand position edits, shared by the dialog
///     (<see cref="FhpViewModel" />) and the in-place editor on the timeline
///     (<see cref="FhpInlineEditorViewModel" />) so there is ONE implementation of what
///     "save a fret-hand position" writes.
/// </summary>
internal static class FhpAnchorEdit
{
    /// <summary>Changes <paramref name="target" /> from <paramref name="before" /> to <paramref name="after" />.</summary>
    public static IChartAction Mutate(IChartEditorSurface editor, FretAnchor target, FretAnchor before, FretAnchor after,
        ObservableCollection<FretAnchor>? anchors = null) =>
        new MutateAnchorAction(editor, target, before, after, anchors ?? editor.Anchors);

    /// <summary>Adds <paramref name="anchor" /> and keeps the anchors sorted.</summary>
    public static IChartAction Add(IChartEditorSurface editor, FretAnchor anchor, ObservableCollection<FretAnchor>? anchors = null) =>
        new AddAnchorAction(editor, anchor, anchors ?? editor.Anchors);

    /// <summary>Re-sorts the anchors by position and redraws; both actions end here.</summary>
    private static void Resort(IChartEditorSurface editor, ObservableCollection<FretAnchor> anchors)
    {
        var sorted = anchors.OrderBy(a => a.Position).ToList();
        anchors.Clear();
        foreach (var anchor in sorted) anchors.Add(anchor);
        editor.RequestRedraw();
    }

    private sealed class MutateAnchorAction(IChartEditorSurface editor, FretAnchor target, FretAnchor before, FretAnchor after,
        ObservableCollection<FretAnchor> anchors)
        : IChartAction, ILinkPolicyAction
    {
        private readonly ObservableCollection<FretAnchor> _anchors = anchors;

        /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

        public bool HasEffect => !ChartActionStateComparer.AnchorsEqual(before, after);

        public void Execute() => Apply(after);

        public void Undo() => Apply(before);

        private void Apply(FretAnchor state)
        {
            target.Fret = state.Fret;
            target.Width = state.Width;
            target.Position = state.Position;
            Resort(editor, _anchors);
        }
    }

    private sealed class AddAnchorAction(IChartEditorSurface editor, FretAnchor anchor, ObservableCollection<FretAnchor> anchors) : IChartAction, ILinkPolicyAction
    {
        private readonly ObservableCollection<FretAnchor> _anchors = anchors;

        /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

        public void Execute()
        {
            _anchors.Add(anchor);
            Resort(editor, _anchors);
        }

        public void Undo()
        {
            _anchors.Remove(anchor);
            Resort(editor, _anchors);
        }
    }
}
