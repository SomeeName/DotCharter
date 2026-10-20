using DotCharter.UI.Controls;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     "The chart" of an arrangement is its HARDEST level — the whole song at
///     its hardest — never <c>Levels[0]</c>, the thinnest reduction. That
///     distinction only became visible once a psarc import kept its whole
///     ladder (#323).
/// </summary>
public sealed class ChartLevelsTests
{
    [Fact]
    public void Hardest_is_the_highest_difficulty_whatever_the_collection_order()
    {
        var arrangement = new Arrangement();
        Assert.Null(ChartLevels.Hardest(arrangement));
        Assert.Null(ChartLevels.Hardest(null));

        var easy = new ChartLevel { DifficultyIndex = 0 };
        var hard = new ChartLevel { DifficultyIndex = 7 };
        var mid = new ChartLevel { DifficultyIndex = 3 };
        arrangement.Levels.Add(easy);
        arrangement.Levels.Add(hard);
        arrangement.Levels.Add(mid);

        Assert.Same(hard, ChartLevels.Hardest(arrangement));
    }

    /// <summary>The 2D timeline drew <c>Levels[0]</c> of the ACTIVE arrangement,
    /// so a levelled chart showed its easiest level while every edit landed in
    /// the active (hardest) one. The active arrangement must draw the editor's
    /// active level (null here = "use PlacedNotes/Anchors/…"); another
    /// arrangement's row draws its hardest level.</summary>
    [Fact]
    public void The_timeline_draws_the_active_level_and_another_rows_hardest()
    {
        var arrangement = new Arrangement();
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        var hard = new ChartLevel { DifficultyIndex = 1 };
        arrangement.Levels.Add(hard);

        Assert.Null(ChartRenderer.SnapshotSourceLevel(arrangement, isOverride: false));
        Assert.Same(hard, ChartRenderer.SnapshotSourceLevel(arrangement, isOverride: true));
    }
}
