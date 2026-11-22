using DotCharter.Services.Editing.Linking;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #839: the same grip set again and again on the same arpeggio. A grip save in a linked measure propagates
///     to every placement as one undo step and keeps the link; identical repeated bars are suggested as one
///     link group; on unlinked shapes the page offers "Also apply to N identical hand shapes" and applies
///     nothing until that click, which is one undo step.
/// </summary>
public sealed class GripInspectorLinkTests
{
    private static readonly Dictionary<int, int> Frets = new() { [1] = 5, [2] = 5, [3] = 7 };

    [Fact]
    public void AGripSave_InALinkedMeasure_ReachesEveryPlacement_AsOneStep_AndKeepsTheLink()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new(Frets));
        var first = Arpeggio(rig, grip, 0);
        Arpeggio(rig, grip, 2);
        var rule = LinkSuggestionFinder.RuleFor(new AppConfig());
        Assert.Equal(LinkCreateRefusal.None, LinkGroupCreator.TryBuild(rig.Project,
            [new(rig.Arrangement, new(0), new(4)), new(rig.Arrangement, new(8), new(12))], "Riff", rule, out var link));
        Assert.True(rig.History.Do(link!));
        var group = Assert.Single(rig.Project.LinkGroups);
        rig.Select(SoundSelectionKind.HandShape, [first], []);

        rig.Vm.SetFinger(3, 2);
        rig.Vm.SaveFingeringCommand.Execute(null);

        Assert.Same(group, Assert.Single(rig.Project.LinkGroups));
        Assert.Equal(2, group.Placements.Count);
        var copy = rig.Level.HandShapes.Single(s => s.Position == new FractionalPosition(8));
        Assert.Equal(2, rig.Arrangement.FindChordTemplate(first.TemplateId)!.GetFinger(3));
        Assert.Equal(2, rig.Arrangement.FindChordTemplate(copy.TemplateId)!.GetFinger(3));

        rig.History.Undo();
        Assert.All(rig.Level.HandShapes, s => Assert.Equal(grip.Id, s.TemplateId));
        Assert.Same(group, Assert.Single(rig.Project.LinkGroups));
    }

    [Fact]
    public void AutoFill_InALinkedMeasure_KeepsTheLink_AndFillsEveryPlacement()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new(Frets));
        // Strummed, so the grip has no ghost strings and auto-fill has fingers to fill.
        rig.Strum(new(0), grip);
        rig.Strum(new(8), grip);
        var first = rig.Shape(grip, 0, 3);
        rig.Shape(grip, 8, 11);
        Assert.Equal(LinkCreateRefusal.None, LinkGroupCreator.TryBuild(rig.Project,
            [new(rig.Arrangement, new(0), new(4)), new(rig.Arrangement, new(8), new(12))], "Riff",
            LinkSuggestionFinder.RuleFor(new AppConfig()), out var link));
        Assert.True(rig.History.Do(link!));
        var group = Assert.Single(rig.Project.LinkGroups);
        rig.Select(SoundSelectionKind.HandShape, [first], []);

        rig.Vm.AutoFillCommand.Execute(null);

        Assert.Same(group, Assert.Single(rig.Project.LinkGroups));
        var copy = rig.Level.HandShapes.Single(s => s.Position == new FractionalPosition(8));
        Assert.NotEqual(grip.Id, first.TemplateId);
        Assert.Equal(first.TemplateId, copy.TemplateId);
    }

    [Fact]
    public void IdenticalBars_InDifferentPlaces_AreSuggestedAsOneLinkGroup()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new(Frets));
        Arpeggio(rig, grip, 0);
        rig.Level.Notes.Add(new GuitarNote { Position = new(4), StringIndex = 0, Fret = 3 });
        Arpeggio(rig, grip, 2);
        rig.Level.Notes.Add(new GuitarNote { Position = new(12), StringIndex = 5, Fret = 1 });

        var found = LinkSuggestionFinder.Detect(rig.Project, rig.Arrangement, LinkDetectionScope.All,
            LinkSuggestionFinder.RuleFor(new AppConfig()));

        var suggestion = Assert.Single(found);
        Assert.Equal([0, 2], suggestion.FirstMeasures);
    }

    [Fact]
    public void OnUnlinkedShapes_TheOfferCountsIdenticalShapes_AppliesOnlyOnItsClick_AndUndoesInOneStep()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new(Frets));
        var source = Arpeggio(rig, grip, 0);
        var other = rig.Shape(grip, 4, 7);
        rig.Level.Notes.Add(new GuitarNote { Position = new(4), StringIndex = 3, Fret = 7 });
        var twin = Arpeggio(rig, grip, 2);
        var twinAgain = Arpeggio(rig, grip, 3);
        rig.Select(SoundSelectionKind.HandShape, [source], []);
        Assert.False(rig.Vm.HasIdenticalShapes);

        rig.Vm.SetFinger(3, 2);
        rig.Vm.SaveFingeringCommand.Execute(null);

        Assert.NotEqual(grip.Id, source.TemplateId);
        Assert.Equal(2, rig.Vm.IdenticalShapeCount);
        Assert.True(rig.Vm.HasIdenticalShapes);
        Assert.Contains("2", rig.Vm.ApplyToIdenticalText);
        Assert.All(new[] { other, twin, twinAgain }, s => Assert.Equal(grip.Id, s.TemplateId));

        rig.Vm.ApplyToIdenticalShapesCommand.Execute(null);

        Assert.Equal(source.TemplateId, twin.TemplateId);
        Assert.Equal(source.TemplateId, twinAgain.TemplateId);
        Assert.Equal(grip.Id, other.TemplateId);
        Assert.False(rig.Vm.HasIdenticalShapes);

        rig.History.Undo();
        Assert.Equal(grip.Id, twin.TemplateId);
        Assert.Equal(grip.Id, twinAgain.TemplateId);
        Assert.NotEqual(grip.Id, source.TemplateId);
    }

    [Fact]
    public void TheOffer_Closes_WhenAnotherEditFollowsTheSave()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new(Frets));
        var source = Arpeggio(rig, grip, 0);
        var twin = Arpeggio(rig, grip, 2);
        rig.Select(SoundSelectionKind.HandShape, [source], []);
        rig.Vm.SetFinger(3, 2);
        rig.Vm.SaveFingeringCommand.Execute(null);
        Assert.True(rig.Vm.HasIdenticalShapes);

        rig.History.Undo();

        Assert.False(rig.Vm.HasIdenticalShapes);
        Assert.False(rig.Vm.ApplyToIdenticalShapesCommand.CanExecute(null));
        Assert.Equal(grip.Id, twin.TemplateId);
    }

    private static (GripInspectorRig Rig, HandShape Other) RigWithOffer()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new(Frets));
        var source = Arpeggio(rig, grip, 0);
        var other = Arpeggio(rig, grip, 2);
        Arpeggio(rig, grip, 3);
        rig.Select(SoundSelectionKind.HandShape, [source], []);
        rig.Vm.SetFinger(3, 2);
        rig.Vm.SaveFingeringCommand.Execute(null);
        Assert.True(rig.Vm.HasIdenticalShapes);
        return (rig, other);
    }

    [Fact]
    public void TheOffer_Closes_WhenAnotherShapeIsSelected()
    {
        var (rig, other) = RigWithOffer();

        rig.Select(SoundSelectionKind.HandShape, [other], []);

        Assert.False(rig.Vm.HasIdenticalShapes);
        Assert.False(rig.Vm.ApplyToIdenticalShapesCommand.CanExecute(null));
    }

    [Fact]
    public void TheOffer_Closes_WhenTheLevelChanges()
    {
        var (rig, _) = RigWithOffer();

        rig.Arrangement.Levels.Add(new ChartLevel());
        rig.State.ActiveLevel = rig.Arrangement.Levels[1];

        Assert.False(rig.Vm.HasIdenticalShapes);
        Assert.False(rig.Vm.ApplyToIdenticalShapesCommand.CanExecute(null));
    }

    [Fact]
    public void TheOffer_Closes_WhenTheArrangementChanges()
    {
        var (rig, _) = RigWithOffer();
        var second = new Arrangement { Levels = { new ChartLevel() } };
        rig.Project.Arrangements.Add(second);

        rig.State.ActiveArrangement = second;

        Assert.False(rig.Vm.HasIdenticalShapes);
        Assert.False(rig.Vm.ApplyToIdenticalShapesCommand.CanExecute(null));
    }

    /// <summary>A one-bar arpeggio over <paramref name="grip" /> in measure <paramref name="measure" />: four notes and
    ///     the hand shape over them.</summary>
    private static HandShape Arpeggio(GripInspectorRig rig, ChordTemplate grip, int measure)
    {
        var start = measure * 4;
        foreach (var (beat, stringIndex) in new[] { (0, 1), (1, 2), (2, 3), (3, 2) })
            rig.Level.Notes.Add(new GuitarNote { Position = new(start + beat), StringIndex = stringIndex, Fret = Frets[stringIndex] });
        return rig.Shape(grip, start, start + 3);
    }
}
