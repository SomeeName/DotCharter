using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class GripRunTests
{
    [Fact]
    public void RunInChartOrderNavigatesFromTheCurrentShapeAtBothEnds()
    {
        var shapes = Enumerable.Range(0, 10).Select(index => new HandShape { Position = new(index) }).ToArray();
        var arrangement = new Arrangement { StringCount = 6 };

        var middle = new GripRun(shapes.Reverse(), shapes[5], arrangement);
        Assert.Equal(6, middle.Position);
        Assert.Equal(10, middle.Count);
        Assert.Same(shapes[4], middle.Previous);
        Assert.Same(shapes[6], middle.Next);
        Assert.True(middle.Describe(shapes[5], "Unnamed").IsCurrent);
        Assert.Equal(7, middle.Describe(shapes[6], "Unnamed").Number);

        var first = new GripRun(shapes.Reverse(), shapes[0], arrangement);
        Assert.Null(first.Previous);
        Assert.Same(shapes[1], first.Next);

        var last = new GripRun(shapes.Reverse(), shapes[^1], arrangement);
        Assert.Null(last.Next);
        Assert.Same(shapes[^2], last.Previous);
    }

    [Fact]
    public void DescriptionsUseRealFretTextAndFlagOnlyIncompleteFingerings()
    {
        var shapes = Enumerable.Range(0, 8)
            .Select(index => new HandShape { Position = new(index), TemplateId = index % 2 }).ToArray();
        var frets = Enumerable.Range(0, 8).ToDictionary(index => index, _ => 12);
        var arrangement = new Arrangement
        {
            StringCount = 8,
            ChordTemplates =
            [
                new ChordTemplate { Id = 0, Name = "F", Frets = frets, Fingerless = true },
                new ChordTemplate { Id = 1, Name = "G", Frets = frets }
            ]
        };
        var run = new GripRun(shapes, shapes[2], arrangement);
        var items = shapes.Select(shape => run.Describe(shape, "Unnamed")).ToArray();

        Assert.Equal("12 12 12 12 12 12 12 12", Assert.Single(items, item => item.IsCurrent).Frets);
        Assert.All(items, item => Assert.Equal(item.Shape.TemplateId == 1, item.IsMissing));
        Assert.Equal("G", items[3].Name);
        Assert.Same(shapes[3], run.NextMissing);
        Assert.Null(new GripRun(shapes, shapes[^1], arrangement).NextMissing);
    }

    [Fact]
    public void ChartWithoutCurrentShapeStartsAtTheFirstShapeAndSupportsAnEmptyChart()
    {
        var shape = new HandShape { Position = new(3) };
        var arrangement = new Arrangement { StringCount = 6 };
        var run = new GripRun([shape], null, arrangement);
        Assert.Equal(0, run.Position);
        Assert.Null(run.Previous);
        Assert.Same(shape, run.Next);
        Assert.False(run.Describe(shape, "Unnamed").IsCurrent);
        Assert.Equal("Unnamed", run.Describe(shape, "Unnamed").Name);

        var empty = new GripRun([], null, arrangement);
        Assert.Null(empty.Next);
    }

    [Fact]
    public void AChordOutsideAShapeAdvancesFromItsTimelinePosition()
    {
        var first = new HandShape { Position = new(2), TemplateId = 0 };
        var last = new HandShape { Position = new(6), TemplateId = 0 };
        var arrangement = new Arrangement { ChordTemplates = [new() { Id = 0, Frets = { [0] = 5 } }] };

        var gap = new GripRun([last, first], null, arrangement, new FractionalPosition(4));
        Assert.Same(first, gap.Previous);
        Assert.Same(last, gap.Next);
        Assert.Same(last, gap.NextMissing);

        var after = new GripRun([last, first], null, arrangement, new FractionalPosition(7));
        Assert.Same(last, after.Previous);
        Assert.Null(after.Next);
        Assert.Null(after.NextMissing);
    }
}
