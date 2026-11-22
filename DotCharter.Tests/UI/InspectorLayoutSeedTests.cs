using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Perf;

namespace DotCharter.Tests.UI;

public sealed class InspectorLayoutSeedTests
{
    [Fact]
    public void TheNarrowPaneSeedHasRealAnchorFindingsWithoutTemplateFingerings()
    {
        var state = new ProjectState();
        NarrowWidthLayout.SeedSong(state);
        var arrangement = state.ActiveArrangement!;
        var issues = new ArrangementValidationService(new FakeAppLogger())
            .Validate(arrangement, state.Beats, project: state.ActiveProject);
        Assert.Contains(issues, issue => issue.Type == ArrangementIssueType.NoteOutsideAnchorRange);
        Assert.True(issues.Count >= 100, "the tab-strip fixture needs a three-digit Issues badge");

        var templateIssues = new List<ArrangementIssue>();
        new TemplateAndOverlapRule().Validate(
            ArrangementValidationContext.Build(arrangement, state.Beats, null, state.ActiveProject), templateIssues);
        Assert.Empty(arrangement.ChordTemplates);
        Assert.Empty(templateIssues);
    }
}
