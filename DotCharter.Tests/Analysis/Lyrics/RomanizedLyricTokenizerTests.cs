using DotCharter.Analysis.Lyrics;

namespace DotCharter.Tests.Analysis.Lyrics;

/// <summary>
///     Text → model columns. The vocabulary mirrors the real MMS-FA one
///     (<c>&lt;blank&gt;</c> first, then control tokens, then the roman letters),
///     so the id arithmetic under test is the arithmetic that ships.
/// </summary>
public sealed class RomanizedLyricTokenizerTests
{
    private static LyricVocabulary Vocabulary()
    {
        var tokens = new List<string> { "<blank>", "<pad>", "</s>", "<unk>" };
        tokens.AddRange("abcdefghijklmnopqrstuvwxyz".Select(c => c.ToString()));
        tokens.Add("'");
        return LyricVocabulary.FromTokens(tokens);
    }

    [Fact]
    public void TheBlankIsResolvedByNameRatherThanAssumedToBeIndexZero()
    {
        var vocabulary = LyricVocabulary.FromTokens(["a", "b", "<pad>", "c"]);

        Assert.Equal(2, vocabulary.BlankId);
    }

    [Fact]
    public void AnExplicitBlankTokenWinsOverPad()
    {
        var vocabulary = LyricVocabulary.FromTokens(["<pad>", "a", "<blank>"]);

        Assert.Equal(2, vocabulary.BlankId);
    }

    /// <summary>
    ///     The star is synthesised by the aligner, so it must sit one past the
    ///     model's real width — never inside it, where it would collide with a
    ///     letter.
    /// </summary>
    [Fact]
    public void TheStarSitsOnePastTheModelsOwnWidth()
    {
        var vocabulary = Vocabulary();

        Assert.Equal(31, vocabulary.SymbolCount);
        Assert.Equal(31, vocabulary.StarId);
        Assert.Equal(32, vocabulary.WidthWithStar);
    }

    [Fact]
    public void ControlTokensAreNotAlignableCharacters()
    {
        var vocabulary = Vocabulary();

        Assert.False(vocabulary.TryGetId('<', out _));
        Assert.True(vocabulary.TryGetId('a', out _));
        Assert.True(vocabulary.TryGetId('\'', out _));
    }

    [Fact]
    public void EachLineIsWrappedInStarsAndTheSheetIsClosedByOne()
    {
        var sheet = LyricSheetParser.Parse("a\nb");
        var vocabulary = Vocabulary();

        var tokenization = RomanizedLyricTokenizer.Tokenize(sheet, vocabulary);

        // star a star b star
        Assert.Equal(5, tokenization.Tokens.Count);
        Assert.Equal(vocabulary.StarId, tokenization.Tokens[0]);
        Assert.Equal(vocabulary.StarId, tokenization.Tokens[2]);
        Assert.Equal(vocabulary.StarId, tokenization.Tokens[4]);
    }

    [Fact]
    public void SyllablesPointAtTheirOwnTokensAndSkipTheStars()
    {
        var sheet = LyricSheetParser.Parse("tsuk-kon-de");
        var vocabulary = Vocabulary();

        var tokenization = RomanizedLyricTokenizer.Tokenize(sheet, vocabulary);

        Assert.Equal(3, tokenization.Syllables.Count);
        Assert.Equal(4, tokenization.Syllables[0].TokenCount); // t s u k
        Assert.Equal(3, tokenization.Syllables[1].TokenCount); // k o n
        Assert.Equal(2, tokenization.Syllables[2].TokenCount); // d e

        // The leading star occupies index 0, so the first syllable starts at 1
        // and the syllables are contiguous from there.
        Assert.Equal(1, tokenization.Syllables[0].TokenStart);
        Assert.Equal(5, tokenization.Syllables[1].TokenStart);
        Assert.Equal(8, tokenization.Syllables[2].TokenStart);
    }

    /// <summary>
    ///     A macron folds to the bare vowel. Doubling it would be the intuitive
    ///     encoding of a held vowel and is wrong: CTC needs a blank BETWEEN two
    ///     identical tokens, so <c>oo</c> forces a silent gap into the middle of
    ///     a sustained note.
    /// </summary>
    [Fact]
    public void MacronsAndAccentsFoldToTheBareLetter()
    {
        var vocabulary = Vocabulary();
        var withMacron = RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse("tō"), vocabulary);
        var plain = RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse("to"), vocabulary);

        Assert.Equal(plain.Tokens, withMacron.Tokens);
        Assert.Empty(withMacron.DroppedCharacters);
    }

    [Fact]
    public void CaseIsFoldedSoACapitalisedLineStartAlignsLikeAnyOther()
    {
        var vocabulary = Vocabulary();

        Assert.Equal(
            RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse("mi"), vocabulary).Tokens,
            RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse("Mi"), vocabulary).Tokens);
    }

    /// <summary>Punctuation is never sung; it is dropped silently so it cannot
    ///     drown out the report that actually matters.</summary>
    [Fact]
    public void PunctuationIsRemovedWithoutBeingReportedAsDropped()
    {
        var vocabulary = Vocabulary();

        var tokenization = RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse("hey!?"), vocabulary);

        Assert.Equal(3, tokenization.Tokens.Count - 2); // minus the two stars
        Assert.Empty(tokenization.DroppedCharacters);
    }

    /// <summary>
    ///     A sheet pasted in kana or kanji has no alignable characters at all.
    ///     Reporting the count is what lets the caller say so plainly instead of
    ///     returning an alignment that is merely bad.
    /// </summary>
    [Fact]
    public void NonRomanScriptIsReportedRatherThanSilentlyDropped()
    {
        var vocabulary = Vocabulary();

        var tokenization = RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse("水-溜"), vocabulary);

        Assert.Equal(2, tokenization.UnalignableSyllableCount);
        Assert.Contains('水', tokenization.DroppedCharacters.Keys);
        Assert.Contains('溜', tokenization.DroppedCharacters.Keys);
    }

    [Fact]
    public void AnEmptySheetProducesNoTokensAtAll()
    {
        var tokenization = RomanizedLyricTokenizer.Tokenize(LyricSheet.Empty, Vocabulary());

        Assert.Empty(tokenization.Tokens);
    }

    // ---- the word delimiter -------------------------------------------------

    /// <summary>An English wav2vec2 CTC head: uppercase letters and a '|' between words.</summary>
    private static LyricVocabulary EnglishVocabulary()
    {
        var tokens = new List<string> { "<pad>", "<s>", "</s>", "<unk>", "|" };
        tokens.AddRange("ETAONIHSRDLUMWCFGYPBVK".Select(c => c.ToString()));
        tokens.Add("'");
        tokens.AddRange("XJQZ".Select(c => c.ToString()));
        return LyricVocabulary.FromTokens(tokens);
    }

    /// <summary>
    ///     The pipe is a MODEL symbol, so it must not be reachable from text — a
    ///     stray '|' in a pasted sheet would otherwise align as if it were sung.
    /// </summary>
    [Fact]
    public void TheWordDelimiterIsLiftedOutOfTheCharacterMap()
    {
        var vocabulary = EnglishVocabulary();

        Assert.Equal(4, vocabulary.WordDelimiterId);
        Assert.False(vocabulary.TryGetId('|', out _));
    }

    /// <summary>
    ///     MMS-FA aligns romanized text as an unbroken letter stream and has no
    ///     such symbol, which is what makes every delimiter-aware path a no-op for
    ///     the shipping model by construction.
    /// </summary>
    [Fact]
    public void AVocabularyWithoutAPipeReportsNoDelimiter() => Assert.Equal(-1, Vocabulary().WordDelimiterId);

    [Fact]
    public void ADelimiterIsEmittedBetweenWordsButNotInsideOne()
    {
        var vocabulary = EnglishVocabulary();
        var sheet = LyricSheetParser.Parse("ta-ke on me");

        var tokenization = RomanizedLyricTokenizer.Tokenize(sheet, vocabulary);

        // star t a k e | o n | m e star  -- no delimiter inside "ta-ke", and the
        // line-final syllable is closed by the star instead.
        Assert.Equal(vocabulary.WordDelimiterId, tokenization.Tokens[5]);
        Assert.Equal(vocabulary.WordDelimiterId, tokenization.Tokens[8]);
        Assert.Equal(2, tokenization.Tokens.Count(t => t == vocabulary.WordDelimiterId));
    }

    /// <summary>
    ///     The delimiter sits OUTSIDE both syllables' spans. If it were inside one,
    ///     that syllable's measured onset would include a boundary nobody sings.
    /// </summary>
    [Fact]
    public void TheDelimiterBelongsToNeitherNeighbouringSyllable()
    {
        var vocabulary = EnglishVocabulary();

        var tokenization = RomanizedLyricTokenizer.Tokenize(LyricSheetParser.Parse("on me"), vocabulary);

        Assert.Equal(2, tokenization.Syllables[0].TokenCount); // o n
        Assert.Equal(2, tokenization.Syllables[1].TokenCount); // m e
        var afterFirst = tokenization.Syllables[0].TokenStart + tokenization.Syllables[0].TokenCount;
        Assert.Equal(vocabulary.WordDelimiterId, tokenization.Tokens[afterFirst]);
        Assert.Equal(afterFirst + 1, tokenization.Syllables[1].TokenStart);
    }

    [Fact]
    public void TheDelimiterCanBeTurnedOffForComparison()
    {
        var vocabulary = EnglishVocabulary();

        var without = RomanizedLyricTokenizer.Tokenize(
            LyricSheetParser.Parse("on me"), vocabulary, insertWordDelimiters: false);

        Assert.DoesNotContain(vocabulary.WordDelimiterId, without.Tokens);
    }

    /// <summary>
    ///     A syllable that contributed no alignable characters must not be followed
    ///     by a delimiter either — that would put a boundary where there is no word.
    /// </summary>
    [Fact]
    public void AnUnalignableSyllableIsNotFollowedByADelimiter()
    {
        var vocabulary = EnglishVocabulary();

        var tokenization = RomanizedLyricTokenizer.Tokenize(
            LyricSheetParser.Parse("水 me"), vocabulary);

        Assert.Equal(1, tokenization.UnalignableSyllableCount);
        Assert.DoesNotContain(vocabulary.WordDelimiterId, tokenization.Tokens);
    }
}
