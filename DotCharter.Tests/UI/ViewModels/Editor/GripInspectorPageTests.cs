using System.Xml.Linq;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 phase 6 review: the Shape page's guards and verbs. A ghost toggle obeys the edit mask and needs a hand
///     shape and a grip fret; Auto-fill reaches only the Apply-to rung; the Name page's lists are built only while it
///     shows and only when their inputs change (a search burst is one pass); a suggested name is one click; an
///     inferred ghost is never stored, so a later note edit cannot leave a stale mask for the exporters; and the
///     "Save and next fingering" keybind saves the grip draft.
/// </summary>
public sealed class GripInspectorPageTests
{
    [Fact]
    public void SetGhost_IsRefused_OffTheEditMask_OnAStringWithoutAGripFret_AndWithoutAHandShape()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [2] = 5, [3] = 5 });
        var shape = rig.Shape(grip, 0, 4);
        rig.Select(SoundSelectionKind.HandShape, [shape], []);

        rig.Vm.SetGhost(0, true); // no grip fret on string 0
        Assert.False(rig.Vm.HasDraft);
        rig.State.ToggleStringEditMask(3);
        rig.Vm.SetGhost(2, true); // off the mask
        Assert.False(rig.Vm.HasDraft);
        rig.Vm.SetGhost(3, true);
        Assert.True(rig.Vm.HasDraft);

        var chordOnly = new GripInspectorRig();
        var bare = chordOnly.Grip(new() { [2] = 5 });
        var chord = new Chord { Position = new(1), TemplateId = bare.Id };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
        chordOnly.Level.Chords.Add(chord);
        chordOnly.Select(SoundSelectionKind.Chord, [], [chord]);
        chordOnly.Vm.SetGhost(2, true); // no hand shape to hold a ghost
        Assert.False(chordOnly.Vm.HasDraft);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void AutoFill_FillsOnlyTheShapesOnTheRung_InOneUndoStep(int scope)
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [0] = 3, [1] = 5, [2] = 5 });
        grip.Name = "";
        var other = rig.Grip(new() { [1] = 7, [2] = 7 });
        other.Name = "";
        var first = rig.Shape(grip, 0, 2);
        var far = rig.Shape(grip, 4, 6);
        var elsewhere = rig.Shape(other, 8, 10);
        foreach (var (bar, frets) in new[] { (first, grip), (far, grip), (elsewhere, other) })
            rig.Strum(bar.Position, frets);
        rig.Select(SoundSelectionKind.HandShape, [first], []);
        rig.Vm.ScopeIndex = scope;
        var node = rig.History.CurrentNodeId;

        rig.Vm.AutoFillCommand.Execute(null);

        Assert.NotEqual(grip.Id, first.TemplateId);
        Assert.Equal(scope == 3, far.TemplateId != grip.Id);
        Assert.Equal(other.Id, elsewhere.TemplateId);
        rig.History.Undo();
        Assert.Equal(node, rig.History.CurrentNodeId);
        Assert.Equal(grip.Id, first.TemplateId);
        Assert.Equal(grip.Id, far.TemplateId);
    }

    [Fact]
    public void TheNamePageLists_AreBuiltWhileTheyShow_OnlyWhenTheirInputsChange_AndASearchBurstIsOnePass()
    {
        var rig = new GripInspectorRig();
        var queued = new List<Action>();
        rig.Vm.PostNameOffers = queued.Add;
        for (var i = 0; i < 40; i++) rig.Grip(new() { [1] = i % 20 + 1, [2] = i % 20 + 3 });
        var grip = rig.Grip(new() { [2] = 5, [3] = 5 });
        var shape = rig.Shape(grip, 0, 4);
        rig.Select(SoundSelectionKind.HandShape, [shape], []);

        rig.Vm.SetFinger(2, 1);
        rig.Row(3).GripFretText = "6";
        Assert.Equal(0, rig.Vm.NameOfferPasses); // the Board page shows: nothing built

        rig.Vm.ShowPageCommand.Execute("1");
        Assert.Equal(1, rig.Vm.NameOfferPasses);
        Assert.NotEmpty(rig.Vm.TemplateOptions);

        rig.Vm.SetFinger(3, 2); // a finger edit changes no list input
        rig.Row(3).IsGhost = true;
        Assert.Equal(1, rig.Vm.NameOfferPasses);
        rig.Row(3).GripFretText = "7"; // the frets did
        Assert.Equal(2, rig.Vm.NameOfferPasses);

        foreach (var text in new[] { "x", "x0", "x07", "x075", "x0755" }) rig.Vm.Query = text;
        Assert.Equal(2, rig.Vm.NameOfferPasses);
        Assert.Single(queued);
        queued[0]();
        Assert.Equal(3, rig.Vm.NameOfferPasses);
    }

    [Fact]
    public void ASuggestedName_IsOneClick_AndSavesOntoTheGrip()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [0] = 3, [1] = 5, [2] = 5 });
        grip.Name = "";
        var shape = rig.Shape(grip, 0, 4);
        rig.Select(SoundSelectionKind.HandShape, [shape], []);
        rig.Vm.ShowPageCommand.Execute("1");

        var suggestion = Assert.IsType<GripNameSuggestion>(rig.Vm.SuggestedNames.FirstOrDefault());
        rig.Vm.UseNameCommand.Execute(suggestion);
        rig.Vm.SaveFingeringCommand.Execute(null);

        Assert.Equal(suggestion.Name, rig.Arrangement.FindChordTemplate(shape.TemplateId)!.Name);
    }

    [Fact]
    public void AnInferredGhost_IsNeverStored_SoANoteEditUnderTheShape_LeavesNoStaleGhostInTheExports()
    {
        var rig = new GripInspectorRig();
        rig.Project.AudioFileName = "song.wav";
        var grip = rig.Grip(new() { [2] = 5, [3] = 5, [4] = 7 });
        var shape = rig.Shape(grip, 0, 4);
        var chord = new Chord { Position = new(0), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 4, Fret = 7 });
        rig.Level.Chords.Add(chord);
        rig.Select(SoundSelectionKind.HandShape, [shape], []);
        Assert.True(rig.Row(2).IsGhost); // inferred: held, not struck at the opening

        rig.Vm.SetFinger(3, 1);
        rig.Vm.SaveFingeringCommand.Execute(null);
        Assert.Null(shape.GhostMask);
        Assert.Equal(0, rig.Arrangement.FindChordTemplate(shape.TemplateId)!.GhostMask);

        // The note edit under the saved shape: string 2 is struck now.
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
        rig.Vm.Sync(chartChanged: true);
        Assert.False(rig.Row(2).IsGhost);

        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, rig.Project);
        var written = Assert.Single(doc.Descendants(), e => e.Attribute("templateId") != null && e.Attribute("ep") != null);
        Assert.Null(written.Attribute("ghost"));

        var dir = Path.Combine(Path.GetTempPath(), "dc-grip-ghost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "p.eof");
            File.WriteAllBytes(path, EofExporter.Export(rig.Project).Bytes);
            var importer = new EofImporter();
            var back = importer.ImportTracks(path, importer.GetAvailableTracks(path).Select(t => t.Id).ToList()).First();
            foreach (var bar in back.Levels.SelectMany(l => l.HandShapes))
                Assert.Equal(0, bar.GetGhostMask(back.FindChordTemplate(bar.TemplateId) ?? new ChordTemplate()));
        }
        finally
        {
            BestEffortDelete.Directory(dir);
        }
    }

    [Fact]
    public void TheSaveAndNextFingeringKeybind_SavesTheGripDraft_AndWalksToTheNextShapeMissingAFingering()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [2] = 5, [3] = 5 });
        var next = rig.Grip(new() { [2] = 7, [3] = 8 });
        var shape = rig.Shape(grip, 0, 2);
        var missing = rig.Shape(next, 4, 6);
        rig.Select(SoundSelectionKind.HandShape, [shape], []);

        rig.Vm.SetFinger(2, 1);
        rig.Vm.SetFinger(3, 1);
        rig.Vm.SaveAndNextFingeringCommand.Execute(null);

        Assert.Equal(1, rig.Arrangement.FindChordTemplate(shape.TemplateId)!.GetFinger(2));
        Assert.Same(missing, Assert.Single(rig.Editor.Selection.SelectedHandShapes));
    }
}
