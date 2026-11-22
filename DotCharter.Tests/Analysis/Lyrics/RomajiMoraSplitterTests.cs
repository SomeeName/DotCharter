using DotCharter.Analysis.Lyrics;
using DotCharter.Analysis.Lyrics.Syllables;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>#869: the mora rules for romaji and their wiring into a pasted sheet.</summary>
public sealed class RomajiMoraSplitterTests
{
    private const string RomajiLine = "kyouwa tanoshii natsuyasumi gakkou ni ikou sakura ga saku haru no hikari";

    [Theory]
    [InlineData("sakura", "sa|ku|ra")]
    [InlineData("kyou", "kyo|u")]
    [InlineData("shashin", "sha|shi|n")]
    [InlineData("chotto", "cho|t|to")]
    [InlineData("gakkou", "ga|k|ko|u")]
    [InlineData("tsukkonde", "tsu|k|ko|n|de")]
    [InlineData("matcha", "ma|t|cha")]
    [InlineData("kampai", "ka|m|pa|i")]
    [InlineData("konnichiwa", "ko|n|ni|chi|wa")]
    [InlineData("nihon", "ni|ho|n")]
    [InlineData("kin'iro", "ki|n'|i|ro")]
    [InlineData("Tōkyō", "Tō|kyō")]
    [InlineData("nyan", "nya|n")]
    public void Morae(string word, string expected) => Assert.Equal(expected, string.Join('|', RomajiMoraSplitter.Split(word)!));

    [Theory]
    [InlineData("love")]
    [InlineData("quick")]
    [InlineData("cat")]
    [InlineData("str")]
    public void A_word_that_is_not_romaji_is_not_split(string word) => Assert.Null(RomajiMoraSplitter.Split(word));

    [Fact]
    public void A_romaji_sheet_is_split_and_marked()
    {
        var sheet = LyricSheetParser.Parse(RomajiLine);
        Assert.True(LyricSheetSyllabifier.IsRomajiSheet(sheet));

        var split = LyricSheetSyllabifier.ApplyJapanese(sheet);

        Assert.Equal("kyo|u|wa", string.Join('|', split.Syllables.Take(3).Select(s => s.Text)));
        Assert.All(split.Syllables.Take(3), s => Assert.True(s.SplitByRules));
        Assert.True(split.Syllables[0].ContinuesWord);
        Assert.False(split.Syllables[2].ContinuesWord);
    }

    [Fact]
    public void A_hand_hyphenated_romaji_word_is_left_alone()
    {
        var sheet = LyricSheetParser.Parse(RomajiLine.Replace("gakkou", "gak-kou"));
        var split = LyricSheetSyllabifier.ApplyJapanese(sheet);

        var texts = split.Syllables.Select(s => s.Text).ToList();
        Assert.Contains("gak", texts);
        Assert.Contains("kou", texts);
    }

    [Fact]
    public void English_and_short_sheets_are_not_romaji()
    {
        var english = LyricSheetParser.Parse("the love that you give is all that I have and you will never leave me alone with this just like that");
        Assert.False(LyricSheetSyllabifier.IsRomajiSheet(english));
        Assert.Same(english, LyricSheetSyllabifier.ApplyJapanese(english));

        var shortSheet = LyricSheetParser.Parse("kyouwa tanoshii");
        Assert.Same(shortSheet, LyricSheetSyllabifier.ApplyJapanese(shortSheet));
    }

    [Fact]
    public void A_kana_sheet_is_split_into_morae()
    {
        var split = LyricSheetSyllabifier.ApplyJapanese(LyricSheetParser.Parse("がっこう きょう"));

        Assert.Equal("が|っ|こ|う|きょ|う", string.Join('|', split.Syllables.Select(s => s.Text)));
        Assert.Equal([true, true, true, false, true, false], split.Syllables.Select(s => s.ContinuesWord));
        Assert.All(split.Syllables, s => Assert.True(s.SplitByRules));
    }
}
