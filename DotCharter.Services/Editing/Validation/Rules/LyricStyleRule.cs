using System.Text;
using DotCharter.Services.Parsing.Xml.Vocals;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     D13: lyric style checks from the community charting guides, kept narrow so a finding is
///     never a guess. Lyrics belong to the PROJECT, not to an arrangement, so this runs once per
///     project (the Issues tab's pass and <c>ChartQualityReporter</c>), never as an
///     <see cref="IArrangementRule" />: that would repeat every finding once per arrangement and
///     skip a vocals-only chart. A LINE is the written syllables up to a line-end flag or the end
///     of the phrase, the same rule the vocal writers follow
///     (<see cref="RsVocalLyricEncoding.EncodeLine" />). Each finding names its syllable
///     (<see cref="ArrangementIssue.Lyric" />).
///     <list type="bullet">
///         <item>A line whose first letter has a lowercase form starts in lowercase, reported on
///         the syllable that carries that letter (a quote-only syllable before it is skipped).
///         Scripts without case (Japanese) never match, and a line whose first syllable
///         continues the previous syllable's word is not a line start.</item>
///         <item>A period or comma that is not part of an ellipsis, a number (3.5), an
///         abbreviation (S.O.S., L.A., Ph.D., also split across syllables or followed by 's or
///         a hyphen) or a title (Mr.). One finding per syllable that carries one.</item>
///         <item>A syllable that holds a space between words: several words, or the whole line,
///         charted as one syllable. Boundaries INSIDE a word are never guessed.</item>
///     </list>
/// </summary>
public static class LyricStyleRule
{
    private static readonly HashSet<string> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mr.", "Mrs.", "Ms.", "Dr.", "St.", "Jr.", "Sr.", "vs."
    };

    /// <summary>The D13 findings for <paramref name="project" />'s vocal tracks; none for a null project.</summary>
    public static IReadOnlyList<ArrangementIssue> Check(ChartProject? project, IList<BeatMarker> beats)
    {
        if (project?.VocalTracks is not { Count: > 0 } tracks || beats.Count == 0) return [];

        var issues = new List<ArrangementIssue>();
        var line = new List<(LyricEvent Lyric, LyricPlace Place)>();
        for (var t = 0; t < tracks.Count; t++)
        {
            LyricEvent? previous = null;
            for (var p = 0; p < tracks[t].Phrases.Count; p++)
            {
                var lyrics = tracks[t].Phrases[p].Lyrics;
                for (var s = 0; s < lyrics.Count; s++)
                {
                    if (RsVocalLyricEncoding.WrittenText(lyrics[s]).Length > 0) line.Add((lyrics[s], new LyricPlace(t, p, s)));
                    if (lyrics[s].IsPhraseEnd) previous = Flush(line, previous, beats, issues);
                }

                previous = Flush(line, previous, beats, issues);
            }
        }

        return issues;
    }

    private static LyricEvent? Flush(List<(LyricEvent Lyric, LyricPlace Place)> line, LyricEvent? previous,
        IList<BeatMarker> beats, List<ArrangementIssue> issues)
    {
        if (line.Count == 0) return previous;

        if (previous?.IsWordPart != true && FirstLetterSyllable(line) is { } lower)
            issues.Add(Finding("Validation_LyricLineStartsLowercase", line[lower], beats));

        foreach (var index in StrayMarkSyllables(line))
            issues.Add(Finding("Validation_LyricStrayPunctuation", line[index], beats));

        foreach (var syllable in line.Where(syllable => syllable.Lyric.Text.Trim().Contains(' ')))
            issues.Add(Finding("Validation_LyricUnsplitSyllable", syllable, beats));

        var last = line[^1].Lyric;
        line.Clear();
        return last;
    }

    private static ArrangementIssue Finding(string key, (LyricEvent Lyric, LyricPlace Place) syllable,
        IList<BeatMarker> beats)
    {
        var issue = ArrangementIssues.Make(ArrangementIssueType.LyricStyle, key,
            syllable.Lyric.Position.GetPositionMs(beats), args: [syllable.Lyric.Text.Trim()]);
        return issue with { Lyric = syllable.Place };
    }

    /// <summary>The index of the syllable whose first letter or digit starts the line, when that
    /// is a lowercase letter; null otherwise. Punctuation-only syllables are skipped.</summary>
    private static int? FirstLetterSyllable(List<(LyricEvent Lyric, LyricPlace Place)> line)
    {
        for (var i = 0; i < line.Count; i++)
        {
            foreach (var c in line[i].Lyric.Text)
            {
                if (char.IsLetter(c)) return char.IsLower(c) ? i : null;
                if (char.IsDigit(c)) return null;
            }
        }

        return null;
    }

    /// <summary>The line's words joined across word-part syllables, each character owned by the
    /// syllable it came from, so every stray mark is reported on the syllable that carries it.</summary>
    private static IEnumerable<int> StrayMarkSyllables(List<(LyricEvent Lyric, LyricPlace Place)> line)
    {
        var text = new StringBuilder();
        var owners = new List<int>();
        for (var i = 0; i < line.Count; i++)
        {
            var written = line[i].Lyric.Text.Trim();
            var wordPart = line[i].Lyric.IsWordPart;
            text.Append(written);
            if (!wordPart) text.Append(' ');
            owners.AddRange(Enumerable.Repeat(i, written.Length + (wordPart ? 0 : 1)));
        }

        var all = text.ToString();
        var reported = new SortedSet<int>();
        var start = 0;
        while (start < all.Length)
        {
            var end = all.IndexOf(' ', start);
            if (end < 0) end = all.Length;
            foreach (var mark in StrayMarks(all[start..end])) reported.Add(owners[start + mark]);
            start = end + 1;
        }

        return reported;
    }

    /// <summary>Every stray period or comma in <paramref name="word" />. The word is read in parts
    /// split at apostrophes and hyphens, so L.A.'s and L.A.-bound keep their abbreviation.</summary>
    private static IEnumerable<int> StrayMarks(string word)
    {
        var partStart = 0;
        for (var i = 0; i <= word.Length; i++)
        {
            if (i < word.Length && !IsJoiner(word[i])) continue;
            foreach (var mark in StrayMarksInPart(word, partStart, i)) yield return mark;
            partStart = i + 1;
        }
    }

    private static IEnumerable<int> StrayMarksInPart(string word, int start, int end)
    {
        while (start < end && IsWrapper(word[start])) start++;
        while (end > start && IsWrapper(word[end - 1])) end--;

        var stemEnd = end;
        while (stemEnd > start && word[stemEnd - 1] == ',') stemEnd--;
        for (var i = stemEnd; i < end; i++) yield return i;

        var stem = word[start..stemEnd];
        if (IsAbbreviation(stem) || Titles.Contains(stem)) yield break;

        for (var i = start; i < stemEnd; i++)
        {
            if (IsStray(word, i)) yield return i;
        }
    }

    private static bool IsJoiner(char c) => c is '\'' or '’' or '-';

    private static bool IsWrapper(char c) => !char.IsLetterOrDigit(c) && c is not ('.' or ',');

    private static bool IsStray(string word, int i)
    {
        var c = word[i];
        if (c is not ('.' or ',')) return false;
        var before = i > 0 ? word[i - 1] : ' ';
        var after = i + 1 < word.Length ? word[i + 1] : ' ';
        if (c == '.' && (before == '.' || after == '.')) return false;
        return !(char.IsDigit(before) && char.IsDigit(after));
    }

    /// <summary>Two or more groups of one or two letters, each but the last followed by a period
    /// (the last one's is optional), at least one group a single letter: S.O.S., L.A., U.S.A, Ph.D.</summary>
    private static bool IsAbbreviation(string stem)
    {
        var groups = stem.Split('.');
        if (groups[^1].Length == 0) groups = groups[..^1];
        if (groups.Length < 2) return false;
        var single = false;
        foreach (var group in groups)
        {
            if (group.Length is < 1 or > 2 || !group.All(char.IsLetter)) return false;
            single |= group.Length == 1;
        }

        return single;
    }
}
