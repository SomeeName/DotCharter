using DotCharter.Services.Import.GuitarPro.Report.AnswerKey;

namespace DotCharter.Tests.Services.Import.GuitarPro.Report.AnswerKey;

/// <summary>
///     The matching half of <c>tools/tab-vs-chart</c> on SYNTHETIC names only — the owner's CSV,
///     tabs and charts are never needed, read or quoted by a test.
/// </summary>
public sealed class TabChartMatchingTests
{
    [Fact]
    public void TheCsv_KeepsQuotedCommasQuotesAndLineBreaks_AndKnownGoodGoesByTheUpdateDateOnly()
    {
        const string csv = "\uFEFFID,Title,Album,Artist ID,Author Note,Created At,Updated At\r\n" +
                           "1,\"Alpha, Beta\",X,10,\"a \"\"quoted\"\"\nnote\",2023-01-01 10:00:00,2024-08-01 09:00:00\r\n" +
                           "2,Gamma,Y,11,,2023-01-01 10:00:00,2023-02-01 10:00:00\r\n" +
                           "3,Delta,Z,12,,2024-07-20 00:00:00,2024-07-20 00:00:00\r\n" +
                           "4,Epsilon,Z,12,,2024-09-01 00:00:00,2024-07-19 23:59:59\r\n";

        var rows = TabChartMatching.ReadCdlcCsv(csv);
        var cutoff = new DateTime(2024, 7, 20, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(["1", "2", "3", "4"], rows.Select(r => r.Id));
        Assert.Equal("Alpha, Beta", rows[0].Title);
        Assert.Equal("10", rows[0].ArtistId);
        Assert.Equal(["1", "3"], rows.Where(r => r.IsKnownGood(cutoff)).Select(r => r.Id));
    }

    [Fact]
    public void Titles_MatchAcrossCasePunctuationAccentsAndBrackets_ButNotOnAShortSharedWord()
    {
        Assert.Equal(1.0, TabChartMatching.TitleSimilarity("A Loser Doesn't Need an Encore", "a loser doesnt need an ENCORE"));
        Assert.Equal(1.0, TabChartMatching.TitleSimilarity("Café Noir", "Cafe Noir (Live)"));
        Assert.True(TabChartMatching.TitleSimilarity("Fireworks Beneath My Shoes", "Firworks Beneath My Shoes") >= TabChartMatching.MinimumScore);
        Assert.True(TabChartMatching.TitleSimilarity("Bring", "Bring Eyes Death Invite") < TabChartMatching.MinimumScore);
        Assert.True(TabChartMatching.TitleSimilarity("Amy", "Army") < TabChartMatching.MinimumScore);
    }

    [Fact]
    public void TheChartFolder_IsMatchedByItsTitle_AndYieldsItsArtist()
    {
        string[] folders = ["Band A - Night Walk", "Band B - Night Walker Returns", "Band C - Other Song (Toolkit)"];

        var best = TabChartMatching.BestChartFolder("Night Walk", folders);

        Assert.NotNull(best);
        Assert.Equal("Band A - Night Walk", best.Path);
        Assert.Equal("Band A", best.Artist);
        Assert.Null(TabChartMatching.BestChartFolder("Completely Different", folders));
    }

    [Fact]
    public void TheTab_PrefersTheChartsArtist_ThenTheRicherFormat()
    {
        string[] tabs =
        [
            "Other Band - Night Walk.gp",
            Path.Combine("Band A", "Band A - Night Walk.gp5"),
            Path.Combine("Band A", "Band A - Night Walk.gp"),
            Path.Combine("Band A", "notes.pdf")
        ];

        var best = TabChartMatching.BestTab("Night Walk", "Band A", tabs);

        Assert.NotNull(best);
        Assert.Equal(Path.Combine("Band A", "Band A - Night Walk.gp"), best.Path);
        Assert.Null(TabChartMatching.BestTab("Unknown Title", "Band A", tabs));
    }

    [Fact]
    public void Overrides_SetATabAChartFolderOrASkip_AndIgnoreComments()
    {
        const string text = "# id\ttab\tchart\n42\tsub\\file.gp\t-\n43\tskip\n44\t-\tBand - Song\n\n";

        var map = TabChartMatching.ReadOverrides(text);

        Assert.Equal(3, map.Count);
        Assert.Equal("sub\\file.gp", map["42"].Tab);
        Assert.Null(map["42"].ChartFolder);
        Assert.True(map["43"].Skip);
        Assert.Null(map["44"].Tab);
        Assert.Equal("Band - Song", map["44"].ChartFolder);
    }
}
