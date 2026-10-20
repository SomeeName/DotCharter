using static DotCharter.UI.Controls.Layers.Helpers.BendLabelPlacement;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     Where a bend's step label goes on the 2D timeline. The live app (2026-09-23) drew "↗Full"
///     over its own note head, so "14" read as "1", and drew "↗1½" and "↗Full" of one note on top of
///     each other. The renderer needs a GPU batcher, so the rules are pinned here on the pure helper,
///     and a source lint pins that the renderer uses it.
/// </summary>
public sealed class BendLabelPlacementTests
{
    private const float HeadRight = 110f;

    [Fact]
    public void ALabelAtTheHead_StartsPastTheHeadsRim()
    {
        var x = LabelX(bendX: 100f, HeadRight, sustainEndX: 400f, hasSustain: true, boxWidth: 40f);
        Assert.True(x >= HeadRight, $"the label starts at {x}, over the head that ends at {HeadRight}");
    }

    [Fact]
    public void ALabelLaterInTheSustain_StaysAtItsBendPoint() => Assert.Equal(250f, LabelX(bendX: 250f, HeadRight, sustainEndX: 400f, hasSustain: true, boxWidth: 40f));

    [Fact]
    public void ALabelNearTheSustainEnd_IsPulledBackInsideTheNote()
    {
        var x = LabelX(bendX: 390f, HeadRight, sustainEndX: 400f, hasSustain: true, boxWidth: 40f);
        Assert.Equal(360f, x);
    }

    /// <summary>A sustain too short for the label: the fret number wins over "inside the note".</summary>
    [Fact]
    public void AShortSustain_NeverPullsTheLabelOverTheHead()
    {
        var x = LabelX(bendX: 115f, HeadRight, sustainEndX: 130f, hasSustain: true, boxWidth: 40f);
        Assert.True(x >= HeadRight, $"the label was pulled back to {x}, over the head that ends at {HeadRight}");
    }

    [Fact]
    public void TheFirstLabelOfANote_NeverCollides() => Assert.Equal(Collision.None, Resolve(float.NegativeInfinity, 0f, labelX: 0f, step: 1f));

    [Fact]
    public void ALabelClearOfTheLastOne_IsDrawnBesideIt() => Assert.Equal(Collision.None, Resolve(previousRight: 150f, previousStep: 1f, labelX: 160f, step: 2f));

    [Fact]
    public void TwoOverlappingLabels_KeepTheBiggerStep()
    {
        Assert.Equal(Collision.ReplacePrevious, Resolve(previousRight: 150f, previousStep: 1.5f, labelX: 140f, step: 2f));
        Assert.Equal(Collision.SkipThis, Resolve(previousRight: 150f, previousStep: 2f, labelX: 140f, step: 1f));
    }

    /// <summary>Zoomed out, "↗0.3" ran onto the next note's head and hid its fret (live app,
    /// 2026-09-23): a label that cannot end before the next head on its string is not drawn.</summary>
    [Fact]
    public void ALabelRunningIntoTheNextHead_IsNotDrawn()
    {
        const float radius = 12f;
        Assert.True(FitsBeforeNextHead(labelX: 120f, boxWidth: 40f, nextHeadX: 200f, headRadius: radius));
        Assert.False(FitsBeforeNextHead(labelX: 120f, boxWidth: 40f, nextHeadX: 170f, headRadius: radius));
        Assert.True(FitsBeforeNextHead(labelX: 120f, boxWidth: 40f, nextHeadX: float.PositiveInfinity, headRadius: radius));
    }

    /// <summary>The curve was pure white and read as a selection underline under a slight bend.</summary>
    [Fact]
    public void TheCurve_IsALightTintOfItsString_NotWhite()
    {
        var green = Avalonia.Media.Color.FromRgb(40, 180, 60);
        var ink = CurveColor(green, 255);

        Assert.NotEqual(Avalonia.Media.Color.FromRgb(255, 255, 255), Avalonia.Media.Color.FromRgb(ink.R, ink.G, ink.B));
        Assert.True(ink.G > ink.R && ink.G > ink.B, $"the tint lost its string's hue: {ink}");
        Assert.True(ink.R > green.R && ink.G > green.G && ink.B > green.B, "the tint is not lighter than the string");
        Assert.Equal(255, ink.A);
    }

    [Fact]
    public void TheHeadRenderer_DrawsTheWholeCurveInTheTint()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Layers", "Helpers",
            "HighwayNoteHeadRenderer.cs")));
        Assert.Contains("var bendInk = BendLabelPlacement.CurveColor(c, alpha);", source);
        Assert.Equal(3, source.Split("bendInk);").Length - 1);
    }

    [Fact]
    public void TheHeadRenderer_PlacesEveryBendLabelThroughTheHelper()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Layers", "Helpers",
            "HighwayNoteHeadRenderer.cs")));
        Assert.Contains("BendLabelPlacement.LabelX(", source);
        Assert.Contains("BendLabelPlacement.ResolveAgainst(", source);
    }

    /// <summary>
    ///     The review's three-point case: L1 at 100–130 and L2 at 134–160 both stand; L3, the bigger
    ///     step and wider, is pulled left to 130 by the sustain clamp. Checked against L2 alone it
    ///     replaced L2 and sat on L1's edge. Against all standing labels it replaces BOTH, so no
    ///     two labels touch.
    /// </summary>
    [Fact]
    public void AReplacingLabel_IsCheckedAgainstEveryStandingLabel_NotOnlyTheLast()
    {
        float[] rights = [130f, 160f];
        float[] steps = [1f, 1.5f];

        var collision = ResolveAgainst(rights, steps, labelX: 130f, step: 2f, out var keep);

        Assert.Equal(Collision.ReplacePrevious, collision);
        Assert.Equal(0, keep); // both earlier labels give way
    }

    /// <summary>…and if any label it reaches is the bigger step, this one is not drawn and all stand.</summary>
    [Fact]
    public void ALabelReachingABiggerStep_IsSkipped_AndEveryStandingLabelStays()
    {
        float[] rights = [130f, 160f];
        float[] steps = [2.5f, 1f];

        var collision = ResolveAgainst(rights, steps, labelX: 130f, step: 2f, out var keep);

        Assert.Equal(Collision.SkipThis, collision);
        Assert.Equal(2, keep);
    }

    /// <summary>A label clear of every standing one keeps them all; one hitting only the last replaces it alone.</summary>
    [Fact]
    public void ResolveAgainst_MatchesResolve_ForTheLastLabelAlone()
    {
        float[] rights = [130f, 160f];
        float[] steps = [1f, 1.5f];

        Assert.Equal(Collision.None, ResolveAgainst(rights, steps, labelX: 170f, step: 1f, out var keepAll));
        Assert.Equal(2, keepAll);
        Assert.Equal(Collision.ReplacePrevious, ResolveAgainst(rights, steps, labelX: 150f, step: 2f, out var keepOne));
        Assert.Equal(1, keepOne);
        Assert.Equal(Collision.None, ResolveAgainst([], [], labelX: 0f, step: 1f, out var keepNone));
        Assert.Equal(0, keepNone);
    }
}
