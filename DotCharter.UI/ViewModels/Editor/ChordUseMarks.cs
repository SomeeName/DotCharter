using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary><see cref="IChordUseMarks" /> over the chart editor's use highlight.</summary>
public sealed class ChordUseMarks(ChartEditorViewModel editor) : IChordUseMarks
{
    public void Mark(IReadOnlyList<Chord> chords, IReadOnlyList<HandShape> handShapes) =>
        editor.PublishUseHighlight(ChordUseHighlight.From(chords, handShapes, editor.ActiveArrangement));

    public void Clear() => editor.PublishUseHighlight(ChordUseHighlight.None);
}
