using System.Globalization;
using System.Text;

namespace DotCharter.Analysis.Lyrics;

/// <summary>One syllable reduced to the acoustic model's output columns.</summary>
/// <param name="SyllableIndex">Index into <see cref="LyricSheet.Syllables" />.</param>
/// <param name="TokenStart">First index of this syllable inside the flat token sequence.</param>
/// <param name="TokenCount">How many tokens it contributed. Zero when nothing survived.</param>
public readonly record struct SyllableTokens(int SyllableIndex, int TokenStart, int TokenCount);

/// <summary>
///     The flat token sequence handed to the aligner, plus the map back to
///     syllables and anything that had to be dropped on the way.
/// </summary>
/// <param name="Tokens">
///     Model output columns in singing order, star-wrapped — see
///     <see cref="RomanizedLyricTokenizer.Tokenize" />.
/// </param>
/// <param name="Syllables">Where each syllable's tokens landed in <paramref name="Tokens" />.</param>
/// <param name="DroppedCharacters">
///     Distinct characters the vocabulary has no column for, with counts. Surfaced
///     rather than swallowed: a sheet full of kanji drops nearly every character
///     and would otherwise align to silence and merely look inaccurate.
/// </param>
public sealed record LyricTokenization(
    IReadOnlyList<int> Tokens,
    IReadOnlyList<SyllableTokens> Syllables,
    IReadOnlyDictionary<char, int> DroppedCharacters)
{
    /// <summary>Syllables that contributed no tokens at all.</summary>
    public int UnalignableSyllableCount => Syllables.Count(s => s.TokenCount == 0);
}

/// <summary>
///     Turns a <see cref="LyricSheet" /> into the flat token sequence CTC
///     alignment consumes, and remembers which tokens belong to which syllable
///     so the frame spans can be folded back up afterwards.
///     <para>
///         <b>Accents are folded, not doubled.</b> Input is normalized to NFD and
///         stripped of combining marks, so <c>ō</c>, <c>ā</c> and <c>é</c> become
///         <c>o</c>, <c>a</c>, <c>e</c>. Mapping a macron to a DOUBLED vowel
///         (<c>ō</c> → <c>oo</c>) is the tempting alternative — it looks like it
///         encodes the held length — but under CTC two identical adjacent tokens
///         require a blank frame BETWEEN them, so it forces the aligner to
///         invent a gap in the middle of a sustained vowel. A long vowel is
///         already one token held across many frames, which is exactly what CTC
///         represents natively.
///     </para>
///     <para>
///         <b>Every line is star-wrapped.</b> A star goes before the first
///         syllable, between every pair of lines and after the last. CTC forces
///         every frame to be explained, so without them the intro, the gaps
///         between sung lines, the solo and the outro all have to be absorbed by
///         a real syllable at the seam — which is precisely the syllable whose
///         onset the charter is looking at. Only about half of a song's sung
///         span is actually voiced, so this is not a rare correction.
///     </para>
/// </summary>
public static class RomanizedLyricTokenizer
{
    /// <param name="sheet">The parsed lyric sheet, in singing order.</param>
    /// <param name="vocabulary">The acoustic model's output alphabet.</param>
    /// <param name="insertWordDelimiters">
    ///     Emit the model's own word-boundary symbol between words when it has one
    ///     (<see cref="LyricVocabulary.WordDelimiterId" />). A no-op for MMS-FA,
    ///     which has no such symbol; the parameter exists so the effect can be
    ///     measured on a model that does.
    /// </param>
    public static LyricTokenization Tokenize(
        LyricSheet sheet, LyricVocabulary vocabulary, bool insertWordDelimiters = true)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(vocabulary);

        var tokens = new List<int>();
        var map = new List<SyllableTokens>(sheet.Syllables.Count);
        var dropped = new Dictionary<char, int>();

        var delimiterId = insertWordDelimiters ? vocabulary.WordDelimiterId : -1;
        var previousLine = -1;

        for (var i = 0; i < sheet.Syllables.Count; i++)
        {
            var syllable = sheet.Syllables[i];

            // One star before the first line and one at every line boundary.
            if (syllable.LineIndex != previousLine)
            {
                tokens.Add(vocabulary.StarId);
                previousLine = syllable.LineIndex;
            }

            var start = tokens.Count;
            foreach (var c in Fold(syllable.Text))
            {
                if (vocabulary.TryGetId(c, out var id)) tokens.Add(id);
                else dropped[c] = dropped.GetValueOrDefault(c) + 1;
            }

            map.Add(new SyllableTokens(i, start, tokens.Count - start));

            // The delimiter sits OUTSIDE both syllables' spans, which is what
            // keeps it out of their measured onsets: it is a boundary the model
            // predicts, not a sound anybody sings. A syllable that continues its
            // word gets none, and a line-final one gets the star instead.
            if (delimiterId >= 0 && !syllable.ContinuesWord && !syllable.EndsLine && tokens.Count > start)
                tokens.Add(delimiterId);
        }

        // Close the last line so the outro has somewhere to go.
        if (tokens.Count > 0) tokens.Add(vocabulary.StarId);

        return new LyricTokenization(tokens, map, dropped);
    }

    /// <summary>
    ///     Lower-case, strip diacritics, and drop anything that is not a letter
    ///     or an apostrophe. Punctuation is silently removed rather than
    ///     reported — it is never sung, so counting it as "dropped" would bury
    ///     the signal that matters (a sheet in the wrong script).
    /// </summary>
    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            var lower = char.ToLowerInvariant(c);
            if (char.IsLetter(lower) || lower == '\'') sb.Append(lower);
        }

        return sb.ToString();
    }
}
