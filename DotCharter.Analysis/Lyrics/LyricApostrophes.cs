namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     The one definition of "an apostrophe" for every lyric path. Lyric sites, word processors and
///     phones type the typographic forms (right and left single quote, modifier letter apostrophe,
///     grave, acute, fullwidth, prime); each must behave exactly like the ASCII <c>'</c>. Every form
///     is one UTF-16 unit, so a normalized copy has the same length and offsets as the original: callers
///     decide on the normalized text and slice the ORIGINAL, which keeps the user's own character in
///     the syllables written back to the chart.
/// </summary>
public static class LyricApostrophes
{
    /// <summary>The ASCII apostrophe every other form folds to.</summary>
    public const char Ascii = '\'';

    /// <summary>True for the ASCII apostrophe and every typographic form that stands in for it.</summary>
    public static bool Is(char c) => c is '\'' or '’' or '‘' or 'ʼ' or '`' or '´' or '＇' or '′';

    /// <summary>The text with every apostrophe form replaced by ASCII; same length, same offsets.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return text.Any(Is) ? string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Is(source[i]) ? Ascii : source[i];
        }) : text;
    }
}
