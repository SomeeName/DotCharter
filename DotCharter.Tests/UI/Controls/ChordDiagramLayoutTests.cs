using Avalonia;
using Avalonia.Media;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The Chord shapes list's diagram arithmetic (#567): the fret window, and the three rules the
///     owner's screenshot broke — dots that touched, fret numbers that ran together ("56789") and
///     open / muted strings drawn as a stack of grey dots.
/// </summary>
public sealed class ChordDiagramLayoutTests
{
    public static TheoryData<double, double, int> Sizes =>
    [
        (56, 52, 6), (76, 82, 6), (76, 82, 4), (76, 82, 8), (140, 104, 6), (40, 40, 7)
    ];

    [Theory]
    [InlineData(new[] { 0, 2, 2, 1, 0 }, 1, 4)] // Am: from the nut
    [InlineData(new[] { 8, 6 }, 6, 4)] // x x 8 6 x x: up the neck, from its lowest fret
    [InlineData(new[] { 5, 7, 7, 9 }, 5, 5)] // a five-fret stretch keeps every fret
    [InlineData(new[] { 0, 0, 0 }, 1, 4)] // all open
    [InlineData(new[] { 4, 3 }, 1, 4)] // still fits under the nut
    public void TheWindow_StartsAtTheNutWhileItFits_ElseAtTheLowestFret(int[] frets, int start, int rows) =>
        Assert.Equal((start, rows), ChordDiagramLayout.WindowFor(frets));

    [Theory]
    [MemberData(nameof(Sizes))]
    public void AtEverySize_DotsNeverTouch_AndStayInside(double width, double height, int strings)
    {
        var layout = ChordDiagramLayout.For(new Size(width, height), strings, [8, 6, 9], labelWidth: 14);

        Assert.True(layout.DotRadius * 2 < layout.StringSpacing, $"dots {layout.DotRadius * 2:0.#} wide, strings {layout.StringSpacing:0.#} apart");
        Assert.True(layout.DotRadius * 2 < layout.FretSpacing, $"dots {layout.DotRadius * 2:0.#} tall, frets {layout.FretSpacing:0.#} apart");
        Assert.True(layout.GridLeft - layout.DotRadius >= 0, "the lowest string's dot runs off the left edge");
        Assert.True(layout.GridRight + layout.DotRadius <= width + 1e-9, "the highest string's dot runs off the right edge");
        Assert.True(layout.GridBottom <= height, "the grid runs off the bottom");
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void OnlyTheFirstFretIsNamed_InItsOwnBand_AndTheMarkersSitAboveTheNut(double width, double height, int strings)
    {
        var layout = ChordDiagramLayout.For(new Size(width, height), strings, [8, 6], labelWidth: 14);

        Assert.Equal("6fr", layout.FretLabel);
        Assert.False(layout.ShowsNut);
        Assert.True(layout.LabelRect.Right <= layout.GridLeft - layout.DotRadius,
            $"the label ends at {layout.LabelRect.Right:0.#}, the first string's dots start at {layout.GridLeft - layout.DotRadius:0.#}");
        Assert.True(layout.MarkerY < layout.GridTop, "the o / x row is not above the grid");

        var nut = ChordDiagramLayout.For(new Size(width, height), strings, [0, 2, 2, 1, 0], labelWidth: 14);
        Assert.True(nut.ShowsNut);
        Assert.Equal("", nut.FretLabel);
        Assert.Equal(default, nut.LabelRect);
    }

    [Fact]
    public void AFingerNumber_IsPrintedOnlyInADotBigEnoughToHoldIt()
    {
        Assert.True(ChordDiagramLayout.For(new Size(76, 82), 6, [3, 2], 0).ShowsFingerNumbers);
        Assert.False(ChordDiagramLayout.For(new Size(40, 40), 6, [3, 2], 0).ShowsFingerNumbers);
    }

    [Fact]
    public void TheFingerNumber_IsDarkOnTheLightStrings_AndWhiteOnTheDarkOnes()
    {
        Assert.Equal(Colors.White, ChordDiagramControl.FingerBrushOn(Colors.DarkRed).Color);
        Assert.Equal(Colors.White, ChordDiagramControl.FingerBrushOn(Color.FromRgb(40, 110, 230)).Color);
        Assert.NotEqual(Colors.White, ChordDiagramControl.FingerBrushOn(Colors.Gold).Color);
        Assert.NotEqual(Colors.White, ChordDiagramControl.FingerBrushOn(Color.FromRgb(90, 220, 90)).Color);
    }

    [Theory]
    [InlineData(320, 340, 0, 1)]
    [InlineData(600, 340, 0, 1)]
    [InlineData(680, 340, 0, 2)]
    [InlineData(700, 340, 12, 2)]
    [InlineData(1100, 340, 12, 3)]
    [InlineData(double.PositiveInfinity, 340, 0, 1)]
    public void TheListBecomesAGrid_OnlyWhereTwoColumnsFit(double width, double min, double spacing, int columns) =>
        Assert.Equal(columns, AdaptiveColumnsPanel.ColumnsFor(width, min, spacing));

    [Fact]
    public void AnUnnamedShape_IsCalledByItsFrets()
    {
        var template = new ChordTemplate { Name = " ", Frets = { [2] = 8, [3] = 6 } };
        Assert.True(ChordShapeNames.IsUnnamed(template));
        Assert.Equal("x x 8 6 x x", ChordShapeNames.DisplayName(template, 6));

        template.Name = "Bb5";
        Assert.Equal("Bb5", ChordShapeNames.DisplayName(template, 6));
        Assert.Equal("x x 8 6", ChordShapeNames.FretPattern(template, 4));
    }
}
