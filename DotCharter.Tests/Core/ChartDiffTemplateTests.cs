using DotCharter.Core.Diff;

namespace DotCharter.Tests.Core;

public sealed class ChartDiffTemplateTests
{
    private static Arrangement Build()
    {
        var arrangement = new Arrangement();
        // Non-contiguous IDs, in reverse order: IDs are not list indexes.
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 42, Name = "A", Frets = new Dictionary<int, int> { [0] = 5 },
            Fingers = new Dictionary<int, int> { [0] = 2 }
        });
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 17, Name = "A", Frets = new Dictionary<int, int> { [0] = 5 },
            Fingers = new Dictionary<int, int> { [0] = 1 }
        });
        var level = new ChartLevel();
        var chord = new Chord { TemplateId = 17 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        level.Chords.Add(chord);
        level.HandShapes.Add(new HandShape { TemplateId = 17, EndPosition = new FractionalPosition(1) });
        arrangement.Levels.Add(level);
        return arrangement;
    }

    [Fact]
    public void RenumberingTemplatesAndReferencesDoesNotChangeTheChart()
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        newArrangement.ChordTemplates[0].Id = 8;
        newArrangement.ChordTemplates[1].Id = 93;
        newArrangement.Levels[0].Chords[0].TemplateId = 93;
        newArrangement.Levels[0].HandShapes[0].TemplateId = 93;

        Assert.True(ChartDiffEngine.Diff(oldArrangement, newArrangement).IsEmpty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReassigningAGripIsSeenEvenWhenTheTemplateMultisetIsUnchanged(bool swapTemplateIds)
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        if (swapTemplateIds)
        {
            newArrangement.ChordTemplates[0].Id = 17;
            newArrangement.ChordTemplates[1].Id = 42;
        }
        else
        {
            newArrangement.Levels[0].Chords[0].TemplateId = 42;
            newArrangement.Levels[0].HandShapes[0].TemplateId = 42;
        }

        var result = ChartDiffEngine.Diff(oldArrangement, newArrangement);

        var chord = Assert.Single(Assert.Single(result.Levels).Entries);
        Assert.Equal(ChartDiffChange.Changed, chord.Change);
        Assert.Equal(new[] { "Template" }, chord.ChangedFields);
        var shape = Assert.Single(result.Structure);
        Assert.Equal(ChartDiffElementKind.HandShape, shape.Kind);
        Assert.Equal(new[] { "Template" }, shape.ChangedFields);
        Assert.Equal(2, result.ChangedCount);
    }

    [Fact]
    public void ResolvingAPreviouslyDanglingReferenceIsSeen()
    {
        var oldArrangement = Build();
        var newArrangement = Build();
        oldArrangement.ChordTemplates.RemoveAt(1);

        var result = ChartDiffEngine.Diff(oldArrangement, newArrangement);

        Assert.Contains("Template", Assert.Single(Assert.Single(result.Levels).Entries).ChangedFields);
        Assert.Contains(result.Structure, e => e.Kind == ChartDiffElementKind.HandShape
                                               && e.ChangedFields.Contains("Template"));
    }
}
