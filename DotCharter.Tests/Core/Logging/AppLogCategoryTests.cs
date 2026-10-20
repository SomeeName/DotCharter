namespace DotCharter.Tests.Core.Logging;

public class AppLogCategoryTests
{
    [Fact]
    public void All_IncludesPerformanceAndRendering()
    {
        Assert.True(AppLogCategory.All.HasFlag(AppLogCategory.Performance));
        Assert.True(AppLogCategory.All.HasFlag(AppLogCategory.Rendering));
    }

    [Fact]
    public void All_IncludesEveryIndividualCategory()
    {
        foreach (var value in Enum.GetValues<AppLogCategory>())
        {
            if (value is AppLogCategory.None or AppLogCategory.All) continue;
            Assert.True(AppLogCategory.All.HasFlag(value), $"All is missing {value}");
        }
    }

    [Fact]
    public void IndividualCategoriesHaveUniqueBits()
    {
        var seen = new HashSet<int>();
        foreach (var value in Enum.GetValues<AppLogCategory>())
        {
            if (value is AppLogCategory.None or AppLogCategory.All) continue;
            Assert.True(seen.Add((int)value), $"{value} has a duplicated bit value.");
        }
    }
}
