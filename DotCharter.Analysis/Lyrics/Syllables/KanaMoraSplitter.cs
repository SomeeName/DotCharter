using System.Text;

namespace DotCharter.Analysis.Lyrics.Syllables;

/// <summary>
///     Splits kana into morae, the unit Japanese is sung in. A small ゃゅょ (ゎ) joins the kana
///     before it; っ, ん and ー each count as one mora of their own; a small ぁぃぅぇぉ joins the
///     kana before it only in a loanword combination such as ファ, ティ or ウィ, and stands
///     alone elsewhere (あぁ is two morae). Katakana is read the same as hiragana.
/// </summary>
public static class KanaMoraSplitter
{
    private const string SmallYa = "ゃゅょゎ";
    private const string SmallVowels = "ぁぃぅぇぉ";

    // Bases that take a small vowel in loanwords: ふぁ, てぃ, うぃ, ゔぁ, とぅ, しぇ, ちぇ, じぇ...
    private const string LoanBases = "ふうてでつしちじいゔとどくぐすずひ";

    /// <summary>The morae of a run of kana, in order. Anything that is not kana is kept as its own piece.</summary>
    public static IReadOnlyList<string> Split(string kana)
    {
        ArgumentNullException.ThrowIfNull(kana);

        var morae = new List<StringBuilder>();
        foreach (var c in kana)
        {
            var hira = ToHiragana(c);
            var joins = morae.Count > 0 && (SmallYa.Contains(hira) || SmallVowels.Contains(hira) && morae[^1].Length == 1 && LoanBases.Contains(ToHiragana(morae[^1][0])));
            if (joins) morae[^1].Append(c);
            else morae.Add(new StringBuilder().Append(c));
        }

        return [.. morae.Select(m => m.ToString())];
    }

    /// <summary>True when the text is only kana (either syllabary, with the long-vowel mark).</summary>
    public static bool IsKana(string text) => text.Length > 0 && text.All(c => IsKanaChar(c));

    private static bool IsKanaChar(char c) => c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー' or 'ヽ' or 'ゝ';

    private static char ToHiragana(char c) => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;
}
