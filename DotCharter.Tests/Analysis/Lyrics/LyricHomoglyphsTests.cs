using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     #921: Cyrillic and Greek letters that look Latin, inside an otherwise Latin word, are folded to
///     Latin for the aligner and the chart; genuinely Cyrillic or Greek words are never touched.
///     Escapes are used on purpose so the look-alikes are visible in the source.
/// </summary>
public sealed class LyricHomoglyphsTests
{
    [Theory]
    [InlineData("thе end", "the end", 1)] // Cyrillic e
    [InlineData("sоmеthing", "something", 1)] // Cyrillic o and e in one word
    [InlineData("Сome рlace", "Come place", 2)] // capital Es, small er
    [InlineData("lοve", "love", 1)] // Greek omicron
    [InlineData("you'rе,", "you're,", 1)] // punctuation stays
    public void A_look_alike_inside_a_latin_word_is_folded(string text, string expected, int words)
    {
        Assert.Equal(expected, LyricHomoglyphs.Fold(text, out var folded));
        Assert.Equal(words, folded);
    }

    [Theory]
    [InlineData("все хорошо")] // a Russian line: no Latin letter
    [InlineData("с")] // a lone Cyrillic letter
    [InlineData("καλημέρα")] // a Greek word
    [InlineData("okже")] // a letter with no Latin twin keeps the whole word as typed
    [InlineData("plain latin words")]
    public void Anything_else_is_left_exactly_as_typed(string text)
    {
        Assert.Same(text, LyricHomoglyphs.Fold(text, out var folded));
        Assert.Equal(0, folded);
    }

    [Fact]
    public void The_sheet_parser_reads_the_folded_text()
    {
        var sheet = LyricSheetParser.Parse("sоmе-thing все");

        Assert.Equal(["some", "thing", "все"], sheet.Syllables.Select(s => s.Text));
    }
}
