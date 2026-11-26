using DotCharter.Analysis.Lyrics;
using DotCharter.Analysis.Lyrics.Syllables;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     Every typographic apostrophe a lyric site, word processor or phone produces must behave exactly
///     like the ASCII one in every lyric path, and the charter's own character must survive into the
///     syllables written back to the chart (#916).
/// </summary>
public sealed class LyricApostropheFormsTests
{
    public static TheoryData<char> Forms =>
    [
        '’', '‘', 'ʼ', '`', '´', '＇', '′'
    ];

    private static string Line(char a) => $"we don{a}t know what{a}s going on{a} there{a}s nothing{a}";

    private static LyricVocabulary Vocabulary()
    {
        var tokens = new List<string> { "<blank>", "<pad>", "</s>", "<unk>" };
        tokens.AddRange("abcdefghijklmnopqrstuvwxyz".Select(c => c.ToString()));
        tokens.Add("'");
        return LyricVocabulary.FromTokens(tokens);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void TokensAndTheTextToTokenMapMatchAscii(char form)
    {
        var ascii = RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse(Line('\'')), Vocabulary());
        var typed = RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse(Line(form)), Vocabulary());

        Assert.Equal(ascii.Tokens, typed.Tokens);
        Assert.Equal(ascii.Syllables, typed.Syllables);
        Assert.Equal(ascii.DroppedCharacters, typed.DroppedCharacters);
        Assert.Empty(typed.DroppedCharacters);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void WordSplittingMatchesAsciiAndKeepsTheOriginalCharacter(char form)
    {
        foreach (var word in new[] { "don{0}t", "what{0}s", "l{0}amour", "ev{0}ry-thing", "rock{0}n{0}roll", "going{0}", "{0}cause" })
        {
            var ascii = LyricSyllabifier.Split(string.Format(word, '\''), SyllableLanguage.English);
            var typed = LyricSyllabifier.Split(string.Format(word, form), SyllableLanguage.English);

            Assert.Equal(ascii.Count, typed.Count);
            Assert.Equal(string.Format(word, form), string.Concat(typed));
            Assert.Equal(ascii.Select(p => p.Replace('\'', form)), typed);
        }
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void PlacedSyllablesMatchAsciiAndKeepTheOriginalCharacter(char form)
    {
        var line = $"the water{form}s under the bridge and you{form}re everything";
        var ascii = LyricSheetSyllabifier.Apply(LyricSheetParser.Parse(line.Replace(form, '\'')), SyllableLanguage.English);
        var typed = LyricSheetSyllabifier.Apply(LyricSheetParser.Parse(line), SyllableLanguage.English);

        Assert.Equal(ascii.Syllables.Count, typed.Syllables.Count);
        Assert.Equal(ascii.Syllables.Select(s => s.Text.Replace('\'', form)), typed.Syllables.Select(s => s.Text));
        Assert.Equal(ascii.Syllables.Select(s => s.ContinuesWord), typed.Syllables.Select(s => s.ContinuesWord));
        Assert.Contains(typed.Syllables, s => s.Text.Contains(form));
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void FunctionWordDetectionIgnoresTheForm(char form)
    {
        var line = $"the and you that with for your don{form}t";
        Assert.Equal(
            LyricSheetSyllabifier.DetectLanguage(LyricSheetParser.Parse(line.Replace(form, '\''))),
            LyricSheetSyllabifier.DetectLanguage(LyricSheetParser.Parse(line)));
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void RomajiSplitMatchesAscii(char form)
    {
        var ascii = RomajiMoraSplitter.Split("shin'ya");
        var typed = RomajiMoraSplitter.Split($"shin{form}ya");

        Assert.NotNull(typed);
        Assert.Equal(ascii!.Select(m => m.Replace('\'', form)), typed);
    }
}
