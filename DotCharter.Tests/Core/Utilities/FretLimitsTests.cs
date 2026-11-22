using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     #373: the neck limits have ONE home (<see cref="FretLimits" />) and every anchor
///     producer agrees with it: generation, the suggester, the improvement pass.
/// </summary>
public sealed class FretLimitsTests
{
    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    [InlineData(23, 24)]
    [InlineData(24, 24)]
    [InlineData(99, 24)]
    public void The_lowest_anchor_is_capo_plus_one_clamped_to_the_neck_and_a_negative_capo_counts_as_none(int capo, int expected)
    {
        Assert.Equal(expected, FretLimits.MinAnchorFret(capo));
        Assert.Equal(expected, AnchorFretSuggester.MinFret(capo));
        Assert.Equal(expected, ChartImprovementService.MinAnchorFret(capo));
    }

    [Theory]
    [InlineData(1, 24)]
    [InlineData(4, 21)]
    [InlineData(7, 18)]
    [InlineData(40, 1)]
    public void An_anchor_of_a_width_must_fit_its_whole_span_on_the_neck(int width, int expected)
        => Assert.Equal(expected, FretLimits.MaxAnchorFretFor(width));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(20)]
    [InlineData(-2)]
    public void Generated_anchors_obey_the_same_floor_and_ceiling_the_inspector_enforces(int capo)
    {
        var level = new ChartLevel();
        foreach (var (beat, fret) in new[] { (0, 24), (2, 23), (4, 22), (6, 5), (8, 24) })
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), Fret = Math.Max(fret, capo + 1) });

        var anchors = new FretAnchorGenerationService(new FakeAppLogger()).GenerateAnchors(level, null, null, null, capo);

        Assert.NotEmpty(anchors);
        Assert.All(anchors, a =>
        {
            Assert.True(a.Fret >= FretLimits.MinAnchorFret(capo), $"anchor {a.Fret} below the floor at capo {capo}");
            Assert.True(a.Fret + a.Width - 1 <= FretLimits.MaxFret,
                $"anchor {a.Fret} width {a.Width} runs past fret {FretLimits.MaxFret} (CapoAnchorRangeRule.AnchorTopFretTooHigh)");
        });
    }
}
