using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The capture library beside the amp head is searchable (owner,
///     2026-09-08: "why not have this searchable"). The rule is one pure
///     function over the rows: case-insensitive substring on the title,
///     library order kept, an empty or blank filter showing everything.
/// </summary>
public sealed class RigCaptureSearchTests
{
    private static RigCaptureRow Row(string title, string file = "x.nam") =>
        new(new NamCaptureEntry(
                new NamCaptureManifest(1, 1, title, "a", "amp", "nam", "std", "cc", "", "", file, "", 0, DateTimeOffset.UnixEpoch),
                file),
            null!); // the target is only read by IsActive / Load, neither of which a filter touches

    [Theory]
    [InlineData("", 4)]
    [InlineData("   ", 4)]
    [InlineData("reverb", 2)]
    [InlineData("TWIN", 1)]
    [InlineData("bass di", 1)]
    [InlineData("nothing here", 0)]
    public void MatchingCaptures_FiltersByTitle_CaseInsensitively(string filter, int expected)
    {
        var rows = new[] { Row("Twin Reverb Normal"), Row("Deluxe Reverb Clean"), Row("Dingwall Bass DI"), Row("JCM900 Dual Ch A") };
        Assert.Equal(expected, RigDockUnit.MatchingCaptures(rows, filter).Count);
    }

    [Fact]
    public void MatchingCaptures_KeepsLibraryOrder()
    {
        var rows = new[] { Row("B Reverb"), Row("A Reverb"), Row("C Reverb") };
        Assert.Equal(["B Reverb", "A Reverb", "C Reverb"], RigDockUnit.MatchingCaptures(rows, "reverb").Select(r => r.Title));
    }
}
