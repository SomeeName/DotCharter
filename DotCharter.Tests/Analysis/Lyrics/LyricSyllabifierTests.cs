using DotCharter.Analysis.Lyrics;
using DotCharter.Analysis.Lyrics.Syllables;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     #869: word lists per language for the pattern-based splitter, including the hard cases
///     (compounds, silent e, French apostrophe and liaison words) and the sheet-level rules.
/// </summary>
public sealed class LyricSyllabifierTests
{
    [Theory]
    [InlineData("hello", "hel-lo")]
    [InlineData("water", "wa-ter")]
    [InlineData("about", "a-bout")]
    [InlineData("making", "mak-ing")]
    [InlineData("pretty", "pret-ty")]
    [InlineData("table", "ta-ble")]
    [InlineData("football", "foot-ball")]
    [InlineData("sunflower", "sun-flow-er")]
    [InlineData("make", "make")]
    [InlineData("love", "love")]
    [InlineData("walked", "walked")]
    [InlineData("heart", "heart")]
    [InlineData("fire", "fire")]
    public void English_words(string word, string expected) => Assert.Equal(expected, Joined(word, SyllableLanguage.English));

    [Theory]
    [InlineData("Handschuh", "Hand-schuh")]
    [InlineData("Liebe", "Lie-be")]
    [InlineData("Sonnenschein", "Son-nen-schein")]
    [InlineData("Wasserfall", "Was-ser-fall")]
    [InlineData("Mädchen", "Mäd-chen")]
    [InlineData("Herz", "Herz")]
    [InlineData("nicht", "nicht")]
    public void German_words(string word, string expected) => Assert.Equal(expected, Joined(word, SyllableLanguage.German));

    [Theory]
    [InlineData("amour", "a-mour")]
    [InlineData("maison", "mai-son")]
    [InlineData("chanson", "chan-son")]
    [InlineData("toujours", "tou-jours")]
    [InlineData("l'amour", "l'a-mour")]
    [InlineData("liberté", "li-ber-té")]
    public void French_words(string word, string expected) => Assert.Equal(expected, Joined(word, SyllableLanguage.French));

    [Theory]
    [InlineData("corazón", "co-ra-zón")]
    [InlineData("amor", "a-mor")]
    [InlineData("noche", "no-che")]
    [InlineData("cantar", "can-tar")]
    [InlineData("siempre", "siem-pre")]
    [InlineData("mariposa", "ma-ri-po-sa")]
    public void Spanish_words(string word, string expected) => Assert.Equal(expected, Joined(word, SyllableLanguage.Spanish));

    [Fact]
    public void Syllables_always_concatenate_back_to_the_word()
    {
        foreach (var word in new[] { "unbelievable", "Donaudampfschiff", "extraordinaire", "electrocardiograma" })
        {
            foreach (var language in Enum.GetValues<SyllableLanguage>())
                Assert.Equal(word, string.Concat(LyricSyllabifier.Split(word, language)));
        }
    }

    [Fact]
    public void No_syllable_is_without_a_vowel()
    {
        foreach (var word in new[] { "strengths", "rhythms", "Angst", "sprichst", "stress" })
        {
            foreach (var language in Enum.GetValues<SyllableLanguage>())
                Assert.All(LyricSyllabifier.Split(word, language), s => Assert.Contains(s, c => "aeiouyäöü".Contains(char.ToLowerInvariant(c))));
        }
    }

    [Fact]
    public void French_liaison_words_stay_separate_words_in_a_sheet()
    {
        var sheet = LyricSheetSyllabifier.Apply(LyricSheetParser.Parse("les amis"), SyllableLanguage.French);
        Assert.Equal(["les", "a-", "mis"], Display(sheet));
    }

    [Fact]
    public void Sheet_without_any_hyphen_is_split_and_keeps_lines()
    {
        var sheet = LyricSheetSyllabifier.Apply(LyricSheetParser.Parse("Hello, about water\nmaking love"), SyllableLanguage.English);
        Assert.Equal(["Hel-", "lo,", "a-", "bout", "wa-", "ter", "mak-", "ing", "love"], Display(sheet));
        Assert.Equal([0, 0, 0, 0, 0, 0, 1, 1, 1], sheet.Syllables.Select(s => s.LineIndex));
        Assert.True(sheet.Syllables[^1].EndsLine);
        Assert.False(sheet.Syllables[^2].EndsLine);
    }

    [Fact]
    public void Sheet_with_one_hand_hyphenated_word_is_left_whole()
    {
        var sheet = LyricSheetParser.Parse("Hello, a-bout water\nmaking love");
        Assert.True(LyricSheetSyllabifier.HasHandHyphenation(sheet));
        Assert.Same(sheet, LyricSheetSyllabifier.Apply(sheet, SyllableLanguage.English));
    }

    [Fact]
    public void Romaji_sheet_with_one_hand_hyphenated_word_is_left_whole()
    {
        var sheet = LyricSheetParser.Parse("Mizu tamari ni ashi wo tsukkonde sora ga aoi kaze ga fuku ta-bi ni omou");
        Assert.True(LyricSheetSyllabifier.IsRomajiSheet(sheet));
        Assert.Same(sheet, LyricSheetSyllabifier.ApplyJapanese(sheet));
    }

    [Fact]
    public void Romaji_sheet_without_hyphens_is_still_split()
    {
        var sheet = LyricSheetParser.Parse("Mizu tamari ni ashi wo tsukkonde sora ga aoi kaze ga fuku tabi ni omou");
        Assert.False(LyricSheetSyllabifier.HasHandHyphenation(sheet));
        Assert.True(LyricSheetSyllabifier.ApplyJapanese(sheet).Syllables.Count > sheet.Syllables.Count);
    }

    [Fact]
    public void A_punctuation_dash_does_not_count_as_hyphenation()
    {
        var sheet = LyricSheetParser.Parse("Hello - about – water\nmaking — love");
        Assert.False(LyricSheetSyllabifier.HasHandHyphenation(sheet));
        Assert.Equal(["Hel-", "lo", "a-", "bout", "wa-", "ter", "mak-", "ing", "love"], Display(LyricSheetSyllabifier.Apply(sheet, SyllableLanguage.English)));
    }

    [Fact]
    public void Language_is_detected_from_function_words_and_romaji_is_left_alone()
    {
        Assert.Equal(SyllableLanguage.English, LyricSheetSyllabifier.DetectLanguage(LyricSheetParser.Parse("you and I are the one that will")));
        Assert.Equal(SyllableLanguage.German, LyricSheetSyllabifier.DetectLanguage(LyricSheetParser.Parse("ich liebe dich und die Nacht ist nicht")));
        Assert.Equal(SyllableLanguage.French, LyricSheetSyllabifier.DetectLanguage(LyricSheetParser.Parse("je suis dans la nuit pour les amis")));
        Assert.Equal(SyllableLanguage.Spanish, LyricSheetSyllabifier.DetectLanguage(LyricSheetParser.Parse("yo no se como por que con los sueños")));
        Assert.Null(LyricSheetSyllabifier.DetectLanguage(LyricSheetParser.Parse("Mizu tamari ni ashi wo tsukkonde sora ga aoi")));
    }

    /// <summary>#921: a word-initial y before a vowel is a consonant (the glide of "you"), never a syllable of its own.</summary>
    [Theory]
    [InlineData("you", "you")]
    [InlineData("You", "You")]
    [InlineData("your", "your")]
    [InlineData("yours", "yours")]
    [InlineData("young", "young")]
    [InlineData("you'd", "you'd")]
    [InlineData("you’re", "you’re")]
    [InlineData("yellow", "yel-low")]
    [InlineData("beyond", "be-yond")]
    [InlineData("yesterday", "yes-ter-day")]
    [InlineData("happy", "hap-py")]
    public void A_glide_y_is_not_a_syllable(string word, string expected) => Assert.Equal(expected, Joined(word, SyllableLanguage.English));

    /// <summary>#921: only the LAST syllable of a split line-final word ends the line (<c>to- geth- er+</c>).</summary>
    [Fact]
    public void A_split_line_final_word_ends_the_line_only_on_its_last_syllable()
    {
        var sheet = LyricSheetSyllabifier.Apply(LyricSheetParser.Parse("we stay together\nhello water"), SyllableLanguage.English);

        Assert.Equal(["we", "stay", "to-", "geth-", "er", "hel-", "lo", "wa-", "ter"], Display(sheet));
        Assert.Equal([false, false, false, false, true, false, false, false, true], sheet.Syllables.Select(s => s.EndsLine));
    }

    private static string Joined(string word, SyllableLanguage language) => string.Join('-', LyricSyllabifier.Split(word, language));

    private static IEnumerable<string> Display(LyricSheet sheet) => sheet.Syllables.Select(s => s.Text + (s.ContinuesWord ? "-" : ""));
}
