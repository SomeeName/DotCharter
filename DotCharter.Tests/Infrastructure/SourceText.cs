using System.Text.RegularExpressions;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Comment stripping for the source lints, so a rule is checked against code and
///     not against a comment that mentions it. Two C# flavours, because the lints
///     disagree on purpose: <see cref="StripComments" /> also cuts a trailing
///     <c>// note</c> (and so anything after <c>//</c> inside a string, such as a URL),
///     while <see cref="StripCommentLines" /> only drops lines that ARE comments and
///     leaves the rest of every code line alone. Neither knows about strings; a lint
///     that must not see inside literals masks them itself.
/// </summary>
internal static partial class SourceText
{
    /// <summary>Removes <c>/* … */</c> blocks and everything from <c>//</c> to the end of each line.</summary>
    public static string StripComments(string code) => LineComment().Replace(BlockComment().Replace(code, string.Empty), string.Empty);

    /// <summary>Removes <c>/* … */</c> blocks and every line whose first non-blank text is <c>//</c>.</summary>
    public static string StripCommentLines(string code) => WholeLineComment().Replace(BlockComment().Replace(code, string.Empty), string.Empty);

    /// <summary>
    ///     <see cref="StripComments" />, but a <c>/* … */</c> block leaves its line breaks
    ///     behind, so an index into the result still maps to the original line
    ///     (<see cref="LineAt" />) and a failure can name it.
    /// </summary>
    public static string StripCommentsKeepingLines(string code) =>
        LineComment().Replace(BlockComment().Replace(code, m => new string('\n', m.Value.Count(c => c == '\n'))), string.Empty);

    /// <summary>The 1-based line of <paramref name="index" /> in <paramref name="text" />.</summary>
    public static int LineAt(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>Removes XML <c>&lt;!-- … --&gt;</c> comments from markup.</summary>
    public static string StripMarkupComments(string markup) => MarkupComment().Replace(markup, string.Empty);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"^[ \t]*//[^\r\n]*", RegexOptions.Multiline)]
    private static partial Regex WholeLineComment();

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex MarkupComment();
}
