using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
///     #921: the export boundary's lyric characters. Typographic punctuation goes to ASCII, a letter
///     the game's lyric font lacks folds to its base letter (EOF's substitution table), and every
///     character the font draws (EOF's extended set) stays.
/// </summary>
public sealed class RsLyricTextTests
{
    [Theory]
    [InlineData("didn’t", "didn't")]
    [InlineData("‘yes’", "'yes'")]
    [InlineData("“go”", "\"go\"")]
    [InlineData("now—then", "now-then")]
    [InlineData("wait…", "wait...")]
    [InlineData("a b", "a b")]
    [InlineData("São", "Sao")] // a with tilde: EOF substitutes it
    [InlineData("Ýves", "Yves")] // Y with acute
    [InlineData("Žena", "Zena")] // Z with caron
    [InlineData("naïve", "naïve")] // i with diaeresis: the font draws it
    [InlineData("Mädchen", "Mädchen")]
    [InlineData("¿qué?", "¿qué?")]
    [InlineData("plain", "plain")]
    public void The_game_gets_characters_its_font_can_show(string text, string expected) =>
        Assert.Equal(expected, RsLyricText.ForGame(text));

    [Fact]
    public void Text_with_nothing_to_change_is_returned_itself()
    {
        const string text = "café";

        Assert.Same(text, RsLyricText.ForGame(text));
    }

    [Fact]
    public void A_line_end_and_a_word_part_keep_their_markers_after_the_substitution()
    {
        Assert.Equal("didn't+", RsVocalLyricEncoding.Encode("didn’t", continuesWord: false, endsPhrase: true));
        Assert.Equal("Sé-", RsVocalLyricEncoding.Encode("Sé", continuesWord: true, endsPhrase: false));
    }
}
