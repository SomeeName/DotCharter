using System.Globalization;

namespace DotCharter.Services.Parsing.MoverPhrases;

/// <summary>
///     The "moveR&lt;N&gt;" phrase name: a phrase written on a beat whose iteration (and the
///     section on the same beat) really starts at the N-th note counted from that beat.
///     There is no moveL. The two tools that read the name disagree on its spelling, and
///     each reader here follows the tool that wrote the file:
///     <list type="bullet">
///         <item>
///             DLC Builder (<c>PhraseMover.fs</c>, Rocksmith XML): the name STARTS WITH
///             "mover", case-insensitive, and the rest parses as an
///             <c>NumberStyles.Integer</c> (so surrounding blanks and a sign pass).
///         </item>
///         <item>
///             EOF (<c>event.c</c> <c>eof_is_mover_phrase</c>): the FIRST "mover" anywhere
///             in the text, case-insensitive, followed by nothing but digits, value above 0.
///             What precedes it is the phrase's own name ("riff moveR2").
///         </item>
///     </list>
/// </summary>
public static class MoverPhraseName
{
    private const string Token = "mover";

    /// <summary>The phrase name every writer emits — the one form both tools accept.</summary>
    public static string Format(int moveBy) => "moveR" + moveBy.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    ///     True when DLC Builder treats <paramref name="name" /> as a moveR phrase (its
    ///     filter), whether or not the number parses — DLC Builder then fails the build.
    /// </summary>
    public static bool IsDlcBuilderMover(string? name) =>
        name is not null && name.StartsWith(Token, StringComparison.OrdinalIgnoreCase);

    /// <summary>DLC Builder's <c>getMoveBy</c>. False when the number does not parse.</summary>
    public static bool TryParseDlcBuilder(string? name, out int moveBy)
    {
        moveBy = 0;
        return IsDlcBuilderMover(name)
               && int.TryParse(name.AsSpan(Token.Length), NumberStyles.Integer, NumberFormatInfo.InvariantInfo,
                   out moveBy);
    }

    /// <summary>
    ///     EOF's <c>eof_is_mover_phrase</c>. <paramref name="ownName" /> is the text before
    ///     the token, trimmed — empty for a plain "moveR2".
    /// </summary>
    public static bool TryParseEof(string? text, out int moveBy, out string ownName)
    {
        moveBy = 0;
        ownName = "";
        if (string.IsNullOrEmpty(text)) return false;

        var at = text.IndexOf(Token, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;

        var digits = text.AsSpan(at + Token.Length);
        if (digits.IsEmpty) return false;

        long value = 0;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9') return false;
            value = Math.Min(value * 10 + (c - '0'), int.MaxValue);
        }

        if (value == 0) return false;

        moveBy = (int)value;
        ownName = text[..at].Trim();
        return true;
    }

    /// <summary>
    ///     The name a resolved moveR phrase takes when it carries no name of its own: the
    ///     section it starts (phrases usually share their section's name), else "phrase",
    ///     numbered past any name already in <paramref name="taken" /> — two phrases that
    ///     share a name share their difficulty and solo settings.
    /// </summary>
    public static string FallbackName(string? section, ICollection<string> taken)
    {
        var stem = string.IsNullOrWhiteSpace(section) ? "phrase" : section.Trim();
        if (!IsTaken(stem)) return stem;

        for (var n = 2; ; n++)
        {
            var candidate = stem + n.ToString(CultureInfo.InvariantCulture);
            if (!IsTaken(candidate)) return candidate;
        }

        bool IsTaken(string name) => taken.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
    }
}
