using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Fakes.Editor;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     The chord-shape workflow fakes behave as the mockups claim, so a screenshot of a variant
///     shows what the model does: a scope relights the song strip and restates Apply before
///     anything applies, hand shapes follow unless switched off, a merge and a used delete wait
///     in one confirmation bar, and Clean up counts only what is ticked or decided.
/// </summary>
public sealed class ShapeWorkflowFakeTests
{
    [Fact]
    public void Choosing_a_scope_relights_exactly_its_uses_and_restates_apply()
    {
        var screen = FakeShapeEditScreen.Default();
        Assert.Equal("Apply to every use (14)", screen.ApplyLabel);
        Assert.Equal(screen.UseBars.Count(b => b.IsUse), screen.UseBars.Count(b => b.IsReached));

        screen.Scopes[0].SelectCommand.Execute(null);

        Assert.True(screen.Scopes[0].IsSelected);
        Assert.False(screen.Scopes[2].IsSelected);
        Assert.Equal("Apply to this chord", screen.ApplyLabel);
        var reached = Assert.Single(screen.UseBars, b => b.IsReached);
        Assert.True(reached.IsOrigin);
        Assert.Contains("A new shape is made for this chord", screen.ResultText, StringComparison.Ordinal);
        Assert.Contains("13 keep", screen.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_reached_bar_is_a_use()
    {
        foreach (var variant in FakeShapeEditScreen.Variants)
        {
            var screen = variant.Create();
            Assert.All(screen.UseBars, b => Assert.True(!b.IsReached || b.IsUse, $"{variant.Name}: a reached bar without a use"));
            Assert.Single(screen.Scopes, s => s.IsSelected);
        }
    }

    [Fact]
    public void Turning_hand_shape_sync_off_warns_that_they_will_no_longer_match()
    {
        var screen = FakeShapeEditScreen.Default();
        Assert.Contains("follow the new grip", screen.HandShapeSyncText, StringComparison.Ordinal);

        screen.KeepHandShapesInSync = false;

        Assert.Contains("no longer match", screen.HandShapeSyncText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matching_grip_moves_the_uses_onto_the_existing_shape()
    {
        var screen = FakeShapeEditScreen.MatchesExisting();
        Assert.True(screen.HasMatch);
        Assert.Contains("“Am7”", screen.MatchText, StringComparison.Ordinal);
        Assert.Contains("no new shape is made", screen.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_merge_waits_in_the_bar_and_names_what_moves_where()
    {
        var screen = FakeShapePanelScreen.Default();
        var source = screen.Rows.First(r => r.NameText == "A5 (2)");
        var target = screen.Rows.First(r => r.NameText == "A5");

        source.BeginMergeCommand.Execute(null);
        Assert.True(screen.IsChoosingMergeTarget);
        Assert.True(source.IsMergeSource);
        Assert.False(screen.HasPending);

        // Only the cards with the same frets can take it.
        Assert.True(target.CanMergeHere);
        Assert.All(screen.Rows.Where(r => r.FretsText != target.FretsText), r => Assert.False(r.CanMergeHere));

        target.MergeHereCommand.Execute(null);
        Assert.True(screen.HasPending);
        Assert.False(screen.IsPendingDestructive);
        Assert.Contains("into “A5”", screen.PendingText, StringComparison.Ordinal);
        Assert.True(target.IsMergeTarget);

        screen.CancelPendingCommand.Execute(null);
        Assert.False(screen.HasPending);
        Assert.False(source.IsMergeSource);
        Assert.False(target.IsMergeTarget);
        Assert.False(target.CanMergeHere);
    }

    [Fact]
    public void Merge_is_offered_only_on_a_used_card_that_has_a_copy()
    {
        var screen = FakeShapePanelScreen.Default();

        Assert.True(screen.Rows.First(r => r.NameText == "A5 (2)").CanMerge);
        Assert.False(screen.Rows.First(r => r.NameText == "G").CanMerge);
        Assert.All(screen.Rows.Where(r => r.IsUnused), r => Assert.False(r.CanMerge));
    }

    [Fact]
    public void Ticked_rows_get_the_bulk_bar_and_merge_into_the_most_used_one()
    {
        var screen = FakeShapePanelScreen.Default();
        Assert.False(screen.HasSelection);
        var copies = screen.Rows.Where(r => r.FretsText == "x x 9 8 11 x").ToList();
        foreach (var row in copies) row.IsSelected = true;

        Assert.True(screen.HasSelection);
        Assert.StartsWith("3 shapes selected", screen.SelectionText, StringComparison.Ordinal);

        screen.MergeSelectedCommand.Execute(null);
        Assert.True(screen.HasPending);
        Assert.Contains("31 chords", screen.PendingText, StringComparison.Ordinal);
        Assert.Contains("2 copies are removed", screen.PendingText, StringComparison.Ordinal);

        screen.ClearSelectionCommand.Execute(null);
        Assert.False(screen.HasSelection);
    }

    [Fact]
    public void Deleting_a_used_shape_asks_and_an_unused_one_does_not()
    {
        var screen = FakeShapePanelScreen.Default();
        screen.Rows.First(r => r.IsUnused).DeleteCommand.Execute(null);
        Assert.False(screen.HasPending);

        screen.Rows.First(r => !r.IsUnused).DeleteCommand.Execute(null);
        Assert.True(screen.HasPending);
        Assert.True(screen.IsPendingDestructive);
    }

    [Fact]
    public void No_name_clears_the_name_and_search_filters_by_name_or_frets()
    {
        var screen = FakeShapePanelScreen.Default();
        var row = screen.Rows.First(r => r.NameText == "Am");
        row.IsNoName = true;
        Assert.Equal("", row.NameText);

        screen.SearchText = "9 8 11";
        Assert.All(screen.Rows, r => Assert.Contains("9 8 11", r.FretsText, StringComparison.Ordinal));
        Assert.Equal(3, screen.Rows.Count);
    }

    [Fact]
    public void Cleanup_counts_only_what_is_ticked_or_decided()
    {
        var screen = FakeShapeCleanupScreen.Default();
        var all = screen.Duplicates.Count + screen.Unused.Count;
        Assert.Equal($"Apply {all} changes", screen.ApplyLabel);

        screen.Unused[0].IsIncluded = false;
        screen.NearDuplicates[0].Choices[1].SelectCommand.Execute(null);

        Assert.Equal($"Apply {all} changes", screen.ApplyLabel);
        Assert.Single(screen.NearDuplicates[0].Choices, c => c.IsSelected);
        Assert.True(screen.NearDuplicates[0].Choices[1].IsSelected);
    }

    [Fact]
    public void An_empty_cleanup_is_tidy()
    {
        IShapeCleanupScreen screen = FakeShapeCleanupScreen.Empty();
        Assert.True(screen.IsTidy);
        Assert.False(screen.HasDuplicates || screen.HasUnused || screen.HasNearDuplicates);
    }
}
