using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     The cross-tab search as a pure function over what the tabs declare —
///     the rules the old tree walk had: a row matches on its label or its
///     description, a group whose title matches shows every row in it, a
///     rowless group is reachable by its title, and a tab the user has never
///     opened contributes exactly like the one on screen.
/// </summary>
public sealed class SettingsSearchTests
{
    private static readonly SettingsSearchSource Keybinds = new("keybinds", "Keybinds",
    [
        new SettingsSearchGroup("Keybinds", "Click a field and press the new chord", []),
        new SettingsSearchGroup("Transport", "Play, tempo and the loop", [
            new SettingsSearchRow("keybinds.PlayPause", "Play / Pause"),
            new SettingsSearchRow("keybinds.ToggleMetronome", "Toggle metronome")
        ]),
        new SettingsSearchGroup("Navigation", "Moving the playhead", [
            new SettingsSearchRow("keybinds.SeekLeft", "Seek left"),
            new SettingsSearchRow("keybinds.SeekRight", "Seek right")
        ])
    ]);

    private static readonly SettingsSearchSource Updates = new("updates", "Updates",
    [
        new SettingsSearchGroup("Application updates", "Checks against GitHub releases", [
            new SettingsSearchRow("updates.checkNow", "Check for updates", "Asks GitHub for a newer release"),
            new SettingsSearchRow("updates.checkOnStartup", "Check on startup", "Runs the check a few seconds after launch")
        ])
    ]);

    [Fact]
    public void AnEmptyQuery_IsNoSearch()
    {
        Assert.Empty(SettingsSearch.Hits([Keybinds, Updates], ""));
        Assert.Empty(SettingsSearch.Hits([Keybinds, Updates], "   "));
    }

    [Fact]
    public void ARowMatchesOnItsLabel_CaseInsensitively_AndCarriesItsGroupAndTab()
    {
        var hit = Assert.Single(SettingsSearch.Hits([Keybinds, Updates], "METRONOME"));
        Assert.Equal("Toggle metronome", hit.Label);
        Assert.Equal("Transport", hit.GroupName);
        Assert.Equal("keybinds", hit.TabKey);
        Assert.Equal("Keybinds", hit.TabName);
        Assert.Equal("keybinds.ToggleMetronome", hit.RowKey);
    }

    [Fact]
    public void ARowMatchesOnItsDescription_Too()
    {
        var hit = Assert.Single(SettingsSearch.Hits([Keybinds, Updates], "after launch"));
        Assert.Equal("updates.checkOnStartup", hit.RowKey);
    }

    [Fact]
    public void AGroupTitleMatch_ShowsEveryRowInTheGroup()
    {
        var hits = SettingsSearch.Hits([Keybinds, Updates], "navigation");
        Assert.Equal(["Seek left", "Seek right"], hits.Select(h => h.Label));
        Assert.All(hits, h => Assert.Equal("Navigation", h.GroupName));
    }

    [Fact]
    public void ARowlessGroup_IsReachedByItsTitle_WithNoGroupName()
    {
        var hit = Assert.Single(SettingsSearch.Hits([Keybinds, Updates], "keybinds"));
        Assert.Equal("Keybinds", hit.Label);
        Assert.Null(hit.GroupName);
        Assert.Null(hit.RowKey);
    }

    [Fact]
    public void HitsComeInTabOrder_RowsBeforeRowlessGroups()
    {
        var tabs = new[] { Updates, Keybinds };
        var hits = SettingsSearch.Hits(tabs, "e");
        Assert.Equal("updates", hits[0].TabKey);
        Assert.Equal("keybinds", hits[^1].TabKey);
        // The rowless "Keybinds" title matches "e" and comes after that tab's rows.
        Assert.Null(hits[^1].RowKey);
    }

    [Fact]
    public void TheHitList_IsCapped()
    {
        var rows = Enumerable.Range(0, 200).Select(i => new SettingsSearchRow($"g.{i}", $"Setting {i}")).ToList();
        var big = new SettingsSearchSource("general", "General", [new SettingsSearchGroup("Everything", null, rows)]);

        Assert.Equal(SettingsSearch.MaxHits, SettingsSearch.Hits([big], "setting").Count);
    }

    [Fact]
    public void RowAndGroupMatching_AreTheSameRulesTheTabsFilterWith()
    {
        var group = Keybinds.Groups[1];
        Assert.True(SettingsSearch.GroupMatches(group, "TEMPO"));
        Assert.False(SettingsSearch.GroupMatches(group, "seek"));
        Assert.True(SettingsSearch.RowMatches(group.Rows[0], "pause"));
        Assert.False(SettingsSearch.RowMatches(group.Rows[0], "metronome"));
        Assert.False(SettingsSearch.Matches(null, "x"));
    }
}
