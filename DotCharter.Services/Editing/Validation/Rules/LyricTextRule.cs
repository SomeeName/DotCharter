using System.Globalization;
using DotCharter.Analysis.Lyrics.Syllables;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D13, part two: the line and character checks from the community charting guides, on the
///     same lines as <see cref="LyricStyleRule" /> and listed with it (<see cref="LyricStyleRule.CheckAll" />).
///     Each finding names its syllable (<see cref="ArrangementIssue.Lyric" />); none changes the text,
///     the fixes are <see cref="LyricStyleFix" />'s and run only from a Fix button.
///     <list type="bullet">
///         <item>Plain characters: smart quotes, typographic dashes, the ellipsis character, emoji and
///         other symbols, and the ASCII marks a lyric never sings (# * @ ~ | _ &lt; &gt; [ ] { } \ ^ `).
///         Letters of every script are plain, so Japanese (kana, kanji, ー) and its punctuation
///         (、。「」) are never flagged.</item>
///         <item>Line length: a line of a cased script (where words are split by spaces) holds 3 to 7
///         words; any line holds at most 40 characters, counted as written with one space between
///         words. A short line is reported on its first syllable, a long one too, with a split fix.</item>
///         <item>One syllable per note: a whole word charted on one note that the English syllabifier
///         (<see cref="LyricSyllabifier" />) breaks into two or more syllables. Only words of ASCII
///         letters (and apostrophes) are checked; any other word is never guessed.</item>
///         <item>Backing vocals in the lead track: in the first vocal track, a syllable that opens a
///         parenthesised run, the usual way a backing vocal is written.</item>
///     </list>
/// </summary>
public static class LyricTextRule
{
    /// <summary>Fewest words a line of a cased script should hold.</summary>
    public const int MinWords = 3;

    /// <summary>Most words a line should hold.</summary>
    public const int MaxWords = 7;

    /// <summary>Most characters a line should hold.</summary>
    public const int MaxCharacters = 40;

    private const string NeverSung = "#*@~|_<>[]{}\\^`";

    /// <summary>The line findings for <paramref name="project" />'s vocal tracks; none for a null project.</summary>
    public static IReadOnlyList<ArrangementIssue> Check(ChartProject? project, IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return [];

        var issues = new List<ArrangementIssue>();
        foreach (var (line, previous) in LyricStyleRule.Lines(project))
        {
            foreach (var syllable in line.Where(s => HasNonPlain(s.Lyric.Text)))
                issues.Add(LyricStyleRule.Finding("Validation_LyricPlainCharacters", syllable, beats));

            CheckLength(line, previous, beats, issues);
            CheckSyllables(line, previous, beats, issues);

            foreach (var syllable in line.Where(s => s.Place.Track == 0 && s.Lyric.Text.TrimStart().StartsWith('(')))
                issues.Add(LyricStyleRule.Finding("Validation_LyricBackingVocalInLead", syllable, beats));
        }

        return issues;
    }

    /// <summary>Whether <paramref name="c" /> is not a plain character (see the class remarks).</summary>
    public static bool IsNonPlain(char c) =>
        NeverSung.Contains(c) || PlainReplacement(c) != null || CharUnicodeInfo.GetUnicodeCategory(c) is
            UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
            or UnicodeCategory.Control or UnicodeCategory.Format
            || (c > 127 && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.ModifierSymbol);

    /// <summary>The plain text a typographic character stands for, or null when it has none.</summary>
    public static string? PlainReplacement(char c) => c switch
    {
        '‘' or '’' or '‚' or '‛' or '′' => "'",
        '“' or '”' or '„' or '‟' or '″' => "\"",
        '–' or '—' or '‐' or '‑' => "-",
        '…' => "...",
        _ => null
    };

    private static bool HasNonPlain(string text) => text.Any(IsNonPlain);

    /// <summary>The line as written: word parts joined, words split by one space.</summary>
    internal static string LineText(IReadOnlyList<(LyricEvent Lyric, LyricPlace Place)> line) =>
        string.Concat(line.Select((s, i) => s.Lyric.Text.Trim() + (s.Lyric.IsWordPart || i == line.Count - 1 ? "" : " ")));

    /// <summary>The words of the line as written: word parts joined, so several words charted in one
    /// syllable still count one each.</summary>
    internal static int WordCount(IReadOnlyList<(LyricEvent Lyric, LyricPlace Place)> line) =>
        LineText(line).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Whether the line is too long: more than <see cref="MaxWords" /> words in a cased script, or
    /// more than <see cref="MaxCharacters" /> characters.</summary>
    internal static bool IsTooLong(IReadOnlyList<(LyricEvent Lyric, LyricPlace Place)> line) =>
        (IsCased(line) && WordCount(line) > MaxWords) || LineText(line).Length > MaxCharacters;

    private static bool IsCased(IReadOnlyList<(LyricEvent Lyric, LyricPlace Place)> line) =>
        line.Any(s => s.Lyric.Text.Any(c => char.IsUpper(c) || char.IsLower(c)));

    private static void CheckLength(List<(LyricEvent Lyric, LyricPlace Place)> line, LyricEvent? previous,
        IList<BeatMarker> beats, List<ArrangementIssue> issues)
    {
        // A line that continues the previous line's word is cut mid-word; its count would be a guess.
        if (previous?.IsWordPart == true) return;

        var text = LineText(line);
        var words = WordCount(line);
        if (IsTooLong(line))
            issues.Add(LyricStyleRule.Finding("Validation_LyricLineTooLong", line[0], beats, [text, words, text.Length]));
        else if (IsCased(line) && words < MinWords)
            issues.Add(LyricStyleRule.Finding("Validation_LyricLineTooShort", line[0], beats, [text, words]));
    }

    private static void CheckSyllables(List<(LyricEvent Lyric, LyricPlace Place)> line, LyricEvent? previous,
        IList<BeatMarker> beats, List<ArrangementIssue> issues)
    {
        var joined = previous?.IsWordPart == true;
        foreach (var syllable in line)
        {
            var wholeWord = !joined && !syllable.Lyric.IsWordPart;
            joined = syllable.Lyric.IsWordPart;
            if (!wholeWord) continue;

            var word = syllable.Lyric.Text.Trim().Trim(',', '.', '!', '?', '"', '(', ')', ';', ':');
            if (word.Length == 0 || !word.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '\'')) continue;

            var parts = LyricSyllabifier.Split(word, SyllableLanguage.English);
            if (parts.Count >= 2)
            {
                issues.Add(LyricStyleRule.Finding("Validation_LyricSeveralSyllablesOnOneNote", syllable, beats,
                    [word, parts.Count, string.Join('-', parts)]));
            }
        }
    }
}
