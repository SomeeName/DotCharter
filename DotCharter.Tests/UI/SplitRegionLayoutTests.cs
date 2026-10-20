using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
/// Pure split/parallel-region board layout (ABI v23). Mirrors the native routing
/// contract: positional regions, the exact branch→row clamp, bypassed-splitter
/// collapse, missing-merger auto-merge, and no-nesting. No Avalonia instance.
/// </summary>
public sealed class SplitRegionLayoutTests
{
    private static BoardNodeInfo N(string type, int branch = 0, bool bypass = false, int branches = 2)
        => new(type, bypass, branch, branches);

    private static BoardLayout Layout(params BoardNodeInfo[] nodes) => SplitRegionLayout.Compute(nodes);

    // ---- branch → row clamp (the exact native clamp) ------------------------

    [Theory]
    [InlineData(0, 2, 1)]  // unassigned → branch 1
    [InlineData(1, 2, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(3, 2, 2)]  // clamps down to enabled branch count
    [InlineData(3, 3, 3)]
    [InlineData(-5, 3, 1)] // max(tag,1)
    public void RowForBranch_matches_native_clamp(int branch, int rowCount, int expected)
        => Assert.Equal(expected, SplitRegionLayout.RowForBranch(branch, rowCount));

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(1, 2)] // enabledBranches below range clamps up
    [InlineData(9, 3)] // above range clamps down
    public void RowCountFor_clamps_enabled_branches_to_2_3(int enabled, int expected)
        => Assert.Equal(expected, SplitRegionLayout.RowCountFor(N("splitter", branches: enabled)));

    // ---- linear chains ------------------------------------------------------

    [Fact]
    public void LinearChain_is_all_pedal_columns_in_row_one()
    {
        var layout = Layout(N("overdrive"), N("amp"), N("cab"));

        Assert.Equal(1, layout.RegionRowCount);
        Assert.All(layout.Columns, c => Assert.Equal(BoardColumnKind.Pedal, c.Kind));
        Assert.Equal(new[] { 0, 1, 2 }, layout.Columns.Select(c => c.NodeIndex).ToArray());
    }

    [Fact]
    public void EmptyChain_yields_no_columns()
    {
        var layout = Layout();
        Assert.Empty(layout.Columns);
        Assert.Equal(1, layout.RegionRowCount);
    }

    // ---- a basic region -----------------------------------------------------

    [Fact]
    public void Region_fans_body_nodes_into_branch_rows_between_junctions()
    {
        // od | SPLIT(2) | a@br1  b@br2  c@br1 | MERGE | reverb
        var layout = Layout(
            N("overdrive"),
            N("splitter", branches: 2),
            N("distortion", branch: 1),
            N("chorus", branch: 2),
            N("delay", branch: 1),
            N("merger"),
            N("reverb"));

        Assert.Equal(2, layout.RegionRowCount);

        var kinds = layout.Columns.Select(c => c.Kind).ToArray();
        Assert.Equal(
        [
            BoardColumnKind.Pedal,     // overdrive
            BoardColumnKind.Junction,  // splitter
            BoardColumnKind.Region,    // fanned body
            BoardColumnKind.Junction,  // merger
            BoardColumnKind.Pedal      // reverb
        ], kinds);

        var region = layout.Columns.Single(c => c.Kind == BoardColumnKind.Region).Region!;
        Assert.Equal(2, region.RowCount);
        Assert.False(region.AutoMergeAtEnd);
        Assert.Equal(new[] { 2, 4 }, region.Rows[0].ToArray()); // branch 1: distortion, delay
        Assert.Equal(new[] { 3 }, region.Rows[1].ToArray());    // branch 2: chorus
    }

    [Fact]
    public void BranchTag_clamps_down_to_enabled_branches()
    {
        // enabledBranches = 2, a body node tagged branch 3 clamps into row 2.
        var layout = Layout(
            N("splitter", branches: 2),
            N("distortion", branch: 3),
            N("merger"));

        var region = layout.Columns.Single(c => c.Kind == BoardColumnKind.Region).Region!;
        Assert.Empty(region.Rows[0]);
        Assert.Equal(new[] { 1 }, region.Rows[1].ToArray());
    }

    [Fact]
    public void ThreeBranch_splitter_opens_three_rows()
    {
        var layout = Layout(
            N("splitter", branches: 3),
            N("a", branch: 1), N("b", branch: 2), N("c", branch: 3),
            N("merger"));

        var region = layout.Columns.Single(c => c.Kind == BoardColumnKind.Region).Region!;
        Assert.Equal(3, region.RowCount);
        Assert.Equal(new[] { 1 }, region.Rows[0].ToArray());
        Assert.Equal(new[] { 2 }, region.Rows[1].ToArray());
        Assert.Equal(new[] { 3 }, region.Rows[2].ToArray());
    }

    // ---- bypassed splitter collapses to linear ------------------------------

    [Fact]
    public void BypassedSplitter_collapses_region_to_linear_row_one()
    {
        var layout = Layout(
            N("splitter", bypass: true, branches: 2),
            N("distortion", branch: 2),
            N("merger"));

        Assert.Equal(1, layout.RegionRowCount);
        Assert.All(layout.Columns, c => Assert.Equal(BoardColumnKind.Pedal, c.Kind));
    }

    // ---- missing / bypassed merger auto-merges at chain end -----------------

    [Fact]
    public void MissingMerger_runs_region_to_chain_end_and_flags_auto_merge()
    {
        var layout = Layout(
            N("overdrive"),
            N("splitter", branches: 2),
            N("distortion", branch: 1),
            N("chorus", branch: 2));

        var region = layout.Columns.Single(c => c.Kind == BoardColumnKind.Region).Region!;
        Assert.True(region.AutoMergeAtEnd);
        // No trailing merger junction, no trailing pedals.
        Assert.DoesNotContain(layout.Columns.Skip(2), c => c.Kind == BoardColumnKind.Junction);
        Assert.Equal(new[] { 2 }, region.Rows[0].ToArray());
        Assert.Equal(new[] { 3 }, region.Rows[1].ToArray());
    }

    [Fact]
    public void BypassedMerger_is_ignored_and_region_auto_merges()
    {
        var layout = Layout(
            N("splitter", branches: 2),
            N("distortion", branch: 1),
            N("merger", bypass: true), // inert — a wire in branch 1's row
            N("chorus", branch: 2));

        var region = layout.Columns.Single(c => c.Kind == BoardColumnKind.Region).Region!;
        Assert.True(region.AutoMergeAtEnd);
        // the bypassed merger is a body node in branch 1's row (index 2)
        Assert.Equal(new[] { 1, 2 }, region.Rows[0].ToArray());
        Assert.Equal(new[] { 3 }, region.Rows[1].ToArray());
    }

    // ---- no nesting: a second splitter inside the region is a wire ----------

    [Fact]
    public void SecondSplitterInsideRegion_is_a_plain_body_pedal()
    {
        var layout = Layout(
            N("splitter", branches: 2),
            N("distortion", branch: 1),
            N("splitter", branch: 2, branches: 3), // inside the open region → a wire
            N("merger"));

        // Only ONE region, opened by the FIRST splitter (2 rows, not 3).
        Assert.Equal(2, layout.RegionRowCount);
        var region = layout.Columns.Single(c => c.Kind == BoardColumnKind.Region).Region!;
        Assert.Equal(new[] { 1 }, region.Rows[0].ToArray()); // distortion
        Assert.Equal(new[] { 2 }, region.Rows[1].ToArray()); // the inner splitter, as a wire
    }

    // ---- the first non-bypassed splitter wins -------------------------------

    [Fact]
    public void FirstNonBypassedSplitter_opens_the_region_skipping_bypassed_ones()
    {
        var layout = Layout(
            N("splitter", bypass: true, branches: 3), // skipped
            N("overdrive"),
            N("splitter", branches: 2),               // opens the region
            N("distortion", branch: 2),
            N("merger"));

        Assert.Equal(2, layout.RegionRowCount);
        // index 0 (bypassed splitter) + index 1 (overdrive) are linear pedals.
        Assert.Equal(BoardColumnKind.Pedal, layout.Columns[0].Kind);
        Assert.Equal(BoardColumnKind.Pedal, layout.Columns[1].Kind);
        Assert.Equal(BoardColumnKind.Junction, layout.Columns[2].Kind); // the live splitter
    }

    // ---- RowForY drop-to-row picker -----------------------------------------

    [Theory]
    [InlineData(5, 100, 2, 1)]   // top band
    [InlineData(60, 100, 2, 2)]  // bottom band
    [InlineData(10, 90, 3, 1)]
    [InlineData(40, 90, 3, 2)]
    [InlineData(80, 90, 3, 3)]
    [InlineData(200, 100, 2, 2)] // below the body clamps to last row
    [InlineData(-5, 100, 2, 1)]  // above clamps to first row
    public void RowForY_picks_the_band(double y, double height, int rowCount, int expected)
        => Assert.Equal(expected, SplitRegionLayout.RowForY(y, height, rowCount));

    [Fact]
    public void RowForY_single_row_or_bad_height_is_row_one()
    {
        Assert.Equal(1, SplitRegionLayout.RowForY(50, 100, 1));
        Assert.Equal(1, SplitRegionLayout.RowForY(50, 0, 2));
    }
}
