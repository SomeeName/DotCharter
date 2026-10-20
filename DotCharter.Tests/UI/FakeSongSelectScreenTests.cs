using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The redesign's fixture cannot quietly decay into an empty one.
///     <para>
///         This is the cheap half of the Song Select redesign's evidence and it
///         needs no Avalonia session, so it runs in the DEFAULT gate. It exists
///         because every layout fault the owner has reported on this screen —
///         the overlapping timestamp, the sliced tuning cell, the tag strip
///         running into the next column — is invisible against a library of
///         nothing, and a capture harness pointed at an empty list is green and
///         useless. <see cref="FakeSongSelectScreen.Library" /> is what the
///         smoke test and the gallery both draw; if someone trims it to three
///         rows, this fails instead of the pictures quietly getting easier.
///     </para>
/// </summary>
public sealed class FakeSongSelectScreenTests
{
    [Fact]
    public void Library_IsABigVariedLibrary_NotAnEmptyFixture()
    {
        var screen = FakeSongSelectScreen.Library();

        Assert.True(screen.Rows.Count >= 30,
            $"Library() offers {screen.Rows.Count} rows — the layout faults this fixture exists to expose need a full list");
        Assert.Equal($"{screen.Rows.Count} of {screen.Rows.Count} songs", screen.CountText);
        Assert.True(screen.HasRows);
        Assert.False(screen.ShowFirstRun);
        Assert.False(screen.ShowNoMatch);

        var tunings = screen.Rows.Select(r => r.TuningLabel).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(tunings.Count >= 3, $"only {tunings.Count} distinct tuning(s) — the tuning column would never be exercised");

        Assert.Contains(screen.Rows, r => r.HasBest && r.BestGradeLabel.Length > 0);
        Assert.Contains(screen.Rows, r => !r.HasBest);
        Assert.Contains(screen.Rows, r => r.IsFullCombo);
        Assert.Contains(screen.Rows, r => r.TuningLabel.Contains("capo", StringComparison.Ordinal));
        Assert.Contains(screen.Rows, r => r.TuningLabel.Length == 0);
        Assert.Contains(screen.Rows, r => r.DifficultyLabel == "—");
        Assert.Contains(screen.Rows, r => r.HasGroupHeader);
        Assert.Contains(screen.Rows, r => !r.HasGroupHeader);

        // A selection, so the detail rail has something to draw at rest.
        Assert.NotNull(screen.SelectedRow);
        Assert.True(screen.HasSelection);
        Assert.NotEmpty(screen.Arrangements);
        Assert.NotNull(screen.SelectedArrangement);
        Assert.True(screen.CanPlay);
    }

    [Fact]
    public void LongText_IsGenuinelyLong_InEveryFieldARowDraws()
    {
        var screen = FakeSongSelectScreen.LongText();

        Assert.NotEmpty(screen.Rows);
        Assert.True(screen.ActiveFilters.Count >= 6,
            $"LongText() carries {screen.ActiveFilters.Count} chips — the chip strip has to be forced to wrap");
        Assert.Contains(screen.Rows, r => r.Title.Length > 60);
        Assert.Contains(screen.Rows, r => r.Artist.Length > 40);
        Assert.Contains(screen.Rows, r => r.Album.Length > 40);
        Assert.All(screen.Rows, r => Assert.True(r.PathsSummary.Length > 20));
    }

    /// <summary>
    ///     <c>Full</c> is the list at the size the screen must stay usable at — 2,160 songs
    ///     (target-architecture.md § 3) — and every one a different song, so a capture of it
    ///     is not forty rows drawn fifty-four times.
    /// </summary>
    [Fact]
    public void Full_IsA2160SongLibrary_OfDistinctSongs()
    {
        var screen = FakeSongSelectScreen.Full();

        Assert.Equal(2_160, screen.Rows.Count);
        Assert.Equal(2_160, screen.Rows.Select(r => (r.Artist, r.Title)).Distinct().Count());
        Assert.True(screen.Rows.Select(r => r.Artist).Distinct(StringComparer.Ordinal).Count() >= 500,
            "fewer than 500 artists — the artist headings would never thin out the way a real library's do");
        Assert.Equal($"{2_160:N0} of {2_160:N0} songs", screen.CountText);
        Assert.False(screen.HasActiveFilters);
        Assert.Contains(screen.Rows, r => r.HasBest);
        Assert.Contains(screen.Rows, r => !r.HasBest);
        Assert.Contains(screen.Rows, r => r.IsFullCombo);
        Assert.Contains(screen.Rows, r => r.TuningLabel.Contains("capo", StringComparison.Ordinal));
        Assert.Contains(screen.Rows, r => r.DifficultyLabel == "—");

        var selected = Assert.IsAssignableFrom<ISongRow>(screen.SelectedRow);
        var index = screen.Rows.ToList().IndexOf(selected);
        Assert.True(index > 100, $"the selection is row {index} — it should sit mid-list, out of the first screenful");
    }

    [Fact]
    public void TheTwoEmptyStates_AreExclusive_AndSayWhichOne()
    {
        var first = FakeSongSelectScreen.Empty();
        Assert.True(first.ShowFirstRun);
        Assert.False(first.ShowNoMatch);
        Assert.False(first.HasRows);
        Assert.False(first.CanPlay);

        var none = FakeSongSelectScreen.NoMatch();
        Assert.False(none.ShowFirstRun);
        Assert.True(none.ShowNoMatch);
        Assert.False(none.HasRows);
        Assert.True(none.HasActiveFilters);
        Assert.EndsWith("of 41 songs", none.CountText, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The verbs MUTATE the fake, which is the whole reason the gallery copy
    ///     of the screen is usable rather than a still frame.
    /// </summary>
    [Fact]
    public void TheCommands_FilterRemoveAndRestore()
    {
        var screen = FakeSongSelectScreen.Library();
        var all = screen.Rows.Count;

        screen.SearchText = "metallica";
        Assert.True(screen.Rows.Count < all);
        Assert.True(screen.Rows.Count > 0);
        Assert.True(screen.HasSearchText);

        screen.ClearSearchCommand.Execute(null);
        Assert.Equal(all, screen.Rows.Count);

        var none = FakeSongSelectScreen.NoMatch();
        var chips = none.ActiveFilters.Count;
        none.ActiveFilters[0].RemoveCommand.Execute(null);
        Assert.Equal(chips - 1, none.ActiveFilters.Count);

        none.ClearFiltersCommand.Execute(null);
        Assert.False(none.HasActiveFilters);
        Assert.False(none.ShowNoMatch);
        Assert.True(none.HasRows);
    }

    /// <summary>
    ///     The column header is the one sort control and carries the column's funnel, as in the
    ///     Library: a press sorts (best first for the record), a tick narrows and becomes a chip,
    ///     and the chip's ✕ gives the rows back. Artist headings only while in artist order.
    /// </summary>
    [Fact]
    public void TheHeaders_SortAndFilter_TheWayTheLibrarysDo()
    {
        var screen = FakeSongSelectScreen.Library();
        var all = screen.Rows.Count;

        screen.RecordColumn.SortCommand.Execute(null);
        Assert.Equal("↓", screen.RecordColumn.SortGlyph);
        Assert.Equal("", screen.SongColumn.SortGlyph);
        Assert.True(screen.Rows[0].HasBest);
        Assert.False(screen.Rows[^1].HasBest);
        Assert.DoesNotContain(screen.Rows, r => r.HasGroupHeader);

        var drop = screen.TuningColumn.FilterOptions.Single(o => o.Label == "Drop tunings");
        drop.IsSelected = true;
        Assert.True(screen.TuningColumn.IsFiltered);
        Assert.All(screen.Rows, r => Assert.StartsWith("Drop", r.TuningLabel, StringComparison.Ordinal));
        var chip = Assert.Single(screen.ActiveFilters);

        chip.RemoveCommand.Execute(null);
        Assert.False(screen.HasActiveFilters);
        Assert.Equal(all, screen.Rows.Count);
    }

    /// <summary>
    ///     Picking a tile that is ALREADY picked still republishes — the shipped
    ///     rail's lesson, because the click has already flipped the visual to
    ///     unchecked by the time the command runs.
    /// </summary>
    [Fact]
    public void PickingTheSameArrangementTwice_StillRepublishesIt()
    {
        var screen = FakeSongSelectScreen.Library();
        var tile = screen.Arrangements[0];

        var republished = 0;
        screen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ISongSelectScreen.SelectedArrangement)) republished++;
        };

        tile.SelectCommand.Execute(null);
        tile.SelectCommand.Execute(null);

        Assert.Equal(2, republished);
        Assert.Same(tile, screen.SelectedArrangement);
        Assert.True(tile.IsSelected);
    }
}
