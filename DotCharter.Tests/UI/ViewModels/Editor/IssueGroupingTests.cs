using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class IssueGroupingTests
{
    // FirstPhraseNotEmpty / OverlappingNotes are Important; NoEndPhrase /
    // HopoIntoSameNote fall through to Warning (see ArrangementIssueType.GetSeverity).
    private static ArrangementIssue Important(string msg = "i")
        => new(ArrangementIssueType.FirstPhraseNotEmpty, msg);

    private static ArrangementIssue Warning(string msg = "w")
        => new(ArrangementIssueType.NoEndPhrase, msg);

    [Fact]
    public void Build_EmptyInput_ReturnsNoGroups()
        => Assert.Empty(IssueGrouping.Build(System.Array.Empty<ArrangementIssue>()));

    [Fact]
    public void Build_OrdersImportantBeforeWarning()
    {
        var groups = IssueGrouping.Build(new[] { Warning(), Important(), Warning() });

        Assert.Equal(2, groups.Count);
        Assert.Equal(ArrangementIssueSeverity.Important, groups[0].Severity);
        Assert.Equal(ArrangementIssueSeverity.Warning, groups[1].Severity);
    }

    [Fact]
    public void Build_OmitsEmptySeverities()
    {
        var groups = IssueGrouping.Build(new[] { Warning(), Warning() });

        var group = Assert.Single(groups);
        Assert.Equal(ArrangementIssueSeverity.Warning, group.Severity);
        Assert.Equal(2, group.Count);
        Assert.False(group.IsImportant);
    }

    [Fact]
    public void Build_PreservesInputOrderWithinGroup()
    {
        var a = Warning("a");
        var b = Warning("b");
        var c = Warning("c");

        var group = Assert.Single(IssueGrouping.Build(new[] { a, b, c }));

        Assert.Equal(new[] { "a", "b", "c" }, group.Issues.Select(i => i.Message));
    }

    [Fact]
    public void Build_CountsPerSeverityAreCorrect()
    {
        var groups = IssueGrouping.Build(new[]
        {
            Important(), Important(), Warning()
        });

        Assert.Equal(2, groups.Single(g => g.IsImportant).Count);
        Assert.Equal(1, groups.Single(g => !g.IsImportant).Count);
    }
}
