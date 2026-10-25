using DotCharter.Tests.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     The epic's acceptance (#239): the six old-chart fixes of the approved design, each driven through the real
///     Shapes panel and Edit shape dialog over an old chart with the reporter's mess (one grip stored as three
///     unnamed copies and a fourth fingered otherwise, a named shape and its unnamed copy, unused shapes, two
///     levels, a notation voice). Each test counts the user's steps against the design's tally and against
///     delete-and-redo, asserts the chart afterwards exactly, and that ONE undo restores it.
/// </summary>
public sealed class ShapeWorkflowAcceptanceTests
{
    private readonly ShapeWorkflowRig _rig = new();

    private MessyShapesFixture Old => _rig.Old;

    private UserSteps Steps => _rig.Steps;

    [Fact]
    public void Merge_ThreeCopiesOfOneGrip_TickMergeConfirm_IntoTheMostUsedShape_OnEveryLevel()
    {
        var before = _rig.Describe();

        _rig.Tick(Old.Grip0, Old.Grip1, Old.Grip2);
        Steps.Do("Merge into one", () => _rig.Panel.MergeSelectedCommand.Execute(null));
        Assert.True(_rig.Panel.HasPending);
        Assert.Equal(before, _rig.Describe());
        Steps.Do("confirm", () => _rig.Panel.ConfirmPendingCommand.Execute(null));

        Assert.False(_rig.Panel.HasPending);
        Assert.Equal(string.Join('\n',
            "0 - frets 2:9 3:8 4:11 fingers 2:2 3:1 4:4",
            "1 E5 frets 0:0 1:2 2:2 fingers 1:1 2:2",
            "2 - frets 1:0 2:2 3:2 fingers 2:1 3:2",
            "3 - frets 3:5 4:7 fingers 3:1 4:3",
            "4 A5 frets 1:0 2:2 3:2 fingers 2:1 3:2",
            "5 - frets 2:9 3:8 4:11 fingers 2:2 3:1 4:3",
            "6 Dm frets 2:0 3:2 4:3 fingers 3:1 4:2",
            "7 - frets 0:1 fingers 0:1",
            "L0 chords 0@0 0@4 0@8 5@12 4@16",
            "L0 hands 0@0 0@8 5@12",
            "L1 chords 0@0 5@4 4@8 2@12 1@16",
            "L1 hands 0@0 2@12",
            "voice chords 0@20"), _rig.Describe());
        _rig.AssertChordsPlayTheirShapes();
        _rig.AssertSteps(design: 3, deleteAndRedo: 6 * Old.Arrangement.Levels.Count);
        _rig.AssertOneUndoRestores(before);
    }

    [Fact]
    public void Edit_TheFretOnEveryUse_EditApply_ChangesTheShapeAndEveryChordAndHandShapeOverItFollows()
    {
        var before = _rig.Describe();
        var chords = Old.AllChords.Count(c => c.TemplateId == Old.Grip0.Id);
        var hands = Old.AllHandShapes.Count(h => h.TemplateId == Old.Grip0.Id);
        _rig.EditDialog = dialog =>
        {
            Assert.True(dialog.Scopes[2].IsSelected);
            Steps.Do("change the fret", () => dialog.Strings[2].Fret = "10");
            Steps.Do("Apply", () => dialog.ApplyCommand.Execute(null));
        };

        Steps.Do("Edit", () => _rig.Row(Old.Grip0).EditCommand.Execute(null));

        Assert.Equal((2, 1), (chords, hands));
        Assert.Equal(string.Join('\n',
            "0 - frets 2:10 3:8 4:11 fingers 2:2 3:1 4:4",
            "1 E5 frets 0:0 1:2 2:2 fingers 1:1 2:2",
            "2 - frets 2:9 3:8 4:11 fingers 2:2 3:1 4:4",
            "3 - frets 1:0 2:2 3:2 fingers 2:1 3:2",
            "4 - frets 3:5 4:7 fingers 3:1 4:3",
            "5 - frets 2:9 3:8 4:11 fingers 2:2 3:1 4:4",
            "6 A5 frets 1:0 2:2 3:2 fingers 2:1 3:2",
            "7 - frets 2:9 3:8 4:11 fingers 2:2 3:1 4:3",
            "8 Dm frets 2:0 3:2 4:3 fingers 3:1 4:2",
            "9 - frets 0:1 fingers 0:1",
            "L0 chords 0@0 0@4 2@8 7@12 6@16",
            "L0 hands 0@0 2@8 7@12",
            "L1 chords 5@0 7@4 6@8 3@12 1@16",
            "L1 hands 5@0 3@12",
            "voice chords 5@20"), _rig.Describe());
        Assert.All(Old.AllChords.Where(c => c.TemplateId == Old.Grip0.Id), c => Assert.Contains(c.ChordNotes, n => n.StringIndex == 2 && n.Fret == 10));
        _rig.AssertChordsPlayTheirShapes();
        _rig.AssertSteps(design: 3, deleteAndRedo: chords * 2 + hands);
        _rig.AssertOneUndoRestores(before);
    }

    [Fact]
    public void Delete_AUsedShape_DeleteConfirm_MovesItsUsesOntoACopyWithTheSameFrets()
    {
        var before = _rig.Describe();
        var uses = Old.AllChords.Count(c => c.TemplateId == Old.GripAlt.Id) + Old.AllHandShapes.Count(h => h.TemplateId == Old.GripAlt.Id);

        Steps.Do("Delete", () => _rig.Row(Old.GripAlt).DeleteCommand.Execute(null));
        Assert.True(_rig.Panel.HasPending);
        Assert.True(_rig.Panel.IsPendingDestructive);
        Assert.Equal(before, _rig.Describe());
        Steps.Do("confirm", () => _rig.Panel.ConfirmPendingCommand.Execute(null));

        Assert.Equal(string.Join('\n',
            "0 - frets 2:9 3:8 4:11 fingers 2:2 3:1 4:4",
            "1 E5 frets 0:0 1:2 2:2 fingers 1:1 2:2",
            "2 - frets 2:9 3:8 4:11 fingers 2:2 3:1 4:4",
            "3 - frets 1:0 2:2 3:2 fingers 2:1 3:2",
            "4 - frets 3:5 4:7 fingers 3:1 4:3",
            "5 - frets 2:9 3:8 4:11 fingers 2:2 3:1 4:4",
            "6 A5 frets 1:0 2:2 3:2 fingers 2:1 3:2",
            "7 Dm frets 2:0 3:2 4:3 fingers 3:1 4:2",
            "8 - frets 0:1 fingers 0:1",
            "L0 chords 0@0 0@4 2@8 0@12 6@16",
            "L0 hands 0@0 2@8 0@12",
            "L1 chords 5@0 0@4 6@8 3@12 1@16",
            "L1 hands 5@0 3@12",
            "voice chords 5@20"), _rig.Describe());
        _rig.AssertChordsPlayTheirShapes();
        _rig.AssertSteps(design: 2, deleteAndRedo: 1 + uses + 1);
        _rig.AssertOneUndoRestores(before);
    }

    [Fact]
    public void Refinger_AllCopiesOfAGrip_TickEditTogetherSetApply_LeavesOneShapeWithTheNewFingers()
    {
        var before = _rig.Describe();
        var copies = new[] { Old.Grip0, Old.Grip1, Old.Grip2, Old.GripAlt };
        _rig.EditDialog = dialog =>
        {
            Assert.True(dialog.Scopes[1].IsSelected);
            Steps.Do("set the fingers", () =>
            {
                dialog.Strings[2].Finger = "3";
                dialog.Strings[3].Finger = "2";
                dialog.Strings[4].Finger = "1";
            });
            Steps.Do("Apply", () => dialog.ApplyCommand.Execute(null));
        };

        _rig.Tick(copies);
        Steps.Do("Edit together", () => _rig.Panel.EditSelectedCommand.Execute(null));

        Assert.Equal(string.Join('\n',
            "0 - frets 2:9 3:8 4:11 fingers 2:3 3:2 4:1",
            "1 E5 frets 0:0 1:2 2:2 fingers 1:1 2:2",
            "2 - frets 1:0 2:2 3:2 fingers 2:1 3:2",
            "3 - frets 3:5 4:7 fingers 3:1 4:3",
            "4 A5 frets 1:0 2:2 3:2 fingers 2:1 3:2",
            "5 Dm frets 2:0 3:2 4:3 fingers 3:1 4:2",
            "6 - frets 0:1 fingers 0:1",
            "L0 chords 0@0 0@4 0@8 0@12 4@16",
            "L0 hands 0@0 0@8 0@12",
            "L1 chords 0@0 0@4 4@8 2@12 1@16",
            "L1 hands 0@0 2@12",
            "voice chords 0@20"), _rig.Describe());
        _rig.AssertChordsPlayTheirShapes();
        // Refinger each shape, merge the copies into one, then remove the unused one.
        _rig.AssertSteps(design: 3, deleteAndRedo: copies.Length + (copies.Length - 1) + 1);
        _rig.AssertOneUndoRestores(before);
    }

    [Fact]
    public void Rename_AShape_TypeTheNameInTheRow_OneStep()
    {
        var before = _rig.Describe();
        var uses = Old.AllChords.Count(c => c.TemplateId == Old.Grip0.Id);

        Steps.Do("type the name", () =>
        {
            _rig.Row(Old.Grip0).NameText = "Fmaj7";
            _rig.Row(Old.Grip0).CommitNameCommand.Execute(null);
        });

        Assert.Equal("Fmaj7", Old.Grip0.Name);
        Assert.Equal(before.Replace("0 - frets 2:9", "0 Fmaj7 frets 2:9"), _rig.Describe());
        _rig.AssertSteps(design: 1, deleteAndRedo: uses * 2);
        _rig.AssertOneUndoRestores(before);
    }

    [Fact]
    public void Rename_ToNoName_TickTheNoNameBoxOnTheRow_OneStep()
    {
        var before = _rig.Describe();
        var uses = Old.AllChords.Count(c => c.TemplateId == Old.E5.Id);

        Steps.Do("tick No name", () => _rig.Row(Old.E5).IsNoName = true);

        Assert.True(Old.E5.Unnamed);
        Assert.Equal("", Old.E5.Name);
        Assert.Equal(before.Replace("1 E5 frets", "1 - frets"), _rig.Describe());
        _rig.AssertSteps(design: 1, deleteAndRedo: uses * 2);
        _rig.AssertOneUndoRestores(before);
    }

    [Fact]
    public void Split_ASharedShapeForAPassage_SelectEditApply_GivesThePassageItsOwnShape_AndTheRestKeepsTheOld()
    {
        // A passage of three chords on the grip, on the level on screen.
        foreach (var beat in new[] { 5, 6 }) Old.Level0.Chords.Add(ChordShapesFixture.ChordOn(Old.Grip0, beat));
        var passage = Old.Level0.Chords.Where(c => c.TemplateId == Old.Grip0.Id && c.Position.BeatId >= 4).ToList();
        Assert.Equal(3, passage.Count);
        var before = _rig.Describe();
        _rig.EditDialog = dialog =>
        {
            Steps.Pick("choose Selection", () => dialog.Scopes[1].SelectCommand.Execute(null));
            Assert.True(dialog.Scopes[1].IsSelected);
            Steps.Do("change the fret", () => dialog.Strings[2].Fret = "10");
            Steps.Do("Apply", () => dialog.ApplyCommand.Execute(null));
        };

        Steps.Pick("mark the passage", () =>
        {
            foreach (var chord in passage) _rig.Chart.Editor.Selection.SelectedChords.Add(chord);
        });
        Steps.Do("Edit", () => _rig.Row(Old.Grip0).EditCommand.Execute(null));

        var made = Assert.Single(Old.Arrangement.ChordTemplates, t => t.Frets.GetValueOrDefault(2) == 10);
        Assert.NotSame(Old.Grip0, made);
        Assert.Equal(9, Old.Grip0.Frets[2]);
        Assert.All(passage, c => Assert.Equal(made.Id, c.TemplateId));
        Assert.Equal([Old.Grip0.Id], Old.Level0.Chords.Where(c => c.Position.BeatId == 0).Select(c => c.TemplateId));
        Assert.Equal(11, Old.Arrangement.ChordTemplates.Count);
        _rig.AssertChordsPlayTheirShapes();
        // Mark the passage, create a new shape, re-point each chord of it, then edit the copy.
        _rig.AssertSteps(design: 2 + 1, deleteAndRedo: 1 + 1 + passage.Count + 1);
        _rig.AssertOneUndoRestores(before);
    }
}
