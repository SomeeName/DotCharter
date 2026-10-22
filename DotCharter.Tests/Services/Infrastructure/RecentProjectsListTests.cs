namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     The pure MRU rules behind File ▸ Open Recent
///     (<see cref="RecentProjectsList" /> over
///     <see cref="AppConfig.RecentProjects" />): front insertion,
///     case-insensitive de-duplication, the cap, and the missing-file prune.
/// </summary>
public sealed class RecentProjectsListTests
{
    private static readonly DateTime Now = new(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Touch_InsertsAtTheFront_MostRecentFirst()
    {
        var list = new List<RecentProjectEntry>();
        RecentProjectsList.Touch(list, @"C:\charts\first.rscp", "First", Now);
        RecentProjectsList.Touch(list, @"C:\charts\second.rscp", "Second", Now.AddMinutes(1));

        Assert.Equal(2, list.Count);
        Assert.Equal("Second", list[0].DisplayName);
        Assert.Equal("First", list[1].DisplayName);
    }

    [Fact]
    public void Touch_DeduplicatesCaseInsensitively_AndMovesTheEntryUp()
    {
        var list = new List<RecentProjectEntry>();
        RecentProjectsList.Touch(list, @"C:\charts\song.rscp", "Song", Now);
        RecentProjectsList.Touch(list, @"C:\charts\other.rscp", "Other", Now.AddMinutes(1));
        RecentProjectsList.Touch(list, @"C:\CHARTS\SONG.RSCP", "Song v2", Now.AddMinutes(2));

        Assert.Equal(2, list.Count);
        Assert.Equal("Song v2", list[0].DisplayName);
        Assert.Equal(@"C:\CHARTS\SONG.RSCP", list[0].Path);
    }

    [Fact]
    public void Touch_CapsTheList()
    {
        var list = new List<RecentProjectEntry>();
        for (var i = 0; i < RecentProjectsList.Cap + 5; i++)
            RecentProjectsList.Touch(list, $@"C:\charts\song{i}.rscp", $"Song {i}", Now.AddMinutes(i));

        Assert.Equal(RecentProjectsList.Cap, list.Count);
        // The newest survives, the oldest fell off the end.
        Assert.Equal($"Song {RecentProjectsList.Cap + 4}", list[0].DisplayName);
        Assert.DoesNotContain(list, e => e.DisplayName == "Song 0");
    }

    [Fact]
    public void Touch_BlankDisplayName_FallsBackToTheFileName()
    {
        var list = new List<RecentProjectEntry>();
        RecentProjectsList.Touch(list, @"C:\charts\Master of Puppets.rscp", "  ", Now);
        Assert.Equal("Master of Puppets", list[0].DisplayName);
    }

    [Fact]
    public void Touch_BlankPath_IsIgnored()
    {
        var list = new List<RecentProjectEntry>();
        RecentProjectsList.Touch(list, "", "x", Now);
        RecentProjectsList.Touch(list, "   ", "x", Now);
        Assert.Empty(list);
    }

    [Fact]
    public void Prune_DropsEntriesWhoseFilesAreGone()
    {
        var list = new List<RecentProjectEntry>
        {
            new() { Path = @"C:\charts\alive.rscp", DisplayName = "Alive" },
            new() { Path = @"C:\charts\deleted.rscp", DisplayName = "Deleted" },
            new() { Path = "", DisplayName = "Blank" }
        };

        var pruned = RecentProjectsList.Prune(list,
            p => p.EndsWith("alive.rscp", StringComparison.Ordinal));

        var survivor = Assert.Single(pruned);
        Assert.Equal("Alive", survivor.DisplayName);
    }

    /// <summary>
    ///     THE regression. Pruning on the READ alone left dead entries in the
    ///     stored list, so they went on occupying Cap slots: with the list full and
    ///     half its projects deleted, opening one more trimmed a LIVE entry off the
    ///     end to make room. The menu shrank, permanently, and nothing said why.
    /// </summary>
    [Fact]
    public void TouchLive_DropsDeadEntries_SoTheyNeverCostALiveOneItsSlot()
    {
        var list = new List<RecentProjectEntry>();
        for (var i = 0; i < RecentProjectsList.Cap; i++)
            RecentProjectsList.Touch(list, $@"C:\charts\song{i}.rscp", $"Song {i}", Now.AddMinutes(i));

        // The five oldest are still on disk; the five most recent were deleted.
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 5; i++) alive.Add(System.IO.Path.GetFullPath($@"C:\charts\song{i}.rscp"));

        RecentProjectsList.TouchLive(list, @"C:\chartsresh.rscp", "Fresh", Now.AddHours(1), alive.Contains);

        Assert.Equal(6, list.Count);
        Assert.Equal("Fresh", list[0].DisplayName);
        foreach (var i in Enumerable.Range(0, 5))
            Assert.Contains(list, e => e.DisplayName == $"Song {i}");
        Assert.DoesNotContain(list, e => e.DisplayName == "Song 9");
    }

    /// <summary>The entry being recorded goes in whatever the probe says about it:
    /// the callers record only after the file is open or written, and a probe that
    /// answers "no" for a file we just wrote must not swallow the entry.</summary>
    [Fact]
    public void TouchLive_AlwaysRecordsTheEntryItWasGiven()
    {
        var list = new List<RecentProjectEntry>();

        RecentProjectsList.TouchLive(list, @"C:\charts
ew.rscp", "New", Now, _ => false);

        Assert.Equal("New", Assert.Single(list).DisplayName);
    }

    /// <summary>Dead entries are dropped, but the ORDER of the survivors is the MRU
    /// order they had — pruning is not a re-sort.</summary>
    [Fact]
    public void TouchLive_KeepsTheSurvivorsInTheirMruOrder()
    {
        var list = new List<RecentProjectEntry>();
        RecentProjectsList.Touch(list, @"C:\charts.rscp", "A", Now);
        RecentProjectsList.Touch(list, @"C:\charts\gone.rscp", "Gone", Now.AddMinutes(1));
        RecentProjectsList.Touch(list, @"C:\charts.rscp", "B", Now.AddMinutes(2));

        RecentProjectsList.TouchLive(list, @"C:\charts\c.rscp", "C", Now.AddMinutes(3),
            // No .dcproj sits beside any of them, so none is a shadowed Charter companion.
            p => !p.EndsWith("gone.rscp", StringComparison.OrdinalIgnoreCase) &&
                 !p.EndsWith(".dcproj", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(["C", "B", "A"], list.Select(e => e.DisplayName));
    }
}
