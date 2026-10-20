using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     The paste format. The load-bearing case is
///     <see cref="ItReproducesTheHandSyncedRocksmithSplitExactly" />: the parse
///     of the compact text must equal the syllable split a human actually
///     charted, or every measurement taken against <c>TestData/EOF</c> is
///     comparing different things.
/// </summary>
public sealed class LyricSheetParserTests
{
    private static string[] Texts(LyricSheet sheet) => sheet.Syllables.Select(s => s.Text).ToArray();

    /// <summary>
    ///     The opening line of <c>TestData/EOF/Yorushika - Matasaburo/
    ///     arr_vocals_RS2.xml</c>, which stores it as the twelve entries
    ///     <c>Mi- zu ta- ma- ri ni a- shi wo tsuk- kon- de+</c>. Typing it in the
    ///     compact format has to yield the same twelve syllables with the same
    ///     word boundaries and the same phrase end.
    /// </summary>
    [Fact]
    public void ItReproducesTheHandSyncedRocksmithSplitExactly()
    {
        var sheet = LyricSheetParser.Parse("Mi-zu ta-ma-ri ni a-shi wo tsuk-kon-de");

        Assert.Equal(["Mi", "zu", "ta", "ma", "ri", "ni", "a", "shi", "wo", "tsuk", "kon", "de"], Texts(sheet));
        Assert.Equal(
            [true, false, true, true, false, false, true, false, false, true, true, false],
            sheet.Syllables.Select(s => s.ContinuesWord).ToArray());
        Assert.Equal(11, sheet.Syllables.Select((s, i) => (s, i)).Single(x => x.s.EndsLine).i);
    }

    /// <summary>
    ///     …and the same text copied back OUT of a chart, suffixes and all,
    ///     parses identically. That is what makes an export/paste round trip
    ///     lossless.
    /// </summary>
    [Fact]
    public void RocksmithsOwnSuffixedFormIsAcceptedAndParsesTheSameWay()
    {
        var compact = LyricSheetParser.Parse("Mi-zu ta-ma-ri ni a-shi wo tsuk-kon-de");
        var exported = LyricSheetParser.Parse("Mi- zu ta- ma- ri ni a- shi wo tsuk- kon- de+");

        Assert.Equal(compact.Syllables, exported.Syllables);
    }

    [Fact]
    public void EachLineEndsItsOwnPhrase()
    {
        var sheet = LyricSheetParser.Parse("hel-lo world\nsec-ond line");

        Assert.Equal(2, sheet.LineCount);
        Assert.Equal([false, false, true, false, false, true],
            sheet.Syllables.Select(s => s.EndsLine).ToArray());
    }

    /// <summary>A word cannot continue across a line break, whatever the hyphen said.</summary>
    [Fact]
    public void ATrailingHyphenAtTheEndOfALineDoesNotJoinTheNextLine()
    {
        var sheet = LyricSheetParser.Parse("ho-\nla");

        Assert.False(sheet.Syllables[0].ContinuesWord);
        Assert.True(sheet.Syllables[0].EndsLine);
    }

    [Fact]
    public void BlankLinesAreStanzaBreaksAndCreateNoEmptyPhrase()
    {
        var sheet = LyricSheetParser.Parse("one\n\n\ntwo");

        Assert.Equal(2, sheet.LineCount);
        Assert.Equal(["one", "two"], Texts(sheet));
        Assert.Equal([0, 1], sheet.Syllables.Select(s => s.LineIndex).ToArray());
    }

    [Fact]
    public void CarriageReturnsAndRaggedSpacingAreTolerated()
    {
        var sheet = LyricSheetParser.Parse("  a-b   c \r\n\t d \r\n");

        Assert.Equal(["a", "b", "c", "d"], Texts(sheet));
        Assert.Equal(2, sheet.LineCount);
    }

    [Fact]
    public void EnDashesAndEmDashesSplitSyllablesLikeAPlainHyphen()
    {
        var sheet = LyricSheetParser.Parse("a–b—c");

        Assert.Equal(["a", "b", "c"], Texts(sheet));
        Assert.Equal([true, true, false], sheet.Syllables.Select(s => s.ContinuesWord).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\t\n  ")]
    public void NothingToParseYieldsAnEmptySheet(string? text)
    {
        var sheet = LyricSheetParser.Parse(text);

        Assert.Empty(sheet.Syllables);
        Assert.Equal(0, sheet.LineCount);
    }

    /// <summary>
    ///     A line of pure punctuation must not burn a line index — otherwise the
    ///     reported line count disagrees with the phrases that actually exist.
    /// </summary>
    [Fact]
    public void ALineThatContributesNoSyllablesDoesNotAdvanceTheLineCount()
    {
        var sheet = LyricSheetParser.Parse("one\n---\ntwo");

        Assert.Equal(2, sheet.LineCount);
        Assert.Equal([0, 1], sheet.Syllables.Select(s => s.LineIndex).ToArray());
    }
}
