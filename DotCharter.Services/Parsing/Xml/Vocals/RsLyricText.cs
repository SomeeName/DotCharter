using System.Globalization;
using System.Text;
using DotCharter.Services.Export.Sng;

namespace DotCharter.Services.Parsing.Xml.Vocals;

/// <summary>
///     The EXPORT-BOUNDARY text of one lyric syllable: what the vocals XML and the vocals SNG write
///     for it, never what the chart stores (the chart keeps the charter's own characters).
///     <list type="number">
///         <item>
///             Typographic punctuation goes to ASCII: the curly apostrophes and quotes, the dashes,
///             the ellipsis, the odd spaces. Editor on Fire's own Rocksmith export drops them
///             (its filter refuses Windows-1252 codes below 161) and its lyric import reads the file
///             as Windows-1252 (<c>didn’t</c> shows as mojibake), and stock vocals carry ASCII, so
///             this applies even where the game's font happens to have a glyph.
///         </item>
///         <item>
///             A letter the game's lyric font cannot draw folds to its base letter when that one is
///             drawn — EOF's <c>rs_lyric_substitute_char_extended</c> table (Ã ã Í Õ õ Ý ý Ÿ ÿ Ž
///             become A a I O o Y y Y y Z), generalised through Unicode decomposition.
///         </item>
///         <item>
///             Every other character the font draws stays, which is EOF's "allow RS2 extended ASCII
///             lyrics" set (<see cref="DefaultVocalSymbols.HasGlyph" /> is the font itself); a
///             character with neither a glyph nor a fold is written as typed.
///         </item>
///     </list>
/// </summary>
public static class RsLyricText
{
    private static readonly Dictionary<char, string> Typographic = new()
    {
        ['‘'] = "'", ['’'] = "'", ['‚'] = "'", ['‛'] = "'", ['′'] = "'",
        ['ʼ'] = "'", ['＇'] = "'", ['`'] = "'",
        ['“'] = "\"", ['”'] = "\"", ['„'] = "\"", ['‟'] = "\"", ['″'] = "\"",
        ['‐'] = "-", ['‑'] = "-", ['‒'] = "-", ['–'] = "-", ['—'] = "-",
        ['―'] = "-", ['−'] = "-",
        ['…'] = "...",
        [' '] = " ", [' '] = " ", [' '] = " ", [' '] = " ", [' '] = " ",
        ['​'] = "", ['‌'] = "", ['‍'] = "", ['﻿'] = "", ['­'] = ""
    };

    /// <summary>The text as the game is given it; <paramref name="text" /> itself when nothing changes.</summary>
    public static string ForGame(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Any(NeedsWork)) return text;

        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (Typographic.TryGetValue(c, out var ascii)) result.Append(ascii);
            else if (c < 0x80 || DefaultVocalSymbols.HasGlyph(c)) result.Append(c);
            else result.Append(BaseLetter(c) ?? c);
        }

        return result.ToString();
    }

    private static bool NeedsWork(char c) => Typographic.ContainsKey(c) || c >= 0x80 && !DefaultVocalSymbols.HasGlyph(c);

    private static char? BaseLetter(char c)
    {
        // Half of a surrogate pair (an emoji) is no letter, and Normalize throws on it alone.
        if (char.IsSurrogate(c)) return null;
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        var first = decomposed[0];
        if (decomposed.Length < 2 || !char.IsLetter(first) || first >= 0x80 && !DefaultVocalSymbols.HasGlyph(first)) return null;
        return decomposed.Skip(1).All(m => CharUnicodeInfo.GetUnicodeCategory(m) == UnicodeCategory.NonSpacingMark) ? first : null;
    }
}
