using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The 2D timeline's sticky labels (section, phrase, tone, measure pill, FHP fret chip): pushed
///     out by the next label of their row, and text that stays on the visible part of a chip cut off
///     at the left edge. The event layer's half runs on the real layer in
///     <c>Rendering/EventLayerPinnedLabelHitTests</c>; the source lint below pins that the other two
///     layers use the same rule.
/// </summary>
public sealed class StickyLabelTests
{
    private const float Fixed = 4f;
    private const float Width = 120f;

    [Fact]
    public void WithNothingClose_ThePinnedLabelStaysAtItsFixedX()
    {
        Assert.Equal(Fixed, StickyLabel.PinnedX(Fixed, Width, float.PositiveInfinity));
        Assert.Equal(Fixed, StickyLabel.PinnedX(Fixed, Width, 500f));
    }

    /// <summary>The claim the measure band's old guard existed for, kept: a pinned label never
    /// lands on the next one — across every position the next label passes through.</summary>
    [Fact]
    public void ThePinnedLabel_NeverOverlapsTheLabelPushingIt()
    {
        for (var next = -50f; next <= 300f; next += 0.5f)
        {
            var x = StickyLabel.PinnedX(Fixed, Width, next);
            Assert.True(x + Width <= next - StickyLabel.PushGapPx + 0.001f, $"next at {next}: pinned ends at {x + Width}");
        }
    }

    /// <summary>…and it is PUSHED, not dropped: the old measure pill vanished as soon as the next
    /// pill came within a pill's width, which at a normal zoom was most of the time.</summary>
    [Fact]
    public void ALabelWithRoomLeft_IsStillDrawn_ShovedLeft()
    {
        var x = StickyLabel.PinnedX(Fixed, Width, 60f);
        Assert.True(x < Fixed);
        Assert.True(StickyLabel.IsVisible(x, Width));
    }

    [Fact]
    public void ALabelPushedWhollyOffScreen_IsNotDrawn()
    {
        var x = StickyLabel.PinnedX(Fixed, Width, 2f);
        Assert.False(StickyLabel.IsVisible(x, Width));
    }

    [Fact]
    public void AChipOnScreen_KeepsItsTextAtItsOwnPad() => Assert.Equal(109f, StickyLabel.TextX(chipX: 100f, chipWidth: 80f, textWidth: 20f, pad: 9f));

    /// <summary>The blank grey syllable at the lyric row's left edge (live app, 2026-09-23).</summary>
    [Fact]
    public void AChipCutOffAtTheLeftEdge_KeepsItsTextOnTheVisiblePart()
    {
        var x = StickyLabel.TextX(chipX: -40f, chipWidth: 80f, textWidth: 20f, pad: 9f);
        Assert.Equal(9f, x);
    }

    [Fact]
    public void TheTextNeverRunsPastTheChipsEnd()
    {
        var x = StickyLabel.TextX(chipX: -70f, chipWidth: 80f, textWidth: 20f, pad: 9f);
        Assert.True(x + 20f <= -70f + 80f - 9f + 0.001f, $"text ends at {x + 20f}");
    }

    [Fact]
    public void TheMeasureBandAndTheFhpRow_UseTheSameStickyRule()
    {
        var layers = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Layers"));
        var background = UiSourceTree.ReadAllText(Path.Combine(layers, "BackgroundLayer.cs"));
        var anchors = UiSourceTree.ReadAllText(Path.Combine(layers, "FretAnchorLayer.cs"));
        var events = UiSourceTree.ReadAllText(Path.Combine(layers, "EventLayer.cs"));

        Assert.Contains("StickyLabel.PinnedX(", background);
        Assert.Contains("StickyLabel.PinnedX(", anchors);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(events, @"StickyLabel\.PinnedX\(").Count);
        Assert.Contains("LyricPillLayout.Layout(", events);
    }
}
