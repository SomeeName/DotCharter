namespace DotCharter.Analysis.Lyrics;

/// <summary>One syllable as the charter typed it, before any tokenization.</summary>
/// <param name="Text">The syllable exactly as written, suffixes stripped.</param>
/// <param name="ContinuesWord">
///     No space follows — the next syllable finishes the same word. Maps to
///     Rocksmith's trailing <c>-</c> and to <c>LyricEvent.IsWordPart</c>.
/// </param>
/// <param name="EndsLine">
///     Last syllable of its line. Maps to Rocksmith's trailing <c>+</c> and to
///     <c>LyricEvent.IsPhraseEnd</c>.
/// </param>
/// <param name="LineIndex">Zero-based line this syllable came from.</param>
public readonly record struct LyricSyllable(string Text, bool ContinuesWord, bool EndsLine, int LineIndex);

/// <summary>A parsed lyric sheet: syllables in order, grouped into lines.</summary>
/// <param name="Syllables">Every syllable, in singing order.</param>
/// <param name="LineCount">Number of non-empty lines.</param>
public sealed record LyricSheet(IReadOnlyList<LyricSyllable> Syllables, int LineCount)
{
    public static LyricSheet Empty { get; } = new([], 0);
}

/// <summary>
///     Parses the paste format the charter types into syllables and lines.
///     <para>
///         <b>The format is the one Rocksmith already uses, minus the noise:</b>
///         a newline ends a phrase, a space ends a word, and a <c>-</c> joins
///         syllables inside a word. So
///         <c>"Mi-zu ta-ma-ri ni a-shi wo tsuk-kon-de"</c> parses to the twelve
///         syllables that the hand-synced <c>arr_vocals_RS2.xml</c> in
///         <c>TestData/EOF</c> stores as
///         <c>Mi- zu ta- ma- ri ni a- shi wo tsuk- kon- de+</c> — same splits,
///         same word boundaries, same phrase end. That is deliberate: it means a
///         round trip through an export is lossless, and the owner can paste
///         either their own compact text or a line copied straight back out of a
///         chart.
///     </para>
///     <para>
///         Rocksmith's own suffixes are therefore also accepted on input: a
///         token ending in <c>-</c> continues the word, a token ending in
///         <c>+</c> ends the phrase. Both are stripped from
///         <see cref="LyricSyllable.Text" /> so they never reach the tokenizer
///         (an alignment that tried to match a literal '+' against the acoustic
///         model would drop the character and, worse, silently).
///     </para>
///     <para>
///         Blank lines are stanza breaks: they carry no syllables and do not
///         create an empty phrase, but the line before one still ends normally.
///     </para>
/// </summary>
public static class LyricSheetParser
{
    /// <summary>Characters treated as a syllable separator inside a word.</summary>
    private const string SyllableSeparators = "-‐‑‒–—";

    public static LyricSheet Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return LyricSheet.Empty;

        var syllables = new List<LyricSyllable>();
        var lineIndex = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;

            var firstOfLine = syllables.Count;

            foreach (var word in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var token = word;

                // Rocksmith's phrase-end marker. Stripped here rather than
                // honoured as a line break: the newline is the authority on
                // where a phrase ends, so a stray '+' mid-line cannot split one.
                token = token.TrimEnd('+');

                // A trailing separator means the LAST syllable of this token
                // also continues the word — that is how Rocksmith exports
                // "Mi-" as its own space-separated entry.
                var trailingJoin = token.Length > 0 && SyllableSeparators.Contains(token[^1]);
                token = token.TrimEnd(SyllableSeparators.ToCharArray());
                if (token.Length == 0) continue;

                var parts = token.Split(SyllableSeparators.ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                for (var i = 0; i < parts.Length; i++)
                {
                    var continues = i < parts.Length - 1 || trailingJoin;
                    syllables.Add(new LyricSyllable(parts[i], continues, EndsLine: false, lineIndex));
                }
            }

            // An all-punctuation line contributes nothing; don't burn a line
            // index on it, or the caller's line count disagrees with reality.
            if (syllables.Count == firstOfLine) continue;

            // The last syllable of a line ends the phrase and, whatever the
            // hyphens said, cannot continue a word across the break.
            syllables[^1] = syllables[^1] with { ContinuesWord = false, EndsLine = true };
            lineIndex++;
        }

        return syllables.Count == 0 ? LyricSheet.Empty : new LyricSheet(syllables, lineIndex);
    }
}
