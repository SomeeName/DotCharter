using System.Globalization;
using DotCharter.UI.Converters;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Issues tab's card captions (inspector design D) keep what the old rail entries said — a count, a
///     needs-a-look dot.
/// </summary>
public sealed class IssueRowConvertersTests
{
    private static object? Title(params object?[] values) =>
        IssueRowConverters.PageTitle.Convert([.. values], typeof(string), null, CultureInfo.InvariantCulture);

    [Fact]
    public void APageTitle_CarriesACountOrADot()
    {
        Assert.Equal("Findings 12", Title("Findings", 12));
        Assert.Equal("Findings", Title("Findings", 0));
        Assert.Equal("Project •", Title("Project", true));
        Assert.Equal("Project", Title("Project", false));
        Assert.Null(Title(null, 3));
    }
}
