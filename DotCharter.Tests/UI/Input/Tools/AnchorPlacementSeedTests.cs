using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.Input.Tools;

namespace DotCharter.Tests.UI.Input.Tools;

/// <summary>
///     The pointer's anchor placement seeds the new anchor's width from the anchor in effect by
///     POSITION: a level's anchors are not kept sorted mid-session, so the last stored
///     anchor at or before the click can be an earlier one that was merely stored later.
/// </summary>
public sealed class AnchorPlacementSeedTests
{
    [Fact]
    public void A_placed_anchor_takes_its_width_from_the_latest_anchor_by_position_not_the_last_one_stored()
    {
        var project = new ChartProject();
        for (var i = 0; i < 16; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        level.Anchors.Add(new FretAnchor(new FractionalPosition(8), 9, 5));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 2, 3));
        var track = new TrackEditorContext(arrangement, new SelectionService()) { Project = project };

        var placed = FretAnchorInteractionTool.AddAnchorAt(new FractionalPosition(9), 7, track,
            new HistoryService(new FakeAppLogger()), new FakeAppLogger());

        Assert.NotNull(placed);
        Assert.Equal(7, placed.Fret);
        Assert.Equal(5, placed.Width);
    }
}
