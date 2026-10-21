using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.State.Linking;
using DotCharter.UI.Editing.Linking;
using DotCharter.UI.Editing.Tracks;
using static DotCharter.Tests.Core.Linking.LinkFixture;

namespace DotCharter.Tests.UI.Linking;

/// <summary>
///     The LINKS row's model and lane rules: one span per placement of the drawn arrangement,
///     the chip hit test, and a row that exists only while the track has link groups.
/// </summary>
public sealed class LinkLaneModelTests
{
    private static readonly RibbonContent Everything = new(DistinctTones: 2, HasSections: true, HasPhrases: true, HasLyrics: true);

    [Fact]
    public void Spans_are_the_drawn_arrangements_placements_in_time_order()
    {
        var h = LinkHarness.WithRhythm();
        var group = h.Link((h.Lead, 4), (h.Lead, 1), (h.Rhythm, 1));

        var spans = LinkLaneModel.Spans(h.Project, h.Lead);

        Assert.Equal(2, spans.Count);
        Assert.Equal(At(1, 0).GetPositionMs(h.Project.Beats), spans[0].StartMs);
        Assert.Equal(At(2, 0).GetPositionMs(h.Project.Beats), spans[0].EndMs);
        Assert.Equal(At(4, 0).GetPositionMs(h.Project.Beats), spans[1].StartMs);
        Assert.All(spans, s => Assert.Same(group, s.Group));
        Assert.All(spans, s => Assert.Equal(3, s.GroupSize));
        Assert.All(spans, s => Assert.Equal(group.Letter, s.Letter));
        Assert.Single(LinkLaneModel.Spans(h.Project, h.Rhythm));
    }

    [Fact]
    public void The_hit_test_finds_the_span_under_the_pointer_and_nothing_between_spans()
    {
        var h = LinkHarness.WithRhythm();
        h.Link((h.Lead, 1), (h.Lead, 4));
        var spans = LinkLaneModel.Spans(h.Project, h.Lead);
        var beats = h.Project.Beats;

        Assert.Equal(spans[1].Placement, LinkLaneModel.HitTest(spans, At(4, 2).GetPositionMs(beats))?.Placement);
        Assert.Equal(spans[0].Placement, LinkLaneModel.HitTest(spans, At(1, 0).GetPositionMs(beats))?.Placement);
        Assert.Null(LinkLaneModel.HitTest(spans, At(2, 0).GetPositionMs(beats)));
        Assert.Null(LinkLaneModel.HitTest(spans, At(3, 1).GetPositionMs(beats)));
    }

    [Fact]
    public void HasLinks_and_PlacementAt_follow_the_groups()
    {
        var h = LinkHarness.WithRhythm();
        Assert.False(LinkLaneModel.HasLinks(h.Project, h.Lead));

        var group = h.Link((h.Lead, 1), (h.Lead, 4), (h.Rhythm, 4));

        Assert.True(LinkLaneModel.HasLinks(h.Project, h.Lead));
        Assert.True(LinkLaneModel.HasLinks(h.Project, h.Rhythm));
        Assert.False(LinkLaneModel.HasLinks(h.Project, new Arrangement { Name = "Bass" }));
        Assert.Same(group, LinkLaneModel.PlacementAt(h.Project, h.Lead, At(4, 3))?.Group);
        Assert.Null(LinkLaneModel.PlacementAt(h.Project, h.Lead, At(3, 3)));
        Assert.Null(LinkLaneModel.PlacementAt(h.Project, h.Rhythm, At(1, 0)));
        Assert.Equal("Lead ×2, Rhythm ×1", LinkLaneModel.PerArrangement(h.Project, group));
    }

    [Fact]
    public void The_links_row_exists_only_while_the_track_has_groups_whatever_auto_hide_says()
    {
        foreach (var autoHide in new[] { true, false })
        {
            var visibility = new RibbonVisibility { AutoHideEmpty = autoHide };
            Assert.False(RibbonLaneRules.Compose(visibility, Everything).ShowLinks);
            Assert.True(RibbonLaneRules.Compose(visibility, Everything with { HasLinks = true }).ShowLinks);
        }

        var off = new RibbonVisibility { ShowLinks = false };
        Assert.False(RibbonLaneRules.Compose(off, Everything with { HasLinks = true }).ShowLinks);
        Assert.Equal(RibbonLanes.None, RibbonLanes.All & RibbonLanes.Links);
    }

    [Fact]
    public void The_links_row_sits_under_the_lyrics_takes_one_row_and_hit_tests_as_Link()
    {
        var without = RibbonLaneRules.Compose(new RibbonVisibility(), Everything);
        var with = RibbonLaneRules.Compose(new RibbonVisibility(), Everything with { HasLinks = true });

        Assert.Equal(without.TotalHeight + with.RowPitch, with.TotalHeight);
        Assert.True(with.LinkY > with.LyricY);
        Assert.Equal(EventRow.Link, with.HitTestRow(with.BarTop(with.LinkY) + 1));
        Assert.Equal(EventRow.Lyric, with.HitTestRow(with.BarTop(with.LyricY) + 1));
        Assert.NotEqual(EventRow.Link, without.HitTestRow(with.BarTop(with.LinkY) + 1));
    }
}
