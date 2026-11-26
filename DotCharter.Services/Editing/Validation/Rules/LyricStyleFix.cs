using System.Text;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>A lyric finding's fix: the action, the text it writes (or, for a split, the syllable the
/// first line ends on) and whether it splits the line.</summary>
public sealed record LyricFixProposal(IChartAction Action, string Text, bool IsSplit);

/// <summary>
///     The safe, undoable fixes for the D13 lyric findings. Each is recomputed from the LIVE
///     project at the finding's <see cref="LyricPlace" />, runs only from a Fix button and is one undo
///     step; nothing here runs on its own. Offered for: a lowercase line start (capitalise its first
///     letter), stray punctuation (remove the stray periods and commas the rule reports), non-plain
///     characters (typographic quotes, dashes and the ellipsis become their plain form, other symbols
///     are removed) and a line that is too long (split at the word boundary that best halves it).
///     A fix that would leave the syllable empty, or a split with no word boundary, is not offered.
/// </summary>
public static class LyricStyleFix
{
    /// <summary>The fix for <paramref name="issue" /> in <paramref name="project" />, or null when it has none.</summary>
    public static LyricFixProposal? For(ArrangementIssue? issue, ChartProject? project)
    {
        if (issue is not { Type: ArrangementIssueType.LyricStyle, Lyric: { } place } || project == null) return null;
        if (LyricStyleRule.LineAt(project, place) is not { } line) return null;
        var index = line.FindIndex(s => s.Place == place);
        var lyric = line[index].Lyric;
        return issue.MessageKey switch
        {
            "Validation_LyricLineStartsLowercase" => TextFix(lyric, Capitalised(line, index)),
            "Validation_LyricStrayPunctuation" => TextFix(lyric, WithoutStrayMarks(line, index)),
            "Validation_LyricPlainCharacters" => TextFix(lyric, Plain(lyric.Text)),
            "Validation_LyricLineTooLong" => Split(project, line),
            _ => null
        };
    }

    /// <summary><paramref name="text" /> in plain characters (see <see cref="LyricTextRule" />).</summary>
    public static string Plain(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (LyricTextRule.PlainReplacement(c) is { } replacement) plain.Append(replacement);
            else if (!LyricTextRule.IsNonPlain(c)) plain.Append(c);
        }

        return plain.ToString();
    }

    private static LyricFixProposal? TextFix(LyricEvent lyric, string? text) =>
        text == null || text.Trim().Length == 0 || text == lyric.Text ? null : new(new SetLyricTextAction(lyric, text), text.Trim(), false);

    private static string? Capitalised(List<(LyricEvent Lyric, LyricPlace Place)> line, int index)
    {
        if (LyricStyleRule.FirstLetterSyllable(line) != index) return null;
        var text = line[index].Lyric.Text;
        var at = text.ToList().FindIndex(char.IsLetter);
        return at < 0 ? null : text[..at] + char.ToUpperInvariant(text[at]) + text[(at + 1)..];
    }

    private static string? WithoutStrayMarks(List<(LyricEvent Lyric, LyricPlace Place)> line, int index)
    {
        var text = line[index].Lyric.Text;
        var lead = text.Length - text.TrimStart().Length;
        var offsets = LyricStyleRule.StrayMarks(line).Where(m => m.Syllable == index).Select(m => lead + m.Offset).ToHashSet();
        return offsets.Count == 0 ? null : string.Concat(text.Where((_, i) => !offsets.Contains(i)));
    }

    /// <summary>Splits the line after the word that leaves the two halves closest in length.</summary>
    private static LyricFixProposal? Split(ChartProject project, List<(LyricEvent Lyric, LyricPlace Place)> line)
    {
        if (!LyricTextRule.IsTooLong(line)) return null;
        var total = LyricTextRule.LineText(line).Length;
        int? best = null;
        var bestScore = int.MaxValue;
        for (var i = 0; i < line.Count - 1; i++)
        {
            if (line[i].Lyric.IsWordPart) continue;
            var left = LyricTextRule.LineText(line[..(i + 1)]).Length;
            var score = Math.Abs(total - 1 - (2 * left));
            if (score >= bestScore) continue;
            bestScore = score;
            best = i;
        }

        if (best is not { } at) return null;
        var place = line[at].Place;
        var phrases = project.VocalTracks[place.Track].Phrases;
        var action = new SetLyricPhraseEndAction(phrases, phrases[place.Phrase], line[at].Lyric, true);
        return action.HasEffect ? new(action, line[at].Lyric.Text.Trim(), true) : null;
    }
}
