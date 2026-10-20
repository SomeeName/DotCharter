using System.Text.RegularExpressions;

namespace DotCharter.UI.Controls.Markdown;

internal enum MdBlockKind
{
    Heading,
    Paragraph,
    Bullet,
    Code,
    Rule
}

/// <summary>One styled text fragment inside a block. <see cref="IsLineBreak"/>
/// fragments carry no text and render as a hard line break (GitHub keeps
/// single newlines inside release-note paragraphs).</summary>
internal sealed record MdInline(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strike = false,
    string? LinkUrl = null,
    bool IsLineBreak = false)
{
    public static readonly MdInline Break = new(string.Empty, IsLineBreak: true);
}

/// <summary>One block-level element. <see cref="Marker"/> is the ordered-list
/// marker ("3.") when the bullet came from a numbered list; <see cref="Literal"/>
/// is the verbatim body of a fenced code block.</summary>
internal sealed record MdBlock(
    MdBlockKind Kind,
    IReadOnlyList<MdInline> Inlines,
    int HeadingLevel = 0,
    int IndentLevel = 0,
    string? Marker = null,
    string? Literal = null);

/// <summary>
/// Minimal GitHub-flavored markdown parser for release notes: headings, bullet
/// and numbered lists (one nesting level per 2 spaces), bold/italic/strike,
/// inline code, fenced code blocks, links, bare-URL autolinks, blockquote
/// prefixes and horizontal rules. Anything it does not understand degrades to
/// the literal text — it must never throw on arbitrary input.
/// </summary>
internal static partial class MarkdownParser
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRx();

    [GeneratedRegex(@"^\s{0,3}(?:-{3,}|\*{3,}|_{3,})\s*$")]
    private static partial Regex RuleRx();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(\S.*)$")]
    private static partial Regex BulletRx();

    [GeneratedRegex(@"^(\s*)(\d{1,3})[.)]\s+(\S.*)$")]
    private static partial Regex OrderedRx();

    private const string Escapable = "\\`*_{}[]()#+-.!~>";

    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        List<string>? fence = null;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var inlines = new List<MdInline>();
            for (var i = 0; i < paragraph.Count; i++)
            {
                if (i > 0) inlines.Add(MdInline.Break);
                ParseInto(inlines, paragraph[i], bold: false, italic: false, strike: false, link: null);
            }
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, inlines));
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            if (fence != null)
            {
                if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(new MdBlock(MdBlockKind.Code, [], Literal: string.Join('\n', fence)));
                    fence = null;
                }
                else
                {
                    fence.Add(raw.TrimEnd());
                }
                continue;
            }

            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                fence = [];
                continue;
            }

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // Blockquotes render as regular text (release notes use them rarely
            // and only for emphasis-by-indent — the "> " chrome is just noise).
            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                line = line[2..];
            }
            else if (line == ">") { FlushParagraph(); continue; }

            var m = HeadingRx().Match(line);
            if (m.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Heading,
                    ParseInlines(m.Groups[2].Value.TrimEnd('#', ' ')),
                    HeadingLevel: m.Groups[1].Length));
                continue;
            }

            if (RuleRx().IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Rule, []));
                continue;
            }

            m = BulletRx().Match(line);
            if (m.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Bullet,
                    ParseInlines(m.Groups[2].Value),
                    IndentLevel: IndentLevel(m.Groups[1].Value)));
                continue;
            }

            m = OrderedRx().Match(line);
            if (m.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Bullet,
                    ParseInlines(m.Groups[3].Value),
                    IndentLevel: IndentLevel(m.Groups[1].Value),
                    Marker: m.Groups[2].Value + "."));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        // An unterminated fence still shows its content instead of eating it.
        if (fence is { Count: > 0 })
            blocks.Add(new MdBlock(MdBlockKind.Code, [], Literal: string.Join('\n', fence)));
        FlushParagraph();
        return blocks;
    }

    internal static List<MdInline> ParseInlines(string text)
    {
        var result = new List<MdInline>();
        ParseInto(result, text, bold: false, italic: false, strike: false, link: null);
        return result;
    }

    private static int IndentLevel(string indent)
    {
        var width = 0;
        foreach (var c in indent) width += c == '\t' ? 2 : 1;
        return Math.Clamp(width / 2, 0, 2);
    }

    private static void ParseInto(List<MdInline> output, string text, bool bold, bool italic, bool strike, string? link)
    {
        var i = 0;
        var start = 0;

        void EmitPlain(int end)
        {
            if (end > start) output.Add(new MdInline(text[start..end], bold, italic, false, strike, link));
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length && Escapable.Contains(text[i + 1]))
            {
                EmitPlain(i);
                output.Add(new MdInline(text[i + 1].ToString(), bold, italic, false, strike, link));
                i += 2;
                start = i;
                continue;
            }

            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    EmitPlain(i);
                    output.Add(new MdInline(text[(i + 1)..close], bold, italic, Code: true, strike, link));
                    i = close + 1;
                    start = i;
                    continue;
                }
            }

            if (c is '*' or '_' && TryParseEmphasis(text, i, c, out var inner, out var next, out var delimiterLen))
            {
                EmitPlain(i);
                ParseInto(output, inner,
                    bold || delimiterLen >= 2,
                    italic || delimiterLen is 1 or 3,
                    strike, link);
                i = next;
                start = i;
                continue;
            }

            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    EmitPlain(i);
                    ParseInto(output, text[(i + 2)..close], bold, italic, strike: true, link);
                    i = close + 2;
                    start = i;
                    continue;
                }
            }

            // Images degrade to their alt text as a link (no inline bitmaps).
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' &&
                TryParseLink(text, i + 1, out var altLabel, out var imgUrl, out var afterImg))
            {
                EmitPlain(i);
                ParseInto(output, altLabel, bold, italic, strike, imgUrl);
                i = afterImg;
                start = i;
                continue;
            }

            if (c == '[' && TryParseLink(text, i, out var label, out var url, out var afterLink))
            {
                EmitPlain(i);
                ParseInto(output, label, bold, italic, strike, url);
                i = afterLink;
                start = i;
                continue;
            }

            if (c == 'h' && link == null &&
                (i == 0 || !char.IsLetterOrDigit(text[i - 1])) &&
                (text.AsSpan(i).StartsWith("http://") || text.AsSpan(i).StartsWith("https://")))
            {
                var end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
                while (end > i && ".,;:!?)]}'\"".Contains(text[end - 1])) end--;
                if (end > i + 8)
                {
                    EmitPlain(i);
                    var bare = text[i..end];
                    output.Add(new MdInline(bare, bold, italic, false, strike, bare));
                    i = end;
                    start = i;
                    continue;
                }
            }

            i++;
        }

        EmitPlain(text.Length);
    }

    /// <summary>Matches <c>*i*</c>, <c>**b**</c> and <c>***bi***</c> (same for
    /// <c>_</c>). Opens only against a non-space; closes only after a non-space
    /// — so "2 * 3 * 4" stays literal.</summary>
    private static bool TryParseEmphasis(string text, int i, char c, out string inner, out int next, out int delimiterLen)
    {
        inner = string.Empty;
        next = i;

        var openRun = 1;
        while (i + openRun < text.Length && text[i + openRun] == c) openRun++;
        delimiterLen = Math.Min(openRun, 3);

        var contentStart = i + delimiterLen;
        if (contentStart >= text.Length || char.IsWhiteSpace(text[contentStart])) return false;

        var j = contentStart;
        while (j < text.Length)
        {
            j = text.IndexOf(c, j);
            if (j <= contentStart)
            {
                if (j < 0) return false;
                j++;
                continue;
            }

            var closeRun = 1;
            while (j + closeRun < text.Length && text[j + closeRun] == c) closeRun++;
            if (closeRun >= delimiterLen && !char.IsWhiteSpace(text[j - 1]))
            {
                inner = text[contentStart..j];
                next = j + delimiterLen;
                return true;
            }

            j += closeRun;
        }

        return false;
    }

    private static bool TryParseLink(string text, int i, out string label, out string url, out int next)
    {
        label = url = string.Empty;
        next = i;

        var closeBracket = text.IndexOf(']', i + 1);
        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(') return false;
        var closeParen = text.IndexOf(')', closeBracket + 2);
        if (closeParen < 0) return false;

        label = text[(i + 1)..closeBracket];
        url = text[(closeBracket + 2)..closeParen].Trim();
        var titleSep = url.IndexOf(' ');
        if (titleSep > 0) url = url[..titleSep];
        if (label.Length == 0 || url.Length == 0) return false;

        next = closeParen + 1;
        return true;
    }
}
