using DotCharter.UI.ViewModels.CommandPalette;

namespace DotCharter.Tests.UI;

/// <summary>
/// Pure-logic tests for the command-palette fuzzy matcher (no Avalonia needed,
/// stays in the default gate).
/// </summary>
public class CommandFuzzyMatcherTests
{
    [Theory]
    [InlineData("", "Open Project")]
    [InlineData("   ", "Open Project")]
    public void Match_EmptyQuery_MatchesEverythingNeutrally(string query, string candidate)
    {
        var m = CommandFuzzyMatcher.Match(query, candidate);

        Assert.True(m.IsMatch);
        Assert.Equal(0, m.Score);
        Assert.Empty(m.MatchedIndices);
    }

    [Theory]
    [InlineData("op", "Open Project")]   // initials
    [InlineData("opj", "Open Project")]  // scattered subsequence
    [InlineData("OPEN", "Open Project")] // case-insensitive full word
    [InlineData("prj", "Open Project")]
    public void Match_Subsequence_IsMatch(string query, string candidate) => Assert.True(CommandFuzzyMatcher.Match(query, candidate).IsMatch);

    [Theory]
    [InlineData("xyz", "Open Project")]
    [InlineData("jo", "Open Project")]     // order matters: 'j' before 'o' fails
    [InlineData("openx", "Open Project")]  // trailing char with no match
    public void Match_NonSubsequence_IsNotMatch(string query, string candidate)
    {
        var m = CommandFuzzyMatcher.Match(query, candidate);

        Assert.False(m.IsMatch);
        Assert.Empty(m.MatchedIndices);
    }

    [Fact]
    public void Match_ReportsMatchedIndicesInOrder()
    {
        var m = CommandFuzzyMatcher.Match("pj", "Open Project");

        Assert.True(m.IsMatch);
        // Greedy left-to-right: 'p' at index 1 (Open), 'j' at index 8 (Project).
        Assert.Equal([1, 8], m.MatchedIndices);
    }

    [Fact]
    public void Match_PrefixBeatsScatteredMatch()
    {
        var prefix = CommandFuzzyMatcher.Match("sn", "Snap All");
        var scattered = CommandFuzzyMatcher.Match("sn", "Set Fingering None");

        Assert.True(prefix.IsMatch);
        Assert.True(scattered.IsMatch);
        Assert.True(prefix.Score > scattered.Score);
    }

    [Fact]
    public void Match_ExactEqualityScoresHighest()
    {
        var exact = CommandFuzzyMatcher.Match("Undo", "Undo");
        var prefix = CommandFuzzyMatcher.Match("Undo", "Undo Redo");

        Assert.True(exact.Score > prefix.Score);
    }

    [Fact]
    public void Match_WordBoundaryInitialsOutrankMidWord()
    {
        // "op" as word-initials of "Open Project" should beat "op" buried inside
        // a single word ("Loop").
        var initials = CommandFuzzyMatcher.Match("op", "Open Project");
        var midWord = CommandFuzzyMatcher.Match("op", "Loop");

        Assert.True(initials.IsMatch);
        Assert.True(midWord.IsMatch);
        Assert.True(initials.Score > midWord.Score);
    }

    [Fact]
    public void Rank_DropsNonMatches_AndOrdersByScore()
    {
        var items = new[] { "Zoom In", "Open Project", "Save Project", "New Project" };

        var ranked = CommandFuzzyMatcher.Rank("proj", items, s => s);

        Assert.DoesNotContain("Zoom In", ranked);
        Assert.Equal(3, ranked.Count);
        // Every result actually contains the subsequence.
        Assert.All(ranked, r => Assert.True(CommandFuzzyMatcher.Match("proj", r).IsMatch));
    }

    [Fact]
    public void Rank_EmptyQuery_KeepsSourceOrder()
    {
        var items = new[] { "B", "A", "C" };

        var ranked = CommandFuzzyMatcher.Rank("", items, s => s);

        Assert.Equal(items, ranked);
    }

    [Fact]
    public void Rank_TiesAreStableBySourceOrder()
    {
        // Two identical titles: the relative order must be preserved.
        var items = new[] { ("Copy", 0), ("Copy", 1), ("Paste", 2) };

        var ranked = CommandFuzzyMatcher.Rank("copy", items, t => t.Item1);

        Assert.Equal(2, ranked.Count);
        Assert.Equal(0, ranked[0].Item2);
        Assert.Equal(1, ranked[1].Item2);
    }

    [Fact]
    public void Rank_FallsBackToSecondarySelector_BelowTitleHits()
    {
        var items = new[]
        {
            ("Zoom In", "View"),      // title miss, category "View" matches "vie"
            ("View Something", "X")   // title hit
        };

        var ranked = CommandFuzzyMatcher.Rank("vie", items, t => t.Item1, t => t.Item2);

        Assert.Equal(2, ranked.Count);
        // The title match ranks above the category-only match.
        Assert.Equal("View Something", ranked[0].Item1);
        Assert.Equal("Zoom In", ranked[1].Item1);
    }
}
