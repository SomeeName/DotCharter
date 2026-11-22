using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

/// <summary>
///     The finger-draft and chord-box collaborators the template panel delegates to, driven
///     through a recording <see cref="IChordFingerSurface" /> instead of the whole view model.
/// </summary>
public class ChordFingerDraftEditorTests
{
    private sealed class FakeSurface : IChordFingerSurface
    {
        public ChordTemplate? Template { get; set; }
        public int Capo { get; set; }
        public bool IsUpdatingUi { get; set; }
        public List<StringFingeringInput> Inputs { get; } = [];
        public IReadOnlyList<StringFingeringInput> StringInputs => Inputs;
        public bool HasPendingFingerEdits { get; set; }
        public bool CanSaveFingering => HasPendingFingerEdits;
        public ChordTemplate? DraftTemplate { get; set; }
        public List<(ChordTemplate State, ChordTemplateEditKind? Kind)> Edits { get; } = [];

        public bool ApplyTemplateEdit(ChordTemplate newState, ChordTemplateEditKind? kind = null)
        {
            Edits.Add((newState, kind));
            Template = newState;
            return true;
        }
    }

    private static FakeSurface Surface(int capo = 0, params (int String, int Fret)[] frets)
    {
        var template = new ChordTemplate { Id = 1, Name = "X" };
        foreach (var (s, f) in frets) template.Frets[s] = f;
        var surface = new FakeSurface { Template = template, Capo = capo };
        for (var i = 0; i < 6; i++) surface.Inputs.Add(new StringFingeringInput(i));
        return surface;
    }

    [Fact]
    public void SetFingerForStrings_WithoutNamedStrings_FingersOnlyFrettedStringsAndCommitsOnce()
    {
        var surface = Surface(0, (0, 3), (1, 0), (2, 2));
        var editor = new ChordFingerDraftEditor(surface);

        Assert.True(editor.SetFingerForStrings(2, null));

        var edit = Assert.Single(surface.Edits);
        Assert.Equal(ChordTemplateEditKind.Fingering, edit.Kind);
        Assert.Equal([0, 2], edit.State.Fingers.Keys.OrderBy(k => k));
        Assert.False(surface.HasPendingFingerEdits);
    }

    [Fact]
    public void SetFingerForStrings_EmptyCollection_TouchesNothing()
    {
        var surface = Surface(0, (0, 3));
        var editor = new ChordFingerDraftEditor(surface);

        Assert.False(editor.SetFingerForStrings(1, []));

        Assert.Empty(surface.Edits);
        Assert.All(surface.Inputs, i => Assert.True(string.IsNullOrEmpty(i.FingerText)));
    }

    [Fact]
    public void SetFingerDraftForStrings_OpenStringUnderCapo_GetsNoFinger()
    {
        var surface = Surface(2, (0, 2), (1, 4));
        var editor = new ChordFingerDraftEditor(surface);

        Assert.True(editor.SetFingerDraftForStrings(1, [0, 1]));

        Assert.True(string.IsNullOrEmpty(surface.Inputs[0].FingerText));
        Assert.Equal("1", surface.Inputs[1].FingerText);
        Assert.True(surface.HasPendingFingerEdits);
        Assert.Empty(surface.Edits);
        Assert.Equal(1, surface.DraftTemplate!.Fingers[1]);
    }

    [Fact]
    public void CommitPendingFingerEdits_DropsFingersOnStringsTheShapeDoesNotFret()
    {
        var surface = Surface(0, (0, 3));
        surface.Inputs[3].FingerText = "2";
        surface.Inputs[0].FingerText = "1";
        surface.HasPendingFingerEdits = true;
        var editor = new ChordFingerDraftEditor(surface);

        Assert.True(editor.CommitPendingFingerEdits());

        Assert.Equal([0], surface.Edits.Single().State.Fingers.Keys);
    }

    [Fact]
    public void SnapFretToNeck_LandsSubCapoFretOnTheCapo()
    {
        var editor = new ChordFingerDraftEditor(Surface(5));

        Assert.Equal(5, editor.SnapFretToNeck(3));
        Assert.Equal(7, editor.SnapFretToNeck(7));
    }

    private static (ChordBoxEditHandler Box, FakeSurface Surface) Box(int capo = 0, params (int String, int Fret)[] frets)
    {
        var surface = Surface(capo, frets);
        var box = new ChordBoxEditHandler(surface, new ChordFingerDraftEditor(surface),
            new HistoryService(new FakeAppLogger()), _ => { });
        return (box, surface);
    }

    [Fact]
    public void ToggleNote_AddsFrettedNoteWithFingerThenRemovesIt()
    {
        var (box, surface) = Box();

        box.ToggleNote((1, 3));
        Assert.Equal(3, surface.Template!.Frets[1]);
        Assert.Equal(1, surface.Template.Fingers[1]);

        box.ToggleNote((1, 3));
        Assert.False(surface.Template.Frets.ContainsKey(1));
        Assert.False(surface.Template.Fingers.ContainsKey(1));
    }

    [Fact]
    public void ToggleNote_OnOpenStringUnderCapo_MintsNoFinger()
    {
        var (box, surface) = Box(3);

        box.ToggleNote((0, 1));

        Assert.Equal(3, surface.Template!.Frets[0]);
        Assert.False(surface.Template.Fingers.ContainsKey(0));
    }

    [Fact]
    public void ClearString_RemovesFretAndFinger_AndIgnoresUnusedStrings()
    {
        var (box, surface) = Box(0, (0, 2));
        surface.Template!.Fingers[0] = 3;

        box.ClearString(4);
        Assert.Empty(surface.Edits);

        box.ClearString(0);
        Assert.Empty(surface.Template.Frets);
        Assert.Empty(surface.Template.Fingers);
    }

    [Fact]
    public void SetHoveredFinger_FillsTheDraftWithoutCommitting()
    {
        var (box, surface) = Box(0, (2, 5));

        box.SetHoveredFinger(new ChordBoxFingerEdit(2, 4));

        Assert.Equal("4", surface.Inputs[2].FingerText);
        Assert.True(surface.HasPendingFingerEdits);
        Assert.Empty(surface.Edits);
    }

    [Fact]
    public void SetHoveredFret_SnapsBelowCapoOntoTheCapo()
    {
        var (box, surface) = Box(5, (1, 7));

        box.SetHoveredFret(new ChordBoxFretEdit(1, 3, false));

        Assert.Equal(5, surface.Template!.Frets[1]);
    }

    [Fact]
    public void Gestures_AreIgnoredWhileThePanelIsUpdating()
    {
        var (box, surface) = Box(0, (0, 2));
        surface.IsUpdatingUi = true;

        box.ToggleNote((1, 3));
        box.ClearString(0);
        box.SetHoveredFret(new ChordBoxFretEdit(1, 3, false));
        box.SetHoveredFinger(new ChordBoxFingerEdit(0, 1));

        Assert.Empty(surface.Edits);
        Assert.False(surface.HasPendingFingerEdits);
    }
}
