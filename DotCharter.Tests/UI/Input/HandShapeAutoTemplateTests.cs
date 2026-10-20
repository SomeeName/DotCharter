using System.Collections.ObjectModel;
using DotCharter.UI.Input.Actions;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     A hand shape's grip follows its length, EOF-style.
///     <para>
///         Generating a bar over one note builds a ONE-STRING template, and
///         extending the bar over three more notes used to leave that template
///         behind — the bar covered four notes and the editor still showed a
///         single finger on one string. That is the reported case (m53).
///     </para>
///     <para>
///         The rebuild is only ever safe on a template the bar owns ALONE. A
///         <c>ChordTemplate</c> is SHARED, so rewriting a chord's template is the
///         v0.33.2 corruption — and a bar sitting on a chord's grip is describing
///         that chord on purpose.
///     </para>
/// </summary>
public sealed class HandShapeAutoTemplateTests
{
    [Fact]
    public void ATemplateNoChordAndNoOtherBarUses_IsOwnedAlone()
    {
        var shape = Shape(0, 4, 7);
        Assert.True(HandShapeAutoTemplate.OwnsItsTemplateAlone(shape, [], [shape]));
    }

    [Fact]
    public void ATemplateAChordUses_IsNotOwnedAlone()
    {
        var shape = Shape(0, 4, 7);
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 7 };
        Assert.False(HandShapeAutoTemplate.OwnsItsTemplateAlone(shape, [chord], [shape]));
    }

    [Fact]
    public void ATemplateASecondBarUses_IsNotOwnedAlone()
    {
        var shape = Shape(0, 4, 7);
        var sibling = Shape(8, 12, 7);
        Assert.False(HandShapeAutoTemplate.OwnsItsTemplateAlone(shape, [], [shape, sibling]));
    }

    [Fact]
    public void ABarWithNoTemplateAtAll_OwnsNothing() =>
        Assert.False(HandShapeAutoTemplate.OwnsItsTemplateAlone(Shape(0, 4, -1), [], []));

    [Fact]
    public void TheRebuiltGrip_TakesTheFretsTheBarNowCoversAndFingersThem()
    {
        // The m53 shape: one string, one finger, then three more notes arrive.
        var current = Template(0, (0, 7));
        current.Fingers[0] = 1;
        var seed = Template(-1, (0, 7), (1, 9), (2, 9), (3, 8));

        var rebuilt = HandShapeAutoTemplate.Rebuild(current, seed, 6);

        Assert.NotNull(rebuilt);
        Assert.Equal(4, rebuilt!.Frets.Count);
        Assert.Equal(seed.Frets, rebuilt.Frets);
        // Every fretted string is fingered — the half that was missing.
        Assert.All(rebuilt.Frets.Where(f => f.Value > 0),
            f => Assert.True(rebuilt.Fingers.ContainsKey(f.Key), $"string {f.Key} has no finger"));
        // …and Rebuild never mutates what it was given.
        Assert.Single(current.Frets);
    }

    [Fact]
    public void AHandAuthoredFingerOnAnUnmovedString_Survives()
    {
        var current = Template(0, (0, 7), (1, 9));
        current.Fingers[0] = 2; // deliberate, and not what the suggester would say
        current.Fingers[1] = 4;
        var seed = Template(-1, (0, 7), (1, 9), (2, 9));

        var rebuilt = HandShapeAutoTemplate.Rebuild(current, seed, 6)!;

        Assert.Equal(2, rebuilt.Fingers[0]);
        Assert.Equal(4, rebuilt.Fingers[1]);
    }

    [Fact]
    public void AFingerOnAStringWhoseFretMoved_IsRederived()
    {
        var current = Template(0, (0, 7));
        current.Fingers[0] = 4;
        var seed = Template(-1, (0, 3));

        var rebuilt = HandShapeAutoTemplate.Rebuild(current, seed, 6)!;

        Assert.Equal(3, rebuilt.Frets[0]);
        Assert.Equal(1, rebuilt.Fingers[0]); // a lone stopped note is the index finger
    }

    [Fact]
    public void TheNameAndArpeggioFlagSurviveTheRebuild()
    {
        var current = Template(0, (0, 7));
        current.Name = "C#min7";
        current.Arpeggio = true;

        var rebuilt = HandShapeAutoTemplate.Rebuild(current, Template(-1, (0, 7), (1, 9)), 6)!;

        Assert.Equal("C#min7", rebuilt.Name);
        Assert.True(rebuilt.Arpeggio);
        Assert.Equal(0, rebuilt.Id);
    }

    [Fact]
    public void OpenStringsNeverCarryAFinger()
    {
        var current = Template(0, (0, 5));
        current.Fingers[0] = 1;
        var rebuilt = HandShapeAutoTemplate.Rebuild(current, Template(-1, (0, 0), (1, 5)), 6)!;
        Assert.False(rebuilt.Fingers.ContainsKey(0));
    }

    [Fact]
    public void ATickThatChangesNothing_CostsNoEdit()
    {
        var current = Template(0, (0, 7), (1, 9));
        ChordTemplateFingerSetter.SetSuggestedFingers(current, 6);
        Assert.Null(HandShapeAutoTemplate.Rebuild(current, Template(-1, (0, 7), (1, 9)), 6));
    }

    /// <summary>#511: a rebuilt power chord takes the arrangement's 1·3 / 1·4 when given one.</summary>
    [Fact]
    public void ARebuiltPowerChord_TakesThePairHint()
    {
        var current = Template(0, (0, 7));
        current.Fingers[0] = 1;
        var seed = Template(-1, (0, 7), (1, 9));

        var hinted = HandShapeAutoTemplate.Rebuild(current, seed, 6, _ => PairFingering.IndexPinky)!;
        var plain = HandShapeAutoTemplate.Rebuild(current, seed, 6)!;

        Assert.Equal(4, hinted.Fingers[1]);
        Assert.Equal(3, plain.Fingers[1]);
    }

    /// <summary>#511: the wheel resize goes through the arrangement-aware rebuild, so a bar
    /// grown into a power chord follows the song's own fingering.</summary>
    [Fact]
    public void TheWheelResize_RebuildsTheGripInTheArrangementsStyle()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Input", "TimelineWheelAdjuster.cs")));

        Assert.Contains("ArrangementFingeringStyle.RebuildHandShapeGrip(arrangement,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HandShapeAutoTemplate.Rebuild(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ABarOverNothingFretted_LeavesTheGripAlone() =>
        Assert.Null(HandShapeAutoTemplate.Rebuild(Template(0, (0, 7)), null, 6));

    // ---- the action that carries the rewrite -------------------------------

    [Fact]
    public void TheResizeAction_AppliesBothHalvesAndUndoesBoth()
    {
        var templates = new ObservableCollection<ChordTemplate>();
        var grip = Template(0, (0, 7));
        grip.Fingers[0] = 1;
        templates.Add(grip);

        var shapes = new ObservableCollection<HandShape>();
        var shape = Shape(0, 4, 0);
        shapes.Add(shape);

        var after = Template(0, (0, 7), (1, 9));
        after.Fingers[0] = 1;
        after.Fingers[1] = 3;

        var action = new ResizeHandShapesAction(shapes, [shape], [shape.Clone()],
            [Shape(0, 8, 0)], templates,
            [new ResizeHandShapesAction.GripRewrite(grip, grip.Clone(), after)], [], null);

        action.Execute();
        Assert.Equal(new FractionalPosition(8), shape.EndPosition);
        Assert.Equal(2, grip.Frets.Count);
        Assert.Equal(3, grip.Fingers[1]);

        action.Undo();
        Assert.Equal(new FractionalPosition(4), shape.EndPosition);
        Assert.Single(grip.Frets);
        Assert.False(grip.Fingers.ContainsKey(1));
    }

    [Fact]
    public void AMintedTemplate_IsRemovedAgainOnUndo()
    {
        var templates = new ObservableCollection<ChordTemplate>();
        var shapes = new ObservableCollection<HandShape>();
        var shape = Shape(0, 4, -1);
        shapes.Add(shape);

        var minted = Template(0, (0, 7), (1, 9));

        var action = new ResizeHandShapesAction(shapes, [shape], [shape.Clone()], [Shape(0, 8, 0)], templates,
            [], [minted], null);

        action.Execute();
        Assert.Single(templates);
        Assert.Equal(0, shape.TemplateId);

        action.Undo();
        Assert.Empty(templates);
        Assert.Equal(-1, shape.TemplateId);
    }

    [Fact]
    public void AbsorbingALaterTick_KeepsTheGesturesOriginAndTakesItsNewestGrip()
    {
        // What makes a whole wheel sweep ONE undo step, grips included.
        var templates = new ObservableCollection<ChordTemplate>();
        var grip = Template(0, (0, 7));
        templates.Add(grip);
        var shapes = new ObservableCollection<HandShape>();
        var shape = Shape(0, 4, 0);
        shapes.Add(shape);

        var tickOne = new ResizeHandShapesAction(shapes, [shape], [shape.Clone()], [Shape(0, 6, 0)], templates,
            [new ResizeHandShapesAction.GripRewrite(grip, grip.Clone(), Template(0, (0, 7), (1, 9)))], [], null);
        tickOne.Execute();

        var tickTwo = new ResizeHandShapesAction(shapes, [shape], [shape.Clone()], [Shape(0, 9, 0)], templates,
            [new ResizeHandShapesAction.GripRewrite(grip, grip.Clone(), Template(0, (0, 7), (1, 9), (2, 9)))],
            [], null);

        Assert.True(tickOne.CanAbsorb(tickTwo));
        tickTwo.Execute();
        tickOne.Absorb(tickTwo);

        // One undo takes the bar and its grip all the way back to the start.
        tickOne.Undo();
        Assert.Equal(new FractionalPosition(4), shape.EndPosition);
        Assert.Single(grip.Frets);
    }

    /// <summary>
    ///     <c>ChordTemplate</c> has VALUE equality that EXCLUDES <c>Id</c>, so a
    ///     plain <c>Contains</c> answers "a template like this one", not "this
    ///     exact instance". A resize that mints a grip already drawn — verbatim —
    ///     by another template therefore skipped the Add, and the bar was left
    ///     pointing at an id no template in the arrangement carries. A dangling
    ///     <c>TemplateId</c> is what <c>SngFromDomainMapper</c> silently drops.
    /// </summary>
    [Fact]
    public void AMintedTemplate_IsAddedEvenWhenAValueEqualTemplateAlreadyExists()
    {
        var templates = new ObservableCollection<ChordTemplate>
        {
            // Somebody else's template, same shape, different id — ordinary chart
            // state (every format lists templates verbatim and nothing dedupes).
            Template(3, (0, 7), (1, 9))
        };

        var shapes = new ObservableCollection<HandShape>();
        var shape = Shape(0, 4, -1);
        shapes.Add(shape);

        var minted = Template(4, (0, 7), (1, 9));
        var action = new ResizeHandShapesAction(shapes, [shape], [shape.Clone()], [Shape(0, 8, 4)], templates,
            [], [minted], null);

        action.Execute();

        Assert.Equal(4, shape.TemplateId);
        Assert.Contains(templates, t => ReferenceEquals(t, minted));
    }

    /// <summary>
    ///     The undo half of the same trap: <c>Remove</c> deletes the FIRST
    ///     value-equal template, so undoing a resize that minted a grip took out
    ///     a DIFFERENT, still-referenced template and left every chord using it
    ///     dangling.
    /// </summary>
    [Fact]
    public void UndoingAMintedTemplate_RemovesThatInstanceAndNotAValueEqualNeighbour()
    {
        var templates = new ObservableCollection<ChordTemplate>();
        var incumbent = Template(3, (0, 7), (1, 9));
        templates.Add(incumbent);

        var shapes = new ObservableCollection<HandShape>();
        var shape = Shape(0, 4, -1);
        shapes.Add(shape);

        var minted = Template(4, (0, 7), (1, 9));
        var action = new ResizeHandShapesAction(shapes, [shape], [shape.Clone()], [Shape(0, 8, 4)], templates,
            [], [minted], null);

        action.Execute();
        action.Undo();

        Assert.Single(templates);
        Assert.Same(incumbent, templates[0]);
    }

    private static HandShape Shape(int start, int end, int templateId) => new()
    {
        Position = new FractionalPosition(start),
        EndPosition = new FractionalPosition(end),
        TemplateId = templateId
    };

    private static ChordTemplate Template(int id, params (int stringIndex, int fret)[] frets)
    {
        var template = new ChordTemplate { Id = id };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        return template;
    }
}
