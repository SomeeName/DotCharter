using System.Collections.ObjectModel;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     0.24 note-inspector icon-chip rework: the technique chips are tri-state
///     ToggleButtons bound to the inspector's <c>bool?</c> projection, and every
///     click routes through the same <c>MutateNotesAction</c> + IHistoryService
///     path the old text chips used. These tests pin the two behaviours the
///     rework must preserve exactly — the tri-state projection, and
///     one-gesture-one-undo that stomps the whole selection — at the
///     view-model-helper / action level (constructing the full InspectorViewModel
///     headless boots the app-session teardown hang, see the Tests AGENTS guide).
/// </summary>
public sealed class NoteTechniqueChipBehaviorTests
{
    // --- Tri-state projection (drives the chip's checked / unchecked /
    // :indeterminate visual). Mirrors InspectorViewModel.SyncNoteProperties'
    // `NoteAccent = AllSameNullable(notes, n => n.Accent)`. ---

    [Fact]
    public void Projection_AllOn_IsChecked()
    {
        var notes = new[] { Note(accent: true), Note(accent: true) };
        Assert.True(InspectorViewModel.AllSameNullable(notes, n => n.Accent));
    }

    [Fact]
    public void Projection_AllOff_IsUnchecked()
    {
        var notes = new[] { Note(accent: false), Note(accent: false) };
        Assert.False(InspectorViewModel.AllSameNullable(notes, n => n.Accent));
    }

    [Fact]
    public void Projection_Mixed_IsIndeterminate()
    {
        var notes = new[] { Note(accent: true), Note(accent: false) };
        Assert.Null(InspectorViewModel.AllSameNullable(notes, n => n.Accent));
    }

    // --- Click-from-mixed applies to ALL selected, in a single undoable
    // gesture. Replicates InspectorViewModel.ApplyNoteMutation for the chip's
    // "commit true to every selected note" path. ---

    [Fact]
    public void ClickFromMixed_AppliesToAllSelected_InOneUndoableGesture()
    {
        var n1 = Note(accent: true);
        var n2 = Note(accent: false);
        var placed = new ObservableCollection<GuitarNote> { n1, n2 };
        var selected = new List<GuitarNote> { n1, n2 }; // heterogeneous selection
        var history = new HistoryService(new FakeAppLogger());

        // Precondition: the chip is showing indeterminate.
        Assert.Null(InspectorViewModel.AllSameNullable(selected, n => n.Accent));

        // Clicking an indeterminate chip commits `true` to every selected note.
        var applied = history.Do(new MutateNotesAction(placed, selected, n => n.Accent = true));

        Assert.True(applied);
        Assert.All(selected, n => Assert.True(n.Accent));
        Assert.True(InspectorViewModel.AllSameNullable(selected, n => n.Accent)); // now all-on
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Toggle_IsExactlyOneHistoryEntry_AndUndoRestores()
    {
        var n1 = Note(accent: true);
        var n2 = Note(accent: false);
        var placed = new ObservableCollection<GuitarNote> { n1, n2 };
        var selected = new List<GuitarNote> { n1, n2 };
        var history = new HistoryService(new FakeAppLogger());

        history.Do(new MutateNotesAction(placed, selected, n => n.Accent = true));
        Assert.True(history.CanUndo);

        history.Undo();

        // Back at the root sentinel => exactly one action was recorded.
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
        // Original per-note values restored (not blanket-cleared).
        Assert.True(n1.Accent);
        Assert.False(n2.Accent);
    }

    private static GuitarNote Note(bool accent) => new() { Accent = accent };
}
