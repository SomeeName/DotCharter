namespace DotCharter.Core.Domain.Vocals;

/// <summary>
///     The one rule for a syllable's two structure flags: a syllable that ENDS its line never
///     continues a word. A Rocksmith file has one marker per syllable, so a syllable carrying
///     both would be one line in the editor and lose its line break in the file. Editing paths
///     that want a line-final word part join the next line instead
///     (<c>LyricWordPartEdit</c>); where there is no next line to join, the line end wins and
///     the word part is dropped. Every editing writer of the two flags goes through here;
///     importers keep what the file says.
/// </summary>
public static class LyricLineRule
{
    /// <summary>The word-part flag a syllable may carry given whether it ends its line.</summary>
    public static bool WordPart(bool isWordPart, bool isPhraseEnd) => isWordPart && !isPhraseEnd;

    /// <summary>Sets both flags under the rule.</summary>
    public static void Apply(LyricEvent lyric, bool isWordPart, bool isPhraseEnd)
    {
        lyric.IsPhraseEnd = isPhraseEnd;
        lyric.IsWordPart = WordPart(isWordPart, isPhraseEnd);
    }
}
