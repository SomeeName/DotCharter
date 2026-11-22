using DotCharter.Services.Editing.Targets;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>Keeps implied-target promotion and pick-slide placement in the chart gesture's undo node.</summary>
internal sealed class PickSlideSelectionAction : IChartAction, ILinkPolicyAction
{
    private readonly ISelectionService _selection;
    private readonly Snapshot _before;
    private readonly Snapshot _after;

    public PickSlideSelectionAction(ISelectionService selection, EditTarget target,
        IReadOnlyCollection<HandShape> removedShapes, Chord? placed)
    {
        _selection = selection;
        _before = Snapshot.Capture(selection);
        _after = target.Source == EditTargetSource.Pen || placed != null
            ? new Snapshot(placed == null ? target.Notes.ToArray() : [],
                placed == null ? target.Chords.ToArray() : [placed], [], [], [], [], null)
            : _before with
            {
                Notes = _before.Notes.Concat(target.Notes).Distinct().ToArray(),
                Chords = _before.Chords.Concat(target.Chords).Distinct().ToArray(),
                Shapes = _before.Shapes.Where(h => !removedShapes.Contains(h)).ToArray()
            };
    }

    public LinkPolicy LinkPolicy => LinkPolicy.Retain;
    public void Execute() => _after.Restore(_selection);
    public void Undo() => _before.Restore(_selection);

    private sealed record Snapshot(GuitarNote[] Notes, Chord[] Chords, HandShape[] Shapes, FretAnchor[] Anchors,
        EventPoint[] Events, ToneChange[] Tones, LyricEvent? Lyric)
    {
        public static Snapshot Capture(ISelectionService selection)
            => new(selection.SelectedNotes.ToArray(), selection.SelectedChords.ToArray(),
                selection.SelectedHandShapes.ToArray(), selection.SelectedAnchors.ToArray(),
                selection.SelectedEventPoints.ToArray(), selection.SelectedTones.ToArray(), selection.SelectedLyric);

        public void Restore(ISelectionService selection)
        {
            SelectionBulkOps.ReplaceWith(selection.SelectedNotes, Notes);
            SelectionBulkOps.ReplaceWith(selection.SelectedChords, Chords);
            SelectionBulkOps.ReplaceWith(selection.SelectedHandShapes, Shapes);
            SelectionBulkOps.ReplaceWith(selection.SelectedAnchors, Anchors);
            SelectionBulkOps.ReplaceWith(selection.SelectedEventPoints, Events);
            SelectionBulkOps.ReplaceWith(selection.SelectedTones, Tones);
            selection.SelectedLyric = Lyric;
        }
    }
}
