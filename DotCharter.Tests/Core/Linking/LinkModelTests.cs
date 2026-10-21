using DotCharter.Core.Linking;

namespace DotCharter.Tests.Core.Linking;

/// <summary>
///     The link model's obligations as a position-bearing, snapshotted part of the project (#88):
///     a snapshot maps each placement to ITS project's arrangement, the beat-grid tools move the
///     barlines, and a stored rule reads back as the rule that wrote it.
/// </summary>
public sealed class LinkModelTests
{
    private static ChartProject TwoParts(out LinkGroup group)
    {
        var project = LinkFixture.Project(null);
        project.Arrangements.Add(ChartProjectCloner.CloneArrangement(project.Arrangements[0]));
        group = new LinkGroup { Letter = "A", Name = "Riff", Rule = MeasureContentOptions.Detection.Header() };
        group.Placements.Add(new LinkPlacement { Arrangement = project.Arrangements[0], Position = new(4), EndPosition = new(8) });
        group.Placements.Add(new LinkPlacement { Arrangement = project.Arrangements[1], Position = new(16), EndPosition = new(20) });
        project.LinkGroups.Add(group);
        return project;
    }

    [Fact]
    public void A_clone_points_each_placement_at_the_clones_own_arrangement()
    {
        var project = TwoParts(out _);

        var clone = ChartProjectCloner.Clone(project);

        var placements = clone.LinkGroups.Single().Placements;
        Assert.Same(clone.Arrangements[0], placements[0].Arrangement);
        Assert.Same(clone.Arrangements[1], placements[1].Arrangement);
    }

    [Fact]
    public void A_snapshot_restore_keeps_the_live_group_and_placement_instances()
    {
        var project = TwoParts(out var group);
        var placement = group.Placements[1];
        var snapshot = ChartProjectCloner.Clone(project);
        snapshot.LinkGroups[0].Placements[1].Position = new FractionalPosition(20);

        ChartProjectRestorer.Restore(project, snapshot);

        Assert.Same(group, project.LinkGroups.Single());
        Assert.Same(placement, group.Placements[1]);
        Assert.Equal(new FractionalPosition(20), placement.Position);
        Assert.Same(project.Arrangements[1], placement.Arrangement);
    }

    [Fact]
    public void A_placement_whose_arrangement_left_the_project_is_not_carried_by_a_snapshot()
    {
        var project = TwoParts(out _);
        project.Arrangements.RemoveAt(1);

        var clone = ChartProjectCloner.Clone(project);

        Assert.Single(clone.LinkGroups.Single().Placements);
    }

    [Fact]
    public void Shifting_the_grid_moves_both_barlines_and_a_beat_inside_a_run_widens_only_it()
    {
        var project = TwoParts(out var group);

        ChartProjectPositions.ShiftFromBeat(project, 6, +1);

        Assert.Equal((new FractionalPosition(4), new FractionalPosition(9)), (group.Placements[0].Position, group.Placements[0].EndPosition));
        Assert.Equal((new FractionalPosition(17), new FractionalPosition(21)), (group.Placements[1].Position, group.Placements[1].EndPosition));
    }

    [Theory]
    [InlineData(SpacingGapMode.MeasureFraction, 64, 10.0)]
    [InlineData(SpacingGapMode.BeatFraction, 8, 10.0)]
    [InlineData(SpacingGapMode.Milliseconds, 64, 37.5)]
    public void A_stored_rule_reads_back_as_the_rule_that_wrote_it(SpacingGapMode mode, int divisor, double ms)
    {
        var rule = MeasureContentOptions.Detection with
        {
            Spacing = new SpacingGap(mode, divisor, ms),
            AnchorSnapToleranceMs = 4.5,
            IncludeHandShapes = false
        };

        Assert.True(MeasureContentOptions.TryParseHeader(rule.Header(), out var read));
        Assert.Equal(rule.Header(), read.Header());
        Assert.False(read.IncludeHandShapes);
        Assert.False(MeasureContentOptions.TryParseHeader("mc1;det;mf64;snap5;AHF", out _));
        Assert.False(MeasureContentOptions.TryParseHeader("", out _));
    }
}
