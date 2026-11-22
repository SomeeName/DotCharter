using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Maintenance;

/// <summary>Failure rollback restores the caller's focus and transient selection, after chart restoration.</summary>
internal sealed class MutationViewState
{
    private readonly Action _restore;

    public MutationViewState(ProjectState state, IChartEditorSurface editor)
    {
        var arrangement = state.ActiveArrangement;
        var level = state.ActiveLevel;
        var vocalTrack = editor.ActiveVocalTrack;
        var selection = editor.Selection;
        var collections = new[]
        {
            Capture(selection.SelectedNotes), Capture(selection.SelectedChords),
            Capture(selection.SelectedEventPoints), Capture(selection.SelectedTones),
            Capture(selection.SelectedHandShapes), Capture(selection.SelectedAnchors),
            Capture(selection.PreviewNotes), Capture(selection.PreviewChords),
            Capture(selection.PreviewHandShapes), Capture(selection.PreviewEventPoints),
            Capture(selection.PreviewAnchors)
        };
        var selectedNote = selection.SelectedNote;
        var selectedChord = selection.SelectedChord;
        var selectedLyric = selection.SelectedLyric;
        var hoveredNote = selection.HoveredNote;
        var hoveredChord = selection.HoveredChord;
        var hoveredHandShape = selection.HoveredHandShape;
        var hoveredEventPoint = selection.HoveredEventPoint;
        var hoveredTone = selection.HoveredTone;
        var hoveredAnchor = selection.HoveredAnchor;
        var hoveredLyric = selection.HoveredLyric;
        var hoveredTimeMs = selection.HoveredTimeMs;
        var hoveredStringIndex = selection.HoveredStringIndex;
        var previewArrangement = selection.PreviewArrangement;
        _restore = () =>
        {
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = level;
            editor.ActiveVocalTrack = vocalTrack;
            selection.ClearAll();
            foreach (var restore in collections) restore();
            selection.SelectedNote = selectedNote;
            selection.SelectedChord = selectedChord;
            selection.SelectedLyric = selectedLyric;
            selection.HoveredNote = hoveredNote;
            selection.HoveredChord = hoveredChord;
            selection.HoveredHandShape = hoveredHandShape;
            selection.HoveredEventPoint = hoveredEventPoint;
            selection.HoveredTone = hoveredTone;
            selection.HoveredAnchor = hoveredAnchor;
            selection.HoveredLyric = hoveredLyric;
            selection.HoveredTimeMs = hoveredTimeMs;
            selection.HoveredStringIndex = hoveredStringIndex;
            selection.PreviewArrangement = previewArrangement;
            editor.RequestRedraw();
        };
    }

    public void Restore() => _restore();

    private static Action Capture<T>(ICollection<T> collection)
    {
        var items = collection.ToArray();
        return () =>
        {
            collection.Clear();
            foreach (var item in items) collection.Add(item);
        };
    }
}
