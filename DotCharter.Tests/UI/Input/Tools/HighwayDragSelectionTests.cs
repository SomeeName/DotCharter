using System.Reflection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.Editing.Highway;
using DotCharter.UI.Input.Tools;

namespace DotCharter.Tests.UI.Input.Tools;

public sealed class HighwayDragSelectionTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DraggingEitherSoundClassMovesTheWholeSelection(bool clickChord, bool nearMissNote)
    {
        var h = new ChordKeyboardMutationTests.Harness();
        using var service = h.Service;
        using var tool = new HighwayInteractionTool(h.Service, h.History, new FakeAppLogger());
        var note = h.AddNote(2, 0, 7);
        h.AddTemplate(9, (1, 5), (2, 7));
        var chord = h.AddChord(4, 9, (1, 5), (2, 7));
        h.Selection.SelectedNotes.Add(note);
        h.Selection.SelectedChords.Add(chord);
        var track = new TrackEditorContext(h.Arrangement, h.Selection) { Project = h.State.ActiveProject };
        // These three gesture stages use only the track, selection and beat delta.
        // The full shell and viewport are unnecessary for their behavior.
        var context = new ToolContext(null!, null!, null!) { Track = track };
        var before = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);

        Invoke(tool, "HandleLeftClick", context, clickChord ? chord : null,
            !clickChord || nearMissNote ? note : null);
        Invoke(tool, "CaptureDragOriginals", h.Selection);
        Invoke(tool, "RefreshDragPreviews", new FractionalPosition(1));

        Assert.Equal(new FractionalPosition(3), Assert.Single(h.Selection.PreviewNotes).Position);
        Assert.Equal(new FractionalPosition(5), Assert.Single(h.Selection.PreviewChords).Position);
        Assert.True(h.Service.CommitHandShapeDragAndDrop(
            h.Selection.SelectedNotes.ToList(), h.Selection.PreviewNotes.ToList(),
            h.Selection.SelectedChords.ToList(), h.Selection.PreviewChords.ToList(),
            [], [], [], []));

        Assert.Equal(new FractionalPosition(3), Assert.Single(h.Scope.Notes).Position);
        Assert.Equal(new FractionalPosition(5), Assert.Single(h.Scope.Chords).Position);
        var after = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);
        h.History.Undo();
        Assert.Equal(new FractionalPosition(2), Assert.Single(h.Scope.Notes).Position);
        Assert.Equal(new FractionalPosition(4), Assert.Single(h.Scope.Chords).Position);
        Assert.Equal(before, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
        h.History.Redo();
        Assert.Equal(new FractionalPosition(3), Assert.Single(h.Scope.Notes).Position);
        Assert.Equal(new FractionalPosition(5), Assert.Single(h.Scope.Chords).Position);
        Assert.Equal(after, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
    }

    [Fact]
    public void ANearMissNoteDoesNotKeepAForeignSelectionWhenTheChordWins()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        using var service = h.Service;
        using var tool = new HighwayInteractionTool(h.Service, h.History, new FakeAppLogger());
        var note = h.AddNote(2, 0, 7);
        h.AddTemplate(9, (1, 5), (2, 7));
        var chord = h.AddChord(4, 9, (1, 5), (2, 7));
        h.Selection.SelectedNotes.Add(note);
        var track = new TrackEditorContext(h.Arrangement, h.Selection) { Project = h.State.ActiveProject };
        var context = new ToolContext(null!, null!, null!) { Track = track };

        Invoke(tool, "HandleLeftClick", context, chord, note);
        Invoke(tool, "CaptureDragOriginals", h.Selection);
        Invoke(tool, "RefreshDragPreviews", new FractionalPosition(1));

        Assert.Empty(h.Selection.SelectedNotes);
        Assert.Empty(h.Selection.PreviewNotes);
        Assert.Equal(new FractionalPosition(5), Assert.Single(h.Selection.PreviewChords).Position);
    }

    [Fact]
    public void TheReleasePassesBothSoundClassesToTheCommit()
    {
        // Like DragEndsAtTheReleaseTests, pin the real release wiring without
        // booting MainWindowViewModel just to obtain a snapped pointer delta.
        var source = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Input", "Tools", "HighwayInteractionTool.cs"));
        var start = source.IndexOf("public void OnPointerReleased", StringComparison.Ordinal);
        var end = source.IndexOf("public void OnPointerExited", start, StringComparison.Ordinal);
        var released = source[start..end];
        Assert.DoesNotContain("_dragTargetNote", released);
        Assert.DoesNotContain("_dragTargetChord", released);
        Assert.Contains("selection.SelectedNotes.ToList()", released);
        Assert.Contains("selection.SelectedChords.ToList()", released);
    }

    private static void Invoke(HighwayInteractionTool tool, string method, params object?[] arguments)
    {
        var member = typeof(HighwayInteractionTool).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(member);
        member.Invoke(tool, arguments);
    }
}
