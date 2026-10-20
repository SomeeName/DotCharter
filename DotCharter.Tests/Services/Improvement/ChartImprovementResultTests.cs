namespace DotCharter.Tests.Services.Improvement;

public class ChartImprovementResultTests
{
    [Fact]
    public void ToLogDetails_EmptyResult_DescribesNoChanges()
    {
        var result = new ChartImprovementResult { ArrangementsProcessed = 0 };
        Assert.Contains("changes=none", result.ToLogDetails());
    }

    [Fact]
    public void Add_AccumulatesByLabel()
    {
        var result = new ChartImprovementResult();
        result.Add("chord sustains", 3);
        result.Add("chord sustains", 2);
        result.Add("phrase anchors", 1);

        Assert.Equal(6, result.TotalChanges);
        Assert.Equal(5, result.Changes["chord sustains"]);
        Assert.Equal(1, result.Changes["phrase anchors"]);
    }

    [Fact]
    public void Add_IgnoresNonPositiveCounts()
    {
        var result = new ChartImprovementResult();
        result.Add("x", 0);
        result.Add("y", -5);

        Assert.Equal(0, result.TotalChanges);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void ToLogDetails_IncludesLabelsAndCounts()
    {
        var result = new ChartImprovementResult { ArrangementsProcessed = 2 };
        result.Add("chord sustains", 4);

        var text = result.ToLogDetails();
        Assert.Contains("ArrangementsProcessed=2", text);
        Assert.Contains("chord sustains=4", text);
    }
}
